using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;

namespace BBBirder.UnityInjection.Editor
{
    internal static class CecilHelper
    {
        class UnityEditorAssemblyResolver : DefaultAssemblyResolver
        {
            Dictionary<string, AssemblyDefinition> cache = new();
            public string[] allowedAssemblies;

            public AssemblyDefinition FindAssembly(string name)
            {
                if (!cache.TryGetValue(name, out var assemblyDefinition))
                {
                    var path = allowedAssemblies.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a) == name);
                    cache[name] = assemblyDefinition = ModuleDefinition.ReadModule(path, new ReaderParameters()
                    {
                        InMemory = true,
                        ReadingMode = ReadingMode.Immediate,
                    }).Assembly;
                }

                return assemblyDefinition;
            }

            public string GetAssemblyLocation(string name)
            {
                return allowedAssemblies.FirstOrDefault(a => Path.GetFileNameWithoutExtension(a) == name);
            }

            public override AssemblyDefinition Resolve(AssemblyNameReference name)
            {
                if (cache.TryGetValue(name.FullName, out var value))
                {
                    return value;
                }

                value = base.Resolve(name);
                // Logger.Warning("Resolve assembly: " + name.FullName + " from " + value + "\n" + string.Join("\n", allowedAssemblies));
                cache[name.FullName] = value;
                return value;
            }

            protected override void Dispose(bool disposing)
            {
                foreach (var (path, assembly) in cache)
                {
                    assembly.Dispose();
                }

                cache.Clear();
                base.Dispose(disposing);
            }
        }

        public static Exception[] InjectAssembly(string assemblyPath, InjectionInfo[] injectionInfos, string[] allowedAssemblies, string outputPath)
        {
            Logger.Info("weave assembly: " + assemblyPath);
            using var assemblyResolver = new UnityEditorAssemblyResolver()
            {
                allowedAssemblies = allowedAssemblies,
            };


            assemblyResolver.AddSearchDirectory(Path.GetDirectoryName(assemblyPath));

            foreach (var folder in allowedAssemblies.Select(Path.GetDirectoryName).Distinct())
            {
                assemblyResolver.AddSearchDirectory(folder);
            }

            var shouldAccessSymbols = File.Exists(Path.ChangeExtension(assemblyPath, "pdb"));

            using var targetAssembly = AssemblyDefinition.ReadAssembly(assemblyPath, new ReaderParameters()
            {
                AssemblyResolver = assemblyResolver,
                ReadingMode = ReadingMode.Immediate,
                ReadSymbols = shouldAccessSymbols,
                InMemory = true,
            });

            var isDirty = false;
            TypeDefinition markAttributeTypeDef = null;

            var exceptions = new List<Exception>();
            foreach (var injectionInfo in injectionInfos.Distinct())
            {
                try
                {
                    var targetType = targetAssembly.MainModule.FindTypeBySignature(injectionInfo.InjectedMethod.DeclaringType.GetSignature());

                    // var targetMethod = targetAssembly.MainModule..ImportReference(injectionInfo.InjectedMethod).Resolve();
                    var targetMethod = targetType.FindMethodBySignature(injectionInfo.InjectedMethod.GetSignature());
                    if (targetMethod is null)
                    {
                        Logger.Warning($"Cannot find Method `{targetMethod}`");
                        continue;
                    }

                    if ((injectionInfo.customWeaveAction ?? DefaultWeaveAction)(targetMethod))
                    {
                        isDirty = true;
                    }

                    markAttributeTypeDef ??= targetAssembly.GetOrCreateMarkAttribute();
                    targetMethod.AddMarkAttributeIfNotExists(markAttributeTypeDef);
                }
                catch (Exception e)
                {
                    exceptions.Add(e);
                }
            }

            // targetAssembly.RegenerateUnitySignature(additionalTypes);
            if (isDirty)
            {
                Logger.Info($"writed (pdb:{shouldAccessSymbols})");
                targetAssembly.Write(outputPath, new WriterParameters()
                {
                    WriteSymbols = shouldAccessSymbols,
                });
            }
            else
            {
                Logger.Info($"assembly {outputPath} is not dirty, hence let it is.");
            }

            return exceptions.ToArray();
        }

        public static bool DefaultWeaveAction(MethodDefinition targetMethod)
        {
            var targetType = targetMethod.DeclaringType;
            var targetModule = targetType.Module;
            var markAttribute = targetModule.Assembly.GetOrCreateMarkAttribute();
            //check if already injected
            if (IsExistsMethodMarkAttribute(targetMethod, markAttribute)) return false;

            //clone origin
            var originName = WeavingUtils.GetOriginMethodName(targetMethod.Name, targetMethod.MetadataToken.ToInt32());
            var duplicatedMethod = targetMethod.Clone(targetType);
            duplicatedMethod.IsPrivate = true;
            duplicatedMethod.Name = originName;

            //add delegate
            var delegateType = CreateOrGetCorlibDelegateForMethod(targetModule, targetMethod, out var invokeMethod);
            // Fixes: Il2Cpp will collect it twice in DataModelBuilder::BuildCecilSourcedData!
            // targetType.NestedTypes.Add(delegateType);
            // targetModule.Types.Add(delegateType);

            //add static field
            var delegateName = WeavingUtils.GetInjectedFieldName(targetMethod.Name, targetMethod.MetadataToken.ToInt32());
            var delegateField = new FieldDefinition(delegateName, Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Private, delegateType);
            targetType.Fields.Add(delegateField);

            //write method body
            targetMethod.MakeJumpMethod(duplicatedMethod, delegateField, invokeMethod);
            targetMethod.AddMarkAttributeIfNotExists(markAttribute);
            targetMethod.AddCustomAttribute(targetModule.FindCorrespondingType(typeof(DebuggerStepThroughAttribute)));
            return true;
        }

        internal static void RegenerateUnitySignature(this AssemblyDefinition assembly, IList<TypeDefinition> additionalTypes)
        {
            var type = assembly.MainModule.GetType("UnitySourceGeneratedAssemblyMonoScriptTypes_v1");
            if (type is null) return;
            var mGet = type.Methods.First(m => m.Name == "Get");
            if (mGet is null) return;
            var ilPro = mGet.Body.GetILProcessor();

            Instruction instr, ldi;
            byte[] bytes;

            // get file path data binary
            instr = GetSetFor(mGet.Body, "FilePathsData");
            ldi = GetFirstPrev(instr, i => i.OpCode == OpCodes.Ldtoken);
            bytes = (ldi.Operand as FieldDefinition).InitialValue;
            int lastIndexOfTypeCount = 0;
            for (int i = 0; i < bytes.Length;)
            {
                lastIndexOfTypeCount = i;
                var typesInFile = ReadInt32BE(bytes, ref i);
                var filePath = ReadNStringUTF8(bytes, ref i);
                Logger.Verbose("file:" + filePath);
            }

            // set file path data binary
            var lastCount = ReadInt32BE(bytes, ref lastIndexOfTypeCount);
            lastIndexOfTypeCount -= 4;
            WriteInt32BE(bytes, ref lastIndexOfTypeCount, lastCount + additionalTypes.Count);

            // get types data binary
            instr = GetSetFor(mGet.Body, "TypesData");
            ldi = GetFirstPrev(instr, i => i.OpCode == OpCodes.Ldtoken);
            var fldTypesData = ldi.Operand as FieldDefinition;
            bytes = fldTypesData.InitialValue;
            for (int i = 0; i < bytes.Length;)
            {
                var isPartial = ReadByte(bytes, ref i);
                var typeSignature = ReadNStringUTF8(bytes, ref i);
                Logger.Verbose("type:" + typeSignature);
            }

            // set types data binary
            var newBytes = new List<byte>(bytes);
            foreach (var additionalType in additionalTypes)
            {
                AppendByte(newBytes, 0);
                AppendNStringUTF8(newBytes, GetUnitySourceGeneratorLikeTypeName(additionalType));
            }

            var baseValueType = assembly.MainModule.FindCorrespondingType(typeof(ValueType));
            var ldarrcnt = GetFirstPrev(ldi, i => TryGetLdcI4Value(i, out var arrSize));
            fldTypesData.FieldType.Resolve().ClassSize = newBytes.Count;
            fldTypesData.InitialValue = newBytes.ToArray();
            ilPro.Replace(ldarrcnt, ilPro.CreateLdcI4(newBytes.Count));

            // var totalFiles = 0;
            // instr = GetSetFor(mGet.Body, "TotalFiles");
            // ldi = GetFirstPrev(instr, i => GetConstValueInt32(i, out totalFiles));
            // Logger.Info("TotalFiles " + totalFiles);

            // get total types
            var totalTypes = 0;
            instr = GetSetFor(mGet.Body, "TotalTypes");
            ldi = GetFirstPrev(instr, i => TryGetLdcI4Value(i, out totalTypes));
            Logger.Verbose("TotalTypes " + totalTypes);

            // set total types
            ilPro.Replace(ldi, ilPro.CreateLdcI4(totalTypes + additionalTypes.Count));

            static void WriteInt32BE(byte[] buffer, ref int index, int value)
            {
                buffer[index + 3] = (byte)(value & 0xFF);
                value >>= 8;
                buffer[index + 2] = (byte)(value & 0xFF);
                value >>= 8;
                buffer[index + 1] = (byte)(value & 0xFF);
                value >>= 8;
                buffer[index + 0] = (byte)(value & 0xFF);
                value >>= 8;

                index += 4;
            }

            static int ReadInt32BE(byte[] buffer, ref int index)
            {
                if (index + 4 > buffer.Length)
                    throw new IndexOutOfRangeException($"buffer size is {buffer.Length}, index is {index}");
                var n = 0;
                n = (n << 8) + buffer[index++];
                n = (n << 8) + buffer[index++];
                n = (n << 8) + buffer[index++];
                n = (n << 8) + buffer[index++];
                return n;
            }

            // static void WriteByte(byte[] buffer, ref int index, byte value)
            // {
            //     buffer[index++] = value;
            // }

            static byte ReadByte(byte[] buffer, ref int index)
            {
                return buffer[index++];
            }

            static void AppendByte(List<byte> buffer, byte value)
            {
                buffer.Add(value);
            }

            static void AppendInt32BE(List<byte> buffer, int value)
            {
                buffer.Add((byte)(value >> 24));
                buffer.Add((byte)(value >> 16));
                buffer.Add((byte)(value >> 8));
                buffer.Add((byte)(value >> 0));
            }

            static void AppendNStringUTF8(List<byte> buffer, string value)
            {
                var len = value.Length;
                AppendInt32BE(buffer, len);
                var utf8 = Encoding.UTF8.GetBytes(value);
                buffer.AddRange(utf8);
            }

            static string ReadNStringUTF8(byte[] buffer, ref int index)
            {
                var l = ReadInt32BE(buffer, ref index);
                var str = Encoding.UTF8.GetString(buffer, index, l);
                index += l;
                return str;
            }

            static Instruction GetFirstPrev(Instruction instr, Predicate<Instruction> predicate)
            {
                instr = instr.Previous;
                while (instr != null)
                {
                    if (predicate(instr)) return instr;
                    instr = instr.Previous;
                }

                return null;
            }

            static Instruction GetSetFor(MethodBody body, string name)
            {
                return body.Instructions.First(c =>
                    c.OpCode == OpCodes.Stfld &&
                    c.Operand is FieldDefinition fd &&
                    fd.Name == name);
            }

            static string GetUnitySourceGeneratorLikeTypeName(TypeDefinition td)
            {
                var patterns = new List<string>(); // (ns),([decltype],]),name
                var declType = td;
                patterns.Add(declType.Name);
                while (declType.DeclaringType != null)
                {
                    declType = declType.DeclaringType;
                    patterns.Add(declType.Name);
                }

                if (!string.IsNullOrEmpty(declType.Namespace))
                {
                    patterns.Add(declType.Namespace);
                }

                patterns.Reverse();
                return string.Join('.', patterns.SkipLast(1)) + "|" + patterns[^1];
            }

            static void GetAllTypes(TypeDefinition td, HashSet<string> result)
            {
                if (td.CustomAttributes.Any(attr => attr.AttributeType.Name == nameof(CompilerGeneratedAttribute)))
                {
                    return;
                }

                result.Add(GetUnitySourceGeneratorLikeTypeName(td));

                foreach (var nest in td.NestedTypes)
                {
                    GetAllTypes(nest, result);
                }
            }

        }

        #region defination
        internal static void MarkByAttribute(this AssemblyDefinition assembly)
        {
            var typeSystem = assembly.MainModule.TypeSystem;
            var module = assembly.MainModule;
            var attributeType = assembly.MainModule.FindCorrespondingType(typeof(System.Reflection.AssemblyDescriptionAttribute));
            var attribute = assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType == attributeType);
            if (attribute == null)
            {
                var ctorMethod = attributeType.Resolve().Methods.Single(m => true
                    && m.Name == ".ctor"
                    && m.Parameters.Count == 1
                    // && m.Parameters[0].ParameterType == typeSystem.String
                    );
                Logger.Verbose($"{module} im attr {ctorMethod.Module}");
                attribute = new CustomAttribute(module.ImportReference(ctorMethod));
                attribute.ConstructorArguments.Add(new CustomAttributeArgument(typeSystem.String, ""));
                assembly.CustomAttributes.Add(attribute);
            }

            var oldDesc = attribute.ConstructorArguments[0].Value as string;
            attribute.ConstructorArguments[0] = new CustomAttributeArgument(typeSystem.String, oldDesc + WeavingUtils.INJECTED_DESCRIPTION_SUFFIX);
        }

        static Dictionary<AssemblyDefinition, TypeDefinition> s_cacheAttributes;
        internal static TypeDefinition GetOrCreateMarkAttribute(this AssemblyDefinition assembly)
        {
            s_cacheAttributes ??= new();
            if (s_cacheAttributes.TryGetValue(assembly, out var markAttribute))
            {
                return markAttribute;
            }

            markAttribute = assembly.MainModule.Types.FirstOrDefault(t => t.Name == "InjectedMethodAttribute");
            if (markAttribute == null)
            {
                var attrBaseType = assembly.MainModule.FindCorrespondingType(typeof(Attribute));
                markAttribute = new TypeDefinition("com.bbbirder.UnityInjection", "InjectedMethodAttribute", TypeAttributes.Public, attrBaseType);

                var tokenField = new FieldDefinition("originalToken", FieldAttributes.Public, assembly.MainModule.TypeSystem.Int32);
                markAttribute.Fields.Add(tokenField);

                var ctor2 = new MethodDefinition(".ctor", MethodAttributes.Public | MethodAttributes.SpecialName, assembly.MainModule.TypeSystem.Void);
                ctor2.Parameters.Add(new ParameterDefinition(assembly.MainModule.TypeSystem.Int32));
                var ilPro = ctor2.Body.GetILProcessor();
                ilPro.Append(Instruction.Create(OpCodes.Ldarg_0));
                ilPro.Append(Instruction.Create(OpCodes.Ldarg_1));
                ilPro.Append(Instruction.Create(OpCodes.Stfld, tokenField));
                ilPro.Append(Instruction.Create(OpCodes.Ret));
                markAttribute.Methods.Add(ctor2);

                assembly.MainModule.Types.Add(markAttribute);
            }

            s_cacheAttributes[assembly] = markAttribute;
            return markAttribute;
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="method"></param>
        /// <returns>True if already exists</returns>
        internal static void AddCustomAttribute(this MethodDefinition method, TypeReference attributeType, params CustomAttributeArgument[] arguments)
        {
            attributeType = method.Module.ImportReference(attributeType);
            var ctorMethod = attributeType.Resolve().Methods.Single(m => true
                && m.Name == ".ctor"
                && m.Parameters.Count == arguments.Length
                // && m.Parameters[0].ParameterType == typeSystem.String
                );
            var attribute = new CustomAttribute(method.Module.ImportReference(ctorMethod));
            foreach (var arg in arguments)
            {
                attribute.ConstructorArguments.Add(arg);
            }

            method.CustomAttributes.Add(attribute);
        }

        internal static void AddMarkAttributeIfNotExists(this MethodDefinition method, TypeReference markAttributeType)
        {
            if (!IsExistsMethodMarkAttribute(method, markAttributeType))
            {
                var lowerToken = method.MetadataToken.ToInt32() & 0xFFFFFF;
                method.AddCustomAttribute(markAttributeType, new CustomAttributeArgument(method.Module.TypeSystem.Int32, lowerToken));
            }
        }

        internal static bool IsExistsMethodMarkAttribute(this MethodDefinition method, TypeReference attributeType)
        {
            var attribute = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == attributeType.FullName);
            return attribute != null;
        }

        internal static bool HasInjectedMark(this AssemblyDefinition assembly)
        {
            var typeSystem = assembly.MainModule.TypeSystem;
            var attributeType = assembly.MainModule.FindCorrespondingType(typeof(System.Reflection.AssemblyDescriptionAttribute));
            var attribute = assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == attributeType.FullName);
            if (attribute == null)
            {
                return false;
            }

            var oldDesc = attribute.ConstructorArguments[0].Value as string;
            return oldDesc.Contains(WeavingUtils.INJECTED_DESCRIPTION_SUFFIX);
        }

        internal static CustomAttribute AddAttribute(this AssemblyDefinition assembly, Type attributeType)
        {
            var corAttributeType = assembly.MainModule.FindCorrespondingType(attributeType);
            return assembly.CustomAttributes.FirstOrDefault(a => a.AttributeType == corAttributeType);
        }

        internal static TypeDefinition CreateAdditionalTypeAttributeType(this AssemblyDefinition assembly)
        {
            var baseAttrType = assembly.MainModule.FindCorrespondingType(typeof(Attribute));
            var baseTypeType = assembly.MainModule.FindCorrespondingType(typeof(Type));
            var attributeType = new TypeDefinition(
                "com.bbbirder.UnityInjection",
                "AdditionalTypeAttribute",
                TypeAttributes.NotPublic,
                baseAttrType);
            var ctor = new MethodDefinition(".ctor", MethodAttributes.Public, assembly.MainModule.TypeSystem.Void);
            ctor.Parameters.Add(new ParameterDefinition(baseTypeType));
            assembly.MainModule.Types.Add(attributeType);
            return attributeType;
        }

        internal static void AddAdditionalTypeAttribute(this AssemblyDefinition assembly, TypeReference attributeType, TypeReference additionalType)
        {
            var baseTypeType = assembly.MainModule.FindCorrespondingType(typeof(Type));
            var ctorMethod = attributeType.Resolve().Methods.Single(m => true
                && m.Name == ".ctor"
                && m.Parameters.Count == 1
            );
            var attribute = new CustomAttribute(ctorMethod);
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(baseTypeType, additionalType));
            assembly.CustomAttributes.Add(attribute);
        }

        static Dictionary<int, Instruction> lutOffset2Instr = new();
        /// <summary>
        /// Create a clone of the given method definition into targetType
        /// </summary>
        internal static MethodDefinition Clone(this MethodDefinition source, TypeDefinition targetType)
        {
            var result = new MethodDefinition(source.Name, source.Attributes, source.ReturnType);

            foreach (var p in source.Parameters)
            {
                var param = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
                if (p.HasConstant)
                {
                    param.Constant = p.Constant;
                }

                result.Parameters.Add(param);
            }

            // foreach (var p in source.CustomAttributes) result.CustomAttributes.Add(p);
            foreach (var p in source.GenericParameters)
            {
                result.GenericParameters.Add(new GenericParameter(p.Name, p.Owner));
            }

            lutOffset2Instr.Clear();
            if (source.HasBody)
            {
                var sourceBody = source.Body;
                var targetBody = result.Body; // get or create
                var ilProcessor = targetBody.GetILProcessor();
                ilProcessor.Body.InitLocals = true;
                if (sourceBody.HasVariables)
                {
                    foreach (var v in sourceBody.Variables)
                    {
                        targetBody.Variables.Add(v);
                    }
                }

                foreach (var i in sourceBody.Instructions)
                {
                    lutOffset2Instr[i.Offset] = i;
                    ilProcessor.Append(i);
                }

                if (sourceBody.HasExceptionHandlers)
                {
                    foreach (ExceptionHandler eh in sourceBody.ExceptionHandlers)
                    {
                        var neh = new ExceptionHandler(eh.HandlerType)
                        {
                            CatchType = eh.CatchType != null
                                ? targetType.Module.ImportReference(eh.CatchType)
                                : null
                        };
                        if (eh.TryStart != null)
                        {
                            neh.TryStart = targetBody.Instructions[sourceBody.Instructions.IndexOf(eh.TryStart)];
                        }

                        if (eh.TryEnd != null)
                        {
                            neh.TryEnd = targetBody.Instructions[sourceBody.Instructions.IndexOf(eh.TryEnd)];
                        }

                        if (eh.HandlerStart != null)
                        {
                            neh.HandlerStart = targetBody.Instructions[sourceBody.Instructions.IndexOf(eh.HandlerStart)];
                        }

                        if (eh.HandlerEnd != null)
                        {
                            neh.HandlerEnd = targetBody.Instructions[sourceBody.Instructions.IndexOf(eh.HandlerEnd)];
                        }

                        targetBody.ExceptionHandlers.Add(neh);
                    }
                }

                foreach (var sp in source.DebugInformation.SequencePoints)
                {
                    if (lutOffset2Instr.TryGetValue(sp.Offset, out var instr))
                    {
                        result.DebugInformation.SequencePoints.Add(new SequencePoint(instr, new Document(sp.Document.Url))
                        {
                            StartLine = sp.StartLine,
                            EndLine = sp.EndLine,
                            StartColumn = sp.StartColumn,
                            EndColumn = sp.EndColumn,
                        });
                    }
                }
            } // end of HasBody

            result.ImplAttributes = source.ImplAttributes;
            result.SemanticsAttributes = source.SemanticsAttributes;

            targetType.Methods.Add(result);
            return result;
        }

        internal static TypeReference FindCorrespondingType(this ModuleDefinition module, System.Type type)
        {
            // What if shipped BCL differs between weaver and weaved assembly?
            // We can find a corresponding memberInfo via metadata, Cecil has already done this for us!
            return module.ImportReference(type);
        }
        // internal static TypeReference FindCorrespondingType(this ModuleDefinition module, System.Type type)
        // {
        //     if (type == null) return null;
        //     var assemblyResolver = module.AssemblyResolver as UnityEditorAssemblyResolver;

        //     var assemblyName = type.Assembly.GetName();
        //     var signature = type.GetSignature();

        //     var result = module.FindTypeBySignature(signature);
        //     // Logger.Verbose($"{module}, {type} this find {result}");
        //     if (result != null) return result;

        //     var corlib = assemblyResolver.FindAssembly("mscorlib")?.MainModule;
        //     // Logger.Verbose("got corlib " + corlib);
        //     result = corlib.FindTypeBySignature(signature);
        //     // Logger.Verbose($"{module}, {type} corlib '{corlib}' find {result}");
        //     if (result != null) return module.ImportReference(result);

        //     var possibleModule = assemblyResolver.FindAssembly(assemblyName.Name)?.MainModule;
        //     if (possibleModule != null)
        //     {
        //         result = possibleModule.FindTypeBySignature(signature);
        //         // Logger.Verbose($"{module}, {type} search name find {result}");
        //         if (result != null) return module.ImportReference(result);
        //     }

        //     // var correspondingName = new AssemblyNameDefinition(assemblyName.Name, assemblyName.Version);
        //     // var correspondingModule = module.AssemblyResolver.Resolve(correspondingName).MainModule;
        //     // var typeDefinition = correspondingModule.FindTypeBySignature(signature);
        //     // Logger.Verbose($"{module}, {type} from {result.Module}");
        //     return module.ImportReference(result);
        // }

        internal static TypeReference CreateOrGetCorlibDelegateForMethod(this ModuleDefinition module, MethodDefinition method, out MethodReference invokeMethod)
        {
            do
            {
                bool hasByRefParamter = false;
                foreach (var parameter in method.Parameters)
                {
                    if (parameter.ParameterType.IsByReference || parameter.IsOut)
                    {
                        hasByRefParamter = true;
                        break;
                    }
                }

                var hasThis = method.HasThis;
                if (hasThis && method.DeclaringType.IsValueType)
                {
                    hasByRefParamter = true;
                }

                if (hasByRefParamter)
                    break;

                var parameterCount = method.Parameters.Count;
                var hasReturnType = method.ReturnType != module.TypeSystem.Void;
                if (hasThis) parameterCount++;
                // if (hasReturnType) parameterCount++;

                var rtBclMemberInfo = hasReturnType
                    ? Type.GetType("System.Func`" + -~parameterCount)
                    : Type.GetType("System.Action`" + parameterCount)
                    ;
                if (rtBclMemberInfo == null)
                    break;

                var bclDelegateGeneric = FindCorrespondingType(module, rtBclMemberInfo);
                if (bclDelegateGeneric == null)
                    break;

                var bclDelegateInstance = new GenericInstanceType(bclDelegateGeneric);

                if (hasThis)
                {
                    bclDelegateInstance.GenericArguments.Add(method.DeclaringType);
                }

                foreach (var p in method.Parameters)
                {
                    bclDelegateInstance.GenericArguments.Add(p.ParameterType);
                }

                if (hasReturnType)
                {
                    bclDelegateInstance.GenericArguments.Add(method.ReturnType);
                }

                var originInvokeMethod = bclDelegateInstance.Resolve().Methods.Single(m => m.Name == "Invoke");
                invokeMethod = new MethodReference("Invoke", originInvokeMethod.ReturnType)
                {
                    DeclaringType = bclDelegateInstance,
                    HasThis = originInvokeMethod.HasThis,
                    ExplicitThis = originInvokeMethod.ExplicitThis,
                    CallingConvention = originInvokeMethod.CallingConvention,
                };

                for (int i = 0; i < originInvokeMethod.Parameters.Count; i++)
                {
                    var parameter = originInvokeMethod.Parameters[i];
                    invokeMethod.Parameters.Add(new ParameterDefinition("arg" + -~i, parameter.Attributes, parameter.ParameterType));
                }

                invokeMethod = module.ImportReference(invokeMethod);

                return bclDelegateInstance;
            } while (false);

            var customDelegateType = CreateDelegateForMethod(method, out invokeMethod, true);
            module.Types.Add(customDelegateType);
            // targetType.NestedTypes.Add(customDelegateType);
            return customDelegateType;
        }


        /// <summary>
        /// Create a delegate for the given method.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="moreCompatible">if True, the return type will be set to `object` on reference types</param>
        /// <returns></returns>
        internal static TypeDefinition CreateDelegateForMethod(MethodDefinition templateMethod, out MethodReference invokeMethod, bool moreCompatible = true)
        {
            var module = templateMethod.Module;
            var typeSystem = templateMethod.Module.TypeSystem;
            var returnType = templateMethod.ReturnType;
            var thisType = templateMethod.DeclaringType as TypeReference;

            if (moreCompatible)
            {
                if (!returnType.IsValueType && returnType != typeSystem.Void) returnType = typeSystem.Object;
            }

            if (thisType.IsValueType)
            {
                thisType = new ByReferenceType(thisType);
            }

            if (templateMethod.IsStatic)
            {
                thisType = null;
            }

            var typeAttributes = 0
                | TypeAttributes.Sealed
                | TypeAttributes.Abstract
                | TypeAttributes.NotPublic
                ;
            var methodAttributes = default(MethodAttributes);

            var typeName = WeavingUtils.GetDelegateName(templateMethod.Name, templateMethod.MetadataToken.ToInt32());
            var delDef = new TypeDefinition("", typeName, typeAttributes, module.FindCorrespondingType(typeof(System.MulticastDelegate)));

            // add .ctor
            {
                methodAttributes = 0
                    | MethodAttributes.FamANDAssem
                    | MethodAttributes.Family
                    | MethodAttributes.HideBySig
                    | MethodAttributes.RTSpecialName
                    | MethodAttributes.SpecialName
                    ;
                var ctor = new MethodDefinition(".ctor", methodAttributes, typeSystem.Void)
                {
                    IsRuntime = true,
                };
                ctor.Parameters.Add(new ParameterDefinition(typeSystem.Object));
                ctor.Parameters.Add(new ParameterDefinition(typeSystem.IntPtr));
                // ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                // ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
                // ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, module.FindCorrespondingType(typeof(MulticastDelegate)).Resolve().GetConstructor(new[] { typeof(object), typeof(IntPtr) }))));
                // ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                delDef.Methods.Add(ctor);
            }

            // add Invoke
            {
                methodAttributes = 0
                    | MethodAttributes.Public
                    | MethodAttributes.HideBySig
                    | MethodAttributes.NewSlot
                    | MethodAttributes.Virtual
                    ;
                var method = new MethodDefinition("Invoke", methodAttributes, returnType)
                {
                    HasThis = true,
                    IsRuntime = true,
                };

                if (thisType != null)
                {
                    method.Parameters.Add(new ParameterDefinition(thisType));
                }

                foreach (var p in templateMethod.Parameters)
                {
                    var param = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
                    if (p.HasConstant)
                    {
                        param.Constant = p.Constant;
                    }

                    method.Parameters.Add(param);
                }

                delDef.Methods.Add(method);
                invokeMethod = method;
            }

            // add BeginInvoke
            {
                methodAttributes = 0
                    | MethodAttributes.Public
                    | MethodAttributes.HideBySig
                    | MethodAttributes.NewSlot
                    | MethodAttributes.Virtual
                    ;
                var method = new MethodDefinition("BeginInvoke", methodAttributes, module.FindCorrespondingType(typeof(System.IAsyncResult)))
                {
                    HasThis = true,
                    IsRuntime = true,
                };
                if (thisType != null)
                {
                    method.Parameters.Add(new ParameterDefinition(thisType));
                }

                foreach (var p in templateMethod.Parameters)
                {
                    var param = new ParameterDefinition(p.Name, p.Attributes, p.ParameterType);
                    if (p.HasConstant)
                    {
                        param.Constant = p.Constant;
                    }

                    method.Parameters.Add(param);
                }

                method.Parameters.Add(new ParameterDefinition(module.FindCorrespondingType(typeof(System.AsyncCallback))));
                method.Parameters.Add(new ParameterDefinition(typeSystem.Object));
                delDef.Methods.Add(method);
            }

            // add EndInvoke
            {
                methodAttributes = 0
                    | MethodAttributes.Public
                    | MethodAttributes.HideBySig
                    | MethodAttributes.NewSlot
                    | MethodAttributes.Virtual
                    ;
                var method = new MethodDefinition("EndInvoke", methodAttributes, returnType)
                {
                    HasThis = true,
                    IsRuntime = true,
                };
                // if (thisType != null && thisType.IsByReference)
                // {
                //     method.Parameters.Add(new ParameterDefinition(thisType));
                // }
                // foreach (var arg in templateMethod.Parameters)
                // {
                //     if (arg.ParameterType.IsByReference)
                //         method.Parameters.Add(arg);
                // }
                var argAsync = new ParameterDefinition(module.FindCorrespondingType(typeof(IAsyncResult)));
                method.Parameters.Add(argAsync);
                delDef.Methods.Add(method);
            }

            return delDef;
        }
        #endregion

        #region emission

        /// <summary>
        /// Jump to <paramref name="destinationMethod"/> when <paramref name="delegateField"/> is unset.
        /// </summary>
        /// <param name="sourceMethod"></param>
        /// <param name="destinationMethod"></param>
        /// <param name="delegateField"></param>
        internal static void MakeJumpMethod(
            this MethodDefinition sourceMethod, MethodDefinition destinationMethod,
            FieldDefinition delegateField, MethodReference invokeMethod
        )
        {
            var argidx = 0;
            var parameters = sourceMethod.Parameters;
            var returnType = sourceMethod.ReturnType;

            //redirect method
            if (!sourceMethod.HasBody)
            {
                throw new ArgumentException($"method {sourceMethod.Name} dont have a body");
            }

            sourceMethod.Body.Instructions.Clear();
            var ilProcessor = sourceMethod.Body.GetILProcessor();
            ilProcessor.Body.InitLocals = true;
            ilProcessor.Body.Variables.Clear();
            ilProcessor.Body.ExceptionHandlers.Clear();

            var tagOp = Instruction.Create(OpCodes.Nop);
            //check null
            // ilProcessor.Append(Instruction.Create(OpCodes.Nop));
            ilProcessor.Append(Instruction.Create(OpCodes.Ldsfld, delegateField));
            ilProcessor.Append(Instruction.Create(OpCodes.Brtrue_S, tagOp));

            //invoke origin
            argidx = 0;
            if (!sourceMethod.IsStatic)
                ilProcessor.Append(ilProcessor.CreateLdarg(argidx++));
            for (var i = 0; i < parameters.Count; i++)
            {
                ilProcessor.Append(ilProcessor.CreateLdarg(argidx++));
            }

            // ilProcessor.Append(Instruction.Create(OpCodes.Tail));
            if (destinationMethod.IsVirtual)
                ilProcessor.Append(Instruction.Create(OpCodes.Callvirt, destinationMethod));
            else
                ilProcessor.Append(Instruction.Create(OpCodes.Call, destinationMethod));
            ilProcessor.Append(Instruction.Create(OpCodes.Ret));

            //invoke
            // var delegateInvoker = source.Module.ImportReference(delegateField.FieldType.Resolve().Methods.First(m => m.Name == "Invoke"));
            var delegateInvoker = invokeMethod;
            ilProcessor.Append(tagOp);
            ilProcessor.Append(Instruction.Create(OpCodes.Ldsfld, delegateField));
            argidx = 0;
            if (!sourceMethod.IsStatic)
                ilProcessor.Append(ilProcessor.CreateLdarg(argidx++));
            for (var i = 0; i < parameters.Count; i++)
            {
                ilProcessor.Append(ilProcessor.CreateLdarg(argidx++));
            }

            // ilProcessor.Append(Instruction.Create(OpCodes.Tail));
            ilProcessor.Append(Instruction.Create(OpCodes.Callvirt, delegateInvoker));
            ilProcessor.Append(Instruction.Create(OpCodes.Ret));

            sourceMethod.Body.ExceptionHandlers.Clear();
            sourceMethod.DebugInformation.SequencePoints.Clear();
        }

        static OpCode[] s_ldargs = new[] { OpCodes.Ldarg_0, OpCodes.Ldarg_1, OpCodes.Ldarg_2, OpCodes.Ldarg_3 };
        static Instruction CreateLdarg(this ILProcessor ilProcessor, int i)
        {
            if (i < s_ldargs.Length)
            {
                return Instruction.Create(s_ldargs[i]);
            }
            else if (i < 256)
            {
                return ilProcessor.Create(OpCodes.Ldarg_S, (byte)i);
            }
            else
            {
                return ilProcessor.Create(OpCodes.Ldarg, (short)i);
            }
        }

        static OpCode[] s_ldc_ints = new[] {
            OpCodes.Ldc_I4_0, OpCodes.Ldc_I4_1, OpCodes.Ldc_I4_2, OpCodes.Ldc_I4_3,
            OpCodes.Ldc_I4_4, OpCodes.Ldc_I4_5, OpCodes.Ldc_I4_6, OpCodes.Ldc_I4_7,
            OpCodes.Ldc_I4_8,
        };
        static Instruction CreateLdcI4(this ILProcessor ilProcessor, int i)
        {
            if (i == -1)
            {
                return ilProcessor.Create(OpCodes.Ldc_I4_M1);
            }

            if (i >= 0 && i < s_ldc_ints.Length)
            {
                return ilProcessor.Create(s_ldc_ints[i]);
            }

            if (-128 <= i && i < 127)
            {
                return ilProcessor.Create(OpCodes.Ldc_I4_S, (sbyte)i);
            }
            else
            {
                return ilProcessor.Create(OpCodes.Ldc_I4, i);
            }
        }

        static bool TryGetLdcI4Value(Instruction instr, out int int32)
        {
            if (instr.OpCode == OpCodes.Ldc_I4_M1)
            {
                int32 = -1;
                return true;
            }

            var idx = Array.IndexOf(s_ldc_ints, instr.OpCode);
            if (idx > 0)
            {
                int32 = idx;
                return true;
            }

            if (instr.OpCode == OpCodes.Ldc_I4_S || instr.OpCode == OpCodes.Ldc_I4)
            {
                int32 = Convert.ToInt32(instr.Operand);
                return true;
            }

            int32 = -1;
            return false;
        }

        #endregion

        #region signature
        public static TypeDefinition FindTypeBySignature(this ModuleDefinition module, string signature)
        {
            var nestingTypes = signature.Split('+');
            var type = module.Types.FirstOrDefault(t => t.GetSignature() == nestingTypes[0]);
            // Logger.Verbose($"find by sig start {signature} " + type);
            if (type == null) return null;
            foreach (var name in nestingTypes.Skip(1))
            {
                // Logger.Verbose($"find by sig {signature} iter {name} in {type} {module}");
                type = type.NestedTypes.FirstOrDefault(t => t.Name == name);
                // Logger.Verbose($"find by sig {signature} iter {name} out {type}");
                if (type == null) return null;
            }
            // Logger.Verbose($"find by sig ret" + type);
            return type;
        }

        public static MethodDefinition FindMethodBySignature(this TypeDefinition type, string signature)
        {
            return type.Methods.FirstOrDefault(m => m.GetSignature() == signature);
        }

        /// <summary>
        /// Get a signature of the given type. Likely Yours.Namespace.TypeA+TypeB`1+TypeC
        /// </summary>
        /// <param name="t"></param>
        /// <param name="genericContext"></param>
        /// <returns></returns>
        internal static string GetSignature(this TypeReference t, GenericContext genericContext = null)
        {
            var includesGenericContext = null != genericContext;
            var declaringTypes = GetDeclaringTypes(t).Reverse().ToArray();
            var builder = new StringBuilder();
            // append namespace
            if (!string.IsNullOrEmpty(declaringTypes[0].Namespace))
            {
                builder.Append(declaringTypes[0].Namespace);
                builder.Append('.');
            }

            // append nesting types
            builder.AppendJoin('+', declaringTypes.Select(t => t.Name));

            // // append generic arguments
            if (t is GenericInstanceType git && includesGenericContext)
            {
                var genericArgumentTypes = git.GenericArguments;
                builder.Append('[');
                for (int i = 0; i < genericArgumentTypes.Count; i++)
                {
                    var gat = genericArgumentTypes[i];
                    if (i > 0) builder.Append(",");
                    if (gat.IsGenericParameter && gat is GenericParameter gp)
                    {
                        if (gp.Type == GenericParameterType.Type)
                        {
                            builder.Append(".T");
                            builder.Append(gp.Position);
                        }
                        else
                        {
                            builder.Append(".T");
                            builder.Append(gp.Position + genericContext.klassGenericArguments.Count);
                        }
                    }
                    else
                    {
                        builder.Append(gat.GetSignature(genericContext));
                    }
                }

                builder.Append(']');
            }

            return builder.ToString();

            static IEnumerable<TypeReference> GetDeclaringTypes(TypeReference type)
            {
                while (type != null)
                {
                    yield return type;
                    type = type.DeclaringType;
                }
            }
        }

        /// <summary>
        /// Get a signature of the given method. Likely "MethodName`1(Type1,.T1,Type2)"
        /// </summary>
        /// <param name="method"></param>
        /// <returns></returns>
        internal static string GetSignature(this MethodDefinition method)
        {
            // implement of CLS Rule 43 

            var builder = new StringBuilder();
            // append method name
            builder.Append(method.Name);
            if (method.HasGenericParameters)
            {
                builder.Append('`');
                builder.Append(method.GenericParameters.Count);
            }

            // append method parameters
            builder.Append('<');
            var declaringType = method.Module.LookupToken(method.DeclaringType.MetadataToken) as TypeReference;
            var parameters = method.Parameters;
            for (int i = 0; i < parameters.Count; i++)
            {
                var parameterDefinition = parameters[i];
                var parameterType = parameterDefinition.ParameterType;
                if (i > 0)
                {
                    builder.Append(",");
                }

                var klassGenericArguments = declaringType.GenericParameters;
                var methodGenericArguments = method.GenericParameters;

                if (parameterType.IsGenericParameter && parameterType is GenericParameter gp)
                {
                    if (gp.Type == GenericParameterType.Type)
                    {
                        builder.Append("!");
                        builder.Append(gp.Position);
                    }
                    else
                    {
                        builder.Append("!");
                        builder.Append(gp.Position + klassGenericArguments.Count);
                    }
                }
                else
                {
                    var argSig = GetSignature(parameterType, new()
                    {
                        klassGenericArguments = klassGenericArguments,
                        methodGenericArguments = methodGenericArguments,
                    });
                    builder.Append(argSig);
                }
            }

            builder.Append('>');
            return builder.ToString();
        }

        internal class GenericContext
        {
            public Collection<GenericParameter> klassGenericArguments;
            public Collection<GenericParameter> methodGenericArguments;
        }
        #endregion
    }
}

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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
                    cache[name] = assemblyDefinition = ModuleDefinition.ReadModule(path).Assembly;
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

        public static void InjectAssembly(string assemblyPath, WeavingRecord[] weavingRecords, string[] allowedAssemblies, string outputPath)
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

            foreach (var record in weavingRecords.Distinct())
            {
                var (_, klsSig, mthSig) = record;
                var methodName = mthSig.Split(".")[^1];
                var targetType = targetAssembly.MainModule.FindTypeBySignature(klsSig);
                if (targetType is null)
                {
                    Logger.Warning($"Cannot find Type `{klsSig}` in target assembly {assemblyPath}");
                    continue;
                }
                var targetMethod = targetType.FindMethodBySignature(mthSig);
                if (targetMethod is null)
                {
                    Logger.Warning($"Cannot find Method `{mthSig}` in Type `{klsSig}`");
                    continue;
                }

                isDirty |= record.customWeaveAction(targetMethod);
            }

            // targetAssembly.RegenerateUnitySignature(additionalTypes);
            if (isDirty)
            {
                Logger.Info("writed");
                targetAssembly.Write(outputPath, new WriterParameters()
                {
                    WriteSymbols = shouldAccessSymbols,
                });
            }
            else
            {
                Logger.Info("already uptodate");
            }

        }

        public static bool DefaultWeaveAction(MethodDefinition targetMethod)
        {
            var targetType = targetMethod.DeclaringType;
            var targetModule = targetType.Module;
            var markAttribute = targetModule.Assembly.GetOrCreateMarkAttribute();
            //check if already injected
            if (IsExistsMethodMarkAttribute(targetMethod, markAttribute)) return false;

            //clone origin
            var originName = WeavingUtils.GetOriginMethodName(targetMethod.MetadataToken.ToInt32());
            var duplicatedMethod = targetMethod.Clone();
            duplicatedMethod.IsPrivate = true;
            duplicatedMethod.Name = originName;
            targetType.Methods.Add(duplicatedMethod);

            //add delegate
            var delegateType = CecilHelper.CreateDelegateForMethod(targetMethod, true);
            // Fixes: Il2Cpp will collect it twice in DataModelBuilder::BuildCecilSourcedData!
            // targetType.NestedTypes.Add(delegateType);
            targetModule.Types.Add(delegateType);

            //add static field
            var delegateName = WeavingUtils.GetInjectedFieldName(targetMethod.MetadataToken.ToInt32());
            var delegateField = new FieldDefinition(delegateName, Mono.Cecil.FieldAttributes.Static | Mono.Cecil.FieldAttributes.Private, delegateType);
            targetType.Fields.Add(delegateField);

            //write method body
            targetMethod.MakeJumpMethod(duplicatedMethod, delegateField);
            targetMethod.AddMethodMarkAttribute(markAttribute);
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
        internal static bool AddMethodMarkAttribute(this MethodDefinition method, TypeReference attributeType)
        {
            attributeType = method.Module.ImportReference(attributeType);
            var ctorMethod = attributeType.Resolve().Methods.Single(m => true
                && m.Name == ".ctor"
                && m.Parameters.Count == 1
                // && m.Parameters[0].ParameterType == typeSystem.String
                );
            var attribute = new CustomAttribute(method.Module.ImportReference(ctorMethod));
            var lowerToken = method.MetadataToken.ToInt32() & 0xFFFFFF;
            attribute.ConstructorArguments.Add(new CustomAttributeArgument(method.Module.TypeSystem.Int32, lowerToken));
            method.CustomAttributes.Add(attribute);
            return false;
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

        /// <summary>
        /// Create a clone of the given method definition
        /// </summary>
        internal static MethodDefinition Clone(this MethodDefinition source)
        {
            var result = new MethodDefinition(source.Name, source.Attributes, source.ReturnType)
            {
                ImplAttributes = source.ImplAttributes,
                SemanticsAttributes = source.SemanticsAttributes,
                HasThis = source.HasThis,
                ExplicitThis = source.ExplicitThis,
                CallingConvention = source.CallingConvention
            };
            foreach (var p in source.Parameters) result.Parameters.Add(p);
            // foreach (var p in source.CustomAttributes) result.CustomAttributes.Add(p);
            foreach (var p in source.GenericParameters) result.GenericParameters.Add(p);
            if (source.HasBody)
            {
                result.Body = CloneMethodBody(source.Body, result);
            }
            return result;
            static MethodBody CloneMethodBody(MethodBody source, MethodDefinition target)
            {
                var result = new MethodBody(target) { InitLocals = source.InitLocals, MaxStackSize = source.MaxStackSize };
                var worker = result.GetILProcessor();
                if (source.HasVariables)
                {
                    foreach (var v in source.Variables)
                    {
                        result.Variables.Add(v);
                    }
                }
                foreach (var i in source.Instructions)
                {
                    // Poor mans clone, but sufficient for our needs
                    var clone = Instruction.Create(OpCodes.Nop);
                    clone.OpCode = i.OpCode;
                    clone.Operand = i.Operand;
                    worker.Append(clone);
                }
                return result;
            }
        }


        internal static TypeReference FindCorrespondingType(this ModuleDefinition module, System.Type type)
        {
            if (type == null) return null;
            var assemblyResolver = module.AssemblyResolver as UnityEditorAssemblyResolver;

            var assemblyName = type.Assembly.GetName();
            var signature = type.GetSignature();

            var result = module.FindTypeBySignature(signature);
            // Logger.Verbose($"{module}, {type} this find {result}");
            if (result != null) return result;

            var corlib = assemblyResolver.FindAssembly("mscorlib")?.MainModule;
            // Logger.Verbose("got corlib " + corlib);
            result = corlib.FindTypeBySignature(signature);
            // Logger.Verbose($"{module}, {type} corlib '{corlib}' find {result}");
            if (result != null) return module.ImportReference(result);

            var possibleModule = assemblyResolver.FindAssembly(assemblyName.Name)?.MainModule;
            if (possibleModule != null)
            {
                result = possibleModule.FindTypeBySignature(signature);
                // Logger.Verbose($"{module}, {type} search name find {result}");
                if (result != null) return module.ImportReference(result);
            }

            // var correspondingName = new AssemblyNameDefinition(assemblyName.Name, assemblyName.Version);
            // var correspondingModule = module.AssemblyResolver.Resolve(correspondingName).MainModule;
            // var typeDefinition = correspondingModule.FindTypeBySignature(signature);
            // Logger.Verbose($"{module}, {type} from {result.Module}");
            return module.ImportReference(result);
        }

        /// <summary>
        /// Create a delegate for the given method.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="moreCompatible">if True, the return type will be set to `object` on reference types</param>
        /// <returns></returns>
        internal static TypeDefinition CreateDelegateForMethod(MethodDefinition templateMethod, bool moreCompatible = true)
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

            var typeName = WeavingUtils.GetDelegateName(templateMethod.MetadataToken.ToInt32());
            var delDef = new TypeDefinition("", typeName, typeAttributes, module.FindCorrespondingType(typeof(System.MulticastDelegate)));
            {   // add .ctor
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
            {   // add Invoke
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
                foreach (var arg in templateMethod.Parameters)
                {
                    method.Parameters.Add(arg);
                }
                delDef.Methods.Add(method);
            }
            {   // add BeginInvoke
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
                foreach (var arg in templateMethod.Parameters)
                {
                    method.Parameters.Add(arg);
                }
                method.Parameters.Add(new ParameterDefinition(module.FindCorrespondingType(typeof(System.AsyncCallback))));
                method.Parameters.Add(new ParameterDefinition(typeSystem.Object));
                delDef.Methods.Add(method);
            }
            {   // add EndInvoke
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
        /// Jump to <paramref name="destination"/> when <paramref name="delegateField"/> is unset.
        /// </summary>
        /// <param name="source"></param>
        /// <param name="destination"></param>
        /// <param name="delegateField"></param>
        internal static void MakeJumpMethod(
            this MethodDefinition source, MethodDefinition destination,
            FieldDefinition delegateField
        )
        {
            var argidx = 0;
            var Parameters = source.Parameters;
            var ReturnType = source.ReturnType;

            //redirect method
            if (!source.HasBody)
            {
                throw new ArgumentException($"method {source.Name} dont have a body");
            }

            source.Body.Instructions.Clear();
            var ilProcessor = source.Body.GetILProcessor();
            var tagOp = Instruction.Create(OpCodes.Nop);
            //check null
            ilProcessor.Append(Instruction.Create(OpCodes.Ldsfld, delegateField));
            ilProcessor.Append(Instruction.Create(OpCodes.Brtrue_S, tagOp));

            //invoke origin
            argidx = 0;
            if (!source.IsStatic)
                ilProcessor.Append(ilProcessor.CreateLdarg(argidx++));
            for (var i = 0; i < Parameters.Count; i++)
            {
                ilProcessor.Append(ilProcessor.CreateLdarg(argidx++));
            }

            if (destination.IsVirtual)
                ilProcessor.Append(Instruction.Create(OpCodes.Callvirt, destination));
            else
                ilProcessor.Append(Instruction.Create(OpCodes.Call, destination));
            ilProcessor.Append(Instruction.Create(OpCodes.Ret));

            //invoke
            var delegateInvoker = delegateField.FieldType.Resolve().Methods.First(m => m.Name == "Invoke");
            ilProcessor.Append(tagOp);
            ilProcessor.Append(Instruction.Create(OpCodes.Ldsfld, delegateField));
            argidx = 0;
            if (!source.IsStatic)
                ilProcessor.Append(ilProcessor.CreateLdarg(argidx++));
            for (var i = 0; i < Parameters.Count; i++)
            {
                ilProcessor.Append(ilProcessor.CreateLdarg(argidx++));
            }
            ilProcessor.Append(Instruction.Create(OpCodes.Callvirt, delegateInvoker));
            ilProcessor.Append(Instruction.Create(OpCodes.Ret));
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
            if (i == -1) return ilProcessor.Create(OpCodes.Ldc_I4_M1);
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

            IEnumerable<TypeReference> GetDeclaringTypes(TypeReference type)
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
            var builder = new StringBuilder();
            // append method name
            builder.Append(method.Name);
            if (method.HasGenericParameters)
            {
                builder.Append('`');
                builder.Append(method.GenericParameters.Count);
            }

            // append method parameters
            builder.Append('(');
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
                        builder.Append(".T");
                        builder.Append(gp.Position);
                    }
                    else
                    {
                        builder.Append(".T");
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

            builder.Append(')');
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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using com.bbbirder.UnityInjection;
using MonoHook;
using UnityEngine;

namespace BBBirder.UnityInjection.Editor
{

    // not ready yet
    internal class MonoHookImplement : BaseInjectionImplement//, IEditorInjectionImplement
    {
        Dictionary<Assembly, HashSet<MethodHook>> injectedAssemblies = new();
        public override void InstallAssembly(Assembly assembly, InjectionInfo[] injectionInfos)
        {
            var asm = CreateDynamicMetadata(assembly, injectionInfos);
            var hooks = injectedAssemblies[assembly] = new();
            foreach (var info in injectionInfos)
            {
                // var targetMethod = info.InjectedMethod;
                // Debug.Log("enter " + targetMethod);
                // var asmType = asm.GetType($"{targetMethod.Name}_{targetMethod.MetadataToken}");
                // Debug.Log(asmType);
                // if (asmType == null) continue;
                // var proxyMethod = info.proxyMethod = asmType.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault();
                // Debug.Log(proxyMethod);
                // if (proxyMethod == null) continue;
                // var delegateType = CreateDynamicDelegateTypeFor(targetMethod);
                // Debug.Log(delegateType + "  -  " + proxyMethod);
                // var del = Delegate.CreateDelegate(delegateType, proxyMethod);
                // Debug.Log("pass " + del);
                // var fixingMethod = info.GetFixingMethod(del);
                // var methodHook = new MethodHook(targetMethod, fixingMethod, proxyMethod);
                // methodHook.Install();
                // Debug.Log(targetMethod);
                // hooks.Add(methodHook);
            }
        }

        public override MethodInfo GetProxyMethod(MethodBase targetMethod)
        {
            return null; //proxyMethods[targetMethod];
        }

        public override void UninstallAssembly(Assembly assembly)
        {
            var hooks = injectedAssemblies[assembly] = new();
            foreach (var hook in hooks) hook.Uninstall();
        }

        static int dynmaicIndex = 0;
        public DynamicMethod CreateDynamicMethodFor(MethodBase method)
        {
            var returnType = method is MethodInfo mi ? mi.ReturnType : typeof(void);
            var paramTypes = method.GetParameters().Select(p =>
            {
                return p.ParameterType;
            });
            if (!method.IsStatic)
            {
                var thisType = method.DeclaringType;
                if (thisType.IsValueType) thisType = thisType.MakeByRefType();
                paramTypes = paramTypes.Prepend(thisType);
            }
            var isReturnVoid = returnType == typeof(void);
            var dynMth = new DynamicMethod("dyn_" + method.Name, returnType, paramTypes.ToArray());
            var ilProcessor = dynMth.GetILGenerator();
            if (!isReturnVoid)
                ilProcessor.DeclareLocal(returnType);

            ilProcessor.Emit(OpCodes.Ldstr, "dummy scope");
            ilProcessor.Emit(OpCodes.Ldc_I4, dynmaicIndex++); // avoid Jit code being shared for multiple methods
            ilProcessor.Emit(OpCodes.Call, new Action<string, object>(Console.WriteLine).Method);// avoid being optimized
            if (!isReturnVoid)
                ilProcessor.Emit(OpCodes.Ldloc_0);
            ilProcessor.Emit(OpCodes.Ret);
            return dynMth;
        }
        // static Dictionary<MethodBase, Type> delegateTypes = new();
        // static Dictionary<MethodBase, MethodInfo> proxyMethods = new();
        public static Assembly CreateDynamicMetadata(Assembly assembly, InjectionInfo[] injectionInfos)
        {
            var infos = injectionInfos;//.Where(info => info.InjectedMethod.GetParameters().Any(p => p.ParameterType.IsByRef));

            var assemblyPath = Environment.CurrentDirectory + "/Library/ScriptAssemblies/dyn_" + assembly.GetName().Name + ".dll";
            var assemblyName = "dyn_" + assembly.GetName().Name;
            var dynAsm = AssemblyBuilder.DefineDynamicAssembly(new(assemblyName), AssemblyBuilderAccess.Save);
            var dynMod = dynAsm.DefineDynamicModule(assemblyName + ".dll");
            foreach (var _info in infos.GroupBy(info => info.InjectedMethod))
            {
                var method = _info.Key;
                var returnType = method is MethodInfo mi ? mi.ReturnType : typeof(void);
                var paramTypes = method.GetParameters().Select(p => p.ParameterType);
                if (!method.IsStatic)
                {
                    var thisType = method.DeclaringType;
                    if (thisType.IsValueType) thisType = thisType.MakeByRefType();
                    paramTypes = paramTypes.Prepend(thisType);
                }
                var isReturnVoid = returnType == typeof(void);

                var rootType = dynMod.DefineType($"{method.Name}_{method.MetadataToken}", TypeAttributes.Sealed | TypeAttributes.Abstract | TypeAttributes.Public);

                // // create delegate type
                var methodAttributes = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot | MethodAttributes.HideBySig;
                var methodImplFlags = MethodImplAttributes.Runtime | MethodImplAttributes.Managed;
                var delegateType = rootType.DefineNestedType($"Invoker_{method.MetadataToken}", TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.AutoClass, typeof(MulticastDelegate));
                delegateType.DefineConstructor(MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Public, CallingConventions.Standard,
                    new[] { typeof(object), typeof(IntPtr) })
                    .SetImplementationFlags(methodImplFlags);

                delegateType.DefineMethod("Invoke", methodAttributes, returnType,
                    paramTypes.ToArray())
                    .SetImplementationFlags(methodImplFlags);
                delegateType.DefineMethod("BeginInvoke", methodAttributes, typeof(IAsyncResult),
                    paramTypes.Concat(new[] { typeof(AsyncCallback), typeof(object) }).ToArray())
                    .SetImplementationFlags(methodImplFlags);
                delegateType.DefineMethod("EndInvoke", methodAttributes, returnType,
                    new[] { typeof(IAsyncResult) })
                    .SetImplementationFlags(methodImplFlags);
                // delegateTypes[method] = delegateType.CreateType();
                // create stub methods
                foreach (var (idx, info) in _info.Select((e, i) => (i, e)))
                {
                    var stub = rootType.DefineMethod($"Stub_{idx}", MethodAttributes.Static | MethodAttributes.Public, returnType, paramTypes.ToArray());
                    stub.SetImplementationFlags(MethodImplAttributes.NoOptimization);
                    var ilProcessor = stub.GetILGenerator();
                    if (!isReturnVoid)
                        ilProcessor.DeclareLocal(returnType);
                    ilProcessor.EmitWriteLine($"dummy scope {dynmaicIndex++}");// avoid Jit code being shared for multiple methods
                    if (!isReturnVoid)
                        ilProcessor.Emit(OpCodes.Ldloc_0);
                    ilProcessor.Emit(OpCodes.Ret);
                }
                var t = rootType.CreateType();
            }
            dynMod.CreateGlobalFunctions();
            dynAsm.Save(assemblyName + ".dll");
            return Assembly.LoadFile(Environment.CurrentDirectory + "/" + assemblyName + ".dll");
        }

        // static ModuleBuilder dynamicModule;
        public Type CreateDynamicDelegateTypeFor(MethodBase method)
        {
            var thisType = method.DeclaringType;
            if (thisType.IsValueType) thisType = thisType.MakeByRefType();
            var returnType = method is MethodInfo mi ? mi.ReturnType : typeof(void);
            if (!returnType.IsValueType) returnType = typeof(object);
            var paramTypes = method.GetParameters().Select(p => p.ParameterType);
            if (!method.IsStatic)
            {
                paramTypes = paramTypes.Prepend(thisType);
            }
            paramTypes = paramTypes.Append(returnType);
            return Expression.GetDelegateType(paramTypes.ToArray());
            // if (dynamicModule == null)
            // {
            //     var dynamicAssembly = AssemblyBuilder.DefineDynamicAssembly(new(nameof(dynamicModule)), AssemblyBuilderAccess.ReflectionOnly);
            //     dynamicModule = dynamicAssembly.DefineDynamicModule(nameof(dynamicModule));
            // }
            // Debug.Log($"{method} {method?.DeclaringType}");
            // var paramTypesArray = paramTypes.ToArray();
            // var isReturnVoid = returnType == typeof(void);

            // var methodAttributes = MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot;
            // var delegateType = dynamicModule.DefineType($"delegate_{method.Name}", TypeAttributes.Class, typeof(MulticastDelegate));
            // delegateType.DefineConstructor(MethodAttributes.SpecialName | MethodAttributes.RTSpecialName | MethodAttributes.Public, CallingConventions.Any,
            //     new[] { typeof(object), typeof(IntPtr) });

            // delegateType.DefineMethod("Invoke", methodAttributes, returnType,
            //     paramTypes.ToArray());
            // delegateType.DefineMethod("BeginInvoke", methodAttributes, typeof(IAsyncResult),
            //     paramTypes.Concat(new[] { typeof(AsyncCallback), typeof(object) }).ToArray());
            // delegateType.DefineMethod("EndInvoke", methodAttributes, returnType,
            //     new[] { typeof(IAsyncResult) });

            // return delegateType;
        }


        public override bool IsInjected(Assembly assembly) => true;
        public override bool IsInjected(MethodBase method) => true;

    }
}

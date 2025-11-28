using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BBBirder.DirectAttribute;

#if UNITY_EDITOR
using Mono.Cecil;
#endif
using UnityEngine.Assertions;
using TargetMethodType = System.Reflection.MethodBase;


namespace BBBirder.UnityInjection
{

    public partial class InjectionInfo
    {

        /// <summary>
        /// indicate the method to be injected
        /// </summary>
        public TargetMethodType InjectedMethod { get; protected set; }
        // public MethodInfo ProxyMethod { protected get; set; }

        protected Func<Delegate, Delegate> td2d;
        protected Func<Delegate, MethodInfo> td2m;
#if UNITY_EDITOR
        public Func<MethodDefinition, bool> customWeaveAction;
#endif
        public Action<InjectionInfo> customInstallAction;
        // public Action<Delegate> OriginReceiver;
        public Action onStartFix;
        protected InjectionInfo()
        {
        }
        // public void InitWithProxyDelegate(Delegate proxyMethod)
        // {

        // }
        public InjectionInfo(TargetMethodType targetMethod, Func<Delegate, MethodInfo> transformer)
        {
            InjectedMethod = targetMethod;
            td2m = transformer;
        }
        public InjectionInfo(TargetMethodType targetMethod, Func<Delegate, Delegate> transformer)
        {
            InjectedMethod = targetMethod;
            td2d = transformer;
        }

        public virtual Delegate GetFixingDelegate(Delegate proxyMethod, Type delegateType = null)
        {
            if (td2d != null) return td2d.Invoke(proxyMethod);
            if (td2m != null) return td2m.Invoke(proxyMethod).CreateDelegate(delegateType);
            return null;
        }
    }


    public sealed class InjectionInfo<T> : InjectionInfo where T : MulticastDelegate
    {
        Func<T, T> Transformer;

        public override Delegate GetFixingDelegate(Delegate proxyMethod, Type delegateType = null)
        {
            // TODO: runtime wrapper for proxy method
            if (Transformer != null) return Transformer(Delegate.CreateDelegate(typeof(T), proxyMethod.Method) as T);
            // if (Transformer != null) return Transformer(Delegate.CreateDelegate(typeof(T), proxyMethod.Method) as T).Method;
            return base.GetFixingDelegate(proxyMethod);
        }
        public InjectionInfo(T targetMethod, Func<T, T> transformer)
        {
            InjectedMethod = targetMethod.Method;
            Transformer = transformer;

        }
        public InjectionInfo(TargetMethodType targetMethod, Func<T, T> transformer)
        {
            Assert.IsNotNull(targetMethod);
            InjectedMethod = targetMethod;
            Transformer = transformer;
        }
    }


    partial class InjectionInfo
    {
        static Dictionary<Assembly, InjectionInfo[]> s_cache = new();
        static InjectionInfo[] s_allInjections;

        /// <summary>
        /// Get all InjectionInfos defined in the given assemblies.
        /// </summary>
        /// <param name="assemblies">The assemblies to search in.</param>
        /// <returns></returns>
        internal static InjectionInfo[] RetrieveInjectionInfosFrom(params Assembly[] assemblies)
        {
            return assemblies.SelectMany(RetrieveInjectionInfosFrom_Impl).ToArray();
        }

        internal static InjectionInfo[] RetriveAllInjectionInfos()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            s_allInjections ??= assemblies.SelectMany(RetrieveInjectionInfosFrom_Impl).ToArray();
            return s_allInjections;
        }

        /// <summary>
        /// Get all InjectionInfos whose target is one of the given assemblies.
        /// </summary>
        /// <param name="assemblies"></param>
        /// <returns></returns>
        internal static InjectionInfo[] RetrieveInjectionInfosTowards(params Assembly[] assemblies)
        {
            if (assemblies == null || assemblies.Length == 0)
                assemblies = AppDomain.CurrentDomain.GetAssemblies();
            return RetrieveInjectionInfosFrom()
                .Where(info => assemblies.Contains(info.InjectedMethod.DeclaringType.Assembly))
                .ToArray()
                ;
        }

        static InjectionInfo[] RetrieveInjectionInfosFrom_Impl(Assembly assembly)
        {
            if (!s_cache.TryGetValue(assembly, out var injectionInfos))
            {
                var injections = Retriever.GetAllAttributes<InjectionAttribute>(assembly)
                    .SelectMany(attr => attr.ProvideInjections())
                    ;
                var injections2 = Retriever.GetAllSubtypes<IInjectionProvider>(assembly)
                    .SelectMany(type => GetInjectionInfos(type))
                    ;
                s_cache[assembly] = injectionInfos = injections.Concat(injections2).ToArray();
            }

            return injectionInfos;
        }

        // static MethodInfo GetProvideMethod(Type type)
        // {
        //     var interfaceType = typeof(IInjectionProvider);
        //     var interfaceMethod = interfaceType.GetMethod("ProvideInjections");

        //     if (type.IsInterface)
        //     {
        //         var parameterTypes = interfaceMethod.GetParameters().Select(p => p.ParameterType).ToArray();

        //         var methodOnImplInterface = type.GetMethod(
        //             interfaceMethod.Name,
        //             BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
        //             null,
        //             CallingConventions.Any,
        //             parameterTypes,
        //             null);

        //         methodOnImplInterface ??= type.GetMethod(
        //              $"{interfaceType.FullName}.{interfaceMethod.Name}",
        //             BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic,
        //             null,
        //             CallingConventions.Any,
        //             parameterTypes,
        //             null);

        //         return methodOnImplInterface;
        //     }

        //     var interfaceMap = type.GetInterfaceMap(interfaceType);

        //     int index = Array.IndexOf(interfaceMap.InterfaceMethods, interfaceMethod);

        //     if (index != ~0)
        //     {
        //         return interfaceMap.TargetMethods[index];
        //     }

        //     return null;
        // }

        public unsafe static IEnumerable<InjectionInfo> GetInjectionInfos(Type type)
        {
            /*
            接口类和抽象类不再提供注入信息。
            当其方法内部涉及到this时，这些类型无法提供正确类型的实例。（子类不能代表基类，实现上会产生差异）
            对于类似IDataProxy的，需要影响子类的实现，有时会有有限个抽象基类或接口继承自IInjectionProvider,
            其内部应把基类的信息也Provide出来
            */
            var empty = Array.Empty<InjectionInfo>();

            // if (type.IsInterface)
            // {
            //     if (type.IsGenericType && !type.IsConstructedGenericType) return empty;

            //     var miProvide = type.GetMethod(nameof(IInjectionProvider.ProvideInjections), ProviderFlags);

            //     if (miProvide == null || miProvide.IsAbstract) return empty;
            //     if (miProvide.IsGenericMethod && !miProvide.IsConstructedGenericMethod) return empty;

            //     var @delegate = (delegate*<object, IEnumerable<InjectionInfo>>)miProvide.MethodHandle.GetFunctionPointer();
            //     return @delegate(null);
            // }

            if (type.IsAbstract || type.IsInterface)
            {
                return empty;
                // if (type.IsGenericType && !type.IsConstructedGenericType) return empty;

                // var miProvide = GetProvideMethod(type);
                // if (miProvide == null || miProvide.IsAbstract) return empty;
                // if (miProvide.IsGenericMethod && !miProvide.IsConstructedGenericMethod) return empty;

                // var subType = Retriever.GetAllSubtypes(type).FirstOrDefault(t => !t.IsAbstract && !t.IsInterface);
                // if (subType == null) return empty;

                // IInjectionProvider provider;
                // try
                // {
                //     provider = ReflectionHelper.CreateInstance(subType) as IInjectionProvider;
                // }
                // catch
                // {
                //     provider = RuntimeHelpers.GetUninitializedObject(subType) as IInjectionProvider;
                // }

                // return miProvide.Invoke(provider, null) as IEnumerable<InjectionInfo>;
            }
            else
            {
                IInjectionProvider provider;
                try
                {
                    provider = ReflectionHelper.CreateInstance(type) as IInjectionProvider;
                }
                catch
                {
                    provider = RuntimeHelpers.GetUninitializedObject(type) as IInjectionProvider;
                }

                if (provider is null) return empty;
                return provider.ProvideInjections();
            }
        }
    }
}

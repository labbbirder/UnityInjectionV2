using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using com.bbbirder;

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
        // public Action<Delegate> OriginReceiver;
        public Action onStartFix;
        protected InjectionInfo()
        {
        }
        public void InitWithProxyDelegate(Delegate proxyMethod)
        {

        }
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
            if (s_allInjections == null)
            {
                s_allInjections = assemblies.SelectMany(RetrieveInjectionInfosFrom_Impl).ToArray();
            }
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
                    .Where(type => !type.IsInterface && !type.IsAbstract)
                    .Select(type => System.Activator.CreateInstance(type) as IInjectionProvider)
                    .SelectMany(ii => ii.ProvideInjections())
                    ;
                s_cache[assembly] = injectionInfos = injections.Concat(injections2).ToArray();
            }
            return injectionInfos;
        }
    }
}

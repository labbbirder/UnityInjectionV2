
using System.Reflection;
using System.Runtime.CompilerServices;
#if UNITY_EDITOR
using UnityEditor.Build.Reporting;
#endif

namespace BBBirder.UnityInjection
{
    public interface IInjectionImplement
    {
        ImplementUsage implementUsage { get; set; }
        void InstallAssembly(Assembly assembly, InjectionInfo[] injectionInfos);
        void UninstallAssembly(Assembly assembly);
        MethodInfo GetProxyMethod(MethodBase targetMethod);

        bool IsInjected(Assembly assembly);
        bool IsInjected(MethodBase method);
#if UNITY_EDITOR
        void OnDomainReload();
        void OnCompiledAssemblies(bool isEditor, string[] assemblies);
        void OnPreprocessBuild(BuildReport report);
        void OnPostprocessBuild(BuildReport report);
#endif
    }

    /// <summary>
    /// 可以用于编辑器模式下的实现
    /// </summary>
    public interface IEditorInjectionImplement : IInjectionImplement
    {
    }

    /// <summary>
    /// 可以同于最终构建产物中的实现
    /// </summary>
    public interface IRuntimeInjectionImplement : IInjectionImplement
    {

    }

    public enum ImplementUsage
    {
        None = 0,
        Editor = 1,
        Runtime = 2,
        Both = -1,
    }

    internal abstract class BaseInjectionImplement : IInjectionImplement
    {
        /*
            Editor:
                * compile start
                * compile assembly1 finished
                * compile assembly2 finished
                * ...
                * compile assemblyN finished
                * compile finished
                * before assembly reload (OnCompiledAssemblies)
                * InitialOnLoadMethod (OnDomainReload)
                * after assembly reload
            Build:
                // Editor Compile
                * compile start
                * compile assembly1 finished
                * compile assembly2 finished
                * ...
                * compile assemblyN finished
                * compile finished
                // Runtime Compile
                * compile start
                * compile assembly1 finished
                * compile assembly2 finished
                * ...
                * compile assemblyN finished
                * compile finished
                * Post Build Player Scripts
                * write rsp files
                * run bee
        */

        public ImplementUsage implementUsage { get; set; }
        public bool IsUsedAsEditorImplement => (implementUsage & ImplementUsage.Editor) != 0;
        public bool IsUsedAsRuntimeImplement => (implementUsage & ImplementUsage.Runtime) != 0;
        public abstract void InstallAssembly(Assembly assembly, InjectionInfo[] injectionInfos);
        public abstract void UninstallAssembly(Assembly assembly);
        public abstract MethodInfo GetProxyMethod(MethodBase targetMethod);
        public abstract bool IsInjected(Assembly assembly);
        public abstract bool IsInjected(MethodBase method);
#if UNITY_EDITOR
        public virtual void OnDomainReload() { }
        // public virtual void OnCompileAssembliesStart(bool isEditor) { }
        // public virtual void OnCompileAssembly(string assemblyFullPath) { }
        public virtual void OnCompiledAssemblies(bool isEditor, string[] assemblies) { }
        public virtual void OnPreprocessBuild(BuildReport report) { }
        public virtual void OnPostprocessBuild(BuildReport report) { }
#endif
    }
}

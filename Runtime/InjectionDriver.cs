
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using System.Reflection.Emit;
using System.IO;
using com.bbbirder;




#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
#endif

namespace BBBirder.UnityInjection
{
    public partial class InjectionDriver
    {

#if UNITY_EDITOR
        internal IEditorInjectionImplement m_EditorImplement;
        internal IEditorInjectionImplement EditorImplement
        {
            get => m_EditorImplement;
            set
            {
                m_EditorImplement = value;
                m_EditorImplement.implementUsage |= ImplementUsage.Editor;
            }
        }
#endif
        internal IRuntimeInjectionImplement m_RuntimeImplement;
        internal IRuntimeInjectionImplement RuntimeImplement
        {
            get => m_RuntimeImplement;
            set
            {
                m_RuntimeImplement = value;
                m_RuntimeImplement.implementUsage |= ImplementUsage.Runtime;
            }
        }

        internal IInjectionImplement CurrentImplement
#if UNITY_EDITOR
            => EditorImplement;
#else
            => RuntimeImplement;
#endif


        public InjectionDriver()
        {
#if !UNITY_EDITOR
            RuntimeImplement = new WeavingFixImplement();
#endif
        }
        public void AutoInstallOnInitialize()
        {
            var autoInjectAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => asm.GetCustomAttribute<SuppressAutoInjectionAttribute>() == null)
                .ToArray()
                ;
            var injectionInfoGroups = GetInjectionInfos(autoInjectAssemblies).GroupBy(info => info.InjectedMethod.DeclaringType.Assembly);

            Logger.Info($"auto install {injectionInfoGroups.Count()} involved assemblies");
            foreach (var grp in injectionInfoGroups)
            {
                var assemly = grp.Key;
                InstallAssembly_Impl(assemly, grp.ToArray());
            }
        }

#if !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RuntimeInit()
        {
            Instance.AutoInstallOnInitialize();
        }
#endif

        public void InstallAssembly(Assembly assembly)
        {
            var injectionInfos = GetInjectionInfos(assembly).ToArray();
            InstallAssembly_Impl(assembly, injectionInfos);
        }

        public void UninstallAssembly(Assembly assembly)
        {
            UninstallAssembly_Impl(assembly);
        }

        public void InstallAllAssemblies()
        {
            var injectionInfoGroups = GetInjectionInfos().GroupBy(info => info.InjectedMethod.DeclaringType.Assembly);
            Logger.Info($"install all {injectionInfoGroups.Count()} involved assemblies");
            foreach (var grp in injectionInfoGroups)
            {
                var assemly = grp.Key;
                InstallAssembly_Impl(assemly, grp.ToArray());
            }
        }

        public void UninstallAllAssemblies()
        {
            var assemblies = GetInjectionInfos().Select(info => info.InjectedMethod.DeclaringType.Assembly).Distinct();
            foreach (var assembly in assemblies)
            {
                UninstallAssembly(assembly);
            }
        }

        private void UninstallAssembly_Impl(Assembly assembly)
        {
            CurrentImplement.UninstallAssembly(assembly);
            registry.Remove(assembly);
        }

        private void InstallAssembly_Impl(Assembly assembly, InjectionInfo[] injectionInfos)
        {
            if (!registry.TryGetValue(assembly, out _))
            {
                registry[assembly] = injectionInfos;
                Logger.Info($"install assembly {assembly} with {injectionInfos.Length} injections");
                CurrentImplement.InstallAssembly(assembly, injectionInfos);
            }
        }

        public bool IsInjected(Assembly assembly) => registry.ContainsKey(assembly);
        public bool IsInjected(MethodInfo method)
        {
            var assembly = method.DeclaringType.Assembly;
            if (!registry.ContainsKey(assembly)) return false;
            var infos = registry[assembly];
            return infos.Any(info => info.InjectedMethod == method);
        }

        public MethodInfo GetProxyMethod(MethodInfo method)
        {
            return CurrentImplement.GetProxyMethod(method);
        }

        /// <summary>
        /// Get all injections in current domain.
        /// </summary>
        /// <param name="assemblies">The assemblies to search in. All loaded assemblies if omitted</param>
        /// <returns></returns>
        public static IEnumerable<InjectionInfo> GetInjectionInfos(params Assembly[] assemblies)
        {
            if (assemblies == null || assemblies.Length == 0)
                assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // Debug.Log("iter " + assemblies.Length);
            var injections = assemblies
                // .Where(a=>a.MayContainsInjection()) 
                .SelectMany(a => Retriever.GetAllAttributes<InjectionAttribute>(a))
                .SelectMany(attr => attr.ProvideInjections())
                ;
            var injections2 = assemblies
                .SelectMany(a => Retriever.GetAllSubtypes<IInjectionProvider>(a))
                .Where(type => !type.IsInterface && !type.IsAbstract)
                .Select(type => System.Activator.CreateInstance(type) as IInjectionProvider)
                .SelectMany(ii => ii.ProvideInjections())
                ;
            return injections.Concat(injections2).ToArray();
        }
        Dictionary<Assembly, InjectionInfo[]> registry = new();
        static InjectionDriver m_Instance;
        public static InjectionDriver Instance => m_Instance ??= new();
    }

#if UNITY_EDITOR

    partial class InjectionDriver : IPreprocessBuildWithReport, IPostprocessBuildWithReport
    {
        public int callbackOrder => -1;

        internal void OnDomainReload()
        {
            Logger.Verbose("On Domain Reload");
            Logger.Verbose($"editor impl: {Instance.EditorImplement}");
            Logger.Verbose($"runtime impl: {Instance.RuntimeImplement}");

            // mono .net framework : net_unity_4_8    : jit
            // mono .net standard 2.1 : net_Standrad_2_0  : jit
            // il2cpp .net framework : net_unity_4_8  : aot
            // il2cpp .net standard 2.1 : net_Standrad_2_0  : aot
            Instance.EditorImplement.OnDomainReload();
            Instance.RuntimeImplement.OnDomainReload();

            bool BuildingForEditor = true;
            HashSet<string> compiledAssemblies = new();
            UnityEditor.Compilation.CompilationPipeline.compilationStarted += (o) =>
            {
                Logger.Verbose("compilation started");
                var settings = GetScriptAssemblySettings();
                if (settings is null)
                {
                    throw new NotSupportedException("cannot get BeeScriptCompilationState, Unity Version" + Application.unityVersion);
                }
                BuildingForEditor = (bool)GetMemberValue(settings, "BuildingForEditor", true);
                compiledAssemblies.Clear();
                // var OutputDirectory = (string)GetMemberValue(settings, "OutputDirectory", true);
            };

            UnityEditor.Compilation.CompilationPipeline.assemblyCompilationFinished += (path, msg) =>
            {
                var fullPath = Path.GetFullPath(path);
                compiledAssemblies.Add(fullPath);
            };

            UnityEditor.Compilation.CompilationPipeline.compilationFinished += (o) =>
            {
                Logger.Verbose("compilation finished");
                Instance.EditorImplement.OnCompiledAssemblies(BuildingForEditor, compiledAssemblies.ToArray());
                Instance.RuntimeImplement.OnCompiledAssemblies(BuildingForEditor, compiledAssemblies.ToArray());
            };

            AssemblyReloadEvents.beforeAssemblyReload += () =>
            {
                // Instance.EditorImplement.OnCompiledAssemblies(BuildingForEditor, compiledAssemblies.ToArray());
                // // if (Instance.EditorImplement != Instance.RuntimeImplement)
                // {
                //     Instance.RuntimeImplement.OnCompiledAssemblies(BuildingForEditor, compiledAssemblies.ToArray());
                // }
                Logger.Verbose("before assembly reload");
            };

            AssemblyReloadEvents.afterAssemblyReload += () =>
            {
                Logger.Verbose("after assembly reload");
            };

            static object GetScriptAssemblySettings()
            {
                var t = t_EditorCompilationInterface ??= GetType("UnityEditor.CoreModule", "EditorCompilationInterface");

                var editorCompilation = t.GetProperty("Instance", bindingFlags).GetValue(null);
                if (editorCompilation is null)
                    throw new($"cannot get editorCompilation,Unity Version: {Application.unityVersion}");

                var state = GetMemberValue(editorCompilation, "activeBeeBuild")
                    ?? GetMemberValue(editorCompilation, "_currentBeeScriptCompilationState");
                if (state is null)
                    throw new($"cannot get compile state from {editorCompilation.GetType()},Unity Version: {Application.unityVersion}");

                return GetMemberValue(state, "Settings", true);
            }
            static Type GetType(string moduleName, string typeName)
            {
                return System.AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => a.FullName.StartsWith(moduleName + ","))
                    .SelectMany(a => a.GetTypes())
                    .Where(t => t.Name.Equals(typeName))
                    .Single();
            }
            static object GetMemberValue(object obj, string name, bool IgnoreCase = false)
            {
                var flags = bindingFlags;
                if (IgnoreCase) flags |= BindingFlags.IgnoreCase;
                var memberInfo = obj.GetType().GetMember(name, flags).FirstOrDefault();

                if (memberInfo is null)
                {
                    return null;
                }
                // {
                //     var fields = obj.GetType().GetFields(flags).Select(f=>"field:"+f.Name);
                //     var props = obj.GetType().GetProperties(flags).Select(f=>"prop:"+f.Name);
                //     Debug.Log(string.Join("\n",fields)+"\n"+string.Join("\n",props));
                //     throw new($"cannot find member {name} in {obj}");
                // }
                if (memberInfo is FieldInfo fi)
                    return fi.GetValue(obj);

                if (memberInfo is PropertyInfo pi)
                    return pi.GetValue(obj);

                if (memberInfo is MethodInfo mi)
                    return mi.Invoke(obj, null);

                return null;
            }

        }
        static Type t_EditorCompilationInterface;
        static BindingFlags bindingFlags = 0
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            ;

        public void OnPreprocessBuild(BuildReport report)
        {
            Logger.Verbose("OnPreprocessBuild" + report.name);
            // Instance.EditorImplement.OnPreprocessBuild(report);
            Instance.RuntimeImplement.OnPreprocessBuild(report);
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            Logger.Verbose("OnPostprocessBuild" + report.name);
            // Instance.EditorImplement.OnPostprocessBuild(report);
            Instance.RuntimeImplement.OnPostprocessBuild(report);
        }
    }
#endif
}


using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using System.IO;
using com.bbbirder;
using System.Runtime.CompilerServices;



#if UNITY_EDITOR
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
#endif

namespace BBBirder.UnityInjection
{
    public partial class InjectionDriver
    {
        bool inited;
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

        public void AutoInstallOnInitialize(bool allowFailures)
        {
            if (inited) return;
            inited = true;
            var autoInjectAssemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(asm => asm.GetCustomAttribute<SuppressAutoInjectionAttribute>() == null)
                .ToArray()
                ;
            var injectionInfoGroups = GetInjectionInfos(autoInjectAssemblies).GroupBy(info => info.InjectedMethod.DeclaringType.Assembly);

            Logger.Info($"auto install {injectionInfoGroups.Count()} involved assemblies");

            AssemblyInstallationException exception = null;
            foreach (var grp in injectionInfoGroups)
            {
                var assemly = grp.Key;
                try
                {
                    InstallAssembly_Impl(assemly, grp.ToArray());
                }
                catch (Exception e)
                {
                    exception ??= new(assemly, e);
                }
            }

#if UNITY_EDITOR
            if (exception is not null && !allowFailures)
            {
#if UNITY_EDITOR
                CleanAndReopenProject(exception);
#endif
            }
#endif
        }

#if !UNITY_EDITOR
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void RuntimeInit()
        {
            Instance.AutoInstallOnInitialize(true);
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

#if UNITY_EDITOR
        internal static void CleanAndReopenProject(AssemblyInstallationException reason)
        {
            EditorApplication.update -= InnerFunc;
            EditorApplication.update += InnerFunc;
            EditorApplication.QueuePlayerLoopUpdate();

            void InnerFunc()
            {
                EditorApplication.update -= InnerFunc;

                var shouldRecompile = EditorUtility.DisplayDialog(
                    "Unity Injection",
                    $"installing methods failed on Assembly {reason?.Assembly?.GetName()?.Name}, would you like to recompile it and restart Unity?",
                    "yes", "no"
                );

                if (shouldRecompile)
                {
                    EditorApplication.quitting += () =>
                    {
                        InjectionDriver.RestoreAllPrecompiledAssemblies();

                        foreach (var file in Directory.GetFiles("Library/ScriptAssemblies"))
                        {
                            try
                            {
                                File.Delete(file);
                            }
                            catch { }
                        }
                    };

                    EditorApplication.OpenProject(Directory.GetCurrentDirectory());

                    // It doesn't build scripts into ScriptAssemblies but Bee
                    // UnityEditor.Compilation.CompilationPipeline.RequestScriptCompilation(UnityEditor.Compilation.RequestScriptCompilationOptions.CleanBuildCache);
                }
            }
        }

#endif

        public void InstallAllAssemblies()
        {
            var injectionInfoGroups = GetInjectionInfos().GroupBy(info => info.InjectedMethod.DeclaringType.Assembly);
            Logger.Info($"install all {injectionInfoGroups.Count()} involved assemblies");

            AssemblyInstallationException exception = null;
            foreach (var grp in injectionInfoGroups)
            {
                var assemly = grp.Key;
                try
                {
                    InstallAssembly_Impl(assemly, grp.ToArray());
                }
                catch (Exception e)
                {
                    exception ??= new(assemly, e);
                }
            }

            if (exception is not null)
            {
                throw exception;
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

        public bool TryGetOriginToken(MethodInfo targetMethod, out int token)
        {
            return CurrentImplement.TryGetOriginToken(targetMethod, out token);
        }

        public static void RestoreAllPrecompiledAssemblies()
        {
            foreach (var info in GetInjectionInfos().GroupBy(i => i.InjectedMethod.Module.Assembly))
            {
                var assemblyPath = info.Key.GetAssemblyPath();
                var inputFullPath = Path.GetFullPath(assemblyPath);
                var isGeneratedAssembly = inputFullPath.StartsWith(Path.GetFullPath("Library"))
                    || inputFullPath.StartsWith(Path.GetFullPath("Temp"))
                    ;

                if (!isGeneratedAssembly)
                {
                    var backupPath = assemblyPath + ".backup";
                    if (File.Exists(backupPath))
                    {
                        File.Copy(backupPath, assemblyPath, true);
                    }
                }
            }
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

            return InjectionInfo.RetrieveInjectionInfosFrom(assemblies);
        }

        Dictionary<Assembly, InjectionInfo[]> registry = new();
        static InjectionDriver m_Instance;
        public static InjectionDriver Instance => m_Instance ??= new();
    }

#if UNITY_EDITOR

    partial class InjectionDriver : IPreprocessBuildWithReport, IPostprocessBuildWithReport, IPostBuildPlayerScriptDLLs
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

            bool BuildingForEditor = true;
            bool hasError = false;
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
                hasError |= msg.Any(m => m.type == UnityEditor.Compilation.CompilerMessageType.Error);
                compiledAssemblies.Add(fullPath);
            };

            UnityEditor.Compilation.CompilationPipeline.compilationFinished += (o) =>
            {
                Logger.Verbose("compilation finished");
                Instance.EditorImplement.OnCompiledAssemblies(BuildingForEditor, hasError, compiledAssemblies.ToArray());
                Instance.RuntimeImplement.OnCompiledAssemblies(BuildingForEditor, hasError, compiledAssemblies.ToArray());
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

            Instance.EditorImplement.OnDomainReload();
            Instance.RuntimeImplement.OnDomainReload();

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
            try
            {
                Logger.Verbose("OnPreprocessBuild" + report.name);
                Instance.RuntimeImplement.OnPreprocessBuild(report);
            }
            catch (Exception e)
            {
#if UNITY_2021_3_OR_NEWER
                throw new BuildFailedException(e);
#else
                throw new("", e);
#endif
            }
        }

        public void OnPostprocessBuild(BuildReport report)
        {
            try
            {
                Logger.Verbose("OnPostprocessBuild" + report.name);
                Instance.RuntimeImplement.OnPostprocessBuild(report);
            }
            catch (Exception e)
            {
#if UNITY_2021_3_OR_NEWER
                throw new BuildFailedException(e);
#else
                throw new("", e);
#endif
            }
        }

        public void OnPostBuildPlayerScriptDLLs(BuildReport report)
        {
            try
            {
                Instance.RuntimeImplement.OnPostBuildPlayerDll(report);
            }
            catch (Exception e)
            {
#if UNITY_2021_3_OR_NEWER
                throw new BuildFailedException(e);
#else
                throw new("", e);
#endif
            }
        }
    }
#endif
}

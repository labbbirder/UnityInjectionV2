using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace BBBirder.UnityInjection.Editor
{
    internal partial class EditorWeaveImplement : WeavingFixImplement, IEditorInjectionImplement
    {
        const string Key_MissingInjectionChecked = "<UnityInjection>MissingInjectionChecked";

        private static bool ss_MissingInjectionChecked
        {
            get => SessionState.GetBool(Key_MissingInjectionChecked, false);
            set
            {
                Logger.Info("ss_MissingInjectionChecked " + value);
                SessionState.SetBool(Key_MissingInjectionChecked, value);
            }
        }

        public void OnCompiledAssemblies_Incremental(bool isEditor, string[] assemblyPaths)
        {
            if (!isEditor) return;

            Logger.Info($"compiled assemblies: {string.Join("\n", assemblyPaths)}");

            var assemblies = assemblyPaths
                .Where(File.Exists)
                .Select(File.ReadAllBytes)
                .Select(Assembly.Load)
                .ToArray()
                ;

            var outwardInjections = assemblies.SelectMany(a => InjectionInfo.RetrieveInjectionInfosFrom(a));
            var inwardInjections = assemblies.SelectMany(a => InjectionInfo.RetrieveInjectionInfosTowards(a));
            var injectionInfos = outwardInjections.Concat(inwardInjections).ToArray();

            SafelyWeaveInjectionInfos_Impl(injectionInfos, GetAllowedAssemblies().Concat(assemblyPaths).Distinct().ToArray());
        }

        public void OnDomainReload_Incremental()
        {
            var hasCompilationError = EditorUtility.scriptCompilationFailed;
            if (hasCompilationError)
            {
                Logger.Info("Ingore injections because of compilation error.");
                return;
            }

            if (ss_MissingInjectionChecked)
            {
                // missing injections were weaved during previous domain reload
                ss_MissingInjectionChecked = false;
                InjectionDriver.Instance.AutoInstallOnInitialize(true);
                return;
            }

            ss_MissingInjectionChecked = true;
            var allInjectionInfos = InjectionInfo.RetriveAllInjectionInfos().ToHashSet();
            var missingInjectionInfos = allInjectionInfos
                // .Where(info => !prevInjectionRecords.Contains(WeavingRecord.FromInjectionInfo(info)))
                .Where(info => !IsInjected(info.InjectedMethod))
                .ToArray()
                ;

            Logger.Info($"missingInjectionInfos {missingInjectionInfos.Length}");

            if (missingInjectionInfos.Length > 0)
            {
                EditorApplication.update -= RequestScriptReload;
                EditorApplication.update += RequestScriptReload;
                EditorApplication.QueuePlayerLoopUpdate();

                SafelyWeaveInjectionInfos(missingInjectionInfos);
            }
            else
            {
                InjectionDriver.Instance.AutoInstallOnInitialize(false);
                ss_MissingInjectionChecked = false;
            }

            static void RequestScriptReload()
            {
                EditorApplication.update -= RequestScriptReload;

                Logger.Info($"request script reload");
#if UNITY_2019_3_OR_NEWER
                EditorUtility.RequestScriptReload();
#else
                UnityEditorInternal.InternalEditorUtility.RequestScriptReload();
#endif
            }
        }

        static void SafelyWeaveInjectionInfos(InjectionInfo[] injectionInfos)
        {
            SafelyWeaveInjectionInfos_Impl(injectionInfos, GetAllowedAssemblies());
        }

        // public static void SafelyWeaveCompiledAssemblies((string, Assembly)[] compiledAssemblies, string[] allowedAssemblies)
        // {
        //     var assembly2Location = compiledAssemblies.ToDictionary(p => p.Item2, p => p.Item1);
        //     var assemblies = compiledAssemblies.Select(p => p.Item2).ToArray();
        //     var outwardInjections = assemblies.SelectMany(a => InjectionInfo.RetrieveInjectionInfosFrom(a));
        //     var inwardInjections = assemblies.SelectMany(a => InjectionInfo.RetrieveInjectionInfosTowards(a));
        //     var injectionInfos = outwardInjections.Concat(inwardInjections).ToArray();

        //     SafelyWeaveInjectionInfos_Impl(injectionInfos, allowedAssemblies, assembly2Location);
        // }

        static string[] GetAllowedAssemblies()
        {
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => a.Location)
                .Where(File.Exists)
                .Distinct()
                .ToArray();
        }
    }
}

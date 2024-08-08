using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;

namespace BBBirder.UnityInjection.Editor
{
    // [Serializable]
    // struct Arguments
    // {
    //     public string[] allowedAssemblies;
    //     public string[] compiledAssemblies;
    // }
    internal partial class EditorWeaveImplement : WeavingFixImplement, IEditorInjectionImplement
    {
        public void OnCompiledAssemblies_Incremental(bool isEditor, string[] assemblyPathes)
        {
            if (!isEditor) return;

            var assemblies = assemblyPathes
                .Select(File.ReadAllBytes)
                .Select(Assembly.Load)
                // .SelectMany(a => InjectionInfo.RetrieveInjectionInfosFrom(a))
                .ToArray()
                ;

            var outwardInjections = assemblies.SelectMany(a => InjectionInfo.RetrieveInjectionInfosFrom(a));
            var inwardInjections = assemblies.SelectMany(a => InjectionInfo.RetrieveInjectionInfosTowards(a));
            var injectionInfos = outwardInjections.Concat(inwardInjections).ToArray();

            SafelyWeaveInjectionInfos(injectionInfos);
        }

        public void OnDomainReload_Incremental()
        {
            var allInjectionInfos = InjectionInfo.RetriveAllInjectionInfos().ToHashSet();
            var missingInjectionInfos = allInjectionInfos
                // .Where(info => !prevInjectionRecords.Contains(WeavingRecord.FromInjectionInfo(info)))
                .Where(info => !IsInjected(info.InjectedMethod))
                .ToArray()
                ;
            Logger.Info($"missingInjectionInfos {missingInjectionInfos.Length}");

            if (missingInjectionInfos.Length > 0)
            {
                SafelyWeaveInjectionInfos(missingInjectionInfos);
                EditorApplication.delayCall +=
#if UNITY_2019_3_OR_NEWER
                    EditorUtility.RequestScriptReload;
#else
                    UnityEditorInternal.InternalEditorUtility.RequestScriptReload;
#endif
                EditorApplication.QueuePlayerLoopUpdate();
            }
            else
            {
                InjectionDriver.Instance.AutoInstallOnInitialize();
            }

            // EphemeronSettings.instance.weavingRecords = allInjectionInfos
            //     .Select(WeavingRecord.FromInjectionInfo)
            //     .ToArray()
            //     ;
            // EphemeronSettings.instance.Save();
        }

        static void SafelyWeaveInjectionInfos(InjectionInfo[] injectionInfos)
        {
            SafelyWeaveInjectionInfos_Impl(injectionInfos, GetAllowedAssemblies(), null);
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
                // .Select(Path.GetDirectoryName)
                .Distinct()
                .ToArray();
        }

    }
}

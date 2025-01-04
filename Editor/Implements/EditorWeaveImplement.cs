using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor.Build.Reporting;

namespace BBBirder.UnityInjection.Editor
{
    internal partial class EditorWeaveImplement : WeavingFixImplement, IEditorInjectionImplement
    {
        public override void OnDomainReload()
        {
            // if (IsUsedAsRuntimeImplement) OnDomainReload_BuildPlayer();
            if (IsUsedAsEditorImplement) OnDomainReload_Incremental();
        }

        public override void OnCompiledAssemblies(bool isEditor, bool hasError, string[] assemblies)
        {
            // if (IsUsedAsRuntimeImplement) OnCompiledAssemblies_BuildPlayer(isEditor, hasError, assemblies);
            if (IsUsedAsEditorImplement) OnCompiledAssemblies_Incremental(isEditor, assemblies);
        }

        public override void OnPostBuildPlayerDll(BuildReport report)
        {
            // var weavingRecords = EphemeronSettings.instance.weavingRecords;
            var dlls = report.GetFiles().Select(f => f.path).Where(p => p.EndsWith(".dll")).ToArray();
            SafelyWeaveInjectionInfos_Impl(InjectionDriver.GetInjectionInfos().ToArray(), dlls);
        }

        static void SafelyWeaveInjectionInfos_Impl(InjectionInfo[] injectionInfos, string[] allowedAssemblies, Dictionary<Assembly, string> assemblyLocator)
        {
            foreach (var group in injectionInfos.GroupBy(info => info.InjectedMethod.DeclaringType.Assembly))
            {
                var assembly = group.Key;
                var assemblyInjections = group.ToArray();
                // var weavingRecords = group.Select(WeavingRecord.FromInjectionInfo)
                //     .Distinct()
                //     .Where(s => !string.IsNullOrEmpty(s.klassSignature) && !string.IsNullOrEmpty(s.methodSignature))
                //     .ToArray()
                //     ;
                var inputPath = assembly.Location;
                if (string.IsNullOrEmpty(inputPath) && assemblyLocator != null)
                {
                    assemblyLocator.TryGetValue(assembly, out inputPath);
                }
                if (string.IsNullOrEmpty(inputPath))
                {
                    inputPath = allowedAssemblies.FirstOrDefault(asmPath => Path.GetFileNameWithoutExtension(asmPath) == assembly.GetName().Name);
                }
                if (string.IsNullOrEmpty(inputPath))
                {
                    // Logger.Error(assembly);
                    // Logger.Error(assembly.Location);
                    // Logger.Error(string.Join('\n', assemblyLocator.AsEnumerable()));
                    throw new Exception($"Fail to locate assembly {assembly}");
                }
                SafelyWeaveAssembly(inputPath, assemblyInjections, allowedAssemblies);
            }
        }

        static void SafelyWeaveInjectionInfos_Impl(InjectionInfo[] records, string[] allowedAssemblies)
        {
            var assemblyName2Location = allowedAssemblies.ToDictionary(Path.GetFileNameWithoutExtension, s => s);
            foreach (var group in records.GroupBy(info => info.InjectedMethod.DeclaringType.Assembly))
            {
                var assemblyName = group.Key.GetName().Name;
                var weavingRecords = group
                    .Distinct()
                    // .Where(s => !string.IsNullOrEmpty(s.klassSignature) && !string.IsNullOrEmpty(s.methodSignature))
                    .ToArray()
                    ;
                var inputPath = assemblyName2Location.GetValueOrDefault(assemblyName);

                if (string.IsNullOrEmpty(inputPath))
                {
                    Logger.Info($"Fail to locate assembly {assemblyName}, ignore it.");
                    continue;
                }
                SafelyWeaveAssembly(inputPath, weavingRecords, allowedAssemblies);
            }
        }

        static void SafelyWeaveAssembly(string assemblyPath, InjectionInfo[] injectionInfos, string[] allowedAssemblies)
        {
            var inputFullPath = Path.GetFullPath(assemblyPath);
            var isGeneratedAssembly = inputFullPath.StartsWith(Path.GetFullPath("Library"))
                || inputFullPath.StartsWith(Path.GetFullPath("Temp"))
                ;
            // engine dlls should be backed up
            if (!isGeneratedAssembly)
            {
                TryBackup(assemblyPath);
                TryBackup(Path.ChangeExtension(assemblyPath, ".pdb"));
            }

            try
            {
                CecilHelper.InjectAssembly(assemblyPath, injectionInfos, allowedAssemblies, assemblyPath);
            }
            catch
            {
                TryRecover(assemblyPath);
                TryRecover(Path.ChangeExtension(assemblyPath, ".pdb"));
            }

            static bool TryBackup(string inputPath)
            {
                var backPath = inputPath + ".backup";
                if (!File.Exists(inputPath))
                {
                    return false;
                }
                if (!File.Exists(backPath))
                {
                    File.Copy(inputPath, backPath, true);
                }
                return true;
            }
            static bool TryRecover(string inputPath)
            {
                var backPath = inputPath + ".backup";
                if (!File.Exists(backPath))
                {
                    return false;
                }
                File.Copy(backPath, inputPath, true);
                return true;
            }
        }

        static readonly string DOTNET_BINARY_PATH;

        static EditorWeaveImplement()
        {
            var isUnityEnv = AppDomain.CurrentDomain.FriendlyName.Contains("Unity", StringComparison.OrdinalIgnoreCase);
            if (!isUnityEnv) return;
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .Single(a => a.GetName().Name == "UnityEditor.CoreModule");
            var fiPath = assembly.GetType("UnityEditor.Scripting.NetCoreProgram")
                .GetField("DotNetMuxerPath", bindingFlags)
                ;

            DOTNET_BINARY_PATH = fiPath.GetValue(null).ToString();
        }

        static BindingFlags bindingFlags = 0
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            ;
    }
}

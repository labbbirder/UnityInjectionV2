using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BBBirder.UnityInjection.Editor
{
    internal partial class EditorWeaveImplement : WeavingFixImplement, IEditorInjectionImplement
    {
        public override void OnDomainReload()
        {
            if (IsUsedAsRuntimeImplement) OnDomainReload_BuildPlayer();
            if (IsUsedAsEditorImplement) OnDomainReload_Incremental();
        }

        public override void OnCompiledAssemblies(bool isEditor, string[] assemblies)
        {
            if (IsUsedAsRuntimeImplement) OnCompiledAssemblies_BuildPlayer(isEditor, assemblies);
            if (IsUsedAsEditorImplement) OnCompiledAssemblies_Incremental(isEditor, assemblies);
        }

        static void SafelyWeaveInjectionInfos_Impl(InjectionInfo[] injectionInfos, string[] allowedAssemblies, Dictionary<Assembly, string> assemblyLocator)
        {
            foreach (var group in injectionInfos.GroupBy(info => info.InjectedMethod.DeclaringType.Assembly))
            {
                var assembly = group.Key;
                var weavingRecords = group.Select(WeavingRecord.FromInjectionInfo)
                    .Distinct()
                    .Where(s => !string.IsNullOrEmpty(s.klassSignature) && !string.IsNullOrEmpty(s.methodSignature))
                    .ToArray()
                    ;
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
                SafelyWeaveAssembly(inputPath, weavingRecords, allowedAssemblies);
            }
        }

        static void SafelyWeaveInjectionInfos_Impl(WeavingRecord[] records, string[] allowedAssemblies)
        {
            var assemblyName2Location = allowedAssemblies.ToDictionary(Path.GetFileNameWithoutExtension, s => s);
            foreach (var group in records.GroupBy(info => info.assemblyName))
            {
                var assemblyName = group.Key;
                var weavingRecords = group
                    .Distinct()
                    .Where(s => !string.IsNullOrEmpty(s.klassSignature) && !string.IsNullOrEmpty(s.methodSignature))
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

        static void SafelyWeaveAssembly(string inputPath, WeavingRecord[] weavingRecords, string[] allowedAssemblies)
        {
            var inputFullPath = Path.GetFullPath(inputPath);
            var isGeneratedAssembly = inputFullPath.StartsWith(Path.GetFullPath("Library"))
                || inputFullPath.StartsWith(Path.GetFullPath("Temp"))
                ;
            // engine dlls should be backed up
            if (!isGeneratedAssembly)
            {
                TryBackup(inputPath);
                TryBackup(Path.ChangeExtension(inputPath, ".pdb"));
            }
            CecilHelper.InjectAssembly(inputPath, weavingRecords, allowedAssemblies, inputPath);
            static bool TryBackup(string inputPath)
            {
                if (!File.Exists(inputPath))
                {
                    return false;
                }
                var backPath = inputPath + ".backup";
                if (!File.Exists(backPath))
                {
                    File.Copy(inputPath, backPath, true);
                }
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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build.Reporting;

namespace BBBirder.UnityInjection.Editor
{
    internal partial class EditorWeaveImplement : WeavingFixImplement, IEditorInjectionImplement
    {
        static AppDomain assemblyValidatorDomain;
        public override void OnDomainReload()
        {
            if (!UnityInjectionSettings.Instance.enabled) return;
            // if (IsUsedAsRuntimeImplement) OnDomainReload_BuildPlayer();
            if (IsUsedAsEditorImplement)
            {
                OnDomainReload_Incremental();
            }
        }

        public override void OnCompiledAssemblies(bool isEditor, bool hasError, string[] assemblies)
        {
            if (!UnityInjectionSettings.Instance.enabled) return;
            // if (IsUsedAsRuntimeImplement) OnCompiledAssemblies_BuildPlayer(isEditor, hasError, assemblies);
            if (IsUsedAsEditorImplement) OnCompiledAssemblies_Incremental(isEditor, assemblies);
        }

        public override void OnPostBuildPlayerDll(BuildReport report)
        {
            if (!UnityInjectionSettings.Instance.enabled) return;
            var dlls = report.GetFiles().Select(f => f.path).Where(p => p.EndsWith(".dll")).ToArray();
            SafelyWeaveInjectionInfos_Impl(InjectionDriver.GetInjectionInfos().ToArray(), dlls);
        }

        internal static void SafelyWeaveInjectionInfos_Impl(InjectionInfo[] records, string[] allowedAssemblies)
        {
            Exception weaveException = null;
            var assemblyName2Location = allowedAssemblies.ToDictionary(Path.GetFileNameWithoutExtension, s => s);

            UnityInjectionSettings.Instance.errorRecords.Clear();

            var groups = records.GroupBy(info => info.InjectedMethod.DeclaringType.Assembly);
            HashSet<string> visitedAssemblyPaths = new();

            foreach (var group in groups)
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

                // Some Assemblies were loaded from these newly compiled, hence the instance is not equal to those in AppDomain.
                // We check the equality by file path as a resolution.
                if (visitedAssemblyPaths.Contains(inputPath)) continue;
                visitedAssemblyPaths.Add(inputPath);

                try
                {
                    SafelyWeaveAssembly(inputPath, weavingRecords, allowedAssemblies);
                }
                catch (Exception e)
                {
                    weaveException = e;
                    UnityInjectionSettings.Instance.errorRecords.Add(new()
                    {
                        assemblyPath = Path.GetFullPath(inputPath),
                        message = e.ToString(),
                    });
                }
            }

            if (weaveException != null)
            {
                throw new Exception("weave error", weaveException);
            }
        }

        private static void SafelyWeaveAssembly(string assemblyPath, InjectionInfo[] injectionInfos, string[] allowedAssemblies)
        {
            var inputFullPath = Path.GetFullPath(assemblyPath);
            var isGeneratedAssembly = inputFullPath.StartsWith(Path.GetFullPath("Library"))
                || inputFullPath.StartsWith(Path.GetFullPath("Temp"))
                ;
            // engine dlls should be backed up
            if (!isGeneratedAssembly)
            {
                TryRecover(assemblyPath);
                TryBackup(assemblyPath);
                TryBackup(Path.ChangeExtension(assemblyPath, ".pdb"));
            }

            Exception[] exceptions = null;

            try
            {
                exceptions = CecilHelper.InjectAssembly(assemblyPath, injectionInfos, allowedAssemblies, assemblyPath);
            }
            catch
            {
                TryRecoverIfAssemblyBroken(assemblyPath);
                throw;
            }

            if (exceptions.Length > 0)
            {
                TryRecoverIfAssemblyBroken(assemblyPath);
                throw exceptions[0];
            }

            static void TryRecoverIfAssemblyBroken(string assemblyPath)
            {
                assemblyValidatorDomain ??= AppDomain.CreateDomain("assembly-validator");
                try
                {
                    assemblyValidatorDomain.Load(File.ReadAllBytes(assemblyPath));
                    // Assembly.Load(File.ReadAllBytes(assemblyPath));
                }
                catch
                {
                    TryRecover(assemblyPath);
                    TryRecover(Path.ChangeExtension(assemblyPath, ".pdb"));
                    Logger.Warning("recovered broken assembly: " + assemblyPath);
                }
                finally
                {
                    AppDomain.Unload(assemblyValidatorDomain);
                    assemblyValidatorDomain = null;
                }
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

            // 只在高版本Unity有效，兼容低版本，可以改成FileFind形式
            // var type = Type.GetType("UnityEditor.Scripting.NetCoreProgram, UnityEditor.CoreModule");
            // var fiPath = type.GetField("DotNetMuxerPath", bindingFlags);

            // DOTNET_BINARY_PATH = fiPath.GetValue(null).ToString();
        }

        static BindingFlags bindingFlags = 0
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            ;
    }
}

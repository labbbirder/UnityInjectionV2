#define PROXY_LINKER_APPROACH
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Runtime.InteropServices;
using MonoHook;
using Newtonsoft.Json;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditorInternal;
using UnityEngine;

using System.Threading;
using System.IO.Pipes;
using System.Threading.Tasks;
namespace BBBirder.UnityInjection.Editor
{
    using static ToolsConstants;
    // Inject player by collecting assemblies manually
#if MANUAL_RETRIEVE_APPROACH
    /*
        Unity安装了某个平台模块，将在ModuleManager::platformSupportModules记录。
        模块磁盘内容位于Editor\Data\PlaybackEngines。
        其中的文件内容大致为：
            * Build Data   - 平台特定性的构建参数，比如WebGL的emscripten；Android的JDK、NDK等
            * Build Logic  - 通常是基于Bee的构建流程逻辑。Bee实现了基于Dag的增量的流程构建和可视化的构建报告等，因此平台构建流程倾向于基于Bee实现（通过useBee字段判断）
            * Build Window - Unity编辑器打包窗口的UI代码
            * Platform Tools - 平台相关的构建、优化等工具
            * BuildPostprocessor派生类 - 与Unity编辑器对接，响应编辑器发起的构建请求（ModuleManager::platformSupportModules）
            * Variations   - 不同选项（通常为宏定义）下编译的程序集
    */
    internal partial class EditorWeaveImplement : WeavingFixImplement, IRuntimeInjectionImplement
    {

        internal static string[] GetCompatibleProfileAssemblies(NamedBuildTarget buildTarget)
        {
            var scriptingBackend = PlayerSettings.GetScriptingBackend(buildTarget);
            var compatible = scriptingBackend switch
            {
                ScriptingImplementation.Mono2x => ApiCompatibilityLevel.NET_Unity_4_8,
                ScriptingImplementation.IL2CPP => ApiCompatibilityLevel.NET_Standard,
                _ => throw new NotSupportedException($"unknown scripting backend: {scriptingBackend}, unity version: {Application.unityVersion}"),
            };

            var miGetProfileFolderName = typeof(BuildPipeline).GetMethod("CompatibilityProfileToClassLibFolder", BindingFlags.Static | BindingFlags.NonPublic);
            var profileFolderName = miGetProfileFolderName.Invoke(null, new object[] { compatible }) as string;

            var monoFinderType = Type.GetType("UnityEditor.Utils.MonoInstallationFinder,UnityEditor");
            var MonoBleedingEdgeInstallation = monoFinderType.GetField("MonoBleedingEdgeInstallation").GetValue(null) as string;
            var miGetProfileDirectory = monoFinderType.GetMethod("GetProfileDirectory", new[] { typeof(string), typeof(string) });
            var profileDir = miGetProfileDirectory.Invoke(null, new[] { profileFolderName, MonoBleedingEdgeInstallation }) as string;

            return Directory.GetFiles(profileDir, "*.dll", SearchOption.AllDirectories);
        }

        internal static string[] GetPlaybackEngineAssemblies(bool isEditor, BuildTarget buildTarget)
        {
            var miGetUnityAssemblies = typeof(InternalEditorUtility).GetMethod("GetUnityAssembliesInternal", bindingFlags);
            var assemblies = (Array)miGetUnityAssemblies.Invoke(null, new object[] { isEditor, buildTarget, });
            var fiPath = miGetUnityAssemblies.ReturnType.GetElementType().GetField("Path", bindingFlags);
            return assemblies.OfType<object>()
                .Select(a => fiPath.GetValue(a))
                .OfType<string>()
                .ToArray();
        }

        public void OnDomainReload_BuildPlayer() { }

        BuildTargetGroup buildTargetGroup;
        BuildTarget buildTarget;
        public override void OnPreprocessBuild(BuildReport report)
        {
            buildTargetGroup = report.summary.platformGroup;
            buildTarget = report.summary.platform;
        }

        public void OnCompiledAssemblies_BuildPlayer(bool isEditor, string[] assemblies)
        {
            Logger.Verbose($"build player, is editor mode: {isEditor}");
            if (isEditor) return;
            var namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(buildTargetGroup);
            var allowedAssemblies = assemblies
                .Concat(GetCompatibleProfileAssemblies(namedBuildTarget))
                .Concat(GetPlaybackEngineAssemblies(false, buildTarget))
                .ToArray();
            var weavingRecords = EphemeronSettings.instance.weavingRecords;
            Logger.Verbose($"{weavingRecords.Length} {allowedAssemblies.Length} weaving records");

            SafelyWeaveWeavingRecords_Impl(weavingRecords, allowedAssemblies);
        }

        static void SafelyWeaveWeavingRecords_Impl(WeavingRecord[] records, string[] allowedAssemblies)
        {
            var assemblyName2Location = allowedAssemblies.Distinct().ToDictionary(Path.GetFileNameWithoutExtension, s => s);
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
                    Logger.Warning("no input "+inputPath);
                    continue;
                    //throw new Exception($"Fail to locate assembly {assemblyName}");
                }
                SafelyWeaveAssembly(inputPath, weavingRecords, allowedAssemblies);
            }
        }
    }
#endif

    // Inject player by hijacking UnityLinker
#if PROXY_LINKER_APPROACH
    /* # Sketch of Unity.SourceGenerators Binary Structure

        FilePathsData:
            [TypeCount(INT32BE) | #FILE_PATH(INT32BE) | FILE_PATH(UTF8)]
        TypesData: (TYPE_STRIRNG=NAME_SPACE|CLASS_NAME)
            [#TYPE_STRIRNG(INT32BE) | TYPE_STRING]
    */
    internal partial class EditorWeaveImplement : WeavingFixImplement, IRuntimeInjectionImplement
    {
        const string BATCH_FILE = TEMP_PATH
#if UNITY_EDITOR_WIN
            + "/MyLinker.bat"
#else
            + "/MyLinker.sh"
#endif
            ;
        static MethodHook methodHook;

        [MethodImpl(MethodImplOptions.NoOptimization)]
        static string RawGetExePath(string name)
        {
            return name + "dummy";
        }

        static string GetExePath(string name)
        {
            if (name.ToLowerInvariant() == "unitylinker")
            {
                return BATCH_FILE;
            }
            return RawGetExePath(name);
        }

        static void WaitForLinkerCompleteAndWeaveAssemblies(CancellationToken token)
        {
            // init pipe
            using var server = new NamedPipeServerStream(LINKER_PIPE_NAME);
            token.Register(() => server.Close());
            server.WaitForConnection();

            // read output path
            var bytes = new List<byte>();
            byte b = 0;
            while ((b = (byte)server.ReadByte()) != 0)
            {
                bytes.Add(b);
            }
            var outputDir = Encoding.UTF8.GetString(bytes.ToArray());

            // do weave
            ProxyLinkerResultCode exitCode = ProxyLinkerResultCode.Success;
            try
            {
                var weavingRecords = EphemeronSettings.instance.weavingRecords;
                SafelyWeaveInjectionInfos_Impl(weavingRecords, Directory.GetFiles(outputDir, "*.dll"));
            }
            catch (Exception e)
            {
                exitCode = ProxyLinkerResultCode.InjectionError;
                Logger.Error(e);
            }
            finally
            {
                server.WriteByte((byte)exitCode);
            }
        }

        public void OnDomainReload_BuildPlayer()
        {
            var il2CPPUtilsType = Type.GetType("UnityEditorInternal.IL2CPPUtils,UnityEditor.CoreModule");
            var miOrigin = il2CPPUtilsType.GetMethod("GetExePath", bindingFlags);
            var miReplace = new Func<string, string>(GetExePath).Method;
            var miProxy = new Func<string, string>(RawGetExePath).Method;
            methodHook = new MethodHook(miOrigin, miReplace, miProxy, "redirect GetExePath");
            if (UnityInjectionSettings.instance.enabled)
            {
                methodHook.Install();
            }
        }

        CancellationTokenSource linkerToken;
        public override void OnPreprocessBuild(BuildReport report)
        {
            if (!IsUsedAsRuntimeImplement) return;

            // bump stripping level to make sure UnityLinker is run
            var namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);
            strippingLevel = PlayerSettings.GetManagedStrippingLevel(namedBuildTarget);
            if (strippingLevel == ManagedStrippingLevel.Disabled)
            {
                Logger.Verbose($"set stripping level from {strippingLevel} to {ManagedStrippingLevel.Low}");
                PlayerSettings.SetManagedStrippingLevel(namedBuildTarget, ManagedStrippingLevel.Low);
            }

            // write batch file for ProxyLinker
            var dotnetPath = DOTNET_BINARY_PATH;
            var linkerPath = RawGetExePath("UnityLinker");
            var proxyDllPath = AssetDatabase.GUIDToAssetPath(PROXY_LINKER_TOOL_GUID);
            var builder = new StringBuilder();
#if UNITY_EDITOR_WIN
            builder.AppendLine($"\"{linkerPath}\" %*");
            builder.AppendLine($"IF %ERRORLEVEL% EQU 0 \"{dotnetPath}\" \"{proxyDllPath}\" %*");
#else
            builder.AppendLine($"\"{linkerPath}\" $*");
            builder.AppendLine($"\"{dotnetPath}\" \"{proxyDllPath}\" $*");
#endif
            var batchDir = Path.GetDirectoryName(BATCH_FILE);
            if (!Directory.Exists(batchDir)) Directory.CreateDirectory(batchDir);
            File.WriteAllText(BATCH_FILE, builder.ToString());
#if !UNITY_EDITOR_WIN
            var ret = chmod(BATCH_FILE, 511);
#endif

            // run an isolate guard thread
            if (linkerToken != null)
            {
                linkerToken.Cancel();
                linkerToken = null;
            }
            var token = (linkerToken = new()).Token;
            proxyTask = Task.Run(() => WaitForLinkerCompleteAndWeaveAssemblies(token));
        }
        Task proxyTask;
        public override void OnPostprocessBuild(BuildReport report)
        {
            if (!IsUsedAsRuntimeImplement) return;
            if (linkerToken != null)
            {
                linkerToken.Cancel();
                linkerToken = null;
            }
            if (proxyTask != null)
            {
                proxyTask.Dispose();
                proxyTask = null;
                Debug.Log("dispose task");
            }
            // restore stripping level
            var namedBuildTarget = NamedBuildTarget.FromBuildTargetGroup(report.summary.platformGroup);
            var prev = PlayerSettings.GetManagedStrippingLevel(namedBuildTarget);
            Logger.Verbose($"set stripping level from {prev} to {strippingLevel}");
            PlayerSettings.SetManagedStrippingLevel(namedBuildTarget, strippingLevel);
        }

        // // Invoked by ProxyLinker
        // public static void WeaveAllAssembliesForPlayer(string[] allowedAssemblies)
        // {
        //     var weavingRecords = EphemeronSettings.instance.weavingRecords;
        //     SafelyWeaveInjectionInfos_Impl(weavingRecords, allowedAssemblies);
        // }

        public void OnCompiledAssemblies_BuildPlayer(bool isEditor, string[] assemblies) { }

        static ManagedStrippingLevel strippingLevel;

        #region external stubs
        [DllImport("libc")]
        extern static int chmod(string path, int mode);
        #endregion
    }
#endif
}

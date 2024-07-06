using System;

namespace BBBirder.UnityInjection
{
    internal static class ToolsConstants
    {
        public const string TEMP_PATH = "Library/com.bbbirder.unity-injection",
                            LINKER_PIPE_NAME = "bbbirder.injection.pipe",
                            PROXY_LINKER_TOOL_GUID = "749ea6ed851bdf6479cd08f1e388fc5c";
    }
    public enum ProxyLinkerResultCode : byte
    {
        Success = 0,
        ConnectionTimeout = 101,
        InjectionError = 102,
    }
}

using System;
using System.Runtime.InteropServices;

namespace BBBirder.UnityInjection.Editor
{
    internal static class PlatformApi
    {
#if UNITY_EDITOR_WIN
        internal static int chmod(string path, int flags) => 0;

        public enum Protection
        {
            PAGE_NOACCESS = 0x01,
            PAGE_READONLY = 0x02,
            PAGE_READWRITE = 0x04,
            PAGE_WRITECOPY = 0x08,
            PAGE_EXECUTE = 0x10,
            PAGE_EXECUTE_READ = 0x20,
            PAGE_EXECUTE_READWRITE = 0x40,
            PAGE_EXECUTE_WRITECOPY = 0x80,
            PAGE_GUARD = 0x100,
            PAGE_NOCACHE = 0x200,
            PAGE_WRITECOMBINE = 0x400
        }

        [DllImport("kernel32")]
        public static extern bool VirtualProtect(IntPtr lpAddress, uint dwSize, Protection flNewProtect, out uint lpflOldProtect);

#else
        [DllImport("libc", SetLastError = true)]
        internal static extern int chmod(string path, int flags);
        [Flags]
        public enum MmapProts : int
        {
            PROT_READ = 0x1,
            PROT_WRITE = 0x2,
            PROT_EXEC = 0x4,
            PROT_NONE = 0x0,
            PROT_GROWSDOWN = 0x01000000,
            PROT_GROWSUP = 0x02000000,
        }

        [DllImport("libc", SetLastError = true, CallingConvention = CallingConvention.Cdecl)]
        private static extern int mprotect(IntPtr start, IntPtr len, MmapProts prot);
#endif
    }
}


using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace BBBirder.UnityInjection.Editor
{
    internal unsafe static class MonoApi
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MonoMethod
        {
            public short flags;
            public short iflags;
            public int token;
            public IntPtr klass;
            public IntPtr signature;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct MonoJitInfo
        {
            public MonoMethod* method;
            public IntPtr next;
            public nint code_start;
            public int unwind_info;
            public int code_size;
        }

        [DllImport("mono-2.0-bdwgc", EntryPoint = "mono_jit_info_table_find", CharSet = CharSet.Unicode)]
        extern static MonoJitInfo* FindJitInfo(IntPtr ptrDomain, IntPtr ptrFunc);
        [DllImport("mono-2.0-bdwgc", EntryPoint = "mono_domain_get", CharSet = CharSet.Unicode)]
        extern static IntPtr GetDomain();

        static MonoJitInfo* GetJitInfo(MethodInfo mi)
        {
            RuntimeHelpers.PrepareMethod(mi.MethodHandle);
            var monoDomain = GetDomain();
            var funcPtr = mi.MethodHandle.GetFunctionPointer();
            return FindJitInfo(monoDomain, funcPtr);
        }

        internal static void SetMethodCodeStart(MethodInfo mi, nint addr)
        {
            GetJitInfo(mi)->code_start = addr;
        }
        internal static void SetMethodCodeSize(MethodInfo mi, int size)
        {
            GetJitInfo(mi)->code_size = size;
        }

        static int Test(int a, int b, int c, int d, int e, int f)
        {
            return a + b + c + d + e + f;
        }

        static byte[] GetJitCodes(IntPtr addr, int size)
        {
            var buffer = new byte[size];
            Marshal.Copy(addr, buffer, 0, size);
            Logger.Info(string.Join(",", buffer.Select(b => $"{b:x2}")));
            return buffer;
        }
    }
}

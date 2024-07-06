using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace BBBirder.UnityInjection
{
    [Description]
    public static class WeavingUtils
    {
        static Dictionary<string, string> s_md5cache = new();
        public const string INJECTED_DESCRIPTION_SUFFIX = "(injected by com.bbbirder.Injection)";
        public static string GetInjectedFieldName(int methodToken) => $"__Injection_DelegateField_{methodToken & 0xFFFFFF:x2}";
        public static string GetOriginMethodName(int methodToken) => $"__Injection_OriginMethod_{methodToken & 0xFFFFFF:x2}";
        public static string GetDelegateName(int methodToken) => $"__Injection_Delegate_{methodToken & 0xFFFFFF:x2}";

        public static string GetInjectedFieldName(string methodName, string methodSignature)
            => strBuilder.Clear().Append("__Injection_DelegateField_").Append(methodName).Append(MD5Hash(methodSignature)).ToString();

        public static string GetOriginMethodName(string methodName, string methodSignature)
            => strBuilder.Clear().Append("__Injection_OriginMethod_").Append(methodName).Append(MD5Hash(methodSignature)).ToString();

        public static string GetDelegateName(string methodName, string methodSignature)
            => strBuilder.Clear().Append("__Injection_Delegate_").Append(methodName).Append(MD5Hash(methodSignature)).ToString();

        static string MD5Hash(string rawContent)
        {
            if (!s_md5cache.TryGetValue(rawContent, out var result))
            {
                var md5 = MD5.Create();
                var buffer = md5.ComputeHash(Encoding.UTF8.GetBytes(rawContent));
                s_md5cache[rawContent] = result = string.Concat(buffer.Select(b => b.ToString("X")));
            }
            return result;
        }

        static StringBuilder strBuilder = new();
    }
}

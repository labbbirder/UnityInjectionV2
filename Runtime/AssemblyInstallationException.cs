using System;
using System.Reflection;

namespace BBBirder.UnityInjection
{
    internal class AssemblyInstallationException : Exception
    {
        public readonly Assembly Assembly;

        public AssemblyInstallationException(Assembly assembly, Exception inner) : base("install fail", inner)
        {
            Assembly = assembly;
        }
    }
}

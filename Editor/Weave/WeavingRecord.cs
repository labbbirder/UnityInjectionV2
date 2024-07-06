using System;
using System.Linq;
using System.Reflection;
using Mono.Cecil;

#if UNITY_EDITOR
using UnityEngine;
#endif
namespace BBBirder.UnityInjection.Editor
{
    [Serializable]
    internal struct WeavingRecord : IEquatable<WeavingRecord>
    {
        public string assemblyName;
        public string klassSignature;
        public string methodSignature;
        public Func<MethodDefinition, bool> customWeaveAction;

#if UNITY_EDITOR
        public static WeavingRecord FromString(string json)
        {
            return JsonUtility.FromJson<WeavingRecord>(json);
        }
        public override string ToString()
        {
            return JsonUtility.ToJson(this);
        }

        public static WeavingRecord FromInjectionInfo(InjectionInfo injectionInfo)
        {
            var targetMethod = injectionInfo.InjectedMethod;
            var targetType = targetMethod.DeclaringType;
            var targetAssembly = targetType.Assembly;
            var defaultWeaveAction = (Func<MethodDefinition, bool>)CecilHelper.DefaultWeaveAction;
            return new WeavingRecord()
            {
                assemblyName = targetAssembly.GetName().Name,
                klassSignature = targetType.GetSignature(),
                methodSignature = targetMethod.GetSignature(),
                customWeaveAction = injectionInfo.customWeaveAction ?? defaultWeaveAction,
            };
        }
#endif

        internal void Deconstruct(out string assemblyName, out string klassSignature, out string methodSignature)
        {
            assemblyName = this.assemblyName;
            klassSignature = this.klassSignature;
            methodSignature = this.methodSignature;
        }

        public override int GetHashCode()
        {
            return assemblyName.GetHashCode()
                ^ klassSignature.GetHashCode()
                ^ methodSignature.GetHashCode()
                ;
        }

        public bool Equals(WeavingRecord other)
        {
            return assemblyName == other.assemblyName
                && klassSignature == other.klassSignature
                && methodSignature == other.methodSignature
                ;
        }
    }
}

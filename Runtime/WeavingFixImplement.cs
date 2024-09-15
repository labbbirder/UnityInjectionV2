using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace BBBirder.UnityInjection
{
    internal class WeavingFixImplement : BaseInjectionImplement, IRuntimeInjectionImplement
    {
        Dictionary<Assembly, InjectionInfo[]> registry = new();

        public override MethodInfo GetProxyMethod(MethodBase targetMethod)
        {
            var found = TryGetOriginToken(targetMethod, out var token);
            // var methodName = targetMethod.Name;
            var targetType = targetMethod.DeclaringType;
            // var targetMethodSignature = targetMethod.GetSignature();
            var proxyMethodName = WeavingUtils.GetOriginMethodName(token);
            var proxyMethod = targetType.GetMethod(proxyMethodName, bindingFlags);
            return proxyMethod;
        }

        public override void InstallAssembly(Assembly assembly, InjectionInfo[] injectionInfos)
        {
            registry[assembly] = injectionInfos;
            foreach (var info in injectionInfos)
            {
                try
                {
                    ; (info.customInstallAction ?? FixMethod)(info);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }
        }

        public override void UninstallAssembly(Assembly assembly)
        {
            if (!registry.TryGetValue(assembly, out var injections))
            {
                return;
            }
            foreach (var group in injections.GroupBy(inj => inj.InjectedMethod).Distinct())
            {
                var targetMethod = group.Key;
                var targetType = targetMethod.DeclaringType;
                var methodName = targetMethod.Name;
                // set static field value
                FieldInfo sfld = GetStaticField(targetMethod);
                sfld.SetValue(null, null);
            }
        }

        void FixMethod(InjectionInfo injection)
        {

            // injection.onStartFix?.Invoke();
            var targetMethod = injection.InjectedMethod;
            var methodName = targetMethod.Name;
            // set static field value
            var staticField = GetStaticField(targetMethod);
            var proxyMethod = GetProxyMethod(targetMethod);
            Delegate proxyDelegate;
            try
            {
                proxyDelegate = proxyMethod.CreateDelegate(staticField.FieldType);
                if (proxyDelegate is null)
                {
                    throw new($"create original delegate for {targetMethod.DeclaringType.Name}::{methodName} failed");
                }
            }
            catch (Exception e)
            {
                var msg = $"error on create and set delegate for original method {targetMethod.DeclaringType.Name}::{methodName}\n{e.Message}\n{e.StackTrace}";
                Logger.Error(msg);
                throw;
            }

            try
            {
                var fixingDelegate = injection.GetFixingDelegate(proxyDelegate);
                // Debug.Log($"set delegate {staticField.FieldType}- {fixingMethod}");
                // var fixingDelegate = Delegate.CreateDelegate(staticField.FieldType, fixingMethod);
                fixingDelegate = Delegate.CreateDelegate(staticField.FieldType, fixingDelegate.Target, fixingDelegate.Method);
                var combined = Delegate.Combine(fixingDelegate, staticField.GetValue(null) as Delegate);
                staticField.SetValue(null, combined);
            }
            catch (Exception e)
            {
                var msg = $"error on create and set delegate for injection method {targetMethod.DeclaringType.Name}::{methodName}\n{e.Message}\n{e.StackTrace}";
                Logger.Error(msg);
                throw;
            }

        }

        internal static FieldInfo GetStaticField(MethodBase targetMethod, bool throwNoNotFound = true)
        {
            var found = TryGetOriginToken(targetMethod, out var token);

            if (!found && throwNoNotFound)
            {
                throw new($"No injection mark attribute found on method {targetMethod}. The method is not injected.");
            }
            var targetType = targetMethod.DeclaringType;

            var sfldName = WeavingUtils.GetInjectedFieldName(token);
            var sfld = targetType.GetField(sfldName, bindingFlags ^ BindingFlags.Instance);
            if (sfld == null && throwNoNotFound)
            {
                throw new($"injection field {sfldName} not found.");
            }
            return sfld;
        }

        public override bool IsInjected(Assembly assembly)
        {
            var attr = assembly.GetCustomAttribute<AssemblyDescriptionAttribute>();
            if (attr == null) return false;
            return attr.Description.EndsWith(WeavingUtils.INJECTED_DESCRIPTION_SUFFIX);
        }

        public override bool IsInjected(MethodBase method)
        {
            return TryGetOriginToken(method, out _);
        }

        internal static bool TryGetOriginToken(MethodBase method, out int token)
        {
            var attr = method.GetCustomAttributes(false).FirstOrDefault(a => a.GetType().Name == "InjectedMethodAttribute");

            if (attr != null)
            {
                token = (int)attr.GetType().GetField("originalToken", bindingFlags).GetValue(attr);
                return true;
            }
            else
            {
                token = -1;
                return false;
            }
        }

        static BindingFlags bindingFlags = 0
            | BindingFlags.Static
            | BindingFlags.Instance
            | BindingFlags.Public
            | BindingFlags.NonPublic
            ;
    }
}

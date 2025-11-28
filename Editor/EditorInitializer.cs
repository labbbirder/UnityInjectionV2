using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace BBBirder.UnityInjection.Editor
{
    class EditorInitializer //: IInjectionProvider
    {
        [InitializeOnLoadMethod]
        static void EditorModeSetup()
        {
            Logger.loggerLevel = UnityInjectionSettings.Instance.loggerLevel;
            SetupImplements();
            InjectionDriver.Instance.OnDomainReload();
            // try
            // {
            //     InjectionDriver.Instance.AutoInstallOnInitialize();
            // }
            // catch { }
        }

        [RuntimeInitializeOnLoadMethod]
        static void PlayModeSetup()
        {
            SetupImplements();
        }

        static void SetupImplements()
        {
            var editorImplementType = Type.GetType(UnityInjectionSettings.Instance.EditorImplement);
            InjectionDriver.Instance.EditorImplement = Activator.CreateInstance(editorImplementType) as IEditorInjectionImplement;

            var runtimeImplementType = Type.GetType(UnityInjectionSettings.Instance.EditorImplement);
            InjectionDriver.Instance.RuntimeImplement = Activator.CreateInstance(runtimeImplementType) as IRuntimeInjectionImplement;
        }
    }
}

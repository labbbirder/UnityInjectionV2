using System;
using UnityEngine;
using UnityEngine.Scripting;

namespace BBBirder.UnityInjection
{
    [Preserve]
    public class RuntimeInitializer
    {
        [RuntimeInitializeOnLoadMethod]
        public static void Init()
        {
            // I'm a switcher
        }
    }
}

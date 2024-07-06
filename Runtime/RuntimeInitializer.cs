using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Scripting;

namespace BBBirder.UnityInjection
{
    [Preserve]
    internal class RuntimeInitializer
    {
        [RuntimeInitializeOnLoadMethod]
        public static void Init()
        {
            // I'm a switcher
        }
    }
}

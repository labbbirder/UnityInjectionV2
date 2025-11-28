using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BBBirder.DirectAttribute;
using UnityEngine.Assertions;

namespace BBBirder.UnityInjection
{
    public abstract class InjectionAttribute : DirectRetrieveAttribute
    {
        /// <summary>
        /// set this property to populate injections
        /// </summary>
        /// <value></value>
        public abstract IEnumerable<InjectionInfo> ProvideInjections();
    }
}

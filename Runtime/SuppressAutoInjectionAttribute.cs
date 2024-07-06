using System;

namespace BBBirder.UnityInjection
{
    [AttributeUsage(AttributeTargets.Assembly)]
    public class SuppressAutoInjectionAttribute : Attribute { }
}
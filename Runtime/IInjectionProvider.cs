using System.Collections.Generic;
using com.bbbirder;

namespace BBBirder.UnityInjection
{
    [RetrieveSubtype]
    public interface IInjectionProvider
    {
        /// <summary>
        /// set this property to populate injections
        /// </summary>
        /// <value></value>
        public IEnumerable<InjectionInfo> ProvideInjections();
    }
}

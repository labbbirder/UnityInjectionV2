using System.Collections.Generic;
using com.bbbirder;

namespace BBBirder.UnityInjection
{
    public interface IInjectionProvider : IDirectRetrieve
    {
        /// <summary>
        /// set this property to populate injections
        /// </summary>
        /// <value></value>
        public IEnumerable<InjectionInfo> ProvideInjections();
    }
}

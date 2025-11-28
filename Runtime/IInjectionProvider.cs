using System.Collections.Generic;
using BBBirder.DirectAttribute;

namespace BBBirder.UnityInjection
{
    [RetrieveSubtype(preserveSubtypes: true)]
    public interface IInjectionProvider
    {
        /// <summary>
        /// set this property to populate injections
        /// </summary>
        /// <value></value>
        IEnumerable<InjectionInfo> ProvideInjections();
    }
}

using System.Runtime.CompilerServices;
using CSharpTSFInput.Abstractions;

namespace CSharpTSFInput.KnowledgeModule
{
    /// <summary>
    /// The ONE wiring point.
    ///
    /// [ModuleInitializer] runs once when the assembly's module is initialized — before any
    /// ManjuEngine is constructed. It registers the factory so ManjuEngine can create the dictionary
    /// module without the core ever naming a concrete type.
    /// </summary>
    internal static class KnowledgeModuleBootstrap
    {
        // CA2255 (avoid ModuleInitializer in libraries) is intentionally suppressed: the
        // analyzer's recommended alternative is "expose an explicit init method and call it from
        // the app", but that would force the CORE to name and invoke this concrete module —
        // exactly the coupling the modularity invariant forbids. The suppression IS the
        // correct resolution here; the analyzer's fix would break the design.
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Usage", "CA2255",
            Justification = "Module self-registration is load-bearing; an explicit init method would re-couple the core to the concrete module.")]
        [ModuleInitializer]
        internal static void Register()
        {
            DictionaryModuleRegistry.Factory = engine => new KnowledgeModule(engine);
        }
    }
}

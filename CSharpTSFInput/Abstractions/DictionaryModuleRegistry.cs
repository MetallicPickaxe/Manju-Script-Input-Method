using System;

namespace CSharpTSFInput.Abstractions
{
    /// <summary>
    /// Registration point for the optional dictionary module.
    ///
    /// <see cref="Factory"/> is set ONCE, at module load, by a [ModuleInitializer] inside the
    /// KnowledgeModule/ directory.
    ///
    /// The factory takes the <see cref="ManjuEngine"/> so the module can read configuration
    /// (knowledge_window.*), the font path/data, etc. — without the core knowing any module type.
    ///
    /// This is the ONLY core-side touch point besides <see cref="IDictionaryModule"/>.
    /// </summary>
    public static class DictionaryModuleRegistry
    {
        public static Func<ManjuEngine, IDictionaryModule>? Factory;
    }
}

namespace CSharpTSFInput.Core.Logging
{
    /// <summary>
    /// Minimal engine-agnostic logging interface. Core code logs through this so
    /// it never depends on a concrete file logger; a host DLL built on this Core that wants those
    /// lines supplies its own implementation (e.g. one that wraps its FileLogger). Keeps Core free of
    /// host-specific logging coupling.
    /// </summary>
    public interface ICoreLogger
    {
        void Log(string message);
    }

    /// <summary>A no-op logger so Core consumers can default to "no logging" without null checks.</summary>
    public sealed class NullCoreLogger : ICoreLogger
    {
        public static readonly NullCoreLogger Instance = new NullCoreLogger();
        private NullCoreLogger() { }
        public void Log(string message) { }
    }
}

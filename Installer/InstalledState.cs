using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ManjuInstaller
{
    /// <summary>One copy of Manju IME on this computer.</summary>
    /// <param name="Dll">The DLL.</param>
    /// <param name="Root">The folder the copy lives in: the one holding `Runtime\`, or the DLL's own
    /// folder when the copy has no `Runtime\`.</param>
    /// <param name="LangId">The LANGID in the settings file beside the DLL, or when that names none, the one
    /// its registration carries (see <see cref="InstalledState.Of(string, bool, IReadOnlyList{string})"/>).</param>
    /// <param name="LangSource">Where <paramref name="LangId"/> was read from, or why it is the fallback.</param>
    /// <param name="Registered">True when Windows has this DLL registered as the input method.</param>
    public sealed record Installation(string Dll, string Root, string LangId, string LangSource, bool Registered);

    /// <summary>
    /// WHETHER MANJU IME IS INSTALLED, AND WHERE.
    ///
    /// The registration Windows keeps is the answer: the COM server's `InProcServer32` value names
    /// the DLL Windows loads. The language comes from the settings file beside that DLL, the one setup
    /// wrote when it installed that copy, and from the registration when that file cannot say.
    /// </summary>
    public static class InstalledState
    {
        /// <summary>The DLL registered as this input method's COM server, or null when none is.</summary>
        public static string? RegisteredDll()
        {
            using RegistryKey? key = Registry.ClassesRoot.OpenSubKey($@"CLSID\{Ids.Clsid}\InProcServer32");
            return key?.GetValue(null) is string s && s.Trim().Length > 0 ? s.Trim() : null;
        }

        /// <summary>The registered copy, or null when none is registered.</summary>
        public static Installation? Read()
        {
            string? dll = RegisteredDll();
            return dll is null ? null : Of(dll, registered: true);
        }

        /// <summary>The languages this input method has a TSF profile under, as four hex digits,
        /// read from its registration: the subkeys of `CTF\TIP\{CLSID}\LanguageProfile` (`0x00000c09` is
        /// 0C09). Empty when there is no registration.</summary>
        public static IReadOnlyList<string> RegisteredLangIds()
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey($@"{Ids.TipKey}\LanguageProfile");
            if (key is null) return Array.Empty<string>();
            return key.GetSubKeyNames()
                      .Where(n => Regex.IsMatch(n, "^0x[0-9A-Fa-f]{8}$"))
                      .Select(n => n[^4..].ToUpperInvariant())
                      .ToList();
        }

        /// <summary>The copy whose DLL is <paramref name="dll"/>.</summary>
        public static Installation Of(string dll, bool registered) =>
            Of(dll, registered, registered ? RegisteredLangIds() : Array.Empty<string>());

        /// <summary>
        /// The copy whose DLL is <paramref name="dll"/>, with the languages its registration carries.
        ///
        /// The settings file beside the DLL says which language setup gave this copy. When it cannot
        /// (the folder was deleted after the install, or the file names no language), the registration
        /// itself still does, and that comes before the built-in fallback: an uninstall that fell back to
        /// 0804 would leave the language-list entry of a copy registered under another language, visible
        /// in Settings with its files gone.
        /// </summary>
        public static Installation Of(string dll, bool registered, IReadOnlyList<string> registeredLangIds)
        {
            string full = Path.GetFullPath(dll);
            string dir = Path.GetDirectoryName(full) ?? full;
            string root = string.Equals(Path.GetFileName(dir), "Runtime", StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(dir) ?? dir
                : dir;
            string langId = Ids.ResolveLangIdFrom(InstallPlan.SettingsFileOf(full), out string source);
            if (source.StartsWith("FALLBACK", StringComparison.Ordinal) && registeredLangIds.Count > 0)
            {
                langId = registeredLangIds[0];
                source = $"the registration, CTF\\TIP LanguageProfile {langId} ({source[("FALLBACK - ".Length)..]})";
            }
            return new Installation(full, root, langId.ToUpperInvariant(), source, registered);
        }

        /// <summary>
        /// What an uninstall removes: the registered copy, by the language in the settings file beside
        /// its DLL. When nothing is registered, the copy this program belongs to, so the uninstall
        /// still clears whatever settings a removed copy left behind.
        /// </summary>
        public static Installation UninstallTarget(Installation? registered) =>
            registered ?? Of(InstallPlan.ResolveDll(), registered: false);

        /// <summary>The uninstaller that belongs to <paramref name="copy"/>.</summary>
        public static string UninstallerOf(Installation copy) =>
            Path.Combine(copy.Root, "Installer", Ids.UninstallerName);
    }
}

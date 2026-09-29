using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace ManjuInstaller
{
    /// <summary>One language of this user's Windows language list.</summary>
    /// <param name="Tag">The tag the list knows it by, such as `en-US`, `ja` or `mn-Mong`.</param>
    /// <param name="LangId">Its LANGID as four hex digits, the number the input method is registered
    /// under.</param>
    /// <param name="Name">Its name in the Windows display language, as Settings shows it.</param>
    public sealed record InstalledLanguage(string Tag, string LangId, string Name);

    /// <summary>
    /// THE LANGUAGE LIST, THROUGH THE SAME CMDLETS THE SCRIPT USES.
    ///
    /// `Get-/New-/Set-WinUserLanguageList` have no public Win32 equivalent, and reimplementing them
    /// against the undocumented registry layout would be a rewrite. The exe drives the SAME cmdlets
    /// in a PowerShell host instead: the same API, a different host process.
    ///
    /// Reading the list for the language page goes through `Get-WinUserLanguageList` as well, so
    /// the languages setup offers are the entries the add step later finds by the same tag.
    ///
    /// THE CALL IS IN THE PLAN, AND THAT IS THE POINT. <see cref="Chain"/> goes into the plan line, and <see cref="BuildScript"/>
    /// is checked against that same array before anything runs (<see cref="Run"/>): if the script text
    /// ever stops containing one of the cmdlets the plan advertises, the step fails instead of quietly
    /// doing something else. Plan and execution cannot drift apart.
    /// </summary>
    public static partial class LanguageList
    {
        /// <summary>
        /// The tag Windows knows a LANGID by, asked OF WINDOWS.
        ///
        /// `CultureInfo` cannot answer this: both projects build with InvariantGlobalization, so the
        /// managed tables are not there. `LCIDToLocaleName` is the Win32 answer and is always present.
        /// An unknown LANGID THROWS instead of guessing.
        /// </summary>
        public static string TagOf(ushort langId)
        {
            var buffer = new char[85];   // LOCALE_NAME_MAX_LENGTH
            int n = LCIDToLocaleName(langId, buffer, buffer.Length, 0);
            if (n > 1) return new string(buffer, 0, n - 1);   // n counts the terminating NUL
            throw new InvalidOperationException(
                $"Windows knows no locale name for LANGID 0x{langId:X4}. Check install.language_identifier in the "
                + "settings file beside the DLL.");
        }

        /// <summary>What an uninstall plan names the language by: its tag, or the number when Windows
        /// knows no tag for it. The removal itself goes by the language-list entry, so the name is
        /// only a label.</summary>
        public static string LabelOf(string langId)
        {
            try { return TagOf(Convert.ToUInt16(langId, 16)); }
            catch (Exception) { return "LANGID " + langId; }
        }

        /// <summary>The name Windows gives the language of a LANGID in the display language, such as
        /// "Japanese (Japan)", or its tag when Windows has no name for it.</summary>
        public static string DisplayNameOf(string langId)
        {
            string tag = LabelOf(langId);
            var buffer = new char[256];
            int n = GetLocaleInfoEx(tag, LOCALE_SLOCALIZEDDISPLAYNAME, buffer, buffer.Length);
            return n > 1 ? new string(buffer, 0, n - 1) : tag;
        }

        private const uint LOCALE_SLOCALIZEDDISPLAYNAME = 0x00000002;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LCIDToLocaleName(uint locale, [Out] char[] name, int cchName, uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int GetLocaleInfoEx(string localeName, uint type, [Out] char[] data, int cchData);

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();

        /// <summary>The Windows display language of this user, as a LANGID.</summary>
        public static ushort DisplayLanguage() => GetUserDefaultUILanguage();

        // ------------------------------------------------------------------ reading the list

        /// <summary>
        /// Lists the language list, one line per language: tag, LANGID, name, separated by tabs.
        ///
        /// The LANGID is the one the language's own input methods are registered under, the part of
        /// each `InputMethodTips` entry before the colon. That is the number an input method has to
        /// be registered under to appear in that entry, and it is the only place the list states it:
        /// `LocaleNameToLCID` gives a different number for some tags (a neutral one, or none).
        /// Output is UTF-8, so a name in any display language comes back intact.
        ///
        /// The list is assigned before it is walked: the cmdlet writes the whole list as ONE object,
        /// so `foreach` over the call itself would see a single item whose properties are arrays.
        /// </summary>
        public const string ListScript = @"
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$list = Get-WinUserLanguageList
foreach ($lang in $list) {
    $id = @($lang.InputMethodTips | ForEach-Object { ($_ -split ':')[0] })[0]
    ""{0}`t{1}`t{2}"" -f $lang.LanguageTag, $id, $lang.LocalizedName
}
";

        /// <summary>
        /// The languages of this user's Windows language list that an input method can be registered
        /// under, in the list's own order. Throws when the list cannot be read.
        /// </summary>
        public static IReadOnlyList<InstalledLanguage> ReadInstalled()
        {
            var psi = StartInfo(ListScript);
            psi.StandardOutputEncoding = Encoding.UTF8;
            psi.StandardErrorEncoding = Encoding.UTF8;
            using var p = Process.Start(psi)
                          ?? throw new InvalidOperationException("powershell.exe could not be started to read the language list.");
            string output = p.StandardOutput.ReadToEnd();
            string error = p.StandardError.ReadToEnd();
            p.WaitForExit(60_000);
            if (p.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Get-WinUserLanguageList failed with exit code {p.ExitCode}: {error.Trim()}");
            return Parse(output);
        }

        /// <summary>
        /// The lines <see cref="ListScript"/> prints, as languages. A line without a four-digit
        /// LANGID is left out, and so is a transient LANGID (see <see cref="IsTransient"/>).
        /// </summary>
        public static List<InstalledLanguage> Parse(string output)
        {
            var list = new List<InstalledLanguage>();
            foreach (string raw in output.Replace("\r\n", "\n").Split('\n'))
            {
                string[] f = raw.Trim((char)0xFEFF, '\r').Split('\t');
                if (f.Length < 3) continue;
                string tag = f[0].Trim(), id = f[1].Trim().ToUpperInvariant(), name = f[2].Trim();
                if (tag.Length == 0 || id.Length != 4 || !id.All(Uri.IsHexDigit)) continue;
                if (IsTransient(id)) continue;
                list.Add(new InstalledLanguage(tag, id, name.Length > 0 ? name : tag));
            }
            return list;
        }

        /// <summary>
        /// The four LANGIDs Windows assigns per user to languages that have no locale ID of their own
        /// (LOCALE_TRANSIENT_KEYBOARD1 to 4). The language one of them stands for can change when the
        /// list changes, so an input method registered under it could end up under another language, or
        /// under none. They are not offered.
        /// </summary>
        public static bool IsTransient(string langId) =>
            langId.ToUpperInvariant() is "2000" or "2400" or "2800" or "2C00";

        /// <summary>
        /// The language setup preselects: the Windows display language when it is in the list,
        /// otherwise the first language of the list. Null for an empty list.
        /// </summary>
        public static InstalledLanguage? DefaultOf(IReadOnlyList<InstalledLanguage> list, ushort displayLanguage)
        {
            if (list.Count == 0) return null;
            return list.FirstOrDefault(l => Convert.ToUInt16(l.LangId, 16) == displayLanguage) ?? list[0];
        }

        /// <summary>
        /// The language to install under: <paramref name="requested"/> when it is given and in the
        /// list, the default when it is not given. A requested LANGID that is not in the list THROWS:
        /// setup never adds a language to Windows.
        /// </summary>
        public static InstalledLanguage Choose(IReadOnlyList<InstalledLanguage> list, string? requested,
                                               ushort displayLanguage, out string why)
        {
            if (list.Count == 0)
                throw new InvalidOperationException(
                    "The Windows language list of this user has no language an input method can be added to.");
            if (!string.IsNullOrWhiteSpace(requested))
            {
                string id = requested.Trim().ToUpperInvariant();
                var hit = list.FirstOrDefault(l => l.LangId == id)
                          ?? throw new InvalidOperationException(IsTransient(id) ? TransientRefused(id, list) : NotInList(id, list));
                why = "requested";
                return hit;
            }
            var chosen = DefaultOf(list, displayLanguage)!;
            why = Convert.ToUInt16(chosen.LangId, 16) == displayLanguage
                ? $"the Windows display language (0x{displayLanguage:X4})"
                : $"the first language of the list (the Windows display language 0x{displayLanguage:X4} is not in it)";
            return chosen;
        }

        /// <summary>The error for a language that is not in the list.</summary>
        public static string NotInList(string langId, IReadOnlyList<InstalledLanguage> list) =>
            $"LANGID {langId} is not in the Windows language list of this user, and setup does not add languages "
            + "to Windows. Add the language in Windows Settings first, or choose one of: " + Offered(list) + ".";

        /// <summary>The error for a transient LANGID.</summary>
        public static string TransientRefused(string langId, IReadOnlyList<InstalledLanguage> list) =>
            $"LANGID {langId} is a transient LANGID. Windows assigns it per user to a language that has no "
            + "locale ID, and the language it stands for can change when the list changes, so setup does not "
            + "register under it. Choose one of: " + Offered(list) + ".";

        private static string Offered(IReadOnlyList<InstalledLanguage> list) =>
            string.Join(", ", list.Select(l => $"{l.LangId} {l.Tag}"));

        // ------------------------------------------------------------------ changing the list

        /// <summary>The cmdlets this step runs, in order. This array is BOTH what the plan line
        /// advertises and what the built script is verified against — one source, two readers.</summary>
        public static readonly string[] Cmdlets =
        {
            "Get-WinUserLanguageList",
            "New-WinUserLanguageList",
            "Set-WinUserLanguageList",
        };

        /// <summary>What the plan line carries. The PowerShell script emits this same literal.</summary>
        public static string Chain => string.Join(";", Cmdlets);

        /// <summary>
        /// The add path, mirroring `Install_MANJU_IME.ps1` step 4 statement for statement.
        ///
        /// ONLY THE ENTRY WHOSE TAG WAS CHOSEN is touched, and it must already be in the list. When it
        /// is not, the script stops before `Set-WinUserLanguageList` and nothing is changed: setup
        /// never adds a language to Windows, because adding one makes Windows download and install it.
        ///
        /// THE REBUILD IS REQUIRED. `Get-WinUserLanguageList` can hand back each language's
        /// `InputMethodTips` as a FIXED-SIZE wrapper, so `.Add()` throws "Collection was of a fixed
        /// size.". The entry is rebuilt through `New-WinUserLanguageList` for the SAME tag, its tips
        /// are cleared and the existing ones copied across, so the entry keeps exactly the input
        /// methods it had, and only then is it swapped into the list.
        /// </summary>
        private static string AddScript(string tip, string tag) => $@"
$ErrorActionPreference = 'Stop'
$tip = '{tip}'
$tag = '{tag}'
$oldList = Get-WinUserLanguageList
$newList = [System.Collections.Generic.List[Microsoft.InternationalSettings.Commands.WinUserLanguage]]::new()
$found = $false
foreach ($lang in $oldList) {{
    if ($lang.LanguageTag -eq $tag) {{
        $found = $true
        if ($lang.InputMethodTips -contains $tip) {{
            $newList.Add($lang)
        }} else {{
            $rebuilt = (New-WinUserLanguageList -Language $lang.LanguageTag)[0]
            $rebuilt.InputMethodTips.Clear()
            foreach ($existingTip in $lang.InputMethodTips) {{
                $rebuilt.InputMethodTips.Add($existingTip)
            }}
            $rebuilt.InputMethodTips.Add($tip)
            $newList.Add($rebuilt)
        }}
    }} else {{
        $newList.Add($lang)
    }}
}}
if (-not $found) {{
    throw ""$tag is not in the Windows language list of this user. Nothing was changed.""
}}
Set-WinUserLanguageList -LanguageList $newList -Force
";

        /// <summary>
        /// The remove path, mirroring `Uninstall_MANJU_IME.ps1`. That one does NOT rebuild: it removes
        /// in place and only writes back when something actually changed, and so does this.
        ///
        /// `New-WinUserLanguageList` is not used here, so the check in <see cref="Run"/> asks only for
        /// the cmdlets a given direction actually claims. The plan line for a removal says so too.
        /// </summary>
        private static string RemoveScript(string tip) => $@"
$ErrorActionPreference = 'Stop'
$tip = '{tip}'
$langList = Get-WinUserLanguageList
$modified = $false
foreach ($lang in $langList) {{
    if ($lang.InputMethodTips -contains $tip) {{
        $lang.InputMethodTips.Remove($tip) | Out-Null
        $modified = $true
    }}
}}
if ($modified) {{ Set-WinUserLanguageList $langList -Force }}
";

        public static string BuildScript(string tip, bool add, string tag) =>
            add ? AddScript(tip, tag) : RemoveScript(tip);

        /// <summary>
        /// How `powershell.exe` is started for this step, for any script.
        ///
        /// NO WINDOW. `powershell.exe` is a console program, and the installer lets go of a console
        /// that exists only for itself (`Program.ReleaseOwnConsole`). Without `CreateNoWindow` the
        /// child would then open a console window of its own in the middle of the install.
        ///
        /// AN ERROR COMES BACK AS ITS MESSAGE. Started with `-EncodedCommand`, `powershell.exe` writes
        /// its error stream as CLIXML, so the script runs inside a catch that writes the message as
        /// plain text and exits with 1; progress records are switched off.
        /// </summary>
        /// <summary>Windows PowerShell 5.1, by its full path in System32.</summary>
        public static string PowerShellPath =>
            System.IO.Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        public static ProcessStartInfo StartInfo(string script)
        {
            string wrapped = "$ProgressPreference = 'SilentlyContinue'\n"
                             + "try {\n" + script + "\n}\n"
                             + "catch { [Console]::Error.WriteLine($_.Exception.Message); exit 1 }\n";
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapped));
            // The full path in System32: started by its file name alone, powershell.exe would be looked
            // for first in this program's folder and in the current folder, which the elevated restart sets to
            // Installer\, so a file of that name placed there would run with administrator rights.
            return new ProcessStartInfo(PowerShellPath,
                $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
        }

        /// <summary>The cmdlets a direction is allowed to claim — the add path uses all three, the
        /// remove path does not rebuild.</summary>
        public static string[] CmdletsFor(bool add) =>
            add ? Cmdlets : Cmdlets.Where(c => c != "New-WinUserLanguageList").ToArray();

        /// <summary>
        /// Runs it in Windows PowerShell. `powershell.exe`, not `pwsh`: the
        /// `International` module that owns these cmdlets ships with Windows and is not part of a
        /// PowerShell 7 installation, which a given machine may or may not have on PATH anyway.
        ///
        /// A FAILED ADD THROWS, so the step is named in the final sentence: an input method that is
        /// not in the language list cannot be switched to. A failed removal is recorded and the
        /// uninstall continues, as the script's own catch does.
        /// </summary>
        public static void Run(string tip, bool add, List<string> log, string tag)
        {
            string script = BuildScript(tip, add, tag);

            // THE PLAN-TO-EXECUTION TIE. The plan line names which cmdlets run; this refuses to run
            // anything that does not contain them. Without it, "the call is in the plan" would be a
            // claim instead of a fact.
            foreach (string c in CmdletsFor(add))
                if (!script.Contains(c, StringComparison.Ordinal))
                {
                    log.Add($"language list: refused — the script does not contain {c}, which the plan advertises");
                    if (add) throw new InvalidOperationException($"the language-list script does not contain {c}");
                    return;
                }

            var psi = StartInfo(script);
            string direction = add ? "add" : "remove";
            try
            {
                using var p = Process.Start(psi);
                if (p == null) throw new InvalidOperationException("could not start powershell.exe");
                string err = p.StandardError.ReadToEnd();
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(120_000);
                if (p.ExitCode != 0)
                    throw new InvalidOperationException($"exit {p.ExitCode}: {err.Trim()}");
                log.Add($"language list ({direction}): {tip}");
            }
            catch (Exception ex) when (!add)
            {
                log.Add($"language list ({direction}) skipped: {ex.Message}");
            }
        }
    }
}

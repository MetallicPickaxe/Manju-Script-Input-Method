using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ManjuInstaller
{
    /// <summary>
    /// THE INSTALL, AS AN ORDERED LIST OF STEPS.
    ///
    /// WHY A PLAN AND NOT JUST CODE THAT INSTALLS. This exe and `Install_MANJU_IME.ps1` must produce
    /// the SAME ORDERED SEQUENCE of operations. If `--whatif` were a second code path that printed what
    /// the real path "would" do, the two could drift. So the
    /// plan IS the install: `--whatif` prints this list, a real run executes this list, and there is no
    /// third description of what the installer does.
    ///
    /// ORDER IS PART OF THE CONTRACT, not an implementation detail.
    /// </summary>
    public enum Op
    {
        RequireAdmin,
        PreUninstall,

        /// <summary>Copies the delivery's three folders into the folder chosen on the location page.
        /// `Target` is that folder, `Value` the delivery the copy is made from. Present only when the
        /// chosen folder is not the one the delivery is already in.</summary>
        CopyDelivery,

        LocateDll,

        /// <summary>Writes one setting into the settings file beside the DLL. `Target` is the file,
        /// `Value` is `key=value`.</summary>
        WriteSetting,

        CreateDirectory,
        GrantDirectoryAccess,
        CreateRegistryKey,
        SetRegistryValue,
        VerifySignature,
        RegisterServer,
        UnregisterServer,
        AddLanguageTip,
        RemoveLanguageTip,

        /// <summary> The PowerShell call that actually moves the language list.
        ///
        /// `AddLanguageTip` says WHAT is wanted;
        /// this says HOW it is done, which cmdlets and in which order. The exe shells out for this one
        /// step. `Target` is the cmdlet chain, and `LanguageList.Run` refuses to execute a script that
        /// does not contain the cmdlets this line advertises.</summary>
        InvokeLanguageListCmdlets,

        ActivateProfile,
        BroadcastSettingChange,
        EnsureCtfmon,
        DeleteRegistryKey,
        DeleteRegistryValue,

        Done,
    }

    /// <summary>One operation; the format is fixed by <see cref="ToLine"/>.</summary>
    public sealed record Step(Op Kind, string Target, string Value = "")
    {
        /// <summary>The wire format: three fields, pipe-separated.</summary>
        public string ToLine() => $"{Kind}|{Target}|{Value}";

        /// <summary>A step whose failure ends the walk. After a failed copy the steps would act on
        /// files that are not all there; after a failed language setting the DLL would register
        /// under another language than the one the language list gets.</summary>
        public bool EndsTheWalkOnFailure => Kind is Op.CopyDelivery or Op.WriteSetting;

        /// <summary>What the progress page shows while this runs.</summary>
        public string Describe() => Kind switch
        {
            Op.RequireAdmin => "Checking administrator rights",
            Op.PreUninstall => "Removing any previous installation",
            Op.CopyDelivery => $"Copying Manju IME to {Target}",
            Op.LocateDll => "Locating the input method",
            Op.WriteSetting => "Writing the language setting",
            Op.CreateDirectory => $"Creating {Path.GetFileName(Target)}",
            Op.GrantDirectoryAccess => "Granting access rights",
            Op.CreateRegistryKey => "Preparing settings",
            Op.SetRegistryValue => "Writing settings",
            Op.VerifySignature => "Skipping the signature check: Windows does not require one",
            Op.RegisterServer => "Registering with Windows",
            Op.UnregisterServer => "Unregistering from Windows",
            Op.AddLanguageTip => "Adding to the language list",
            Op.RemoveLanguageTip => "Removing from the language list",
            Op.InvokeLanguageListCmdlets => "Updating the language list",
            Op.ActivateProfile => "Activating the input method",
            Op.BroadcastSettingChange => "Refreshing the language bar",
            Op.EnsureCtfmon => "Checking the text services process",
            Op.DeleteRegistryKey => "Clearing settings",
            Op.DeleteRegistryValue => "Clearing settings",
            Op.Done => "Finished",
            _ => Kind.ToString(),
        };
    }

    /// <summary>The identifiers the install is built around. Every one of these is also in the
    /// PowerShell script; they are repeated here because the exe cannot read them out of a .ps1.</summary>
    public static class Ids
    {
        public const string Clsid = "{11A06A2B-EA6D-43D9-870C-ADF907750ACA}";
        public const string Profile = "{1490E7F2-8B6B-41FA-B70A-BBEA2BA7A244}";
        /// <summary>The LANGID an uninstall uses when the settings file names none, in the
        /// hexadecimal form the registry key name and the language-tip string use. The DLL registers
        /// under the same number when the setting is missing.
        /// </summary>
        public const string LangIdFallback = "0804";

        /// <summary>The setting that records which language this copy is registered under. Setup
        /// writes it; uninstall reads it back.</summary>
        public const string LangIdSetting = "install.language_identifier";

        /// <summary>The LANGID recorded in one settings file. It reads and writes no static state, so
        /// one process can ask it about more than one file.</summary>
        /// <param name="source">"settings (…)", or a sentence starting with "FALLBACK" that says why.</param>
        public static string ResolveLangIdFrom(string cfg, out string source)
        {
            try
            {
                if (File.Exists(cfg))
                {
                    foreach (string line in File.ReadAllLines(cfg))
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(
                            line, "^\\s*language_identifier\\s*:\\s*\"?([0-9A-Fa-f]{4})\"?\\s*(?:#.*)?$");
                        if (!m.Success) continue;
                        source = "settings (" + cfg + ")";
                        return m.Groups[1].Value;
                    }
                    source = "FALLBACK - no install.language_identifier in " + cfg;
                }
                else source = "FALLBACK - no settings file at " + cfg;
            }
            catch (Exception ex)
            {
                source = "FALLBACK - reading the setting threw: " + ex.Message;
            }
            return LangIdFallback;
        }

        /// <summary> The name the language bar shows.
        ///
        /// The product is the Manchu input method, so the name says exactly that.
        /// Three places consume this string and all three move together:
        /// this constant, `Globals.Desc_TextService`, and `$Desc` in the PowerShell installer.</summary>
        public const string Description = "Manju IME";

        public const string TipKeyRoot = @"SOFTWARE\Microsoft\CTF\TIP\";
        public const string LogDirectoryValue = "LogDirectory";
        public const string UsersSid = "S-1-5-32-545";
        public const string AppPackagesSid = "S-1-15-2-1";
        public const string DllName = "CSharpTSFInput.dll";
        public const string UninstallerName = "Uninstall Manju IME.exe";
        public const string InstallerName = "Install Manju IME.exe";

        public static string TipKey => TipKeyRoot + Clsid;

        /// <summary>The language-list entry of this input method under one LANGID.</summary>
        public static string TipFor(string langId) => $"{langId}:{Clsid}{Profile}";
    }

    /// <summary>
    /// Builds the ordered plan. EVERY STEP OF THE SCRIPT HAS ONE HERE, in the script's order. No step
    /// is dropped because it looks unnecessary: each one guards against a real failure.
    /// </summary>
    public static class InstallPlan
    {
        /// <summary>Where this program was run from.
        /// By default the DLL is registered WHERE IT SITS, as the script does (`Resolve-FirstExistingPath`):
        /// unzip anywhere and run from there, nothing is copied into Program Files. A folder chosen on
        /// the location page receives a copy of the delivery, and that copy is registered.</summary>
        public static string InstallRoot { get; set; } = AppContext.BaseDirectory.TrimEnd('\\');

        /// <summary>The delivery this program belongs to: the folder that holds `Runtime\`,
        /// `Installer\` and `Script\`. Installing here is installing in place.</summary>
        public static string DeliveryRoot => Path.GetFullPath(Path.Combine(InstallRoot, ".."));


        private static string LogDirOf(string installRoot) =>
            Path.GetFullPath(Path.Combine(installRoot, "..", "Logs"));

        /// <summary>The settings file the DLL at <paramref name="dll"/> reads, the one that carries
        /// `install.language_identifier`.</summary>
        public static string SettingsFileOf(string dll) =>
            Path.Combine(Path.GetDirectoryName(dll) ?? "", "Resource", "Configuration", "configuration.yaml");

        /// <summary>The DLL, by the script's own fallback order.</summary>
        public static string ResolveDll()
        {
            // 1. beside the installer
            string here = Path.Combine(InstallRoot, Ids.DllName);
            if (File.Exists(here)) return here;

            // 2. THE SHARED PAYLOAD, one level up: `<own dir>\..\Runtime`.
            //
            // A delivery is three directories: `Runtime\` (the only payload), `Installer\` (this exe)
            // and `Script\`, so both install routes have to reach ONE `Resource\` tree. Without this
            // candidate the payload has to be duplicated beside each route, and then
            // `configuration.yaml` exists twice: the user edits one, reinstalls by the other route,
            // and the edit is invisible. That is a defect, not untidiness.
            string shared = Path.GetFullPath(Path.Combine(InstallRoot, "..", "Runtime", Ids.DllName));
            if (File.Exists(shared)) return shared;

            return here;   // reported as written; a real run fails loudly on it
        }

        /// <summary>
        /// The install, under <paramref name="language"/>, in place or into <paramref name="location"/>.
        ///
        /// The copy runs before anything is removed, so a copy that fails leaves an installed copy as it was.
        /// </summary>
        /// <param name="location">The folder chosen on the location page; null or the delivery's own
        /// folder installs in place.</param>
        public static List<Step> BuildInstall(InstalledLanguage language, string? location = null)
        {
            string dll = ResolveDll();
            string installRoot = InstallRoot;
            string? copyTo = null;
            if (location != null && !Delivery.SamePath(location, DeliveryRoot))
            {
                string? refusal = Delivery.CheckTarget(DeliveryRoot, dll, location, installed: null);
                if (refusal != null) throw new InvalidOperationException(refusal);
                copyTo = Delivery.Full(location);
                dll = Path.Combine(copyTo, Path.GetRelativePath(DeliveryRoot, dll));
                installRoot = Path.Combine(copyTo, Path.GetRelativePath(DeliveryRoot, InstallRoot));
            }
            string dllDir = Path.GetDirectoryName(dll) ?? installRoot;
            string tip = Ids.TipFor(language.LangId);
            // The `Done` plan line reports WHERE the install landed.
            // The DELIVERY root, not this program's own directory. The delivery is three sibling
            // directories, so "one level up" is the same place from either route, while each
            // program's own directory is not: reporting InstallRoot would make the exe say Installer
            // and the script say Script for the same install.
            string doneTarget = System.IO.Path.GetFullPath(System.IO.Path.Combine(installRoot, ".."));
            var p = new List<Step>
            {
                // --- 1. elevation ---
                new(Op.RequireAdmin, "HKLM"),
            };
            // --- the copy into the chosen folder, before anything is removed ---
            if (copyTo != null) p.Add(new(Op.CopyDelivery, copyTo, DeliveryRoot));
            p.AddRange(new Step[]
            {
                // --- remove any previous installation first ---
                new(Op.PreUninstall, Ids.Clsid),
                // --- locate the DLL ---
                new(Op.LocateDll, dll),
                // --- the chosen language, into the settings file the DLL reads while it registers ---
                new(Op.WriteSetting, SettingsFileOf(dll), $"{Ids.LangIdSetting}={language.LangId}"),
                // --- Step 1: signature ---
                new(Op.VerifySignature, dll),
                // --- Step 2: ACL on the DLL directory, inherited, for UWP/Store apps ---
                new(Op.GrantDirectoryAccess, dllDir, $"{Ids.AppPackagesSid}:ReadAndExecute"),
                // --- Step 3: the COM server ---
                new(Op.RegisterServer, dll),
                // --- Step 4: the language list, under the entry the language page chose ---
                new(Op.AddLanguageTip, language.Tag, tip),
                // the shell-out itself
                new(Op.InvokeLanguageListCmdlets, string.Join(";", LanguageList.CmdletsFor(true)), tip),
                // --- Step 5: activate through TSF, no sign-out ---
                new(Op.ActivateProfile, Ids.Clsid, Ids.Profile),
                new(Op.BroadcastSettingChange, "intl"),
                // the script deliberately does NOT restart a running ctfmon: its own comment says a
                // restart can freeze the desktop. It only starts one if none is running in its session,
                // and so does this.
                new(Op.EnsureCtfmon, "ctfmon.exe"),
                new(Op.Done, doneTarget),
            });
            return p;
        }

        /// <summary>Where the uninstaller of an install into <paramref name="location"/> is: beside
        /// this program, or at the same place inside the copy.</summary>
        public static string UninstallerFor(string? location)
        {
            string root = location == null || Delivery.SamePath(location, DeliveryRoot)
                ? InstallRoot
                : Path.Combine(Delivery.Full(location), Path.GetRelativePath(DeliveryRoot, InstallRoot));
            return Path.Combine(root, Ids.UninstallerName);
        }

        /// <summary>The uninstall of the copy whose DLL is <paramref name="dll"/>, registered under
        /// <paramref name="langId"/>.</summary>
        public static List<Step> BuildUninstall(string dll, string langId)
        {
            string tip = Ids.TipFor(langId);
            return new List<Step>
            {
                new(Op.RequireAdmin, "HKLM"),
                new(Op.RemoveLanguageTip, LanguageList.LabelOf(langId), tip),
                // the removal does NOT rebuild the entry, so it claims one cmdlet fewer
                new(Op.InvokeLanguageListCmdlets, string.Join(";", LanguageList.CmdletsFor(false)), tip),
                new(Op.UnregisterServer, dll),
                // HKCU leftovers matter: a stale per-user Enable=0 makes a fresh install look broken.
                new(Op.DeleteRegistryKey, $"HKCU\\{Ids.TipKey}"),
                new(Op.DeleteRegistryKey, $"HKLM\\{Ids.TipKey}"),
                // The COM registration, as a backstop: regsvr32 /u has the DLL remove it, and a DLL
                // whose folder was deleted cannot. The uninstall script deletes the same key.
                new(Op.DeleteRegistryKey, $"HKLM\\SOFTWARE\\Classes\\CLSID\\{Ids.Clsid}"),
                new(Op.BroadcastSettingChange, "intl"),
                new(Op.Done, ""),
            };
        }

        /// <summary>The uninstall of <paramref name="target"/>.</summary>
        public static List<Step> BuildUninstall(Installation target) => BuildUninstall(target.Dll, target.LangId);

        public static string Render(IEnumerable<Step> steps) =>
            string.Join(Environment.NewLine, steps.Select(s => s.ToLine()));
    }
}

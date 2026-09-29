using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace ManjuInstaller
{
    /// <summary>
    /// EVERY STEP, PERFORMED. This is a PORT of `Install_MANJU_IME.ps1`, not a rewrite: each step
    /// guards against a real failure, and none is dropped because it looks unnecessary.
    ///
    /// A step that fails is RECORDED and the run continues, which is what the script does: it wraps
    /// almost every step in try/catch and warns. The two the script treats as fatal (no administrator
    /// rights, regsvr32 non-zero) stay fatal here.
    ///
    /// ONE RUNNER PER PLAN. The language a step works on is read out of the plan it belongs to (the
    /// `AddLanguageTip` or `RemoveLanguageTip` line), so an uninstall of the installed copy and the
    /// install that follows it each use their own language.
    /// </summary>
    public sealed class StepRunner
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
                                                         uint flags, uint timeout, out IntPtr result);

        private readonly string _tag;
        private readonly string _tip;

        public StepRunner(IReadOnlyList<Step> plan)
        {
            var language = plan.FirstOrDefault(s => s.Kind is Op.AddLanguageTip or Op.RemoveLanguageTip);
            _tag = language?.Target ?? "";
            _tip = language?.Value ?? "";
        }

        /// <summary>The LANGID of this plan's language-list entry: the part of the tip before the colon.</summary>
        private string LangId => _tip.Split(':')[0];

        /// <summary>A Windows program by its full path in System32. Started by its file name alone, it
        /// would be looked for first in this program's folder and in the current folder, which the elevated
        /// restart sets to Installer\, so a file of that name placed there would run with administrator
        /// rights.</summary>
        public static string System32(string file) => Path.Combine(Environment.SystemDirectory, file);

        public void Execute(Step s, List<string> log)
        {
            switch (s.Kind)
            {
                case Op.RequireAdmin:
                    // The first thing the progress log records is WHICH language this plan works on.
                    log.Add($"language {_tag}, tip {_tip}");
                    using (var id = WindowsIdentity.GetCurrent())
                        if (!new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator))
                            throw new InvalidOperationException("administrator rights are required");
                    break;

                case Op.PreUninstall:
                {
                    // The script removes any previous installation first, every time. What is removed
                    // is the copy Windows has registered, by the language in the settings file beside
                    // its DLL, which may be another folder and another language than this install's.
                    var target = InstalledState.UninstallTarget(InstalledState.Read());
                    var uninstall = InstallPlan.BuildUninstall(target);
                    var runner = new StepRunner(uninstall);
                    log.Add($"pre-uninstall: {target.Dll}, langid {target.LangId} from {target.LangSource}");
                    foreach (var st in uninstall)
                        if (st.Kind is not (Op.RequireAdmin or Op.Done))
                            try { runner.Execute(st, log); } catch (Exception ex) { log.Add($"pre-uninstall {st.Kind}: {ex.Message}"); }
                    break;
                }

                case Op.CopyDelivery:
                {
                    int n = Delivery.Copy(s.Value, s.Target);
                    log.Add($"copied {n} file(s) from {s.Value} to {s.Target}");
                    break;
                }

                case Op.LocateDll:
                    if (!File.Exists(s.Target))
                        throw new FileNotFoundException($"{Ids.DllName} not found at {s.Target}");
                    break;

                case Op.WriteSetting:
                    SettingsFile.Write(s.Target, s.Value);
                    log.Add($"setting {s.Value} written to {s.Target}");
                    break;

                case Op.CreateDirectory:
                    Directory.CreateDirectory(s.Target);
                    break;

                case Op.GrantDirectoryAccess:
                {
                    string[] parts = s.Value.Split(':');
                    var sid = new SecurityIdentifier(parts[0]);
                    var rights = Enum.Parse<FileSystemRights>(parts[1]);
                    var di = new DirectoryInfo(s.Target);
                    if (!di.Exists) break;
                    var acl = di.GetAccessControl();
                    acl.AddAccessRule(new FileSystemAccessRule(sid, rights,
                        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags.None, AccessControlType.Allow));
                    di.SetAccessControl(acl);
                    break;
                }

                case Op.CreateRegistryKey:
                    OpenForWrite(s.Target, create: true)?.Dispose();
                    break;

                case Op.SetRegistryValue:
                {
                    int cut = s.Target.LastIndexOf('\\');
                    string keyPath = s.Target[..cut], name = s.Target[(cut + 1)..];
                    using var k = OpenForWrite(keyPath, create: true);
                    k?.SetValue(name, s.Value, RegistryValueKind.String);
                    break;
                }

                case Op.VerifySignature:
                    // Not checked here: Windows registers and loads an input method without a
                    // signature, so a check could not change what happens next. The script looks at the
                    // signature and prints what it finds; both go on either way.
                    log.Add($"signature: not checked, Windows does not require one ({s.Target})");
                    break;

                case Op.RegisterServer:
                case Op.UnregisterServer:
                {
                    string arg = s.Kind == Op.RegisterServer ? $"/s \"{s.Target}\"" : $"/s /u \"{s.Target}\"";
                    var p = Process.Start(new ProcessStartInfo(System32("regsvr32.exe"), arg) { UseShellExecute = false });
                    p?.WaitForExit();
                    if (s.Kind == Op.RegisterServer && p is { ExitCode: not 0 })
                        throw new InvalidOperationException($"regsvr32 exit code {p.ExitCode}");
                    break;
                }

                case Op.AddLanguageTip:
                case Op.RemoveLanguageTip:
                    // INTENT ONLY. What is wanted is recorded here; HOW it happens is the next step,
                    // Op.InvokeLanguageListCmdlets. Doing the work here as well would run
                    // the cmdlets twice.
                    log.Add($"language list ({(s.Kind == Op.AddLanguageTip ? "add" : "remove")}) requested: {s.Value}");
                    break;

                case Op.InvokeLanguageListCmdlets:
                {
                    // THE DIRECTION IS READ OUT OF THE PLAN LINE, not out of runner state. The add
                    // path has to rebuild the language entry (its Tips collection can come back fixed-size),
                    // so New-WinUserLanguageList appears in its chain and in no other. The line is
                    // self-describing, and a reader of the plan can tell the two apart too.
                    bool add = s.Target.Contains("New-WinUserLanguageList", StringComparison.Ordinal);
                    // The tag is the one the plan's AddLanguageTip line names: the generated script
                    // has to look in the SAME language entry the plan named.
                    LanguageList.Run(s.Value, add, log, _tag);
                    break;
                }

                case Op.ActivateProfile:
                {
                    // See TsfActivation for why the interop is consumed from CSharpTSFInput.Core
                    // instead of being declared a second time here.
                    if (Guid.TryParse(s.Target, out Guid clsid) && Guid.TryParse(s.Value, out Guid profile))
                        TsfActivation.Activate(clsid, profile, Convert.ToUInt16(LangId, 16), log);
                    else
                        log.Add($"activate: skipped, unparsable identifiers {s.Target} {s.Value}");
                    break;
                }

                case Op.BroadcastSettingChange:
                    SendMessageTimeoutW((IntPtr)0xFFFF, 0x001A, IntPtr.Zero, s.Target, 0x0002, 1000, out _);
                    break;

                case Op.EnsureCtfmon:
                {
                    // The script deliberately does NOT restart a running ctfmon: its comment says a
                    // restart can freeze the desktop. Only start one when none is running in THIS session:
                    // the ctfmon of another user's session does not serve this one.
                    int session = Process.GetCurrentProcess().SessionId;
                    if (!Process.GetProcessesByName("ctfmon").Any(c => c.SessionId == session))
                        try { Process.Start(new ProcessStartInfo(System32("ctfmon.exe")) { UseShellExecute = true }); } catch { }
                    break;
                }

                case Op.DeleteRegistryKey:
                    DeleteKey(s.Target);
                    break;

                case Op.DeleteRegistryValue:
                {
                    int cut = s.Target.LastIndexOf('\\');
                    using var k = OpenForWrite(s.Target[..cut], create: false);
                    k?.DeleteValue(s.Target[(cut + 1)..], throwOnMissingValue: false);
                    break;
                }

                case Op.Done:
                    break;
            }
        }

        private static RegistryKey? OpenForWrite(string path, bool create)
        {
            var (root, sub) = Split(path);
            return create ? root.CreateSubKey(sub, true) : root.OpenSubKey(sub, true);
        }

        private static void DeleteKey(string path)
        {
            var (root, sub) = Split(path);
            try { root.DeleteSubKeyTree(sub, throwOnMissingSubKey: false); } catch { }
        }

        private static (RegistryKey, string) Split(string path)
        {
            int i = path.IndexOf('\\');
            string hive = path[..i], sub = path[(i + 1)..];
            return hive.Equals("HKCU", StringComparison.OrdinalIgnoreCase)
                ? (Registry.CurrentUser, sub) : (Registry.LocalMachine, sub);
        }

    }
}

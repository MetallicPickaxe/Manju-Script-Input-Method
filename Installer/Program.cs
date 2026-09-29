using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ManjuInstaller
{
    /// <summary>
    /// THE TWO PROGRAMS OF THE DELIVERY: `Install Manju IME.exe` (<see cref="Main"/>) and
    /// `Uninstall Manju IME.exe` (<see cref="RunUninstaller"/>, entered from UninstallerEntry).
    ///
    /// Setup is a wizard: Welcome, Language, Location, Progress, Finish. Nothing is changed before
    /// Install is clicked. When a copy is already registered, the first page says where and under which
    /// language, and offers Reinstall (remove it, then install this one) or Cancel.
    ///
    /// Uninstall is a wizard too: Confirm, Progress, Finish. It removes the copy Windows has
    /// registered, by the language written in the settings file beside that copy's DLL.
    ///
    /// Neither program registers itself in the Windows list of installed apps. The uninstaller sits
    /// beside the installer, and the finish page says where.
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// `CharSet = CharSet.Unicode` IS LOAD-BEARING, not decoration.
        ///
        /// The entry point is the WIDE one and wants UTF-16. Without a CharSet a `string` parameter
        /// marshals as ANSI, so what arrives is ANSI bytes read as UTF-16: the path does not survive the
        /// call and ShellExecute answers SE_ERR_FNF (2) for a file that is sitting right there.
        /// This hits ASCII paths too: without a CharSet the call returns 2 for `C:\Windows` as well as
        /// for a non-ASCII path.
        ///
        /// What it costs to get wrong is the whole product failing to install with nothing on screen:
        /// the call returns 2, `Main` turns that into exit code 1, and the process ends before any
        /// window or UAC prompt exists.
        /// </summary>
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr ShellExecuteW(IntPtr hwnd, string lpOperation, string lpFile,
                                                   string lpParameters, string lpDirectory, int nShowCmd);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

        private const uint MB_ICONERROR = 0x00000010;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList(uint[] processList, uint processCount);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool FreeConsole();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

        private const int STD_INPUT_HANDLE = -10;
        private const int STD_OUTPUT_HANDLE = -11;
        private const int STD_ERROR_HANDLE = -12;

        /// <summary>
        /// BOTH PROGRAMS ARE CONSOLE PROGRAMS so that `--whatif` prints into the shell that ran them. The
        /// cost is that a double-click, and the elevated relaunch after it, each get a console of their
        /// own: an empty terminal window beside the wizard for the whole run.
        ///
        /// A console this process shares with a shell is left alone. One that exists only for this
        /// process is released, and the standard handles are cleared with it. A handle left pointing
        /// at a released console is a number the next opened file or key can reuse, and a later write
        /// through it would land there. Cleared, every console write becomes a no-op, and a child
        /// process started without redirection gets no handle at all.
        /// </summary>
        private static void ReleaseOwnConsole()
        {
            var ids = new uint[2];
            if (GetConsoleProcessList(ids, (uint)ids.Length) != 1) return;
            FreeConsole();
            SetStdHandle(STD_INPUT_HANDLE, IntPtr.Zero);
            SetStdHandle(STD_OUTPUT_HANDLE, IntPtr.Zero);
            SetStdHandle(STD_ERROR_HANDLE, IntPtr.Zero);
        }

        private static bool IsAdmin()
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Refuses a command line with an unknown, incomplete or repeated option, or one
        /// the program cannot act on: it prints what is wrong and the options, on stderr, and the program
        /// ends with exit code 1 having changed nothing. Null when the command line may run.</summary>
        private static int? Refuse(CommandLine cl, string? problem, string program, IReadOnlyList<CommandLine.Option> options)
        {
            string? error = cl.Error ?? problem;
            if (error == null) return null;
            Console.Error.WriteLine(error);
            Console.Error.Write(CommandLine.Usage(program, options));
            Console.Error.Flush();
            return 1;
        }

        [STAThread]
        private static int Main(string[] args) => RunInstaller(args);

        /// <summary>
        /// Setup. `--whatif` (`-w`) prints the plan for the default language, in place, and changes nothing;
        /// `--language-identifier XXXX` (`-l`) and `--location <folder>` (`-p`) print the plan for that language
        /// and that folder instead. Without `--whatif` the wizard opens, after the UAC prompt, and asks for
        /// both; the two options are refused there, since the wizard would not use them.
        /// </summary>
        public static int RunInstaller(string[] args)
        {
            var cl = CommandLine.Parse(args, CommandLine.InstallerOptions);
            string? problem = !cl.Has(CommandLine.WhatIf) && (cl.Has(CommandLine.LanguageIdentifier) || cl.Has(CommandLine.Location))
                ? "--language-identifier and --location work only with --whatif; the wizard asks for the language and the folder."
                : null;
            if (Refuse(cl, problem, Ids.InstallerName, CommandLine.InstallerOptions) is int refused) return refused;
            ApplyRoot(cl);
            if (cl.Has(CommandLine.WhatIf))
            {
                try
                {
                    var languages = LanguageList.ReadInstalled();
                    var language = LanguageList.Choose(languages, cl.ValueOf(CommandLine.LanguageIdentifier),
                                                       LanguageList.DisplayLanguage(), out string why);
                    string? location = cl.ValueOf(CommandLine.Location);
                    if (location != null)
                    {
                        string? refusal = Delivery.CheckTarget(InstallPlan.DeliveryRoot, InstallPlan.ResolveDll(),
                                                               location, InstalledState.Read());
                        if (refusal != null) throw new InvalidOperationException(refusal);
                    }
                    var plan = InstallPlan.BuildInstall(language, location);
                    // Nothing is written, nothing is read that could change. This is the same list a
                    // real run walks; see InstallPlan's own comment for why it is one list and not two.
                    Console.Out.Write(InstallPlan.Render(plan));
                    Console.Out.Flush();
                    // WHICH LANGUAGE, AND WHY, ON STDERR. Not on stdout: stdout is the plan.
                    Console.Error.WriteLine($"[language] {language.LangId} {language.Tag} ({language.Name}): {why}");
                    return 0;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.Message);
                    return 1;
                }
            }

            // Past this point nothing is printed for a shell to read: the window and the message box
            // are the only output.
            ReleaseOwnConsole();
            if (!IsAdmin()) return Elevate(args);

            return WizardWindow.Run(new WizardSetup
            {
                Installed = InstalledState.Read(),
                DisplayLanguage = LanguageList.DisplayLanguage(),
            });
        }

        /// <summary>
        /// Uninstall. `--whatif` (`-w`) prints the plan and changes nothing. Without it the wizard opens,
        /// after the UAC prompt.
        /// </summary>
        public static int RunUninstaller(string[] args)
        {
            var cl = CommandLine.Parse(args, CommandLine.UninstallerOptions);
            if (Refuse(cl, null, Ids.UninstallerName, CommandLine.UninstallerOptions) is int refused) return refused;
            ApplyRoot(cl);
            var target = InstalledState.UninstallTarget(InstalledState.Read());
            if (cl.Has(CommandLine.WhatIf))
            {
                Console.Out.Write(InstallPlan.Render(InstallPlan.BuildUninstall(target)));
                Console.Out.Flush();
                Console.Error.WriteLine(target.Registered
                    ? $"[installed] {target.Dll}, langid {target.LangId} from {target.LangSource}"
                    : $"[installed] nothing is registered; the plan clears what {target.Dll} would leave, langid {target.LangId} from {target.LangSource}");
                return 0;
            }

            ReleaseOwnConsole();
            if (!IsAdmin()) return Elevate(args);

            return WizardWindow.Run(new WizardSetup
            {
                Uninstaller = true,
                Installed = target.Registered ? target : null,
                Target = target,
            });
        }

        /// <summary>`--root` is not a "choose where to install" option: the user never sees it, and
        /// without it the root is simply where the exe sits.</summary>
        private static void ApplyRoot(CommandLine cl)
        {
            string? root = cl.ValueOf(CommandLine.Root);
            if (root != null) InstallPlan.InstallRoot = root.TrimEnd('\\');
        }

        /// <summary>
        /// SELF-ELEVATION. The script tells the user to re-launch as administrator, which is the
        /// most off-putting step it has. Here the exe relaunches itself elevated and the user sees
        /// only the UAC prompt they would see anyway.
        /// </summary>
        private static int Elevate(string[] args)
        {
            string exe = Environment.ProcessPath ?? "";
            string rest = string.Join(' ', args.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));
            IntPtr r = ShellExecuteW(IntPtr.Zero, "runas", exe, rest, InstallPlan.InstallRoot, 1);
            return ReportElevation((long)r, ShowError);
        }

        /// <summary>
        /// A FAILED ELEVATION IS NOT ALLOWED TO BE SILENT.
        ///
        /// ShellExecute answers 32 or less on failure, and the commonest one is the user choosing No on
        /// the Windows prompt. If that answer turned into `return 1` and nothing else, the process would
        /// end with no window, no message and no prompt, which looks exactly like an installer that does
        /// nothing. Correct marshalling alone does not cover it.
        ///
        /// What is reported is decided apart from HOW it is shown: <paramref name="show"/> receives the
        /// finished sentence, and <see cref="ElevationFailure"/> decides it from the ShellExecute result
        /// alone.
        /// </summary>
        /// <returns>The process exit code: 0 when elevation started, 1 when it did not.</returns>
        public static int ReportElevation(long shellExecuteResult, Action<string> show)
        {
            string? failure = ElevationFailure(shellExecuteResult);
            if (failure is null) return 0;
            show(failure);
            return 1;
        }

        /// <summary>What the user is told, or null when elevation did start. The number is kept in the
        /// text: it is the only diagnostic anyone gets, and 2 says the marshalling broke, not that the
        /// user declined the prompt.</summary>
        public static string? ElevationFailure(long shellExecuteResult) =>
            shellExecuteResult > 32
                ? null
                : "This program could not restart itself with administrator rights "
                  + $"(ShellExecute returned {shellExecuteResult}). Nothing has been changed."
                  + "\r\n\r\nIf you chose No on the Windows prompt, start it again and choose Yes. "
                  + "Otherwise right-click it and pick “Run as administrator”.";

        /// <summary>Both channels: the message box is what a double-click shows, and stderr is what a
        /// shell shows. On the double-click route the console has already been released, so the
        /// stderr write goes nowhere and the message box carries the text alone.</summary>
        private static void ShowError(string text)
        {
            Console.Error.WriteLine(text);
            Console.Error.Flush();
            MessageBoxW(IntPtr.Zero, text, "Manju IME", MB_ICONERROR);
        }
    }
}

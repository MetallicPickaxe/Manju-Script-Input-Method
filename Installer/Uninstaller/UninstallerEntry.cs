using System;

namespace ManjuInstaller
{
    /// <summary>
    /// The entry point of `Uninstall Manju IME.exe`. Everything it runs is shared with the installer:
    /// the uninstall plan, the steps and the wizard window.
    /// </summary>
    public static class UninstallerEntry
    {
        [STAThread]
        private static int Main(string[] args) => Program.RunUninstaller(args);
    }
}

namespace CSharpTSFInput
{
    /// <summary>
    /// What this build shows the user. <see cref="SettingsMenu"/> says whether the settings menu is
    /// exposed; the 1.0 release hides it. Hidden, its key is not claimed and goes to the host, the menu
    /// never opens, and the finish page of setup does not name the key. The menu's code is the same
    /// either way. Setup compiles this same file.
    /// </summary>
    internal static class Exposure
    {
        internal const bool SettingsMenu = false;
    }
}

using System;
using System.Collections.Generic;
using System.Linq;

namespace ManjuInstaller
{
    /// <summary>
    /// WALKING THE PLAN, AND THE ONE SENTENCE THE USER IS LEFT WITH.
    ///
    /// A step that throws is recorded and the walk CARRIES ON, which is StepRunner's own contract:
    /// a partly failed install can still leave a usable one. The one exception is the copy into a
    /// chosen folder: every step after it works on the copy, so a copy that failed ends the walk.
    /// What the final sentence then says is decided here, so a failure is never reported as an
    /// install that worked.
    ///
    /// The decision lives outside the window on purpose. The window creates itself and pumps
    /// messages; here the walk is a plain function of the plan and of how each step is performed,
    /// and the window only displays what this returns.
    /// </summary>
    public static class InstallOutcome
    {
        /// <summary>What the walk found. <paramref name="FailedSteps"/> holds the USER-FACING name of
        /// each step that threw, in the order they ran; <paramref name="Message"/> is the finished
        /// sentence for the window. <paramref name="Stopped"/> is true when a failed step ended the
        /// walk before the last step.</summary>
        public sealed record Result(IReadOnlyList<string> FailedSteps, string Message, bool Stopped = false)
        {
            public bool Failed => FailedSteps.Count > 0;
        }

        /// <summary>Runs every step, records the ones that threw, and returns the sentence.</summary>
        /// <param name="execute">How a step is performed. The wizard passes a
        /// <see cref="StepRunner"/> made for this plan.</param>
        /// <param name="onStep">Called before each step, for the progress bar. Not part of the
        /// decision.</param>
        public static Result Walk(IReadOnlyList<Step> plan, bool uninstall,
                                    Action<Step, List<string>> execute, List<string> log,
                                    Action<int, Step>? onStep = null)
        {
            var failed = new List<string>();
            bool stopped = false;
            for (int i = 0; i < plan.Count; i++)
            {
                onStep?.Invoke(i, plan[i]);
                try
                {
                    execute(plan[i], log);
                }
                catch (Exception ex)
                {
                    // The log keeps the internal name, which is what the other log lines use; the
                    // sentence takes the user-facing one. Same event, two readers.
                    log.Add($"{plan[i].Kind}: {ex.Message}");
                    failed.Add(plan[i].Describe());
                    if (plan[i].EndsTheWalkOnFailure && i < plan.Count - 1)
                    {
                        log.Add($"{plan[i].Kind} failed, so the {plan.Count - i - 1} step(s) after it were not run");
                        stopped = true;
                        break;
                    }
                }
            }
            string message = Sentence(failed, uninstall, LogDirOf(plan));
            if (stopped) message += "\r\nThe steps after it were not run, and nothing was registered.";
            return new Result(failed, message, stopped);
        }

        /// <summary>
        /// The directory the plan points the input method's log at, or null when the plan writes no
        /// log setting.
        ///
        /// READ FROM THE PLAN THAT WAS WALKED, so the sentence cannot name a log the install never set
        /// up.
        /// </summary>
        public static string? LogDirOf(IReadOnlyList<Step> plan) =>
            plan.LastOrDefault(s => s.Kind == Op.SetRegistryValue
                                    && s.Target.EndsWith("\\" + Ids.LogDirectoryValue, StringComparison.Ordinal))
                ?.Value;

        /// <summary>The final sentence. The two success forms are reachable only when nothing
        /// threw. The "Log:" line appears only when <paramref name="logDir"/> names one.
        /// </summary>
        public static string Sentence(IReadOnlyList<string> failedSteps, bool uninstall, string? logDir)
        {
            string logLine = logDir is null ? "" : $"\r\nLog: {logDir}";

            if (failedSteps.Count == 0)
                return uninstall
                    ? "Removed. You can close this window."
                    : $"Installed. Press Win+Space to switch to it.{logLine}";

            // Named, not counted: "a step failed" sends the user to a log to find out which one,
            // and the log is the thing they are least likely to open.
            string named = failedSteps.Count <= 3
                ? string.Join(", ", failedSteps)
                : $"{string.Join(", ", failedSteps.Take(3))} and {failedSteps.Count - 3} more";

            return $"{(uninstall ? "Removal" : "Installation")} FAILED at: {named}.{logLine}";
        }

        /// <summary>
        /// The switch keys, as the finish page states them. Win+Space is the Windows key for moving
        /// between input methods; the other two are this input method's own.
        /// </summary>
        public static string SwitchKeys => SwitchKeysFor(CSharpTSFInput.Exposure.SettingsMenu);

        /// <summary>The switch keys with the settings menu's key, or without it when the menu
        /// is not exposed.</summary>
        internal static string SwitchKeysFor(bool settingsMenu) =>
            "While Manju IME is active, a short tap of Shift switches between Latin and Manchu input"
            + (settingsMenu ? ", and Ctrl+Shift+` opens its settings menu." : ".");

        /// <summary>The body of the installer's finish page.</summary>
        /// <param name="uninstaller">The uninstaller of the copy that was installed.</param>
        public static string InstallFinish(Result result, string uninstaller) =>
            result.Failed
                ? result.Message
                : $"{result.Message}\r\n\r\n{SwitchKeys}\r\n\r\nTo remove Manju IME later, run:\r\n{uninstaller}";

        /// <summary>The body of the uninstaller's finish page.</summary>
        /// <param name="root">The folder of the copy that was removed.</param>
        public static string UninstallFinish(Result result, string root) =>
            result.Failed
                ? result.Message
                : $"{result.Message}\r\n\r\nThe files of Manju IME are still in:\r\n{root}\r\n\r\n"
                  + "Delete that folder to remove them. If Windows says a file is in use, sign out or "
                  + "restart first.";
    }
}

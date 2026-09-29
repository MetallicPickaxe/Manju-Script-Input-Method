using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CSharpTSFInput
{
    /// <summary>
    /// The decisions behind ending a word before a Chromium host does, kept apart from the TSF
    /// calls so that tests can ask them directly. See <see cref="TextService"/>'s part in
    /// TextService.ChromiumHost.cs for why they exist.
    /// </summary>
    public static class ChromiumHost
    {
        public const uint WM_ACTIVATE = 0x0006, WM_KILLFOCUS = 0x0008, WM_ACTIVATEAPP = 0x001C, WM_NCACTIVATE = 0x0086;
        public const uint WA_INACTIVE = 0;
        public const uint VK_TAB = 0x09;

        /// <summary>Is a top-level window of this class one of Chromium's? Chromium, and every program
        /// built on it (Edge, Chrome, Electron applications), names its top-level windows
        /// Chrome_WidgetWin_ followed by a number.</summary>
        public static bool IsChromiumWindowClass(string? className) =>
            className != null && className.StartsWith("Chrome_WidgetWin_", StringComparison.Ordinal);

        /// <summary>Does passing this key to the host end the word first? Tab, with any modifier, while a
        /// word is being typed in a Chromium host: the host moves the focus on it and commits the
        /// composition text itself. The key still goes to the host.</summary>
        public static bool EndsWordBeforePassingKey(uint virtualKey, bool composing, bool chromiumHost) =>
            virtualKey == VK_TAB && composing && chromiumHost;

        /// <summary>Does this posted message end the word, with the mixed string, before the host sees
        /// it? A mouse button, or a pen or touch contact, going down: in a Chromium host a press anywhere
        /// commits the composition text.</summary>
        public static bool EndsWordOnPostedMessage(uint message) => message switch
        {
            0x0201 or 0x0204 or 0x0207 or 0x020B => true,   // WM_LBUTTONDOWN, WM_RBUTTONDOWN, WM_MBUTTONDOWN, WM_XBUTTONDOWN
            0x00A1 or 0x00A4 or 0x00A7 or 0x00AB => true,   // the same on the non-client area (the title bar with the tabs)
            0x0246 => true,                                 // WM_POINTERDOWN
            _ => false,
        };

        public const uint VK_MENU = 0x12;

        /// <summary>Is this posted key message Alt going down, the first press of it? Alt+Tab takes the
        /// foreground away before the host sees the Tab, and an edit session asked for then does not run:
        /// so while Alt is held the composition range holds nothing instead of the placeholder, and a
        /// switch-away finds nothing to commit. <paramref name="lParam"/> bit 30 is set on a repeat.</summary>
        public static bool IsAltPress(uint message, nuint wParam, nint lParam) =>
            message == 0x0104 && wParam == VK_MENU && ((long)lParam & (1L << 30)) == 0;   // WM_SYSKEYDOWN

        /// <summary>Is this posted key message Alt coming up? Then the placeholder goes back.</summary>
        public static bool IsAltRelease(uint message, nuint wParam) =>
            (message == 0x0105 || message == 0x0101) && wParam == VK_MENU;                   // WM_SYSKEYUP, WM_KEYUP

        /// <summary>Is this posted key message Alt+Tab or Alt+Esc, a switch-away, which drops the word as
        /// ESC does? The system takes the Tab press for its task switcher, but the key's release still
        /// reaches the host as WM_SYSKEYUP, and Chromium ends the composition on it, before any activation
        /// message arrives.</summary>
        public static bool IsSwitchAwayKeyMessage(uint message, nuint wParam) =>
            (message == 0x0104 || message == 0x0105) && (wParam == VK_TAB || wParam == 0x1B);   // WM_SYSKEYDOWN, WM_SYSKEYUP

        /// <summary>Is this sent message the host window losing the user to another program, a
        /// switch-away, which drops the word as ESC does? WM_NCACTIVATE deactivating the window while the
        /// foreground window is another process's (Chromium ends the composition on this one, the first of
        /// the deactivation messages; it names no other window, so the foreground is asked); WM_ACTIVATE
        /// deactivating the window for a window of another process or for none; WM_ACTIVATEAPP deactivating
        /// the application; WM_KILLFOCUS giving the focus to a window of another process.
        /// <paramref name="processOf"/> answers the process of a window, 0 when there is none, and
        /// <paramref name="foreground"/> the foreground window.</summary>
        public static bool IsSwitchAwayMessage(uint message, nuint wParam, nint lParam, Func<nint, uint> processOf, uint self, Func<nint> foreground)
        {
            switch (message)
            {
                case WM_NCACTIVATE:
                    if (wParam != 0) return false;
                    nint fg = foreground();
                    return fg != 0 && processOf(fg) != self;
                case WM_ACTIVATE:
                    if ((wParam & 0xFFFF) != WA_INACTIVE) return false;
                    return lParam == 0 || processOf(lParam) != self;
                case WM_ACTIVATEAPP:
                    return wParam == 0;
                case WM_KILLFOCUS:
                    return wParam != 0 && processOf((nint)wParam) != self;
                default:
                    return false;
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════════
    // A CHROMIUM HOST COMMITS THE PLACEHOLDER ITSELF.
    //
    // While a word is being typed the host's composition range holds only the placeholder. Chromium
    // (Edge, Chrome and the programs built on it) keeps its own copy of that text, and when it ends a
    // composition itself (Tab moves the focus, a click, the window losing activation) it first commits
    // that copy into the page and only then calls TerminateComposition. The rewrite OnCompositionTerminated
    // makes (the mixed string, or nothing at a switch-away) then no longer reaches the page, and the
    // placeholder, U+00B7, would be left in the document.
    //
    // So in such a host this service ends the word itself, by the same rules, before the host acts:
    // Tab, which the host is still given, and a mouse button pressed on one of the host's windows end it
    // with the mixed string (picked Manchu, unpicked Latin); the window losing the user to another program
    // ends it as ESC does. The press and the activation are seen through two hooks on this thread, set
    // the first time a word is typed in a Chromium host and removed at Deactivate: WH_GETMESSAGE for the
    // posted mouse messages and the posted Alt+Tab key, WH_CALLWNDPROC for the sent WM_NCACTIVATE,
    // WM_ACTIVATE, WM_ACTIVATEAPP and WM_KILLFOCUS. At Alt+Tab Chromium ends the composition on the
    // WM_SYSKEYUP of Tab, which comes before every activation message.
    // They act only while a word is being typed. Other hosts (Notepad, Word) end a composition through
    // TSF and take the rewrite, so nothing here runs for them.
    // ══════════════════════════════════════════════════════════════════════════════════════════════
    public partial class TextService
    {
        private const int WH_GETMESSAGE = 3, WH_CALLWNDPROC = 4;
        private const nuint PM_REMOVE = 1;
        private const uint GA_ROOT = 2;

        [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
        private static extern nint SetWindowsHookExW(int idHook, nint lpfn, nint hmod, uint threadId);
        [DllImport("user32.dll", EntryPoint = "CallNextHookEx")]
        private static extern nint CallNextHookEx(nint hhk, int code, nuint wParam, nint lParam);
        [DllImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
        private static extern int UnhookWindowsHookEx(nint hhk);
        [DllImport("user32.dll", EntryPoint = "GetAncestor")]
        private static extern nint GetAncestor(nint hwnd, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetFocus")]
        private static extern nint GetFocusWindow();
        [DllImport("user32.dll", EntryPoint = "GetClassNameW")]
        private static extern unsafe int GetClassNameRaw(nint hwnd, char* buffer, int max);

        [StructLayout(LayoutKind.Sequential)]
        private struct HookMsg { public nint hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x; public int y; }
        [StructLayout(LayoutKind.Sequential)]
        private struct HookCwp { public nint lParam; public nuint wParam; public uint message; public nint hwnd; }

        [ThreadStatic] private static TextService? t_hostHookOwner;
        private nint _getMessageHook, _callWndProcHook;
        private bool _endingWordBeforeHost;

        private static unsafe string ClassOf(nint hwnd)
        {
            if (hwnd == 0) return string.Empty;
            char* buffer = stackalloc char[128];
            int n = GetClassNameRaw(hwnd, buffer, 128);
            return n > 0 ? new string(buffer, 0, n) : string.Empty;
        }

        /// <summary>Is the window this word is typed in one of a Chromium program's? The top-level window
        /// of the context's view, or of this thread's focus window, is asked for its class.</summary>
        private bool IsChromiumHost(NativeMethods.ITfContext? context)
        {
            nint hwnd = 0;
            try
            {
                if (context != null && context.GetActiveView(out NativeMethods.ITfContextView view) == NativeMethods.S_OK && view != null)
                    view.GetWnd(out hwnd);
            }
            catch { hwnd = 0; }
            if (hwnd == 0) hwnd = GetFocusWindow();
            return hwnd != 0 && ChromiumHost.IsChromiumWindowClass(ClassOf(GetAncestor(hwnd, GA_ROOT)));
        }

        /// <summary>Sets the two hooks on this thread, once, when a word is typed in a Chromium host.</summary>
        private unsafe void ArmChromiumHostHooks(NativeMethods.ITfContext? context)
        {
            if (_getMessageHook != 0 || !IsChromiumHost(context)) return;
            uint thread = NativeMethods.GetCurrentThreadId();
            nint getMessage = (nint)(delegate* unmanaged[Stdcall]<int, nuint, nint, nint>)&OnHostGetMessage;
            nint callWndProc = (nint)(delegate* unmanaged[Stdcall]<int, nuint, nint, nint>)&OnHostCallWndProc;
            _getMessageHook = SetWindowsHookExW(WH_GETMESSAGE, getMessage, 0, thread);
            _callWndProcHook = SetWindowsHookExW(WH_CALLWNDPROC, callWndProc, 0, thread);
            t_hostHookOwner = this;
        }

        private void DisarmChromiumHostHooks()
        {
            if (_getMessageHook != 0) { UnhookWindowsHookEx(_getMessageHook); _getMessageHook = 0; }
            if (_callWndProcHook != 0) { UnhookWindowsHookEx(_callWndProcHook); _callWndProcHook = 0; }
            if (ReferenceEquals(t_hostHookOwner, this)) t_hostHookOwner = null;
        }

        /// <summary>Is <paramref name="hwnd"/> one of this service's own windows (the candidate window,
        /// the settings menu)? A press on them is not the host's.</summary>
        private static bool IsOwnWindow(nint hwnd) => ClassOf(hwnd).StartsWith("Manju", StringComparison.Ordinal);

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe nint OnHostGetMessage(int code, nuint wParam, nint lParam)
        {
            try
            {
                if (code >= 0 && wParam == PM_REMOVE && lParam != 0 && t_hostHookOwner is TextService service)
                {
                    HookMsg* msg = (HookMsg*)lParam;
                    if (ChromiumHost.EndsWordOnPostedMessage(msg->message) && !IsOwnWindow(msg->hwnd))
                        service.EndWordBeforeHost(switchAway: false, null);
                    else if (ChromiumHost.IsSwitchAwayKeyMessage(msg->message, msg->wParam))
                        service.EndWordBeforeHost(switchAway: true, null);
                    else if (ChromiumHost.IsAltPress(msg->message, msg->wParam, msg->lParam))
                        service.SetHostTextBlank(blank: true, "Alt down");
                    else if (ChromiumHost.IsAltRelease(msg->message, msg->wParam))
                        service.SetHostTextBlank(blank: false, "Alt up");
                }
            }
            catch { }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static unsafe nint OnHostCallWndProc(int code, nuint wParam, nint lParam)
        {
            try
            {
                if (code >= 0 && lParam != 0 && t_hostHookOwner is TextService service)
                {
                    HookCwp* cwp = (HookCwp*)lParam;
                    if (ChromiumHost.IsSwitchAwayMessage(cwp->message, cwp->wParam, cwp->lParam, ProcessOfWindow, (uint)Environment.ProcessId, ForegroundWindow))
                        service.EndWordBeforeHost(switchAway: true, null);
                }
            }
            catch { }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        private static uint ProcessOfWindow(nint hwnd)
        {
            if (hwnd == 0) return 0;
            return GetWindowThreadAndProcessId(hwnd, out uint pid) == 0 ? 0 : pid;
        }

        private bool _hostTextBlank;

        /// <summary>While Alt is held in a Chromium host during a word, the composition range holds
        /// nothing (<paramref name="blank"/>); when Alt comes up without a switch-away, the placeholder is
        /// written back. The word itself is not touched. At Alt+Tab the system takes the foreground away
        /// when Tab goes down, the host then commits the text of the composition range, and an edit session
        /// asked for after that does not run in time; while Alt is down, one still does.</summary>
        private void SetHostTextBlank(bool blank, string reason)
        {
            if (blank == _hostTextBlank) return;
            NativeMethods.ITfComposition? composition = this.Obj_Composition;
            if (!blank) _hostTextBlank = false;
            if (composition == null || !this.Engine_Processor.IsComposing()) return;
            NativeMethods.ITfContext? context = ContextOf(composition);
            if (context == null) return;
            string text = blank ? string.Empty : ComposingHostPlaceholder;
            var session = new KeyHandlerEditSession(context, this, text, false);
            int hr = context.RequestEditSession(this.Id_Client, session, NativeMethods.TF_ES_READWRITE | NativeMethods.TF_ES_SYNC, out int hrSession);
            bool done = hr == NativeMethods.S_OK && hrSession == NativeMethods.S_OK;
            if (blank) _hostTextBlank = done;
            // The next key writes the placeholder again when it is not there.
            this._hostPlaceholderWritten = !blank && done;
        }

        /// <summary>Ends the word being typed now, before the Chromium host acts: with the mixed string,
        /// or, for a switch-away, as ESC does. The edit session is synchronous, since the host acts as
        /// soon as this returns. Nothing happens when no word is being typed.</summary>
        private void EndWordBeforeHost(bool switchAway, string? reason)
        {
            if (_endingWordBeforeHost) return;
            NativeMethods.ITfComposition? composition = this.Obj_Composition;
            if (composition == null || !this.Engine_Processor.IsComposing()) return;
            _endingWordBeforeHost = true;
            try
            {
                if (switchAway)
                {
                    _menuComboHeld = false;
                    CloseSettingsMenu(reason ?? "switch-away");
                    this.Engine_Processor.OnKeyDown(VK_ESCAPE);
                }
                // No composition is named for the session (leftBehind null), as for a key: a named one is
                // the asynchronous switch-away session, which does nothing while the engine is composing.
                CommitSelection(ContextOf(composition), this.Engine_Processor.GetSelectedCandidate() ?? string.Empty,
                    enterKey: !switchAway, NativeMethods.TF_ES_SYNC, null);
            }
            finally { _endingWordBeforeHost = false; }
        }
    }
}

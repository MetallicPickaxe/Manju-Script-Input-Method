using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace ManjuInstaller
{
    /// <summary>The pages of the two wizards.</summary>
    public enum WizardPage { Welcome, Installed, Language, Location, Progress, Finish, Confirm }

    /// <summary>
    /// THE ORDER OF THE PAGES, apart from the window.
    ///
    /// Setup: Welcome, or Installed when a copy is already registered, then Language, Location,
    /// Progress and Finish. Uninstall: Confirm, Progress, Finish. Back goes from Location to Language
    /// and from Language to the first page; nothing goes back once the progress page has started.
    /// </summary>
    public static class WizardFlow
    {
        public static WizardPage First(bool uninstaller, bool installed) =>
            uninstaller ? WizardPage.Confirm : installed ? WizardPage.Installed : WizardPage.Welcome;

        public static WizardPage? Next(WizardPage page) => page switch
        {
            WizardPage.Welcome or WizardPage.Installed => WizardPage.Language,
            WizardPage.Language => WizardPage.Location,
            WizardPage.Location or WizardPage.Confirm => WizardPage.Progress,
            WizardPage.Progress => WizardPage.Finish,
            _ => null,
        };

        public static WizardPage? Back(WizardPage page, WizardPage first) => page switch
        {
            WizardPage.Language => first,
            WizardPage.Location => WizardPage.Language,
            _ => null,
        };

    }

    /// <summary>
    /// What a wizard is given. The two programs pass the real things (see Program); each input is a
    /// value or a function, so nothing is read or run before the page that needs it.
    /// </summary>
    public sealed class WizardSetup
    {
        /// <summary>True for `Uninstall Manju IME.exe`.</summary>
        public bool Uninstaller { get; init; }

        /// <summary>The copy Windows has registered, or null.</summary>
        public Installation? Installed { get; init; }

        /// <summary>The uninstaller's copy to remove.</summary>
        public Installation? Target { get; init; }


        /// <summary>The Windows display language, which the language page preselects.</summary>
        public ushort DisplayLanguage { get; init; }

    }

    /// <summary>
    /// THE WIZARD WINDOW, for both programs: a white page with a title, a line under it, the page's
    /// own controls, and Back, Next and Cancel along the bottom.
    ///
    /// The window is built from raw Win32 calls, so the delivery is a self-contained NativeAOT exe with
    /// no UI framework. The steps run on a worker thread so the window keeps pumping messages; the
    /// worker reports each step with a posted message and the window updates itself.
    ///
    /// Nothing is changed on the computer before Install (or Uninstall) is clicked. The finish page
    /// does not close itself: it says what happened, how to switch to the input method and where
    /// the uninstaller is.
    /// </summary>
    public sealed class WizardWindow
    {
        // The command identifiers. Next is IDOK and Cancel is IDCANCEL, so Enter and Esc reach them.
        public const int IdNext = 1, IdCancel = 2, IdBack = 3, IdBrowse = 4, IdLanguage = 5, IdFolder = 6;
        public const int IdTitle = 10, IdSubtitle = 11, IdBody = 12, IdLabel = 13, IdError = 14, IdStatus = 15, IdProgress = 16;

        // The layout, in pixels at 96 DPI; S() scales each number to the system DPI.
        private const int ClientW = 560, ClientH = 400, Margin = 24;
        private const int HeaderH = 66, BodyTop = 82, FooterTop = ClientH - 52;
        private const int ButtonW = 90, ButtonH = 26;

        private const string ClassName = "ManjuSetupWizard";
        private const uint WM_APP_LANGUAGES = 0x8000 + 1, WM_APP_STEP = 0x8000 + 2, WM_APP_DONE = 0x8000 + 3;

        private readonly WizardSetup _setup;
        private readonly WizardPage _first;
        private WizardPage _page;
        private IntPtr _hwnd, _title, _subtitle, _body, _label, _combo, _edit, _browse, _error, _status, _bar;
        private IntPtr _back, _next, _cancel, _font, _titleFont;
        private int _dpi = 96;

        private IReadOnlyList<InstalledLanguage>? _languages;
        private string? _languagesError;
        private InstalledLanguage? _language;
        private List<Step>? _plan;
        private string _uninstaller = "";
        private volatile InstallOutcome.Result? _result;
        private bool _running;
        private int _exitCode = 2;

        public WizardWindow(WizardSetup setup)
        {
            _setup = setup;
            _first = WizardFlow.First(setup.Uninstaller, setup.Installed != null);
            _page = _first;
        }


        /// <summary>Opens setup and runs it until the window closes. Returns 0 when the install
        /// finished, 1 when a step failed, 2 when setup was cancelled.</summary>
        public static int Run(WizardSetup setup)
        {
            var w = new WizardWindow(setup);
            w.Create();
            return w.RunMessageLoop();
        }

        // ------------------------------------------------------------------ the window

        /// <summary>Creates the window and its controls and shows the first page. Setup starts
        /// reading the language list here.</summary>
        public void Create()
        {
            var icc = new INITCOMMONCONTROLSEX
            {
                dwSize = Marshal.SizeOf<INITCOMMONCONTROLSEX>(),
                dwICC = ICC_STANDARD_CLASSES | ICC_PROGRESS_CLASS,
            };
            InitCommonControlsEx(ref icc);
            uint dpi = GetDpiForSystem();
            _dpi = dpi == 0 ? 96 : (int)dpi;

            IntPtr hInst = GetModuleHandleW(IntPtr.Zero);
            RegisterClassOnce(hInst);

            var rc = new RECT { right = S(ClientW), bottom = S(ClientH) };
            AdjustWindowRectEx(ref rc, WindowStyle, false, 0);
            int w = rc.right - rc.left, h = rc.bottom - rc.top;
            var work = new RECT();
            SystemParametersInfoW(SPI_GETWORKAREA, 0, ref work, 0);
            int x = work.left + Math.Max(0, (work.right - work.left - w) / 2);
            int y = work.top + Math.Max(0, (work.bottom - work.top - h) / 2);

            string caption = _setup.Uninstaller ? "Uninstall Manju IME" : "Manju IME Setup";
            _creating = this;
            try
            {
                _hwnd = CreateWindowExW(0, ClassName, caption, WindowStyle, x, y, w, h,
                                        IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
            }
            finally { _creating = null; }
            if (_hwnd == IntPtr.Zero)
                throw new InvalidOperationException($"the setup window could not be created ({Marshal.GetLastWin32Error()})");

            _font = CreateFontW(-MulDiv(9, _dpi, 72), 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");
            _titleFont = CreateFontW(-MulDiv(11, _dpi, 72), 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, "Segoe UI");

            _title = Child("STATIC", "", WS_VISIBLE | SS_LEFT | SS_NOPREFIX, IdTitle);
            _subtitle = Child("STATIC", "", WS_VISIBLE | SS_LEFT | SS_NOPREFIX, IdSubtitle);
            _body = Child("STATIC", "", SS_LEFT | SS_NOPREFIX | SS_EDITCONTROL, IdBody);
            _label = Child("STATIC", "", SS_LEFT, IdLabel);
            // Owner-drawn, so that a language without a language identifier of its own shows in gray.
            _combo = Child("COMBOBOX", "", CBS_DROPDOWNLIST | CBS_OWNERDRAWFIXED | CBS_HASSTRINGS | WS_VSCROLL | WS_TABSTOP, IdLanguage);
            _edit = Child("EDIT", InstallPlan.DeliveryRoot, ES_AUTOHSCROLL | WS_TABSTOP, IdFolder, WS_EX_CLIENTEDGE);
            _browse = Child("BUTTON", "B&rowse…", BS_PUSHBUTTON | WS_TABSTOP, IdBrowse);
            _error = Child("STATIC", "", SS_LEFT | SS_NOPREFIX | SS_EDITCONTROL, IdError);
            _status = Child("STATIC", "", SS_LEFT | SS_NOPREFIX | SS_EDITCONTROL, IdStatus);
            _bar = Child("msctls_progress32", "", 0, IdProgress);
            _back = Child("BUTTON", "< &Back", BS_PUSHBUTTON | WS_TABSTOP, IdBack);
            _next = Child("BUTTON", "&Next >", BS_DEFPUSHBUTTON | WS_TABSTOP, IdNext);
            _cancel = Child("BUTTON", "Cancel", BS_PUSHBUTTON | WS_TABSTOP, IdCancel);
            SendMessageW(_title, WM_SETFONT, _titleFont, (IntPtr)1);

            Place(_title, Margin, 14, ClientW - 2 * Margin, 22);
            Place(_subtitle, Margin + 12, 40, ClientW - 2 * Margin - 12, 20);
            Place(_label, Margin, 200, ClientW - 2 * Margin, 18);
            Place(_combo, Margin, 220, 320, 300);
            Place(_edit, Margin, 220, ClientW - 2 * Margin - ButtonW - 10, 24);
            Place(_browse, ClientW - Margin - ButtonW, 219, ButtonW, ButtonH);
            Place(_error, Margin, 256, ClientW - 2 * Margin, 60);
            Place(_status, Margin, 100, ClientW - 2 * Margin, 40);
            Place(_bar, Margin, 146, ClientW - 2 * Margin, 20);
            int cancelX = ClientW - Margin - ButtonW, nextX = cancelX - 12 - ButtonW, backX = nextX - ButtonW;
            int buttonY = FooterTop + 13;
            Place(_back, backX, buttonY, ButtonW, ButtonH);
            Place(_next, nextX, buttonY, ButtonW, ButtonH);
            Place(_cancel, cancelX, buttonY, ButtonW, ButtonH);

            if (!_setup.Uninstaller)
            {
                SendMessageW(_combo, CB_ADDSTRING, IntPtr.Zero, ReadingLanguages);
                SendMessageW(_combo, CB_SETCURSEL, IntPtr.Zero, IntPtr.Zero);
                StartReadingLanguages();
            }
            ShowPage(_first);
            ShowWindow(_hwnd, SW_SHOW);
            UpdateWindow(_hwnd);
            SetForegroundWindow(_hwnd);
        }

        /// <summary>Pumps messages until the window closes; returns the exit code.</summary>
        public int RunMessageLoop()
        {
            while (GetMessageW(out MSG m, IntPtr.Zero, 0, 0) > 0)
            {
                if (_hwnd != IntPtr.Zero && IsDialogMessageW(_hwnd, ref m)) continue;
                TranslateMessage(ref m);
                DispatchMessageW(ref m);
            }
            return _exitCode;
        }

        private const string ReadingLanguages = "Reading the language list…";

        private int IndexOfLanguage(InstalledLanguage? language)
        {
            if (language == null || _languages == null) return -1;
            for (int i = 0; i < _languages.Count; i++) if (ReferenceEquals(_languages[i], language)) return i;
            return -1;
        }

        /// <summary>One item of the language list, or the selection field: a language that is not supported in
        /// gray and never highlighted, in the system colors, so a high-contrast theme shows it too.</summary>
        private void DrawLanguageItem(DRAWITEMSTRUCT d)
        {
            int index = unchecked((int)d.itemID);
            string text = "";
            bool supported = true;
            if (_languages != null && index >= 0 && index < _languages.Count)
            {
                text = LanguageList.ShownName(_languages[index]);
                supported = _languages[index].Supported;
            }
            else if (_languages == null && index == 0) text = ReadingLanguages;
            bool disabled = (d.itemState & ODS_DISABLED) != 0;
            bool highlighted = (d.itemState & ODS_SELECTED) != 0 && supported && !disabled;
            RECT rc = d.rcItem;
            FillRect(d.hDC, ref rc, GetSysColorBrush(highlighted ? COLOR_HIGHLIGHT : COLOR_WINDOW));
            SetBkMode(d.hDC, TRANSPARENT);
            SetTextColor(d.hDC, GetSysColor(disabled || !supported ? COLOR_GRAYTEXT : highlighted ? COLOR_HIGHLIGHTTEXT : COLOR_WINDOWTEXT));
            IntPtr old = SelectObject(d.hDC, _font);
            RECT tr = rc;
            tr.left += S(4);
            DrawTextW(d.hDC, text, -1, ref tr, DT_SINGLELINE | DT_VCENTER | DT_NOPREFIX | DT_END_ELLIPSIS);
            SelectObject(d.hDC, old);
            if ((d.itemState & ODS_FOCUS) != 0 && (d.itemState & ODS_NOFOCUSRECT) == 0) DrawFocusRect(d.hDC, ref rc);
        }

        private IntPtr Child(string cls, string text, int style, int id, int exStyle = 0)
        {
            IntPtr h = CreateWindowExW(exStyle, cls, text, WS_CHILD | style, 0, 0, 0, 0,
                                       _hwnd, (IntPtr)id, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
            SendMessageW(h, WM_SETFONT, _font, (IntPtr)1);
            return h;
        }

        private int S(int v) => MulDiv(v, _dpi, 96);

        private void Place(IntPtr h, int x, int y, int w, int height) => MoveWindow(h, S(x), S(y), S(w), S(height), true);

        private static void Show(IntPtr h, bool on) => ShowWindow(h, on ? SW_SHOW : SW_HIDE);

        private static string TextOf(IntPtr h)
        {
            int n = GetWindowTextLengthW(h);
            var buffer = new char[n + 1];
            int got = GetWindowTextW(h, buffer, buffer.Length);
            return new string(buffer, 0, Math.Max(0, got));
        }

        // ------------------------------------------------------------------ the pages

        private void ShowPage(WizardPage page)
        {
            _page = page;
            bool installer = !_setup.Uninstaller;
            string title, subtitle, body, next;
            bool nextEnabled = true;

            switch (page)
            {
                case WizardPage.Welcome:
                    title = "Welcome to Manju IME Setup";
                    subtitle = "Manju IME is an input method for the traditional vertical Manchu script.";
                    body = "Setup adds Manju IME to Windows. It asks which Windows language to list Manju IME "
                           + "under and which folder to run it from, and it changes nothing on this computer "
                           + "before you click Install.\r\n\r\nClick Next to continue, or Cancel to close Setup.";
                    next = "&Next >";
                    break;

                case WizardPage.Installed:
                {
                    var copy = _setup.Installed!;
                    title = "Manju IME is already installed";
                    subtitle = "Reinstall removes the installed copy first, then installs this one.";
                    body = $"Installed in:\r\n{copy.Root}\r\n\r\nLanguage: {LanguageList.DisplayNameOf(copy.LangId)}"
                           + "\r\n\r\nTo remove Manju IME without installing it again, close Setup and run:\r\n"
                           + $"{InstalledState.UninstallerOf(copy)}\r\n\r\nClick Reinstall to continue, or Cancel to close Setup.";
                    next = "&Reinstall";
                    break;
                }

                case WizardPage.Language:
                    title = "Choose a language";
                    subtitle = "Windows lists Manju IME under the language you choose.";
                    body = _languagesError is not null
                        ? "The Windows language list could not be read, so Setup cannot add Manju IME to a "
                          + "language.\r\n\r\n" + _languagesError
                        : _languages != null && _language == null
                        ? "Manju IME can be added only to a language that has a language identifier of its own, the "
                          + "number by which Windows tells languages apart. None of the languages in your Windows "
                          + "language list has one.\r\n\r\nAdd another language in Windows Settings, then run Setup again."
                        : "Manju IME is added to one of the languages already in your Windows language list, "
                          + "next to that language’s other input methods. A language in gray has no language "
                          + "identifier of its own, and Manju IME cannot be added to it.\r\n\r\nOnly languages already "
                          + "on this computer are listed. To use another language, add it in Windows Settings first, "
                          + "then run Setup again.";
                    next = "&Next >";
                    nextEnabled = _language != null;
                    break;

                case WizardPage.Location:
                    title = "Choose the install location";
                    subtitle = "Manju IME runs from this folder.";
                    body = "Leave the folder as it is to install Manju IME where it is now. For another folder, "
                           + "Setup copies the Runtime, Installer and Script folders there and registers Manju IME "
                           + "from the copy.\r\n\r\nApart from registering with Windows, Setup writes nothing "
                           + "outside this folder.";
                    next = "&Install";
                    break;

                case WizardPage.Progress:
                    title = installer ? "Installing Manju IME" : "Removing Manju IME";
                    subtitle = "Please wait. This takes a few seconds.";
                    body = "";
                    next = "&Next >";
                    nextEnabled = false;
                    break;

                case WizardPage.Confirm:
                {
                    var copy = _setup.Target!;
                    title = "Uninstall Manju IME";
                    subtitle = "Uninstall removes Manju IME from Windows.";
                    body = copy.Registered
                        ? $"Installed in:\r\n{copy.Root}\r\n\r\nLanguage: {LanguageList.DisplayNameOf(copy.LangId)}"
                          + "\r\n\r\nUninstall removes Manju IME from your Windows language list and unregisters it. "
                          + "Its files stay in that folder; delete the folder afterwards to remove them."
                          + "\r\n\r\nClick Uninstall to continue, or Cancel to close."
                        : "Manju IME is not registered with Windows.\r\n\r\nUninstall still clears any settings a "
                          + $"removed copy left behind, using the copy in:\r\n{copy.Root}"
                          + "\r\n\r\nClick Uninstall to continue, or Cancel to close.";
                    next = "&Uninstall";
                    break;
                }

                default:   // Finish
                {
                    var r = _result!;
                    if (installer)
                    {
                        title = r.Failed ? "Setup did not finish" : "Manju IME is installed";
                        subtitle = r.Failed ? "One or more steps failed." : $"It is listed under {_language?.Name}.";
                        body = InstallOutcome.InstallFinish(r, _uninstaller);
                    }
                    else
                    {
                        title = r.Failed ? "Uninstall did not finish" : "Manju IME is removed";
                        subtitle = r.Failed ? "One or more steps failed." : "It is no longer registered with Windows.";
                        body = InstallOutcome.UninstallFinish(r, _setup.Target!.Root);
                    }
                    next = "&Finish";
                    break;
                }
            }

            SetWindowTextW(_title, title);
            SetWindowTextW(_subtitle, subtitle);
            SetWindowTextW(_body, body);
            bool chooser = page is WizardPage.Language or WizardPage.Location;
            Place(_body, Margin, BodyTop, ClientW - 2 * Margin, chooser ? 110 : FooterTop - 12 - BodyTop);
            Show(_body, page != WizardPage.Progress);
            SetWindowTextW(_label, page == WizardPage.Language ? "&Language:" : "&Folder:");
            Show(_label, chooser);
            Show(_combo, page == WizardPage.Language);
            EnableWindow(_combo, _languages != null);
            Show(_edit, page == WizardPage.Location);
            Show(_browse, page == WizardPage.Location);
            if (page != WizardPage.Location) SetWindowTextW(_error, "");
            Show(_error, page == WizardPage.Location);
            Show(_status, page == WizardPage.Progress);
            Show(_bar, page == WizardPage.Progress);

            Show(_back, WizardFlow.Back(page, _first) != null);
            SetWindowTextW(_next, next);
            EnableWindow(_next, nextEnabled);
            Show(_next, true);
            Show(_cancel, page != WizardPage.Finish);
            EnableWindow(_cancel, page != WizardPage.Progress);

            IntPtr focus = page switch
            {
                WizardPage.Language when _languages != null => _combo,
                WizardPage.Location => _edit,
                _ => nextEnabled ? _next : IntPtr.Zero,
            };
            if (focus != IntPtr.Zero) SetFocus(focus);
            // The folder box shows the start of the path, with the caret there.
            if (page == WizardPage.Location) SendMessageW(_edit, EM_SETSEL, IntPtr.Zero, IntPtr.Zero);
            InvalidateRect(_hwnd, IntPtr.Zero, true);
        }

        private void OnCommand(int id, int code)
        {
            if (id == IdLanguage)
            {
                if (code == CBN_SELCHANGE && _languages != null)
                {
                    // A language that is not supported cannot be chosen: an arrow key passes over it, the
                    // mouse and other keys leave the selection where it was.
                    int i = (int)SendMessageW(_combo, CB_GETCURSEL, IntPtr.Zero, IntPtr.Zero);
                    int previous = IndexOfLanguage(_language);
                    bool arrow = GetKeyState(VK_UP) < 0 || GetKeyState(VK_DOWN) < 0;
                    int take = LanguageList.NextSelectable(_languages, previous, i, arrow);
                    if (take != i) SendMessageW(_combo, CB_SETCURSEL, (IntPtr)take, IntPtr.Zero);
                    _language = take >= 0 && take < _languages.Count ? _languages[take] : null;
                }
                return;
            }
            IntPtr button = id switch { IdNext => _next, IdCancel => _cancel, IdBack => _back, IdBrowse => _browse, _ => IntPtr.Zero };
            // A command counts only from a button the user can see and press; Enter and Esc arrive
            // as IdNext and IdCancel whether or not that button is on the page.
            if (button == IntPtr.Zero || !IsWindowVisible(button) || !IsWindowEnabled(button)) return;

            switch (id)
            {
                case IdCancel:
                    DestroyWindow(_hwnd);
                    break;
                case IdBack:
                    if (WizardFlow.Back(_page, _first) is WizardPage back) ShowPage(back);
                    break;
                case IdBrowse:
                {
                    string? chosen = FolderPicker.Pick(_hwnd, TextOf(_edit).Trim(), "Choose the folder to install Manju IME in");
                    if (chosen != null) { SetWindowTextW(_edit, chosen); SetWindowTextW(_error, ""); }
                    break;
                }
                case IdNext:
                    switch (_page)
                    {
                        case WizardPage.Welcome:
                        case WizardPage.Installed:
                        case WizardPage.Language:
                            ShowPage(WizardFlow.Next(_page)!.Value);
                            break;
                        case WizardPage.Location:
                            Install();
                            break;
                        case WizardPage.Confirm:
                            _plan = InstallPlan.BuildUninstall(_setup.Target!);
                            StartSteps();
                            break;
                        case WizardPage.Finish:
                            DestroyWindow(_hwnd);
                            break;
                    }
                    break;
            }
        }

        /// <summary>The Install button: the folder is checked, the plan is built for the chosen
        /// language and folder, and the steps start.</summary>
        private void Install()
        {
            string folder = TextOf(_edit).Trim();
            string? refusal = Delivery.CheckTarget(InstallPlan.DeliveryRoot, InstallPlan.ResolveDll(), folder, _setup.Installed);
            if (refusal == null)
            {
                string? location = Delivery.SamePath(folder, InstallPlan.DeliveryRoot) ? null : folder;
                try
                {
                    _plan = InstallPlan.BuildInstall(_language!, location);
                    _uninstaller = InstallPlan.UninstallerFor(location);
                }
                catch (Exception ex) { refusal = ex.Message; }
            }
            if (refusal != null)
            {
                SetWindowTextW(_error, refusal);
                MessageBeep(MB_ICONWARNING);
                SetFocus(_edit);
                return;
            }
            StartSteps();
        }

        private void StartSteps()
        {
            var plan = _plan!;
            Action<Step, List<string>> execute = new StepRunner(plan).Execute;
            bool uninstall = _setup.Uninstaller;
            IntPtr hwnd = _hwnd;
            _running = true;
            SendMessageW(_bar, PBM_SETRANGE32, IntPtr.Zero, (IntPtr)plan.Count);
            SendMessageW(_bar, PBM_SETPOS, IntPtr.Zero, IntPtr.Zero);
            SetWindowTextW(_status, "Starting…");
            ShowPage(WizardPage.Progress);

            var worker = new Thread(() =>
            {
                var log = new List<string>();
                _result = InstallOutcome.Walk(plan, uninstall, execute, log,
                    (i, s) => PostMessageW(hwnd, WM_APP_STEP, (IntPtr)i, IntPtr.Zero));
                PostMessageW(hwnd, WM_APP_DONE, IntPtr.Zero, IntPtr.Zero);
            })
            { IsBackground = true };
            worker.Start();
        }

        private void StartReadingLanguages()
        {
            IntPtr hwnd = _hwnd;
            Func<IReadOnlyList<InstalledLanguage>> read = LanguageList.ReadInstalled;
            var reader = new Thread(() =>
            {
                try { _languages = read(); }
                catch (Exception ex) { _languagesError = ex.Message; }
                PostMessageW(hwnd, WM_APP_LANGUAGES, IntPtr.Zero, IntPtr.Zero);
            })
            { IsBackground = true };
            reader.Start();
        }

        /// <summary>The language list has been read: it fills the list and preselects the default.</summary>
        private void OnLanguages()
        {
            SendMessageW(_combo, CB_RESETCONTENT, IntPtr.Zero, IntPtr.Zero);
            if (_languages != null && _languages.Count == 0)
                _languagesError = "The list has no language an input method can be added to.";
            if (_languagesError != null || _languages == null)
            {
                _languages = null;
                _language = null;
            }
            else
            {
                foreach (var l in _languages) SendMessageW(_combo, CB_ADDSTRING, IntPtr.Zero, LanguageList.ShownName(l));
                _language = LanguageList.DefaultOf(_languages, _setup.DisplayLanguage);
                SendMessageW(_combo, CB_SETCURSEL, (IntPtr)IndexOfLanguage(_language), IntPtr.Zero);
            }
            if (_page == WizardPage.Language) ShowPage(WizardPage.Language);
            else EnableWindow(_combo, _languages != null);
        }

        private IntPtr OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_COMMAND:
                    OnCommand((int)((long)wParam & 0xFFFF), (int)(((long)wParam >> 16) & 0xFFFF));
                    return IntPtr.Zero;

                case DM_GETDEFID:
                    return IsWindowVisible(_next) && IsWindowEnabled(_next)
                        ? (IntPtr)((DC_HASDEFID << 16) | IdNext) : IntPtr.Zero;

                case WM_APP_LANGUAGES:
                    OnLanguages();
                    return IntPtr.Zero;

                case WM_APP_STEP:
                {
                    int i = (int)wParam;
                    if (_plan != null && i >= 0 && i < _plan.Count)
                    {
                        SetWindowTextW(_status, _plan[i].Describe() + "…");
                        SendMessageW(_bar, PBM_SETPOS, (IntPtr)i, IntPtr.Zero);
                    }
                    return IntPtr.Zero;
                }

                case WM_APP_DONE:
                    _running = false;
                    _exitCode = _result!.Failed ? 1 : 0;
                    SendMessageW(_bar, PBM_SETPOS, (IntPtr)(_plan?.Count ?? 0), IntPtr.Zero);
                    ShowPage(WizardPage.Finish);
                    return IntPtr.Zero;

                case WM_MEASUREITEM:
                {
                    var mis = Marshal.PtrToStructure<MEASUREITEMSTRUCT>(lParam);
                    if (mis.CtlID != IdLanguage) break;
                    mis.itemHeight = (uint)S(22);
                    Marshal.StructureToPtr(mis, lParam, false);
                    return (IntPtr)1;
                }

                case WM_DRAWITEM:
                {
                    var dis = Marshal.PtrToStructure<DRAWITEMSTRUCT>(lParam);
                    if (dis.CtlID != IdLanguage) break;
                    DrawLanguageItem(dis);
                    return (IntPtr)1;
                }

                case WM_CTLCOLORSTATIC:
                    SetBkColor(wParam, GetSysColor(COLOR_WINDOW));
                    SetTextColor(wParam, lParam == _error ? 0x001C2BC4 : GetSysColor(COLOR_WINDOWTEXT));
                    return GetSysColorBrush(COLOR_WINDOW);

                case WM_CTLCOLORBTN:
                    return GetSysColorBrush(lParam == _browse ? COLOR_WINDOW : COLOR_BTNFACE);

                case WM_PAINT:
                {
                    IntPtr dc = BeginPaint(_hwnd, out PAINTSTRUCT ps);
                    GetClientRect(_hwnd, out RECT client);
                    var footer = new RECT { left = 0, top = S(FooterTop), right = client.right, bottom = client.bottom };
                    FillRect(dc, ref footer, GetSysColorBrush(COLOR_BTNFACE));
                    var line = new RECT { left = 0, top = S(HeaderH), right = client.right, bottom = S(HeaderH) + 2 };
                    DrawEdge(dc, ref line, EDGE_ETCHED, BF_TOP);
                    line = new RECT { left = 0, top = S(FooterTop), right = client.right, bottom = S(FooterTop) + 2 };
                    DrawEdge(dc, ref line, EDGE_ETCHED, BF_TOP);
                    EndPaint(_hwnd, ref ps);
                    return IntPtr.Zero;
                }

                case WM_CLOSE:
                    // The steps cannot be stopped halfway, so the window stays open until they end.
                    if (!_running) DestroyWindow(_hwnd);
                    return IntPtr.Zero;

                case WM_DESTROY:
                    if (_font != IntPtr.Zero) DeleteObject(_font);
                    if (_titleFont != IntPtr.Zero) DeleteObject(_titleFont);
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
            return DefWindowProcW(_hwnd, msg, wParam, lParam);
        }

        // ------------------------------------------------------------------ the window class

        private static readonly Dictionary<IntPtr, WizardWindow> Open = new();
        [ThreadStatic] private static WizardWindow? _creating;
        private static readonly WndProcFn Proc = WndProc;
        private static bool _registered;

        private static void RegisterClassOnce(IntPtr hInst)
        {
            lock (Open)
            {
                if (_registered) return;
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    hInstance = hInst,
                    // The title bar, the taskbar button and Alt+Tab show the exe's own icon, at the
                    // sizes Windows uses for this system DPI (the program is system DPI aware).
                    hIcon = ExeIcon(hInst, GetSystemMetrics(SM_CXICON), GetSystemMetrics(SM_CYICON)),
                    hIconSm = ExeIcon(hInst, GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON)),
                    hCursor = LoadCursorW(IntPtr.Zero, (IntPtr)IDC_ARROW),
                    hbrBackground = (IntPtr)(COLOR_WINDOW + 1),
                    lpszClassName = ClassName,
                    lpszMenuName = "",
                };
                if (RegisterClassExW(ref wc) == 0)
                    throw new InvalidOperationException($"the setup window class could not be registered ({Marshal.GetLastWin32Error()})");
                _registered = true;
            }
        }

        /// <summary>
        /// The icon of this exe at the size asked for, or zero when the exe carries none.
        ///
        /// The icon group is the one the SDK builds from `ApplicationIcon` (Installer.props). Its resource ID is
        /// the SDK's choice, not ours, so it is not written here: the first icon group in the exe's own
        /// resources is the one taken, and an exe has only that one.
        /// </summary>
        private static IntPtr ExeIcon(IntPtr hInst, int cx, int cy)
        {
            _iconGroup = IntPtr.Zero;
            _iconGroupName = null;
            EnumResourceNamesW(hInst, (IntPtr)RT_GROUP_ICON, Marshal.GetFunctionPointerForDelegate(FirstIconGroup), IntPtr.Zero);
            if (_iconGroupName != null) return LoadImageW(hInst, _iconGroupName, IMAGE_ICON, cx, cy, 0);
            return _iconGroup == IntPtr.Zero ? IntPtr.Zero : LoadImageW(hInst, _iconGroup, IMAGE_ICON, cx, cy, 0);
        }

        private static readonly EnumResNameFn FirstIconGroup = OnIconGroup;
        private static IntPtr _iconGroup;
        private static string? _iconGroupName;

        /// <summary>Keeps the first name and stops the enumeration. A name that is a string is valid only during
        /// the call, so it is copied; an integer ID is kept as it is.</summary>
        private static bool OnIconGroup(IntPtr module, IntPtr type, IntPtr name, IntPtr param)
        {
            if ((ulong)name > 0xFFFF) _iconGroupName = Marshal.PtrToStringUni(name);
            else _iconGroup = name;
            return false;
        }

        private static IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
        {
            WizardWindow? w;
            lock (Open)
            {
                if (!Open.TryGetValue(h, out w) && _creating != null)
                {
                    w = _creating;
                    w._hwnd = h;
                    Open[h] = w;
                }
                if (msg == WM_NCDESTROY) Open.Remove(h);
            }
            return w != null && msg != WM_NCDESTROY ? w.OnMessage(msg, wParam, lParam) : DefWindowProcW(h, msg, wParam, lParam);
        }

        // ------------------------------------------------------------------ Win32

        private const int WindowStyle = WS_OVERLAPPED | WS_CAPTION | WS_SYSMENU | WS_MINIMIZEBOX;
        private const int WS_OVERLAPPED = 0x00000000, WS_CAPTION = 0x00C00000, WS_SYSMENU = 0x00080000;
        private const int WS_MINIMIZEBOX = 0x00020000, WS_CHILD = 0x40000000, WS_VISIBLE = 0x10000000, WS_TABSTOP = 0x00010000;
        private const int WS_VSCROLL = 0x00200000, WS_EX_CLIENTEDGE = 0x00000200;
        private const int SS_LEFT = 0x0, SS_NOPREFIX = 0x80, SS_EDITCONTROL = 0x2000;
        private const int BS_PUSHBUTTON = 0x0, BS_DEFPUSHBUTTON = 0x1;
        private const int CBS_DROPDOWNLIST = 0x3, CBS_OWNERDRAWFIXED = 0x10, CBS_HASSTRINGS = 0x200, ES_AUTOHSCROLL = 0x80;
        private const uint WM_DRAWITEM = 0x2B, WM_MEASUREITEM = 0x2C;
        private const uint ODS_SELECTED = 0x1, ODS_DISABLED = 0x4, ODS_FOCUS = 0x10, ODS_NOFOCUSRECT = 0x200;
        private const int COLOR_HIGHLIGHT = 13, COLOR_HIGHLIGHTTEXT = 14, COLOR_GRAYTEXT = 17;
        private const uint DT_VCENTER = 0x4, DT_SINGLELINE = 0x20, DT_NOPREFIX = 0x800, DT_END_ELLIPSIS = 0x8000;
        private const int TRANSPARENT = 1, VK_UP = 0x26, VK_DOWN = 0x28;
        private const uint CB_ADDSTRING = 0x143, CB_SETCURSEL = 0x14E, CB_GETCURSEL = 0x147, CB_RESETCONTENT = 0x14B;
        private const int CBN_SELCHANGE = 1;
        private const uint PBM_SETRANGE32 = 0x406, PBM_SETPOS = 0x402, EM_SETSEL = 0xB1;
        private const uint WM_DESTROY = 0x2, WM_PAINT = 0xF, WM_CLOSE = 0x10, WM_SETFONT = 0x30, WM_NCDESTROY = 0x82;
        private const uint WM_COMMAND = 0x111, WM_CTLCOLORBTN = 0x135, WM_CTLCOLORSTATIC = 0x138;
        private const uint DM_GETDEFID = 0x400;
        private const int DC_HASDEFID = 0x534B;
        private const int SW_HIDE = 0, SW_SHOW = 5;
        private const int COLOR_WINDOW = 5, COLOR_WINDOWTEXT = 8, COLOR_BTNFACE = 15;
        private const uint EDGE_ETCHED = 0x6, BF_TOP = 0x2;
        private const uint SPI_GETWORKAREA = 0x30;
        private const int IDC_ARROW = 32512;
        private const uint MB_ICONWARNING = 0x30;
        private const int ICC_PROGRESS_CLASS = 0x20, ICC_STANDARD_CLASSES = 0x4000;
        private const int RT_GROUP_ICON = 14;
        private const uint IMAGE_ICON = 1;
        private const int SM_CXICON = 11, SM_CYICON = 12, SM_CXSMICON = 49, SM_CYSMICON = 50;

        private delegate IntPtr WndProcFn(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private delegate bool EnumResNameFn(IntPtr module, IntPtr type, IntPtr name, IntPtr param);

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSEX
        {
            public int cbSize; public int style; public IntPtr lpfnWndProc;
            public int cbClsExtra, cbWndExtra; public IntPtr hInstance, hIcon, hCursor, hbrBackground;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszMenuName;
            [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
            public IntPtr hIconSm;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEASUREITEMSTRUCT { public uint CtlType, CtlID, itemID, itemWidth, itemHeight; public UIntPtr itemData; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DRAWITEMSTRUCT
        {
            public uint CtlType, CtlID, itemID, itemAction, itemState;
            public IntPtr hwndItem, hDC;
            public RECT rcItem;
            public UIntPtr itemData;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PAINTSTRUCT
        {
            public IntPtr hdc; public int fErase; public RECT rcPaint; public int fRestore, fIncUpdate;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgbReserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INITCOMMONCONTROLSEX { public int dwSize, dwICC; }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassExW(ref WNDCLASSEX c);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowExW(int ex, string cls, string name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
        [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] private static extern bool UpdateWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] private static extern bool MoveWindow(IntPtr h, int x, int y, int w, int height, bool repaint);
        [DllImport("user32.dll")] private static extern bool EnableWindow(IntPtr h, bool enable);
        [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr h);
        [DllImport("user32.dll")] private static extern bool InvalidateRect(IntPtr h, IntPtr rect, bool erase);
        [DllImport("user32.dll")] private static extern int GetMessageW(out MSG m, IntPtr h, uint a, uint b);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessageW(ref MSG m);
        [DllImport("user32.dll")] private static extern bool IsDialogMessageW(IntPtr h, ref MSG m);
        [DllImport("user32.dll")] private static extern IntPtr DefWindowProcW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageW(IntPtr h, uint m, IntPtr w, string l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SetWindowTextW(IntPtr h, string s);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr h, [Out] char[] s, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadCursorW(IntPtr inst, IntPtr name);
        [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] private static extern bool AdjustWindowRectEx(ref RECT rc, int style, bool menu, int exStyle);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool SystemParametersInfoW(uint action, uint param, ref RECT rc, uint winIni);
        [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out RECT rc);
        [DllImport("user32.dll")] private static extern int FillRect(IntPtr dc, ref RECT rc, IntPtr brush);
        [DllImport("user32.dll")] private static extern bool DrawEdge(IntPtr dc, ref RECT rc, uint edge, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr GetSysColorBrush(int index);
        [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
        [DllImport("user32.dll")] private static extern bool MessageBeep(uint type);
        [DllImport("gdi32.dll")] private static extern uint SetBkColor(IntPtr dc, uint color);
        [DllImport("gdi32.dll")] private static extern uint SetTextColor(IntPtr dc, uint color);
        [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int DrawTextW(IntPtr dc, string text, int count, ref RECT rc, uint format);
        [DllImport("user32.dll")] private static extern int DrawFocusRect(IntPtr dc, ref RECT rc);
        [DllImport("user32.dll")] private static extern short GetKeyState(int key);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateFontW(int h, int w, int esc, int ori, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, [MarshalAs(UnmanagedType.LPWStr)] string face);
        [DllImport("kernel32.dll")] private static extern IntPtr GetModuleHandleW(IntPtr name);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool EnumResourceNamesW(IntPtr module, IntPtr type, IntPtr proc, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr inst, IntPtr name, uint type, int cx, int cy, uint load);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr LoadImageW(IntPtr inst, string name, uint type, int cx, int cy, uint load);
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
        [DllImport("kernel32.dll")] private static extern int MulDiv(int number, int numerator, int denominator);
        [DllImport("comctl32.dll")] private static extern bool InitCommonControlsEx(ref INITCOMMONCONTROLSEX i);
    }
}

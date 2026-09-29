using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
// ITfCategoryMgr + CoCreateInstance (used here for display-attribute GUID atom
// registration) live in the shared Core.Interop. Engine-coupled TSF interfaces are in NativeMethods.
using CSharpTSFInput.Core.Interop;

namespace CSharpTSFInput
{
	[GeneratedComClass]
	[Guid ("11A06A2B-EA6D-43D9-870C-ADF907750ACA")]
	public partial class TextService : NativeMethods.ITfTextInputProcessorEx, NativeMethods.ITfKeyEventSink, NativeMethods.ITfCompositionSink, NativeMethods.ITfThreadMgrEventSink, NativeMethods.ITfTextLayoutSink, NativeMethods.ITfThreadFocusSink
	{
		#region Field

		private NativeMethods.ITfThreadMgr? Mgr_Thread;
		private UInt32 Id_Client;
        private const UInt32 InvalidSinkCookie = 0xFFFFFFFF;
		private UInt32 _cookieThreadMgrEventSink = 0xFFFFFFFF; // TF_INVALID_COOKIE
		private UInt32 _cookieThreadFocusSink = 0xFFFFFFFF;
		private UInt32 _cookieTextLayoutSink = 0xFFFFFFFF;
		private NativeMethods.ITfContext? _currentContext = null;
        private bool _activationEntryInProgress;
        private bool _keyEventSinkAdvised;
        private bool _frameReadySubscribed;
        private bool _foregroundFocusActive;
        private bool _pendingFrameReadyAfterFocusSync;
        private uint _lastObservedVirtualKey;
        // The key OnTestKeyDown saw last. A host that calls the test gate (Notepad, Word) follows
        // it with OnKeyDown for the same key; a host that never calls it (Edge and other Chromium hosts)
        // leaves this null. OnKeyDown reads the settings file only for a key the test gate did not see.
        private uint? _keyTheTestGateSaw;
        // Shift-tap manju<->en mode toggle (a light tap on Shift switches). A lone Shift
        // press+release (no other key in between) flips _englishMode; in EN mode the IME owns
        // nothing so Latin passes through raw. Tap is detected on Shift key-up in OnKeyUp.
        // _englishMode is the fast in-memory cache; the persisted source of truth is a compartment of
        // Manju IME's own on the thread (GUID_COMPARTMENT_MANJU_INPUTMODE). Activate reads it
        // (InitInputModeFromCompartment) so the mode survives focus cycles — incl. the per-edit churn
        // in contexts like Explorer's rename field — and every toggle writes it back.
        private bool _englishMode = false;
        // Lone-Shift-tap state lives in a tracker that follows the documented TSF delivery contract.
        // A counterexample: in EN mode Shift+A means OnKeyDown(A) is never delivered by a strict host
        // (its OnTestKeyDown returned 0), so latching "other key seen" only in OnKeyDown misreads the
        // Shift release as a lone tap and flips the input mode. The tracker latches in OnTestKeyDown,
        // which IS delivered for every keystroke by contract. See ShiftTapTracker for the full analysis.
        // The tap ceiling comes from the USER'S OWN keyboard repeat delay, not from a
        // number chosen here: past that point Windows starts auto-repeating a held key, so it is exactly
        // where this machine already draws the line between "pressed" and "held".
        private readonly ShiftTapTracker _shiftTap = new(null, SystemKeyboardRepeatDelayMs());

        /// <summary> SPI_GETKEYBOARDDELAY returns 0..3 meaning 250/500/750/1000 ms. Any failure
        /// falls back to the tracker's own default instead of leaving the threshold unbounded.</summary>
        private static int SystemKeyboardRepeatDelayMs()
        {
            try
            {
                if (NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETKEYBOARDDELAY, 0, out int setting, 0)
                    && setting >= 0 && setting <= 3)
                {
                    return 250 * (setting + 1);
                }
            }
            catch { }
            return ShiftTapTracker.DefaultTapMaxHoldMs;
        }
        // System-tray input-mode indicator (langbar item button). Created + added on
        // Activate, removed on Deactivate. Best-effort: any failure degrades to "no tray icon" and
        // never breaks the core IME.
        private NativeMethods.ITfLangBarItemMgr? _langBarMgr;
        private LangBarButton? _langBarButton;
        private bool _langBarItemAdded;
		
		// Composition object
		private NativeMethods.ITfComposition? Obj_Composition;
		
		// Candidate list presenter
		private CandidateListUIPresenter? Presenter_Candidate;
		// The dictionary feature (window + lookup) is owned entirely by
		// ManjuEngine.DictionaryModule (an IDictionaryModule). TextService only drives its lifecycle
		// hooks through that interface — it holds no concrete knowledge-window field/type.

		// Logic engine
		private CompositionProcessorEngine Engine_Processor;

		// Display attribute GUID atom cache
		private UInt32 _guidDisplayAttributeAtom;
		private Boolean _guidDisplayAttributeAtomInitialized;
		private readonly Lock _guidDisplayAttributeAtomLock = new (); // .NET 9 Lock

		#endregion Field

		#region Constructor

		public TextService ()
		{
			this.Engine_Processor = new CompositionProcessorEngine ();
			// The dictionary window is WS_EX_NOACTIVATE + MA_NOACTIVATE — clicking
			// it never produces OnSetFocus(0), so it raises this engine event instead. Without it, holding
			// Shift, clicking the dictionary window and releasing would fire a false lone-tap escape (k → k').
			this.Engine_Processor.GetEngine().OnPointerInterruption += () => _shiftTap.ObserveInterruption();
		}

		#endregion Constructor

		#region Method

		// Activate/Deactivate (ITfTextInputProcessor)
		public Int32 Activate (NativeMethods.ITfThreadMgr Ptr_ThreadMgr, UInt32 Id_Client)
		{
			return ActivateInternal (Ptr_ThreadMgr, Id_Client, 0);
		}

		// ITfTextInputProcessorEx::ActivateEx - Windows 10/11 calls this one first
		public Int32 ActivateEx (NativeMethods.ITfThreadMgr Ptr_ThreadMgr, UInt32 Id_Client, UInt32 Flags_Activate)
		{
			return ActivateInternal (Ptr_ThreadMgr, Id_Client, Flags_Activate);
		}

		private Int32 ActivateInternal (NativeMethods.ITfThreadMgr Ptr_ThreadMgr, UInt32 Id_Client, UInt32 Flags)
		{
            bool sameThreadMgr = ReferenceEquals(this.Mgr_Thread, Ptr_ThreadMgr);
            bool sameClientId = this.Id_Client == Id_Client;
            if (ShouldShortCircuitActivationEntry(
                sameThreadMgr,
                sameClientId,
                _activationEntryInProgress,
                this.Presenter_Candidate != null,
                _keyEventSinkAdvised,
                _cookieThreadMgrEventSink != InvalidSinkCookie,
                _frameReadySubscribed))
            {
                return NativeMethods.S_OK;
            }

			this.Mgr_Thread = Ptr_ThreadMgr;
			this.Id_Client = Id_Client;
            _hostThreadId = NativeMethods.GetCurrentThreadId();
            _activationEntryInProgress = true;

            try
            {
			    // defer UI initialisation to the Activate stage
			    if (this.Presenter_Candidate == null)
			    {
				    this.Presenter_Candidate = new CandidateListUIPresenter (this.Engine_Processor.GetEngine ());
				    this.Presenter_Candidate.EnsureWindowCreated ();
			    }
			    // Let the dictionary module create its HWND
			    // HERE — on the UI/message-pump thread (Activate). Its window must NOT be created on
			    // the worker thread (where OnFrameReady runs), or it never paints and freezes the
			    // host on click. Null/no-op when the module isn't compiled in or is disabled.
			    this.Engine_Processor.GetEngine().DictionaryModule?.OnActivate();

			    // Restore the input mode from Manju IME's own mode compartment (so the
			    // mode persists across activations / focus churn), then add the tray input-mode button.
			    // Both are best-effort and self-guarded — a failure here must not break the IME.
			    InitInputModeFromCompartment();
			    RegisterLangBarButton();

			    // Register the key event sink
			    if (this.Mgr_Thread is NativeMethods.ITfKeystrokeMgr Mgr_Keystroke)
			    {
                    if (!_keyEventSinkAdvised)
                    {
				        try
				        {
					        int hr = Mgr_Keystroke.AdviseKeyEventSink (this.Id_Client, this, 1);  // fForeground = TRUE (Int32)
                            if (hr == NativeMethods.S_OK)
                            {
                                _keyEventSinkAdvised = true;
                            }
				        }
				        catch (Exception)
				        {
				        }
                    }
                    if (_keyEventSinkAdvised) PreserveSettingsMenuKey(Mgr_Keystroke);
			    }

                // Register ITfThreadMgrEventSink
                if (this.Mgr_Thread is NativeMethods.ITfSource sourceThread && _cookieThreadMgrEventSink == InvalidSinkCookie)
                {
                    try
                    {
                        nint punk;
                        unsafe
                        {
                            punk = (nint)ComInterfaceMarshaller<NativeMethods.ITfThreadMgrEventSink>.ConvertToUnmanaged(this);
                        }
                        try
                        {
                            sourceThread.AdviseSink(NativeMethods.IID_ITfThreadMgrEventSink, punk, out _cookieThreadMgrEventSink);
                        }
                        finally
                        {
                            Marshal.Release(punk);
                        }

                        // Manually sync current focus: OnSetFocus callback only fires on CHANGES,
                        // so we miss the initial document if it was already focused before registration.
                        try
                        {
                            if (this.Mgr_Thread.GetFocus(out NativeMethods.ITfDocumentMgr currentDoc) == NativeMethods.S_OK && currentDoc != null)
                            {
                                this.OnSetFocus(currentDoc, null!);
                            }
                        }
                        catch (Exception)
                        {
                        }
                    }
                    catch (Exception)
                    {
                    }
                }

                // Register ITfThreadFocusSink: TSF calls it when the host thread loses or regains the
                // UI focus, which is what switching to another application or to the desktop is.
                if (this.Mgr_Thread is NativeMethods.ITfSource sourceFocus && _cookieThreadFocusSink == InvalidSinkCookie)
                {
                    try
                    {
                        nint punk;
                        unsafe
                        {
                            punk = (nint)ComInterfaceMarshaller<NativeMethods.ITfThreadFocusSink>.ConvertToUnmanaged(this);
                        }
                        try
                        {
                            int hr = sourceFocus.AdviseSink(NativeMethods.IID_ITfThreadFocusSink, punk, out _cookieThreadFocusSink);
                            if (hr != NativeMethods.S_OK) _cookieThreadFocusSink = InvalidSinkCookie;
                        }
                        finally
                        {
                            Marshal.Release(punk);
                        }
                    }
                    catch (Exception)
                    {
                        _cookieThreadFocusSink = InvalidSinkCookie;
                    }
                }

                // The other sign of a switch-away: the foreground window becoming another program's.
                WatchForeground();

			    // Pre-fetch display attribute GUID atom (SDK behavior)
			    this.TryEnsureDisplayAttributeGuidAtom (out _);
			
                // Subscribe to worker events
                SubscribeFrameEvents();

			    return NativeMethods.S_OK;
            }
            finally
            {
                _activationEntryInProgress = false;
            }
		}

		public Int32 Deactivate ()
		{
            // Unsubscribe from worker events
            UnsubscribeFrameEvents();

            _foregroundFocusActive = false;
            // Atomic {deactivate + clear pending} — a handler captured before the unsubscribe
            // cannot resurrect pending afterwards.
            DeferState_SetDeliveryActive(false);

            // The settings menu: closed, its window destroyed, its key given back.
            CloseSettingsMenu("deactivate");
            _settingsMenuWindow?.Destroy();
            _settingsMenuWindow = null;
            _menuComboHeld = false;
            if (this.Mgr_Thread is NativeMethods.ITfKeystrokeMgr Mgr_KeystrokeForMenu) UnpreserveSettingsMenuKey(Mgr_KeystrokeForMenu);

			// Unregister the key event sink
			if (_keyEventSinkAdvised && this.Mgr_Thread is NativeMethods.ITfKeystrokeMgr Mgr_Keystroke)
			{
				try
				{
					Mgr_Keystroke.UnadviseKeyEventSink (this.Id_Client);
                    _keyEventSinkAdvised = false;
				}
				catch (Exception)
                {
                }
			}

            // Unregister ThreadMgrEventSink
            if (this.Mgr_Thread is NativeMethods.ITfSource sourceThread && _cookieThreadMgrEventSink != InvalidSinkCookie)
            {
                sourceThread.UnadviseSink(_cookieThreadMgrEventSink);
                _cookieThreadMgrEventSink = InvalidSinkCookie;
            }

            // Unregister ThreadFocusSink
            if (this.Mgr_Thread is NativeMethods.ITfSource sourceFocus && _cookieThreadFocusSink != InvalidSinkCookie)
            {
                sourceFocus.UnadviseSink(_cookieThreadFocusSink);
                _cookieThreadFocusSink = InvalidSinkCookie;
            }
            StopWatchingForeground();
            DisarmChromiumHostHooks();

            // Unregister TextLayoutSink
            if (_currentContext is NativeMethods.ITfSource sourceContext && _cookieTextLayoutSink != InvalidSinkCookie)
            {
                sourceContext.UnadviseSink(_cookieTextLayoutSink);
                _cookieTextLayoutSink = InvalidSinkCookie;
            }
            _currentContext = null;
            _activationEntryInProgress = false;

			// Destroy the candidate window
			this.Presenter_Candidate?.Destroy ();
			this.Presenter_Candidate = null;
			// Remove the tray input-mode button for this activation (best-effort).
			UnregisterLangBarButton();

			// Release the dictionary module's WINDOW for this activation — NOT
			// dispose the module. The module is engine-lifetime (single get-only instance) and will
			// be re-activated; OnDeactivate tears down the HWND recreatably (mirroring how the main
			// candidate presenter is Destroy()ed + recreated above). Calling Dispose() here would latch
			// the module permanently dead after the first focus cycle (the knowledge window would never
			// re-show while lookups kept running).
			this.Engine_Processor.GetEngine().DictionaryModule?.OnDeactivate();

			this.Mgr_Thread = null;
			this.Id_Client = 0;
            _hostThreadId = 0;

			// Remove from the GC protection list on Deactivate, so the GC may collect it
			GCProtection.Release (this);

			return NativeMethods.S_OK;
		}

		// Composition accessors
		public NativeMethods.ITfComposition? GetComposition () => this.Obj_Composition;
		public void SetComposition (NativeMethods.ITfComposition? Obj_Comp)
		{
			this.Obj_Composition = Obj_Comp;
			// Composition lifecycle boundary (start OR end) → the host range content is
			// no longer known to be the placeholder; the next live update must actually write.
			this._hostPlaceholderWritten = false;
		}

		// TRUE while the live composition range is known to already hold the '·'
		// placeholder. The placeholder never changes during composing, so every subsequent
		// per-key Update EditSession is an idempotent no-op, yet each one takes a SYNC document lock:
		// under fast typing the host still holds it → HR=0x80040208 TF_E_SYNCHRONOUS, plus a needless
		// host re-layout per key (which shows as flicker).
		// When set, the live-update path SKIPS the EditSession; any failure clears it so the next key
		// retries the write.
		private bool _hostPlaceholderWritten;

        private RECT _cachedCandidatePos;
        private bool _hasCachedCandidatePos;
        private bool _hasAuthoritativeCandidatePos;
        private bool _authoritativeCandidatePosDirty;


        private static bool HasUsableCaretRect(RECT rect)
        {
            return rect.Left != 0 || rect.Top != 0 || rect.Right != 0 || rect.Bottom != 0;
        }

        private static RECT CreatePointRect(POINT point)
        {
            return new RECT
            {
                Left = point.X,
                Top = point.Y,
                Right = point.X,
                Bottom = point.Y
            };
        }

        // The small placeholder written into the host's composition range while composing: a middle
        // dot, which is visible, tiny, and does not push the caret. Commit and terminate both replace it with the real string.
        internal const string ComposingHostPlaceholder = "·";

        private static int ClampToScreen(int value, int screenExtent)
        {
            if (screenExtent <= 0)
            {
                return value;
            }

            if (value < 0)
            {
                return 0;
            }

            int max = screenExtent - 50;
            if (max < 0)
            {
                max = 0;
            }

            return value > max ? max : value;
        }

        // Boundary-avoidance placement (PURE). Given the caret rect, the
        // window size, and the target monitor's WORK area (taskbar-excluded), pick a top-left so the window
        // stays fully on-screen. Anchoring at the caret's bottom-right and clamping to screen-50 would ignore
        // the window size, and a tall Preview would slide under the taskbar or off the bottom.
        //   • prefer below-right of the caret;
        //   • overflow the RIGHT work edge → open LEFTWARD, anchoring the window's right to the caret;
        //   • overflow the BOTTOM work edge → open UPWARD, window bottom at the caret's top line;
        //   • corners flip both; a window larger than the work area is pinned to the work edge (never off-screen,
        //     never under the taskbar). winW/winH<=0 (size not known yet) → anchor + edge-clamp only.
        public static (int x, int y) ComputeAnchoredWindowPosition(RECT caret, int winW, int winH, RECT work)
        {
            int x = caret.Right;   // prefer to the right of the caret
            int y = caret.Bottom;  // prefer below the caret (window grows downward as the composed text grows)

            // Horizontal: flip left near the right edge (only meaningful once the width is known), then ALWAYS
            // clamp the top-left into the work area (so even size-unknown or off-screen anchors stay visible).
            if (winW > 0 && x + winW > work.Right) x = caret.Right - winW; // near the right edge → open leftward (anchor the window's right to the caret's right)
            int maxX = work.Right - (winW > 0 ? winW : 0);                 // keep the WHOLE window on-screen when sized
            if (maxX < work.Left) maxX = work.Left;                        // window wider than the work area → pin to the left
            if (x > maxX) x = maxX;
            if (x < work.Left) x = work.Left;

            // Vertical: flip up near the bottom edge, then always clamp into the work area.
            if (winH > 0 && y + winH > work.Bottom) y = caret.Top - winH; // near the bottom → open upward (window bottom lands on the caret's top, i.e. above the line)
            int maxY = work.Bottom - (winH > 0 ? winH : 0);
            if (maxY < work.Top) maxY = work.Top;                          // window taller than the work area → pin to the top
            if (y > maxY) y = maxY;
            if (y < work.Top) y = work.Top;

            return (x, y);
        }

        // Work area (taskbar-excluded) of the monitor containing the caret; falls back
        // to the full primary-screen bounds when the monitor query is unavailable. Isolated from the pure
        // placement math above, which makes no native calls.
        private static RECT GetWorkAreaForPoint(int px, int py, int fallbackW, int fallbackH)
        {
            try
            {
                nint mon = NativeMethods.MonitorFromPoint(new POINT { X = px, Y = py }, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (mon != 0)
                {
                    var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (NativeMethods.GetMonitorInfo(mon, ref mi) &&
                        mi.rcWork.Right > mi.rcWork.Left && mi.rcWork.Bottom > mi.rcWork.Top)
                    {
                        return mi.rcWork;
                    }
                }
            }
            catch (Exception)
            {
            }
            return new RECT { Left = 0, Top = 0, Right = fallbackW > 0 ? fallbackW : 1920, Bottom = fallbackH > 0 ? fallbackH : 1080 };
        }

        private void SetPresenterLocationWithTrace(int x, int y, string? source)
        {
            this.Presenter_Candidate?.SetLocation(x, y);
            // Re-position the dictionary against the main's NEW rect so
            // it stops occluding the main. CRITICAL: pass the INTENDED (x,y) — the presenter's SetLocation is ASYNC
            // (marks _positionDirty + queues an update; the real SetWindowPos runs LATER), so GetWindowRect right
            // now still returns the PRE-move main rect. Positioning from that stale rect would leave the
            // dictionary occluding the main on first show; anchoring on the target (x,y) avoids that.
            RepositionDictionaryRelativeToMain(x, y);
        }

        private void NotifyPresenterFrameReadyWithTrace(string? source)
        {
            // Hand the presenter the DELIVERED object when there is one; the
            // parameterless overload is for signals that carry no frame (tab-focus repaint).
            var f = _deliveredFrame;
            if (f != null) this.Presenter_Candidate?.NotifyFrameReady(f);
            else this.Presenter_Candidate?.NotifyFrameReady();
            RepositionDictionaryRelativeToMain();
        }

        // Position the dictionary window relative to the main
        // candidate window's CURRENT on-screen rect. Called BOTH on frame-ready AND right after the main
        // window moves, so the dict always avoids the main's real (caret-following) position. Module (if
        // present) positions/shows its own window; null when not compiled in → no-op.
        private void RepositionDictionaryRelativeToMain(int overrideX = int.MinValue, int overrideY = int.MinValue)
        {
            var dictModule = this.Engine_Processor.GetEngine().DictionaryModule;
            if (dictModule == null) return;

            // EFFECT-POINT VALIDATION. This path reaches a
            // REPLACEABLE module's OnFrameReady, which is an observable side effect, and it can be
            // reached without re-checking the token. Serialising delivery onto the owning thread already
            // prevents a cancelled composition from getting here through the frame path, but this method
            // is also called directly after a main-window move, so validation happens at the effect
            // itself instead of resting on how we were reached.
            var held = _deliveredFrame;
            if (held != null && !this.Engine_Processor.GetEngine().IsFrameCurrent(held))
            {
                return;
            }

            nint mainHwnd = this.Presenter_Candidate?.Hwnd ?? 0;
            if (mainHwnd != 0 && NativeMethods.GetWindowRect(mainHwnd, out RECT mainRect))
            {
                int mw = Math.Max(1, mainRect.Right - mainRect.Left);
                int mh = Math.Max(1, mainRect.Bottom - mainRect.Top);
                // Anchor on the INTENDED main position when provided (from the just-issued,
                // still-pending async SetLocation) instead of GetWindowRect's stale Left/Top; size is stable
                // across a move so it's read from the (already-sized) window rect either way.
                int mx = overrideX != int.MinValue ? overrideX : mainRect.Left;
                int my = overrideY != int.MinValue ? overrideY : mainRect.Top;
                dictModule.OnFrameReady(mx, my, mw, mh);
            }
        }

        private static bool HasBoundedActivationEntryState(
            bool hasPresenter,
            bool hasKeyEventSinkAdvice,
            bool hasThreadMgrEventSinkAdvice,
            bool hasFrameReadySubscription)
        {
            return hasPresenter
                && hasKeyEventSinkAdvice
                && hasThreadMgrEventSinkAdvice
                && hasFrameReadySubscription;
        }

        private static bool ShouldShortCircuitActivationEntry(
            bool sameThreadMgr,
            bool sameClientId,
            bool activationEntryInProgress,
            bool hasPresenter,
            bool hasKeyEventSinkAdvice,
            bool hasThreadMgrEventSinkAdvice,
            bool hasFrameReadySubscription)
        {
            if (!sameThreadMgr || !sameClientId)
            {
                return false;
            }

            if (activationEntryInProgress)
            {
                return true;
            }

            return HasBoundedActivationEntryState(
                hasPresenter,
                hasKeyEventSinkAdvice,
                hasThreadMgrEventSinkAdvice,
                hasFrameReadySubscription);
        }

        private static bool TryResolveCandidateWindowLocation(RECT rect, bool hasCaretRect, POINT fallbackPoint, bool hasFallbackPoint, int screenWidth, int screenHeight, int winW, int winH, out int x, out int y)
        {
            RECT caret;
            if (hasCaretRect)
            {
                // Bottom-Right anchor is the PREFERRED direction (window below the caret, following text down).
                caret = rect;
            }
            else if (hasFallbackPoint)
            {
                // Thin caret rect at the fallback point (top==bottom, left==right).
                caret = new RECT { Left = fallbackPoint.X, Top = fallbackPoint.Y, Right = fallbackPoint.X, Bottom = fallbackPoint.Y };
            }
            else
            {
                x = 0;
                y = 0;
                return false;
            }

            // Work-area + window-size aware boundary avoidance. A size-blind
            // ClampToScreen(caret.Right/Bottom, screen-50) would let a tall Preview slide off the
            // bottom / under the taskbar. Prefers below-right of the caret; flips up/left near the work edges.
            RECT work = GetWorkAreaForPoint(caret.Right, caret.Bottom, screenWidth, screenHeight);
            (x, y) = ComputeAnchoredWindowPosition(caret, winW, winH, work);
            return true;
        }

        private void ClearCandidatePos()
        {
            _cachedCandidatePos = default;
            _hasCachedCandidatePos = false;
            _hasAuthoritativeCandidatePos = false;
            _authoritativeCandidatePosDirty = false;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════
        // A SWITCH-AWAY IS ESC.
        //
        // A switch-away is the host thread handing the UI focus to another program or to the desktop.
        // Three things report it: OnKillThreadFocus; the foreground window becoming another program's
        // (WatchForeground); and a focus-loss event, ThreadMgr OnSetFocus(null) or the keyboard sink's
        // OnSetFocus(0), arriving when the thread no longer has the TSF focus (IsHostThreadFocused).
        //
        // It is handled as the ESC key: what was being composed is dropped, every IME window closes at
        // once, and frames still on their way are dropped by their identity. Clear moves the engine to a
        // new composition epoch, and ManjuEngine.IsFrameCurrent refuses a frame stamped with an older one.
        //
        // Focus moving inside the host is not a switch-away. It neither ends the composition nor hides a
        // window.
        // ══════════════════════════════════════════════════════════════════════════════════════════

        /// <summary> The ESC key's own code, run without a key: the engine is given VK_ESCAPE through
        /// CompositionProcessorEngine.OnKeyDown, and the composition ends through
        /// <see cref="CommitSelection"/>, as for the key. The edit session is the one difference.
        /// TF_ES_SYNC is for documented situations such as keystroke handling, and there is no keystroke
        /// here, so the session is asynchronous, and the engine and the windows do not wait for it. When
        /// the host refuses it, the composition stays with the host, or <see cref="EndCompositionLeftBehind"/>
        /// ends it when the focus comes back.</summary>
        private void SwitchAway(string? reason)
        {
            _menuComboHeld = false;
            CloseSettingsMenu(reason ?? "switch-away");
            NativeMethods.ITfComposition? composition = this.Obj_Composition;
            if (composition == null && !this.Engine_Processor.IsComposing())
            {
                // Nothing to drop; the windows close all the same.
                this.ResetCandidateAnchoringCycle();
                return;
            }
            this.Engine_Processor.OnKeyDown(VK_ESCAPE);
            CommitSelection(ContextOf(composition), this.Engine_Processor.GetSelectedCandidate() ?? string.Empty,
                enterKey: false, NativeMethods.TF_ES_ASYNC, composition);
        }

        /// <summary> The focus is back. A composition a switch-away left behind, which its session has not
        /// ended (refused, or not run yet), is ended now through the same path. Nothing is shown.</summary>
        private void EndCompositionLeftBehind(string? reason)
        {
            NativeMethods.ITfComposition? composition = this.Obj_Composition;
            if (composition != null && IsLeftBehind(composition)) SwitchAway(reason);
        }

        /// <summary> Is <paramref name="composition"/> still this service's composition, with nothing typed
        /// into it since the engine was cleared?</summary>
        internal bool IsLeftBehind(NativeMethods.ITfComposition composition)
            => ReferenceEquals(this.Obj_Composition, composition) && !this.Engine_Processor.IsComposing();

        /// <summary> The context a composition lives in, read from its own range. Null without a
        /// composition, or when the range does not answer.</summary>
        private static NativeMethods.ITfContext? ContextOf(NativeMethods.ITfComposition? composition)
        {
            if (composition == null) return null;
            try
            {
                if (composition.GetRange(out NativeMethods.ITfRange range) != NativeMethods.S_OK || range == null) return null;
                return range.GetContext(out NativeMethods.ITfContext context) == NativeMethods.S_OK ? context : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary> ITfThreadMgr::IsThreadFocus: does this thread have the TSF input focus? When the
        /// question cannot be asked or is not answered, the answer is yes, so a failed call never turns a
        /// focus change inside the host into a switch-away.</summary>
        private bool IsHostThreadFocused()
        {
            try
            {
                var mgr = this.Mgr_Thread;
                return mgr == null || mgr.IsThreadFocus(out int focused) != NativeMethods.S_OK || focused != 0;
            }
            catch { return true; }
        }

        // The foreground window, watched through an out-of-context WinEvent hook set on this thread.
        // Windows calls the hook from this thread's message loop, and Deactivate removes it on this thread.
        // It is not set in an app container (a Store app): SetWinEventHook's documentation lists desktop
        // apps only, and its one remark on Store apps concerns in-context hooks, so nothing documents an
        // out-of-context hook set from inside one. There a switch-away rests on OnKillThreadFocus.
        private nint _foregroundHook;
        private static readonly Dictionary<nint, WeakReference<TextService>> s_foregroundHooks = new();


        private unsafe void WatchForeground()
        {
            if (_foregroundHook != 0) return;
            if (IsAppContainerProcess())
            {
                return;
            }
            nint proc = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void>)&OnWinEvent;
            nint hook = NativeMethods.SetWinEventHook(NativeMethods.EVENT_SYSTEM_FOREGROUND, NativeMethods.EVENT_SYSTEM_FOREGROUND,
                0, proc, 0, 0, NativeMethods.WINEVENT_OUTOFCONTEXT);
            if (hook == 0)
            {
                return;
            }
            lock (s_foregroundHooks) s_foregroundHooks[hook] = new WeakReference<TextService>(this);
            _foregroundHook = hook;
        }

        private void StopWatchingForeground()
        {
            nint hook = _foregroundHook;
            if (hook == 0) return;
            _foregroundHook = 0;
            lock (s_foregroundHooks) s_foregroundHooks.Remove(hook);
            NativeMethods.UnhookWinEvent(hook);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static void OnWinEvent(nint hook, uint winEvent, nint hwnd, int idObject, int idChild, uint idEventThread, uint time)
        {
            try
            {
                TextService? service = null;
                lock (s_foregroundHooks)
                {
                    if (s_foregroundHooks.TryGetValue(hook, out var owner)) owner.TryGetTarget(out service);
                }
                service?.OnForegroundWindowChanged(hwnd);
            }
            catch (Exception)
            {
            }
        }

        /// <summary> The foreground window is now <paramref name="foreground"/>. When the UI focus has gone
        /// to another program, it is a switch-away.</summary>
        private void OnForegroundWindowChanged(nint foreground)
        {
            if (UiFocusIsInAnotherProgram(foreground))
                SwitchAway(null);
        }

        /// <summary> Has the UI focus gone to another program? The foreground window has to belong to another
        /// process, and so does the window with the keyboard focus on that window's thread, when there is
        /// one: a window of this process can sit inside another process's top-level window, and focus in it
        /// has not left this program.</summary>
        private static bool UiFocusIsInAnotherProgram(nint foreground)
        {
            if (foreground == 0) return false;
            uint self = (uint)Environment.ProcessId;
            uint thread = GetWindowThreadAndProcessId(foreground, out uint pid);
            if (thread == 0 || pid == self) return false;
            var info = new NativeMethods.GUITHREADINFO { cbSize = Marshal.SizeOf<NativeMethods.GUITHREADINFO>() };
            if (NativeMethods.ReadGuiThreadInfo(thread, ref info) && info.hwndFocus != 0)
            {
                GetWindowThreadAndProcessId(info.hwndFocus, out uint focusPid);
                if (focusPid == self) return false;
            }
            return true;
        }

        /// <summary> Is this process an app container? GetTokenInformation(TokenIsAppContainer) on the
        /// process token; a query that fails answers no.</summary>
        private static unsafe bool IsAppContainerProcess()
        {
            uint value = 0;
            return NativeMethods.GetTokenInformation(NativeMethods.CurrentProcessToken, NativeMethods.TokenIsAppContainer,
                &value, sizeof(uint), out _) && value != 0;
        }

        /// <summary> Has the user switched away? The thread no longer has the TSF focus, or, outside an app
        /// container, the UI focus is in another program.</summary>
        private bool IsSwitchedAway()
            => !IsHostThreadFocused() || (!IsAppContainerProcess() && UiFocusIsInAnotherProgram(ForegroundWindow()));


        private static nint ForegroundWindow()
        {
            return GetForegroundWindow();
        }

        private void ResetCandidateAnchoringCycle()
        {
            ClearCandidatePos();
            // reset focus+_rawInput every end path (prevents same-prefix revival)
            this.Presenter_Candidate?.ResetFocus();
            this.Presenter_Candidate?.Update_Candidates(new List<string>());
            // Universal composition-end chokepoint. Called by
            // ALL end paths (selected-commit, _pendingAutoCommit, OnCompositionTerminated,
            // focus-loss), so notifying the dictionary module here hides its window for every path
            // in one place. Routed through the interface; null when the
            // module isn't compiled in.
            this.Engine_Processor.GetEngine().DictionaryModule?.OnCompositionEnd();
        }

        // Detect structurally invalid rect where coordinates are inverted.
        // Word (WINWORD) can report such a rect, with Left > Right and Top > Bottom.
        private static bool IsStructurallyInvalidRect(RECT rect)
        {
            return rect.Left > rect.Right || rect.Top > rect.Bottom;
        }

        private static bool IsLegitimateSameLineNarrowing(RECT currentRect, RECT newRect)
        {
            const int EdgeDriftThreshold = 3;

            int currentWidth = currentRect.Right - currentRect.Left;
            int newWidth = newRect.Right - newRect.Left;
            bool leftStableOrForward = newRect.Left >= currentRect.Left - EdgeDriftThreshold;
            bool widthMeaningfullyNarrowed = newWidth < currentWidth - EdgeDriftThreshold;

            return leftStableOrForward && widthMeaningfullyNarrowed;
        }

        // Detect regressive authoritative geometry.
        // Classic hosts can report stale/regressive authoritative positions that would cause rollback.
        // Pattern (Notepad, Explorer): the position advances for a few keys, then jumps back behind its start.
        // Regression is detected as both edges moving backward significantly on the same line.
        // Legitimate same-line narrowing must not be misclassified as rollback.
        private static bool IsRegressiveAuthoritativeGeometry(RECT currentRect, RECT newRect, bool hasCachedPos, bool currentIsAuthoritative)
        {
            // Only check for regression if we already have an authoritative position
            if (!hasCachedPos || !currentIsAuthoritative)
            {
                return false;
            }

            // If the Y coordinate moved significantly, it is likely a line wrap or jump, not a rollback.
            // Regression check only applies to backward movement on the SAME line.
            int currentY = currentRect.Top;
            int newY = newRect.Top;
            const int YDriftThreshold = 5; 
            if (Math.Abs(newY - currentY) > YDriftThreshold)
            {
                return false;
            }

            const int RegressionThreshold = 3;

            if (IsLegitimateSameLineNarrowing(currentRect, newRect))
            {
                return false;
            }

            bool rightEdgeRegressed = newRect.Right < currentRect.Right - RegressionThreshold;
            bool leftEdgeRegressed = newRect.Left < currentRect.Left - RegressionThreshold;
            return rightEdgeRegressed && leftEdgeRegressed;
        }

        private static unsafe bool HasSameNativeContextIdentity(NativeMethods.ITfContext? currentContext, NativeMethods.ITfContext? focusContext)
        {
            if (currentContext == null || focusContext == null)
            {
                return false;
            }

            void* currentPtr = null;
            void* focusPtr = null;

            try
            {
                currentPtr = ComInterfaceMarshaller<NativeMethods.ITfContext>.ConvertToUnmanaged(currentContext);
                focusPtr = ComInterfaceMarshaller<NativeMethods.ITfContext>.ConvertToUnmanaged(focusContext);
                return currentPtr != null && focusPtr != null && currentPtr == focusPtr;
            }
            finally
            {
                // CS8500: ComInterfaceMarshaller<T>.Free requires T* for a managed interface T;
                // taking a pointer to ITfContext is intentional for COM Free and is bounded by the
                // surrounding `unsafe` context. Pragma is the minimal, targeted suppression.
#pragma warning disable CS8500
                if (currentPtr != null)
                {
                    ComInterfaceMarshaller<NativeMethods.ITfContext>.Free((NativeMethods.ITfContext*)currentPtr);
                }

                if (focusPtr != null)
                {
                    ComInterfaceMarshaller<NativeMethods.ITfContext>.Free((NativeMethods.ITfContext*)focusPtr);
                }
#pragma warning restore CS8500
            }
        }

        private static bool ShouldShortCircuitThreadMgrFocusSync(bool sameNativeContext, bool hasActiveLayoutSink)
        {
            return sameNativeContext && hasActiveLayoutSink;
        }

        private static bool ShouldUseCachedAuthoritativeAnchorWhileAuthoritativeRefreshPending(
            bool authoritativeCandidatePosDirty,
            bool hasCachedCandidatePos,
            bool hasAuthoritativeCandidatePos)
        {
            return authoritativeCandidatePosDirty && hasCachedCandidatePos && hasAuthoritativeCandidatePos;
        }

        private static bool ShouldUseLiveCaretFallbackWhileAuthoritativeRefreshPending(
            bool authoritativeCandidatePosDirty,
            bool hasFallbackPoint,
            bool hasCachedAuthoritativeAnchor)
        {
            return authoritativeCandidatePosDirty && hasFallbackPoint && !hasCachedAuthoritativeAnchor;
        }

        private void MarkAuthoritativeCandidatePosDirtyAfterMutation()
        {
            _authoritativeCandidatePosDirty = this.Engine_Processor.IsComposing();
        }

        private static bool ShouldApplyImmediateCandidateWindowLocationAfterMutation(bool authoritativeCandidatePosDirty)
        {
            return authoritativeCandidatePosDirty;
        }

        private static bool ShouldResetCandidateCycleOnThreadMgrFocusLoss(
            bool foregroundFocusActive,
            bool isComposing,
            bool hasVisibleCandidateSelection,
            bool hasCachedCandidatePos,
            bool hasCachedAuthoritativeAnchor,
            bool authoritativeCandidatePosDirty,
            bool hasPendingFrameReadyAfterFocusSync,
            bool hasEngineCandidateContent)
        {
            bool hasAuthoritativeRefreshKeepAlive =
                authoritativeCandidatePosDirty
                || (hasCachedAuthoritativeAnchor
                    && (foregroundFocusActive
                        || isComposing
                        || hasVisibleCandidateSelection
                        || hasPendingFrameReadyAfterFocusSync
                        || hasEngineCandidateContent))
                || (hasCachedCandidatePos && hasPendingFrameReadyAfterFocusSync);

            bool hasCompositionKeepAlive =
                isComposing
                && (hasCachedCandidatePos
                    || hasPendingFrameReadyAfterFocusSync
                    || hasEngineCandidateContent);

            // Explorer-like hosts can momentarily drop ThreadMgr focus before foreground focus is
            // actually gone. If foreground focus is still active and we still have visible
            // candidate UI or an authoritative anchor, treat that churn as transient instead of
            // collapsing the cycle into an empty reset immediately.
            bool hasForegroundPresentationKeepAlive =
                foregroundFocusActive
                && (hasVisibleCandidateSelection
                    || hasCachedCandidatePos
                    || hasCachedAuthoritativeAnchor
                    || authoritativeCandidatePosDirty
                    || hasPendingFrameReadyAfterFocusSync);

            return !hasAuthoritativeRefreshKeepAlive
                && !hasForegroundPresentationKeepAlive
                && !hasCompositionKeepAlive;
        }

        private static bool ShouldPreserveCachedAuthoritativeAnchorOnThreadMgrFocusLoss(
            bool shouldResetCandidateCycle,
            bool hasCachedCandidatePos,
            bool hasAuthoritativeCandidatePos)
        {
            return !shouldResetCandidateCycle && hasCachedCandidatePos && hasAuthoritativeCandidatePos;
        }

        private void ApplyImmediateCandidateWindowLocationAfterMutation()
        {
            if (!ShouldApplyImmediateCandidateWindowLocationAfterMutation(_authoritativeCandidatePosDirty))
            {
                return;
            }

            ApplyCandidateWindowLocation();
        }

        private static bool ShouldReplaceCandidatePos(RECT currentRect, bool hasCachedPos, bool hasAuthoritativeCandidatePos, RECT newRect, bool newIsAuthoritative)
        {
            // First, reject structurally invalid rects unconditionally.
            if (IsStructurallyInvalidRect(newRect))
            {
                return false;
            }

            // Following behavior:
            // 1. Authoritative updates (from TSF layout) replace the cache, UNLESS regressive.
            if (newIsAuthoritative)
            {
                // Detect and reject stale/regressive authoritative geometry.
                // Classic hosts can report authoritative positions that roll backward after
                // initially correct movement. These must be rejected to prevent visual snap-back.
                if (IsRegressiveAuthoritativeGeometry(currentRect, newRect, hasCachedPos, hasAuthoritativeCandidatePos))
                {
                    return false;
                }
                return true;
            }

            // 2. If no position is cached, accept the new one.
            if (!hasCachedPos)
            {
                return true;
            }

            // 3. If current cache is authoritative but new update is NOT:
            // Freeze authoritative-anchor precedence: once an authoritative anchor exists
            // in the live composition cycle, ordinary non-authoritative fallback must never replace it.
            // There is no exception for progressive fallback anchors: one would allow snap-back.
            if (hasAuthoritativeCandidatePos)
            {
                return false;
            }

            // 4. If both are non-authoritative (e.g. following cursor fallback):
            // We ALWAYS accept the new one to enable following behavior, even during backspacing.
            return true;
        }

        private void StoreCandidatePosInternal(RECT rect, bool isAuthoritative, string? source)
        {
            if (!HasUsableCaretRect(rect))
            {
                // During active composition with valid cached position,
                // do NOT clear anchor cache on zero rect. CUAS classic hosts may transiently
                // return (0,0,0,0) during re-layout, which would destroy the regression memory.
                if (this.Engine_Processor.IsComposing() && _hasCachedCandidatePos)
                {
                    return;
                }
                ClearCandidatePos();
                return;
            }

            if (IsStructurallyInvalidRect(rect))
            {
                return;
            }

            if (isAuthoritative && IsRegressiveAuthoritativeGeometry(_cachedCandidatePos, rect, _hasCachedCandidatePos, _hasAuthoritativeCandidatePos))
            {
                return;
            }

            bool shouldReplace = ShouldReplaceCandidatePos(_cachedCandidatePos, _hasCachedCandidatePos, _hasAuthoritativeCandidatePos, rect, isAuthoritative);
            if (!shouldReplace)
            {
                return;
            }

            _cachedCandidatePos = rect;
            _hasCachedCandidatePos = true;
            _hasAuthoritativeCandidatePos = isAuthoritative;
            if (isAuthoritative)
            {
                _authoritativeCandidatePosDirty = false;
            }
        }

        // The host's own UI thread: the one TSF activated this text service on, recorded at activation.
        // The caret fallback asks this thread about its caret and no other, so a caret that belongs to
        // another program (the foreground thread after the Win key, Alt+Tab or a click elsewhere) is never
        // taken for the host's. 0 while not activated, and then there is no fallback.
        private uint _hostThreadId;

        private bool TryGetHostCaretScreenRect(out RECT caret)
        {
            caret = default;
            return _hostThreadId != 0 && NativeMethods.TryGetCaretScreenRect(_hostThreadId, out caret);
        }

        // The text caret's position, not the mouse cursor's.
        private bool TryStoreCaretFallbackPosition(string? reason)
    {
        // Use screen coordinates via GetGUIThreadInfo + ClientToScreen
        if (!TryGetHostCaretScreenRect(out RECT caret))
        {
            return false;
        }

        POINT screenPoint = new POINT { X = caret.Left, Y = caret.Top };
        RECT fallbackRect = CreatePointRect(screenPoint);
        StoreCandidatePosInternal(fallbackRect, isAuthoritative: false, source: null);
        return true;
    }

        public void StoreCandidatePos(RECT rect)
        {
            StoreCandidatePosInternal(rect, isAuthoritative: true, source: null);
        }

        // The document is visible (ITfContextView::GetScreenExt succeeded) but
        // the composition range layout isn't ready. Seed a NON-authoritative anchor at the visible doc's
        // top-left so the candidate window never flashes at (0,0); any authoritative anchor (a GetTextExt
        // probe) or the live-caret fallback supersedes it via the priority logic in StoreCandidatePosInternal.
        public void StoreScreenExtFallbackPos(RECT screenExt)
        {
            if (_hasCachedCandidatePos && _hasAuthoritativeCandidatePos) return; // keep the better anchor
            RECT seed = new RECT { Left = screenExt.Left, Top = screenExt.Top, Right = screenExt.Left, Bottom = screenExt.Top };
            StoreCandidatePosInternal(seed, isAuthoritative: false, source: null);
        }

        public void ApplyCandidateWindowLocation()
        {
            // Immediate positioning is a pure cached-or-fallback path.
            // Do not nest a synchronous authoritative refresh inside key-handling.

            int screenWidth = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            // Current main-window size (for boundary-avoidance placement). 0 when the
            // window isn't realized/sized yet → the placement math falls back to anchor + edge-clamp only.
            int winW = 0, winH = 0;
            nint posHwnd = this.Presenter_Candidate?.Hwnd ?? 0;
            if (posHwnd != 0 && NativeMethods.GetWindowRect(posHwnd, out RECT posRect))
            {
                winW = Math.Max(0, posRect.Right - posRect.Left);
                winH = Math.Max(0, posRect.Bottom - posRect.Top);
            }
            bool hasFallbackPoint = TryGetHostCaretScreenRect(out RECT hostCaret);
            POINT fallbackPoint = new POINT { X = hostCaret.Left, Y = hostCaret.Top };
            bool authoritativeCandidatePosDirty = _authoritativeCandidatePosDirty;
            bool hasCachedCandidatePos = _hasCachedCandidatePos;
            bool hasAuthoritativeCandidatePos = _hasAuthoritativeCandidatePos;
            RECT cachedCandidatePos = _cachedCandidatePos;
            bool hasCachedAuthoritativeAnchor = hasCachedCandidatePos && hasAuthoritativeCandidatePos;

            if (ShouldUseCachedAuthoritativeAnchorWhileAuthoritativeRefreshPending(
                authoritativeCandidatePosDirty,
                hasCachedCandidatePos,
                hasAuthoritativeCandidatePos)
                && TryResolveCandidateWindowLocation(cachedCandidatePos, hasCachedCandidatePos, fallbackPoint, hasFallbackPoint, screenWidth, screenHeight, winW, winH, out int cachedX, out int cachedY))
            {
                SetPresenterLocationWithTrace(cachedX, cachedY, null);
                return;
            }

            if (ShouldUseLiveCaretFallbackWhileAuthoritativeRefreshPending(authoritativeCandidatePosDirty, hasFallbackPoint, hasCachedAuthoritativeAnchor))
            {
                // Same boundary-avoidance as the resolved path.
                RECT liveCaret = new RECT { Left = fallbackPoint.X, Top = fallbackPoint.Y, Right = fallbackPoint.X, Bottom = fallbackPoint.Y };
                RECT liveWork = GetWorkAreaForPoint(fallbackPoint.X, fallbackPoint.Y, screenWidth, screenHeight);
                (int liveX, int liveY) = ComputeAnchoredWindowPosition(liveCaret, winW, winH, liveWork);
                SetPresenterLocationWithTrace(liveX, liveY, null);
                return;
            }

            if (!hasCachedCandidatePos)
            {
                TryStoreCaretFallbackPosition(null);
            }

            if (TryResolveCandidateWindowLocation(_cachedCandidatePos, _hasCachedCandidatePos, fallbackPoint, hasFallbackPoint, screenWidth, screenHeight, winW, winH, out int x, out int y))
            {
                SetPresenterLocationWithTrace(x, y, null);
                return;
            }

        }
		internal unsafe Boolean TryEnsureDisplayAttributeGuidAtom (out UInt32 guidAtom)
		{
			guidAtom = this._guidDisplayAttributeAtom;
			if (this._guidDisplayAttributeAtomInitialized && guidAtom != 0)
			{
				return true;
			}

			// COM calls run outside the lock to prevent a potential deadlock
			// COM calls can pump messages, which can cause re-entrancy issues
			Guid clsid = NativeMethods.CLSID_TF_CategoryMgr;
			Guid iid = typeof (ITfCategoryMgr).GUID;
			void* pCategoryMgr;
			Int32 hr = TsfNative.CoCreateInstance (&clsid, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, &iid, &pCategoryMgr);
			if (hr != NativeMethods.S_OK || pCategoryMgr == null)
			{
				return false;
			}

			UInt32 registeredAtom = 0;
			try
			{
				// ITfCategoryMgr::RegisterGUID vtable index = 14
				delegate* unmanaged[Stdcall]< void*, Guid*, UInt32*, Int32 > pRegisterGuid =
					(delegate* unmanaged[Stdcall]<void*, Guid*, UInt32*, Int32>)GetVTableMethod (pCategoryMgr, 14);
				Guid guid = Globals.Guid_DisplayAttribute;
				hr = pRegisterGuid (pCategoryMgr, &guid, &registeredAtom);
				if (hr != NativeMethods.S_OK)
				{
					return false;
				}
			}
			finally
			{
				Marshal.Release ((IntPtr)pCategoryMgr);
			}

			// Only hold the lock briefly for assignment
			lock (this._guidDisplayAttributeAtomLock)
			{
				// Double-check - another thread may have initialized it
				if (this._guidDisplayAttributeAtomInitialized && this._guidDisplayAttributeAtom != 0)
				{
					guidAtom = this._guidDisplayAttributeAtom;
					return true;
				}

				this._guidDisplayAttributeAtom = registeredAtom;
				this._guidDisplayAttributeAtomInitialized = true;
				guidAtom = registeredAtom;
				return true;
			}
		}

		private static unsafe void* GetVTableMethod (void* pInterface, int methodIndex)
		{
			void** vtable = *(void***)pInterface;
			return vtable[methodIndex];
		}


        // Worker callback.
        // Subscription is a PAIR (SubscribeFrameEvents and UnsubscribeFrameEvents), and both halves
        // name the same event and the same delegate. A broken pair is what goes wrong:
        // Activate attaching OnFrameReadyFrame while Deactivate detaches the parameterless
        // event leaks the handler and doubles it on every activation cycle (1 → 1 → 2).

        // The subscribed delegate is a CLOSURE carrying the activation generation it was
        // created for, not the bare method group. The counterexample: an invocation list captured
        // before Deactivate and released after a NEW Activate would deliver its frame into the new
        // presenter. A bool "is delivery active now?" cannot tell that apart
        // from a legitimate delivery — classic ABA — because by release time the flag is true again.
        // The generation in the closure belongs to the dead activation, so the old list is rejected while
        // the current one is not.
        private Action<Messaging.RenderFrame>? _frameReadyHandler;

        internal void SubscribeFrameEvents()
        {
            if (_frameReadySubscribed) return;
            int generation = DeferState_BeginActivation();   // bump + activate, one step under the lock
            _frameReadyHandler = frame => HandleFrameReadyFrame(frame, generation);
            this.Engine_Processor.GetEngine().OnFrameReadyFrame += _frameReadyHandler;
            _frameReadySubscribed = true;
        }

        internal void UnsubscribeFrameEvents()
        {
            if (!_frameReadySubscribed) return;
            if (_frameReadyHandler != null)
            {
                this.Engine_Processor.GetEngine().OnFrameReadyFrame -= _frameReadyHandler;
                _frameReadyHandler = null;
            }
            _frameReadySubscribed = false;
        }

        // Frame-carrying entry point — the object the worker published travels with the
        // notification instead of being re-derived from the global slot. Validated against THIS engine
        // (owner + generation) before it can reach the presenter.
        private void HandleFrameReadyFrame(Messaging.RenderFrame frame, int deliveredForGeneration)
        {
            // Reject FIRST on the activation this delegate belongs to. The engine's
            // owner/epoch check below cannot see this: Deactivate does not advance the composition epoch,
            // so a frame from the previous activation still passes IsFrameCurrent and lands in the new
            // presenter. Nothing else in the chain distinguishes the two activations.
            if (!DeferState_IsCurrentActivation(deliveredForGeneration))
            {
                return;
            }

            var engine = this.Engine_Processor.GetEngine();
            if (!engine.IsFrameCurrent(frame))
            {
                return;
            }
            _deliveredFrame = frame;
            HandleFrameReady();
        }

        // The frame most recently delivered for THIS engine, handed to the presenter so
        // the whole chain does not have to guess from GlobalState.
        private Messaging.RenderFrame? _deliveredFrame;

        // Serialises the deferred-frame state machine. Evaluating readiness
        // BEFORE _pendingFrameReadyAfterFocusSync is written lets the focus-sync flush run in the
        // gap, observe pending=false, return, and then the worker sets pending=true with nobody left to
        // flush it, so the held frame never reaches the presenter until the NEXT keystroke.
        // {evaluate readiness + decide deliver-or-defer + write pending} is one atomic step, and
        // TryFlushDeferredFrameReadyAfterFocusSync takes the same lock for its {check + clear}.
        private readonly object _deferGate = new();

        // Activation state shares the SAME lock as the pending flag. The counterexample:
        // an OnFrameReadyFrame handler captured BEFORE Deactivate unsubscribed could run AFTER
        // Deactivate had cleared pending, and set it back to true: pending resurrected on a torn-down
        // service. Every writer goes through the helpers below, and a
        // late handler cannot set pending once delivery is inactive.
        private bool _deliveryActive;

        /// <summary> Single writer for the deferred-frame flag. All lifecycle paths
        /// (Deactivate / CleanupComposition / focus-loss) route through here instead of touching the
        /// field, so no write can bypass _deferGate.</summary>
        private void DeferState_SetPending(bool value)
        {
            lock (_deferGate)
            {
                if (value && !_deliveryActive)
                {
                    return;
                }
                // …and refuse to arm a wakeup for a composition that has since been
                // cancelled. A handler that passed the epoch check BEFORE the focus-loss Clear
                // can still be inside HandleFrameReady when Clear runs, then set pending afterwards.
                // Serialising {Clear; clear pending} is not enough
                // on its own, because the blocked writer simply runs next. Re-validating the frame HERE,
                // under the same lock the transaction holds, is what closes it: by the time this writer
                // gets the lock the epoch has moved and its frame is dead.
                if (value && !IsDeliveredFrameStillCurrent())
                {
                    return;
                }
                _pendingFrameReadyAfterFocusSync = value;
            }
        }

        /// <summary> Activation transitions, under the same lock. Deactivating also clears pending
        /// atomically so a concurrent late handler cannot re-arm it.</summary>
        private void DeferState_SetDeliveryActive(bool active)
        {
            lock (_deferGate)
            {
                _deliveryActive = active;
                if (!active)
                {
                    _pendingFrameReadyAfterFocusSync = false;
                    // Retiring the activation invalidates every delegate issued under it.
                    _activationGeneration++;
                }
            }
        }

        // Monotonic activation identity. Guarded by _deferGate together with
        // _deliveryActive and the pending flag, so activation state is one consistent tuple.
        private int _activationGeneration;

        /// <summary> Activate and mint the generation the new subscription belongs to —
        /// one step, so no delegate can be issued against a generation that is already retired.</summary>
        private int DeferState_BeginActivation()
        {
            lock (_deferGate)
            {
                _activationGeneration++;
                _deliveryActive = true;
                _pendingFrameReadyAfterFocusSync = false;
                return _activationGeneration;
            }
        }

        /// <summary> Is the frame a wakeup would be armed for still live? A null held frame
        /// means the pending flag is not tied to a specific frame (focus-sync ordering only), which stays
        /// permitted — the deferred flush re-validates before it consumes anything.</summary>
        private bool IsDeliveredFrameStillCurrent()
        {
            var held = _deliveredFrame;
            if (held == null) return true;
            try { return this.Engine_Processor.GetEngine().IsFrameCurrent(held); }
            catch { return true; }
        }

        /// <summary> Read the pending flag under the lock that owns it.</summary>
        private bool DeferState_GetPending() { lock (_deferGate) { return _pendingFrameReadyAfterFocusSync; } }

        /// <summary> Is this delegate's activation still the live one?</summary>
        private bool DeferState_IsCurrentActivation(int generation)
        {
            lock (_deferGate) { return _deliveryActive && _activationGeneration == generation; }
        }


        private void HandleFrameReady()
        {
            bool deliverNow;
            lock (_deferGate)
            {
                // A handler captured before Deactivate's unsubscribe can still arrive here. Once
                // delivery is inactive it must neither deliver nor re-arm pending.
                if (!_deliveryActive)
                {
                    return;
                }
                bool hasPresenter = this.Presenter_Candidate != null;
                bool hasCurrentContext = _currentContext != null;
                bool hasTextLayoutSinkAdvice = _cookieTextLayoutSink != InvalidSinkCookie;
                bool isComposing = this.Engine_Processor.IsComposing();
                deliverNow = CanDeliverFrameReadyToPresenter(hasPresenter, _foregroundFocusActive, hasCurrentContext, hasTextLayoutSinkAdvice, isComposing);

                if (deliverNow)
                {
                    _pendingFrameReadyAfterFocusSync = false;
                }
                else
                {
                    // The FORWARD write goes through the same validation every other
                    // writer uses. Arming pending directly here would skip
                    // the frame-current revalidation and make the "single writer" claim untrue
                    // of the real source. Already inside _deferGate here, and the lock is recursive for
                    // the same thread, so this is the same critical section, not a second one.
                    _pendingFrameReadyAfterFocusSync =
                        hasPresenter && isComposing && IsDeliveredFrameStillCurrent();
                }
            }
            // Presenter notification runs OUTSIDE the lock. Never hold a lock across foreign code.
            if (deliverNow) NotifyPresenterFrameReadyWithTrace(null);
        }

        private const Int32 VK_CONTROL = 0x11;
        private const Int32 VK_MENU = 0x12;
        private const Int32 VK_LWIN = 0x5B;
        private const Int32 VK_RWIN = 0x5C;

        private const UInt32 VK_BACK = 0x08;
        private const UInt32 VK_RETURN = 0x0D;
        private const UInt32 VK_SPACE = 0x20;
        private const UInt32 VK_ESCAPE = 0x1B;
        private const UInt32 VK_PRIOR = 0x21;     // PAGE UP
        private const UInt32 VK_NEXT = 0x22;      // PAGE DOWN
        private const UInt32 VK_UP = 0x26;        // ARROW UP
        private const UInt32 VK_DOWN = 0x28;      // ARROW DOWN
        private const UInt32 VK_A = 0x41;
        private const UInt32 VK_Z = 0x5A;
        private const UInt32 VK_OEM_1 = 0xBA;
        private const UInt32 VK_OEM_4 = 0xDB;
        private const UInt32 VK_OEM_6 = 0xDD;
        private const UInt32 VK_OEM_7 = 0xDE;   // ' / "  — syllable separator (unshifted) while composing
        private const UInt32 VK_OEM_COMMA = 0xBC;
        private const UInt32 VK_OEM_PERIOD = 0xBE;


        private static bool IsModifierPressed(Int32 virtualKey)
        {
            return (NativeMethods.GetKeyState(virtualKey) & 0x8000) != 0;
        }

        /// <summary> A main-row digit with Shift held. It types the symbol on its keycap, so the candidate
        /// window does not take it as a pick.</summary>
        private static bool IsShiftedDigit(UInt32 keyCode) =>
            keyCode >= 0x30 && keyCode <= 0x39 && IsModifierPressed(NativeMethods.VK_SHIFT);

        private static bool IsExplicitCompositionControlKey(UInt32 keyCode, bool isComposing)
        {
            if (!isComposing)
            {
                return false;
            }

            return keyCode == VK_BACK || keyCode == VK_RETURN || keyCode == VK_ESCAPE;
        }

        // Shared positive-whitelist ownership check for both the OnTestKeyDown and the OnKeyDown paths.
        private static bool IsSemanticImeOwnedKey(UInt32 keyCode, bool isShiftPressed, bool isCtrlPressed, bool isAltPressed, bool isWinPressed, bool isComposing)
        {
            return IsSemanticImeOwnedKey(keyCode, isShiftPressed, isCtrlPressed, isAltPressed, isWinPressed, isComposing, false, false, false);
        }

        private static bool IsSemanticImeOwnedKey(UInt32 keyCode, bool isShiftPressed, bool isCtrlPressed, bool isAltPressed, bool isWinPressed, bool isComposing, bool isCandidateSelectionKeyOwned, bool isCandidateNavigationKeyOwned, bool isCandidateCommitKeyOwned)
        {
            if (isCtrlPressed || isAltPressed || isWinPressed)
            {
                return false;
            }

            if (isCandidateSelectionKeyOwned || isCandidateNavigationKeyOwned || isCandidateCommitKeyOwned)
            {
                return true;
            }

            if (keyCode >= VK_A && keyCode <= VK_Z)
            {
                return true;
            }

            if (CompositionProcessorEngine.ClaimsPunctuationKey(keyCode, isShiftPressed))
            {
                return true;
            }

            // Syllable separator ' (VK_OEM_7, unshifted) — owned only WHILE composing,
            // so outside composition a normal apostrophe/quote still passes through to the host.
            if (isComposing && keyCode == VK_OEM_7 && !isShiftPressed)
            {
                return true;
            }

            // Suffix connector '-' (VK_OEM_MINUS, unshifted) — owned only WHILE
            // composing (the hyphen scheme: stem-i genitive etc.). Outside composition a normal
            // hyphen passes to the host. Claimed in BOTH gates: OnTestKeyDown and OnKeyDown both read this.
            if (isComposing && keyCode == 0xBD && !isShiftPressed)
            {
                return true;
            }

            // UNIFIED COMPOSING HOLD — the ownership half.
            //
            // While composing, ordinary letters and
            // symbols must all be caught instead of passed to the host (strict on purpose: a stray key
            // must never interrupt the word); navigation / paging / digits / function keys are excluded
            // because they carry composition-CONTROL semantics.
            //
            // The engine's hold layer catches the symbol keys whose keycap gives no usable text on the
            // current layout. It is DEAD unless those keys are claimed here, because TSF otherwise hands
            // them straight to the host and CompositionProcessorEngine never runs. Claiming them here
            // (only WHILE COMPOSING, so outside composition such a key types normally) is what makes the
            // hold layer take effect.
            //
            // Shares CompositionProcessorEngine.IsOrdinaryTextKey deliberately: one definition, so this
            // claim and the hold can never drift apart.
            if (isComposing && CompositionProcessorEngine.IsOrdinaryTextKey(keyCode))
            {
                return true;
            }

            return IsExplicitCompositionControlKey(keyCode, isComposing);
        }

        // ONE modifier policy for every standalone composing-hotkey claim (Tab).
        // Such a branch must not OR past IsSemanticImeOwnedKey's Ctrl/Alt/Win filter, or
        // Ctrl+Tab is claimed while composing and runs an IME function instead of the
        // host shortcut. Both key paths call this same predicate so they can never disagree.
        internal static bool ComposingHotkeyClaimAllowed(bool isComposing, bool englishMode, bool ctrl, bool alt, bool win)
            => isComposing && !englishMode && !ctrl && !alt && !win;


        /// <summary> Bit 30 of a key message's lParam is the PREVIOUS key state: 1 means the
        /// key was already down, i.e. this arrival is the OS auto-repeating a held key, not a fresh
        /// press. Static: the rule depends on lParam alone.</summary>
        public static bool IsAutoRepeat(nint lParam) => (lParam & 0x40000000) != 0;

        [DllImport("user32.dll")] private static extern nint GetForegroundWindow();

        // A BYREF parameter (`out uint`) needs a marshalling stub, and this assembly is compiled with
        // runtime marshalling disabled (CA1420), so ILC cannot generate one and emits a stub that throws
        // instead.
        //
        // A raw pointer is blittable, needs no stub at all, and is what the byref would have marshalled to.
        // Declared `unsafe` here instead of switching to [LibraryImport], because that attribute needs the
        // containing type to be partial, which TextService is not.
        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
        private static extern unsafe uint GetWindowThreadProcessIdRaw(nint hWnd, uint* pid);

        /// <summary> Thread id of the window, with the process id out of the same call. Safe to
        /// call under NativeAOT; the byref form is not.</summary>
        private static unsafe uint GetWindowThreadAndProcessId(nint hWnd, out uint pid)
        {
            uint local = 0;
            uint tid = GetWindowThreadProcessIdRaw(hWnd, &local);
            pid = local;
            return tid;
        }


        /// <summary> Say WHY a plain letter is being handed to the host, at the moment it
        /// happens. When letters arrive as Latin, the one line a reader needs is the reason, not a
        /// state dump to correlate afterwards.</summary>
        private void LogLetterPassThroughReason(uint keyCode, bool isComposing, bool hasContext)
        {
            string? why = null;
            if (why == null) return;
        }

        private static bool CanOwnKeyAtCurrentBoundary(bool isOwnedByIme, bool hasContext)
        {
            return isOwnedByIme && hasContext;
        }

        private static bool CanCompleteExplicitImeHandling(bool isOwnedByIme, bool hasContext, bool handledByEngine)
        {
            return isOwnedByIme && hasContext && handledByEngine;
        }

        private static bool CanDeliverFrameReadyToPresenter(bool hasPresenter, bool hasForegroundFocus, bool hasCurrentContext, bool hasTextLayoutSinkAdvice, bool isComposing)
        {
            return hasPresenter && hasForegroundFocus && hasCurrentContext && hasTextLayoutSinkAdvice && isComposing;
        }

        private static bool ShouldFlushDeferredFrameReady(bool hasPendingFrameReady, bool hasPresenter, bool hasForegroundFocus, bool hasCurrentContext, bool hasTextLayoutSinkAdvice, bool isComposing)
        {
            return hasPendingFrameReady && CanDeliverFrameReadyToPresenter(hasPresenter, hasForegroundFocus, hasCurrentContext, hasTextLayoutSinkAdvice, isComposing);
        }

        private void TryFlushDeferredFrameReadyAfterFocusSync()
        {
            // {check pending + clear it} under the same lock HandleFrameReady uses to
            // {evaluate + set} it, so the flush cannot observe a stale false and drop the wakeup.
            lock (_deferGate)
            {
                bool hasPresenter = this.Presenter_Candidate != null;
                bool hasCurrentContext = _currentContext != null;
                bool hasTextLayoutSinkAdvice = _cookieTextLayoutSink != InvalidSinkCookie;
                bool isComposing = this.Engine_Processor.IsComposing();

                if (!ShouldFlushDeferredFrameReady(_pendingFrameReadyAfterFocusSync, hasPresenter, _foregroundFocusActive, hasCurrentContext, hasTextLayoutSinkAdvice, isComposing))
                {
                    return;
                }
                _pendingFrameReadyAfterFocusSync = false;
            }
            // Pass the frame we are actually holding. The parameterless overload here would make the
            // presenter keep consuming the frame it parked earlier while a newer one of the SAME owner
            // and epoch is already published, and the display would end on frame 1 with frame 2 live.
            var deferred = _deliveredFrame;
            if (deferred != null) this.Presenter_Candidate!.NotifyFrameReady(deferred);
            else this.Presenter_Candidate!.NotifyFrameReady();
        }

        private bool HasVisibleCandidateSelection()
        {
            return this.Presenter_Candidate?.IsVisible ?? false;
        }

		#endregion Method

		#region ITfKeyEventSink

		// ABI: BOOL = Int32
		public Int32 OnSetFocus (Int32 fForeground) 
		{ 
            if (fForeground == 0) // Lost focus
            {
                // Focus left while Shift may be held (e.g. a click that moves focus) —
                // whatever Shift release follows is not a lone tap.
                _shiftTap.ObserveInterruption();
                _menuComboHeld = false;
                CloseSettingsMenu("keyboard focus lost");
                _foregroundFocusActive = false;
                // A switch-away when the thread no longer has the TSF focus; otherwise the keyboard went to
                // something else inside the host, and the composition and the windows stay as they are.
                if (!IsHostThreadFocused())
                    SwitchAway(null);
            }
            else // Regained focus
            {
                // If the Shift-up landed in another window while we were
                // unfocused, the tracker still thinks Shift is down and would swallow the user's next
                // genuine lone tap. On regain, if Shift is not PHYSICALLY held, hard-reset the tracker.
                if (!IsModifierPressed(NativeMethods.VK_SHIFT)) _shiftTap.Reset();
                _foregroundFocusActive = true;

                TryFlushDeferredFrameReadyAfterFocusSync();
            }
			return NativeMethods.S_OK; 
		}

		public Int32 OnTestKeyDown (nint Context_Context, nuint wParam, nint lParam, out Int32 pfEaten)
		{
			UInt32 Code_Key = (UInt32)wParam;
            _lastObservedVirtualKey = Code_Key;
            _keyTheTestGateSaw = Code_Key;
			// The settings file is read again when it changed (ManjuEngine.PollConfiguration), before the
			// key is looked at, so the key already works under the new settings.
			if (this.Engine_Processor.GetEngine().PollConfiguration().Count > 0) AfterConfigurationRead();
			// THE SETTINGS MENU COMES FIRST. The combination is claimed whether or not a word is being
			// composed and in either input mode; while the menu is open its keys are claimed too, ahead of
			// the candidate window's navigation claim below. OnKeyDown acts on them. The Shift of the
			// combination is reported to the lone-Shift tracker, so releasing it does not toggle the
			// input mode.
			if (IsSettingsMenuCombo(Code_Key) || IsOpenMenuKey(Code_Key))
			{
				_shiftTap.ObserveTestKeyDown(Code_Key);
				// The combination is claimed when this press opens or closes the menu, and only then;
				// a press that can do neither leaves the key to the host.
				pfEaten = IsOpenMenuKey(Code_Key) || SettingsMenuPressActs(Context_Context, lParam) ? 1 : 0;
				return NativeMethods.S_OK;
			}
			// Any other key that is not a modifier ends the press that toggled the menu and closes the
			// menu; the key itself then goes through its normal handling.
			if (!IsModifierKey(Code_Key))
			{
				_menuComboHeld = false;
				if (_settingsMenu.IsOpen) CloseSettingsMenu("another key");
			}
			// Four-column UI navigation key claim (←/→/1-9). Pure predicate; no
			// side effect. If presenter would consume the key, claim ownership so TSF routes to OnKeyDown.
			// Modifier check: running this claim BEFORE the Ctrl/Alt/Win filter in
			// IsSemanticImeOwnedKey swallows Ctrl+←/Ctrl+digit host shortcuts whenever the
			// candidate window is visible, so Ctrl/Alt/Win combinations are not safe on this branch.
			if (this.Presenter_Candidate != null && this.Presenter_Candidate.IsVisible &&
			    !IsModifierPressed(VK_CONTROL) && !IsModifierPressed(VK_MENU) &&
			    !IsModifierPressed(VK_LWIN) && !IsModifierPressed(VK_RWIN) &&
			    !IsShiftedDigit(Code_Key) &&
			    this.Presenter_Candidate.WouldHandleNavigationKey(Code_Key))
			{
				pfEaten = 1;
				return NativeMethods.S_OK;
			}
			bool isShiftPressed = IsModifierPressed(NativeMethods.VK_SHIFT);
			bool isCtrlPressed = IsModifierPressed(VK_CONTROL);
			bool isAltPressed = IsModifierPressed(VK_MENU);
			bool isWinPressed = IsModifierPressed(VK_LWIN) || IsModifierPressed(VK_RWIN);
			// Observe here, because OnTestKeyDown is the only callback a strict host guarantees
			// for keys it does not eat (EN-mode letters), so this is where "another key during Shift"
			// must be latched.
			_shiftTap.ObserveTestKeyDown(Code_Key);
			bool isComposing = this.Engine_Processor.IsComposing();
            bool hasVisibleCandidateSelection = this.HasVisibleCandidateSelection();
            bool ownsCandidateSelectionKey = this.Engine_Processor.CanSelectCandidateWithDigit(Code_Key, hasVisibleCandidateSelection);
            bool ownsCandidateNavigationKey = this.Engine_Processor.CanNavigateCandidatesWithKey(Code_Key, hasVisibleCandidateSelection);
            bool ownsCandidateCommitKey = (Code_Key == VK_SPACE) && (hasVisibleCandidateSelection || isShiftPressed);
			bool isOwnedByIme = !_englishMode && IsSemanticImeOwnedKey(Code_Key, isShiftPressed, isCtrlPressed, isAltPressed, isWinPressed, isComposing, ownsCandidateSelectionKey, ownsCandidateNavigationKey, ownsCandidateCommitKey);
            bool hasContext = Context_Context != 0;
            bool canOwnAtCurrentBoundary = CanOwnKeyAtCurrentBoundary(isOwnedByIme, hasContext);

			if (!isOwnedByIme || !canOwnAtCurrentBoundary) LogLetterPassThroughReason(Code_Key, isComposing, hasContext);

			// Tab toggles dictionary-window focus. OnTestKeyDown must CLAIM it (pfEaten=1)
			// or TSF routes Tab to the host, which terminates the composition (an automatic commit). Claim it ONLY
			// while composing AND a real dictionary window exists (module available) — otherwise let Tab
			// pass through to the host so we never swallow it toward a window that isn't there.
			// The standalone claim goes through ComposingHotkeyClaimAllowed: without it
			// this OR-branch bypasses the modifier filter and Ctrl+Tab is swallowed.
			bool fnClaimOk = ComposingHotkeyClaimAllowed(isComposing, _englishMode, isCtrlPressed, isAltPressed, isWinPressed);
			bool ownsDictTab = Code_Key == 0x09 && fnClaimOk
				&& this.Engine_Processor.GetEngine().DictionaryModule?.IsAvailable == true;
			// Shift must be claimed here.
			//
			// Microsoft's contract: ITfKeystrokeMgr calls OnKeyDown ONLY when OnTestKeyDown returned
			// pfEaten = TRUE (and OnKeyUp only when OnTestKeyUp did). OnKeyDown is where _shiftIsDown is
			// latched and OnKeyUp is where the lone-Shift tap runs the transliteration escape and the
			// manju/EN mode toggle. With Shift (0x10 / 0xA0 / 0xA1) in NEITHER OnTestKeyDown nor OnTestKeyUp, a host that
			// follows the contract strictly fires neither callback and the whole Shift feature is dead;
			// it would work only on a host that delivers the events anyway.
			//
			// Claiming it here makes delivery contractual. It does NOT make us eat the key: OnKeyDown
			// leaves Shift un-owned so the ownership check returns pfEaten = 0, and OnKeyUp explicitly
			// keeps pfEaten = 0 (eating a modifier's up-stroke would strand the host's Shift state). The
			// host therefore still sees Shift normally; we merely get told about it.
			bool ownsShiftObservation = (Code_Key == 0x10 || Code_Key == 0xA0 || Code_Key == 0xA1);

			pfEaten = (canOwnAtCurrentBoundary || ownsDictTab || ownsShiftObservation) ? 1 : 0;  // TRUE / FALSE
			return NativeMethods.S_OK;
		}

		public Int32 OnKeyDown (nint Ptr_Context, nuint wParam, nint lParam, out Int32 pfEaten)
		{
			    UInt32 Code_Key = (UInt32)wParam;
                _lastObservedVirtualKey = Code_Key;
                // A host that never calls OnTestKeyDown (Edge and other Chromium hosts) would never read
                // the settings file again, so a key the test gate did not see reads it here, before the key
                // is looked at. A key the test gate saw had the file read there, so a host that calls the
                // test gate (Notepad, Word) reads it once for the key.
                bool testGateSawKey = _keyTheTestGateSaw == Code_Key;
                _keyTheTestGateSaw = null;
                if (!testGateSawKey && this.Engine_Processor.GetEngine().PollConfiguration().Count > 0) AfterConfigurationRead();
                // Belt-and-braces observation for hosts
                // that skip OnTestKeyDown; the contract-guaranteed latch lives there.
                _shiftTap.ObserveKeyDown(Code_Key);

			    // A host that never calls OnTestKeyDown or OnTestKeyUp (Edge and other Chromium
			    // hosts) never reports the release that clears _menuComboHeld. A fresh press of any other
			    // key, a modifier included, starts a new press; the OS repeats only the ` while the
			    // combination is held, so holding it still toggles the menu once.
			    if (!IsAutoRepeat(lParam) && Code_Key != SettingsMenu.VK_OEM_3) _menuComboHeld = false;

			    // THE SETTINGS MENU. The combination toggles it once per press: a key the OS repeats while it
			    // is held does not toggle again. While the menu is open, a menu key goes to the menu and
			    // nowhere else.
			    if (IsSettingsMenuCombo(Code_Key))
			    {
				    // Eaten when the press opened or closed the menu; a repeat, when its press did.
				    bool acted = IsAutoRepeat(lParam) ? _menuComboHeld : ToggleSettingsMenu("key gate", ContextFromPointer(Ptr_Context));
				    pfEaten = acted ? 1 : 0;
				    return NativeMethods.S_OK;
			    }
			    if (IsOpenMenuKey(Code_Key))
			    {
				    HandleSettingsMenuKey(Code_Key);
				    pfEaten = 1;
				    return NativeMethods.S_OK;
			    }

			    // Four-column UI navigation key consumption:
			    // ↑↓ outer, ←→ inner, 0-9 (main+numpad) explicit pick.
			    // Same modifier check as the OnTestKeyDown claim, because the two key paths must agree.
			    if (this.Presenter_Candidate != null && this.Presenter_Candidate.IsVisible &&
			        !IsModifierPressed(VK_CONTROL) && !IsModifierPressed(VK_MENU) &&
			        !IsModifierPressed(VK_LWIN) && !IsModifierPressed(VK_RWIN) &&
			        !IsShiftedDigit(Code_Key) &&
			        this.Presenter_Candidate.TryHandleNavigationKey(Code_Key))
			    {
			        pfEaten = 1;

			        // If presenter requested auto-commit (last-position pick),
			        // trigger commit-to-host with the mixed commit string.
			        if (this.Presenter_Candidate.TakePendingAutoCommit())
			        {
			            NativeMethods.ITfContext? Context_Auto = null;
			            if (Ptr_Context != 0)
			            {
			                unsafe { Context_Auto = ComInterfaceMarshaller<NativeMethods.ITfContext>.ConvertToManaged((void*)Ptr_Context); }
			            }
			            if (Context_Auto != null)
			            {
			                var engine = this.Engine_Processor.GetEngine();
			                string mixedCommit = engine.BuildCommitString(engine.GetRawBuffer(), unselectedAsPrimary: true); // default Mongolian for unpicked
			                if (!string.IsNullOrEmpty(mixedCommit))
			                {
			                    var AutoCommitSession = new KeyHandlerEditSession(Context_Auto, this, mixedCommit, true);
			                    Context_Auto.RequestEditSession(this.Id_Client, AutoCommitSession,
			                        NativeMethods.TF_ES_READWRITE | NativeMethods.TF_ES_SYNC, out int hrSession);
			                    this.Engine_Processor.Clear();
			                    this.ResetCandidateAnchoringCycle();
			                }
			            }
			        }
			        return NativeMethods.S_OK;
			    }

			    NativeMethods.ITfContext? Context_Context = null;
			    if (Ptr_Context != 0)
			    {
				    unsafe
				    {
					    Context_Context = ComInterfaceMarshaller<NativeMethods.ITfContext>.ConvertToManaged ((void*)Ptr_Context);
				    }
			    }

			    Boolean Flag_Shift = IsModifierPressed (NativeMethods.VK_SHIFT);
                Boolean Flag_Control = IsModifierPressed (VK_CONTROL);
                Boolean Flag_Alt = IsModifierPressed (VK_MENU);
                Boolean Flag_Win = IsModifierPressed (VK_LWIN) || IsModifierPressed (VK_RWIN);
                Boolean Flag_Composing = this.Engine_Processor.IsComposing ();
                Boolean Flag_HasVisibleCandidateSelection = this.HasVisibleCandidateSelection ();
                Boolean Flag_OwnsCandidateSelectionKey = this.Engine_Processor.CanSelectCandidateWithDigit (Code_Key, Flag_HasVisibleCandidateSelection );
                Boolean Flag_OwnsCandidateNavigationKey = this.Engine_Processor.CanNavigateCandidatesWithKey (Code_Key, Flag_HasVisibleCandidateSelection );
                Boolean Flag_OwnsCandidateCommitKey = (Code_Key == VK_SPACE) && (Flag_HasVisibleCandidateSelection || Flag_Shift);
                // Mirror OnTestKeyDown's dict-Tab ownership. OnTestKeyDown claims Tab via a
                // separate ownsDictTab OR, but IsSemanticImeOwnedKey excludes Tab (owns only BACK/RETURN/ESCAPE),
                // so OnKeyDown would bail at the ownership check before the engine ran and the focus toggle
                // would never execute. Own it identically here: composing + a real dict window.
                // Same shared modifier policy as OnTestKeyDown.
                Boolean Flag_FnClaimOk = ComposingHotkeyClaimAllowed(Flag_Composing, _englishMode, Flag_Control, Flag_Alt, Flag_Win);
                Boolean Flag_OwnsDictTab = Code_Key == 0x09 && Flag_FnClaimOk
                    && this.Engine_Processor.GetEngine().DictionaryModule?.IsAvailable == true;
                Boolean Flag_IsOwnedByIme = (!_englishMode && IsSemanticImeOwnedKey (Code_Key, Flag_Shift, Flag_Control, Flag_Alt, Flag_Win, Flag_Composing, Flag_OwnsCandidateSelectionKey, Flag_OwnsCandidateNavigationKey, Flag_OwnsCandidateCommitKey)) || Flag_OwnsDictTab;
                Boolean Flag_HasContext = Context_Context != null;
                Boolean Flag_CanOwnAtBoundary = CanOwnKeyAtCurrentBoundary (Flag_IsOwnedByIme, Flag_HasContext);


                if (!Flag_CanOwnAtBoundary)
                {
                    // Tab goes to the host, and a Chromium host ends the composition on it by
                    // committing the placeholder itself: the word ends here first, with the mixed string
                    // (TextService.ChromiumHost.cs).
                    if (ChromiumHost.EndsWordBeforePassingKey(Code_Key, Flag_Composing, Flag_Composing && IsChromiumHost(Context_Context)))
                        EndWordBeforeHost(switchAway: false, null);
                    pfEaten = 0;  // FALSE
                    return NativeMethods.S_OK;
                }

                // Handle input via the engine
                // only a bare Space (no Shift) counts as the candidate-select / commit key
                Boolean Flag_Handled = this.Engine_Processor.OnKeyDown (Code_Key, Flag_Shift, Flag_OwnsCandidateSelectionKey || (Flag_OwnsCandidateCommitKey && !Flag_Shift), Flag_OwnsCandidateNavigationKey);

                // Tab focus-toggle: the engine flipped main↔dict focus. Repaint BOTH
                // windows (main dim overlay + KW highlight) and return — do NOT fall into the commit/live-update
                // edit session below; the buffer is unchanged, so a focus toggle must never mutate host text.
                if (this.Engine_Processor.ConsumePendingFocusToggleRepaint())
                {
                    NotifyPresenterFrameReadyWithTrace(null);
                    pfEaten = 1;
                    return NativeMethods.S_OK;
                }

			
			    if (CanCompleteExplicitImeHandling (Flag_IsOwnedByIme, Flag_HasContext, Flag_Handled))
			    {
				    pfEaten = 1;  // TRUE
				
				    var editContext = Context_Context;
				    if (editContext == null)
				    {
					    pfEaten = 0;  // FALSE
					    return NativeMethods.S_OK;
				    }
				
				    string? selected = this.Engine_Processor.GetSelectedCandidate();

				    // PgUp/PgDn page the candidate list. Its own channel, because a
				    // page's edge behaviour differs from an arrow step's silent clamp.
				    int pageDelta = Engine_Processor.ConsumePageDelta();
				    if (pageDelta != 0 && Presenter_Candidate != null)
				    {
				        Presenter_Candidate.PagePreviewViewport(pageDelta);
				    }

				    // Handle candidate navigation delta from engine
				    int navDelta = Engine_Processor.ConsumeNavigationDelta();
				    if (navDelta != 0 && Presenter_Candidate != null)
				    {
                        if (navDelta == int.MinValue)
                        {
                            // [ROTATION-DIAG] Repaint-only signal (rotation mode changed)
                            Presenter_Candidate.ForceRepaint();
                        }
                        else
                        {
				            Presenter_Candidate.AdjustSelectedIndex(navDelta);
                        }
				    }
				    if (selected != null)
				    {
					    CommitSelection (editContext, selected, Code_Key == VK_RETURN, NativeMethods.TF_ES_SYNC, null);
				    }
				    else
				    {
					    // Live composition uses mixed BuildCommitString so host text
					    // dynamically reflects "blank-until-selected" — raw Latin where user hasn't picked,
					    // Mongolian where they have. This avoids a jarring jump at commit time.
					    var r0047EngineLive = this.Engine_Processor.GetEngine();
					    var compStr = r0047EngineLive.BuildCommitString(r0047EngineLive.GetRawBuffer());
					    if (string.IsNullOrEmpty(compStr)) compStr = this.Engine_Processor.GetCompositionString();
                        // No synchronous GetCandidates/Update_Candidates here: candidates arrive with the worker's frame event.

					    // While composing, the host's edit area holds only a **small placeholder** (·), not the
					    // full input string. Writing the full string would collapse the selection to its end on every
					    // key, dragging the host caret along, which is painful in Word's large vertical type and makes
					    // the window position drift. An East Asian IME keeps the host caret essentially still until
					    // commit, and this matches that. The Preview/Candidate windows carry the whole visualisation
					    // of the input sequence. On commit the final string overwrites the placeholder; an
					    // external terminate goes through OnCompositionTerminated, which swaps the placeholder back to the
					    // mixed string, because the placeholder must never land in the document.
					    string hostComposingText = string.IsNullOrEmpty(compStr) ? compStr : ComposingHostPlaceholder;
					    // The placeholder is constant during composing → once it's in the host
					    // range, every further per-key Update is an idempotent no-op. Skipping it avoids the
					    // per-key SYNC lock grab (TF_E_SYNCHRONOUS under fast typing) AND the per-key host
					    // re-layout (flicker). First write / any failed write still goes through.
					    bool placeholderRepeat = this._hostPlaceholderWritten
					        && hostComposingText == ComposingHostPlaceholder
					        && this.GetComposition() != null;
					    if (!placeholderRepeat)
					    {
					        var Session_Edit = new KeyHandlerEditSession (editContext, this, hostComposingText, false);
                            int hr = editContext.RequestEditSession (this.Id_Client, Session_Edit, NativeMethods.TF_ES_READWRITE | NativeMethods.TF_ES_SYNC, out int hrSession);
                            if (hr != NativeMethods.S_OK) { this._hostPlaceholderWritten = false; }
                            else if (hrSession != NativeMethods.S_OK) { this._hostPlaceholderWritten = false; }
                            else this._hostPlaceholderWritten = (hostComposingText == ComposingHostPlaceholder);
					    }
					    // A word is being typed: in a Chromium host, the hooks that end it before the host
					    // commits the placeholder itself (TextService.ChromiumHost.cs). Set once per thread.
					    ArmChromiumHostHooks(editContext);

                        this.MarkAuthoritativeCandidatePosDirtyAfterMutation();
                        // Immediate path may use cached position or live caret fallback,
                        // but must not trigger nested synchronous authoritative refresh.
                        this.ApplyImmediateCandidateWindowLocationAfterMutation();

					    // Candidate list: updated asynchronously.
                        // Do NOT update candidates synchronously here.
                        // Waiting for worker event.
				    }
			    }
			    else
			    {
				    pfEaten = 0;  // FALSE
			    }
			    return NativeMethods.S_OK;
		}

		/// <summary> The end of a word, for a commit key, for ESC and for a switch-away: <paramref name="selected"/>
		/// is made into the text to commit and written into the composition range, the composition ends,
		/// and then the engine is cleared and every IME window closed. A key's edit session is synchronous
		/// (<paramref name="sessionMode"/> TF_ES_SYNC). A switch-away's is asynchronous (TF_ES_ASYNC) and
		/// names the composition it ends (<paramref name="leftBehind"/>). The clearing and the closing do not
		/// wait for the session.</summary>
		private void CommitSelection (NativeMethods.ITfContext? editContext, string selected, bool enterKey, uint sessionMode, NativeMethods.ITfComposition? leftBehind)
		{
			// COMMIT string: picked positions = their chosen
			// Mongolian; UNPICKED positions = PRIMARY (default Mongolian):
			// Space with no pick commits the full default set (top; Enter→as-typed, see split below)
			// candidate), NOT raw Latin. (unselectedAsPrimary:true = the commit view; the
			// live-composition path below keeps raw Latin for unpicked positions.)
			var r0047Engine = this.Engine_Processor.GetEngine();
			// Enter commits exactly what has been selected, no more:
			// commit the composition AS TYPED — picked positions → their chosen Mongolian,
			// UNPICKED positions → raw Latin (unselectedAsPrimary:false). Space (and any other
			// commit trigger) = standard commit: unpicked → PRIMARY default Mongolian
			// (unselectedAsPrimary:true).
			// The 2nd-' force-commit wants Enter (as-typed) semantics too.
			bool commitAsTyped = enterKey || this.Engine_Processor.ConsumeForceCommitAsTyped();
			string r0047Mixed = r0047Engine.BuildCommitString(r0047Engine.GetRawBuffer(), unselectedAsPrimary: !commitAsTyped);
			if (!string.IsNullOrEmpty(r0047Mixed)) selected = r0047Mixed;
			// Force-commit symbol (plain apostrophe): append the literal AFTER the
			// committed composition so the host receives e.g. ᠠ' (commit current + append '). One-shot.
			string? trailLit = this.Engine_Processor.TakePendingTrailingLiteral();
			if (!string.IsNullOrEmpty(trailLit)) selected = selected + trailLit;
			// commit the selected candidate
			bool isSelectedCandidateCommit = this.Engine_Processor.IsSelectedCandidateCommitPending ();
			if (editContext != null)
			{
				var Session_Edit = new KeyHandlerEditSession (editContext, this, selected, true, isSelectedCandidateCommit, leftBehind);
				editContext.RequestEditSession (this.Id_Client, Session_Edit, NativeMethods.TF_ES_READWRITE | sessionMode, out int hrSession);
			}

			this.Engine_Processor.Clear ();
			this.ResetCandidateAnchoringCycle(); // also hides KW (see method)
			// No fallback is seeded here: a fallback seeded right after reset could dominate
			// if the host does not quickly provide authoritative geometry. The next composition
			// cycle starts clean; ApplyCandidateWindowLocation() seeds a fallback on demand
			// only if no anchor exists when positioning is needed.
		}

		// OnKeyUp is delivered only when this returns TRUE.
		// The lone-Shift tap (transliteration escape while composing / manju-EN toggle when idle) lives in
		// OnKeyUp, so Shift must be claimed here or that whole feature depends on the host being lenient.
		// Claiming does not eat it — OnKeyUp keeps pfEaten = 0 so the host's Shift state stays consistent.
		// Every other key-up stays unclaimed.
		public Int32 OnTestKeyUp (nint Context_Context, nuint wParam, nint lParam, out Int32 pfEaten)
		{
			uint code = (uint)wParam;
			// Releasing any key of Ctrl+Shift+` ends the press that toggled the settings menu.
			if (code == SettingsMenu.VK_OEM_3 || code == 0x10 || code == 0x11 || (code >= 0xA0 && code <= 0xA3))
				_menuComboHeld = false;
			pfEaten = (code == 0x10 || code == 0xA0 || code == 0xA1) ? 1 : 0;
			return NativeMethods.S_OK;
		}
		public Int32 OnKeyUp (nint Context_Context, nuint wParam, nint lParam, out Int32 pfEaten)
		{
			pfEaten = 0;  // never eat the up — would leave the host's Shift state stuck
			uint code = (uint)wParam;
			// The tracker decides; non-shift up-strokes only feed its observation.
			bool loneShiftTap = _shiftTap.OnKeyUp(code);
			if (code == 0x10 || code == 0xA0 || code == 0xA1)
			{
				// lone Shift (down+up, no other key/interruption between).
				if (loneShiftTap)
				{
					// BOTH Shift keys carry the transliteration escape; **both are treated alike**:
					//   · while composing (manju) → escape / specialise the preceding letter (ApplyShiftEscape). The
					//     check makes it a no-op when the preceding letter has no adapter form, so the learner state stays protected (only letters like k/g/h/r move).
					//   · in the empty state (not composing) → toggle between Manchu and Latin (ToggleInputMode).
					// (The ' key is the ZWNJ separator.)
					bool composing = this.Engine_Processor.IsComposing();
					if (!_englishMode && composing)
					{
						this.Engine_Processor.ApplyShiftEscape();
					}
					else
					{
						ToggleInputMode(); // flip cache + write compartment + refresh tray
					}
				}
			}
			return NativeMethods.S_OK;
		}
		public Int32 OnPreservedKey (nint Context_Context, in Guid rguid, out Int32 pfEaten)
		{
			if (rguid == Globals.Guid_PreservedKey_SettingsMenu && _settingsMenuExposed)
			{
				// The Shift of the combination is held; releasing it must not count as a lone Shift tap.
				_shiftTap.ObserveInterruption();
				// Eaten when the press opened or closed the menu.
				pfEaten = ToggleSettingsMenu("preserved key", ContextFromPointer(Context_Context)) ? 1 : 0;
				return NativeMethods.S_OK;
			}
			pfEaten = 0;
			return NativeMethods.S_OK;
		}

		// ══════════════════════════════════════════════════════════════════════════════════════════
		// THE SETTINGS MENU (Ctrl+Shift+`).
		//
		// The combination is registered with ITfKeystrokeMgr::PreserveKey while the input method is
		// active and is also claimed in both key gates, so it reaches the menu whichever way TSF
		// delivers it. One physical press toggles the menu once: the toggle sets _menuComboHeld, and
		// releasing a key of the combination clears it. The menu's state and keys are SettingsMenu, its
		// rows and their values come from ManjuEngine.SettingsMenuRows, and it is drawn in
		// SettingsMenuWindow.
		// ══════════════════════════════════════════════════════════════════════════════════════════
		internal const uint TF_MOD_CONTROL = 0x0002;
		internal const uint TF_MOD_SHIFT = 0x0004;

		/// <summary> The key the settings menu preserves: Ctrl+Shift and VK_OEM_3.</summary>
		internal static readonly TF_PRESERVEDKEY SettingsMenuKey =
			new TF_PRESERVEDKEY { uVKey = SettingsMenu.VK_OEM_3, uModifiers = TF_MOD_CONTROL | TF_MOD_SHIFT };

		/// <summary> The description registered with the preserved key.</summary>
		internal const string SettingsMenuKeyDescription = "Manju IME settings";

		private readonly SettingsMenu _settingsMenu = new SettingsMenu();
		private SettingsMenuWindow? _settingsMenuWindow;
		private bool _menuComboHeld;

		/// <summary> Whether the settings menu is exposed: its key claimed in both gates and
		/// preserved, the menu opened. From <see cref="Exposure.SettingsMenu"/>; tests set it both ways.</summary>
		private bool _settingsMenuExposed = Exposure.SettingsMenu;
		private bool _menuKeyPreserved;


		/// <summary> Ctrl+Shift+VK_OEM_3 with neither Alt nor Win held.</summary>
		private bool IsSettingsMenuCombo(uint keyCode) =>
			_settingsMenuExposed && keyCode == SettingsMenu.VK_OEM_3
			&& IsModifierPressed(VK_CONTROL) && IsModifierPressed(NativeMethods.VK_SHIFT)
			&& !IsModifierPressed(VK_MENU) && !IsModifierPressed(VK_LWIN) && !IsModifierPressed(VK_RWIN);

		/// <summary> A key the open menu acts on, pressed without Ctrl, Alt or Win.</summary>
		private bool IsOpenMenuKey(uint keyCode) =>
			_settingsMenu.IsOpen && SettingsMenu.IsMenuKey(keyCode)
			&& !IsModifierPressed(VK_CONTROL) && !IsModifierPressed(VK_MENU)
			&& !IsModifierPressed(VK_LWIN) && !IsModifierPressed(VK_RWIN);

		/// <summary> Shift, Ctrl, Alt, either Win key, Caps Lock.</summary>
		private static bool IsModifierKey(uint keyCode) =>
			keyCode == 0x10 || keyCode == 0x11 || keyCode == 0x12 || keyCode == 0x14
			|| (keyCode >= 0xA0 && keyCode <= 0xA5) || keyCode == 0x5B || keyCode == 0x5C;

		private unsafe void PreserveSettingsMenuKey(NativeMethods.ITfKeystrokeMgr keystrokeMgr)
		{
			if (_menuKeyPreserved || !_settingsMenuExposed) return;
			try
			{
				int hr;
				fixed (char* desc = SettingsMenuKeyDescription)
				{
					hr = keystrokeMgr.PreserveKey(this.Id_Client, Globals.Guid_PreservedKey_SettingsMenu, SettingsMenuKey,
						desc, (uint)SettingsMenuKeyDescription.Length);
				}
				_menuKeyPreserved = hr == NativeMethods.S_OK;
			}
			catch (Exception)
			{
			}
		}

		private void UnpreserveSettingsMenuKey(NativeMethods.ITfKeystrokeMgr keystrokeMgr)
		{
			if (!_menuKeyPreserved) return;
			try
			{
				keystrokeMgr.UnpreserveKey(Globals.Guid_PreservedKey_SettingsMenu, SettingsMenuKey);
			}
			catch (Exception)
			{
			}
			_menuKeyPreserved = false;
		}

		/// <summary> Open or close the menu, once per physical press of the combination.</summary>
		private bool ToggleSettingsMenu(string source, NativeMethods.ITfContext? context)
		{
			if (_menuComboHeld)
			{
				return true;
			}
			if (_settingsMenu.IsOpen)
			{
				_menuComboHeld = true;
				CloseSettingsMenu(source);
				return true;
			}
			// Returns whether the press opened or closed the menu. A closed menu opens at a place the
			// host gives (TryGetSettingsMenuAnchor). Where the host gives none, the press does nothing and its
			// key is left to the host.
			POINT? anchor = null;
			var presenter = this.Presenter_Candidate;
			if (presenter != null)
			{
				if (!TryGetSettingsMenuAnchor(presenter, context, out POINT at))
				{
					return false;
				}
				anchor = at;
			}
			_settingsMenu.Open();
			if (!ShowSettingsMenu(anchor)) return false;
			_menuComboHeld = true;
			return true;
		}

		/// <summary> Would this press of the combination open or close the menu? It would if it
		/// already did (the key repeating while held is the same press), if the menu is open, and if the host
		/// gives the closed menu a place. The repeats of a press that did nothing do nothing. The test gate
		/// claims the combination exactly when this holds; ToggleSettingsMenu acts exactly then.</summary>
		private bool SettingsMenuPressActs(nint pic, nint lParam)
		{
			if (_menuComboHeld) return true;
			if (IsAutoRepeat(lParam)) return false;
			if (_settingsMenu.IsOpen) return true;
			var presenter = this.Presenter_Candidate;
			return presenter == null || TryGetSettingsMenuAnchor(presenter, ContextFromPointer(pic), out _);
		}

		private static unsafe NativeMethods.ITfContext? ContextFromPointer(nint pic) =>
			pic == 0 ? null : ComInterfaceMarshaller<NativeMethods.ITfContext>.ConvertToManaged((void*)pic);

		private void CloseSettingsMenu(string reason)
		{
			if (!_settingsMenu.IsOpen) return;
			_settingsMenu.Close();
			_settingsMenuWindow?.Hide();
		}

		private void HandleSettingsMenuKey(uint keyCode)
		{
			var (what, row) = _settingsMenu.OnKey(keyCode);
			if (what == SettingsMenu.Outcome.Closed)
			{
				_settingsMenuWindow?.Hide();
				return;
			}
			if (what == SettingsMenu.Outcome.Cycle) ApplySettingsMenuRow(row);
			ShowSettingsMenu();
		}

		/// <summary> Move one row to its next value and let the candidate window follow it.</summary>
		private void ApplySettingsMenuRow(int row)
		{
			bool changed = this.Engine_Processor.GetEngine().CycleSettingsMenuRow(row);
			if (!changed) return;
			// The shown Latin and the separator, apostrophe and connector marks are recomputed.
			try { this.Presenter_Candidate?.RefreshUnitDisplay(); } catch { }
			// The dictionary window is shown or hidden with the next frame; during a composition that
			// frame is asked for now.
			if (row == ManjuEngine.SettingsMenuRowCount - 1 && this.Engine_Processor.IsComposing())
				NotifyPresenterFrameReadyWithTrace(null);
		}

		/// <summary> Draw the menu with its current values. Needs the candidate window's presenter,
		/// which owns the theme colours; without one the menu keeps its state and draws nothing.</summary>
		private bool ShowSettingsMenu(POINT? anchor = null)
		{
			var presenter = this.Presenter_Candidate;
			if (presenter == null || !_settingsMenu.IsOpen) return _settingsMenu.IsOpen;
			var lines = SettingsMenu.Lines(this.Engine_Processor.GetEngine().SettingsMenuRows());
			_settingsMenuWindow ??= new SettingsMenuWindow { OnTick = PollWhileMenuOpen };
			// Opened, the menu goes to the place it was given. Drawn again, it follows the candidate
			// window while that is on screen, and otherwise stays where it is. Returns whether it is open.
			if (anchor == null && TryGetCandidateWindowAnchor(presenter, out POINT at)) anchor = at;
			if (!_settingsMenuWindow.Show(lines, _settingsMenu.Highlight, presenter.PanelColors(), anchor))
			{
				// No place the host gave, and no window yet that could stay where it is: the menu does not
				// open, so no key is claimed for a menu nobody can see.
				_settingsMenu.Close();
			}
			return _settingsMenu.IsOpen;
		}

		/// <summary> While the settings menu is open, its window's timer looks at the settings file too.</summary>
		private void PollWhileMenuOpen()
		{
			if (this.Engine_Processor.GetEngine().PollConfiguration().Count > 0) AfterConfigurationRead();
		}

		/// <summary> The settings file changed what is shown: the candidate window redraws its units, and
		/// the settings menu, when open, shows the new values.</summary>
		private void AfterConfigurationRead()
		{
			try { this.Presenter_Candidate?.RefreshUnitDisplay(); } catch { }
			if (_settingsMenu.IsOpen) ShowSettingsMenu();
		}

		// The top-left corner the menu opens at: the first of these places the host gives. Right of the
		// candidate window while that is on screen; under the selection (the insertion point when nothing is
		// selected), measured in a read-only edit session; under the host thread's caret; inside the top-left
		// corner of the document's screen extent, or of its window. SettingsMenuWindow keeps the menu in the
		// work area. Without any of them there is no anchor: never the screen's origin, another program's
		// caret or the mouse pointer.
		private bool TryGetSettingsMenuAnchor(CandidateListUIPresenter presenter, NativeMethods.ITfContext? context, out POINT anchor)
		{
			if (TryGetCandidateWindowAnchor(presenter, out anchor)) return true;
			NativeMethods.ITfContextView? view = null;
			try
			{
				if (context != null && context.GetActiveView(out NativeMethods.ITfContextView v) == NativeMethods.S_OK) view = v;
			}
			catch (Exception) { }
			if (view != null && TryGetSelectionExtent(context!, view, out RECT selection))
			{
				anchor = new POINT { X = selection.Left, Y = selection.Bottom + 4 };
				return true;
			}
			if (TryGetHostCaretScreenRect(out RECT caret))
			{
				anchor = new POINT { X = caret.Left, Y = caret.Bottom + 4 };
				return true;
			}
			if (view != null && TryGetDocumentRect(view, out RECT document))
			{
				anchor = new POINT { X = document.Left + 8, Y = document.Top + 8 };
				return true;
			}
			anchor = default;
			return false;
		}

		// Right of the candidate window, while that is on screen.
		private static bool TryGetCandidateWindowAnchor(CandidateListUIPresenter presenter, out POINT anchor)
		{
			if (presenter.IsVisible && presenter.Hwnd != 0 && NativeMethods.GetWindowRect(presenter.Hwnd, out RECT rc))
			{
				anchor = new POINT { X = rc.Right + 8, Y = rc.Top };
				return true;
			}
			anchor = default;
			return false;
		}

		// The selection's rectangle on screen. A synchronous session is for keystroke handling, which
		// is where the menu opens; a host that refuses it, or has not laid the text out, gives nothing here.
		private bool TryGetSelectionExtent(NativeMethods.ITfContext context, NativeMethods.ITfContextView view, out RECT extent)
		{
			extent = default;
			try
			{
				var session = new SelectionExtentEditSession(context, view);
				int hr = context.RequestEditSession(this.Id_Client, session, NativeMethods.TF_ES_READ | NativeMethods.TF_ES_SYNC, out int hrSession);
				if (hr != NativeMethods.S_OK || hrSession != NativeMethods.S_OK || session.Extent is not RECT rect)
				{
					return false;
				}
				extent = rect;
				return true;
			}
			catch (Exception)
			{
				return false;
			}
		}

		// The document's rectangle on screen: the view's screen extent, else its window's rectangle.
		// GetScreenExt gives {0,0,0,0} for a document not on screen, and GetWnd may give no window.
		private static bool TryGetDocumentRect(NativeMethods.ITfContextView view, out RECT rect)
		{
			try
			{
				if (view.GetScreenExt(out rect) == NativeMethods.S_OK && rect.Right > rect.Left && rect.Bottom > rect.Top) return true;
				if (view.GetWnd(out nint hwnd) == NativeMethods.S_OK && hwnd != 0 && NativeMethods.GetWindowRect(hwnd, out rect)
					&& rect.Right > rect.Left && rect.Bottom > rect.Top) return true;
			}
			catch (Exception) { }
			rect = default;
			return false;
		}

		#endregion ITfKeyEventSink

		#region input mode (conversion-mode compartment) + tray langbar button

		// Flip the input mode (manju <-> EN). Centralizes everything a toggle must do
		// regardless of trigger (lone-Shift OR a tray-button click): update the in-memory cache,
		// persist to the conversion-mode compartment, refresh the tray icon, and — when switching to
		// EN mid-composition — drop the in-progress Manchu cleanly.
		private void ToggleInputMode () => SetInputMode (!_englishMode);

		private void SetInputMode (bool english)
		{
			_englishMode = english;
			WriteModeCompartment (english);
			WriteConversionModeCompartment (english);
			_langBarButton?.NotifyUpdate ();
			if (english && this.Engine_Processor.IsComposing ())
			{
				this.Engine_Processor.Clear ();
				this.ResetCandidateAnchoringCycle ();
			}
		}

		// The mode is kept in a compartment of Manju IME's own on the thread. No host knows its GUID, so no
		// host can write it. A host can write the keyboard conversion-mode compartment: read at activation, a
		// value written there would put Manju IME in Latin input, every key going straight to the document,
		// whatever the user chose with Shift. That compartment is written, for the system input indicator,
		// and never read. Each value carries a tag, so a compartment never written, whose read gives
		// whatever the variant held, is no choice made.
		private static readonly Guid GUID_COMPARTMENT_MANJU_INPUTMODE = new ("104F139A-83C1-4AAC-8E73-8AF245D636ED");
		private const int InputModeTagManchu = 0x4D4A0000, InputModeTagLatin = 0x4D4A0001;

		private void WriteModeCompartment (bool english)
		{
			try
			{
				if (this.Mgr_Thread is not NativeMethods.ITfCompartmentMgr compMgr) return;
				Guid g = GUID_COMPARTMENT_MANJU_INPUTMODE;
				int hr = compMgr.GetCompartment (in g, out NativeMethods.ITfCompartment comp);
				if (hr != NativeMethods.S_OK || comp == null) return;
				var v = new VARIANT { vt = NativeMethods.VT_I4, lVal = english ? InputModeTagLatin : InputModeTagManchu };
				comp.SetValue (this.Id_Client, in v);
			}
			catch (Exception) { }
		}

		// Write the mode to the keyboard conversion-mode compartment (NATIVE=manju / ALPHANUMERIC=EN) so
		// the system input indicator agrees. Best-effort.
		private void WriteConversionModeCompartment (bool english)
		{
			try
			{
				if (this.Mgr_Thread is not NativeMethods.ITfCompartmentMgr compMgr) return;
				Guid g = NativeMethods.GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION;
				int hr = compMgr.GetCompartment (in g, out NativeMethods.ITfCompartment comp);
				if (hr != NativeMethods.S_OK || comp == null) return;
				var v = new VARIANT
				{
					vt = NativeMethods.VT_I4,
					lVal = english ? NativeMethods.TF_CONVERSIONMODE_ALPHANUMERIC : NativeMethods.TF_CONVERSIONMODE_NATIVE
				};
				comp.SetValue (this.Id_Client, in v);
			}
			catch (Exception) { }
		}

		// Restore the mode from Manju IME's own compartment on activation (so it persists across focus
		// cycles). A compartment that holds neither tag leaves the default manju mode untouched.
		private void InitInputModeFromCompartment ()
		{
			try
			{
				if (this.Mgr_Thread is not NativeMethods.ITfCompartmentMgr compMgr) return;
				Guid g = GUID_COMPARTMENT_MANJU_INPUTMODE;
				int hr = compMgr.GetCompartment (in g, out NativeMethods.ITfCompartment comp);
				if (hr != NativeMethods.S_OK || comp == null) return;
				int hrv = comp.GetValue (out VARIANT v);
				if (hrv == NativeMethods.S_OK && v.vt == NativeMethods.VT_I4 && (v.lVal == InputModeTagLatin || v.lVal == InputModeTagManchu))
					_englishMode = v.lVal == InputModeTagLatin;
			}
			catch (Exception) { }
		}

		// Create + add the tray input-mode button. Best-effort; on any failure the IME stays fully
		// functional, just without a tray icon — it must NEVER crash the host process.
		private void RegisterLangBarButton ()
		{
			try
			{
				if (_langBarItemAdded) return;
				if (this.Mgr_Thread is not NativeMethods.ITfLangBarItemMgr mgr)
				{
					return;
				}
				_langBarMgr = mgr;
				_langBarButton = new LangBarButton (() => _englishMode, ToggleInputMode);
				unsafe
				{
					nint punk = (nint) ComInterfaceMarshaller<NativeMethods.ITfLangBarItem>.ConvertToUnmanaged (_langBarButton);
					if (punk == 0) { return; }
					try
					{
						int hr = mgr.AddItem (punk);
						if (hr == NativeMethods.S_OK) { _langBarItemAdded = true; }
					}
					finally { Marshal.Release (punk); }
				}
			}
			catch (Exception) { }
		}

		private void UnregisterLangBarButton ()
		{
			try
			{
				if (_langBarItemAdded && _langBarMgr != null && _langBarButton != null)
				{
					unsafe
					{
						nint punk = (nint) ComInterfaceMarshaller<NativeMethods.ITfLangBarItem>.ConvertToUnmanaged (_langBarButton);
						if (punk != 0)
						{
							try { _langBarMgr.RemoveItem (punk); }
							finally { Marshal.Release (punk); }
						}
					}
				}
			}
			catch (Exception) { }
			finally { _langBarItemAdded = false; _langBarMgr = null; _langBarButton = null; }
		}

		#endregion

		#region ITfCompositionSink

		public Int32 OnCompositionTerminated (UInt32 Id_EcWrite, nint Composition_Composition)
		{

            // The host ended the composition because the user switched away (Word does, when it loses the
            // focus): that is ESC too. The engine is given VK_ESCAPE first, so what is written below is the
            // empty commit.
            if (this.Engine_Processor.IsComposing() && IsSwitchedAway())
            {
                this.Engine_Processor.OnKeyDown(VK_ESCAPE);
            }

            // [ZWSP-STRIP] Strip any leading ZWSP (U+200B) before this EXTERNALLY-terminated
            // composition finalizes into the host. Our explicit ESC/commit paths strip it
            // (COMMIT-CLEAN), but an EXTERNAL termination (app ends it / focus change / mouse)
            // never runs that path. TSF hands us a write cookie here, so rewrite the range without
            // the leading ZWSP in place.
            try
            {
                NativeMethods.ITfComposition? comp = this.Obj_Composition;
                if (comp != null)
                {
                    comp.GetRange(out NativeMethods.ITfRange? Range_Term);
                    if (Range_Term != null)
                    {
                        unsafe
                        {
                            const int CAP = 512;
                            char* pBuf = stackalloc char[CAP];
                            int hrGet = Range_Term.GetText(Id_EcWrite, 0, pBuf, (uint)CAP, out uint cch);
                            if (hrGet == NativeMethods.S_OK && cch > 0)
                            {
                                int start = 0;
                                while (start < (int)cch && pBuf[start] == '​') start++;
                                string cleaned = new string(pBuf, start, (int)cch - start);
                                // While composing, the host holds the placeholder (·), and an external
                                // terminate finalises the range's current content into the document — the placeholder must never end
                                // up on the page. So it is swapped back to the engine's mixed string: selected Manchu, plus raw Latin where nothing was selected. Content that is not the placeholder only gets the ZWSP strip.
                                string finalTerm = cleaned;
                                if (cleaned == ComposingHostPlaceholder)
                                {
                                    var termEngine = this.Engine_Processor.GetEngine();
                                    string rawTerm = termEngine.GetRawBuffer();
                                    finalTerm = string.IsNullOrEmpty(rawTerm) ? string.Empty : termEngine.BuildCommitString(rawTerm);
                                }
                                if (start > 0 || !ReferenceEquals(finalTerm, cleaned))
                                {
                                    IntPtr pClean = Marshal.StringToCoTaskMemUni(finalTerm);
                                    try { Range_Term.SetText(Id_EcWrite, 0, pClean, finalTerm.Length); }
                                    finally { Marshal.FreeCoTaskMem(pClean); }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            // Synchronous cleanup to prevent character loss and race conditions
            CleanupComposition();
			return NativeMethods.S_OK;
		}

        private void CleanupComposition()
        {
			this.Obj_Composition = null;
			this._hostPlaceholderWritten = false;   // composition gone → next live update must write
			this.Engine_Processor.Clear ();
            DeferState_SetPending(false);   // through the gated state machine, not a raw field write
            this.ResetCandidateAnchoringCycle(); // also hides KW (see method)
        }

		#endregion ITfCompositionSink

        #region ITfThreadMgrEventSink

        public Int32 OnInitDocumentMgr(NativeMethods.ITfDocumentMgr pdim) => NativeMethods.S_OK;
        public Int32 OnUninitDocumentMgr(NativeMethods.ITfDocumentMgr pdim) => NativeMethods.S_OK;
        
        public Int32 OnSetFocus(NativeMethods.ITfDocumentMgr pdimFocus, NativeMethods.ITfDocumentMgr pdimPrevFocus)
        {

            NativeMethods.ITfContext? focusContext = null;
            bool hasFocusContext = pdimFocus != null && pdimFocus.GetTop(out focusContext) == NativeMethods.S_OK && focusContext != null;
            if (ShouldShortCircuitThreadMgrFocusSync(
                HasSameNativeContextIdentity(_currentContext, focusContext),
                _cookieTextLayoutSink != InvalidSinkCookie))
            {
                TryFlushDeferredFrameReadyAfterFocusSync();
                return NativeMethods.S_OK;
            }

            // The focus moved to another context: the settings menu closes.
            CloseSettingsMenu("document focus changed");

            // 1. Unadvise old layout sink
            if (_currentContext is NativeMethods.ITfSource sourceOld && _cookieTextLayoutSink != InvalidSinkCookie)
            {
                sourceOld.UnadviseSink(_cookieTextLayoutSink);
                _cookieTextLayoutSink = InvalidSinkCookie;
            }
            _currentContext = null;

            if (pdimFocus == null)
            {
                // A switch-away when the thread no longer has the TSF focus. Otherwise the focus left the
                // document for something else inside the host, and the classification below decides as before.
                if (!IsHostThreadFocused())
                {
                    SwitchAway(null);
                    return NativeMethods.S_OK;
                }

                // Lost focus
                bool hasCachedAuthoritativeAnchor = _hasCachedCandidatePos && _hasAuthoritativeCandidatePos;
                bool hasEngineCandidateContent = this.Engine_Processor.GetCurrentSnapshotCandidates().Count > 0;
                // The pending flag is READ under the lock that owns it. An unsynchronised
                // read lets the decision be taken against a pending value that a
                // concurrent HandleFrameReadyFrame is in the middle of writing.
                bool pendingAtDecision = DeferState_GetPending();
                bool shouldResetCandidateCycle = ShouldResetCandidateCycleOnThreadMgrFocusLoss(
                    _foregroundFocusActive,
                    this.Engine_Processor.IsComposing(),
                    this.HasVisibleCandidateSelection(),
                    _hasCachedCandidatePos,
                    hasCachedAuthoritativeAnchor,
                    _authoritativeCandidatePosDirty,
                    pendingAtDecision,
                    hasEngineCandidateContent);
                if (shouldResetCandidateCycle)
                {
                    // Real focus loss (user clicked elsewhere) → cancel composition. The
                    // reset-candidate-cycle path tears down candidate UI but does not clear the engine
                    // buffer or end the TSF composition, which would leave the IME "sticky" until ESC.
                    // So this also clears the engine and signals commit-empty, and the IME exits cleanly
                    // on genuine focus loss. Transient focus churn (Explorer-like) takes the
                    // preserve branch below, so this only fires on real handoff.
                    if (this.Engine_Processor.IsComposing())
                    {
                        // {Clear the engine} and {clear pending} are ONE transaction.
                        // Clear running outside the lock leaves pending set, and a frame handler that
                        // arrives while Clear is in flight then arms a wakeup for a composition that no
                        // longer exists. Holding _deferGate across
                        // Clear() is safe: the worker dispatches frames with no engine lock held, so the
                        // only order here is _deferGate → _epochGate and never the reverse.
                        lock (_deferGate)
                        {
                            this.Engine_Processor.Clear();
                            _pendingFrameReadyAfterFocusSync = false;
                        }
                    }
                    this.ResetCandidateAnchoringCycle(); // also hides KW (see method)
                }
                else
                {
                    bool preserveCachedAuthoritativeAnchor = ShouldPreserveCachedAuthoritativeAnchorOnThreadMgrFocusLoss(
                        shouldResetCandidateCycle,
                        _hasCachedCandidatePos,
                        _hasAuthoritativeCandidatePos);
                    if (preserveCachedAuthoritativeAnchor)
                    {
                        // Keep the last authoritative anchor alive through transient ThreadMgr null focus.
                        // Explorer-like hosts can churn focus without actually invalidating the live composition.
                        _authoritativeCandidatePosDirty = true;
                    }
                    DeferState_SetPending(this.Presenter_Candidate != null && this.Engine_Processor.IsComposing());   // gated
                }
                return NativeMethods.S_OK;
            }

            // A document has the focus. A composition a switch-away left behind is ended; nothing is shown.
            EndCompositionLeftBehind(null);

            // 2. Get new context and advise layout sink
            if (hasFocusContext && focusContext != null)
            {
                _currentContext = focusContext;
                if (_currentContext is NativeMethods.ITfSource sourceNew)
                {
                    nint punk;
                    unsafe
                    {
                        punk = (nint)ComInterfaceMarshaller<NativeMethods.ITfTextLayoutSink>.ConvertToUnmanaged(this);
                    }
                    try
                    {
                        sourceNew.AdviseSink(NativeMethods.IID_ITfTextLayoutSink, punk, out _cookieTextLayoutSink);
                    }
                    finally
                    {
                        Marshal.Release(punk);
                    }
                }
            }

            TryFlushDeferredFrameReadyAfterFocusSync();

            return NativeMethods.S_OK;
        }

        public Int32 OnPushContext(nint pic) => NativeMethods.S_OK;
        public Int32 OnPopContext(nint pic) => NativeMethods.S_OK;

        #endregion ITfThreadMgrEventSink

        #region ITfThreadFocusSink

        // The host thread lost the UI focus: another application or the desktop is in front.
        // A switch-away: the ESC path.
        public Int32 OnKillThreadFocus()
        {
            SwitchAway(null);
            return NativeMethods.S_OK;
        }

        // The host thread has the UI focus again.
        // Nothing is shown. A composition the switch-away's session did not end is ended now.
        public Int32 OnSetThreadFocus()
        {
            EndCompositionLeftBehind(null);
            return NativeMethods.S_OK;
        }

        #endregion ITfThreadFocusSink

        #region ITfTextLayoutSink

        public Int32 OnLayoutChange(nint pic, TfLayoutCode lcode, NativeMethods.ITfContextView pView)
        {
            if (lcode != TfLayoutCode.TF_LC_CHANGE) return NativeMethods.S_OK;

            // Request a read-only EditSession to call GetTextExt with a valid edit cookie.
            // Modeled after Weasel's _UpdateCompositionWindow pattern.
            // Calling GetTextExt with ec=0 (outside EditSession) returns TF_E_NOLAYOUT in many apps.
            if (this.Engine_Processor.IsComposing() && this.Obj_Composition != null && _currentContext != null)
            {
                try
                {
                    var session = new GetTextExtEditSession(this, pView, this.Obj_Composition);
                    _currentContext.RequestEditSession(
                        this.Id_Client, session,
                        NativeMethods.TF_ES_READ | NativeMethods.TF_ES_ASYNC,
                        out int hrSession);
                }
                catch (Exception)
                {
                }
            }
            return NativeMethods.S_OK;
        }

        #endregion ITfTextLayoutSink
	}
}

using System;

namespace CSharpTSFInput.Abstractions
{
    /// <summary>
    /// The SINGLE interface between the core per-character IME and the optional
    /// dictionary / knowledge-window feature.
    ///
    ///   - The CORE references ONLY this interface (+ <see cref="DictionaryModuleRegistry"/>).
    ///     No core file mentions any concrete dictionary/knowledge-window type.
    ///   - The implementation lives entirely under the KnowledgeModule/ directory.
    ///
    /// Threading contract (the same worker/UI split the core uses):
    ///   - <see cref="OnSnapshotChanged"/> is called from the background worker thread; the
    ///     implementation must only mutate its own thread-safe state there, never touch HWNDs.
    ///   - <see cref="OnFrameReady"/>, <see cref="OnCompositionEnd"/>, <see cref="TryToggleFocus"/>,
    ///     <see cref="TryCommitWord"/> are called on the UI (TSF) thread; HWND work is allowed.
    ///
    /// The interface deals only in primitives (string / int / bool) so NO module-internal type
    /// (LemmaEntry, KnowledgeWindow, …) leaks into the core's compiled surface.
    /// </summary>
    public interface IDictionaryModule : IDisposable
    {
        /// <summary>
        /// UI thread: the IME was activated. The module MUST create its window (HWND) here, on the
        /// UI thread that owns the host's message pump — NOT lazily during a worker-thread frame
        /// callback. A window created on a non-pumping thread never paints (white) and blocks the
        /// host on click (SendMessage to a non-pumping owner). Idempotent; safe to call repeatedly.
        /// </summary>
        void OnActivate();

        /// <summary>
        /// UI thread: the IME was deactivated (focus loss / TIP deactivation). Release the
        /// per-activation window resources (HWND/D2D) ONLY — the module instance itself is
        /// engine-lifetime and will be re-activated, so this MUST stay reversible: a subsequent
        /// <see cref="OnActivate"/> recreates the window exactly as the first activation did.
        /// Distinct from <see cref="IDisposable.Dispose"/>, which is the permanent teardown.
        /// Conflating the two (Deactivate→Dispose) would latch the module dead after the
        /// first focus cycle: the window would never re-show while lookups kept running.
        /// </summary>
        void OnDeactivate();

        /// <summary>Worker thread: the composition snapshot changed → (re)lookup + populate.</summary>
        void OnSnapshotChanged(string snapshot);

        /// <summary>
        /// UI thread: a render frame is ready. The main candidate window occupies the given screen
        /// rectangle (physical px). The module positions + shows/hides its own window relative to it.
        /// </summary>
        void OnFrameReady(int mainLeft, int mainTop, int mainWidth, int mainHeight);

        /// <summary>
        /// UI thread: composition ended for any reason (commit / ESC / backspace-empty / focus loss).
        /// The module hides its window and clears transient state.
        /// </summary>
        void OnCompositionEnd();

        /// <summary>
        /// WORKER thread (explicitly): the composition ended while a worker-thread callback of this module
        /// was still running, so the core needs the module to undo whatever that callback populated.
        ///
        /// This exists because the core must NOT call <see cref="OnCompositionEnd"/> from
        /// the worker: that method's contract is UI thread and permits HWND work, and the
        /// compensation can land on the worker thread (snapshotThread == compensationThread). An
        /// implementation MUST NOT touch its HWND here; it must marshal to its own UI thread (e.g. by
        /// posting a message to its window) and return promptly. The core calls this OUTSIDE any lock.
        /// </summary>
        void RequestCompositionEndFromWorker();

        /// <summary>
        /// Whether the module currently owns numeric-key focus. Drives the main candidate window's
        /// dim overlay (the only thing the core needs to read from the module during paint).
        /// </summary>
        bool HasFocusContext { get; }

        /// <summary>
        /// True only when the module is enabled AND a lexicon actually loaded (i.e. a real
        /// dictionary window can exist for this distribution). The key-ownership layer reads this to decide
        /// whether to CLAIM Tab: claim only when a window exists to toggle to, otherwise Tab passes through
        /// to the host. Without this, Tab would either be swallowed with no window to focus, or (when not
        /// claimed at all) reach the host and terminate the composition.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Whether the user currently wants the dictionary window shown. Distinct
        /// from <see cref="IsAvailable"/>, which says whether a window could exist at all.
        /// </summary>
        bool IsWindowEnabledByUser { get; }

        /// <summary>
        /// Show/hide toggle, the dictionary window row of the settings menu. Returns true if the module
        /// handled it; false ⇒ there is no window to toggle.
        /// </summary>
        bool ToggleWindowVisibility();

        /// <summary>
        /// Tab pressed during composition. Returns true if the module handled it (toggled focus);
        /// false ⇒ the caller lets Tab pass through to the host as a normal Tab.
        /// </summary>
        bool TryToggleFocus();

        /// <summary>
        /// Numeric key (VK_0..VK_9) pressed during composition while the module has focus. If the
        /// module claims it, returns true and sets <paramref name="codepoints"/> to the word to
        /// commit; otherwise returns false and the caller routes the key normally.
        /// </summary>
        bool TryCommitWord(uint vkCode, out string codepoints);

        /// <summary>
        /// Arrow key pressed during composition while the module has focus. ←/→ move the
        /// module's highlight; ↑/↓ are consumed as no-ops. Returns true if the key was an arrow handled here
        /// (caller repaints and does NOT route it to the main candidate window); false to route normally.
        /// </summary>
        bool TryHandleFocusedNavigation(uint vkCode);

        /// <summary>
        /// UI thread: the MAIN window's colour theme changed — re-colour this
        /// module's own window to match and repaint.
        ///
        /// The dictionary window is a companion surface of the main input UI and has no theme of its
        /// own, so two palettes must never coexist.
        ///
        /// Colours are packed 0xAARRGGBB so the core→module contract stays primitives-only and no D2D /
        /// theme type leaks across the module boundary. Implementations must recolour IN PLACE
        /// (ID2D1SolidColorBrush::SetColor) rather than releasing/recreating brushes — brush lifetime is
        /// the host-freeze hazard, SetColor is only a value write.
        /// </summary>
        void OnThemeChanged(uint bgArgb, uint textArgb, uint borderArgb, uint highlightArgb, uint accentArgb);

        /// <summary>
        /// UI thread: the active dictionary changed — load the newly selected one and start
        /// answering lookups from it. Returns true if the new dictionary is now in use.
        ///
        /// FAILURE MUST BE VISIBLE AND MUST NOT COST THE USER THEIR DICTIONARY. A selected dictionary
        /// whose data is missing or malformed is an ordinary situation here (a distribution may ship one
        /// folder, or none). Swapping in an empty store and reporting success would turn a
        /// missing folder into a feature that is silently switched off.
        ///
        /// Returning false is therefore not an error path the caller must handle. It means "still on the
        /// previous dictionary", which is a usable state.
        /// </summary>
        bool ReloadDictionary();
    }
}

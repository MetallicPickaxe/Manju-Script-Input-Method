using System;
using CSharpTSFInput.Abstractions;
using CSharpTSFInput.UI;
using CSharpTSFInput.WordDictionary;

namespace CSharpTSFInput.KnowledgeModule
{
    /// <summary>
    /// The optional dictionary / knowledge-window feature, packaged as a
    /// single <see cref="IDictionaryModule"/> implementation.
    ///
    /// Owns the full feature: the lemma lookup store, the knowledge-window UI state, and the
    /// floating HWND host. Reads its own configuration (knowledge_window.*) from the engine's
    /// config store, so the core needs no knowledge-window config knowledge either.
    /// </summary>
    internal sealed class KnowledgeModule : IDictionaryModule
    {
        private readonly ManjuEngine _engine;
        private readonly bool _enabled;
        // The USER's show/hide choice, distinct from _enabled (which is whether the
        // feature exists at all). Not persisted back to disk — see IsWindowEnabledByUser.
        private bool _userVisible;
        // NOT readonly and deliberately `volatile`: a reload REPLACES this reference rather
        // than mutating the store in place. Two reasons, both load-bearing.
        //   1. WordDictionaryStore.Load APPENDS — it never clears _lemmas/_prefixIndex first. Reloading in
        //      place would leave the old dictionary's lemmas merged into the new one.
        //   2. Lookups run on the WORKER thread while the switch happens on the UI thread. A reference
        //      swap is atomic, so a reader sees either the whole old store or the whole new one — never a
        //      store halfway through loading.
        private volatile WordDictionaryStore _dict = new();
        private readonly KnowledgeWindow _state = new();
        private KnowledgeWindowHost? _host;   // lazily created on first UI-thread OnFrameReady
        private bool _disposed;

        public KnowledgeModule(ManjuEngine engine)
        {
            _engine = engine;
            // Self-respect the master enable flag: the core does not carry this concern.
            _enabled = engine.GetConfigStore().GetBool("knowledge_window.enabled", true);
            // The dictionary window starts hidden unless knowledge_window.visible_by_default is true.
            // The dictionary window row of the settings menu shows and hides it for the session.
            _userVisible = engine.GetConfigStore().GetBool("knowledge_window.visible_by_default", false);
            // A changed knowledge_window.visible_by_default in the settings file replaces the session's choice.
            engine.ConfigurationChanged += keys =>
            {
                if (!System.Linq.Enumerable.Contains(keys, "knowledge_window.visible_by_default")) return;
                _userVisible = _engine.GetConfigStore().GetBool("knowledge_window.visible_by_default", false);
                if (_host != null) { _host.UserHidden = !_userVisible; _host.SyncVisibility(); }
            };
            if (!_enabled)
            {
                return;
            }
            var initial = TryLoadActiveDictionary(out string initialDir);
            if (initial != null) _dict = initial;
        }

        /// <summary>
        /// Resolve the ACTIVE dictionary's folder. Reads <see cref="ManjuEngine.ActiveDictionary"/>
        /// and not the raw `dictionary.active` config value:
        /// the dictionary row of the settings menu sets a session override on the engine and never writes the file, so a module reading the
        /// file directly would reload the dictionary it already had and report success.
        /// </summary>
        internal string ResolveActiveDictionaryDirectory()
        {
            // The ROOT comes from ManjuEngine, which is also what the axis scan uses. Two roots that
            // disagree would mean the axis lists dictionaries from one tree while the module loads from
            // another, and switching dictionaries would then appear to do nothing.
            string root = ManjuEngine.DictionaryRootDirectory();
            if (string.IsNullOrEmpty(root)) return string.Empty;
            return System.IO.Path.Combine(root, _engine.ActiveDictionary);
        }

        /// <summary> Load the active dictionary into a FRESH store. Returns null when there is
        /// nothing usable there — the caller decides what to do about it, and in every case the caller
        /// keeps whatever store it already had.</summary>
        private WordDictionaryStore? TryLoadActiveDictionary(out string resDir)
        {
            resDir = string.Empty;
            try
            {
                resDir = ResolveActiveDictionaryDirectory();
                if (string.IsNullOrEmpty(resDir)) return null;
                var fresh = new WordDictionaryStore();
                fresh.Load(resDir);
                return fresh.IsLoaded ? fresh : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The active dictionary changed: start answering from the new one.
        ///
        /// The entries on screen are keyed by the OLD dictionary's romanization, and the number
        /// keys commit from that same list, so leaving them up means the window shows one dictionary
        /// while the module answers from another, and a commit would take a word the user is no longer
        /// looking at. So a successful swap re-runs the lookup for the composition that is live right
        /// now, and a failed one changes nothing at all.
        ///
        /// THE STORE IS REPLACED, NOT RELOADED. See the field comment: Load appends.
        /// </summary>
        public bool ReloadDictionary()
        {
            if (!_enabled) { return false; }
            if (_disposed) { return false; }

            string wanted = _engine.ActiveDictionary;
            var fresh = TryLoadActiveDictionary(out string resDir);
            if (fresh == null)
            {
                return false;
            }

            _dict = fresh;
            RefreshLookupAfterReload();
            return true;
        }

        /// <summary> Re-answer the CURRENT composition from the dictionary that is now active,
        /// so the window never shows rows the module would no longer return.</summary>
        private void RefreshLookupAfterReload()
        {
            try
            {
                string script = _engine.GetTranslatedText();
                if (string.IsNullOrEmpty(script))
                {
                    _state.SetEntries(new System.Collections.Generic.List<CSharpTSFInput.WordDictionary.LemmaEntry>());
                }
                else
                {
                    var matches = _dict.LookupByScript(script);
                    _state.SetEntries(matches);
                }
                _host?.SyncVisibility();
            }
            catch (Exception)
            {
            }
        }

        // ---- IDictionaryModule ----

        public void OnActivate()
        {
            if (!_enabled) return;
            if (_disposed) return;
            if (!_dict.IsLoaded) return;
            // Create the HWND HERE — on the UI/message-pump thread (Activate).
            // OnFrameReady runs on the WORKER thread (it is invoked from ManjuEngine.OnFrameReady →
            // HandleFrameReady, which fires on the backend worker), and a window created on that
            // non-pumping thread never paints (white) and freezes the host on click.
            if (_host == null)
            {
                _host = new KnowledgeWindowHost(_engine, _state);
                // Replay the cached palette into the FRESH host. A palette stashed only ON the host
                // is destroyed with it, so a theme delivered while the window is torn down (or before
                // it first exists) would be silently dropped and the rebuilt window would come back
                // in default colours. The cache lives here, on the module, which survives host
                // teardown.
                if (_paletteCached)
                    _host.ApplyTheme(_pBg, _pText, _pBorder, _pHighlight, _pAccent);
            }
        }

        public void OnSnapshotChanged(string snapshot)
        {
            if (!_enabled || !_dict.IsLoaded) return;
            // Look up by composed Manchu SCRIPT (codepoints), not by
            // any romanization. The engine turns the raw input (whatever scheme) into codepoints; the
            // dictionary transliterates those codepoints to ITS OWN index form (Möllendorff) internally.
            // So lookup works identically for every input scheme and the engine carries no Möllendorff
            // assumption. (An ASCII romanization key would miss the diacritic words of the Möllendorff index.)
            string script = _engine.GetTranslatedTextFromSnapshot(snapshot ?? string.Empty);
            var matches = _dict.LookupByScript(script);
            // Graceful degradation — never blank out mid-word. If the FULL composed script
            // has no match (a typo, a not-yet-complete word, or single-vs-double-letter like manga≠mangga),
            // fall back to the longest trailing-trimmed prefix that still matches, so the dict keeps showing
            // the closest words instead of cutting off (going empty). Real in-dict words (e.g. mangga) match at every
            // keystroke, so this never fires for them — it only rescues no-exact-match input from a blank window.
            int trimmed = 0;
            if (matches.Count == 0 && !string.IsNullOrEmpty(script) && script.Length > 1)
            {
                string probe = script;
                while (probe.Length > 1 && matches.Count == 0)
                {
                    probe = probe.Substring(0, probe.Length - 1);
                    trimmed++;
                    matches = _dict.LookupByScript(probe);
                }
            }
            _state.SetEntries(matches);
        }

        public void OnFrameReady(int mainLeft, int mainTop, int mainWidth, int mainHeight)
        {
            if (!_enabled) return;
            if (_disposed) return;
            // Do NOT create the window here (worker thread). It was created on
            // the UI thread in OnActivate. Here we only position + show/hide — SetWindowPos /
            // ShowWindow / InvalidateRect are cross-thread-safe and the UI pump (owning the HWND)
            // does the actual repaint. If OnActivate hasn't run yet, skip this frame.
            if (_host == null) return;
            // The user's show/hide choice applies to every frame, not just the one where it was
            // made. Hiding once would be undone by the next frame, since this is the call that shows
            // the window.
            // Carried on the host as a separate axis instead of calling HideForCancel:
            // that CLEARS the lookup, so hiding would wipe what the window was showing and the next
            // show would reveal an empty window. Position is still refreshed while hidden, so re-showing
            // lands in the right place with the content intact.
            _host.UserHidden = !_userVisible;
            _host.SetAdaptiveLocation(mainLeft, mainTop, mainWidth, mainHeight);
            _host.SyncVisibility();
        }

        /// <summary> Whether the user currently wants the dictionary window shown.
        /// Distinct from <see cref="IsAvailable"/>, which is whether it could exist at all.
        ///
        /// NOT PERSISTED back to disk, deliberately. configuration.yaml is a file the user edits and the
        /// only configuration anyone ever sees, so a menu row silently
        /// rewriting it would make the file stop describing what the user wrote. The default is therefore
        /// re-read from configuration on every activation, and the menu row is a per-session override. Someone who
        /// wants it up permanently sets knowledge_window.visible_by_default in the file.</summary>
        public bool IsWindowEnabledByUser => _userVisible;

        /// <summary> Toggle it, and apply the choice at once without waiting
        /// for the next frame. Returns true once toggled, false when there is no window to toggle.</summary>
        public bool ToggleWindowVisibility()
        {
            if (!IsAvailable) { return false; }
            _userVisible = !_userVisible;
            // Hiding sets the user axis; it does NOT call HideForCancel, which clears the
            // lookup. Wiping the content on the way out would make re-showing reveal an empty window.
            if (_host != null) { _host.UserHidden = !_userVisible; _host.SyncVisibility(); }
            return true;
        }

        public void OnDeactivate()
        {
            // Per-activation window teardown, NOT permanent disposal. The module is engine-lifetime
            // (created once in the ManjuEngine ctor, get-only property) and WILL be re-activated, so
            // release only the HWND/D2D host and leave it recreatable; the loaded lexicon (_dict) is
            // kept. The next OnActivate then rebuilds the window exactly as the first activation did.
            // Calling Dispose() here instead would latch _disposed=true, and OnActivate's
            // `if (_disposed) return;` would then permanently skip host recreation, so the knowledge
            // window would die after the first focus cycle while OnSnapshotChanged, which never checks
            // _disposed, would keep running lookups.
            _host?.Dispose();
            _host = null;
        }

        public void OnCompositionEnd()
        {
            if (!_enabled) return;
            _host?.HideForCancel();
        }

        /// <summary> Worker-thread compensation. Clears the STATE (safe off the UI
        /// thread) and asks the host to marshal the window work to its own UI thread via its
        /// WM_APP_KW_REFRESH path — it must never touch the HWND from here.</summary>
        public void RequestCompositionEndFromWorker()
        {
            if (!_enabled) return;
            _state.Clear();          // pure state, no HWND
            _host?.SyncVisibility();  // stashes desired visibility + posts to the owning UI thread
        }

        public bool HasFocusContext => _enabled && _state.HasFocusContext;

        // A real dictionary window can exist only when the module is enabled, not disposed,
        // AND a lexicon actually loaded. Tab ownership keys off this so it never toggles toward a window
        // that isn't there; when false, Tab passes through to the host.
        public bool IsAvailable => _enabled && !_disposed && _dict.IsLoaded;

        public bool TryToggleFocus()
        {
            if (!_enabled || !_dict.IsLoaded) return false;
            _state.ToggleFocusContext();
            return true;
        }

        // Arrow navigation while the knowledge window owns focus: ←/→ move its highlight,
        // ↑/↓ consumed as no-ops (so they never leak to the main candidate window). Same availability
        // condition as TryToggleFocus. Returns true when the key was an arrow handled here.
        // Forward the main window's palette to our own window so the two surfaces
        // stay in ONE theme. No-op when the window does not exist yet, because
        // ResourcesReady() re-reads the stashed palette when it is created, so a theme switched while the
        // window is torn down is not lost.
        public void OnThemeChanged(uint bgArgb, uint textArgb, uint borderArgb, uint highlightArgb, uint accentArgb)
        {
            if (!_enabled) return;
            // Cache at MODULE level first — the host may be null (not yet created) or
            // about to be torn down; OnActivate replays this cache into every fresh host so a rebuilt
            // window can never come back in default colours.
            _pBg = bgArgb; _pText = textArgb; _pBorder = borderArgb; _pHighlight = highlightArgb; _pAccent = accentArgb;
            _paletteCached = true;
            _host?.ApplyTheme(bgArgb, textArgb, borderArgb, highlightArgb, accentArgb);
        }

        // Module-level palette cache — survives KnowledgeWindowHost teardown/recreate.
        private bool _paletteCached;
        private uint _pBg, _pText, _pBorder, _pHighlight, _pAccent;

        public bool TryHandleFocusedNavigation(uint vkCode)
        {
            if (!_enabled || !_dict.IsLoaded || !_state.HasFocusContext) return false;
            switch (vkCode)
            {
                case 0x25: _state.MoveHighlight(-1); return true; // VK_LEFT
                case 0x27: _state.MoveHighlight(+1); return true; // VK_RIGHT
                case 0x26: case 0x28: return true;                // VK_UP / VK_DOWN: consume, no-op
                default: return false;
            }
        }

        public bool TryCommitWord(uint vkCode, out string codepoints)
        {
            codepoints = string.Empty;
            if (!_enabled) return false;
            var entry = _state.TryPickByNumericKey(vkCode); // null unless focused + valid idx
            if (entry == null) return false;
            codepoints = entry.ManchuScript ?? string.Empty;
            string.IsNullOrEmpty(codepoints);
            return !string.IsNullOrEmpty(codepoints);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _host?.Dispose();
            _host = null;
        }
    }
}

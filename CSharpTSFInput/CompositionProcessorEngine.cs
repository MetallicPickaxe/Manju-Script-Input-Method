using System;
using System.Collections.Generic;
using System.Text;

namespace CSharpTSFInput
{
    /// <summary>
    /// CompositionProcessorEngine handles the TSF key-intercept logic and its interaction with the composition engine.
    /// </summary>
    internal class CompositionProcessorEngine
    {
        private ManjuEngine Engine = new ManjuEngine();
        private string? _selectedCandidate = null;
        private bool _selectedCandidateCommitPending = false;
        // A literal symbol (e.g. a plain apostrophe) to append to the host AFTER the forced
        // commit of the current composition. The commit content itself comes from BuildCommitString;
        // TextService appends this one-shot literal so the host receives e.g. ᠠ' (commit current + append ').
        private string? _pendingTrailingLiteral = null;
        private int _pendingNavigationDelta = 0;
        // Candidate paging intent: -1 = PgUp, +1 = PgDn, 0 = none. Kept apart from
        // _pendingNavigationDelta because a page has edge semantics an arrow step does not.
        private int _pendingPageDelta = 0;
        // Set when Tab toggles main↔dict focus, so TextService repaints both windows
        // (the toggle mutates no input buffer, so it would otherwise ride no frame and stay invisible).
        private bool _pendingFocusToggleRepaint = false;
        // The dictionary feature is reached only via the
        // ManjuEngine.DictionaryModule interface; no concrete knowledge-window type is named here.

        public ManjuEngine GetEngine() => Engine;

        public string? GetSelectedCandidate() => _selectedCandidate;
        public bool IsSelectedCandidateCommitPending() => _selectedCandidateCommitPending;
        // Consume the pending trailing literal (one-shot). TextService appends it to the commit.
        public string? TakePendingTrailingLiteral() { var v = _pendingTrailingLiteral; _pendingTrailingLiteral = null; return v; }
        // One-shot: the 2nd-' force-commit wants Enter (commit-as-typed) semantics even though
        // the key isn't VK_RETURN. TextService ORs this into its commitAsTyped decision, then it clears.
        private bool _forceCommitAsTyped = false;
        public bool ConsumeForceCommitAsTyped() { bool v = _forceCommitAsTyped; _forceCommitAsTyped = false; return v; }

        // 1 = page up, +1 = page down, 0 = nothing pending.
        public int ConsumePageDelta()
        {
            int d = _pendingPageDelta;
            _pendingPageDelta = 0;
            return d;
        }

        public int ConsumeNavigationDelta()
        {
            int delta = _pendingNavigationDelta;
            _pendingNavigationDelta = 0;
            return delta;
        }

        // True once after a Tab focus-toggle, so TextService can repaint both windows.
        public bool ConsumePendingFocusToggleRepaint()
        {
            bool v = _pendingFocusToggleRepaint;
            _pendingFocusToggleRepaint = false;
            return v;
        }

        // Virtual key code constants
        private const uint VK_BACK = 0x08;
        private const uint VK_RETURN = 0x0D;
        private const uint VK_SPACE = 0x20;
        private const uint VK_ESCAPE = 0x1B;
        private const uint VK_OEM_COMMA = 0xBC; // ','
        private const uint VK_F12 = 0x7B; // [ROTATION-DIAG] rotation toggle

        private const uint VK_OEM_PERIOD = 0xBE; // '.'
        private const uint VK_OEM_1 = 0xBA;     // ';:'
        private const uint VK_OEM_4 = 0xDB;     // '[{'
        private const uint VK_OEM_6 = 0xDD;     // ']}'
        private const uint VK_OEM_2 = 0xBF;     // '/?'
        private const uint VK_PRIOR = 0x21;     // PAGE UP
        private const uint VK_NEXT = 0x22;      // PAGE DOWN
        private const uint VK_LEFT = 0x25;      // ARROW LEFT
        private const uint VK_UP = 0x26;        // ARROW UP
        private const uint VK_RIGHT = 0x27;     // ARROW RIGHT
        private const uint VK_DOWN = 0x28;      // ARROW DOWN


        private static bool IsNavigationOrPagingKey(uint vkCode)
        {
            return vkCode == VK_PRIOR || vkCode == VK_NEXT || vkCode == VK_LEFT || vkCode == VK_UP || vkCode == VK_RIGHT || vkCode == VK_DOWN;
        }

        public bool CanSelectCandidateWithDigit(uint vkCode, bool hasVisibleCandidateSelection)
        {
            // Digit selection is owned by the presenter path
            // (presenter.WouldHandleNavigationKey + TryHandleNavigationKey). Returning false here so
            // TextService.IsSemanticImeOwnedKey does NOT double-claim digits. The presenter path
            // intercepts BEFORE this check via early-return at the top of OnTestKeyDown / OnKeyDown.
            return false;
        }

        public bool CanNavigateCandidatesWithKey(uint vkCode, bool hasVisibleCandidateSelection)
        {
            return hasVisibleCandidateSelection && IsComposing() && IsNavigationOrPagingKey(vkCode);
        }

        private bool TrySelectCandidateByNumberKey(uint vkCode, bool hasVisibleCandidateSelection)
        {
            if (!CanSelectCandidateWithDigit(vkCode, hasVisibleCandidateSelection))
            {
                return false;
            }

            // 1-0 mapping: VK_1..VK_9 (0x31..0x39) → idx 0..8, VK_0 (0x30) → idx 9.
            // Convention: the numeric row is "1, 2, 3, 4, 5, 6, 7, 8, 9, 0" with
            // 0 meaning the tenth slot.
            int index = (vkCode == 0x30) ? 9 : (int)(vkCode - 0x31);
            List<String> candidates = GetCurrentSnapshotCandidates();
            // Candidates may not be populated yet under the async architecture
            if (index < 0 || index >= candidates.Count)
            {
                return false;
            }
            _selectedCandidate = candidates[index];
            _selectedCandidateCommitPending = true;
            return true;
        }

        public bool OnKeyDown(uint vkCode, bool isShiftPressed = false, bool allowCandidateSelection = false, bool allowCandidateNavigation = false)
        {

            // Rotation has no hotkey: the IME uses mode 1, and CycleRotationMode() is for development.

            _selectedCandidate = null;
            _selectedCandidateCommitPending = false;
            _pendingTrailingLiteral = null;

            // ESC robustness when buffer already empty.
            // ESC handling sits OUTSIDE the IsComposing() block: after backspacing to empty,
            // IsComposing() is false (buffer length == 0), so an ESC arm inside that block would never
            // fire. ESC works whenever TSF composition is alive, regardless of buffer state.
            if (vkCode == VK_ESCAPE) {
                Clear();
                _selectedCandidate = "";
                _selectedCandidateCommitPending = true;
                // KW hide is handled centrally by TextService.
                // ResetCandidateAnchoringCycle → DictionaryModule.OnCompositionEnd (the universal
                // composition-end chokepoint), so no KW reference needed here.
                Engine.EnqueueInput((int)vkCode, isShiftPressed ? 1 : 0, Engine.GetRawBuffer());
                return true;
            }

            // Tab — ask the dictionary module to toggle focus.
            // Module is null (or returns false) when the KnowledgeModule directory isn't compiled
            // in OR knowledge_window.enabled=false → Tab passes through to the host as a normal Tab,
            // and HasFocusContext can never become true, so dim-overlay + word-commit stay dormant.
            // The core names no concrete knowledge-window type — only the interface.
            if (vkCode == 0x09 && IsComposing() && Engine.DictionaryModule?.TryToggleFocus() == true) // VK_TAB = 0x09
            {
                _pendingFocusToggleRepaint = true; // signal TextService to repaint main + dict windows
                return true;
            }

            // Numeric key WHILE the module owns focus → word commit.
            // When the module has focus, numbers are word-keys: commit if the module yields a word,
            // otherwise consume as no-op (don't fall through to glyph-pick). When the module is
            // absent / unfocused, this whole block is skipped and numbers route to glyph selection.
            if (IsComposing() && Engine.DictionaryModule?.HasFocusContext == true
                && (vkCode == 0x30 || (vkCode >= 0x31 && vkCode <= 0x39)))
            {
                if (Engine.DictionaryModule.TryCommitWord(vkCode, out string wordCp) && !string.IsNullOrEmpty(wordCp))
                {
                    _selectedCandidate = wordCp;
                    _selectedCandidateCommitPending = true;
                }
                return true;
            }

            // Arrow keys WHILE the module owns focus → navigate the knowledge window
            // (←/→ move highlight; ↑/↓ consumed no-op), NEVER the main candidate list. The presenter's
            // TryHandleNavigationKey defers to the engine when the KW has focus (returns false), so the
            // arrow reaches here. Reuse the focus-repaint signal so the moved highlight is painted, and return
            // so it never falls through to the main candidate navigation below.
            if (IsComposing() && Engine.DictionaryModule?.HasFocusContext == true
                && (vkCode == VK_LEFT || vkCode == VK_RIGHT || vkCode == VK_UP || vkCode == VK_DOWN))
            {
                if (Engine.DictionaryModule.TryHandleFocusedNavigation(vkCode))
                    _pendingFocusToggleRepaint = true;
                return true;
            }

            // 1. special-function intercepts while composing
            if (IsComposing())
            {
                if (vkCode == VK_BACK) {
                    // Two-phase Backspace: if any explicit candidate pick
                    // exists, UNDO the last pick first — highest position, back→front by sequence — WITHOUT deleting
                    // a raw char; refresh with focus on the un-picked position so its preview reverts. Only after ALL
                    // picks are undone does Backspace fall through to deleting raw input chars (the rule of other
                    // IMEs). Each Backspace pops exactly one pick.
                    int unpickedPos = Engine.UnpickHighestSelectedPosition();
                    if (unpickedPos >= 0)
                    {
                        // Rebuild candidates AT THE USER'S CURRENT FOCUS (= the position the
                        // candidate window is already showing), NOT at the un-picked position. Rebuilding at
                        // the un-picked position would switch the candidate window's content to the deleted
                        // pick's position while the preview focus bar stays put, and the two would point at
                        // different elements. Focus hold = bar AND content both stay. The un-picked position's preview cell
                        // reverts anyway when the fresh frame repaints (col3 reads IsPositionSelected per row).
                        int stayFocus = Engine.LastWorkerFocusResolved;
                        Engine.EnqueueFocusChange(Engine.GetRawBuffer(), stayFocus); // refresh, focus holds
                        return true;
                    }
                    Engine.DeleteLast();
                    Engine.EnqueueInput((int)vkCode, isShiftPressed ? 1 : 0, Engine.GetRawBuffer());
                    // Backspace-to-empty auto-cancel.
                    // Deleting all characters dismisses the IME (same effect as ESC). Without this, the
                    // composition would stay alive with an empty buffer: the host sees "" but the
                    // composition object remains open.
                    if (!IsComposing())
                    {
                        _selectedCandidate = "";
                        _selectedCandidateCommitPending = true;
                        // KW hide handled centrally by TextService reset.
                    }
                    return true;
                }
                if (vkCode == VK_RETURN) {
                    _selectedCandidate = GetCompositionString();
                    // KW hide handled centrally by TextService reset
                    // (DictionaryModule.OnCompositionEnd) after the commit completes.
                    return true;
                }
                
                // candidate-navigation intercepts (arrow keys included)
                // Candidate navigation: horizontal layout (LEFT/RIGHT = single step, PGUP/PGDN = page)
                if (allowCandidateNavigation && vkCode == VK_LEFT) { _pendingNavigationDelta = -1; return true; }
                if (allowCandidateNavigation && vkCode == VK_RIGHT) { _pendingNavigationDelta = 1; return true; }
                if (allowCandidateNavigation && vkCode == VK_UP) { _pendingNavigationDelta = -1; return true; }
                if (allowCandidateNavigation && vkCode == VK_DOWN) { _pendingNavigationDelta = 1; return true; }
                // PgUp/PgDn = candidate PAGING, not a ∓9 highlight jump: 9 matches nothing in the UI, and
                // because AdjustSelectedIndex clamps and then returns without repainting, a jump of 9 over
                // a short list would do nothing at all and look unmapped. Paging is a separate channel
                // from the ±1 arrow deltas because its edge behaviour differs: see PageSelectedIndex on
                // the presenter.
                if (allowCandidateNavigation && vkCode == VK_PRIOR) { _pendingPageDelta = -1; return true; }
                if (allowCandidateNavigation && vkCode == VK_NEXT) { _pendingPageDelta = 1; return true; }

                // commit on space
                if (vkCode == VK_SPACE && !isShiftPressed && allowCandidateSelection && GetCurrentSnapshotCandidates().Count > 0)
                {
                    _selectedCandidate = GetCurrentSnapshotCandidates()[0];
                    _selectedCandidateCommitPending = true;
                    // KW hide handled centrally by TextService reset.
                    return true;
                }

                // digit-key intercept (candidate selection)
                if (allowCandidateSelection && TrySelectCandidateByNumberKey(vkCode, hasVisibleCandidateSelection: true))
                {
                    return true;
                }
            }

            // 2. Letter intercept (A-Z) - starts composing
            if (vkCode >= 0x41 && vkCode <= 0x5A)
            {
                // IDENTITY-LANE IDENTITY LANE for letters the active scheme does not map.
                // The `IDENTITY-LANE` token marks the identity lane in this A-Z branch.
                //
                // While composing, EVERY letter goes down the same path (AddInput + EnqueueInput),
                // so an unmapped letter appears in the buffer and the window repaints, exactly like a
                // mapped one. This is the Japanese-IME style: it enters the buffer and is displayed.
                // Swallowing it instead would mean no buffer change, no EnqueueInput and therefore no
                // repaint: under the refined scheme q and x are the only two rejected letters, so they
                // would be dead keys while every other letter repaints. ParseSnapshotUnits already has
                // the identity fallback that makes this safe: an unmatched char becomes a 1-char unit
                // with primary == key, so it is carried through display and commit as itself.
                //
                // When NOT composing, an unmapped letter still passes through to the host: starting a
                // composition from a letter the scheme cannot use would strand the user in an unusable
                // sequence, and the host keeps normal typing.
                //
                // NOTE: this is NOT the layer below — letters return from this block and never
                // reach it.
                if (!Engine.IsAcceptedInputChar((char)vkCode) && !IsComposing())
                {
                    return false;
                }
                Engine.AddInput((char)vkCode);
                Engine.EnqueueInput((int)vkCode, isShiftPressed ? 1 : 0, Engine.GetRawBuffer());
                return true;
            }

            // The plain ' key while composing:
            //   · 1st tap  → SEPARATOR: append U+0027. It breaks the greedy span (n'g→n+g) or completes an
            //                adapter token (k'→ᠺ) and then disappears (consumed on commit) — "just separate".
            //   · 2nd consecutive tap (last buffer char already U+0027) → FORCE-COMMIT (like Enter) + a trailing
            //                literal '. This is the literal path (the only way a real ' reaches the host) and
            //                the Rime convention. The pending U+0027 is consumed by BuildCommitString; the literal
            //                ' is appended via _pendingTrailingLiteral; commit runs as-typed (Enter semantics).
            // (The double-apostrophe ESCAPE r''h is on RIGHT SHIFT, which appends U+0027 without this
            //  force-commit interception.)
            if (IsComposing() && vkCode == 0xDE && !isShiftPressed)
            {
                // THE CHECK AND THE ACTION READ THE SAME PLACE.
                //
                // Asking whether the LAST character of the whole buffer is already a separator does
                // not match the insertion below, which goes through InsertAtFocus and writes at the
                // FOCUSED unit's trailing edge. With focus anywhere but the tail those are different
                // positions: the first tap would put a separator where such a check never looks, the
                // check would stay false, and the second tap would write a second ZWNJ instead of
                // force-committing.
                //
                // The insertion offset does NOT move when a separator lands there — ParseSnapshotUnits does
                // not grow the focused unit over it — so the separator ends up immediately AFTER the
                // insertion point, at buffer[at]. That is the character to look at, and looking at it is
                // the same question as "am I about to insert a separator where one already is".
                //
                // At the tail this is identical to a buffer[^1] check: with focus on the last unit
                // at == length, buffer[at] is the separator that was appended, the same one buffer[^1]
                // reads.
                //
                // The insertion itself is focus-aware: the separator lands at the FOCUSED unit, not at
                // the buffer tail.
                string aposBuf = Engine.GetRawBuffer();
                int sepAt = Engine.GetFocusInsertOffset();
                if (sepAt >= 0 && sepAt < aposBuf.Length && aposBuf[sepAt] == ManjuEngine.SeparatorMarker)
                {
                    _selectedCandidate = GetCompositionString();   // commit as-typed (Enter)
                    _selectedCandidateCommitPending = true;
                    _pendingTrailingLiteral = "'";                 // append a real literal '
                    _forceCommitAsTyped = true;                    // TextService → commitAsTyped like Enter
                    return true;
                }
                // Route through the shared focus-aware insertion path so the separator lands at the FOCUSED unit.
                // An append-only insertion would ignore focus and act on the last element only.
                Engine.InsertAtFocus(ManjuEngine.SeparatorMarker.ToString());   // ' → ZWNJ separator (never an adapter)
                Engine.EnqueueInput((int)vkCode, 0, Engine.GetRawBuffer());
                return true;
            }

            // Suffix connector: SHARED logic for BOTH the '-' key (unshifted
            // VK_OEM_MINUS) AND Shift+Space (below). Both target MVS by default; NNBSP only
            // when toggled. Single source =
            // Engine.SuffixConnectorChar (MVS default; NNBSP when the yaml setting or the connector codepoint menu row picks it). '--'
            // (a '-' right after an existing connector) forces the toggle-independent explicit MVS —
            // the word-internal feminine tail, still reachable even in NNBSP mode. Claimed in both gates.
            if (IsComposing() && vkCode == 0xBD && !isShiftPressed)
            {
                InsertSuffixConnector(vkCode);
                return true;
            }

            // 3. special key-combination intercepts
            // Shift + Space -> the suffix connector (the same logic as '-')
            if (vkCode == VK_SPACE && isShiftPressed)
            {
                InsertSuffixConnector(vkCode);
                return true;
            }

            // 4. PUNCTUATION. The five key and layer pairs of ManchuPunctuation give Manchu punctuation.
            // Every other symbol key gives, in either layer, the text its keycap carries on the current
            // keyboard layout (SymbolKeycap). A key whose keycap gives no usable text goes on below.
            //
            // Both append AT THE TAIL, not at the focus. Each mark is a unit of its own, so it is
            // ordinary input and follows the ordinary rule. InsertAtFocus is
            // reserved for the three marks that ATTACH to an existing unit and occupy no unit of their own
            // (apostrophe adapter, separator/ZWNJ, suffix connector).
            //
            // Why the tail: with the sentinel resolving to unit 0 (pre), a comma inserted at the focus
            // after a word would land after the FIRST unit, re-cut every unit behind it, and leave the
            // picks keyed to those units with no unit to be keyed to.
            string? mark = ManchuPunctuation(vkCode, isShiftPressed) ?? SymbolKeycap(vkCode, isShiftPressed);
            if (mark != null)
            {
                Engine.AppendAtTail(mark);
                Engine.EnqueueInput((int)vkCode, isShiftPressed ? 1 : 0, Engine.GetRawBuffer());
                return true;
            }

            // UNIFIED COMPOSING HOLD LAYER — the principle is
            // JP-IME style (intercept everything: fussy, but safer): while composing, only explicit control
            // actions may end the composition; every other ordinary text key is caught instead of falling
            // through to the host and interrupting the word.
            //
            // Letters do NOT reach this layer: the A-Z branch far above intercepts them and returns
            // there. The symbol keys that reach it are the ones whose keycap gives no usable text on the
            // current layout. They also have to be claimed by TextService.IsSemanticImeOwnedKey, or TSF
            // hands them to the host, the engine never runs, and the layer is unreachable in a real host.
            // The ownership check claims every ordinary text key while composing, which is what makes
            // the layer live.
            //
            // SCOPE (deliberately NOT literally every key): ORDINARY TEXT keys only. Navigation / paging /
            // digits / function keys carry composition-CONTROL semantics owned by the presenter and the
            // ownership check; swallowing them unconditionally breaks the routing invariant that arrow
            // and digit keys pass through unless explicitly allowed. Ctrl/Alt/Win combos never reach
            // here, because the ownership check filters them, so host shortcuts stay safe.
            if (IsComposing() && IsOrdinaryTextKey(vkCode))
            {
                return true;
            }
            return false;
        }

        /// <summary>
        /// An ORDINARY TEXT key: a printable letter or punctuation/symbol that carries
        /// no composition-control meaning. These are what the composing layer must catch so a stray press
        /// never force-commits: apart from explicit control actions, ordinary letters and symbols should all be caught by the composing layer.
        /// Excludes navigation / paging / digits / function keys on purpose — see the call site.
        /// </summary>
        // internal, NOT private: TextService.IsSemanticImeOwnedKey calls this same
        // predicate so the ownership gate and the hold layer can never disagree about which keys are
        // "ordinary text". Two independent copies of this set could drift apart, and the layer would
        // then claim keys that TSF never routes to it.
        internal static bool IsOrdinaryTextKey(uint vkCode)
        {
            if (vkCode >= 0x41 && vkCode <= 0x5A) return true;              // A-Z
            return IsOemSymbolKey(vkCode);
        }

        /// <summary>
        /// THE MANCHU PUNCTUATION TABLE: the only key and layer pairs that give Manchu punctuation.
        /// ',' gives U+1802, '.' U+1803, Shift+';' U+1804, '[' U+1808, ']' U+1809. Null for every other
        /// pair; the other layer of these keys gives its keycap (SymbolKeycap).
        /// </summary>
        internal static string? ManchuPunctuation(uint vkCode, bool shift) => (vkCode, shift) switch
        {
            (VK_OEM_COMMA, false) => "\u1802",  // ᠂
            (VK_OEM_PERIOD, false) => "\u1803", // ᠃
            (VK_OEM_1, true) => "\u1804",       // ᠄
            (VK_OEM_4, false) => "\u1808",      // ᠈
            (VK_OEM_6, false) => "\u1809",      // ᠉
            _ => null,
        };

        /// <summary> A symbol key is an OEM punctuation key in either layer, or a main-row digit with
        /// Shift. Outside the Manchu punctuation table it gives the text its keycap carries on the current
        /// keyboard layout (Keycap.For). Null for any other key, and when the keycap gives no usable text.</summary>
        internal static string? SymbolKeycap(uint vkCode, bool shift)
        {
            bool symbolKey = IsOemSymbolKey(vkCode) || (shift && vkCode >= 0x30 && vkCode <= 0x39);
            return symbolKey && ManchuPunctuation(vkCode, shift) == null ? Keycap.For(vkCode, shift) : null;
        }

        /// <summary> Does this key and layer give Manchu punctuation or a keycap? Both key gates ask this.</summary>
        internal static bool ClaimsPunctuationKey(uint vkCode, bool shift)
            => ManchuPunctuation(vkCode, shift) != null || SymbolKeycap(vkCode, shift) != null;

        /// <summary> The OEM punctuation keys: VK 0xBA-0xC0 (; = , - . / `), 0xDB-0xDF ([ \ ] ' and OEM_8),
        /// 0xE2 (OEM_102, the extra key of a 102-key keyboard).</summary>
        internal static bool IsOemSymbolKey(uint vkCode)
            => (vkCode >= 0xBA && vkCode <= 0xC0) || (vkCode >= 0xDB && vkCode <= 0xDF) || vkCode == 0xE2;

        // The ONE place that inserts
        // the suffix connector, shared by '-' and Shift+Space. Always exactly ONE codepoint =
        // Engine.SuffixConnectorChar (MVS default; NNBSP when the yaml setting or the connector codepoint menu row picks it). No '--' special
        // case — pressing twice just yields two connectors; MVS↔NNBSP is governed solely by the toggle.
        private void InsertSuffixConnector(uint vkCode)
        {
            // Suffix connector is also a position-targeting insertion, so it takes the same shared path:
            // every insertion must honour focus.
            Engine.InsertAtFocus(Engine.SuffixConnectorChar.ToString());
            Engine.EnqueueInput((int)vkCode, 0, Engine.GetRawBuffer());
        }

        // Right-Shift escape: append the U+0027 escape marker after the previous letter
        // (specialize / separate) and refresh. Called from TextService.OnKeyUp on a lone RIGHT-Shift tap
        // while composing in manju mode. Returns true if it did something (so the caller repaints).
        public bool ApplyShiftEscape()
        {
            if (!IsComposing()) return false;
            if (!Engine.ApplyShiftEscape()) return false;
            Engine.EnqueueInput(0, 0, Engine.GetRawBuffer());
            return true;
        }

        public string GetCompositionString() => Engine.GetTranslatedText();

        public List<string> GetCurrentSnapshotCandidates() => Engine.GetCandidatesFromSnapshot(Engine.GetRawBuffer());

        public void Clear()
        {
            Engine.Clear();
        }

        public bool IsComposing() => Engine.GetRawBuffer().Length > 0;
    }
}

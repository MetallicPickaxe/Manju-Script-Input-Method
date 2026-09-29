using System;
using System.Collections.Generic;
using CSharpTSFInput.Rendering.MongolianVertical;
using CSharpTSFInput.WordDictionary;

namespace CSharpTSFInput.UI
{
    /// <summary>
    /// Floating Knowledge Window — dictionary lookup display.
    ///
    /// Design:
    ///   Position:  left of main IME window, suspended, top-aligned, fixed offset (configurable)
    ///   Layout:
    ///     row 0:   digits 1, 2, ..., 9, 0 (numeric tags, horizontal)
    ///     body:    N column-groups, each = vertical Manchu + vertical Latin pair
    ///              column-groups separated by vertical divider lines
    ///              digit row aligns to column-group centers
    ///     row N+1: gloss bar (horizontal Latin romanization + English)
    ///   Highlight: when focus is in knowledge window AND user has highlighted a column N,
    ///              that column's bottom divider segment disappears, gloss bar visually
    ///              "spills" from N (the block underneath is a continuation of it)
    ///   Min gloss bar width fixed; fewer columns than width allows → blank placeholder columns
    ///
    /// Lifecycle:
    ///   Show/hide synchronized with main candidate window. Sources:
    ///     - new RenderFrame with non-empty word-prefix matches → show both
    ///     - ESC / backspace-empty / focus loss / EndComposition → hide both
    ///
    /// Input:
    ///   Tab key toggles focus context between main window and knowledge window
    ///   When knowledge window has focus context:
    ///     1-9 keys → idx 0..8 word pick → commit whole word + EndComposition
    ///     0 key    → idx 9 word pick    → commit whole word + EndComposition
    ///   Mouse click on a column-group → set highlight to that idx (does not auto-commit;
    ///   user can confirm with number key or Enter)
    ///
    /// Scope: this class provides the LAYOUT MATH + state machine. The HWND, D2D rendering and
    /// window procedure live in KnowledgeWindowHost.
    /// </summary>
    public sealed class KnowledgeWindow
    {
        /// <summary>One word entry to display.</summary>
        public sealed class Entry
        {
            public string ManchuScript { get; }
            public string Mollendorff { get; }
            public string Gloss { get; }       // Combined
            // Per-unit romanization segments (digraph-aware) + code-point span, for the
            // host's per-row Latin annotation (multi-letter units draw horizontally at their glyph row).
            public IReadOnlyList<(string latin, int cpLen)> RomanSegments { get; }
            public Entry(string manchu, string mollendorff, string gloss,
                         IReadOnlyList<(string latin, int cpLen)>? segments = null)
            {
                ManchuScript = manchu ?? string.Empty;
                Mollendorff = mollendorff ?? string.Empty;
                Gloss = gloss ?? string.Empty;
                RomanSegments = segments ?? System.Array.Empty<(string, int)>();
            }
        }

        public const int MaxEntriesDisplayed = 10; // locked: numeric row 1..9, 0
        public const float ColumnGroupWidth = 60f; // DIP, includes Manchu + Latin sub-columns
        public const float DigitRowHeight = 24f;
        public const float DividerThickness = 1f;
        public const float GlossBarHeight = 60f;
        public const float MinGlossBarWidth = 360f;

        // Thread safety. SetEntries is called from the WORKER thread (after
        // the dictionary lookup); paint, focus, highlight setters are called from the UI thread.
        // Without synchronization, the worker can mutate _entries while UI iterates Entries
        // during paint → either crash (collection modified) or visual tear. All public state
        // accesses + mutations go through _stateLock.
        private readonly object _stateLock = new();
        private readonly List<Entry> _entries = new();
        private int _highlightedIdx = -1;
        private bool _hasFocusContext = false;

        /// <summary>Snapshot copy for safe iteration off-thread.</summary>
        public IReadOnlyList<Entry> Entries
        {
            get
            {
                lock (_stateLock)
                {
                    return new List<Entry>(_entries);
                }
            }
        }
        public int HighlightedIdx { get { lock (_stateLock) { return _highlightedIdx; } } }
        public bool HasFocusContext { get { lock (_stateLock) { return _hasFocusContext; } } }

        /// <summary>Populate entries from a dictionary lookup; truncate to MaxEntriesDisplayed.</summary>
        public void SetEntries(IReadOnlyList<LemmaEntry> lemmas)
        {
            lock (_stateLock)
            {
                _entries.Clear();
                if (lemmas != null)
                {
                    int n = Math.Min(MaxEntriesDisplayed, lemmas.Count);
                    for (int i = 0; i < n; i++)
                    {
                        var l = lemmas[i];
                        string gloss = ComposeGloss(l);
                        _entries.Add(new Entry(l.Word, l.Mollendorff, gloss, l.RomanSegments));
                    }
                }
                // Each lookup is a fresh result set → default-select entry 0 so
                // the column highlight and the gloss bar both point at the top match.
                // A previous click's index is meaningless for a new word.
                _highlightedIdx = _entries.Count > 0 ? 0 : -1;
            }
        }

        public void Clear()
        {
            lock (_stateLock)
            {
                _entries.Clear();
                _highlightedIdx = -1;
                _hasFocusContext = false;
            }
        }

        /// <summary> Tab key toggles focus context (main vs knowledge window).</summary>
        public void ToggleFocusContext()
        {
            lock (_stateLock) { _hasFocusContext = !_hasFocusContext; }
        }


        /// <summary>Move highlight by delta (←/→ navigation within knowledge window).</summary>
        public void MoveHighlight(int delta)
        {
            lock (_stateLock)
            {
                if (_entries.Count == 0) return;
                int next = _highlightedIdx + delta;
                if (next < 0) next = 0;
                if (next >= _entries.Count) next = _entries.Count - 1;
                _highlightedIdx = next;
            }
        }

        public void SetHighlight(int idx)
        {
            lock (_stateLock)
            {
                if (idx < -1 || idx >= _entries.Count) return;
                _highlightedIdx = idx;
            }
        }

        /// <summary>
        /// Map numeric key to entry index, respecting the 1-0 convention.
        /// VK_1..VK_9 → idx 0..8; VK_0 → idx 9. Returns -1 for out-of-range or invalid.
        /// </summary>
        public static int MapNumericKeyToIdx(uint vkCode)
        {
            if (vkCode == 0x30) return 9;                       // VK_0 → idx 9
            if (vkCode >= 0x31 && vkCode <= 0x39) return (int)(vkCode - 0x31); // VK_1..VK_9 → 0..8
            return -1;
        }

        /// <summary>
        /// Try to pick an entry by numeric key. Returns the entry to commit,
        /// or null if not applicable (no entries, idx out of range, or no focus context).
        /// </summary>
        public Entry? TryPickByNumericKey(uint vkCode)
        {
            lock (_stateLock)
            {
                if (!_hasFocusContext) return null;
                int idx = MapNumericKeyToIdx(vkCode);
                if (idx < 0 || idx >= _entries.Count) return null;
                return _entries[idx];
            }
        }

        // ------------------ Layout math (pure, AOT-friendly) ------------------


        /// <summary>Total window height given longest Manchu word's glyph count and font size.</summary>
        public static float ComputeWindowHeight(int maxGlyphCount, float fontSize, float padding)
        {
            float bodyHeight = maxGlyphCount * fontSize; // approximation; real layout uses YAdvance
            return DigitRowHeight + bodyHeight + GlossBarHeight + 3f * padding;
        }


        /// <summary>
        /// Display number for entry index per the 1-0 convention.
        /// idx 0..8 → "1".."9";  idx 9 → "0".
        /// </summary>
        public static string DisplayDigitForIdx(int idx)
        {
            if (idx == 9) return "0";
            if (idx >= 0 && idx <= 8) return (idx + 1).ToString();
            return "";
        }

        // ------------------ Helpers ------------------

        private static string ComposeGloss(LemmaEntry lemma)
        {
            // Format: "{pos}. {first gloss}" — pick first English gloss, with POS abbrev.
            string pos = string.IsNullOrEmpty(lemma.Pos) ? "" : (lemma.Pos + ". ");
            string first = lemma.Glosses != null && lemma.Glosses.Count > 0 ? lemma.Glosses[0] : "";
            return StripOriginalScript(pos + first);
        }

        // Many corpus glosses embed the original Manchu
        // spelling. The gloss bar renders with the SYSTEM font
        // horizontally, so an embedded vertical-script word would come out as a horizontal run of
        // Manchu glyphs, which breaks the IME's vertical-script premise and looks wrong.
        // The gloss needs nothing from the raw spelling, so: cut the gloss at the first
        // Mongolian-block codepoint (U+1800–U+18AF) and trim trailing connective words/punct.
        // The meaningful English part stays; the embedded original spelling is dropped.
        public static string StripOriginalScript(string text)
        {
            if (string.IsNullOrEmpty(text)) return text ?? string.Empty;
            int cut = -1;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                // Mongolian block + Mongolian Supplement-adjacent control chars (FVS/MVS/NNBSP).
                if ((c >= '᠀' && c <= '᢯') || c == ' ' || c == '‌' || c == '‍')
                {
                    cut = i;
                    break;
                }
            }
            string result = cut >= 0 ? text.Substring(0, cut) : text;
            result = result.TrimEnd();
            // Trim a dangling "of" / "of." / connective.
            foreach (var tail in new[] { " of", " of.", " of:", " of —", "—", "·" })
            {
                if (result.EndsWith(tail, System.StringComparison.OrdinalIgnoreCase))
                {
                    result = result.Substring(0, result.Length - tail.Length).TrimEnd();
                }
            }
            return result;
        }
    }
}

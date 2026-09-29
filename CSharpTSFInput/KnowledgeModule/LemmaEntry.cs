using System.Collections.Generic;

namespace CSharpTSFInput.WordDictionary
{
    /// <summary>
    /// One Manchu lemma entry, in the shape the IME consumes.
    ///
    /// This type describes the SHAPE a lemma row must have, and any corpus that
    /// provides that shape works. Restricted to the fields actually read at lookup time — verbose
    /// fields are deliberately not
    /// represented; they stay in the on-disk JSONL and can be reread if richer metadata is wanted.
    /// </summary>
    public sealed class LemmaEntry
    {
        /// <summary>Manchu-script headword (e.g. "ᠰᠠᡥᠠᠯᡳᠶᠠᠨ ᡠᠯᠠ"). Unicode codepoints.</summary>
        public string Word { get; }

        /// <summary>Möllendorff Latin romanization (e.g. "sahaliyan ula"). Index key.</summary>
        public string Mollendorff { get; }

        /// <summary>Part of speech tag as the corpus gives it (noun / verb / adjective / particle / ...).</summary>
        public string Pos { get; }

        /// <summary>List of English glosses (one entry per sense in the corpus).</summary>
        public IReadOnlyList<string> Glosses { get; }

        /// <summary>Optional etymology text. Null when absent.</summary>
        public string? EtymologyText { get; }

        /// <summary>Per-unit romanization segments of <see cref="Word"/> (digraph-aware),
        /// each with the code-point count it spans. Lets the knowledge window draw multi-letter units
        /// (ng/dz/ū/ts) horizontally at their glyph row instead of one char per glyph. Populated lazily
        /// at lookup time (needs the dictionary's romanizer); null until then.</summary>
        public IReadOnlyList<(string latin, int cpLen)>? RomanSegments { get; set; }

        public LemmaEntry(string word, string mollendorff, string pos, IReadOnlyList<string> glosses, string? etymologyText)
        {
            Word = word ?? string.Empty;
            Mollendorff = mollendorff ?? string.Empty;
            Pos = pos ?? string.Empty;
            Glosses = glosses ?? new List<string>();
            EtymologyText = etymologyText;
        }
    }
}

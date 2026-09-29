using System.Collections.Generic;
using CSharpTSFInput.ManjuShaper;

namespace CSharpTSFInput.Messaging
{
    /// <summary>
    /// Per-position candidate matrix.
    ///
    /// The whole block goes to HarfBuzz, which emits a candidate for every character, so what
    /// comes back is a matrix.
    ///
    /// Shape:
    ///   - RowCount = number of input units (== input.Length for non-digraph input)
    ///   - For each position k in 0..RowCount-1:
    ///       Cells[k] = list of candidate cells, each:
    ///         - CodepointString : pure-Mongolian string up to and including position k
    ///                             (i.e., prefix derived from current selections + this alt at position k)
    ///         - ShapedGlyphs    : pre-shaped via HarfBuzz on the FULL hypothetical input
    ///                             (input with this alt at position k, rest from current selections),
    ///                             then trimmed to glyphs 0..k. This is what makes joining-form
    ///                             initial/medial/final correct.
    ///         - SemanticItem    : enriched display metadata for this cell
    ///
    /// At display time, presenter selects matrix row by focus position. Candidate count at any
    /// focus row = alternatives count for that input character (primary + Dictionary_Alternatives
    /// entries + per-base FVS variants), after the same-as-bare collapse is applied per-cell.
    ///
    /// At commit time, for each position p, if user picked alt at p with highlight idx j, take
    /// Cells[p][j].CodepointString.Substring(prefix_length_at_p) as the codepoint(s) for that
    /// position; otherwise commit raw Latin character.
    ///
    /// Critical invariant: HarfBuzz shaping is done on the **full hypothetical input**, never on
    /// per-position substrings. This is the whole point — joining-form rules need full context.
    /// Without this, every focus position's glyph would be in "isolated" or "final" form because
    /// HB sees a 1-char or end-of-string substring. With this, position 0 gets initial form,
    /// middle positions get medial, last gets final — the what-you-type-is-what-you-see requirement.
    /// </summary>
    public class CandidateMatrix
    {
        public readonly int RowCount;
        public readonly List<List<MatrixCell>> Cells;
        public readonly ushort Upem;

        public CandidateMatrix(int rowCount, List<List<MatrixCell>> cells, ushort upem)
        {
            RowCount = rowCount;
            Cells = cells;
            Upem = upem;
        }

        /// <summary>Get candidate codepoint strings at the given focus position (a view for code
        /// paths that consume List&lt;string&gt; candidates).</summary>
        public List<string> GetCandidateStringsAt(int focusPosition)
        {
            var result = new List<string>();
            if (focusPosition < 0 || focusPosition >= Cells.Count) return result;
            foreach (var cell in Cells[focusPosition])
            {
                result.Add(cell.CodepointString);
            }
            return result;
        }

        /// <summary>Get pre-shaped glyph sequences at the given focus position (one per candidate
        /// at that position). Worker uses these directly for RenderFrame.ShapedResults instead of
        /// a per-candidate FullShape pass.</summary>
        public List<List<ManjuShaperCore.FinalGlyph>> GetShapedGlyphsAt(int focusPosition)
        {
            var result = new List<List<ManjuShaperCore.FinalGlyph>>();
            if (focusPosition < 0 || focusPosition >= Cells.Count) return result;
            foreach (var cell in Cells[focusPosition])
            {
                result.Add(cell.ShapedGlyphs);
            }
            return result;
        }

        /// <summary>Get semantic items at the given focus position.</summary>
        public List<SemanticCandidateItem> GetSemanticItemsAt(int focusPosition)
        {
            var result = new List<SemanticCandidateItem>();
            if (focusPosition < 0 || focusPosition >= Cells.Count) return result;
            foreach (var cell in Cells[focusPosition])
            {
                result.Add(cell.SemanticItem);
            }
            return result;
        }

        /// <summary>
        /// Per-cell position-slice list at the given focus position.
        /// One slice per candidate; slice[hl] is what the user picked AT POSITION focusPosition
        /// when they chose candidate hl. Used by presenter to pass the EXACT codepoint slice into
        /// MarkPositionSelected without any shared-prefix reconstruction.
        /// </summary>
        public List<string> GetSlicesAt(int focusPosition)
        {
            var result = new List<string>();
            if (focusPosition < 0 || focusPosition >= Cells.Count) return result;
            foreach (var cell in Cells[focusPosition])
            {
                result.Add(cell.CodepointSliceAtPosition);
            }
            return result;
        }
    }

    public class MatrixCell
    {
        public readonly string CodepointString;

        /// <summary>
        /// The codepoint slice this cell contributes AT ITS POSITION ONLY —
        /// i.e. just the new codepoints that replace position k's content when this candidate is
        /// chosen. For an FVS variant this includes the base letter AND the FVS modifier
        /// (e.g. "ᠨ᠋"); for a plain alt this is just the alt codepoint(s) (e.g. "ᠩ"); for the
        /// primary this is the primary mapping (e.g. "ᠨ").
        ///
        /// Why this exists: deriving the slice from CodepointString by shared-prefix subtraction
        /// fails for FVS variants, which EXTEND the primary slice at the same position and do not
        /// REPLACE it. The subtraction would strip the base letter and leave only the FVS, so the
        /// commit string would drop the base letter whenever an FVS variant is picked.
        ///
        /// Presenter reads this field via the worker-built frame and passes it verbatim into
        /// ManjuEngine.MarkPositionSelected so BuildCommitString emits the correct codepoint
        /// sequence (base letter + FVS).
        /// </summary>
        public readonly string CodepointSliceAtPosition;

        public readonly List<ManjuShaperCore.FinalGlyph> ShapedGlyphs;
        public readonly SemanticCandidateItem SemanticItem;

        public MatrixCell(string codepointString, string codepointSliceAtPosition, List<ManjuShaperCore.FinalGlyph> shapedGlyphs, SemanticCandidateItem semanticItem)
        {
            CodepointString = codepointString;
            CodepointSliceAtPosition = codepointSliceAtPosition ?? string.Empty;
            ShapedGlyphs = shapedGlyphs;
            SemanticItem = semanticItem;
        }
    }
}

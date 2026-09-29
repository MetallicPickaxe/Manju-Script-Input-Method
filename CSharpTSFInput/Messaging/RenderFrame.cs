using System.Collections.Generic;
using System.Text;
using CSharpTSFInput.ManjuShaper;

namespace CSharpTSFInput.Messaging
{
    // The complete, immutable snapshot the UI needs in order to render
    public class RenderFrame
    {
        public readonly List<List<ManjuShaperCore.FinalGlyph>> ShapedResults;
        public readonly List<float> CandidateLengths;
        public readonly List<string> Candidates;
        public readonly ushort Upem; // the font's real UPEM, used for render scaling
        public int FrameId { get; internal set; }

        /// <summary>
        /// Semantic candidate items with enriched per-candidate metadata.
        /// </summary>
        public readonly List<SemanticCandidateItem> SemanticItems;

        /// <summary>
        /// Original input keystroke buffer (Latin/Cyrillic raw text) at the time
        /// this frame was built. Sourced from worker via input.CompositionSnapshot to guarantee
        /// atomic source-consistency with ShapedResults (single-frame swap invariant).
        /// Consumed by CandidateListUIPresenter for the Latin echo column.
        /// </summary>
        public readonly string RawInput;

        /// <summary>
        /// Per-candidate codepoint slice at the focus position. One
        /// entry per candidate, parallel to Candidates / ShapedResults / SemanticItems. Sourced
        /// from CandidateMatrix.Cells[focus][hl].CodepointSliceAtPosition. Presenter passes the
        /// entry verbatim into MarkPositionSelected at pick time — no shared-prefix subtraction.
        /// Empty list when no matrix produced this frame (defensive default; engine then falls
        /// back to legacy 3-arg path).
        /// </summary>
        public readonly List<string> SlicesAtFocus;

        /// <summary>
        /// The focus position this frame's candidate column was built FOR
        /// (the worker's focusResolved). RawInput alone under-identifies a frame — the same "gisun" has
        /// distinct focus-0 and focus-1 frames with different candidate content.
        /// -1 = unknown/empty frame.
        /// </summary>
        public readonly int FocusResolved;

        /// <summary> The composition generation this frame was built in. Consumers
        /// (TextService frame delivery, presenter update path) reject frames whose epoch no longer
        /// matches the engine — the consumer-side half of the ghost-frame protocol, covering the
        /// "published just before Clear, consumed just after" ordering that publish-side gating alone
        /// cannot. -1 = legacy/unknown (a frame built directly, outside the engine).</summary>
        public readonly int SourceEpoch;

        /// <summary> Which ManjuEngine produced this frame. GlobalState is process-static,
        /// so epoch alone cannot separate two live engines that happen to share a generation number:
        /// engine A's presenter would accept engine B's frame. Consumers check owner
        /// first. -1 = legacy/unknown (a frame built directly, outside the engine).</summary>
        public readonly int OwnerEngineId;

        public RenderFrame(
            List<List<ManjuShaperCore.FinalGlyph>> shaped,
            List<float> lengths,
            List<string> candidates,
            List<SemanticCandidateItem> semanticItems,
            ushort upem = 1000,
            string rawInput = "",
            List<string>? slicesAtFocus = null,
            int focusResolved = -1,
            int sourceEpoch = -1,
            int ownerEngineId = -1)
        {
            OwnerEngineId = ownerEngineId;
            ShapedResults = shaped;
            CandidateLengths = lengths;
            Candidates = candidates;
            SemanticItems = semanticItems;
            Upem = upem;
            RawInput = rawInput ?? "";
            SlicesAtFocus = slicesAtFocus ?? new List<string>();
            FocusResolved = focusResolved;
            SourceEpoch = sourceEpoch;
        }

        public bool IsEmptyFrame =>
            Candidates.Count == 0 &&
            ShapedResults.Count == 0 &&
            CandidateLengths.Count == 0 &&
            SemanticItems.Count == 0;

        public bool IsIntegrityComplete =>
            IsEmptyFrame ||
            (Candidates.Count == ShapedResults.Count &&
             Candidates.Count == CandidateLengths.Count &&
             Candidates.Count == SemanticItems.Count &&
             Upem > 0);

        public string GetIntegrityReason()
        {
            if (IsEmptyFrame)
            {
                return "empty-frame";
            }

            List<string> reasons = new();
            if (Candidates.Count != ShapedResults.Count)
            {
                reasons.Add("candidates!=shaped");
            }

            if (Candidates.Count != CandidateLengths.Count)
            {
                reasons.Add("candidates!=lengths");
            }

            if (Candidates.Count != SemanticItems.Count)
            {
                reasons.Add("candidates!=semanticItems");
            }

            if (Candidates.Count > 0 && Upem == 0)
            {
                reasons.Add("upem==0");
            }

            return reasons.Count == 0 ? "aligned" : string.Join("|", reasons);
        }

        public string DescribeIntegrity()
        {
            var builder = new StringBuilder();
            builder.Append("frameId=").Append(FrameId);
            builder.Append(", candidates=").Append(Candidates.Count);
            builder.Append(", shaped=").Append(ShapedResults.Count);
            builder.Append(", lengths=").Append(CandidateLengths.Count);
            builder.Append(", semanticItems=").Append(SemanticItems.Count);
            builder.Append(", upem=").Append(Upem);
            builder.Append(", complete=").Append(IsIntegrityComplete);
            builder.Append(", reason=").Append(GetIntegrityReason());
            return builder.ToString();
        }
    }
}

namespace CSharpTSFInput.Messaging
{
    /// <summary>
    /// Semantic candidate item contract.
    /// Carries enriched per-candidate semantics from backend to frontend.
    /// SemanticName is a first-class field for Row 2.
    /// </summary>
    public class SemanticCandidateItem
    {
        /// <summary>
        /// Display text for the candidate (the actual Mongolian string).
        /// </summary>
        public string DisplayText { get; }

        /// <summary>
        /// Row 3: Code-point sequence joined with '+' for multi-code-point candidates.
        /// Example: "U+1820" or "U+1820+U+180B"
        /// </summary>
        public string CodePointSequence { get; }

        /// <summary>
        /// Candidate kind / origin classification.
        /// </summary>
        public CandidateKind Kind { get; }

        /// <summary>
        /// Variant/form meaning label where applicable.
        /// Empty string if not a variant candidate.
        /// </summary>
        public string VariantLabel { get; }

        /// <summary>
        /// Row 2: Enriched semantic name or description.
        /// Example: "dictionary", "base", "variant 1"
        /// </summary>
        public string SemanticName { get; }

        public SemanticCandidateItem(
            string displayText,
            string codePointSequence,
            CandidateKind kind,
            string variantLabel,
            string semanticName)
        {
            DisplayText = displayText;
            CodePointSequence = codePointSequence;
            Kind = kind;
            VariantLabel = variantLabel;
            SemanticName = semanticName;
        }

        /// <summary>
        /// Returns a new SemanticCandidateItem with overridden semantic name.
        /// Used by Worker post-shaping pass to relabel FVS variants that produce glyph sequences
        /// identical to the bare candidate ("variant" → "same as base").
        /// </summary>
        public SemanticCandidateItem WithSemanticName(string newSemanticName)
        {
            return new SemanticCandidateItem(
                DisplayText,
                CodePointSequence,
                Kind,
                VariantLabel,
                newSemanticName);
        }

        /// <summary>
        /// Helper to generate code-point sequence string from display text.
        /// </summary>
        public static string GenerateCodePointSequence(string? text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;

            var codePoints = new System.Collections.Generic.List<string>();
            for (int i = 0; i < text.Length; i++)
            {
                int codePoint;
                if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    codePoint = char.ConvertToUtf32(text[i], text[i + 1]);
                    i++; // Skip low surrogate
                }
                else
                {
                    codePoint = text[i];
                }
                codePoints.Add($"U+{(uint)codePoint:X4}");
            }
            return string.Join("+", codePoints);
        }

        /// <summary>
        /// Helper to generate enriched semantic name for Row 2.
        /// </summary>
        public static string GenerateSemanticName(CandidateKind kind, string displayText, string variantLabel)
        {
            // Three effect-based values, because a bare "FVS1/FVS2/FVS3" label does not tell the
            // candidates apart:
            //   base           = bare transliteration (no FVS)
            //   variant        = FVS variant whose shaped glyph sequence differs from bare
            //   same as base   = FVS variant whose shaped glyph sequence equals bare (the FVS does nothing here)
            // "same as base" can only be decided after shaping, so this function produces the first two only;
            // the worker overwrites it afterwards through ReplaceSemanticNameForSameAsBare(...).
            // The FVS designation is VISIBLE, so the velar/uvular medial forms can be told apart by
            // which FVS produced them: an FVS variant shows its number ("variant FVS1"). The
            // effect-based scheme is the prefix (base + variant + (post-shaping) same-as-bare),
            // with the FVS number appended for distinct variants.
            // ENGLISH, because the audience is a Manchu LEARNER: a label in another language would
            // assume the reader knows that language.
            //
            // No language switch: a locale system would be its own feature with its own resource layer.
            return kind switch
            {
                CandidateKind.DictionaryMatch => "dictionary",
                CandidateKind.BaseTransliteration => "base",
                CandidateKind.FvsVariant => string.IsNullOrEmpty(variantLabel) ? "variant" : $"variant {variantLabel}",
                _ => "candidate"
            };
        }

        // Constant used by Worker post-shaping pass to mark FVS variants that produce
        // glyph sequences identical to the bare candidate.
        // The VALUE is safe to change because every comparison in the codebase goes through
        // this constant (in ManjuEngine), never through a literal:
        // a duplicated literal would silently stop matching when the value changes.
        public const string LABEL_SAME_AS_BARE = "same as base";

        /// <summary>
        /// Helper to generate variant label for FVS candidates.
        /// </summary>
        public static string GenerateVariantLabel(string? displayText)
        {
            if (string.IsNullOrEmpty(displayText)) return string.Empty;

            // Check for FVS markers at the end
            char lastChar = displayText[displayText.Length - 1];
            return lastChar switch
            {
                '\u180B' => "FVS1",
                '\u180C' => "FVS2",
                '\u180D' => "FVS3",
                '\u180F' => "FVS4", // U+180F (U+180E is MVS, skipped)
                _ => string.Empty
            };
        }
    }

    /// <summary>
    /// Candidate kind / origin classification.
    /// </summary>
    public enum CandidateKind
    {
        /// <summary>Dictionary match candidate.</summary>
        DictionaryMatch,
        
        /// <summary>Base transliteration candidate.</summary>
        BaseTransliteration,
        
        /// <summary>FVS variant candidate created from base.</summary>
        FvsVariant
    }
}

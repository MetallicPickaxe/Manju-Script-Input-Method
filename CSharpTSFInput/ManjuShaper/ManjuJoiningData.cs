using System;

namespace CSharpTSFInput.ManjuShaper
{
    public enum ManjuJoiningType : byte
    {
        U = 0, // Non-joining
        L = 1, // Left-joining
        R = 2, // Right-joining
        D = 3, // Dual-joining
        C = 4, // Join-causing (Nirugu)
        T = 5, // Transparent (Marks)

        // "THE PRODUCT HAS NO JOINING KNOWLEDGE OF ITS OWN HERE — ASK THE GENERAL CATEGORY."
        //
        // This is NOT a joining type a caller can ever receive: `GetJoiningType` resolves it before
        // returning. It exists so the TABLE can say "unknown" without having to pick a wrong answer.
        //
        // It is the same device HarfBuzz uses, and deliberately so —
        //   `#define JOINING_TYPE_X 7  /* means: use general-category to choose between U or T. */`
        // — because the authority's own default rule is stated in exactly those terms.
        X = 6
    }

    public static class ManjuJoiningData
    {
        // Mongolian block joining types (1806 - 18AA)
        //
        // U+1808 (the Manchu comma) and U+1809 (the Manchu full stop) are `0` (U), not `5` (T).
        //
        // A `T` is skipped with `continue` in `ManjuArabicShaper.Join` **before the state transition
        // runs**, so it is invisible to the joining machine. With both cells at `5`, **two letters would
        // join straight through a full stop**, and the full stop would not break words.
        //
        // The value rests on two **external** sources, and both point to `U`:
        //   ① Unicode `ArabicShaping.txt` (18.0.0): within 1800..180F it lists only four rows —
        //      1806/1807/180A/180E — and neither 1808 nor 1809. Its own stated default rule is that an
        //      unlisted code point is `T` only when its general category is Mn/Me/Cf, and `U` otherwise.
        //      These two are `Po` ⇒ **U**.
        //   ② HarfBuzz's `get_joining_type` in `hb-ot-shaper-arabic.cc`: its table covers only 0621..06D3,
        //      so Mongolian falls back to the general category and the same Mn/Me/Cf check ⇒ **U**.
        //
        // The filler value is `6` (`X`), not `5` (`T`).
        //
        // A table that uses `5` as its "unknown" filler picks the one value that makes a character
        // **wholly invisible to the joining machine**, so "unknown" is implemented as the **most
        // aggressive** answer available. The filler cells are **the ten Mongolian digits U+1810-1819**
        // (as `T`, a digit between two letters would let them join through it) and 13 unassigned code
        // points: 23 cells.
        //
        // No single fixed value is right for every such cell, so the filler is a **rule**: a cell for
        // which the product has no joining knowledge of its own is written `6` (`X`), and
        // `GetJoiningType` **falls back to the general category** for `X`, which is exactly the default
        // rule the two external sources **share**:
        //   ① `ArabicShaping.txt`'s own header: an unlisted code point is `T` only when its general
        //      category is Mn/Me/Cf, and `U` otherwise. **Not one of the 23 appears in that file**;
        //      the data row after `1878` is `1880`, and the one after `180A` is `180E`.
        //   ② HarfBuzz's `get_joining_type` does **the same thing** for `JOINING_TYPE_X`.
        //
        // So a code point added in future gets **the authority's own answer** automatically: `T` when it
        // is Mn/Me/Cf, otherwise `U`. A fixed `U` would suit the 23 cells here but not a future mark.
        // The seven cells below that hold `5` are **genuinely `T`**, not filler: FVS1-3 (180B-180D),
        // FVS4 (180F), 1885, 1886 and 18A9. Their general category is Mn.
        //
        // Across U+1806..U+18AA, `GetJoiningType` returns `T` only for general category Mn, Me or Cf.
        private static readonly byte[] MongolianTypes = new byte[]
        {
            /* 1806-181F */ 0,3,0,0,4,5,5,5,0,5,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,6,
            /* 1820-183F */ 3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
            /* 1840-185F */ 3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
            /* 1860-187F */ 3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,6,6,6,6,6,6,6,
            /* 1880-189F */ 0,0,0,0,0,5,5,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,3,
            /* 18A0-18AA */ 3,3,3,3,3,3,3,3,3,5,3
        };

        private static ManjuJoiningType GetJoiningTypeFromGeneralCategory(uint u)
        {
            var cat = char.GetUnicodeCategory((char)u);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark ||
                cat == System.Globalization.UnicodeCategory.EnclosingMark ||
                cat == System.Globalization.UnicodeCategory.Format)
            {
                return ManjuJoiningType.T;
            }
            return ManjuJoiningType.U;
        }

        public static ManjuJoiningType GetJoiningType(uint u)
        {
            // Mongolian FVS/MVS, matching the HarfBuzz specification exactly
            // FVS1 (180B), FVS2 (180C), FVS3 (180D), FVS4 (180F) -> Transparent (T)
            if ((u >= 0x180B && u <= 0x180D) || u == 0x180F) return ManjuJoiningType.T;
            // MVS (180E) -> Non-joining (U)
            if (u == 0x180E) return ManjuJoiningType.U;

            // 1. the main Mongolian block
            if (u >= 0x1800 && u <= 0x18AA)
            {
                if (u < 0x1806) return GetJoiningTypeFromGeneralCategory(u);
                int index = (int)(u - 0x1806);
                if (index < MongolianTypes.Length)
                {
                    var t = (ManjuJoiningType)MongolianTypes[index];
                    // `X` = "the table has no knowledge of its own" ⇒ use the authority's own default rule, never guess a value.
                    // `X` stops here; a caller never receives it.
                    return t == ManjuJoiningType.X ? GetJoiningTypeFromGeneralCategory(u) : t;
                }
            }
            
            // 2. Phags-pa
            if (u >= 0xA840 && u <= 0xA877)
            {
                if (u == 0xA872) return ManjuJoiningType.L; 
                if (u == 0xA873) return ManjuJoiningType.U; 
                return ManjuJoiningType.D;
            }

            // 3. Adlam (a joining script of West Africa)
            if (u >= 0x1E900 && u <= 0x1E94B)
            {
                if (u >= 0x1E944 && u <= 0x1E94A) return ManjuJoiningType.T;
                return ManjuJoiningType.D;
            }

            // 4. general format characters
            if (u == 0x200C) return ManjuJoiningType.U; // ZWNJ
            if (u == 0x200D) return ManjuJoiningType.C; // ZWJ
            if (u == 0x202F) return ManjuJoiningType.U; // NNBSP
            
            return GetJoiningTypeFromGeneralCategory(u);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CSharpTSFInput.ManjuShaper
{
    public unsafe class ManjuShaperCore
    {
        public struct FinalGlyph
        {
            public uint GlyphId;
            public uint Cluster;
            public int XOffset;
            public int YOffset;
            public int XAdvance;
            public int YAdvance;
            /// <summary>
            /// Horizontal advance width in design units.
            /// Used by vertical-rotation renderers to remove the per-glyph centering
            /// component (-(hAdvance/2)) from XOffset, keeping only GPOS adjustments.
            /// </summary>
            public int HAdvance;
        }

        public static List<FinalGlyph> FullShape(string input, byte* fontData, int fontLength, string script = "mong", string lang = "dflt", bool isVertical = true, int ppem = 0, List<string>? features = null, bool removeIgnorables = false)
        {
            var result = new List<FinalGlyph>();
            if (string.IsNullOrEmpty(input) || fontData == null || fontLength <= 0) return result;

            // 0. Unicode Normalization (NFC)
            input = input.Normalize(System.Text.NormalizationForm.FormC);

            using var buffer = new ManjuBuffer();
            buffer.AddUTF16(input);

            var reader = new ManjuFontReader(fontData, fontLength);
            var gsub = new ManjuGsubEngine(reader);
            var gpos = new ManjuGposEngine(reader);
            var metrics = new ManjuMetricsEngine(reader);

            // 1. mark the joining states
            ManjuArabicShaper.Join(buffer);

            string actualScript = script;

            // 2. Unicode -> Initial Glyph ID
            for (int i = 0; i < buffer.Length; i++)
            {
                if ((buffer.Info[i].Props & ManjuGlyphInfo.FLAG_HIDDEN) != 0) continue;

                uint u = buffer.Info[i].Codepoint;
                
                // Pre-check for default ignorables
                if (IsDefaultIgnorable(u))
                {
                    buffer.Info[i].Props |= ManjuGlyphInfo.FLAG_HIDDEN;
                    // Flag U+180F (FVS4) as an FVS too, alongside FVS1-3. The shipped
                    // NotoSansMongolian implements FVS4 variants via GSUB (e.g. U+182C/182D; HarfBuzz
                    // produces the same variants). Without the flag, GSUB matching would skip the hidden
                    // U+180F and those variants would never be selected.
                    if ((u >= 0x180B && u <= 0x180D) || u == 0x180F) buffer.Info[i].Props |= ManjuGlyphInfo.FLAG_IS_FVS;
                }

                bool isVS = (u >= 0xFE00 && u <= 0xFE0F) || (u >= 0xE0100 && u <= 0xE01EF) || (u >= 0x180B && u <= 0x180D) || u == 0x180F; // FVS4 (U+180F) is a variation selector too, like FVS1-3
                
                if (!isVS)
                {
                    uint vs = 0;
                    if (i + 1 < buffer.Length)
                    {
                        uint next = buffer.Info[i + 1].Codepoint;
                        if ((next >= 0xFE00 && next <= 0xFE0F) || (next >= 0xE0100 && next <= 0xE01EF) || (next >= 0x180B && next <= 0x180D) || next == 0x180F) // FVS4 (U+180F) is a variation selector too, like FVS1-3
                        {
                            vs = next;
                        }
                    }

                    uint uvsGid = 0;
                    if (vs != 0) uvsGid = reader.GetGlyphIdUVS(u, vs);

                    if (uvsGid != 0)
                    {
                        buffer.Info[i].Codepoint = uvsGid;
                        buffer.Info[i + 1].Props |= ManjuGlyphInfo.FLAG_HIDDEN;
                        buffer.Info[i + 1].Codepoint = reader.GetGlyphId(vs); 
                        buffer.Info[i + 1].Cluster = buffer.Info[i].Cluster;
                    }
                    else
                    {
                        buffer.Info[i].Codepoint = reader.GetGlyphId(u);
                    }
                }
                else
                {
                    buffer.Info[i].Codepoint = reader.GetGlyphId(u);
                }
            }

            // 3. GSUB substitution
            gsub.ApplyManjuFeatures(buffer, actualScript, lang, isVertical, features);

            // 4. metrics and initial positions
            metrics.ApplyMetrics(buffer, isVertical);

            // 5. GPOS positioning
            gpos.ApplyPositioning(buffer, actualScript, lang, isVertical, ppem, features);

            // 6. handle default-ignorable characters and zero the GDEF mark advances
            var gdefZero = new ManjuGdefEngine(reader);
            for (int i = 0; i < buffer.Length; i++)
            {
                if ((buffer.Info[i].Props & ManjuGlyphInfo.FLAG_HIDDEN) != 0)
                {
                    buffer.Pos[i].XAdvance = 0;
                    buffer.Pos[i].YAdvance = 0;
                    buffer.Pos[i].XOffset = 0;
                    buffer.Pos[i].YOffset = 0;
                }
                else if (gdefZero.GetGlyphClass(buffer.Info[i].Codepoint) == ManjuGdefEngine.GlyphClass.Mark)
                {
                    // HarfBuzz zeroes GDEF-class mark advances after GPOS (the Arabic/default
                    // shaper this engine mirrors = ZERO_WIDTH_MARKS_BY_GDEF_LATE, adjust_offsets=false): a
                    // mark overlaps its base and must not advance the pen; GPOS-set offsets are preserved.
                    // Real Manju marks are FVS/MVS (FLAG_HIDDEN, handled above) — this covers visible GDEF
                    // marks so the shaper matches HarfBuzz on mark-bearing runs.
                    buffer.Pos[i].XAdvance = 0;
                    buffer.Pos[i].YAdvance = 0;
                }
            }

            // 7. export
            ExportVisibleGlyphs(buffer, result, metrics, isVertical);
            return result;
        }

        private static void ExportVisibleGlyphs(ManjuBuffer buffer, List<FinalGlyph> result, ManjuMetricsEngine metrics, bool isVertical)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if ((buffer.Info[i].Props & ManjuGlyphInfo.FLAG_HIDDEN) != 0)
                {
                    continue;
                }

                // For vertical layout, export the horizontal advance so the renderer
                // can remove the -(hAdvance/2) centering component from XOffset.
                int hAdv = isVertical ? (int)metrics.GetGlyphAdvance(buffer.Info[i].Codepoint, false) : 0;

                result.Add(new FinalGlyph
                {
                    GlyphId = buffer.Info[i].Codepoint,
                    Cluster = buffer.Info[i].Cluster,
                    XAdvance = buffer.Pos[i].XAdvance,
                    YAdvance = buffer.Pos[i].YAdvance,
                    XOffset = buffer.Pos[i].XOffset,
                    YOffset = buffer.Pos[i].YOffset,
                    HAdvance = hAdv
                });
            }
        }

        private static bool IsDefaultIgnorable(uint u)
        {
            // Official Unicode Default_Ignorable_Code_Point list
            if (u >= 0x180B && u <= 0x180D) return true; // FVS1-3
            if (u == 0x180E) return true;               // MVS
            if (u == 0x180F) return true;               // FVS4
            if (u >= 0xFE00 && u <= 0xFE0F) return true; // VS1-16
            if (u >= 0xE0100 && u <= 0xE01EF) return true; // VS17-256
            if (u == 0x200B || u == 0x200C || u == 0x200D) return true; // ZWSP, ZWNJ, ZWJ
            if (u >= 0x200E && u <= 0x200F) return true; // LRM, RLM
            if (u >= 0x202A && u <= 0x202E) return true; // LRE, RLE, PDF, LRO, RLO
            if (u >= 0x2060 && u <= 0x206F) return true; // WJ, the invisible math characters, etc.
            if (u == 0xFEFF) return true;                // BOM
            if (u == 0x00AD) return true;                // Soft Hyphen
            if (u == 0x034F) return true;                // CGJ
            if (u == 0x061C) return true;                // ALM
            if (u >= 0xFFF0 && u <= 0xFFF8) return true;
            if (u >= 0x1D173 && u <= 0x1D17A) return true;
            if (u >= 0xE0000 && u <= 0xE007F) return true; // Tags
            return false;
        }
    }
}

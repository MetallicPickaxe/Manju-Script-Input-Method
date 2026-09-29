using System;
using System.Runtime.InteropServices;

namespace CSharpTSFInput.ManjuShaper
{
    /// <summary>
    /// A 1:1 copy of HarfBuzz's hb_glyph_info_t.
    /// Explicit layout, so the same memory is reused at every stage of shaping.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 20)]
    public struct ManjuGlyphInfo
    {
        // physical layer: holds the Unicode code point, or the final glyph ID
        [FieldOffset(0)] public uint Codepoint;

        // mask layer: holds the OpenType feature tags (init/medi/fina and so on)
        [FieldOffset(4)] public uint Mask;

        // cluster layer: tracks the original character index, for backspace and caret handling
        [FieldOffset(8)] public uint Cluster;

        // variable layer: holds the joining action (the Arabic action)
        // In HarfBuzz this memory is reused (var1/var2).
        [FieldOffset(12)] public byte ArabicAction;
        [FieldOffset(13)] public byte JoiningType;
        
        // helper properties
        [FieldOffset(16)] public uint Props;

        // Props Flags
        public const uint FLAG_HIDDEN = 0x0001;
        public const uint FLAG_UNSAFE_TO_BREAK = 0x0002;
        public const uint FLAG_IS_FVS = 0x0004;
    }

    /// <summary>
    /// A 1:1 copy of hb_glyph_position_t: offsets and advances.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Size = 16)]
    public struct ManjuGlyphPosition
    {
        public int XAdvance;
        public int YAdvance;
        public int XOffset;
        public int YOffset;
    }
}

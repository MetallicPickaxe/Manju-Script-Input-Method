using System;

namespace CSharpTSFInput.ManjuShaper
{
    /// <summary>
    /// ManjuMetricsEngine: parses the hmtx and vmtx tables.
    /// This is the part of the HarfBuzz kernel that settles the layout advances.
    /// </summary>
    public unsafe class ManjuMetricsEngine
    {
        private readonly ManjuFontReader _reader;
        private readonly int _hmtxOffset;
        private readonly int _vmtxOffset;
        private readonly ushort _numHMetrics;
        private readonly ushort _numVMetrics;
        private readonly ushort _totalGlyphs;

        public ManjuMetricsEngine(ManjuFontReader reader)
        {
            _reader = reader;
            _totalGlyphs = _reader.GetNumGlyphs();

            // horizontal metrics
            int hheaOffset = _reader.FindTable(0x68686561); // 'hhea'
            _numHMetrics = hheaOffset != -1 ? _reader.ReadU16(hheaOffset + 34) : (ushort)0;
            _hmtxOffset = _reader.FindTable(0x686D7478); // 'hmtx'

            // vertical metrics (the ones that matter for Manju)
            int vheaOffset = _reader.FindTable(0x76686561); // 'vhea'
            _numVMetrics = vheaOffset != -1 ? _reader.ReadU16(vheaOffset + 34) : (ushort)0;
            _vmtxOffset = _reader.FindTable(0x766D7478); // 'vmtx'
        }

        public ushort GetGlyphAdvance(uint glyphId, bool vertical = false)
        {
            if (glyphId >= _totalGlyphs) return 0;

            int offset = vertical ? _vmtxOffset : _hmtxOffset;
            ushort numMetrics = vertical ? _numVMetrics : _numHMetrics;

            if (offset == -1 || numMetrics == 0) return _reader.GetUnitsPerEm(); // a safe default (usually 1000 or 2048)

            if (glyphId < numMetrics)
            {
                // each record is 4 bytes: [Advance(2)] [Bearing(2)]
                return _reader.ReadU16(offset + (int)(glyphId * 4));
            }
            else
            {
                // past the end, the last record's advance applies
                return _reader.ReadU16(offset + (int)((numMetrics - 1) * 4));
            }
        }

        public void ApplyMetrics(ManjuBuffer buffer, bool isVerticalLayout)
        {
            for (int i = 0; i < buffer.Length; i++)
            {
                if ((buffer.Info[i].Props & ManjuGlyphInfo.FLAG_HIDDEN) != 0) continue;

                uint gid = buffer.Info[i].Codepoint;

                if (isVerticalLayout)
                {
                    ushort vAdvance = GetGlyphAdvance(gid, true);
                    ushort hAdvance = GetGlyphAdvance(gid, false);
                    short vOriginY = _reader.GetVerticalOrigin(gid);
                    buffer.Pos[i] = CreateVerticalGlyphPosition(hAdvance, vAdvance, vOriginY);
                }
                else
                {
                    buffer.Pos[i].XAdvance = (short)GetGlyphAdvance(gid, false);
                    buffer.Pos[i].YAdvance = 0;
                }
            }
        }

        private static ManjuGlyphPosition CreateVerticalGlyphPosition(ushort hAdvance, ushort vAdvance, short vOriginY)
        {
            return new ManjuGlyphPosition
            {
                XAdvance = 0,
                YAdvance = -(int)vAdvance,
                XOffset = -(hAdvance / 2),
                YOffset = -vOriginY
            };
        }

        private short GetTopSideBearing(uint glyphId)
        {
            if (glyphId >= _totalGlyphs || _vmtxOffset == -1 || _numVMetrics == 0) return 0;

            if (glyphId < _numVMetrics)
            {
                // [Advance(2)] [Bearing(2)]
                return (short)_reader.ReadU16(_vmtxOffset + (int)(glyphId * 4) + 2);
            }
            else
            {
                // Extra bearings array
                int bearingsOffset = _vmtxOffset + (_numVMetrics * 4);
                return (short)_reader.ReadU16(bearingsOffset + (int)((glyphId - _numVMetrics) * 2));
            }
        }
    }
}

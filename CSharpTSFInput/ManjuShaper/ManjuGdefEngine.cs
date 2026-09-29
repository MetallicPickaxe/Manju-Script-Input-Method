using System;

namespace CSharpTSFInput.ManjuShaper
{
    /// <summary>
    /// ManjuGdefEngine: parses the OpenType GDEF table.
    /// Used to tell glyph classes apart (Base, Ligature, Mark).
    /// </summary>
    public unsafe class ManjuGdefEngine
    {
        private readonly ManjuFontReader _reader;
        private readonly int _gdefOffset;

        public enum GlyphClass : ushort
        {
            Unclassified = 0,
            Base = 1,
            Ligature = 2,
            Mark = 3,
            Component = 4
        }

        public ManjuGdefEngine(ManjuFontReader reader)
        {
            _reader = reader;
            _gdefOffset = _reader.FindTable(0x47444546); // 'GDEF'
        }

        public GlyphClass GetGlyphClass(uint glyphId)
        {
            if (_gdefOffset == -1) return GlyphClass.Unclassified;

            // Offset to ClassDef table is at offset 4
            int classDefOffset = _gdefOffset + _reader.ReadU16(_gdefOffset + 4);
            return (GlyphClass)GetClass(classDefOffset, glyphId);
        }

        public int GetMarkAttachmentClass(uint glyphId)
        {
            if (_gdefOffset == -1) return 0;
            ushort offset = _reader.ReadU16(_gdefOffset + 10);
            if (offset == 0) return 0;
            return GetClass(_gdefOffset + offset, glyphId);
        }

        private int GetClass(int tableOffset, uint glyphId)
        {
            ushort format = _reader.ReadU16(tableOffset);
            if (format == 1)
            {
                ushort startGlyph = _reader.ReadU16(tableOffset + 2);
                ushort glyphCount = _reader.ReadU16(tableOffset + 4);
                if (glyphId >= startGlyph && glyphId < startGlyph + glyphCount)
                {
                    return _reader.ReadU16((int)(tableOffset + 6 + ((glyphId - startGlyph) * 2)));
                }
            }
            else if (format == 2)
            {
                ushort rangeCount = _reader.ReadU16(tableOffset + 2);
                for (int i = 0; i < rangeCount; i++)
                {
                    int rangeOffset = tableOffset + 4 + (i * 6);
                    ushort start = _reader.ReadU16(rangeOffset);
                    ushort end = _reader.ReadU16(rangeOffset + 2);
                    if (glyphId >= start && glyphId <= end)
                    {
                        return _reader.ReadU16((int)(rangeOffset + 4));
                    }
                }
            }
            return 0; // Unclassified
        }

        public bool IsGlyphInFilterSet(int setIndex, uint glyphId)
        {
            if (_gdefOffset == -1) return false;
            
            // Header 1.0: 0:Version, 4:GlyphClassDef, 6:AttachList, 8:LigCaretList, 10:MarkAttachClassDef
            // Header 1.2: + 12:MarkGlyphSetsDef
            
            // Checking Version
            uint version = _reader.ReadU32(_gdefOffset);
            if (version < 0x00010002) return false;

            int markSetsOffsetPtr = _gdefOffset + 12;
            int markSetsOffset = _gdefOffset + _reader.ReadU16(markSetsOffsetPtr);
            if (markSetsOffset <= _gdefOffset) return false;

            ushort format = _reader.ReadU16(markSetsOffset);
            if (format != 1) return false;

            ushort count = _reader.ReadU16(markSetsOffset + 2);
            if (setIndex >= count) return false;

            int coverageOffset = markSetsOffset + (int)_reader.ReadU32(markSetsOffset + 4 + (setIndex * 4));
            
            // Check Coverage
            return GetGlyphCoverage(coverageOffset, glyphId) != -1;
        }

        private int GetGlyphCoverage(int tableOffset, uint glyphId)
        {
            ushort format = _reader.ReadU16(tableOffset);
            if (format == 1)
            {
                ushort count = _reader.ReadU16(tableOffset + 2);
                int low = 0, high = count - 1;
                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    ushort val = _reader.ReadU16((int)(tableOffset + 4 + (mid * 2)));
                    if (val == glyphId) return mid;
                    if (val < glyphId) low = mid + 1;
                    else high = mid - 1;
                }
            }
            else if (format == 2)
            {
                ushort rangeCount = _reader.ReadU16(tableOffset + 2);
                int low = 0, high = rangeCount - 1;
                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    int rangeOffset = tableOffset + 4 + (mid * 6);
                    ushort start = _reader.ReadU16(rangeOffset);
                    ushort end = _reader.ReadU16(rangeOffset + 2);
                    if (glyphId >= start && glyphId <= end)
                    {
                        ushort startIndex = _reader.ReadU16(rangeOffset + 4);
                        return startIndex + (int)(glyphId - start);
                    }
                    if (start < glyphId) low = mid + 1;
                    else high = mid - 1;
                }
            }
            return -1;
        }
    }
}
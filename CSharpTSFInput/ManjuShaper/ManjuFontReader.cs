using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CSharpTSFInput.ManjuShaper
{
    /// <summary>
    /// ManjuFontReader: a fast binary OpenType parser.
    /// Reads the GSUB/GPOS tables and the character-to-glyph mapping.
    /// </summary>
    public unsafe class ManjuFontReader
    {
        private readonly byte* _data;
        private readonly int _length;
        private readonly Dictionary<uint, int> _tableCache = new();

        public ManjuFontReader(byte* data, int length)
        {
            _data = data;
            _length = length;
        }

        public int Length => _length;

        public ushort ReadU16(int offset)
        {
            if (offset < 0 || offset + 2 > _length) throw new ArgumentOutOfRangeException(nameof(offset), $"ReadU16 out of bounds: {offset} in length {_length}");
            return BinaryPrimitives.ReadUInt16BigEndian(new ReadOnlySpan<byte>(_data + offset, 2));
        }

        public uint ReadU32(int offset)
        {
            if (offset < 0 || offset + 4 > _length) throw new ArgumentOutOfRangeException(nameof(offset), $"ReadU32 out of bounds: {offset} in length {_length}");
            return BinaryPrimitives.ReadUInt32BigEndian(new ReadOnlySpan<byte>(_data + offset, 4));
        }

        public int FindTable(uint tag)
        {
            if (_tableCache.TryGetValue(tag, out int cachedOffset))
                return cachedOffset;

            ushort numTables = ReadU16(4);
            for (int i = 0; i < numTables; i++)
            {
                int entryOffset = 12 + (i * 16);
                uint currentTag = ReadU32(entryOffset);
                if (currentTag == tag)
                {
                    int offset = (int)ReadU32(entryOffset + 8);
                    _tableCache[tag] = offset;
                    return offset;
                }
            }
            
            _tableCache[tag] = -1;
            return -1;
        }

        public uint GetGlyphIdUVS(uint unicode, uint selector)
        {
            if (FindTable(0x636D6170) == -1) return 0;
            // Re-scan for the Format 14 subtable offset. This walks the cmap table the same way
            // GetGlyphId does, in a reduced form that stops at the format-14 record.
            int cmapOffset = _tableCache[0x636D6170];
            ushort numTables = ReadU16(cmapOffset + 2);
            
            for (int i = 0; i < numTables; i++)
            {
                int recordOffset = cmapOffset + 4 + (i * 8);
                ushort platformId = ReadU16(recordOffset);
                ushort encodingId = ReadU16(recordOffset + 2);
                int offset = cmapOffset + (int)ReadU32(recordOffset + 4);
                ushort format = ReadU16(offset);

                if (format == 14)
                {
                    if (platformId == 0 || (platformId == 3 && (encodingId == 1 || encodingId == 10)))
                    {
                        return GetGlyphIdFormat14(offset, unicode, selector);
                    }
                }
            }
            return 0;
        }

        public uint GetGlyphId(uint unicode, uint variationSelector = 0)
        {
            int cmapOffset = FindTable(0x636D6170); // 'cmap'
            if (cmapOffset == -1) return 0;

            ushort numTables = ReadU16(cmapOffset + 2);
            int format0Offset = 0;
            int format4Offset = 0;
            int format6Offset = 0;
            int format10Offset = 0;
            int format12Offset = 0;
            int format14Offset = 0;

            for (int i = 0; i < numTables; i++)
            {
                int recordOffset = cmapOffset + 4 + (i * 8);
                ushort platformId = ReadU16(recordOffset);
                ushort encodingId = ReadU16(recordOffset + 2);
                int offset = cmapOffset + (int)ReadU32(recordOffset + 4);

                ushort format = ReadU16(offset);
                
                // [Note] the Unicode platform is preferred.
                if (platformId == 0 || (platformId == 3 && (encodingId == 1 || encodingId == 10)))
                {
                    if (format == 4) format4Offset = offset;
                    else if (format == 12) format12Offset = offset;
                    else if (format == 14) format14Offset = offset;
                    else if (format == 0) format0Offset = offset;
                    else if (format == 6) format6Offset = offset;
                    else if (format == 10) format10Offset = offset;
                }
            }

            // 1. try the variation sequences (format 14)
            if (variationSelector != 0 && format14Offset != 0)
            {
                uint uvsGlyph = GetGlyphIdFormat14(format14Offset, unicode, variationSelector);
                if (uvsGlyph != 0) return uvsGlyph;
            }

            // 2. the base mapping (in order of preference: 12, 10, 4, 6, 0)
            if (format12Offset != 0) return GetGlyphIdFormat12(format12Offset, unicode);
            if (format10Offset != 0) return GetGlyphIdFormat10(format10Offset, unicode);
            if (format4Offset != 0) return GetGlyphIdFormat4(format4Offset, (ushort)unicode);
            if (format6Offset != 0) return GetGlyphIdFormat6(format6Offset, (ushort)unicode);
            if (format0Offset != 0) return GetGlyphIdFormat0(format0Offset, (ushort)unicode);

            return 0;
        }

        /// <summary>
        /// Format 0: byte encoding table, 256 one-byte glyph ids (codes 0 to 255); used by older Mac fonts.
        /// </summary>
        private uint GetGlyphIdFormat0(int offset, ushort unicode)
        {
            if (unicode >= 256) return 0;
            int targetOffset = offset + 6 + unicode;
            if (targetOffset < 0 || targetOffset >= _length) return 0;
            return _data[targetOffset];
        }

        /// <summary>
        /// Format 6: trimmed table mapping, 16-bit glyph ids for one contiguous range of 16-bit codes.
        /// </summary>
        private uint GetGlyphIdFormat6(int offset, ushort unicode)
        {
            ushort firstCode = ReadU16(offset + 6);
            ushort entryCount = ReadU16(offset + 8);
            if (unicode >= firstCode && unicode < firstCode + entryCount)
            {
                return ReadU16(offset + 10 + (unicode - firstCode) * 2);
            }
            return 0;
        }

        /// <summary>
        /// Format 10: trimmed array, 16-bit glyph ids for one contiguous range of 32-bit codes.
        /// </summary>
        private uint GetGlyphIdFormat10(int offset, uint unicode)
        {
            uint startCode = ReadU32(offset + 12);
            uint numChars = ReadU32(offset + 16);
            if (unicode >= startCode && unicode < startCode + numChars)
            {
                return ReadU16(offset + 20 + (int)(unicode - startCode) * 2);
            }
            return 0;
        }

        private uint GetGlyphIdFormat12(int offset, uint unicode)
        {
            uint numGroups = ReadU32(offset + 12);
            for (int i = 0; i < (int)numGroups; i++)
            {
                int groupOffset = offset + 16 + (i * 12);
                uint start = ReadU32(groupOffset);
                uint end = ReadU32(groupOffset + 4);
                uint startId = ReadU32(groupOffset + 8);
                if (unicode >= start && unicode <= end)
                {
                    return startId + (unicode - start);
                }
            }
            return 0;
        }

        private uint GetGlyphIdFormat4(int offset, ushort unicode)
        {
            unchecked
            {
                ushort segCountX2 = ReadU16(offset + 6);
                int endCodeOffset = offset + 14;
                int startCodeOffset = endCodeOffset + segCountX2 + 2;
                int idDeltaOffset = startCodeOffset + segCountX2;
                int idRangeOffset = idDeltaOffset + segCountX2;

                for (int i = 0; i < segCountX2 / 2; i++)
                {
                    ushort endCode = ReadU16(endCodeOffset + (i * 2));
                    if (endCode >= unicode)
                    {
                        ushort startCode = ReadU16(startCodeOffset + (i * 2));
                        if (startCode <= unicode)
                        {
                            short idDelta = (short)ReadU16(idDeltaOffset + (i * 2));
                            ushort idRange = ReadU16(idRangeOffset + (i * 2));
                            if (idRange == 0) return (uint)(ushort)(unicode + idDelta);

                            int rangeOffsetVal = idRange;
                            int currentAddr = idRangeOffset + (i * 2);
                            int glyphOffset = currentAddr + rangeOffsetVal + ((unicode - startCode) * 2);
                            return ReadU16(glyphOffset);
                        }
                        break;
                    }
                }
                return 0;
            }
        }

        public byte ReadByte(int offset)
        {
            if (offset < 0 || offset >= _length) throw new ArgumentOutOfRangeException(nameof(offset), $"ReadByte out of bounds: {offset} in length {_length}");
            return _data[offset];
        }

        private uint GetGlyphIdFormat14(int offset, uint unicode, uint selector)
        {
            uint numRecords = ReadU32(offset + 6);
            int low = 0, high = (int)numRecords - 1;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                int recordOffset = offset + 10 + (mid * 11);
                uint midSelector = (uint)(ReadByte(recordOffset) << 16 | ReadByte(recordOffset + 1) << 8 | ReadByte(recordOffset + 2));
                
                if (midSelector == selector)
                {
                    uint nonDefaultOffset = ReadU32(recordOffset + 7); 

                    if (nonDefaultOffset != 0)
                    {
                        int uvsTableOffset = offset + (int)nonDefaultOffset;
                        uint numUVS = ReadU32(uvsTableOffset);
                        int uLow = 0, uHigh = (int)numUVS - 1;
                        while (uLow <= uHigh)
                        {
                            int uMid = (uLow + uHigh) / 2;
                            int entryOffset = uvsTableOffset + 4 + (uMid * 5); 
                            uint midUni = (uint)(ReadByte(entryOffset) << 16 | ReadByte(entryOffset + 1) << 8 | ReadByte(entryOffset + 2));
                            
                            if (midUni == unicode) return ReadU16(entryOffset + 3);
                            if (midUni < unicode) uLow = uMid + 1;
                            else uHigh = uMid - 1;
                        }
                    }
                    
                    return 0; 
                }
                if (midSelector < selector) low = mid + 1;
                else high = mid - 1;
            }

            // Fallback: Linear search (in case the table is not sorted properly or binary search failed for edge cases)
            // This is critical for some malformed fonts.
            for (uint i = 0; i < numRecords; i++)
            {
                int recordOffset = offset + 10 + ((int)i * 11);
                uint recSelector = (uint)(ReadByte(recordOffset) << 16 | ReadByte(recordOffset + 1) << 8 | ReadByte(recordOffset + 2));
                
                if (recSelector == selector)
                {
                    uint nonDefaultOffset = ReadU32(recordOffset + 7);
                    if (nonDefaultOffset != 0)
                    {
                        int uvsTableOffset = offset + (int)nonDefaultOffset;
                        uint numUVS = ReadU32(uvsTableOffset);
                        for (uint j = 0; j < numUVS; j++)
                        {
                            int entryOffset = uvsTableOffset + 4 + ((int)j * 5);
                            uint recUni = (uint)(ReadByte(entryOffset) << 16 | ReadByte(entryOffset + 1) << 8 | ReadByte(entryOffset + 2));
                            if (recUni == unicode) return ReadU16(entryOffset + 3);
                        }
                    }
                    return 0;
                }
            }

            return 0;
        }

        public ushort GetNumGlyphs()
        {
            int offset = FindTable(0x6D617870); // 'maxp'
            if (offset == -1) return 0;
            return ReadU16(offset + 4);
        }

        public ushort GetUnitsPerEm()
        {
            int offset = FindTable(0x68656164); // 'head'
            if (offset == -1) return 1000;
            return ReadU16(offset + 18);
        }


        public (short ascender, short descender, short lineGap) GetTypoMetrics()
        {
            int offset = FindTable(0x4F532F32); // 'OS/2'
            if (offset == -1) return (0, 0, 0);
            return ((short)ReadU16(offset + 68), (short)ReadU16(offset + 70), (short)ReadU16(offset + 72));
        }

        /// <summary>
        /// Returns the vertical origin Y coordinate for a glyph in design units.
        /// Follows HarfBuzz's fallback chain:
        ///   1. VORG table (per-glyph or default)
        ///   2. glyf yMax + vmtx topSideBearing (per-glyph, most fonts use this)
        ///   3. OS/2 sTypoAscender (global fallback)
        /// </summary>
        public short GetVerticalOrigin(uint glyphId)
        {
            // 1. Try VORG table first
            int vorgOffset = FindTable(0x564F5247); // 'VORG'
            if (vorgOffset != -1)
            {
                short defaultY = (short)ReadU16(vorgOffset + 4);
                ushort count = ReadU16(vorgOffset + 6);

                int low = 0, high = count - 1;
                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    int entryOffset = vorgOffset + 8 + (mid * 4);
                    ushort gid = ReadU16(entryOffset);
                    if (gid == glyphId) return (short)ReadU16(entryOffset + 2);
                    if (gid < glyphId) low = mid + 1;
                    else high = mid - 1;
                }
                return defaultY;
            }

            // 2. Fallback: glyf yMax + vmtx topSideBearing (matches HarfBuzz ot-font.cc)
            short yMax = GetGlyphYMax(glyphId);
            short tsb = GetVmtxTopSideBearing(glyphId);
            if (yMax != short.MinValue)
            {
                return (short)(yMax + tsb);
            }

            // 3. Last resort: OS/2 sTypoAscender.
            // Reached only when the font has no per-glyph answer at all — no glyf/loca/head.
            // An empty outline does not arrive here; it is a zero box, and its origin is its tsb.
            return GetTypoMetrics().ascender;
        }

        /// <summary>
        /// Reads glyph yMax from the 'glyf' table bounding box header.
        /// Returns short.MinValue when the font carries NO ANSWER for this glyph — no glyf/loca/head, or
        /// an id past the end of loca. An EMPTY outline is NOT that case: see below.
        /// </summary>
        private short GetGlyphYMax(uint glyphId)
        {
            int locaOffset = FindTable(0x6C6F6361); // 'loca'
            int glyfOffset = FindTable(0x676C7966); // 'glyf'
            int headOffset = FindTable(0x68656164); // 'head'
            if (locaOffset == -1 || glyfOffset == -1 || headOffset == -1)
                return short.MinValue;

            ushort indexToLocFormat = ReadU16(headOffset + 50);
            int glyphOffset;
            int nextGlyphOffset;

            if (indexToLocFormat == 0)
            {
                // Short format: offsets are stored as uint16 * 2
                glyphOffset = ReadU16(locaOffset + (int)(glyphId * 2)) * 2;
                nextGlyphOffset = ReadU16(locaOffset + (int)((glyphId + 1) * 2)) * 2;
            }
            else
            {
                // Long format: offsets are stored as uint32
                glyphOffset = (int)ReadU32(locaOffset + (int)(glyphId * 4));
                nextGlyphOffset = (int)ReadU32(locaOffset + (int)((glyphId + 1) * 4));
            }

            // AN EMPTY OUTLINE IS A ZERO BOUNDING BOX, NOT A MISSING ONE.
            //
            // Returning short.MinValue here would send GetVerticalOrigin past the per-glyph branch and
            // into its last resort, OS/2 sTypoAscender, a DOCUMENT-level horizontal metric. That number is
            // not in the same convention as the per-glyph origins the rest of the column uses, and the
            // difference lands as a displacement of the blank glyph's row.
            //
            // MEASURED on the shipped NotoSansMongolian's own tables, every glyph in a Manchu run
            // resolves to origin 880: gid 5 is yMax 836 + tsb 44, gid 7 is
            // 444 + 436, gid 1477 (mvs) is 734 + 146. The font expresses ONE origin, per glyph, through the
            // top side bearing. For its 100 empty-outline glyphs the designer set tsb = 880 directly, which
            // is that same origin and is the only thing tsb can mean for a glyph with no ink. Falling
            // through to OS/2 would substitute 1457 instead: 577 design units off, 18.46 DIP at 32 px.
            //
            // What that costs: `ᠠ + NNBSP + ᠠ` shapes to uni1820.AA.isol + mvs.narrow + uni1820.Aa.isol,
            // and mvs.narrow is one of those empty glyphs. Its row centre lands INSIDE the span the
            // previous glyph owns. NNBSP is one of the two code points the suffix connector can be
            // (the connector codepoint menu row toggles MVS ↔ NNBSP), and a connector is by definition between a stem and its
            // suffix, so that is every suffixed word while the toggle is on.
            //
            // This is not a special case for NNBSP, and there is no code point in this decision: the
            // rule is "an empty box is empty, not unknown", and it lands on all 100 of those glyphs
            // alike. U+00A0, whose tsb IS 1457, therefore keeps 1457, because the rule returns what
            // the font says and not a chosen number.
            if (glyphOffset == nextGlyphOffset)
                return 0;

            int absOffset = glyfOffset + glyphOffset;
            // Glyph header: numberOfContours(2) + xMin(2) + yMin(2) + xMax(2) + yMax(2)
            // yMax is at offset +8 from glyph start
            return (short)ReadU16(absOffset + 8);
        }

        /// <summary>
        /// Reads top side bearing from the 'vmtx' table for a specific glyph.
        /// </summary>
        private short GetVmtxTopSideBearing(uint glyphId)
        {
            int vmtxOffset = FindTable(0x766D7478); // 'vmtx'
            int vheaOffset = FindTable(0x76686561); // 'vhea'
            if (vmtxOffset == -1 || vheaOffset == -1) return 0;

            ushort numVMetrics = ReadU16(vheaOffset + 34);
            if (numVMetrics == 0) return 0;

            if (glyphId < numVMetrics)
            {
                // Each record: [advance(2)] [bearing(2)]
                return (short)ReadU16(vmtxOffset + (int)(glyphId * 4) + 2);
            }
            else
            {
                // Extra bearings array after the last full metric record
                int bearingsOffset = vmtxOffset + (numVMetrics * 4);
                return (short)ReadU16(bearingsOffset + (int)((glyphId - numVMetrics) * 2));
            }
        }


        private static readonly string[] AppleNames = {
            ".notdef", ".null", "nonmarkingreturn", "space", "exclam", "quotedbl", "numbersign", "dollar", "percent", "ampersand", "quotesingle", "parenleft", "parenright", "asterisk", "plus", "comma", "hyphen", "period", "slash", "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "colon", "semicolon", "less", "equal", "greater", "question", "at", "A", "B", "C", "D", "E", "F", "G", "H", "I", "J", "K", "L", "M", "N", "O", "P", "Q", "R", "S", "T", "U", "V", "W", "X", "Y", "Z", "bracketleft", "backslash", "bracketright", "asciicircum", "underscore", "grave", "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u", "v", "w", "x", "y", "z", "braceleft", "bar", "braceright", "asciitilde", "Adieresis", "Aring", "Ccedilla", "Eacute", "Ntilde", "Odieresis", "Udieresis", "aacute", "agrave", "acircumflex", "adieresis", "atilde", "aring", "ccedilla", "eacute", "egrave", "ecircumflex", "edieresis", "iacute", "igrave", "icircumflex", "idieresis", "ntilde", "oacute", "ograve", "ocircumflex", "odieresis", "otilde", "uacute", "ugrave", "ucircumflex", "udieresis", "dagger", "degree", "cent", "sterling", "section", "bullet", "paragraph", "germandbls", "registered", "copyright", "trademark", "acute", "dieresis", "notequal", "AE", "Oslash", "infinity", "plusminus", "lessequal", "greaterequal", "yen", "mu", "partialdiff", "summation", "product", "pi", "integral", "ordfeminine", "ordmasculine", "Omega", "ae", "oslash", "questiondown", "exclamdown", "logicalnot", "radical", "florin", "approxequal", "Delta", "guillemotleft", "guillemotright", "ellipsis", "nonbreakingspace", "Agrave", "Atilde", "Otilde", "OE", "oe", "endash", "emdash", "quotedblleft", "quotedblright", "quoteleft", "quoteright", "divide", "lozenge", "ydieresis", "Ytieresis", "fraction", "currency", "guilsinglleft", "guilsinglright", "fi", "fl", "daggerdbl", "periodcentered", "quotesinglbase", "quotedblbase", "perthousand", "Acircumflex", "Ecircumflex", "Aacute", "Edieresis", "Egrave", "Iacute", "Icircumflex", "Idieresis", "Igrave", "Oacute", "Ocircumflex", "apple", "Ograve", "Uacute", "Ucircumflex", "Ugrave", "dotlessi", "circumflex", "tilde", "macron", "breve", "dotaccent", "ring", "cedilla", "hungarumlaut", "ogonek", "caron", "Lslash", "lslash", "Scaron", "scaron", "Zcaron", "zcaron", "brokenbar", "Eth", "eth", "Yacute", "yacute", "Thorn", "thorn", "minus", "multiply", "onesuperior", "twosuperior", "threesuperior", "onehalf", "onequarter", "threequarters", "franc", "Gbreve", "gbreve", "Idotaccent", "Scedilla", "scedilla", "Cacute", "cacute", "Ccaron", "ccaron", "dcroat"
        };


        public short GetKerning(uint leftGid, uint rightGid)
        {
            int kernOffset = FindTable(0x6B65726E); // 'kern'
            if (kernOffset == -1) return 0;
            ushort nTables = ReadU16(kernOffset + 2);
            int currentOffset = kernOffset + 4;
            for (int i = 0; i < nTables; i++)
            {
                ushort length = ReadU16(currentOffset + 2);
                ushort coverage = ReadU16(currentOffset + 4);
                if ((coverage & 0xFF00) == 0 && (coverage & 0x0001) != 0)
                {
                    int subtableStart = currentOffset + 6;
                    ushort nPairs = ReadU16(subtableStart);
                    for (int j = 0; j < nPairs; j++)
                    {
                        int pairOffset = subtableStart + 8 + (j * 6);
                        if (ReadU16(pairOffset) == leftGid && ReadU16(pairOffset + 2) == rightGid) return (short)ReadU16(pairOffset + 4);
                    }
                }
                currentOffset += length;
            }
            return 0;
        }
    }
}
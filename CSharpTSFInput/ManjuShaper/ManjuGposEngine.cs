using System;
using System.Collections.Generic;

namespace CSharpTSFInput.ManjuShaper
{
    public unsafe class ManjuGposEngine
    {
        private readonly ManjuFontReader _reader;
        private readonly int _gposOffset;
        private readonly ManjuGsubLookup _helper;
        private readonly ManjuGdefEngine _gdef;

        // Recursion and iteration protection
        [ThreadStatic] private static int _recursionDepth;
        private const int MAX_RECURSION_DEPTH = 50;
        private const int MAX_LOOP_ITERATIONS = 10000;

        public ManjuGposEngine(ManjuFontReader reader)
        {
            _reader = reader;
            _gposOffset = _reader.FindTable(0x47504F53);
            _gdef = new ManjuGdefEngine(reader);
            _helper = new ManjuGsubLookup(_reader, _gdef);
        }

        private struct Anchor { public int X; public int Y; }

        private Anchor ReadAnchor(int offset)
        {
            if (offset == 0) return new Anchor { X = 0, Y = 0 };
            short x = (short)_reader.ReadU16(offset + 2);
            short y = (short)_reader.ReadU16(offset + 4);
            return new Anchor { X = x, Y = y };
        }

        public void ApplyPositioning(ManjuBuffer buffer, string scriptTag = "mong", string langTag = "dflt", bool isVertical = true, int ppem = 0, List<string>? customFeatures = null)
        {
            if (_gposOffset == -1)
            {
                ApplyFallbackKerning(buffer, isVertical);
                return;
            }

            // Matches the GPOS feature order of the HarfBuzz Arabic shaper:
            //   curs → kern → mark → mkmk
            //   vertical: curs → vkrn (which replaces kern, it is not additive) → mark → mkmk
            // not applied: dist (USE shaper), abvm/blwm (USE/Indic shaper)
            ManjuGsubFeatureNavigator nav = new(_reader, _gposOffset);
            List<String> orderedFeatures = ["curs"];  // curs before kern (the cursive attachment points settle first)
            if (isVertical)
            {
                orderedFeatures.Add("vkrn");  // vertical mode uses vkrn in place of kern
            }
            else
            {
                orderedFeatures.Add("kern");  // horizontal mode uses kern
            }
            orderedFeatures.AddRange(new[] { "mark", "mkmk" });
            if (customFeatures != null)
            {
                foreach (String cf in customFeatures)
                {
                    if (!orderedFeatures.Contains(cf))
                    {
                        orderedFeatures.Add(cf);
                    }
                }
            }
            foreach (var feature in orderedFeatures)
            {
                List<UInt16> lookups = nav.GetLookupIndices(feature, scriptTag, langTag);
                foreach (var index in lookups)
                {
                    ProcessLookup(index, buffer, isVertical, ppem);
                }
            }
        }

		private void ApplyFallbackKerning(ManjuBuffer buffer, bool isVertical)
        {
            if (isVertical)
			{
				return;
			}

			for (int i = 0; i < buffer.Length - 1; i++)
            {
                short kern = _reader.GetKerning(buffer.Info[i].Codepoint, buffer.Info[i+1].Codepoint);
                if (kern != 0)
				{
					buffer.Pos[i].XAdvance += kern;
				}
			}
        }

        public void ProcessLookup(ushort lookupIndex, ManjuBuffer buffer, bool isVertical, int ppem = 0, int start = 0, int end = -1)
        {
            // Recursion depth protection
            _recursionDepth++;
            if (_recursionDepth > MAX_RECURSION_DEPTH)
            {
                _recursionDepth--;
                return;
            }

            try
            {
                int lookupListOffset = _gposOffset + _reader.ReadU16(_gposOffset + 8);
                int lookupOffset = lookupListOffset + _reader.ReadU16(lookupListOffset + 2 + (lookupIndex * 2));
                ushort type = _reader.ReadU16(lookupOffset);
                ushort lookupFlag = _reader.ReadU16(lookupOffset + 2);
                ushort subCount = _reader.ReadU16(lookupOffset + 4);
                ushort effectiveFlag = lookupFlag;
                if (type == 4 || type == 5 || type == 6)
                {
                    effectiveFlag = (ushort)(lookupFlag & ~0x0008);
                }

                // A nested contextual/chained lookup applies ONLY at the matched sequence index; the
                // caller passes [start,end). The top-level feature pass uses the whole-buffer default.
                // Mirrors the GSUB sibling ManjuGsubLookup.ProcessLookup(start,end).
                if (end < 0 || end > buffer.Length) end = buffer.Length;
                if (start < 0) start = 0;

                // Iteration counter for infinite loop detection
                int iterations = 0;

                for (int i = start; i < end; i++)
                {
                    iterations++;
                    if (iterations > MAX_LOOP_ITERATIONS)
                    {
                        break;
                    }

                    if (_helper.GetNextIndex(buffer, i - 1, 1, effectiveFlag) != i)
                    {
                        continue;
                    }

                    for (int s = 0; s < subCount; s++)
                    {
                        int subOff = lookupOffset + _reader.ReadU16(lookupOffset + 6 + (s * 2));
                        bool applied = false;
                        switch (type)
                        {
                            case 1: applied = ApplySingleAdjustment(subOff, buffer, i, ppem); break;
                            case 2: applied = ApplyPairAdjustment(subOff, buffer, i, lookupFlag, ppem); break;
                            case 3: applied = ApplyCursiveAttachment(subOff, buffer, i, isVertical); break;
                            case 4: applied = ApplyMarkToBase(subOff, buffer, i, lookupFlag); break;
                            case 5: applied = ApplyMarkToLigature(subOff, buffer, i, lookupFlag); break;
                            case 6: applied = ApplyMarkToMark(subOff, buffer, i, lookupFlag); break;
                            case 7: applied = ApplyContextPos(subOff, buffer, i, lookupFlag, isVertical, ppem); break;
                            case 8: applied = ApplyChainContextPos(subOff, buffer, i, lookupFlag, isVertical, ppem); break;
                            case 9: { ushort extType = _reader.ReadU16(subOff + 2); int extOff = subOff + (int)_reader.ReadU32(subOff + 4);
                                      applied = ApplyTypeInternal(extType, extOff, buffer, i, lookupFlag, isVertical, ppem); } break;
                        }
                        if (applied)
                        {
                            break;
                        }
                    }
                }
            }
            finally
            {
                _recursionDepth--;
            }
        }

		private bool ApplyTypeInternal(int type, int off, ManjuBuffer buffer, int i, ushort flag, bool isV, int ppem) => type switch {
            1 => ApplySingleAdjustment(off, buffer, i, ppem),
            2 => ApplyPairAdjustment(off, buffer, i, flag, ppem),
            3 => ApplyCursiveAttachment(off, buffer, i, isV),
            4 => ApplyMarkToBase(off, buffer, i, flag),
            5 => ApplyMarkToLigature(off, buffer, i, flag),
            6 => ApplyMarkToMark(off, buffer, i, flag),
            7 => ApplyContextPos(off, buffer, i, flag, isV, ppem),
            8 => ApplyChainContextPos(off, buffer, i, flag, isV, ppem),
            _ => false
        };

        public bool ApplySingleAdjustment(int subOffset, ManjuBuffer buffer, int index, int ppem)
        {
            ushort format = _reader.ReadU16(subOffset);
            int covIdx = _helper.GetGlyphCoverage(subOffset + _reader.ReadU16(subOffset + 2), buffer.Info[index].Codepoint);
            if (covIdx == -1)
			{
				return false;
			}

			ushort valFormat = _reader.ReadU16(subOffset + 4);
            if (format == 1)
			{
				ApplyValueRecord (subOffset + 6, valFormat, ref buffer.Pos[index], subOffset, ppem);
			}
			else if (format == 2)
			{
				ApplyValueRecord (subOffset + 8 + (covIdx * GetValueRecordSize(valFormat)), valFormat, ref buffer.Pos[index], subOffset, ppem);
			}

			return true;
        }

        public bool ApplyPairAdjustment(int subOffset, ManjuBuffer buffer, int index, ushort lookupFlag, int ppem)
        {
            int covIdx = _helper.GetGlyphCoverage(subOffset + _reader.ReadU16(subOffset + 2), buffer.Info[index].Codepoint);
            if (covIdx == -1) return false;
            int nextIdx = _helper.GetNextIndex(buffer, index, 1, lookupFlag);
            if (nextIdx == -1) return false;
            ushort fmt1 = _reader.ReadU16(subOffset + 4); ushort fmt2 = _reader.ReadU16(subOffset + 6);
            int s1 = GetValueRecordSize(fmt1); int s2 = GetValueRecordSize(fmt2);
            ushort format = _reader.ReadU16(subOffset);
            if (format == 1)
            {
                int setOff = subOffset + _reader.ReadU16(subOffset + 10 + (covIdx * 2));
                ushort count = _reader.ReadU16(setOff); uint target = buffer.Info[nextIdx].Codepoint;
                for (int i = 0; i < count; i++) { int rec = setOff + 2 + (i * (2 + s1 + s2)); if (_reader.ReadU16(rec) == target) { ApplyValueRecord(rec + 2, fmt1, ref buffer.Pos[index], subOffset, ppem); ApplyValueRecord(rec + 2 + s1, fmt2, ref buffer.Pos[nextIdx], subOffset, ppem); return true; } }
            }
            else if (format == 2)
            {
                int c1 = _helper.GetClassFromDef(subOffset + _reader.ReadU16(subOffset + 8), buffer.Info[index].Codepoint);
                int c2 = _helper.GetClassFromDef(subOffset + _reader.ReadU16(subOffset + 10), buffer.Info[nextIdx].Codepoint);
                int rec = subOffset + 16 + (c1 * _reader.ReadU16(subOffset + 14) + c2) * (s1 + s2);
                ApplyValueRecord(rec, fmt1, ref buffer.Pos[index], subOffset, ppem); ApplyValueRecord(rec + s1, fmt2, ref buffer.Pos[nextIdx], subOffset, ppem); return true;
            }
            return false;
        }

        public bool ApplyContextPos(int subOffset, ManjuBuffer buffer, int index, ushort flag, bool isV, int ppem)
        {
            ushort format = _reader.ReadU16(subOffset);
            if (format == 1)
            {
                int covIdx = _helper.GetGlyphCoverage(subOffset + _reader.ReadU16(subOffset + 2), buffer.Info[index].Codepoint);
                if (covIdx == -1) return false;
                int setOff = subOffset + _reader.ReadU16(subOffset + 6 + (covIdx * 2));
                ushort count = _reader.ReadU16(setOff);
                for (int r = 0; r < count; r++) {
                    int ruleOff = setOff + _reader.ReadU16(setOff + 2 + r * 2); ushort gCount = _reader.ReadU16(ruleOff); ushort sCount = _reader.ReadU16(ruleOff + 2);
                    int curr = index; bool match = true;
                    for (int i = 1; i < gCount; i++) { curr = _helper.GetNextIndex(buffer, curr, 1, flag); if (curr == -1 || buffer.Info[curr].Codepoint != _reader.ReadU16(ruleOff + 4 + (i-1)*2)) { match = false; break; } }
                    if (match) { ApplyContextLookups(ruleOff + 4 + (gCount-1)*2, sCount, buffer, index, flag, isV, ppem); return true; }
                }
            }
            else if (format == 2)
            {
                int classDef = subOffset + _reader.ReadU16(subOffset + 4); int startClass = _helper.GetClassFromDef(classDef, buffer.Info[index].Codepoint);
                int setOff = subOffset + _reader.ReadU16(subOffset + 8 + startClass * 2); if (setOff == subOffset) return false;
                ushort count = _reader.ReadU16(setOff);
                for (int r = 0; r < count; r++) {
                    int ruleOff = setOff + _reader.ReadU16(setOff + 2 + r * 2); ushort gCount = _reader.ReadU16(ruleOff); ushort sCount = _reader.ReadU16(ruleOff + 2);
                    int curr = index; bool match = true;
                    for (int i = 1; i < gCount; i++) { curr = _helper.GetNextIndex(buffer, curr, 1, flag); if (curr == -1 || _helper.GetClassFromDef(classDef, buffer.Info[curr].Codepoint) != _reader.ReadU16(ruleOff + 4 + (i-1)*2)) { match = false; break; } }
                    if (match) { ApplyContextLookups(ruleOff + 4 + (gCount-1)*2, sCount, buffer, index, flag, isV, ppem); return true; }
                }
            }
            else if (format == 3)
            {
                ushort gCount = _reader.ReadU16(subOffset + 2); ushort sCount = _reader.ReadU16(subOffset + 4);
                int curr = index;
                for (int i = 0; i < gCount; i++) { int cov = subOffset + _reader.ReadU16(subOffset + 6 + i*2); if (curr == -1 || _helper.GetGlyphCoverage(cov, buffer.Info[curr].Codepoint) == -1) return false; if (i < gCount-1) curr = _helper.GetNextIndex(buffer, curr, 1, flag); }
                ApplyContextLookups(subOffset + 6 + gCount*2, sCount, buffer, index, flag, isV, ppem); return true;
            }
            return false;
        }

        public bool ApplyChainContextPos(int subOffset, ManjuBuffer buffer, int index, ushort flag, bool isV, int ppem)
        {
            ushort format = _reader.ReadU16(subOffset);
            if (format == 1)
            {
                int covIdx = _helper.GetGlyphCoverage(subOffset + _reader.ReadU16(subOffset + 2), buffer.Info[index].Codepoint);
                if (covIdx == -1) return false;
                int setOff = subOffset + _reader.ReadU16(subOffset + 6 + covIdx * 2); ushort count = _reader.ReadU16(setOff);
                for (int r = 0; r < count; r++) {
                    int ruleOff = setOff + _reader.ReadU16(setOff + 2 + r * 2);
                    if (_helper.MatchChainRule(ruleOff, buffer, index, flag, 0, 0, 0)) {
                        int cur = ruleOff; ushort b = _reader.ReadU16(cur); cur += 2 + b * 2; ushort i = _reader.ReadU16(cur); cur += 2 + (i-1) * 2; ushort l = _reader.ReadU16(cur); cur += 2 + l * 2; ushort s = _reader.ReadU16(cur); cur += 2;
                        ApplyContextLookups(cur, s, buffer, index, flag, isV, ppem); return true;
                    }
                }
            }
            else if (format == 2)
            {
                int backClass = subOffset + _reader.ReadU16(subOffset + 4); int inClass = subOffset + _reader.ReadU16(subOffset + 6); int lookClass = subOffset + _reader.ReadU16(subOffset + 8);
                int startClass = _helper.GetClassFromDef(inClass, buffer.Info[index].Codepoint);
                int setOffset = subOffset + _reader.ReadU16(subOffset + 12 + startClass * 2); if (setOffset == subOffset) return false;
                ushort count = _reader.ReadU16(setOffset);
                for (int r = 0; r < count; r++) {
                    int ruleOff = setOffset + _reader.ReadU16(setOffset + 2 + r * 2);
                    if (_helper.MatchChainRule(ruleOff, buffer, index, flag, backClass, inClass, lookClass)) {
                        int cur = ruleOff; ushort b = _reader.ReadU16(cur); cur += 2 + b * 2; ushort i = _reader.ReadU16(cur); cur += 2 + (i-1) * 2; ushort l = _reader.ReadU16(cur); cur += 2 + l * 2; ushort s = _reader.ReadU16(cur); cur += 2;
                        ApplyContextLookups(cur, s, buffer, index, flag, isV, ppem); return true;
                    }
                }
            }
            else if (format == 3)
            {
                ushort bCount = _reader.ReadU16(subOffset + 2); int cur = subOffset + 4; int check = index;
                for (int i = 0; i < bCount; i++) { check = _helper.GetNextIndex(buffer, check, -1, flag); int cov = subOffset + _reader.ReadU16(cur); cur += 2; if (check == -1 || _helper.GetGlyphCoverage(cov, buffer.Info[check].Codepoint) == -1) return false; }
                ushort inCount = _reader.ReadU16(cur); cur += 2; int inputEnd = index;
                for (int i = 0; i < inCount; i++) { int cov = subOffset + _reader.ReadU16(cur); cur += 2; if (inputEnd == -1 || _helper.GetGlyphCoverage(cov, buffer.Info[inputEnd].Codepoint) == -1) return false; if (i < inCount-1) inputEnd = _helper.GetNextIndex(buffer, inputEnd, 1, flag); }
                ushort lCount = _reader.ReadU16(cur); cur += 2; check = inputEnd;
                for (int i = 0; i < lCount; i++) { check = _helper.GetNextIndex(buffer, check, 1, flag); int cov = subOffset + _reader.ReadU16(cur); cur += 2; if (check == -1 || _helper.GetGlyphCoverage(cov, buffer.Info[check].Codepoint) == -1) return false; }
                ushort sCount = _reader.ReadU16(cur); cur += 2; ApplyContextLookups(cur, sCount, buffer, index, flag, isV, ppem); return true;
            }
            return false;
        }

        private void ApplyContextLookups(int offset, ushort count, ManjuBuffer buffer, int index, ushort flag, bool isV, int ppem)
        {
            int cur = offset;
            for (int i = 0; i < count; i++)
            {
                ushort seq = _reader.ReadU16(cur); ushort lookup = _reader.ReadU16(cur + 2); cur += 4;

                int target = index; for (int k = 0; k < seq; k++) target = _helper.GetNextIndex(buffer, target, 1, flag);
                if (target != -1) ProcessLookup(lookup, buffer, isV, ppem, start: target, end: target + 1);
            }
        }

        private int GetValueRecordSize(ushort format) { int s = 0; for(int i=0; i<8; i++) if((format & (1<<i)) != 0) s += 2; return s; }

        private void ApplyValueRecord(int offset, ushort format, ref ManjuGlyphPosition pos, int baseOff, int ppem)
        {
            if (format == 0) return; int cur = offset;
            if ((format & 0x01) != 0) { pos.XOffset += (short)_reader.ReadU16(cur); cur += 2; }
            if ((format & 0x02) != 0) { pos.YOffset += (short)_reader.ReadU16(cur); cur += 2; }
            if ((format & 0x04) != 0) { pos.XAdvance += (short)_reader.ReadU16(cur); cur += 2; }
            if ((format & 0x08) != 0) { pos.YAdvance += (short)_reader.ReadU16(cur); cur += 2; }
            
            // Device tables are handled even if ppem is 0 internally in GetDeviceDelta
            if ((format & 0x10) != 0) { int dOff = _reader.ReadU16(cur); if (dOff != 0) pos.XOffset += (short)GetDeviceDelta(baseOff + dOff, ppem); cur += 2; }
            if ((format & 0x20) != 0) { int dOff = _reader.ReadU16(cur); if (dOff != 0) pos.YOffset += (short)GetDeviceDelta(baseOff + dOff, ppem); cur += 2; }
            if ((format & 0x40) != 0) { int dOff = _reader.ReadU16(cur); if (dOff != 0) pos.XAdvance += (short)GetDeviceDelta(baseOff + dOff, ppem); cur += 2; }
            if ((format & 0x80) != 0) { int dOff = _reader.ReadU16(cur); if (dOff != 0) pos.YAdvance += (short)GetDeviceDelta(baseOff + dOff, ppem); cur += 2; }
        }

        private int GetDeviceDelta(int deviceOffset, int ppem)
        {
            if (ppem <= 0) return 0; // Standard Device Tables (Format 1,2,3) require ppem > 0
            ushort startSize = _reader.ReadU16(deviceOffset); ushort endSize = _reader.ReadU16(deviceOffset + 2); ushort deltaFormat = _reader.ReadU16(deviceOffset + 4);
            if (ppem < startSize || ppem > endSize) return 0;
            int sizeIdx = ppem - startSize; int bits = 1 << deltaFormat; int itemsPerWord = 16 / bits;
            ushort word = _reader.ReadU16(deviceOffset + 6 + (sizeIdx / itemsPerWord) * 2);
            int val = (word >> (16 - (sizeIdx % itemsPerWord + 1) * bits)) & ((1 << bits) - 1);
            if ((val & (1 << (bits - 1))) != 0) val |= -1 << bits;
            return val;
        }

        public bool ApplyCursiveAttachment(int subOffset, ManjuBuffer buffer, int index, bool isVertical)
        {
            int covOff = subOffset + _reader.ReadU16(subOffset + 2);
            int covIdx = _helper.GetGlyphCoverage(covOff, buffer.Info[index].Codepoint);
            if (covIdx == -1 || index == 0) return false;
            int prevIdx = index - 1;
            // the previous glyph must also be in coverage;
            // otherwise prevCovIdx = -1 makes (prevCovIdx * 4) + 2 = -2 read before the entryExitRecord array.
            int prevCovIdx = _helper.GetGlyphCoverage(covOff, buffer.Info[prevIdx].Codepoint);
            if (prevCovIdx == -1) return false;
            int entryAnchorOff = _reader.ReadU16(subOffset + 6 + (covIdx * 4));
            int prevExitOff = _reader.ReadU16(subOffset + 6 + (prevCovIdx * 4) + 2);
            if (entryAnchorOff == 0 || prevExitOff == 0) return false;
            Anchor entry = ReadAnchor(subOffset + entryAnchorOff); Anchor exit = ReadAnchor(subOffset + prevExitOff);
            buffer.Pos[index].XOffset = (short)(buffer.Pos[prevIdx].XOffset + exit.X - entry.X);
            buffer.Pos[index].YOffset = (short)(buffer.Pos[prevIdx].YOffset + exit.Y - entry.Y);
            return true;
        }

        public bool ApplyMarkToBase(int subOff, ManjuBuffer buffer, int index, ushort flag)
        {
            int mIdx = _helper.GetGlyphCoverage(subOff + _reader.ReadU16(subOff + 2), buffer.Info[index].Codepoint);
            if (mIdx == -1) return false;
            int baseIdx = FindBase(buffer, index, subOff + _reader.ReadU16(subOff + 4), flag);
            if (baseIdx < 0) return false;
            int bCovIdx = _helper.GetGlyphCoverage(subOff + _reader.ReadU16(subOff + 4), buffer.Info[baseIdx].Codepoint);
            ushort mClassCount = _reader.ReadU16(subOff + 6);
            int mArray = subOff + _reader.ReadU16(subOff + 8); int bArray = subOff + _reader.ReadU16(subOff + 10);
            ushort mClass = _reader.ReadU16(mArray + 2 + mIdx * 4);
            Anchor mAnc = ReadAnchor(mArray + _reader.ReadU16(mArray + 2 + mIdx * 4 + 2));
            Anchor bAnc = ReadAnchor(bArray + _reader.ReadU16(bArray + 2 + bCovIdx * (mClassCount * 2) + mClass * 2));
            buffer.Pos[index].XOffset = (short)(buffer.Pos[baseIdx].XOffset + bAnc.X - mAnc.X);
            buffer.Pos[index].YOffset = (short)(buffer.Pos[baseIdx].YOffset + bAnc.Y - mAnc.Y);
            return true;
        }

        public bool ApplyMarkToLigature(int subOff, ManjuBuffer buffer, int index, ushort flag)
        {
            int mIdx = _helper.GetGlyphCoverage(subOff + _reader.ReadU16(subOff + 2), buffer.Info[index].Codepoint);
            if (mIdx == -1) return false;
            int baseIdx = FindBase(buffer, index, subOff + _reader.ReadU16(subOff + 4), flag);
            if (baseIdx < 0) return false;
            int bCovIdx = _helper.GetGlyphCoverage(subOff + _reader.ReadU16(subOff + 4), buffer.Info[baseIdx].Codepoint);
            ushort mClassCount = _reader.ReadU16(subOff + 6);
            int mArray = subOff + _reader.ReadU16(subOff + 8); int ligArray = subOff + _reader.ReadU16(subOff + 10);
            ushort mClass = _reader.ReadU16(mArray + 2 + mIdx * 4);
            Anchor mAnc = ReadAnchor(mArray + _reader.ReadU16(mArray + 2 + mIdx * 4 + 2));
            int ligAttach = ligArray + _reader.ReadU16(ligArray + 2 + bCovIdx * 2);
            int compIdx = 0; for (int j = baseIdx + 1; j < index; j++) if (_gdef.GetGlyphClass(buffer.Info[j].Codepoint) == ManjuGdefEngine.GlyphClass.Mark) compIdx++;
            if (compIdx >= _reader.ReadU16(ligAttach)) compIdx = _reader.ReadU16(ligAttach) - 1;
            int ancOff = _reader.ReadU16(ligAttach + 2 + (compIdx * mClassCount + mClass) * 2); if (ancOff == 0) return false;
            Anchor lAnc = ReadAnchor(ligAttach + ancOff);
            buffer.Pos[index].XOffset = (short)(buffer.Pos[baseIdx].XOffset + lAnc.X - mAnc.X);
            buffer.Pos[index].YOffset = (short)(buffer.Pos[baseIdx].YOffset + lAnc.Y - mAnc.Y);
            return true;
        }

        public bool ApplyMarkToMark(int subOff, ManjuBuffer buffer, int index, ushort flag)
        {
            int mIdx = _helper.GetGlyphCoverage(subOff + _reader.ReadU16(subOff + 2), buffer.Info[index].Codepoint);
            if (mIdx == -1) return false;
            int prevMIdx = FindBase(buffer, index, subOff + _reader.ReadU16(subOff + 4), flag);
            if (prevMIdx < 0) return false;
            int m2CovIdx = _helper.GetGlyphCoverage(subOff + _reader.ReadU16(subOff + 4), buffer.Info[prevMIdx].Codepoint);
            ushort mClassCount = _reader.ReadU16(subOff + 6);
            int m1Array = subOff + _reader.ReadU16(subOff + 8); int m2Array = subOff + _reader.ReadU16(subOff + 10);
            ushort mClass = _reader.ReadU16(m1Array + 2 + mIdx * 4);
            Anchor m1Anc = ReadAnchor(m1Array + _reader.ReadU16(m1Array + 2 + mIdx * 4 + 2));
            Anchor m2Anc = ReadAnchor(m2Array + _reader.ReadU16(m2Array + 2 + m2CovIdx * (mClassCount * 2) + mClass * 2));
            buffer.Pos[index].XOffset = (short)(buffer.Pos[prevMIdx].XOffset + m2Anc.X - m1Anc.X);
            buffer.Pos[index].YOffset = (short)(buffer.Pos[prevMIdx].YOffset + m2Anc.Y - m1Anc.Y);
            return true;
        }

        private int FindBase(ManjuBuffer buf, int idx, int cov, ushort flag)
        {
            for (int i = idx - 1; i >= 0; i--) { if (_helper.GetNextIndex(buf, i + 1, -1, flag) != i) continue; if (_helper.GetGlyphCoverage(cov, buf.Info[i].Codepoint) != -1) return i; }
            return -1;
        }
    }
}

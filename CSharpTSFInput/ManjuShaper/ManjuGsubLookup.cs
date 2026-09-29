using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CSharpTSFInput.ManjuShaper
{
    public unsafe class ManjuGsubLookup
    {
        private readonly ManjuFontReader _reader;
        private readonly ManjuGdefEngine? _gdef;
        private readonly int _gsubBase;

        // Recursion and iteration protection
        [ThreadStatic] private static int _recursionDepth;
        private const int MAX_RECURSION_DEPTH = 50;
        private const int MAX_LOOP_ITERATIONS = 10000;
        private const int MAX_BUFFER_LENGTH = 65536;  // matches HB_BUFFER_MAX_LEN_MIN

        public ManjuGsubLookup(ManjuFontReader reader, ManjuGdefEngine? gdef = null)
        {
            _reader = reader;
            _gdef = gdef;
            _gsubBase = _reader.FindTable(0x47535542);
        }

        #region Helper

        public bool IsIndexIgnored(ManjuBuffer buffer, int index, ushort lookupFlag)
        {
            if (index < 0 || index >= buffer.Length) return true;
            
            // Special case for FVS: Never ignore variation selectors during GSUB/GPOS matching, even if hidden
            if ((buffer.Info[index].Props & ManjuGlyphInfo.FLAG_IS_FVS) != 0) return false;

            // Skip hidden glyphs (e.g. consumed VS, default ignorables)
            if ((buffer.Info[index].Props & ManjuGlyphInfo.FLAG_HIDDEN) != 0) return true;

            if (lookupFlag == 0) return false;

            bool ignoreMarks = (lookupFlag & 0x0008) != 0;
            bool ignoreBase = (lookupFlag & 0x0002) != 0;
            bool ignoreLigatures = (lookupFlag & 0x0004) != 0;
            bool useMarkSet = (lookupFlag & 0x0010) != 0;
            ushort markFilterVal = (ushort)(lookupFlag >> 8);

            uint gid = buffer.Info[index].Codepoint;
            var cls = _gdef?.GetGlyphClass(gid) ?? ManjuGdefEngine.GlyphClass.Unclassified;

            if (cls == ManjuGdefEngine.GlyphClass.Mark)
            {
                if (useMarkSet && _gdef != null) return !_gdef.IsGlyphInFilterSet(markFilterVal, gid);
                if (markFilterVal != 0 && _gdef != null) return _gdef.GetMarkAttachmentClass(gid) != markFilterVal;
                return ignoreMarks;
            }
            if (ignoreBase && cls == ManjuGdefEngine.GlyphClass.Base) return true;
            if (ignoreLigatures && cls == ManjuGdefEngine.GlyphClass.Ligature) return true;

            return false;
        }

        public int GetNextIndex(ManjuBuffer buffer, int start, int direction, ushort lookupFlag)
        {
            int curr = start + direction;
            while (curr >= 0 && curr < buffer.Length)
            {
                if (!IsIndexIgnored(buffer, curr, lookupFlag)) return curr;
                curr += direction;
            }
            return -1;
        }

        public void MergeClusters(ManjuBuffer buffer, int start, int end)
        {
            if (start < 0 || end > buffer.Length || start >= end) return;
            uint minCluster = buffer.Info[start].Cluster;
            for (int i = start + 1; i < end; i++)
            {
                if (buffer.Info[i].Cluster < minCluster) minCluster = buffer.Info[i].Cluster;
            }
            for (int i = start; i < end; i++)
            {
                buffer.Info[i].Cluster = minCluster;
            }
        }

        #endregion

        #region Coverage & Class Definition

        public int GetClassFromDef(int tableOffset, uint glyphId)
        {
            if (tableOffset == 0) return 0;
            ushort format = _reader.ReadU16(tableOffset);
            if (format == 1)
            {
                ushort startGlyph = _reader.ReadU16(tableOffset + 2);
                ushort glyphCount = _reader.ReadU16(tableOffset + 4);
                if (glyphId >= startGlyph && glyphId < startGlyph + glyphCount)
                    return _reader.ReadU16(tableOffset + 6 + (int)(glyphId - startGlyph) * 2);
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
                        return _reader.ReadU16(rangeOffset + 4);
                    if (start < glyphId) low = mid + 1;
                    else high = mid - 1;
                }
            }
            return 0;
        }

        public int GetGlyphCoverage(int tableOffset, uint glyphId)
        {
            if (tableOffset <= 0) return -1;
            ushort format = _reader.ReadU16(tableOffset);
            if (format == 1)
            {
                ushort count = _reader.ReadU16(tableOffset + 2);
                int low = 0, high = count - 1;
                while (low <= high)
                {
                    int mid = (low + high) / 2;
                    ushort val = _reader.ReadU16(tableOffset + 4 + (mid * 2));
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

        #endregion

        #region Subst Types (1-4)

        public uint ApplySingleSubst(int subOffset, uint glyphId)
        {
            ushort format = _reader.ReadU16(subOffset);
            int covOffset = subOffset + _reader.ReadU16(subOffset + 2);
            int covIdx = GetGlyphCoverage(covOffset, glyphId);
            if (covIdx == -1) return glyphId;

            if (format == 1)
            {
                short delta = (short)_reader.ReadU16(subOffset + 4);
                return (uint)(glyphId + delta);
            }
            else if (format == 2)
            {
                return _reader.ReadU16(subOffset + 6 + (covIdx * 2));
            }
            return glyphId;
        }

        public int ApplyMultipleSubst(ManjuBuffer buffer, int index, int subOffset)
        {
            ushort covRelOffset = _reader.ReadU16(subOffset + 2);
            if (covRelOffset == 0 || subOffset + covRelOffset + 2 > _reader.Length) return 1;

            int covOffset = subOffset + covRelOffset;
            int covIdx = GetGlyphCoverage(covOffset, buffer.Info[index].Codepoint);
            if (covIdx == -1) return 1;

            if (subOffset + 6 + (covIdx * 2) + 2 > _reader.Length) return 1;
            ushort seqSetRelOffset = _reader.ReadU16(subOffset + 6 + (covIdx * 2));
            if (seqSetRelOffset == 0 || subOffset + seqSetRelOffset + 4 > _reader.Length) return 1;

            int seqOffset = subOffset + seqSetRelOffset;
            ushort glyphCount = _reader.ReadU16(seqOffset);
            if (glyphCount == 0) return 0; // Glyph deleted
            
            // Check if the whole sequence is within font bounds
            if (seqOffset + 2 + (glyphCount * 2) > _reader.Length) return 1;

            uint cluster = buffer.Info[index].Cluster;
            buffer.Info[index].Codepoint = _reader.ReadU16(seqOffset + 2);

            if (glyphCount > 1)
            {
                int newCount = glyphCount - 1;
                // Ensure we don't exceed max buffer length
                if (buffer.Length + newCount > MAX_BUFFER_LENGTH) return 1;

                buffer.EnsureCapacity(newCount);
                Array.Copy(buffer.Info, index + 1, buffer.Info, index + 1 + newCount, buffer.Length - (index + 1));
                Array.Copy(buffer.Pos, index + 1, buffer.Pos, index + 1 + newCount, buffer.Length - (index + 1));
                for (int i = 0; i < newCount; i++)
                {
                    buffer.Info[index + 1 + i] = new ManjuGlyphInfo { 
                        Codepoint = _reader.ReadU16(seqOffset + 4 + (i * 2)), 
                        Cluster = cluster 
                    };
                    buffer.Pos[index + 1 + i] = new ManjuGlyphPosition();
                }
                buffer.Length += newCount;
                return glyphCount;
            }
            return 1;
        }

        public uint ApplyAlternateSubst(int subOffset, uint glyphId)
        {
            ushort covRelOffset = _reader.ReadU16(subOffset + 2);
            if (covRelOffset == 0 || subOffset + covRelOffset + 2 > _reader.Length) return glyphId;
            int covOffset = subOffset + covRelOffset;

            int covIdx = GetGlyphCoverage(covOffset, glyphId);
            if (covIdx == -1) return glyphId;

            if (subOffset + 6 + (covIdx * 2) + 2 > _reader.Length) return glyphId;
            ushort setRelOffset = _reader.ReadU16(subOffset + 6 + (covIdx * 2));
            if (setRelOffset == 0 || subOffset + setRelOffset + 4 > _reader.Length) return glyphId;

            int setOffset = subOffset + setRelOffset;
            ushort altCount = _reader.ReadU16(setOffset);
            if (altCount == 0) return glyphId;
            return _reader.ReadU16(setOffset + 2);
        }

        public int ApplyLigatureSubst(int subOffset, ManjuBuffer buffer, int index, ushort lookupFlag)
        {
            ushort covRelOffset = _reader.ReadU16(subOffset + 2);
            if (covRelOffset == 0 || subOffset + covRelOffset + 2 > _reader.Length) return 0;
            int covOffset = subOffset + covRelOffset;

            int covIdx = GetGlyphCoverage(covOffset, buffer.Info[index].Codepoint);
            if (covIdx == -1) return 0;

            if (subOffset + 6 + (covIdx * 2) + 2 > _reader.Length) return 0;
            ushort setListRelOffset = _reader.ReadU16(subOffset + 6 + (covIdx * 2));
            if (setListRelOffset == 0 || subOffset + setListRelOffset + 2 > _reader.Length) return 0;
            int setListOffset = subOffset + setListRelOffset;

            ushort ligCount = _reader.ReadU16(setListOffset);
            if (setListOffset + 2 + (ligCount * 2) > _reader.Length) return 0;

            for (int i = 0; i < ligCount; i++)
            {
                ushort ligRelOffset = _reader.ReadU16(setListOffset + 2 + (i * 2));
                if (ligRelOffset == 0 || setListOffset + ligRelOffset + 4 > _reader.Length) continue;
                int ligOffset = setListOffset + ligRelOffset;

                ushort ligGlyph = _reader.ReadU16(ligOffset);
                ushort compCount = _reader.ReadU16(ligOffset + 2);
                if (compCount == 0) continue;
                if (ligOffset + 4 + (compCount - 1) * 2 > _reader.Length) continue;

                int[] matchIndices = new int[compCount];
                matchIndices[0] = index;
                bool match = true;
                int current = index;
                for (int j = 1; j < compCount; j++)
                {
                    current = GetNextIndex(buffer, current, 1, lookupFlag);
                    if (current == -1 || buffer.Info[current].Codepoint != _reader.ReadU16(ligOffset + 4 + (j - 1) * 2)) { match = false; break; }
                    matchIndices[j] = current;
                }
                if (match)
                {
                    MergeClusters(buffer, matchIndices[0], matchIndices[compCount - 1] + 1);
                    uint minCluster = buffer.Info[index].Cluster;
                    buffer.Info[index].Codepoint = ligGlyph;
                    buffer.Info[index].Cluster = minCluster;
                    int removed = 0;
                    for (int j = 1; j < compCount; j++)
                    {
                        int delIdx = matchIndices[j] - removed;
                        // The bound is `delIdx >= buffer.Length`. A `delIdx >= buffer.Length - 1` check
                        // would exclude deletion of the LAST element: when a ligature matches the
                        // final two glyphs of the buffer (e.g. [5, 1489] → [5]), matchIndices[1]=1 and
                        // buffer.Length=2, so delIdx=1 and `1 >= 1` would trigger `continue`, leaving the
                        // buffer unshrunk with a spurious gid=1489 residue. Deleting the last element
                        // needs no Array.Copy (tailLen=0 is a valid no-op), only a Length decrement.
                        if (delIdx < 0 || delIdx >= buffer.Length) continue;

                        int tailLen = buffer.Length - delIdx - 1;
                        if (tailLen > 0)
                        {
                            Array.Copy(buffer.Info, delIdx + 1, buffer.Info, delIdx, tailLen);
                            Array.Copy(buffer.Pos, delIdx + 1, buffer.Pos, delIdx, tailLen);
                        }
                        buffer.Length--;
                        removed++;
                    }
                    return removed;
                }
            }
            return 0;
        }

        #endregion

        #region Contextual Match Helper

        public bool MatchChainRule(int ruleOffset, ManjuBuffer buffer, int index, ushort lookupFlag, int backClassDef, int inClassDef, int lookClassDef, bool isReverse = false)
        {
            if (ruleOffset + 2 > _reader.Length) return false;
            int cur = ruleOffset;
            ushort backCount = _reader.ReadU16(cur); cur += 2;
            if (cur + (backCount * 2) + 2 > _reader.Length) return false;

            int check = index;
            for (int i = 0; i < backCount; i++)
            {
                check = GetNextIndex(buffer, check, isReverse ? 1 : -1, lookupFlag);
                if (check == -1) return false;
                ushort target = _reader.ReadU16(cur); cur += 2;
                if (backClassDef != 0) { if (GetClassFromDef(backClassDef, buffer.Info[check].Codepoint) != target) return false; }
                else { if (buffer.Info[check].Codepoint != target) return false; }
            }
            
            ushort inCount = _reader.ReadU16(cur); cur += 2;
            if (cur + ((inCount - 1) * 2) + 2 > _reader.Length) return false;

            int inputEnd = index;
            for (int i = 1; i < inCount; i++)
            {
                inputEnd = GetNextIndex(buffer, inputEnd, isReverse ? -1 : 1, lookupFlag);
                if (inputEnd == -1) return false;
                ushort target = _reader.ReadU16(cur); cur += 2;
                if (inClassDef != 0) { if (GetClassFromDef(inClassDef, buffer.Info[inputEnd].Codepoint) != target) return false; }
                else { if (buffer.Info[inputEnd].Codepoint != target) return false; }
            }
            
            ushort lookCount = _reader.ReadU16(cur); cur += 2;
            if (cur + (lookCount * 2) > _reader.Length) return false;

            check = inputEnd;
            for (int i = 0; i < lookCount; i++)
            {
                check = GetNextIndex(buffer, check, isReverse ? -1 : 1, lookupFlag);
                if (check == -1) return false;
                ushort target = _reader.ReadU16(cur); cur += 2;
                if (lookClassDef != 0) { if (GetClassFromDef(lookClassDef, buffer.Info[check].Codepoint) != target) return false; }
                else { if (buffer.Info[check].Codepoint != target) return false; }
            }
            return true;
        }

        public void ApplyContextLookups(int lookupRecordOffset, ushort substCount, ManjuBuffer buffer, int index, ushort lookupFlag, uint mask, int matchLen = 1)
        {
            if (lookupRecordOffset + (substCount * 4) > _reader.Length) return;

            // HB-style per-position tracking (match_positions[] in hb-ot-layout-gsubgpos.hh apply_lookup). HB
            // uses a fixed-size C++ stack array with MAX_CONTEXT_LENGTH=64 hard limit; this code uses a List<int>,
            // which scales with matchLen and has no artificial cap. Per-position tracking is correct for nested
            // lookups that change glyph count at arbitrary seqIdx; cumulative deltaLength drifts in that case.
            var matchPositions = new List<int>(matchLen);
            {
                int cursor = index;
                matchPositions.Add(cursor);
                for (int k = 1; k < matchLen; k++)
                {
                    cursor = GetNextIndex(buffer, cursor, 1, lookupFlag);
                    if (cursor == -1) break;
                    matchPositions.Add(cursor);
                }
            }

            if (matchLen > 1 && matchPositions.Count >= matchLen)
            {
                MergeClusters(buffer, index, matchPositions[matchLen - 1] + 1);
            }

            ushort lookupListRelOffset = _reader.ReadU16(_gsubBase + 8);
            if (lookupListRelOffset == 0 || _gsubBase + lookupListRelOffset + 2 > _reader.Length) return;
            int lookupListOffset = _gsubBase + lookupListRelOffset;

            ushort lookupCount = _reader.ReadU16(lookupListOffset);
            int cur = lookupRecordOffset;
            for (int i = 0; i < substCount; i++)
            {
                ushort seqIdx = _reader.ReadU16(cur);
                ushort lookupIdx = _reader.ReadU16(cur + 2);
                cur += 4;

                if (lookupIdx >= lookupCount) continue;
                if (seqIdx >= matchPositions.Count) continue;

                int target = matchPositions[seqIdx];
                if (target < 0 || target >= buffer.Length) continue;

                int oldLen = buffer.Length;
                ProcessLookup(lookupIdx, buffer, start: target, end: target + 1, mask: mask);
                int delta = buffer.Length - oldLen;

                if (delta != 0)
                {
                    // Shift subsequent positions by the buffer-length delta (positive for expansion,
                    // negative for deletion). Positions at or before seqIdx are unchanged.
                    for (int k = seqIdx + 1; k < matchPositions.Count; k++)
                    {
                        matchPositions[k] += delta;
                    }
                }
            }
        }

        #endregion

        #region Contextual Subst (Types 5-6)

        public bool ApplyContextSubst(int subOffset, ManjuBuffer buffer, int index, ushort lookupFlag, uint mask)
        {
            if (subOffset + 2 > _reader.Length) return false;
            ushort format = _reader.ReadU16(subOffset);
            if (format == 1)
            {
                ushort covRelOff = _reader.ReadU16(subOffset + 2);
                if (covRelOff == 0 || subOffset + covRelOff + 2 > _reader.Length) return false;
                int covIdx = GetGlyphCoverage(subOffset + covRelOff, buffer.Info[index].Codepoint);
                if (covIdx == -1) return false;

                if (subOffset + 6 + (covIdx * 2) + 2 > _reader.Length) return false;
                ushort setRelOff = _reader.ReadU16(subOffset + 6 + (covIdx * 2));
                if (setRelOff == 0 || subOffset + setRelOff + 2 > _reader.Length) return false;
                int setOffset = subOffset + setRelOff;

                ushort count = _reader.ReadU16(setOffset);
                if (setOffset + 2 + (count * 2) > _reader.Length) return false;

                for (int r = 0; r < count; r++)
                {
                    ushort ruleRelOff = _reader.ReadU16(setOffset + 2 + (r * 2));
                    if (ruleRelOff == 0 || setOffset + ruleRelOff + 4 > _reader.Length) continue;
                    int ruleOff = setOffset + ruleRelOff;

                    ushort gCount = _reader.ReadU16(ruleOff); ushort sCount = _reader.ReadU16(ruleOff + 2);
                    if (ruleOff + 4 + (gCount - 1) * 2 + (sCount * 4) > _reader.Length) continue;

                    int curr = index; bool match = true;
                    for (int i = 1; i < gCount; i++) { curr = GetNextIndex(buffer, curr, 1, lookupFlag); if (curr == -1 || buffer.Info[curr].Codepoint != _reader.ReadU16(ruleOff + 4 + (i - 1) * 2)) { match = false; break; } }
                    if (match) { ApplyContextLookups(ruleOff + 4 + (gCount - 1) * 2, sCount, buffer, index, lookupFlag, mask, gCount); return true; }
                }
            }
            else if (format == 2)
            {
                ushort covRelOff = _reader.ReadU16(subOffset + 2);
                if (covRelOff == 0 || subOffset + covRelOff + 2 > _reader.Length) return false;
                if (GetGlyphCoverage(subOffset + covRelOff, buffer.Info[index].Codepoint) == -1) return false;

                ushort classDefRelOff = _reader.ReadU16(subOffset + 4);
                if (classDefRelOff == 0 || subOffset + classDefRelOff + 2 > _reader.Length) return false;
                int classDef = subOffset + classDefRelOff;

                ushort classSetCount = _reader.ReadU16(subOffset + 6);
                int startClass = GetClassFromDef(classDef, buffer.Info[index].Codepoint);
                if (startClass < 0 || startClass >= classSetCount) return false;

                if (subOffset + 8 + (startClass * 2) + 2 > _reader.Length) return false;
                ushort setRelOff = _reader.ReadU16(subOffset + 8 + (startClass * 2));
                if (setRelOff == 0 || subOffset + setRelOff + 2 > _reader.Length) return false;
                int setOffset = subOffset + setRelOff;

                ushort count = _reader.ReadU16(setOffset);
                if (setOffset + 2 + (count * 2) > _reader.Length) return false;

                for (int r = 0; r < count; r++)
                {
                    ushort ruleRelOff = _reader.ReadU16(setOffset + 2 + (r * 2));
                    if (ruleRelOff == 0 || setOffset + ruleRelOff + 4 > _reader.Length) continue;
                    int ruleOff = setOffset + ruleRelOff;

                    ushort gCount = _reader.ReadU16(ruleOff); ushort sCount = _reader.ReadU16(ruleOff + 2);
                    if (ruleOff + 4 + (gCount - 1) * 2 + (sCount * 4) > _reader.Length) continue;

                    int curr = index; bool match = true;
                    for (int i = 1; i < gCount; i++) { curr = GetNextIndex(buffer, curr, 1, lookupFlag); if (curr == -1 || GetClassFromDef(classDef, buffer.Info[curr].Codepoint) != _reader.ReadU16(ruleOff + 4 + (i - 1) * 2)) { match = false; break; } }
                    if (match) { ApplyContextLookups(ruleOff + 4 + (gCount - 1) * 2, sCount, buffer, index, lookupFlag, mask, gCount); return true; }
                }
            }
            else if (format == 3)
            {
                if (subOffset + 6 > _reader.Length) return false;
                ushort gCount = _reader.ReadU16(subOffset + 2); ushort sCount = _reader.ReadU16(subOffset + 4);
                if (subOffset + 6 + (gCount * 2) + (sCount * 4) > _reader.Length) return false;

                int curr = index;
                for (int i = 0; i < gCount; i++) { ushort covRel = _reader.ReadU16(subOffset + 6 + i*2); if (covRel == 0 || subOffset + covRel + 2 > _reader.Length) return false; int cov = subOffset + covRel; if (curr == -1 || GetGlyphCoverage(cov, buffer.Info[curr].Codepoint) == -1) return false; if (i < gCount-1) curr = GetNextIndex(buffer, curr, 1, lookupFlag); }
                ApplyContextLookups(subOffset + 6 + gCount*2, sCount, buffer, index, lookupFlag, mask, gCount); return true;
            }
            return false;
        }

        public bool ApplyChainContextSubst(int subOffset, ManjuBuffer buffer, int index, ushort lookupFlag, uint mask)
        {
            if (subOffset + 2 > _reader.Length) return false;
            ushort format = _reader.ReadU16(subOffset);
            if (format == 1)
            {
                ushort covRelOff = _reader.ReadU16(subOffset + 2);
                if (covRelOff == 0 || subOffset + covRelOff + 2 > _reader.Length) return false;
                int covIdx = GetGlyphCoverage(subOffset + covRelOff, buffer.Info[index].Codepoint);
                if (covIdx == -1) return false;

                if (subOffset + 6 + (covIdx * 2) + 2 > _reader.Length) return false;
                ushort setRelOff = _reader.ReadU16(subOffset + 6 + (covIdx * 2));
                if (setRelOff == 0 || subOffset + setRelOff + 2 > _reader.Length) return false;
                int setOffset = subOffset + setRelOff;

                ushort count = _reader.ReadU16(setOffset);
                if (setOffset + 2 + (count * 2) > _reader.Length) return false;

                for (int r = 0; r < count; r++)
                {
                    ushort ruleRelOff = _reader.ReadU16(setOffset + 2 + (r * 2));
                    if (ruleRelOff == 0 || setOffset + ruleRelOff + 2 > _reader.Length) continue;
                    int ruleOff = setOffset + ruleRelOff;

                    if (MatchChainRule(ruleOff, buffer, index, lookupFlag, 0, 0, 0)) {
                        int cur = ruleOff; ushort b = _reader.ReadU16(cur); cur += 2 + b * 2; ushort i = _reader.ReadU16(cur); cur += 2 + (i - 1) * 2; ushort l = _reader.ReadU16(cur); cur += 2 + l * 2; ushort s = _reader.ReadU16(cur); cur += 2;
                        ApplyContextLookups(cur, s, buffer, index, lookupFlag, mask, i); return true;
                    }
                }
            }
            else if (format == 2)
            {
                ushort covRelOff = _reader.ReadU16(subOffset + 2);
                if (covRelOff == 0 || subOffset + covRelOff + 2 > _reader.Length) return false;
                if (GetGlyphCoverage(subOffset + covRelOff, buffer.Info[index].Codepoint) == -1) return false;

                if (subOffset + 10 > _reader.Length) return false;
                ushort backClassRelOff = _reader.ReadU16(subOffset + 4);
                ushort inClassRelOff = _reader.ReadU16(subOffset + 6);
                ushort lookClassRelOff = _reader.ReadU16(subOffset + 8);
                
                int backClass = backClassRelOff != 0 && subOffset + backClassRelOff + 2 <= _reader.Length ? subOffset + backClassRelOff : 0; 
                int inClass = inClassRelOff != 0 && subOffset + inClassRelOff + 2 <= _reader.Length ? subOffset + inClassRelOff : 0; 
                int lookClass = lookClassRelOff != 0 && subOffset + lookClassRelOff + 2 <= _reader.Length ? subOffset + lookClassRelOff : 0;

                ushort classSetCount = _reader.ReadU16(subOffset + 10);
                int startClass = GetClassFromDef(inClass, buffer.Info[index].Codepoint);
                if (startClass < 0 || startClass >= classSetCount) return false;

                if (subOffset + 12 + (startClass * 2) + 2 > _reader.Length) return false;
                ushort setRelOff = _reader.ReadU16(subOffset + 12 + (startClass * 2));
                if (setRelOff == 0 || subOffset + setRelOff + 2 > _reader.Length) return false;
                int setOffset = subOffset + setRelOff;

                ushort count = _reader.ReadU16(setOffset);
                if (setOffset + 2 + (count * 2) > _reader.Length) return false;

                for (int r = 0; r < count; r++)
                {
                    ushort ruleRelOff = _reader.ReadU16(setOffset + 2 + (r * 2));
                    if (ruleRelOff == 0 || setOffset + ruleRelOff + 2 > _reader.Length) continue;
                    int ruleOff = setOffset + ruleRelOff;

                    if (MatchChainRule(ruleOff, buffer, index, lookupFlag, backClass, inClass, lookClass)) {
                        int cur = ruleOff; ushort b = _reader.ReadU16(cur); cur += 2 + b * 2; ushort i = _reader.ReadU16(cur); cur += 2 + (i - 1) * 2; ushort l = _reader.ReadU16(cur); cur += 2 + l * 2; ushort s = _reader.ReadU16(cur); cur += 2;
                        ApplyContextLookups(cur, s, buffer, index, lookupFlag, mask, i); return true;
                    }
                }
            }
            else if (format == 3)
            {
                if (subOffset + 4 > _reader.Length) return false;
                ushort bCount = _reader.ReadU16(subOffset + 2); int cur = subOffset + 4; 
                if (cur + (bCount * 2) + 2 > _reader.Length) return false;

                int check = index;
                for (int i = 0; i < bCount; i++) { check = GetNextIndex(buffer, check, -1, lookupFlag); ushort covRel = _reader.ReadU16(cur); cur += 2; if (check == -1 || covRel == 0 || subOffset + covRel + 2 > _reader.Length || GetGlyphCoverage(subOffset + covRel, buffer.Info[check].Codepoint) == -1) return false; }
                
                ushort inCount = _reader.ReadU16(cur); cur += 2; 
                if (cur + (inCount * 2) + 2 > _reader.Length) return false;
                int inputEnd = index;
                for (int i = 0; i < inCount; i++) { ushort covRel = _reader.ReadU16(cur); cur += 2; if (inputEnd == -1 || covRel == 0 || subOffset + covRel + 2 > _reader.Length || GetGlyphCoverage(subOffset + covRel, buffer.Info[inputEnd].Codepoint) == -1) return false; if (i < inCount - 1) inputEnd = GetNextIndex(buffer, inputEnd, 1, lookupFlag); }
                
                ushort lCount = _reader.ReadU16(cur); cur += 2; 
                if (cur + (lCount * 2) + 2 > _reader.Length) return false;
                check = inputEnd;
                for (int i = 0; i < lCount; i++) { check = GetNextIndex(buffer, check, 1, lookupFlag); ushort covRel = _reader.ReadU16(cur); cur += 2; if (check == -1 || covRel == 0 || subOffset + covRel + 2 > _reader.Length || GetGlyphCoverage(subOffset + covRel, buffer.Info[check].Codepoint) == -1) return false; }
                
                ushort substCount = _reader.ReadU16(cur); cur += 2; 
                ApplyContextLookups(cur, substCount, buffer, index, lookupFlag, mask, inCount); return true;
            }
            return false;
        }

        #endregion

        #region Reverse Chain (Type 8)

        public bool ApplyReverseChainContextSubst(int subOffset, ManjuBuffer buffer, int index, ushort lookupFlag)
        {
            if (subOffset + 4 > _reader.Length) return false;
            ushort format = _reader.ReadU16(subOffset);
            if (format != 1) return false;
            ushort covRelOff = _reader.ReadU16(subOffset + 2);
            if (covRelOff == 0 || subOffset + covRelOff + 2 > _reader.Length) return false;
            int covIdx = GetGlyphCoverage(subOffset + covRelOff, buffer.Info[index].Codepoint);
            if (covIdx == -1) return false;

            int cur = subOffset + 4;
            ushort bCount = _reader.ReadU16(cur); cur += 2; 
            if (cur + (bCount * 2) + 2 > _reader.Length) return false;
            int check = index;
            for (int i = 0; i < bCount; i++) { check = GetNextIndex(buffer, check, -1, lookupFlag); ushort covRel = _reader.ReadU16(cur); cur += 2; if (check == -1 || covRel == 0 || subOffset + covRel + 2 > _reader.Length || GetGlyphCoverage(subOffset + covRel, buffer.Info[check].Codepoint) == -1) return false; }
            
            ushort lCount = _reader.ReadU16(cur); cur += 2; 
            if (cur + (lCount * 2) + 2 > _reader.Length) return false;
            check = index;
            for (int i = 0; i < lCount; i++) { check = GetNextIndex(buffer, check, 1, lookupFlag); ushort covRel = _reader.ReadU16(cur); cur += 2; if (check == -1 || covRel == 0 || subOffset + covRel + 2 > _reader.Length || GetGlyphCoverage(subOffset + covRel, buffer.Info[check].Codepoint) == -1) return false; }
            
            ushort gCount = _reader.ReadU16(cur); cur += 2;
            if (cur + (gCount * 2) > _reader.Length) return false;
            if (covIdx < gCount) { buffer.Info[index].Codepoint = _reader.ReadU16(cur + covIdx * 2); return true; }
            return false;
        }

        #endregion

        public void ProcessLookup(ushort lookupIndex, ManjuBuffer buffer, int start = 0, int end = -1, uint mask = 0xFFFF)
        {
            // Recursion depth protection
            _recursionDepth++;
            if (_recursionDepth > MAX_RECURSION_DEPTH)
            {
                _recursionDepth--;
                return;
            }

            if (buffer.Length > MAX_BUFFER_LENGTH)
            {
                _recursionDepth--;
                return;
            }

            ushort lookupListRelOffset = _reader.ReadU16(_gsubBase + 8);
            if (lookupListRelOffset == 0 || _gsubBase + lookupListRelOffset + 2 > _reader.Length) 
            {
                _recursionDepth--;
                return;
            }
            int lookupListOffset = _gsubBase + lookupListRelOffset;

            ushort lookupCount = _reader.ReadU16(lookupListOffset);
            if (lookupIndex >= lookupCount)
            {
                _recursionDepth--;
                return;
            }

            if (lookupListOffset + 2 + (lookupIndex * 2) + 2 > _reader.Length)
            {
                _recursionDepth--;
                return;
            }
            ushort lookupRelOffset = _reader.ReadU16(lookupListOffset + 2 + (lookupIndex * 2));
            if (lookupRelOffset == 0 || lookupListOffset + lookupRelOffset + 6 > _reader.Length) 
            {
                _recursionDepth--;
                return;
            }
            int lookupOffset = lookupListOffset + lookupRelOffset;

            ushort type = _reader.ReadU16(lookupOffset);
            ushort lookupFlag = _reader.ReadU16(lookupOffset + 2);
            ushort subTableCount = _reader.ReadU16(lookupOffset + 4);

            if (lookupOffset + 6 + (subTableCount * 2) > _reader.Length)
            {
                _recursionDepth--;
                return;
            }

            if (end == -1) end = buffer.Length;

            // Iteration counter for infinite loop detection
            int iterations = 0;

            for (int i = (type == 8 ? end - 1 : start); (type == 8 ? i >= start : i < end); i += (type == 8 ? -1 : 1))
            {
                iterations++;
                if (iterations > MAX_LOOP_ITERATIONS)
                {
                    break;
                }

                if (i < 0 || i >= buffer.Length) continue;
                if ((buffer.Info[i].Mask & mask) == 0) continue;

                // HarfBuzz applies LookupFlag-based skipping in the outer loop of
                // apply_forward / apply_backward (hb-ot-layout-gsubgpos.hh, skipping_iterator_t::may_skip) BEFORE
                // dispatching to subtable handlers. Checking only FLAG_HIDDEN here would let glyphs flagged
                // ignoreMarks/ignoreBase/ignoreLigatures/useMarkSet still trigger substitution and cause
                // spurious MultipleSubst firings on Mongolian vowel sequences. IsIndexIgnored covers the FVS
                // exception + FLAG_HIDDEN + the full LookupFlag bit decoding.
                if (IsIndexIgnored(buffer, i, lookupFlag)) continue;

                for (int s = 0; s < subTableCount; s++)
                {
                    ushort subRelOff = _reader.ReadU16(lookupOffset + 6 + (s * 2));
                    if (subRelOff == 0 || lookupOffset + subRelOff + 2 > _reader.Length) continue;
                    int subOffset = lookupOffset + subRelOff;

                    bool applied = false;
                    switch (type)
                    {
                        case 1: { uint old = buffer.Info[i].Codepoint; buffer.Info[i].Codepoint = ApplySingleSubst(subOffset, old); applied = (old != buffer.Info[i].Codepoint); } break;
                        // Align to HB's MultipleSubstFormat1::apply three-branch
                        // semantics: glyphCount==0 delete (applied), glyphCount==1 single replace (applied
                        // iff codepoint actually changed — not a no-match early return), glyphCount>1 expand
                        // (applied). An `n != 1` check alone would miss the glyphCount==1-applied case, because the
                        // method returns 1 for both "no match" and "matched + single replace".
                        case 2: { uint oldCp = buffer.Info[i].Codepoint; int n = ApplyMultipleSubst(buffer, i, subOffset);
                                    if (n == 0) { applied = true; i--; end--; }
                                    else if (n > 1) { applied = true; i += (n - 1); end += (n - 1); }
                                    else if (n == 1 && buffer.Info[i].Codepoint != oldCp) { applied = true; } } break;
                        case 3: { uint old = buffer.Info[i].Codepoint; buffer.Info[i].Codepoint = ApplyAlternateSubst(subOffset, old); applied = (old != buffer.Info[i].Codepoint); } break;
                        case 4: { int n = ApplyLigatureSubst(subOffset, buffer, i, lookupFlag); if (n > 0) { applied = true; end -= n; } } break;
                        case 5: applied = ApplyContextSubst(subOffset, buffer, i, lookupFlag, mask); break;
                        case 6: applied = ApplyChainContextSubst(subOffset, buffer, i, lookupFlag, mask); break;
                        case 7: { ushort extType = _reader.ReadU16(subOffset + 2); uint extRelOff = _reader.ReadU32(subOffset + 4);
                                    if (extRelOff != 0 && subOffset + (int)extRelOff + 2 <= _reader.Length) applied = ProcessTypeInternal(extType, subOffset + (int)extRelOff, buffer, i, lookupFlag, mask); } break;
                        case 8: applied = ApplyReverseChainContextSubst(subOffset, buffer, i, lookupFlag); break;
                    }
                    if (applied) break;
                }
            }
            _recursionDepth--;
        }

        // Align to HB's ExtensionSubst::dispatch which forwards to any
        // inner lookup type 1/2/3/4/5/6/8 (type 7 recursion is spec-forbidden). Omitting types 2
        // (Multiple) and 4 (Ligature) would make Extension-wrapped MultipleSubst / LigatureSubst silent no-ops.
        // Note: Extension-wrapped type 2 glyphCount>1 expansion's outer i/end are not updated by the caller
        // (ProcessLookup case 7). This is a known recursion-boundary limitation.
        private bool ProcessTypeInternal(int type, int off, ManjuBuffer buffer, int i, ushort flag, uint mask)
        {
            switch (type)
            {
                case 1:
                    {
                        uint old = buffer.Info[i].Codepoint;
                        buffer.Info[i].Codepoint = ApplySingleSubst(off, old);
                        return buffer.Info[i].Codepoint != old;
                    }
                case 2:
                    {
                        uint old = buffer.Info[i].Codepoint;
                        int n = ApplyMultipleSubst(buffer, i, off);
                        return n == 0 || n > 1 || (n == 1 && buffer.Info[i].Codepoint != old);
                    }
                case 3:
                    {
                        uint old = buffer.Info[i].Codepoint;
                        buffer.Info[i].Codepoint = ApplyAlternateSubst(off, old);
                        return buffer.Info[i].Codepoint != old;
                    }
                case 4: return ApplyLigatureSubst(off, buffer, i, flag) > 0;
                case 5: return ApplyContextSubst(off, buffer, i, flag, mask);
                case 6: return ApplyChainContextSubst(off, buffer, i, flag, mask);
                case 8: return ApplyReverseChainContextSubst(off, buffer, i, flag);
                default: return false;
            }
        }
    }
}

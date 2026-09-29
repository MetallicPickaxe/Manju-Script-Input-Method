using System;
using System.Collections.Generic;

namespace CSharpTSFInput.ManjuShaper
{
    public class ManjuGsubEngine
    {
        private readonly ManjuFontReader _reader;
        private readonly ManjuGsubLookup _lookup;
        private readonly ManjuGsubFeatureNavigator _nav;
        private readonly int _gsubOffset;

        public ManjuGsubEngine(ManjuFontReader reader)
        {
            _reader = reader;
            _gsubOffset = _reader.FindTable(0x47535542);
            var gdef = new ManjuGdefEngine(reader);
            _lookup = new ManjuGsubLookup(reader, gdef);
            _nav = new ManjuGsubFeatureNavigator(_reader, _gsubOffset);
        }

        public void ApplyManjuFeatures(ManjuBuffer buffer, string scriptTag = "mong", string langTag = "dflt", bool isVertical = true, List<string>? features = null)
        {
            if (_gsubOffset == -1 || buffer == null || buffer.Length == 0)
            {
                return;
            }

            // 2. build the active feature list
            // Matches the feature collection of the HarfBuzz Arabic shaper (hb-ot-shaper-arabic.cc).
            // In HarfBuzz, Mongolian goes down the Arabic shaper branch, not the Universal Shaping Engine,
            // so rclt/calt must be applied: they carry the core glyph-selection logic, including contextual
            // shaping at string boundaries. Without them an INIT glyph falls back to the medial form, which looks like MEDI.
            //
            // rclt/calt MultipleSubst can insert an extra glyph on this font (for instance the
            //         masculine/feminine marker in Noto Sans Mongolian lookups 60/64/75). Blocking such a
            //         lookup is the job of the ManjuGsubLookup layer, which must match what HarfBuzz uses
            //         to block it (LookupFlag / GDEF class checks / chaining conditions / the arabic_fallback
            //         pause, and so on). Dropping the whole feature from the active list would also disable
            //         the contextual shaping rules that do work.
            var activeFeatures = new List<string> { "ccmp", "locl" };
            activeFeatures.AddRange(new[] { "isol", "fina", "fin2", "fin3", "medi", "med2", "init" });
            activeFeatures.AddRange(new[] { "rlig", "rclt", "calt" });
            // HarfBuzz's collect_features_default adds `liga` and `clig` to the
            // default GSUB feature set, and the Arabic shaper inherits this set. Noto Sans Mongolian uses `liga`
            // for consonant-cluster ligatures; omitting liga/clig leaves those ligatures unrendered.
            // Empirically, liga/clig/rlig are no-ops for the Manchu/Mongolian letter range in this font:
            // the visible 2→1 ligature merges come from the REQUIRED
            // contextual shaping (calt/rclt), which also selects init/medi/fina forms and cannot be removed
            // without breaking the script. Do NOT try to suppress merges by toggling liga/clig here.
            activeFeatures.AddRange(new[] { "liga", "clig" });

            if (isVertical)
            {
                var vrt2Lookups = _nav.GetLookupIndices("vrt2", scriptTag, langTag);
                if (vrt2Lookups.Count > 0) activeFeatures.Add("vrt2");
                else activeFeatures.Add("vert");
            }

            // 3. handle the intercept requirements
            if (features != null)
            {
                foreach (var f in features)
                {
                    if (f.StartsWith("-")) 
                    {
                        string tag = f.Substring(1);
                        activeFeatures.Remove(tag);
                    }
                    else
                    {
                        string tag = f.StartsWith("+") ? f.Substring(1) : f;
                        if (!activeFeatures.Contains(tag)) activeFeatures.Add(tag);
                    }
                }
            }

            // Allocate per-feature unique bits at plan time, aligning to HB's
            // (hb-ot-shape-plan.cc / hb_ot_map_builder_t) model where each enabled feature claims its own
            // mask bit. Joining features keep the fixed arabic_action bits (0x01..0x40); non-joining features
            // (ccmp/locl/rlig/rclt/calt/liga/clig/vrt2|vert + any user features) get bits 7..31 in order of
            // their appearance in activeFeatures. A single 0xFFFF "mask universe" would let every
            // feature fire on transparent / ignored glyphs regardless of join state.
            var featureMasks = new Dictionary<string, uint>();
            uint nonJoiningAccumMask = 0;
            int nextNonJoiningBit = 7;
            foreach (var f in activeFeatures)
            {
                uint m = f switch
                {
                    "init" => 0x0001u, "medi" => 0x0002u, "fina" => 0x0004u, "isol" => 0x0008u,
                    "fin2" => 0x0010u, "fin3" => 0x0020u, "med2" => 0x0040u,
                    _ => nextNonJoiningBit < 32 ? (1u << nextNonJoiningBit++) : 0u
                };
                if (!featureMasks.ContainsKey(f)) featureMasks[f] = m;
                if ((m & 0x007Fu) == 0 && m != 0) nonJoiningAccumMask |= m;
            }

            // Per-glyph mask = joining-action bit (if any) | all non-joining feature bits.
            // Transparent / NONE-action glyphs get only the non-joining bits (so ccmp/rclt/calt/liga still
            // reach them, but joining features init/medi/fina/isol/fin2/fin3/med2 do not fire on them).
            for (int i = 0; i < buffer.Length; i++)
            {
                var action = (ManjuArabicShaper.Action)buffer.Info[i].ArabicAction;
                uint joiningBit = action switch
                {
                    ManjuArabicShaper.Action.INIT => 0x0001u,
                    ManjuArabicShaper.Action.MEDI => 0x0002u,
                    ManjuArabicShaper.Action.FINA => 0x0004u,
                    ManjuArabicShaper.Action.ISOL => 0x0008u,
                    ManjuArabicShaper.Action.FIN2 => 0x0010u,
                    ManjuArabicShaper.Action.FIN3 => 0x0020u,
                    ManjuArabicShaper.Action.MED2 => 0x0040u,
                    _ => 0u
                };
                buffer.Info[i].Mask = joiningBit | nonJoiningAccumMask;
            }

            // Plan-compile step: deduplicate lookups across features, OR-merging
            // their feature masks. Aligned to HB `hb_ot_map_builder_t::compile` in hb-ot-map.cc:362-378 which
            // sorts lookups within a stage and merges duplicates via `lookups.arrayZ[j].mask |= lookups.arrayZ[i].mask`.
            // Without this step, a lookup referenced by multiple features (e.g. Noto Sans Mongolian's lookup 60
            // appears in both `rclt` and `calt` with identical lookup lists) fires once per feature — producing
            // duplicate MultipleSubst expansions that HB does not emit.
            var mergedLookups = new Dictionary<ushort, uint>();
            var lookupOrder = new List<ushort>();
            foreach (var feature in activeFeatures)
            {
                var lookups = _nav.GetLookupIndices(feature, scriptTag, langTag);
                if (lookups.Count == 0) continue;

                if (!featureMasks.TryGetValue(feature, out uint featureMask) || featureMask == 0) continue;
                foreach (var idx in lookups)
                {
                    if (mergedLookups.TryGetValue(idx, out uint existing))
                    {
                        mergedLookups[idx] = existing | featureMask;
                    }
                    else
                    {
                        mergedLookups[idx] = featureMask;
                        lookupOrder.Add(idx);
                    }
                }
            }

            // 4. run it (deduplicated, applied once, with the masks OR-ed together)
            foreach (var idx in lookupOrder)
            {
                _lookup.ProcessLookup(idx, buffer, mask: mergedLookups[idx]);
            }
        }

        // Build a reverse map {ligature glyph -> component glyphs} from ALL GSUB type-4
        // (ligature) subtables, incl. Extension(type-7)-wrapped ones. The dictionary window's per-cell separated-form
        // display uses this to decompose a fused velar+vowel glyph (e.g. GhI) back into its real ligature
        // components (Gh + I) — the I being the EXACT i form the font ligated, not an FVS substitute. Pure
        // read of the font's own ligature table; build once per font and cache at the caller.
        public Dictionary<uint, uint[]> BuildLigatureReverseMap()
        {
            var map = new Dictionary<uint, uint[]>();
            if (_gsubOffset < 0 || _gsubOffset + 10 > _reader.Length) return map;
            int lookupList = _gsubOffset + _reader.ReadU16(_gsubOffset + 8); // GSUB header: lookupListOffset @ +8
            if (lookupList <= _gsubOffset || lookupList + 2 > _reader.Length) return map;
            ushort lookupCount = _reader.ReadU16(lookupList);
            for (int li = 0; li < lookupCount; li++)
            {
                int lkPos = lookupList + 2 + li * 2;
                if (lkPos + 2 > _reader.Length) break;
                int lookup = lookupList + _reader.ReadU16(lkPos);
                if (lookup + 6 > _reader.Length) continue;
                ushort lookupType = _reader.ReadU16(lookup);
                ushort subCount = _reader.ReadU16(lookup + 4);
                for (int s = 0; s < subCount; s++)
                {
                    int subPos = lookup + 6 + s * 2;
                    if (subPos + 2 > _reader.Length) break;
                    int sub = lookup + _reader.ReadU16(subPos);
                    int effType = lookupType, effSub = sub;
                    if (lookupType == 7) // Extension Substitution: format1 = ExtType(@+2), ExtOffset(u32 @+4)
                    {
                        if (sub + 8 > _reader.Length) continue;
                        effType = _reader.ReadU16(sub + 2);
                        effSub = sub + (int)_reader.ReadU32(sub + 4);
                    }
                    if (effType == 4) ParseLigatureSubtable(effSub, map);
                }
            }
            return map;
        }

        private void ParseLigatureSubtable(int sub, Dictionary<uint, uint[]> map)
        {
            if (sub <= 0 || sub + 6 > _reader.Length) return;
            if (_reader.ReadU16(sub) != 1) return; // LigatureSubstFormat1
            var firsts = EnumerateCoverage(sub + _reader.ReadU16(sub + 2));
            ushort ligSetCount = _reader.ReadU16(sub + 4);
            for (int i = 0; i < ligSetCount && i < firsts.Count; i++)
            {
                int lsPos = sub + 6 + i * 2;
                if (lsPos + 2 > _reader.Length) break;
                int ligSet = sub + _reader.ReadU16(lsPos);
                if (ligSet + 2 > _reader.Length) continue;
                ushort ligCount = _reader.ReadU16(ligSet);
                for (int j = 0; j < ligCount; j++)
                {
                    int lgPos = ligSet + 2 + j * 2;
                    if (lgPos + 2 > _reader.Length) break;
                    int lig = ligSet + _reader.ReadU16(lgPos);
                    if (lig + 4 > _reader.Length) continue;
                    uint ligGlyph = _reader.ReadU16(lig);
                    ushort compCount = _reader.ReadU16(lig + 2);
                    if (compCount < 1 || compCount > 16) continue;
                    if (lig + 4 + (compCount - 1) * 2 > _reader.Length) continue;
                    var comps = new uint[compCount];
                    comps[0] = firsts[i];
                    for (int c = 1; c < compCount; c++) comps[c] = _reader.ReadU16(lig + 4 + (c - 1) * 2);
                    if (!map.ContainsKey(ligGlyph)) map[ligGlyph] = comps;
                }
            }
        }

        // Enumerate a Coverage table into a list where list[coverageIndex] = glyph id.
        private List<uint> EnumerateCoverage(int covOff)
        {
            var list = new List<uint>();
            if (covOff <= 0 || covOff + 4 > _reader.Length) return list;
            ushort format = _reader.ReadU16(covOff);
            if (format == 1)
            {
                ushort count = _reader.ReadU16(covOff + 2);
                for (int i = 0; i < count; i++)
                {
                    int p = covOff + 4 + i * 2;
                    if (p + 2 > _reader.Length) break;
                    list.Add(_reader.ReadU16(p));
                }
            }
            else if (format == 2)
            {
                ushort rangeCount = _reader.ReadU16(covOff + 2);
                var byIdx = new Dictionary<int, uint>();
                int maxIdx = -1;
                for (int r = 0; r < rangeCount; r++)
                {
                    int ro = covOff + 4 + r * 6;
                    if (ro + 6 > _reader.Length) break;
                    ushort start = _reader.ReadU16(ro);
                    ushort end = _reader.ReadU16(ro + 2);
                    ushort startIdx = _reader.ReadU16(ro + 4);
                    for (int g = start; g <= end; g++) { int idx = startIdx + (g - start); byIdx[idx] = (uint)g; if (idx > maxIdx) maxIdx = idx; }
                }
                for (int i = 0; i <= maxIdx; i++) list.Add(byIdx.TryGetValue(i, out var g) ? g : 0u);
            }
            return list;
        }
    }
}

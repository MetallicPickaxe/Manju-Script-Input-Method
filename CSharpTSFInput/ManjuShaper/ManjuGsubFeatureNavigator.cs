using System;
using System.Collections.Generic;

namespace CSharpTSFInput.ManjuShaper
{
    public class ManjuGsubFeatureNavigator
    {
        private readonly ManjuFontReader _reader;
        private readonly int _gsubBase;

        public ManjuGsubFeatureNavigator(ManjuFontReader reader, int gsubBase)
        {
            _reader = reader;
            _gsubBase = gsubBase;
        }

        /// <summary>
        /// Finds every lookup index belonging to one feature (e.g. 'init').
        /// </summary>
        public List<ushort> GetLookupIndices(string featureTag, string scriptTag = "mong", string langTag = "dflt")
        {
            var indices = new List<ushort>();
            if (_gsubBase == -1 || _gsubBase + 10 > _reader.Length) return indices;

            uint sTag = TagToUint(scriptTag);
            uint lTag = TagToUint(langTag);
            uint fTag = TagToUint(featureTag);

            // 1. locate the script in the ScriptList
            ushort scriptListRelOffset = _reader.ReadU16(_gsubBase + 4);
            if (scriptListRelOffset == 0 || _gsubBase + scriptListRelOffset + 2 > _reader.Length) return indices;
            int scriptListOffset = _gsubBase + scriptListRelOffset;
            
            ushort scriptCount = _reader.ReadU16(scriptListOffset);
            if (scriptListOffset + 2 + (scriptCount * 6) > _reader.Length) return indices;

            int scriptTableOffset = -1;

            for (int i = 0; i < scriptCount; i++)
            {
                int entry = scriptListOffset + 2 + (i * 6);
                if (_reader.ReadU32(entry) == sTag)
                {
                    ushort scriptTableRelOffset = _reader.ReadU16(entry + 4);
                    if (scriptTableRelOffset != 0 && scriptListOffset + scriptTableRelOffset + 4 <= _reader.Length) 
                        scriptTableOffset = scriptListOffset + scriptTableRelOffset;
                    break;
                }
            }

            // Fallback chain: Requested -> DFLT -> latn -> First available
            if (scriptTableOffset == -1)
            {
                uint[] fallbacks = { TagToUint("DFLT"), TagToUint("latn") };
                foreach (var fTag2 in fallbacks)
                {
                    for (int i = 0; i < scriptCount; i++)
                    {
                        int entry = scriptListOffset + 2 + (i * 6);
                        if (_reader.ReadU32(entry) == fTag2)
                        {
                            ushort scriptTableRelOffset = _reader.ReadU16(entry + 4);
                            if (scriptTableRelOffset != 0 && scriptListOffset + scriptTableRelOffset + 4 <= _reader.Length) 
                                scriptTableOffset = scriptListOffset + scriptTableRelOffset;
                            break;
                        }
                    }
                    if (scriptTableOffset != -1) break;
                }
            }

            if (scriptTableOffset == -1 && scriptCount > 0)
            {
                ushort firstScriptTableRelOffset = _reader.ReadU16(scriptListOffset + 2 + 4);
                if (firstScriptTableRelOffset != 0 && scriptListOffset + firstScriptTableRelOffset + 4 <= _reader.Length) 
                    scriptTableOffset = scriptListOffset + firstScriptTableRelOffset;
            }

            if (scriptTableOffset == -1) return indices;

            // 2. locate the LangSys
            int langSysOffset = -1;
            ushort langCount = _reader.ReadU16(scriptTableOffset + 2);
            if (scriptTableOffset + 4 + (langCount * 6) > _reader.Length) return indices;
            
            // when a specific language tag is requested and it is not "dflt"
            if (lTag != TagToUint("dflt"))
            {
                for (int i = 0; i < langCount; i++)
                {
                    int entry = scriptTableOffset + 4 + (i * 6);
                    if (_reader.ReadU32(entry) == lTag)
                    {
                        ushort langSysRelOffset = _reader.ReadU16(entry + 4);
                        if (langSysRelOffset != 0 && scriptTableOffset + langSysRelOffset + 6 <= _reader.Length) 
                            langSysOffset = scriptTableOffset + langSysRelOffset;
                        break;
                    }
                }
            }

            // when the specific language is not found, or dflt was requested, use the default LangSys
            if (langSysOffset == -1)
            {
                ushort dfltOff = _reader.ReadU16(scriptTableOffset);
                if (dfltOff != 0 && scriptTableOffset + dfltOff + 6 <= _reader.Length) 
                {
                    langSysOffset = scriptTableOffset + dfltOff;
                }
                else if (langCount > 0) 
                {
                    ushort firstLangSysRelOffset = _reader.ReadU16(scriptTableOffset + 4 + 4);
                    if (firstLangSysRelOffset != 0 && scriptTableOffset + firstLangSysRelOffset + 6 <= _reader.Length) 
                        langSysOffset = scriptTableOffset + firstLangSysRelOffset;
                }
            }

            if (langSysOffset == -1) return indices;

            // 3. collect every feature index under that LangSys
            var featureIndices = new List<ushort>();
            ushort reqFeatureIdx = _reader.ReadU16(langSysOffset + 2);
            if (reqFeatureIdx != 0xFFFF)
            {
                featureIndices.Add(reqFeatureIdx);
            }

            ushort featureIndexCount = _reader.ReadU16(langSysOffset + 4);
            if (langSysOffset + 6 + (featureIndexCount * 2) > _reader.Length) return indices;

            for (int i = 0; i < featureIndexCount; i++)
            {
                featureIndices.Add(_reader.ReadU16(langSysOffset + 6 + (i * 2)));
            }

            // 4. keep the indices in the FeatureList whose tag matches featureTag
            ushort featureListRelOffset = _reader.ReadU16(_gsubBase + 6);
            if (featureListRelOffset == 0 || _gsubBase + featureListRelOffset + 2 > _reader.Length) return indices;
            int featureListOffset = _gsubBase + featureListRelOffset;

            ushort totalFeatureCount = _reader.ReadU16(featureListOffset);

            foreach (var fIdx in featureIndices)
            {
                if (fIdx >= totalFeatureCount) continue;

                int featureRecordOffset = featureListOffset + 2 + (fIdx * 6);
                if (featureRecordOffset + 6 > _reader.Length) continue;

                if (_reader.ReadU32(featureRecordOffset) == fTag)
                {
                    ushort featureTableRelOffset = _reader.ReadU16(featureRecordOffset + 4);
                    if (featureTableRelOffset == 0 || featureListOffset + featureTableRelOffset + 4 > _reader.Length) continue;

                    int featureTableOffset = featureListOffset + featureTableRelOffset;
                    ushort lookupCount = _reader.ReadU16(featureTableOffset + 2);
                    if (featureTableOffset + 4 + (lookupCount * 2) > _reader.Length) continue;

                    for (int j = 0; j < lookupCount; j++)
                    {
                        indices.Add(_reader.ReadU16(featureTableOffset + 4 + (j * 2)));
                    }
                }
            }

            return indices;
        }

        private uint TagToUint(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return 0;
            uint res = 0;
            for (int i = 0; i < 4; i++)
            {
                char c = i < tag.Length ? tag[i] : ' ';
                res = (res << 8) | (byte)c;
            }
            return res;
        }
    }
}

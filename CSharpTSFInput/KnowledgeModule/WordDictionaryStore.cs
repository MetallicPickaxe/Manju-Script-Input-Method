using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace CSharpTSFInput.WordDictionary
{
    /// <summary>
    /// WordDictionary store for Manchu lemma data.
    ///
    /// The type loads a lemma corpus and answers prefix lookups. Load() tolerates missing files, and
    /// LookupWordPrefix() returns an empty list when nothing is loaded, so an installation with no
    /// dictionary behaves as one with an empty dictionary.
    /// </summary>
    public sealed class WordDictionaryStore
    {
        private readonly List<LemmaEntry> _lemmas = new();
        private readonly Dictionary<string, List<int>> _prefixIndex = new();
        private bool _loaded;

        // The key romanization, owned by THIS dictionary: code points → this
        // dictionary's index key. Loaded from the dictionary's own dictionary.yaml (key_romanization)
        // → Resource/Romanization/<name>.yaml. null until Load resolves it; LookupByScript needs it.
        private CSharpTSFInput.Romanization.Romanizer? _keyRomanizer;

        /// <summary>True once a successful Load() has populated the store.</summary>
        public bool IsLoaded => _loaded;

        /// <summary>Lemma count after load. Zero when nothing is loaded.</summary>
        public int LemmaCount => _lemmas.Count;

        /// <summary>
        /// Load JSONL + index from the given directory. Tolerant of missing files — if either is
        /// absent, the store stays empty and IsLoaded stays false; no exception thrown.
        /// </summary>
        public void Load(string resourceDirectory)
        {
            if (string.IsNullOrEmpty(resourceDirectory)) return;
            if (!Directory.Exists(resourceDirectory)) return;

            string jsonlPath = Path.Combine(resourceDirectory, "manchu_lemmas.jsonl");
            string indexPath = Path.Combine(resourceDirectory, "manchu_index.json");

            if (!File.Exists(jsonlPath)) return;

            try
            {
                LoadLemmas(jsonlPath);
                if (File.Exists(indexPath))
                {
                    LoadIndex(indexPath);
                }
                else
                {
                    BuildIndexInMemory();
                }
                _loaded = _lemmas.Count > 0;
                LoadKeyRomanizer(resourceDirectory);
            }
            catch (Exception)
            {
                _lemmas.Clear();
                _prefixIndex.Clear();
                _loaded = false;
            }
        }

        // Load the key romanization: read this dictionary's own dictionary.yaml (`key_romanization`)
        // and load Resource/Romanization/<name>.yaml (a sibling of the Dictionary/ tree). The dictionary
        // OWNS the declaration; the romanization file ships with the IME.
        private void LoadKeyRomanizer(string resourceDirectory)
        {
            try
            {
                string manifestPath = Path.Combine(resourceDirectory, "dictionary.yaml");
                if (!File.Exists(manifestPath))
                {
                    return;
                }
                var root = CSharpTSFInput.Core.Configuration.MiniYamlReader.ParseFile(manifestPath);
                CSharpTSFInput.Core.Configuration.MiniYamlReader.Node? keyNode = null;
                root.Mapping?.TryGetValue("key_romanization", out keyNode);
                string? name = keyNode?.Scalar;
                if (string.IsNullOrWhiteSpace(name))
                {
                    return;
                }
                // Resource/Dictionary/<dict>/  ->  Resource/Romanization/<name>.yaml
                string romanPath = Path.Combine(resourceDirectory, "..", "..", "Romanization", name + ".yaml");
                _keyRomanizer = CSharpTSFInput.Romanization.Romanizer.Load(romanPath, name);
                if (_keyRomanizer.IsEmpty)
                {
                    _keyRomanizer = null;
                }
            }
            catch (Exception)
            {
                _keyRomanizer = null;
            }
        }

        private void LoadLemmas(string jsonlPath)
        {
            using var reader = new StreamReader(jsonlPath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                LemmaEntry? entry = TryParseLemma(line);
                if (entry != null) _lemmas.Add(entry);
            }
        }

        // Parse one JSONL line. Two shapes appear in the wild:
        //
        //   Shape A (pos == "romanization"):
        //     {"word": "de", "pos": "romanization",
        //      "senses": [{"alt_of": [{"word": "ᡩᡝ"}], "glosses": [...]}]}
        //     → word == Möllendorff Latin, Manchu script in senses[].alt_of[0].word
        //
        //   Shape B (pos == "noun"/"verb"/etc.):
        //     {"word": "ᡩᡝ", "pos": "noun",
        //      "forms": [{"form": "de", "tags": ["romanization"]}, ...],
        //      "senses": [{"glosses": [...]}]}
        //     → word == Manchu script, Möllendorff in forms[].form where tags contains "romanization"
        //
        // We extract BOTH a Manchu-script form and a Möllendorff Latin form whenever possible.
        private static LemmaEntry? TryParseLemma(string jsonLine)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonLine);
                var root = doc.RootElement;
                string word = root.TryGetProperty("word", out var w) ? (w.GetString() ?? string.Empty) : string.Empty;
                string pos = root.TryGetProperty("pos", out var p) ? (p.GetString() ?? string.Empty) : string.Empty;
                string? ety = root.TryGetProperty("etymology_text", out var e) ? e.GetString() : null;
                var glosses = new List<string>();
                string manchuScript = string.Empty;
                string mollendorff = string.Empty;

                if (root.TryGetProperty("senses", out var senses) && senses.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sense in senses.EnumerateArray())
                    {
                        if (sense.TryGetProperty("glosses", out var gl) && gl.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var g in gl.EnumerateArray())
                            {
                                string? gs = g.GetString();
                                if (!string.IsNullOrEmpty(gs)) glosses.Add(gs);
                            }
                        }
                        // Shape A: alt_of points to the Manchu-script form.
                        if (string.IsNullOrEmpty(manchuScript) && sense.TryGetProperty("alt_of", out var altOf) && altOf.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var ao in altOf.EnumerateArray())
                            {
                                if (ao.TryGetProperty("word", out var aoW))
                                {
                                    string? aoWStr = aoW.GetString();
                                    if (!string.IsNullOrEmpty(aoWStr)) { manchuScript = aoWStr; break; }
                                }
                            }
                        }
                    }
                }

                if (pos == "romanization")
                {
                    // word is Möllendorff
                    mollendorff = word;
                    // manchuScript already set from alt_of above (if present)
                }
                else
                {
                    // word is Manchu script; look for Möllendorff in forms[]
                    manchuScript = word;
                    if (root.TryGetProperty("forms", out var forms) && forms.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var form in forms.EnumerateArray())
                        {
                            if (!form.TryGetProperty("form", out var fForm)) continue;
                            string? formStr = fForm.GetString();
                            if (string.IsNullOrEmpty(formStr)) continue;
                            bool isRomanization = false;
                            if (form.TryGetProperty("tags", out var fTags) && fTags.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var t in fTags.EnumerateArray())
                                {
                                    string? ts = t.GetString();
                                    if (ts == "romanization" || ts == "transliteration") { isRomanization = true; break; }
                                }
                            }
                            if (isRomanization)
                            {
                                mollendorff = formStr;
                                break;
                            }
                        }
                    }
                }

                // Need at least one of the two surface forms; otherwise the entry is unusable.
                if (string.IsNullOrEmpty(manchuScript) && string.IsNullOrEmpty(mollendorff)) return null;
                return new LemmaEntry(manchuScript, mollendorff, pos, glosses, ety);
            }
            catch
            {
                return null;
            }
        }

        private void LoadIndex(string indexPath)
        {
            string raw = File.ReadAllText(indexPath);
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;
            foreach (var property in root.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Array) continue;
                var ids = new List<int>();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out int id))
                    {
                        ids.Add(id);
                    }
                }
                _prefixIndex[property.Name] = ids;
            }
        }

        private void BuildIndexInMemory()
        {
            for (int i = 0; i < _lemmas.Count; i++)
            {
                string mol = _lemmas[i].Mollendorff;
                if (string.IsNullOrEmpty(mol)) continue;
                // Index every prefix of length 1..min(8, mol.Length) for cheap lookup.
                int maxPrefixLen = Math.Min(8, mol.Length);
                for (int p = 1; p <= maxPrefixLen; p++)
                {
                    string prefix = mol.Substring(0, p);
                    if (!_prefixIndex.TryGetValue(prefix, out var bucket))
                    {
                        bucket = new List<int>();
                        _prefixIndex[prefix] = bucket;
                    }
                    bucket.Add(i);
                }
            }
        }

        /// <summary>
        /// Look up lemmas matching a Möllendorff prefix. Returns empty list when not loaded or no
        /// match. Result is bounded (max 32 entries) to keep candidate list manageable.
        /// </summary>
        // Fold a romanization to the form the index actually stores. Our canonical
        // romanizations are authored with COMBINING diacritics + the TYPOGRAPHIC apostrophe U+2018,
        // but the index keys on disk are PRECOMPOSED (ū/š/ž = U+016B/0161/017E)
        // and ASCII apostrophe U+0027. Normalize the query to match — applied to every lookup, so both
        // the script path (LookupByScript → combining + ‘) and direct callers compare on equal terms.
        // (The index data is already in the precomposed/ASCII form, so this is a no-op on it.)
        private static string DictKeyNorm(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var sb = new System.Text.StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '\u2018' || c == '\u2019' || c == '\u02BB') { sb.Append('\''); continue; } // fancy apostrophes -> ASCII '
                if (i + 1 < s.Length)
                {
                    char n = s[i + 1];
                    if (c == 'u' && n == '\u0304') { sb.Append('\u016B'); i++; continue; } // u + COMBINING MACRON -> precomposed
                    if (c == 's' && n == '\u030C') { sb.Append('\u0161'); i++; continue; } // s + COMBINING CARON  -> precomposed
                    if (c == 'z' && n == '\u030C') { sb.Append('\u017E'); i++; continue; } // z + COMBINING CARON  -> precomposed
                }
                sb.Append(c);
            }
            return sb.ToString();
        }

        public IReadOnlyList<LemmaEntry> LookupWordPrefix(string mollendorffPrefix)
        {
            var result = new List<LemmaEntry>();
            if (!_loaded || string.IsNullOrEmpty(mollendorffPrefix)) return result;
            mollendorffPrefix = DictKeyNorm(mollendorffPrefix);
            if (!_prefixIndex.TryGetValue(mollendorffPrefix, out var ids)) return result;
            int max = Math.Min(32, ids.Count);
            for (int i = 0; i < max; i++)
            {
                int idx = ids[i];
                if (idx >= 0 && idx < _lemmas.Count) result.Add(_lemmas[idx]);
            }
            return result;
        }

        /// <summary>
        /// Look up lemmas by Manchu SCRIPT (a code-point string,
        /// e.g. the engine's composed text). The dictionary transliterates the code points to ITS OWN
        /// index key via the romanization it declares (its key romanization, loaded in <see cref="LoadKeyRomanizer"/>)
        /// and then prefix-matches. Callers (any input scheme) pass code points and stay
        /// romanization-agnostic. Returns empty if no key romanizer was resolved.
        /// </summary>
        public IReadOnlyList<LemmaEntry> LookupByScript(string codepoints)
        {
            if (!_loaded || _keyRomanizer == null || string.IsNullOrEmpty(codepoints)) return new List<LemmaEntry>();
            string query = _keyRomanizer.Transliterate(codepoints);
            var matches = LookupWordPrefix(query);
            // Attach per-unit romanization segments (digraph-aware) for the knowledge
            // window's per-row Latin annotation. Cached on the entry (deterministic from Word).
            foreach (var m in matches)
                if (m.RomanSegments == null) m.RomanSegments = _keyRomanizer.TransliterateSegments(m.Word);
            return matches;
        }
    }
}

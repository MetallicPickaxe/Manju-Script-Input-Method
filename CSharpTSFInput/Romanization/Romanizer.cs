using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace CSharpTSFInput.Romanization
{
    /// <summary>
    /// A data-driven romanizer: maps a Manchu-SCRIPT code-point
    /// string to a Latin form, using a mapping ASSET (YAML) rather than hardcoded tables. One
    /// <see cref="Romanizer"/> = one loaded mapping (e.g. Möllendorff, Abkai).
    ///
    /// This one mechanism serves two mappings (the model being that code points are the core
    /// asset; a romanization is a code-point→Latin map):
    ///   • the display mapping: the on-screen romanization when a fixed display scheme is selected;
    ///   • the dictionary-key mapping: turning composed code points into a dictionary's own index key.
    /// Both load a <c>Resource/Romanization/&lt;name&gt;.yaml</c> (display schemes) or the romanization
    /// a dictionary declares it is keyed by. The engine/input layer carry NO romanization.
    ///
    /// Asset format (pure ASCII — code points as <c>U+XXXX</c>, joined with <c>+</c> for multi-code-point
    /// keys; invisible/format chars never authored as literal glyphs):
    /// <code>
    /// map:
    ///   "U+186E+U+185F": "ts"     # digraph (greedy longest-match wins over the singles)
    ///   "U+1820": "a"
    ///   "U+180C": ""              # FVS stripped
    /// </code>
    /// Transliterate does greedy longest-match over the keys; code points absent from the map pass
    /// through unchanged (identity).
    /// </summary>
    public sealed class Romanizer
    {
        private readonly Dictionary<string, string> _map;
        private readonly int _maxKeyLen;

        public string Name { get; }
        public bool IsEmpty => _map.Count == 0;

        private Romanizer(string name, Dictionary<string, string> map)
        {
            Name = name;
            _map = map;
            int max = 1;
            foreach (var k in map.Keys) if (k.Length > max) max = k.Length;
            _maxKeyLen = max;
        }

        /// <summary>Load a romanization asset YAML (via the shared Core MiniYamlReader). Returns a
        /// Romanizer with an empty map (identity) if the file is missing or has no <c>map:</c> node —
        /// the caller decides whether an empty romanizer is usable.</summary>
        public static Romanizer Load(string yamlPath, string name)
        {
            var map = new Dictionary<string, string>();
            try
            {
                if (System.IO.File.Exists(yamlPath))
                {
                    var root = CSharpTSFInput.Core.Configuration.MiniYamlReader.ParseFile(yamlPath);
                    CSharpTSFInput.Core.Configuration.MiniYamlReader.Node? mapNode = null;
                    root.Mapping?.TryGetValue("map", out mapNode);
                    if (mapNode?.Mapping != null)
                    {
                        foreach (var kv in mapNode.Mapping)
                        {
                            string? chars = KeyToChars(kv.Key);
                            if (chars == null) continue;
                            // Empty/quoted-empty scalar => strip (map to ""). Null scalar treated as "".
                            map[chars] = kv.Value.Scalar ?? string.Empty;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
            return new Romanizer(name, map);
        }

        /// <summary>Convert a "U+XXXX" / "U+XXXX+U+YYYY" key into its code-point string. Returns null
        /// if no valid code point is found (entry skipped).</summary>
        private static string? KeyToChars(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            var sb = new StringBuilder();
            foreach (var tokenRaw in key.Split('+'))
            {
                string token = tokenRaw.Trim();
                if (token.Length == 0) continue;
                if (token.Equals("U", StringComparison.OrdinalIgnoreCase)) continue;
                if (token.StartsWith("U+", StringComparison.OrdinalIgnoreCase)) token = token.Substring(2);
                if (int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)
                    && cp >= 0 && cp <= 0xFFFF)
                {
                    sb.Append((char)cp);
                }
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        /// <summary>Greedy longest-match transliteration of a code-point string. Unknown code points
        /// pass through unchanged.</summary>
        public string Transliterate(string script)
        {
            if (string.IsNullOrEmpty(script) || _map.Count == 0) return script ?? string.Empty;
            var sb = new StringBuilder(script.Length);
            int i = 0;
            while (i < script.Length)
            {
                bool matched = false;
                int maxLen = Math.Min(_maxKeyLen, script.Length - i);
                for (int len = maxLen; len >= 1; len--)
                {
                    if (_map.TryGetValue(script.Substring(i, len), out var rom))
                    {
                        sb.Append(rom);
                        i += len;
                        matched = true;
                        break;
                    }
                }
                if (!matched) { sb.Append(script[i]); i++; }
            }
            return sb.ToString();
        }

        /// <summary>Like <see cref="Transliterate"/> but returns each matched UNIT as its own segment
        /// together with the number of code points it consumed. Lets callers keep a multi-letter
        /// romanization (e.g. ng, dz, ū, ts) together and align it to the glyph(s)/unit it came from
        /// (used by the knowledge window's per-row Latin annotation). Unknown code points become
        /// 1-code-point identity segments; an empty mapping (e.g. a stripped FVS) yields a segment
        /// with empty Latin that still advances the code-point cursor.</summary>
        public List<(string latin, int cpLen)> TransliterateSegments(string script)
        {
            var segs = new List<(string, int)>();
            if (string.IsNullOrEmpty(script)) return segs;
            int i = 0;
            while (i < script.Length)
            {
                bool matched = false;
                int maxLen = Math.Min(Math.Max(_maxKeyLen, 1), script.Length - i);
                for (int len = maxLen; len >= 1; len--)
                {
                    if (_map.TryGetValue(script.Substring(i, len), out var rom))
                    {
                        segs.Add((rom, len));
                        i += len;
                        matched = true;
                        break;
                    }
                }
                if (!matched) { segs.Add((script[i].ToString(), 1)); i++; }
            }
            return segs;
        }
    }
}

using System.Collections.Generic;
using System.IO;

namespace CSharpTSFInput.Core.Configuration
{
    /// <summary>
    /// Minimal YAML subset reader for this layer: engine-agnostic, AOT-friendly, about 150 lines,
    /// with no reflection and no third-party dependency.
    ///
    /// Supports JUST what IME configuration needs:
    ///   - Top-level + nested mappings (key: value, key: { nested })
    ///   - String / integer / boolean scalars (quotes stripped if present)
    ///   - Lists of scalars (`- item`)
    ///   - `# comment` lines (and trailing-of-line comments)
    ///   - Indentation via spaces (tabs unsupported, as the YAML spec also forbids them)
    ///
    /// Does NOT support: multi-line/folded scalars, anchors/aliases/merge keys, flow style,
    /// mapping inside lists, date/timestamp/null special types.
    /// </summary>
    public sealed class MiniYamlReader
    {
        public sealed class Node
        {
            public string? Scalar;
            public Dictionary<string, Node>? Mapping;
            public List<string>? List;
        }

        public static Node Parse(string text)
        {
            var lines = text.Replace("\r\n", "\n").Split('\n');
            int idx = 0;
            return ParseMapping(lines, ref idx, baseIndent: -1);
        }

        public static Node ParseFile(string path)
        {
            return Parse(File.ReadAllText(path));
        }

        private static Node ParseMapping(string[] lines, ref int idx, int baseIndent)
        {
            var node = new Node { Mapping = new Dictionary<string, Node>() };
            while (idx < lines.Length)
            {
                string raw = lines[idx];
                string stripped = StripComment(raw);
                if (stripped.TrimEnd().Length == 0)
                {
                    idx++;
                    continue;
                }
                int indent = CountIndent(stripped);
                if (indent <= baseIndent)
                {
                    // Dedent: end of this mapping.
                    return node;
                }
                string content = stripped.Substring(indent);
                if (content.StartsWith("- "))
                {
                    // We landed on a list item but we're parsing a mapping — should be caller's job.
                    // Treat as end of mapping.
                    return node;
                }
                int colon = content.IndexOf(':');
                if (colon < 0)
                {
                    // Malformed line; skip.
                    idx++;
                    continue;
                }
                string key = content.Substring(0, colon).Trim();
                string valuePart = content.Substring(colon + 1).Trim();
                idx++;

                if (valuePart.Length > 0)
                {
                    // Inline scalar.
                    node.Mapping![key] = new Node { Scalar = UnquoteIfNeeded(valuePart) };
                    continue;
                }

                // Empty value → look ahead to determine child node type.
                int childIndent;
                bool nextIsList = PeekNextNonEmpty(lines, idx, out childIndent, out string? peekContent);
                if (peekContent == null)
                {
                    // Nothing to attach; leave as empty mapping.
                    node.Mapping![key] = new Node { Mapping = new Dictionary<string, Node>() };
                    continue;
                }
                if (childIndent <= indent)
                {
                    // Sibling / dedent — treat as empty node.
                    node.Mapping![key] = new Node { Mapping = new Dictionary<string, Node>() };
                    continue;
                }
                if (nextIsList)
                {
                    node.Mapping![key] = ParseList(lines, ref idx, indent);
                }
                else
                {
                    node.Mapping![key] = ParseMapping(lines, ref idx, indent);
                }
            }
            return node;
        }

        private static Node ParseList(string[] lines, ref int idx, int baseIndent)
        {
            var node = new Node { List = new List<string>() };
            while (idx < lines.Length)
            {
                string raw = lines[idx];
                string stripped = StripComment(raw);
                if (stripped.TrimEnd().Length == 0)
                {
                    idx++;
                    continue;
                }
                int indent = CountIndent(stripped);
                if (indent <= baseIndent) return node;
                string content = stripped.Substring(indent);
                if (!content.StartsWith("- ")) return node;
                node.List!.Add(UnquoteIfNeeded(content.Substring(2).Trim()));
                idx++;
            }
            return node;
        }

        private static bool PeekNextNonEmpty(string[] lines, int startIdx, out int indent, out string? content)
        {
            indent = -1;
            content = null;
            for (int i = startIdx; i < lines.Length; i++)
            {
                string stripped = StripComment(lines[i]);
                if (stripped.TrimEnd().Length == 0) continue;
                indent = CountIndent(stripped);
                content = stripped.Substring(indent);
                return content.StartsWith("- ");
            }
            return false;
        }

        private static string StripComment(string line)
        {
            // Strip `# ...` but NOT inside a quoted string. Our minimal subset doesn't use
            // quotes-with-hash, so the simple rule "first # not in quotes" is safe enough.
            bool inSingle = false, inDouble = false;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                else if (c == '#' && !inSingle && !inDouble) return line.Substring(0, i);
            }
            return line;
        }

        private static int CountIndent(string line)
        {
            int i = 0;
            while (i < line.Length && line[i] == ' ') i++;
            return i;
        }

        private static string UnquoteIfNeeded(string value)
        {
            if (value.Length >= 2)
            {
                if ((value[0] == '"' && value[value.Length - 1] == '"') ||
                    (value[0] == '\'' && value[value.Length - 1] == '\''))
                {
                    return value.Substring(1, value.Length - 2);
                }
            }
            return value;
        }
    }
}

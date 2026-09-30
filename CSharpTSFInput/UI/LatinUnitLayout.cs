using System;
using System.Collections.Generic;

namespace CSharpTSFInput.UI
{
    /// <summary>
    /// Shared per-UNIT Latin-column iteration for BOTH the candidate (main) window and
    /// the knowledge (dictionary) window, so a per-unit change (e.g. digraph-aware multi-letter
    /// rows) is made once for both.
    ///
    /// This owns ONLY the iteration: walking units with a code-point-span cursor (so a
    /// multi-code-point unit like ᡮᡟ advances the glyph row by 2), skipping empty segments, and
    /// letting the caller stop early (clip). It does NOT draw and does NOT decide the unit source:
    /// each caller supplies its own units (the main window's input display units vs the
    /// dictionary's own Möllendorff segments, two different romanizations by design) and its own
    /// draw call, so each window controls its own visual.
    /// </summary>
    internal static class LatinUnitLayout
    {
        /// <summary>Walk <paramref name="units"/> (each: a Latin segment + the code-point count it
        /// spans) over up to <paramref name="maxRows"/> glyph rows. For each non-empty segment, invoke
        /// <paramref name="drawAtRow"/>(segment, rowIndex); the glyph-row cursor then advances by the
        /// unit's code-point span. <paramref name="drawAtRow"/> returns false to stop early (e.g. once
        /// a row falls below the visible body).</summary>
        public static void ForEachUnit(
            IReadOnlyList<(string text, int cpLen)> units,
            int maxRows,
            Func<string, int, bool> drawAtRow)
        {
            if (units == null || drawAtRow == null) return;
            int row = 0;
            foreach (var (text, cpLen) in units)
            {
                if (row >= maxRows) break;
                if (!string.IsNullOrEmpty(text))
                {
                    if (!drawAtRow(text, row)) break;
                }
                row += Math.Max(1, cpLen);
            }
        }
    }
}

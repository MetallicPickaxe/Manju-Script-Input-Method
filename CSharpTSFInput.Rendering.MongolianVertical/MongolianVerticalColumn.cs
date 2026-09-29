using System;
using System.Collections.Generic;

namespace CSharpTSFInput.Rendering.MongolianVertical
{
    /// <summary>
    /// Shared vertical-column rendering primitives for Manchu script display.
    ///
    /// Design intent:
    ///   - This library is a thin wrapper around the host's DirectWrite + HarfBuzz-shaped glyph
    ///     pipeline. It does NOT own the shaper, font face, or render target — callers pass in
    ///     already-shaped glyph sequences and DirectWrite handles.
    ///   - It encapsulates the LAYOUT math + per-glyph baseline positioning used by both the
    ///     main IME candidate window and the floating Knowledge Window.
    ///   - Consumers render Manchu vertically. This library is named ".MongolianVertical" to make
    ///     the orientation explicit at the namespace level
    ///
    /// Public surface (minimum):
    ///   - <see cref="MongolianVerticalColumn.ComputeRowYCenters"/> — pure-math API to figure
    ///     out where each glyph's visual center lands within a vertical column.
    ///
    /// What is NOT in this library:
    ///   - Window / HWND lifecycle (consumers own that)
    ///   - DirectWrite / D2D resource creation (consumers own that)
    ///   - HarfBuzz shaping (lives in CSharpTSFInput.ManjuShaper; keeping it out of this library
    ///     lets a consumer use a different shaper backend)
    ///   - Selection / focus state (concern of consumer's UI layer)
    /// </summary>
    public static class MongolianVerticalColumn
    {
        /// <summary>
        /// Compute per-glyph vertical centers within a column, given a starting Y baseline and
        /// per-glyph Y advances (in design units, scaled by fontSize / upem).
        ///
        /// This is the pure-math kernel (no DirectWrite calls), so it is reusable by ANY consumer
        /// that needs to know "where does glyph N land in a vertical column starting at Y=baseY".
        /// </summary>
        /// <param name="yAdvancesScaled">Per-glyph Y advance in DIP (already scaled).</param>
        /// <param name="baseY">Starting Y coordinate for glyph 0.</param>
        /// <returns>List of Y centers, one per glyph, in DIP.</returns>
        public static List<float> ComputeRowYCenters(IReadOnlyList<float> yAdvancesScaled, float baseY)
        {
            var centers = new List<float>(yAdvancesScaled?.Count ?? 0);
            if (yAdvancesScaled == null) return centers;
            float cursorY = baseY;
            for (int i = 0; i < yAdvancesScaled.Count; i++)
            {
                float adv = Math.Abs(yAdvancesScaled[i]);
                centers.Add(cursorY + adv / 2f);
                cursorY += adv;
            }
            return centers;
        }

    }

}

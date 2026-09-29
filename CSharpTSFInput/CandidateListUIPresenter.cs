using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CSharpTSFInput.ManjuShaper;

namespace CSharpTSFInput
{
    public partial class CandidateListUIPresenter : IDisposable
    {
        private nint _hwnd = 0;
        // Public accessor so TextService can query window rect for KW
        // adaptive positioning (the knowledge window picks a screen quadrant that
        // does not overlap the text).
        public nint Hwnd => _hwnd;
        private List<string> _candidates = new List<string>();
        private int _selectedIndex = 0;
        private readonly string _className;
        // For the shared UnmanagedCallersOnly window procedure
        private GCHandle _selfHandle;
        private static nint _sharedWndProcPtr;
        
        private readonly ManjuEngine _engine;
        private ushort _classAtom = 0;
        private bool _disposed = false;

        private NativeMethods.ID2D1Factory? _d2dFactory;
        // Typed as the base ID2D1RenderTarget, not ID2D1HwndRenderTarget, so the paint path can draw
        // into any render target. The window assigns a real HwndRenderTarget here (see
        // EnsureResources). HwndRenderTarget-only calls (Resize) are guarded with a runtime cast.
        private NativeMethods.ID2D1RenderTarget? _renderTarget;
        private NativeMethods.ID2D1SolidColorBrush? _textBrush;
        private NativeMethods.ID2D1SolidColorBrush? _borderBrush;
        private NativeMethods.ID2D1SolidColorBrush? _highlightBrush;
        private nint _nativeTextBrush; // Native pointers
        private nint _nativeBorderBrush;
        private nint _nativeHighlightBrush;          // row band (Layer 1, dominant)
        // Highlight hierarchy brushes:
        //   _selectedCardHighlightBrush — Layer 2: candidate card selected by ←→ navigation
        //   _innerGlyphHighlightBrush  — Layer 3a: NARROW VERTICAL GRID LINE drawn to the left
        //                                 of the focus glyph (a box cannot bound the ink
        //                                 outline of a variable-advance Mongolian glyph)
        //   _focusGlyphOverlayBrush    — Layer 3b: solid color used to RECOLOR the focus
        //                                 glyph itself via a second DrawShapedText pass over the
        //                                 single focus glyph (so users see the glyph as "tinted")
        // The three highlight colours must differ visibly without becoming a palette.
        private NativeMethods.ID2D1SolidColorBrush? _selectedCardHighlightBrush;
        private nint _nativeSelectedCardHighlightBrush;
        private NativeMethods.ID2D1SolidColorBrush? _innerGlyphHighlightBrush;
        private nint _nativeInnerGlyphHighlightBrush;
        private NativeMethods.ID2D1SolidColorBrush? _focusGlyphOverlayBrush;
        private nint _nativeFocusGlyphOverlayBrush;
        // Translucent grey overlay drawn over the candidate region when the
        // floating Knowledge Window has focus context. The other side dims to a medium grey.
        // Half-transparent neutral grey — strong enough to dim, light enough to keep text readable.
        private NativeMethods.ID2D1SolidColorBrush? _unfocusedOverlayBrush;
        private nint _nativeUnfocusedOverlayBrush;
        // Row 3 (Unicode ground truth) is de-emphasised: brush = theme Text at 55% alpha.
        // Row 2 draws at full strength, so row 3 reads as supplementary.
        private NativeMethods.ID2D1SolidColorBrush? _metaDimBrush;
        private nint _nativeMetaDimBrush;
        private static D2D1_COLOR_F DimOf(D2D1_COLOR_F c) => new D2D1_COLOR_F { r = c.r, g = c.g, b = c.b, a = 0.55f };
        // Warm amber ribbon brush for first-column indicator when body wraps.
        private NativeMethods.ID2D1SolidColorBrush? _ribbonBrush;
        private nint _nativeRibbonBrush;
        // The settings overlay's backing panel: the theme's own background, forced OPAQUE.
        // It cannot reuse a brush: every other brush in this list is either translucent or a content
        // colour, and the whole point of the panel is that nothing underneath it survives. Alpha is
        // forced to 1 here rather than trusted from the palette, because "the table happens to use 1
        // today" is not a property the panel can rest on.
        private NativeMethods.ID2D1SolidColorBrush? _settingsPanelBrush;
        private nint _nativeSettingsPanelBrush;

        // ══════════════════════════════════════════════════════════════════════════════════════════
        // WHICH RENDER TARGET THESE BRUSHES BELONG TO — identity, not presence.
        //
        // Every brush above is a DEVICE-DEPENDENT resource: it is created BY a render target and is
        // only meaningful with that one. A guard that asks only whether they EXIST
        // (`_renderTarget != null && _fontFace != null`) cannot answer "is it the same one":
        // a presence check cannot express identity, just as a bool lifecycle flag cannot.
        //
        // Recording the target (and the theme the colours came from) answers both questions at once:
        // the same target with the same theme reuses, anything else releases the old set FIRST and
        // then builds. Nothing is left to a finalizer, and nothing is reused across devices.
        // ══════════════════════════════════════════════════════════════════════════════════════════
        private NativeMethods.ID2D1RenderTarget? _brushTarget;
        private int _brushThemeIndex = -1;

        private static D2D1_COLOR_F OpaqueOf(D2D1_COLOR_F c) => new D2D1_COLOR_F { r = c.r, g = c.g, b = c.b, a = 1f };
        private NativeMethods.IDWriteFontFace? _fontFace;
        private nint _nativeFontFace; // Native pointer for FontFace

        
        private List<List<ManjuShaperCore.FinalGlyph>> _shapedCandidates = new List<List<ManjuShaperCore.FinalGlyph>>();
        private List<float> _candidateLengths = new List<float>();
        // Semantic candidate items from backend
        private List<Messaging.SemanticCandidateItem> _semanticItems = new List<Messaging.SemanticCandidateItem>();
        // Per-candidate codepoint slice at the focus position,
        // sourced verbatim from CandidateMatrix.Cells[focus][hl].CodepointSliceAtPosition via the
        // RenderFrame. Used by ExplicitPickAtFocus to pass the EXACT slice into Engine;
        // subtracting a shared prefix instead would break for FVS variants.
        private List<string> _slicesAtFocus = new List<string>();

        // WHOSE candidates are currently on screen. Set from frame.FocusResolved wherever the
        // candidate payload is adopted, and never derived a second time. -1 until a frame has been seen.
        private int _candidateTargetIndex = -1;

        /// <summary> Is <paramref name="now"/> exactly <paramref name="was"/> with ONE
        /// contiguous run inserted somewhere? Computed as: the common prefix and the common suffix
        /// together already account for the whole of the old string.
        ///
        /// This is what separates "the user put a separator in the middle of the word" from "the input was
        /// replaced". The first must not disturb the focus; the second still resets it. Deliberately
        /// strict: anything that also CHANGES a character fails both checks and still resets.</summary>
        internal static bool IsSingleContiguousInsertion(string was, string now)
        {
            was ??= string.Empty; now ??= string.Empty;
            if (was.Length == 0 || now.Length <= was.Length) return false;
            int p = 0;
            while (p < was.Length && was[p] == now[p]) p++;
            int sfx = 0;
            while (sfx < was.Length - p && was[was.Length - 1 - sfx] == now[now.Length - 1 - sfx]) sfx++;
            return p + sfx == was.Length;
        }


        /// <summary> THE INVARIANT, IN ONE PLACE.
        ///
        /// Whichever item's candidates the interface is currently showing and allowing a choice from, the result must be written back to that same target item.
        ///
        /// A pick writes back HERE, and the candidate list the user picked from belongs to
        /// _candidateTargetIndex, so that is the answer — not ResolveFocusIndex(), which is the SELECTION
        /// FOCUS and is a different state. The two differ when the candidate column follows the tail:
        /// after typing "VD" the column shows D's candidates, and a pick written to the selection focus
        /// (V) would put D's content on V. This method exists so there is exactly one expression of
        /// "which item is being decided" and every caller reads it.
        ///
        /// Falls back to the selection focus only when no frame has been adopted yet (nothing is on screen
        /// to have picked from), and is clamped because a frame can be one keystroke behind the row count.</summary>
        private int ResolveWritebackTarget()
        {
            int rowCount = GetCurrentRowCount();
            if (rowCount <= 0) return -1;
            if (_candidateTargetIndex < 0) return ResolveFocusIndex(rowCount);
            return Math.Clamp(_candidateTargetIndex, 0, rowCount - 1);
        }

        // Per-card rectangles captured during paint, used by WM_LBUTTONDOWN
        // hit-test to map a click to a candidate index for ExplicitPickAtFocus. Each entry is
        // (left, top, right, bottom) in client coords, parallel to _candidates / _shapedCandidates.
        private List<(float L, float T, float R, float B)> _lastCardRects = new List<(float, float, float, float)>();
        private ushort _upem = 1000; // the font's UPEM, taken from RenderFrame

        // Backend-sourced raw input echo + focus state
        // _rawInput is refreshed from frame.RawInput on each HandleUpdateMessage; _focusedRowIndex
        // -1 sentinel = "follow last" (the default while typing).
        // ↑↓ outer + ←→ inner + 0-9 explicit pick are always live; no mode gates them.
        // The highlight band shows whenever focusIdx >= 0.
        private string _rawInput = "";
        private int _focusedRowIndex = -1;
        private int _lastLoggedSentinelResolve = int.MinValue;

        // Focus lives in two places that update at
        // different times: this field (synchronous, what the user sees) and the engine's
        // LastWorkerFocusResolved (written by the worker a frame or more later). Insertions read the
        // ENGINE's copy, so without this a separator / punctuation / Shift pressed right after an arrow
        // key would land on the previous unit. Every change to the live focus goes through here, publishing the value
        // to the engine synchronously so an insertion in the very next keystroke already sees it.
        private void SetFocusedRow(int value)
        {
            _focusedRowIndex = value;
            if (_engine != null) _engine.UiFocusAuthoritative = value;
        }

        // The Preview's internal scroll offset (inside the prefix row area) is the first
        // visible row. Once the window height reaches the screen budget, rows that do not fit do not grow the window
        // or draw off the canvas; the Preview scrolls so the current item stays visible. An explicit focus
        // keeps the focused row visible; the sentinel (-1, nothing chosen yet, still typing) follows the last row so new input is always visible. The offset is recomputed each frame in DrawFourColumnPrefix by ComputePrefixScrollOffset (smallest adjustment, no jumping) and stored back; hit-test mapping adds it again.
        private int _prefixScrollOffset = 0;

        // Touch pan-to-scroll (on a touchscreen). WM_GESTURE/GID_PAN drives the SAME
        // _prefixScrollOffset the auto-follow uses. While the user is panning (and until the next
        // focus/input change), _userScrollOverride suppresses the auto-follow target so the view doesn't
        // get yanked back to the focus/tail row mid-swipe. A tap is not handled here: unhandled gestures fall
        // through to DefWindowProc → the system synthesizes WM_LBUTTONDOWN → the JumpFocus path.
        private bool _userScrollOverride;
        private bool _gesturePanActive;
        private int _gestureLastYPhys;
        private float _gesturePanAccumDip;
        private int _lastVisibleRowsForScroll = 1;   // written each DrawFourColumnPrefix; read by the pan handler

        private int _lastViewportFirstVisible = -1;
        private int _lastViewportLastVisible = -1;
        private int _lastLatestInputUnit = -1;


        // The window grows with content until it meets the work-area budget, and then stops
        // growing and lets the internal viewport carry the rest. That transition is a real event that
        // must happen only ONCE, so it is counted, not inferred.
        private bool _heightAtBudget;

        // PURE manual-scroll math: apply a row delta to the current offset, clamped to the legal
        // range. Returns the clamped offset (rowCount/visibleRows guards mirror ComputePrefixScrollOffset).
        public static int ApplyManualScrollDelta(int currentOffset, int deltaRows, int rowCount, int visibleRows)
        {
            if (rowCount <= 0 || visibleRows <= 0) return 0;
            int maxOffset = Math.Max(0, rowCount - visibleRows);
            return Math.Clamp(currentOffset + deltaRows, 0, maxOffset);
        }

        // ── CANDIDATE HORIZONTAL VIEWPORT ─────────────────────────────────────
        //
        // The window is bounded to the work area, which keeps it on the screen and, in
        // the same stroke, caps what it can show at about sixteen cards: at 32 candidates 16 would be
        // unreachable, at 200 it would be 184. The answer is a horizontal viewport,
        // NOT multi-row wrapping, because a second row is bounded by the same work area and still leaves
        // a residue at 200, while a viewport satisfies "the active candidate is visible" at ANY length.
        //
        // The shape is deliberately the one the Preview grid already uses (_prefixScrollOffset +
        // ComputePrefixScrollOffset just below): an offset index, moved by the minimum needed to bring the
        // target inside, clamped, never wrapping. The one difference is that candidate cards have DIFFERENT
        // WIDTHS, so "how many fit" is not a constant and has to be measured from the widths themselves.
        //
        // This offset is a PAINT decision, not a layout one: the window's size comes from the sum of
        // all card widths bounded by the work area. Nothing here feeds
        // CalculateAndResizeWindow, so the rule that a value the layout consumes must be one the gate
        // approved — is not engaged, and the viewport offset deliberately does NOT enter LayoutSnapshot.
        private int _candidateViewportOffset;

        /// <summary> One card's place in the visible strip.</summary>
        public readonly record struct CandidateStripCard(int Index, float Left, float Right);

        /// <summary> What the strip shows this paint, and whether anything is off either end.</summary>
        public sealed class CandidateStrip
        {
            public int Offset;
            public List<CandidateStripCard> Cards = new();
            public bool MoreBefore;
            public bool MoreAfter;
            /// <summary>False only in the degenerate case where ONE card is wider than the whole strip;
            /// reported rather than hidden, because no amount of scrolling can fix it.</summary>
            public bool ActiveFullyVisible = true;
        }

        /// <summary> Does card <paramref name="k"/> fit ENTIRELY when the strip starts at
        /// <paramref name="from"/>? Fully, not partly: the active candidate must be completely
        /// visible, and a half-drawn card is not.</summary>
        private static bool CardFitsFrom(IReadOnlyList<float> cardWidths, float gap, float availableWidth, int from, int k)
        {
            float x = 0f;
            for (int j = from; j < k; j++) x += cardWidths[j] + gap;
            return x + cardWidths[k] <= availableWidth;
        }

        /// <summary> The furthest the strip may scroll: the smallest offset that still shows the
        /// LAST card whole. Scrolling past it would open empty space at the right for no reason, and it is
        /// what makes "no wrap-around at the ends" a clamp instead of a special case.</summary>
        private static int MaxCandidateViewportOffset(IReadOnlyList<float> cardWidths, float gap, float availableWidth)
        {
            int n = cardWidths.Count;
            if (n == 0) return 0;
            for (int off = 0; off < n; off++)
                if (CardFitsFrom(cardWidths, gap, availableWidth, off, n - 1)) return off;
            return n - 1;
        }

        /// <summary> PURE. The visible strip, from the card widths, the room there
        /// is, and where the focus is. Contracts, in the order they appear below:
        ///
        ///   move only when needed: an active card already whole inside the strip leaves the offset
        ///        untouched, so walking the focus across the visible cards does not re-lay-out anything;
        ///   linear: the offset moves by the minimum, forward or back; there is no row to turn on;
        ///   no wrap: clamped to [0, MaxCandidateViewportOffset]; the ends are ends;
        ///   the active card is FULLY inside, which is the whole reason this exists;
        ///   MoreBefore / MoreAfter carry what the indicators need.</summary>
        public static CandidateStrip ComputeCandidateStrip(
            IReadOnlyList<float> cardWidths, float gap, float startX, float availableWidth,
            int activeIndex, int currentOffset)
        {
            var strip = new CandidateStrip();
            int n = cardWidths?.Count ?? 0;
            if (n == 0 || availableWidth <= 0f) return strip;

            int active = Math.Clamp(activeIndex, 0, n - 1);
            int maxOff = MaxCandidateViewportOffset(cardWidths!, gap, availableWidth);
            int off = Math.Clamp(currentOffset, 0, maxOff);

            if (active < off) off = active;                       // the active card is behind the strip
            else while (off < active && !CardFitsFrom(cardWidths!, gap, availableWidth, off, active)) off++;
            off = Math.Clamp(off, 0, Math.Max(maxOff, active));

            strip.Offset = off;
            float x = startX;
            for (int i = off; i < n; i++)
            {
                float w = cardWidths![i];
                // Always emit the first one: if a single card is wider than the strip there is nothing to
                // scroll to, and drawing nothing would be worse than drawing it clipped.
                if (i > off && (x - startX) + w > availableWidth) break;
                strip.Cards.Add(new CandidateStripCard(i, x, x + w));
                x += w + gap;
            }
            strip.MoreBefore = off > 0;
            strip.MoreAfter = strip.Cards.Count > 0 && strip.Cards[^1].Index < n - 1;
            strip.ActiveFullyVisible = CardFitsFrom(cardWidths!, gap, availableWidth, off, active) && active >= off;
            return strip;
        }


        // PURE scroll-offset math. Minimal adjustment: keep the current
        // offset unless the target row is outside the visible window; clamp to [0, rowCount-visibleRows].
        public static int ComputePrefixScrollOffset(int currentOffset, int targetRow, int rowCount, int visibleRows)
        {
            if (rowCount <= 0 || visibleRows <= 0) return 0;
            int maxOffset = Math.Max(0, rowCount - visibleRows);
            int off = Math.Clamp(currentOffset, 0, maxOffset);
            if (targetRow >= 0)
            {
                if (targetRow < off) off = targetRow;                                  // target above window → scroll up to it
                else if (targetRow > off + visibleRows - 1) off = targetRow - visibleRows + 1; // below → scroll down
            }
            return Math.Clamp(off, 0, maxOffset);
        }

        // The colour themes. Switched at runtime via CycleTheme() using
        // ID2D1SolidColorBrush::SetColor IN-PLACE: no COM release/recreate, so no host-freeze risk
        // (the freeze hazard is brush lifetime; SetColor just sets a value). [0] = the default
        // scheme; 1..N = alternatives (the settings menu cycles them).
        private struct Theme
        {
            public D2D1_COLOR_F Bg, Text, Border, Band, Card, Grid, FocusGlyph, Unfocused, Ribbon;
            public string Name;
        }
        private static D2D1_COLOR_F TC(float r, float g, float b, float a = 1f) => new D2D1_COLOR_F { r = r, g = g, b = b, a = a };
        private static readonly Theme[] _themes = new Theme[]
        {
            // The colour values of so_young, google and android are read directly from
            // Weasel's weasel.yaml preset_color_schemes (converted BGR→RGB, never guessed from the name): so_young =
            // the Solarized family with a magenta/cyan accent; google = black on white + #3975CE/#1A73E8; android = a deep teal #1C7399 ground + yellow-green #A8C450.
            // Contrast ratios quoted in the comments below are computed by hand, against the theme's background.
            new Theme { Name="Windows 11", Bg=TC(.953f,.957f,.961f), Text=TC(.106f,.110f,.118f), Border=TC(.882f,.890f,.898f),
                Band=TC(.118f,.541f,.878f,.28f), Card=TC(.118f,.541f,.878f,.14f), Grid=TC(.118f,.541f,.878f,.55f),
                FocusGlyph=TC(0f,.373f,.722f,.95f), Unfocused=TC(.55f,.55f,.55f,.25f), Ribbon=TC(.118f,.541f,.878f) },     // Win11 light: bright blue band/grid; current letter in deep blue #005FB8
            new Theme { Name="So Young", Bg=TC(.992f,.965f,.890f), Text=TC(.396f,.482f,.514f), Border=TC(.933f,.910f,.835f),
                Band=TC(.165f,.631f,.596f,.25f), Card=TC(.165f,.631f,.596f,.11f), Grid=TC(.165f,.631f,.596f,.60f),
                FocusGlyph=TC(.827f,.212f,.510f,.98f), Unfocused=TC(.51f,.58f,.59f,.25f), Ribbon=TC(.165f,.631f,.596f) },  // Weasel's so_young: Solarized base3 ground and base00 text, a **cyan #2AA198** accent (band/grid/ribbon) plus a **magenta #D33682** focus (so_young's main colour, Foc/Bg 4.24), a wholly different character from Court's warm gold and vermilion
            new Theme { Name="Solarized Dark", Bg=TC(0f,.169f,.212f), Text=TC(.514f,.580f,.588f), Border=TC(.027f,.212f,.259f),
                Band=TC(.149f,.545f,.824f,.55f), Card=TC(.149f,.545f,.824f,.26f), Grid=TC(.149f,.545f,.824f,1f),
                FocusGlyph=TC(.149f,.545f,.824f,1f), Unfocused=TC(.35f,.43f,.44f,.30f), Ribbon=TC(.149f,.545f,.824f) },  // canonical Solarized blue #268BD2
            // Court: a warm gold #F7ECCF ground. The border is a quiet pale gold-brown and is outline
            // only, so it does not compete: a saturated deep blue such as cloisonné blue #2E59A7 as the whole window's
            // border colour carries enormous visual weight on a pale gold ground and reads as heavy and stiff.
            // Vermilion carries the focus: the band is **vermilion #C03A00 α.20** (composited #ECC8A6), in the same
            // family as the Card and FocusGlyph. A cloisonné blue band at α .28 would dilute to grey over this ground
            // (.28*(46,89,167)+.72*(247,236,207) ≈ #BFC3C4). The grid lines are gold, and cloisonné blue appears
            // only in the ribbon, as a secondary-column accent.
            new Theme { Name="Court", Bg=TC(.969f,.925f,.812f), Text=TC(.165f,.122f,.071f), Border=TC(.851f,.769f,.604f),
                Band=TC(.753f,.227f,0f,.20f), Card=TC(.753f,.227f,0f,.11f), Grid=TC(.831f,.627f,.090f,.90f),
                FocusGlyph=TC(.753f,.227f,0f,.95f), Unfocused=TC(.45f,.40f,.30f,.25f), Ribbon=TC(.180f,.349f,.655f) },
            new Theme { Name="High Contrast", Bg=TC(1f,1f,1f), Text=TC(0f,0f,0f), Border=TC(0f,0f,0f),
                Band=TC(0f,.298f,.937f,.55f), Card=TC(0f,.298f,.937f,.28f), Grid=TC(0f,0f,0f,.90f),
                FocusGlyph=TC(0f,.235f,.882f,1f), Unfocused=TC(.40f,.40f,.40f,.50f), Ribbon=TC(0f,.298f,.937f) },           // high contrast
            // Bean green: **a pale green ground plus a non-green foreground accent** (blue / purple / pink / orange),
            //       the structure that VS Code, IntelliJ, a palette editor and Eclipse share. The ground is what rests the eye, the accent is what carries the depth;
            //       if every layer sat on the same yellow-green hue, nothing could stand out and the window would read as one sheet of green.
            // The restful green ground keeps a low saturation (10%); the accent is a **navy blue**, near the complement of
            //       yellow-green; the border is a quiet grey-green outline.
            // Values / where they are used / contrast (computed by hand, against the background):
            //   Bg         #CFE7D2  window ground             —
            //   Text       #383D3A  body / candidate text     8.3:1  AAA
            //   Border     #9AB3A2  window + card border (a quiet outline) 1.6:1  outline only, carries no text
            //   Accent     #3E6D9C  focus band / selected card / grid lines / ribbon   4.2:1  AA
            //   FocusGlyph #1F4E79  current character (darkest, it needs the most legibility) 6.5:1  AA+
            new Theme { Name="Bean Green", Bg=TC(.812f,.906f,.824f), Text=TC(.220f,.239f,.227f), Border=TC(.604f,.702f,.635f),
                Band=TC(.243f,.427f,.612f,.30f), Card=TC(.243f,.427f,.612f,.12f), Grid=TC(.243f,.427f,.612f,.60f),
                FocusGlyph=TC(.122f,.306f,.475f,.98f), Unfocused=TC(.42f,.46f,.44f,.25f), Ribbon=TC(.243f,.427f,.612f) },

            new Theme { Name="Google", Bg=TC(1f,1f,1f), Text=TC(.125f,.129f,.141f), Border=TC(.855f,.863f,.878f),
                Band=TC(.224f,.459f,.808f,.22f), Card=TC(.224f,.459f,.808f,.10f), Grid=TC(.224f,.459f,.808f,.55f),
                FocusGlyph=TC(.102f,.451f,.910f,1f), Unfocused=TC(.55f,.55f,.55f,.25f), Ribbon=TC(.224f,.459f,.808f) },  // Google's own colours (white #FFF ground + Google blue #3975CE/#1A73E8)
            new Theme { Name="Android", Bg=TC(.110f,.451f,.600f), Text=TC(1f,1f,1f), Border=TC(.086f,.376f,.498f),
                Band=TC(.659f,.769f,.314f,.40f), Card=TC(.659f,.769f,.314f,.20f), Grid=TC(.659f,.769f,.314f,.85f),
                FocusGlyph=TC(.776f,.886f,.396f,1f), Unfocused=TC(0f,0f,0f,.30f), Ribbon=TC(.208f,.533f,.757f) },  // Weasel's android: a **bright deep-teal #1C7399 ground** (not darkened to black) + white text (5.3) + a yellow-green accent #A8C450; the focus brightens to #C6E265 (3.66); ribbon = the label blue #3588C1
        };
        private int _themeIndex = 0;
        private D2D1_COLOR_F _bgColor = new D2D1_COLOR_F { r = 1, g = 1, b = 1, a = 1 };
        // Per-UNIT prefix grid (digraph-aware). _latinUnits[i] = canonical
        // Möllendorff display form of unit i (e.g. "ng", "ž", "c" — NOT the raw per-char echo);
        // _unitStarts[i] = the unit's first char offset in _rawInput. Row indices everywhere in the
        // prefix grid (focus, band, clicks, EnqueueFocusChange, MarkPositionSelected) are UNIT indices —
        // matching the engine's selection/candidate model (BuildCommitString iterates units).
        // LAZILY derived from _rawInput (cache keyed by the string) so EVERY way _rawInput changes
        // (a frame refresh or a direct assignment) yields a consistent unit view.
        private List<string> _latinUnits = new List<string>();
        private List<int> _unitStarts = new List<int>();
        private string? _unitsCacheKey = null;

        // Separators render as their OWN centered rows. The prefix grid
        // therefore has DISPLAY SLOTS = unit rows + separator rows. To keep _focusedRowIndex == engine
        // UNIT index (an invariant: pick / commit / EnqueueFocusChange all key on units), the extra rows
        // live ONLY in these presenter-side maps, consulted for rendering + hit-test:
        //   _displaySlotUnit[slot]   = engine unit index at that slot, or -1 for a separator slot
        //   _displaySlotSep[slot]    = the separator glyph string for a separator slot ("" for unit slots)
        //   _unitToSlot[unitIndex]   = the display slot a given engine unit renders at
        // Total slot count = _displaySlotUnit.Count; unit count stays _latinUnits.Count (GetCurrentRowCount).
        private List<int> _displaySlotUnit = new List<int>();
        private List<string> _displaySlotSep = new List<string>();
        private List<int> _unitToSlot = new List<int>();

        // Invalidate the Latin-unit cache and repaint. Called when a settings menu row changes a display
        // glyph: _rawInput is unchanged so EnsureUnitsCache would otherwise cache-hit and
        // keep the old glyph; force a recompute + repaint so the new glyph shows immediately.
        public void RefreshUnitDisplay()
        {
            _unitsCacheKey = null;
            if (_hwnd != 0) NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
        }

        private void EnsureUnitsCache()
        {
            string raw = _rawInput ?? string.Empty;
            if (_unitsCacheKey == raw) return;

            // Build the display-row model (unit rows + standalone separator rows) and
            // derive both the unit-keyed arrays and the slot↔unit maps from it.
            var rows = _engine.GetPrefixDisplayRows(raw);
            var newLatin = new List<string>();
            var newStarts = new List<int>();
            var slotUnit = new List<int>(rows.Count);
            var slotSep = new List<string>(rows.Count);
            var unitToSlot = new List<int>();
            for (int slot = 0; slot < rows.Count; slot++)
            {
                var r = rows[slot];
                if (r.IsSeparator)
                {
                    slotUnit.Add(-1);
                    slotSep.Add(r.Display);
                }
                else
                {
                    int unitIndex = newLatin.Count;   // = r.UnitIndex, in order
                    newLatin.Add(r.Display);
                    newStarts.Add(r.SnapshotStart);
                    unitToSlot.Add(slot);
                    slotUnit.Add(unitIndex);
                    slotSep.Add(string.Empty);
                }
            }
            _latinUnits = newLatin;
            _unitStarts = newStarts;
            _displaySlotUnit = slotUnit;
            _displaySlotSep = slotSep;
            _unitToSlot = unitToSlot;
            _unitsCacheKey = raw;
        }

        // Display-slot helpers (unit index → slot, and total slot count). Fall back to identity /
        // unit-count when the maps are not built yet (defensive), so callers never index out of range.
        private int SlotOfUnit(int unitIndex)
        {
            if (unitIndex >= 0 && unitIndex < _unitToSlot.Count) return _unitToSlot[unitIndex];
            return unitIndex;   // identity fallback (no separators)
        }
        private int DisplaySlotCount => _displaySlotUnit.Count > 0 ? _displaySlotUnit.Count : _latinUnits.Count;

        // After variant pick, hide candidate cards for one paint cycle to produce a
        // visual flash, because without one there is no way to tell that it refreshed. Decremented in
        // HandlePaint; while > 0, candidate-card draw loop is skipped (Latin/Index/Mongolian columns
        // still render so user keeps spatial context).
        private int _candidatesFlashHideTicks = 0;

        // Set by ExplicitPickAtFocus when picking at the LAST outer position.
        // Consumed by TextService.OnKeyDown via TakePendingAutoCommit() to trigger commit-to-host
        // immediately after the navigation key was handled.
        private bool _pendingAutoCommit = false;

        public bool TakePendingAutoCommit()
        {
            if (!_pendingAutoCommit) return false;
            _pendingAutoCommit = false;
            return true;
        }
        private int _activeFrameId;

        private const float FontSize = 32.0f;
        private const float Padding = 10.0f;
        // Unified geometry contract:
        // - one readable base card width plus one bounded local increment
        // - one pure window-width derivation from active columns and card widths
        // - one single-glyph readable body height plus one per-glyph increment
        private const float ReadableCardBodyWidthMultiplier = 2.75f;
        private const float CardWidthIncrementQuantumMultiplier = 0.0625f;
        private const float CardWidthBodyGainMultiplier = 0.5f;
        private const float CardWidthMetadataGainMultiplier = 0.5f;
        private const float MaximumCardWidthIncrementMultiplier = 1.25f;
        private const int DefaultVisibleRowCountWhenScreenUnknown = 3;
        private const float SingleGlyphBodyHeightMultiplier = 3.5f;
        // Supporting (metadata) row configuration
        private const float MetadataFontSize = 10.0f;
        // Prefix Latin/digit/apostrophe = larger + bold, so a learner can read them easily.
        private const float PrefixLatinFontSize = 13.0f;
        private const float MetadataRowHeight = 14.0f;
        private const float MetadataRowSpacing = 2.0f;

        // Row-3 detailed Unicode (U+xxxx sequence) is an ADVANCED / debug affordance.
        // Read once, lazily, from the engine's config store. Draw-only gate (card height unchanged): the row
        // stays reserved so there is no card re-layout/misalignment risk.
        private bool? _showDetailedUnicodeCache;
        private bool ShowDetailedUnicodeInfo()
        {
            if (_showDetailedUnicodeCache.HasValue) return _showDetailedUnicodeCache.Value;
            bool v = false;
            try { v = _engine?.GetConfigStore()?.GetBool("appearance.show_detailed_unicode_information", false) ?? false; } catch { }
            _showDetailedUnicodeCache = v;
            return v;
        }
        // Several close light palettes (e.g. Google and so_young) are hard to tell apart → the ACTIVE
        // theme name can be shown in a corner, so it is clear which is on while cycling themes. Gated by
        // `appearance.show_theme_name`, default FALSE: a release UI carries no theme label.
        //
        // Setting `appearance.show_theme_name: true` shows the label, which helps when cycling close
        // light palettes.
        //
        // BOTH DEFAULTS ARE FALSE, and that pairing is the point: the yaml value AND this fallback. A false
        // default in the yaml alone would leave a config that is missing the key (an older user file, a
        // partial copy) still drawing the label, which is the shape of defect where "the default is off" is
        // true of one code path and false of the one the user is on.
        // How long the switch toast stays on screen.
        private const long SettingsOverlayMs = 4000;

        // ══════════════════════════════════════════════════════════════════════════════════════════
        // THE SWITCH TOAST.
        //
        // It is drawn by DrawPanel below, the routine that also draws the settings menu: same panel
        // geometry, same theme colours. That panel is legible in every theme with zero bleed-through from
        // the candidates underneath.
        //
        // NEVER RESIDENT. The theme label is off by default; a toast that
        // outlived its window would put a resident label on screen by a side door.
        // ══════════════════════════════════════════════════════════════════════════════════════════
        private string? _toastLine;
        private long _toastUntilTick;

        /// <summary> Show one line for a few seconds (<see cref="SettingsOverlayMs"/>).
        ///
        /// NO WINDOW ⇒ NO TOAST, and that guard is required, not tidiness. A toast is a thing
        /// on the SCREEN; a presenter with no HWND has no screen to put it on.</summary>
        public void ShowSwitchToast(string line)
        {
            if (_hwnd == 0)
            {
                return;
            }
            _toastLine = line;
            _toastUntilTick = Environment.TickCount64 + SettingsOverlayMs;
            NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
        }


        /// <summary> Is the toast currently drawable?</summary>
        public bool ToastWouldDraw()
            => _toastLine != null
               && Environment.TickCount64 < _toastUntilTick;


        /// <summary> Draw one toast line on the shared panel, with this window's brushes.</summary>
        private void DrawOverlayPanel(IReadOnlyList<string> lines, float width)
        {
            if (_renderTarget == null) return;
            var (panel, edge, soBrush) = OverlayBrushes();
            if (soBrush == 0) return;
            DrawPanel(_renderTarget, lines, width, panel, edge, soBrush, -1, 0);
        }

        /// <summary> The brushes the toast is drawn with: the panel fill, the edge and the text.</summary>
        private (nint Panel, nint Edge, nint Text) OverlayBrushes()
            => (_nativeSettingsPanelBrush,
                _nativeFocusGlyphOverlayBrush != 0 ? _nativeFocusGlyphOverlayBrush : _nativeBorderBrush,
                _nativeTextBrush != 0 ? _nativeTextBrush : _nativeMetaDimBrush);


        /// <summary>
        /// Draw the backing panel and then the lines on it, into any render target. The switch toast on
        /// this window and the settings menu window both draw through here, with brushes in the active
        /// theme's colours.
        ///
        /// PANEL FIRST, TEXT SECOND: without the fill, every glyph would land on top of whatever the
        /// window already shows. The edge is the theme's accent, the one colour each palette is built to
        /// make stand out against its own background, so the panel reads as a layer in every theme.
        /// When <paramref name="highlightRow"/> is 0 or more, that line gets a band in
        /// <paramref name="highlightBrush"/> behind it.
        /// </summary>
        internal static void DrawPanel(NativeMethods.ID2D1RenderTarget rt, IReadOnlyList<string> lines, float width,
            nint panelBrush, nint edgeBrush, nint textBrush, int highlightRow, nint highlightBrush)
        {
            if (textBrush == 0) return;
            var (pl, pt, pr, pb) = ComputeSettingsPanelRect(lines, width);
            if (panelBrush != 0)
            {
                RECT_F panel = new RECT_F { left = pl, top = pt, right = pr, bottom = pb };
                rt.FillRectangle(panel, panelBrush);
                if (edgeBrush != 0)
                {
                    RECT_F edgeRect = new RECT_F { left = pl + 0.5f, top = pt + 0.5f, right = pr - 0.5f, bottom = pb - 0.5f };
                    rt.DrawRectangle(edgeRect, edgeBrush, 1.5f, (nint)0);
                }
            }

            float textWidth = Math.Max(1f, (pr - pl) - 2f * SettingsPanelInsetX);
            float column = FirstColumnWidth(lines);
            float x = pl + SettingsPanelInsetX;
            float y = pt + SettingsPanelInsetY;
            // The text is clipped to the panel. A line longer than a clamped panel wraps, and without the
            // clip its second line would land below the panel, on top of the candidates.
            rt.PushAxisAlignedClip(new RECT_F { left = pl, top = pt, right = pr, bottom = pb }, 1); // D2D1_ANTIALIAS_MODE_PER_PRIMITIVE = 1
            try
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    if (i == highlightRow && highlightBrush != 0)
                    {
                        RECT_F band = new RECT_F { left = pl + 2f, top = y, right = pr - 2f, bottom = y + SettingsOverlayLineHeight };
                        rt.FillRectangle(band, highlightBrush);
                    }
                    int tab = lines[i].IndexOf('\t');
                    if (tab < 0)
                    {
                        ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                            rt, lines[i], x, y, textWidth, SettingsOverlayLineHeight, SettingsOverlayFontSize, textBrush);
                    }
                    else
                    {
                        ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                            rt, lines[i].Substring(0, tab), x, y, Math.Min(column, textWidth),
                            SettingsOverlayLineHeight, SettingsOverlayFontSize, textBrush);
                        ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                            rt, lines[i].Substring(tab + 1), x + column, y, Math.Max(1f, textWidth - column),
                            SettingsOverlayLineHeight, SettingsOverlayFontSize, textBrush);
                    }
                    y += SettingsOverlayLineHeight;
                }
            }
            finally { rt.PopAxisAlignedClip(); }
        }

        /// <summary> Lines may hold two columns separated by a tab. The second column starts this far
        /// from the first: the widest text before a tab, plus <see cref="SettingsPanelColumnGap"/>. 0 when
        /// no line has a tab.</summary>
        private static float FirstColumnWidth(IReadOnlyList<string> lines)
        {
            float widest = 0f;
            foreach (string line in lines)
            {
                int tab = line.IndexOf('\t');
                if (tab >= 0) widest = Math.Max(widest, EstimateMetadataTextWidth(line.Substring(0, tab), SettingsOverlayFontSize));
            }
            return widest > 0f ? widest + SettingsPanelColumnGap : 0f;
        }

        // ══════════════════════════════════════════════════════════════════════════════════════════
        // THE BACKING PANEL.
        //
        // Drawn straight onto the window, the toast would leave the candidate cards and the prefix grid
        // visible underneath, with the two sets of glyphs on top of each other. That is legibility, not taste.
        //
        // NO HARD-CODED COLOURS AND NO HARD-CODED SIZE.
        //   colour — fill is the theme's own background forced opaque, the edge is the theme's accent,
        //            the text is the theme's text colour. Those three are a contrast pair the palette
        //            already guarantees, in all eight themes, without this code choosing anything.
        //   size   — measured from the lines. The menu is 11 rows and a scheme name can
        //            grow, so a pixel constant written today is a clipped row later.
        // ══════════════════════════════════════════════════════════════════════════════════════════
        private const float SettingsOverlayFontSize = 10f;
        private const float SettingsOverlayLineHeight = 14f;
        private const float SettingsPanelInsetX = 7f;
        private const float SettingsPanelInsetY = 5f;
        private const float SettingsPanelColumnGap = 12f;

        /// <summary>
        /// Where the panel goes and how big it is, measured from the text it must cover.
        /// ONE definition, used by the paint and by <see cref="SettingsPanelRect"/>, so whatever
        /// measures the panel measures the rectangle the paint uses.
        /// </summary>
        internal static (float left, float top, float right, float bottom) ComputeSettingsPanelRect(
            IReadOnlyList<string> lines, float windowWidth)
        {
            float column = FirstColumnWidth(lines);
            float widest = 0f;
            foreach (string line in lines)
            {
                int tab = line.IndexOf('\t');
                float w = tab < 0
                    ? EstimateMetadataTextWidth(line, SettingsOverlayFontSize)
                    : column + EstimateMetadataTextWidth(line.Substring(tab + 1), SettingsOverlayFontSize);
                if (w > widest) widest = w;
            }
            float left = Padding;
            float top = Padding;
            float right = left + widest + 2f * SettingsPanelInsetX;
            float bottom = top + lines.Count * SettingsOverlayLineHeight + 2f * SettingsPanelInsetY;

            // A panel wider than the window would be clipped by D2D and the text with it.
            float maxRight = windowWidth - Padding;
            if (maxRight > left + 2f * SettingsPanelInsetX && right > maxRight) right = maxRight;
            return (left, top, right, bottom);
        }

        /// <summary> The panel rectangle, from the same computation the paint uses.</summary>
        public static (float left, float top, float right, float bottom) SettingsPanelRect(
            IReadOnlyList<string> lines, float windowWidth)
            => ComputeSettingsPanelRect(lines, windowWidth);

        /// <summary> The size, in DIPs, of a window that holds the panel for <paramref name="lines"/>
        /// with the standard padding on every side.</summary>
        internal static (float Width, float Height) PanelWindowSize(IReadOnlyList<string> lines)
        {
            var (_, _, right, bottom) = ComputeSettingsPanelRect(lines, float.MaxValue);
            return (right + Padding, bottom + Padding);
        }

        /// <summary> The active theme's colours for a panel drawn in another window: the fill, the
        /// edge, the text and the highlight band, the same four the toast on this window uses.</summary>
        internal (D2D1_COLOR_F Panel, D2D1_COLOR_F Edge, D2D1_COLOR_F Text, D2D1_COLOR_F Highlight) PanelColors()
        {
            var th = _themes[(_themeIndex >= 0 && _themeIndex < _themes.Length) ? _themeIndex : 0];
            return (OpaqueOf(th.Bg), th.FocusGlyph, th.Text, th.Band);
        }

        /// <summary> The settings file was read again: the cached switches are read afresh at the next paint.</summary>
        private void OnConfigurationChanged(IReadOnlyList<string> keys)
        {
            _showDetailedUnicodeCache = null;
            _showThemeNameCache = null;
            if (_hwnd != 0) NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
        }

        private bool? _showThemeNameCache;
        private bool ShowThemeName()
        {
            if (_showThemeNameCache.HasValue) return _showThemeNameCache.Value;
            bool v = false;
            try { v = _engine?.GetConfigStore()?.GetBool("appearance.show_theme_name", false) ?? false; } catch { }
            _showThemeNameCache = v;
            return v;
        }
        // Wrap-within-card column layout constants:
        //   - Column gap narrow (3-5px): 4
        //   - First-column left ribbon (warm amber) acts as "this is the start column" indicator
        //   - Implicit ~100 glyph upper cap (no hard column-count limit)
        //   - Ribbon color chosen for contrast vs both white card bg and blue-ish highlight
        private const float ColumnGap = 4.0f;
        private const float RibbonWidth = 3.0f;
        private const int ImplicitMaxTotalGlyphs = 100;
        // Warm amber #D97E2E (217/255, 126/255, 46/255) = (0.851, 0.494, 0.180)
        private const float RibbonR = 217f / 255f;
        private const float RibbonG = 126f / 255f;
        private const float RibbonB = 46f / 255f;

        // Four-column composition UI prefix block:
        //   Index column (numeric label) | Latin column (raw input echo) | Mongolian column (shaped word preview)
        // These render BEFORE the candidate cards. Their pixel metrics are independent of the
        // card width chain. The window width is
        // additively extended by NewColumnTotalWidth via 2-stage resize in CalculateAndResizeWindow's
        // sibling logic — see EnsureFourColumnSizing().
        private const float IndexColumnWidth = 26f;   // 13px bold 1-2 digit fits this width (usable 22px)
        // 34 fits 3-char Möllendorff units ("ts'", "c'y") + diacritics at full
        // metadata font size (DrawMetadataText auto-shrinks past this width, but 34 avoids shrink).
        private const float LatinColumnWidth = 34f;   // 13px bold 1-2 char Latin fits this width (usable 30px); longer apostrophe strings auto-shrink
        private const float MongolianColumnWidth = 38f;
        // A separated-vs-fused comparison: a fused-form column to the RIGHT of the Mongolian (separated) column.
        // Renders the un-decomposed ligature glyph (the real font merge) ONLY at fusion heads, spanning the
        // component rows (e.g. bu over the b+u rows); non-fusing positions stay blank (the separated column already has them). NOTE: the
        // column is reserved only when the frame has ≥1 fusion (see PrefixRegionWidth), so no-fusion words
        // don't get an empty column, which would look odd.
        private const float HefiColumnWidth = 38f;
        private const float NewColumnGap = 8f;
        // Prefix width is DYNAMIC: the separated col4 (HefiColumnExtra) is reserved ONLY when the current word
        // has a real fusion; a word with none takes no column and leaves no gap. PrefixBaseWidth = Index|Latin|Mongolian (the fused main column, 4 gaps).
        private const float PrefixBaseWidth = IndexColumnWidth + LatinColumnWidth + MongolianColumnWidth + NewColumnGap * 4f;
        private const float HefiColumnExtra = HefiColumnWidth + NewColumnGap;   // the separated col4 + its gap
        private const float NewColumnTotalWidth = PrefixBaseWidth + HefiColumnExtra;  // MAX (with the separated col4); per-frame value = PrefixRegionWidth
        // Per-frame prefix region width — reserves the separated col4 ONLY when the word has ≥1 real canonical fusion
        // (a word with no fusion takes no column). Cached by _rawInput so the full-word FullShape runs once per frame.
        private string? _prefixFusionCacheInput = null;
        private bool _prefixFusionCacheHas = false;
        private float PrefixRegionWidth
        {
            get
            {
                if (!string.Equals(_rawInput, _prefixFusionCacheInput, System.StringComparison.Ordinal))
                {
                    _prefixFusionCacheInput = _rawInput;
                    _prefixFusionCacheHas = HasAnyCanonicalFusion();
                }
                return PrefixBaseWidth + (_prefixFusionCacheHas ? HefiColumnExtra : 0f);
            }
        }
        private unsafe bool HasAnyCanonicalFusion()
        {
            string cps = _engine.GetTranslatedTextFromSnapshot(_rawInput ?? string.Empty);
            byte[]? fontData = _engine.GetFontData();
            if (string.IsNullOrEmpty(cps) || fontData == null || fontData.Length == 0) return false;
            List<ManjuShaperCore.FinalGlyph> canon;
            fixed (byte* pf = fontData) canon = ManjuShaperCore.FullShape(cps, pf, fontData.Length, isVertical: true);
            for (int ci = 0; ci < canon.Count; ci++)
            {
                int s = (int)canon[ci].Cluster;
                int e = (ci + 1 < canon.Count) ? (int)canon[ci + 1].Cluster : cps.Length;
                if (e - s >= 2) return true;
            }
            return false;
        }
        /// <summary> The preview grid's row pitch, 40 DIP.
        ///
        /// The pitch has to hold every SINGLE glyph. U+1861 (ᡡ, the `v` key) paints 38 px of
        /// ink, so a 32 pitch would cut 3 px off the top and 2 off the bottom. It is the only SINGLE glyph
        /// taller than 32; the other units that overflow 32 are fusion or empty-rime units, which legitimately
        /// occupy more than one row.
        ///
        /// A taller pitch is used instead of a smaller font: the grid SCROLLS, so rows lost to a taller
        /// pitch can be scrolled back to, while ink cut off cannot be recovered by any action the user can
        /// take. Correctness over density.
        ///
        /// Everything that depends on this reads it BY NAME: the gesture pan's rows-moved conversion,
        /// the click hit-test, the prefix height budget, the visible-row capacity, the row centres and the
        /// focus band. No grid arithmetic repeats the pitch as a literal.</summary>
        private const float NewColumnRowHeight = 40f;

        // Uniform vertical pitch (design units, upem-relative) for the separated
        // (decomposed-ligature) render path ONLY. Passed to DrawShapedText/MeasureShapedTextRowYCenters
        // for velar/ligature-override rows so the decomposed components stack with a STABLE, EVEN small gap
        // instead of per-glyph ink-width spacing (which varies the separated gap from row to row). Tuned
        // to ≈ a typical decomposed component's ink extent so each component sits visually centered on its
        // row. Natural fused / per-character / empty-rime rows do NOT use this (pitch=0 → ink-width behaviour).
        private const float FentiUniformPitchDesignUnits = 0f;   // 0 = off. In the real prefix Mongolian column each separated component is a SEPARATE single glyph on its own grid row → the spacing between components is the grid row pitch, which is even. A uniform pitch only moves the centring on a single-glyph row and never creates a gap. 0 = ink-width centring.
        // Per-letter INK-EDGE gap (design units) for a candidate card's body (a joined word). When >0, layout
        // follows each glyph's ink bounding box (LSB + width) so the gap between neighbouring ink edges stays constant; spacing by the advance alone does not account for the LSB bearing and gives an uneven pitch. A fused glyph counts as single and is not split. Default 0 = ink-width behaviour.
        public static float CardBodyInkEdgeGapDesignUnits = ManjuDirectWriteRenderer.InkEdgeGapDesignUnits;
        private const float NewColumnLatinFontSize = 16f;
        private const float NewColumnIndexFontSize = 11f;
        // Label cap, 32 characters. The cap counts characters, so at 24 the three commonest cards would lose their tail:
        //   variant FVS1 · final · U+1820   (29) → "variant FVS1 · final · U..."
        //   ligature g+i · isolated · U+1873 (32) → "ligature g+i · isolated ..."
        //   separated FVS1 · final · U+1873  (31) → "separated FVS1 · final ·..."
        // What would be cut is the U+XXXX of the last letter, and the card's row 3, which also carries the
        // codepoint, is gated OFF by default (appearance.show_detailed_unicode_information), so the truncation
        // would be real information loss.
        //
        // A cap of 32 costs ZERO card width. ComputeContentCardWidthMetrics
        // bounds growth by GetMaximumCardWidthIncrement (40 px at font size 32), and the labels
        // saturate it at cap 24 already: FinalCardWidth = 148 px either way.
        //
        // Overflow is handled downstream rather than here: DrawMetadataText shrinks the metadata font to
        // fit the card (floor MinMetadataFontSize = 7.0), so a longer string renders smaller, not outside
        // the card. 32 is the longest label the engine produces, and it lands above that
        // floor; a much larger cap would hit it and start overflowing, which is why this is 32 and not
        // "big enough for anything".
        private const int MaxSemanticNameDisplayChars = 32;
        private const float MinMetadataFontSize = 7.0f;
        private const int FrozenWidthLedgerTermCount = 51;
        private const string UnavailableOnLivePath = "UNAVAILABLE_ON_LIVE_PATH";
        private const string UnavailableOnComFailure = "UNAVAILABLE_ON_COM_FAILURE";
        private const string SnapshotCaptureOk = "OK";
        private static readonly Regex CodePointPattern = new(@"U\+[0-9A-Fa-f]{4,6}", RegexOptions.Compiled);
        private const int DefaultLocationX = 100;
        private const int DefaultLocationY = 100;
        private int _x = DefaultLocationX;
        private int _y = DefaultLocationY;
        private bool _dataDirty = false;
        private bool _layoutDirty = false;

        // Signature of everything that can change the WINDOW GEOMETRY.
        // Layout is recomputed only when this changes, so a repeated / retried update of identical
        // content does not trigger CalculateAndResizeWindow → SetWindowPos → visible flicker.
        // Deliberately excludes anything that only affects PAINT (colors, highlight tint): those still
        // repaint every message via _dataDirty / InvalidateRect.
        private int _lastLayoutSignature = int.MinValue;


        /// <summary>May this frame be put on screen?
        ///
        /// Two ways to qualify:
        ///   · it IS the current state;
        ///   · or it is strictly AHEAD of what is displayed and ON THE PATH to the current state.
        ///
        /// The second clause is what gives progressive display during a burst, and both halves of it
        /// are load-bearing. "Ahead of what is shown" keeps the display moving forward only, so nothing
        /// can roll back, which is the guarantee the whole signature set rests on. "On the path to live" keeps
        /// an abandoned edit from reappearing: after a deletion or a mid-word edit the live buffer no
        /// longer extends the old branch, and frames from it stop qualifying immediately.</summary>
        public static bool IsFrameShowable(string carried, string shown, string live)
        {
            carried ??= string.Empty; shown ??= string.Empty; live ??= string.Empty;
            if (string.Equals(carried, live, StringComparison.Ordinal)) return true;
            return carried.Length > shown.Length
                && carried.StartsWith(shown, StringComparison.Ordinal)
                && live.StartsWith(carried, StringComparison.Ordinal);
        }

        // The signature MUST cover every input CalculateAndResizeWindow reads,
        // or the gate suppresses a resize that was actually needed — trading visible flicker for a stale
        // window size, which is a worse bug. Besides raw input, candidate identity, focused row and
        // glyph COUNT, the real layout consumes
        // _candidateLengths, the shaped glyph geometry, _semanticItems and _upem (see
        // CalculateAndResizeWindow). Those are all folded in below.
        //
        // Bias is deliberately toward OVER-triggering: a false "changed" only costs one extra resize,
        // whereas a false "unchanged" leaves the window the wrong size with no way to recover until the
        // next genuine change.
        //
        // NOTE on hashing: SemanticCandidateItem is a plain class with no GetHashCode override, so hashing
        // the OBJECT would use reference identity: a fresh list every frame would then never compare
        // equal and the gate would silently degrade to always-resize. Its
        // layout-relevant FIELDS are hashed by value instead. String hashes are per-process randomized but
        // stable within a run, which is all this comparison needs.
        // A hash is not the gate: a digest can
        // collide, and folding more fields into a hash does not make it a comparison of the complete
        // layout input. The gate builds an IMMUTABLE, COMPLETE snapshot of every input
        // CalculateAndResizeWindow reads and compares it by VALUE. No collisions.
        // TRULY immutable: every field readonly, every array copied in at construction and
        // never handed out. Public writable fields and writable arrays would let a caller
        // mutate a captured "snapshot": that is a struct-of-live-references, not a
        // snapshot. PrefixRegionWidth, DisplaySlotCount, the real list counts,
        // FinalGlyph.Cluster and the client size are captured here too.
        internal sealed class LayoutSnapshot : IEquatable<LayoutSnapshot>
        {
            public readonly string RawInput;
            public readonly int FocusedRow, Upem, X, Y, ScreenW, ScreenH, ClientW, ClientH;
            /// <summary> The WORK AREA of the monitor this window sits on, in physical px:
            /// origin and size, taskbar and appbars already excluded.
            ///
            /// The window's POSITION is work-area aware:
            /// TextService.ComputeAnchoredWindowPosition flips and pins against rcWork. Its SIZE must be too.
            /// Unbounded, the width is the sum of the candidate cards (ComputeWindowWidthForCardWidths), and
            /// a height budget taken against SM_CYSCREEN is the primary monitor's FULL height,
            /// taskbar included. So a long list would make a window wider than the monitor, the position clamp
            /// would pin it to the left work edge, and everything past the right edge would be off-screen,
            /// while whatever falls outside the bounds must be neither visible nor operable.
            ///
            /// Carried in the snapshot instead of read at layout time, for the reason above: a value the
            /// layout consumes must be one the gate approved. It also makes moving to another monitor, or
            /// showing/hiding the taskbar, a layout change the gate can see.</summary>
            public readonly int WorkX, WorkY, WorkW, WorkH;
            public readonly int CandidateCount, LengthCount, ShapedCount, SemanticCount, SliceCount, DisplaySlots;
            public readonly float PrefixRegionWidth;
            /// <summary>The font face the gate approved, as its COM identity. CONSUMED by
            /// <see cref="ReconcileApprovedFontFace"/> at layout time, in
            /// CalculateAndResizeWindow. A face object's metrics
            /// (design units/em, ascent, descent, per-glyph advances) are immutable for the
            /// lifetime of the face, so capturing the identity captures the metrics: two
            /// layouts computed against the same face pointer cannot disagree. Metrics are
            /// therefore deliberately NOT duplicated into this snapshot.</summary>
            public readonly nint FontFace;
            /// <summary> Generation of the face above. The pointer alone suffers ABA when a
            /// released face's address is reused; the (pointer, generation) PAIR is the identity.</summary>
            public readonly int FontGeneration;
            public readonly float DpiX, DpiY;
            private readonly string[] _candidates, _semanticFields, _slices, _engineSelection;
            private readonly float[] _lengths;
            private readonly long[] _glyphGeometry;

            // PAYLOAD the layout calculation consumes. With only comparison
            // digests captured, CalculateAndResizeWindow would re-read live state and could compute a size
            // from inputs the gate never approved (a torn read between decision and computation).
            // These are the same object graph the frame published — the worker never mutates a frame
            // after publishing, so holding the references IS a snapshot.
            private readonly List<float> _lengthsPayload;
            private readonly List<List<ManjuShaperCore.FinalGlyph>> _shapedPayload;
            private readonly List<Messaging.SemanticCandidateItem>? _semanticPayload;
            // Handed out as READ-ONLY views. Returning the raw List lets a caller
            // mutate a captured snapshot's payload directly (lengths 1→0, shaped 1→0) while Equals still
            // says True, and a "snapshot" you can edit is not a snapshot. The payload COUNTS are part of
            // Equals below so any divergence is also visible to the gate.
            // The INNER lists are handed out read-only too. Deep-COPYING them alone would leave
            // `ShapedPayload[i]` a mutable List<FinalGlyph>, so a caller
            // could edit the approved snapshot's own payload in place: the outer layer read-only, the inner one mutable.
            // SemanticPayload is read-only for the same reason. Returning
            // the raw semantic List would let SemanticPayload[0] be replaced on an APPROVED snapshot:
            // Equals would still say True (the comparison strings are a separate captured array) while the
            // width helper the layout uses, which reads SemanticName / CodePointSequence, returns a different width.
            private readonly System.Collections.ObjectModel.ReadOnlyCollection<float> _lengthsView;
            private readonly System.Collections.ObjectModel.ReadOnlyCollection<IReadOnlyList<ManjuShaperCore.FinalGlyph>> _shapedView;
            private readonly System.Collections.ObjectModel.ReadOnlyCollection<Messaging.SemanticCandidateItem>? _semanticView;
            public IReadOnlyList<float> LengthsPayload => _lengthsView;
            public IReadOnlyList<IReadOnlyList<ManjuShaperCore.FinalGlyph>> ShapedPayload => _shapedView;
            public IReadOnlyList<Messaging.SemanticCandidateItem>? SemanticPayload => _semanticView;

            /// <summary> Whether the fusion card-0 width floor applies. DECIDED AT CAPTURE TIME
            /// from the state the gate approved. If CalculateAndResizeWindow re-derived it from
            /// LIVE raw input / focus / engine selection / font data, a snapshot approved with a
            /// 132 DIP floor could compute against later live state needing 100 DIP.</summary>
            public readonly bool FusionFloorApplies;

            public LayoutSnapshot(string rawInput, int focusedRow, int upem, int x, int y,
                                  int screenW, int screenH, int clientW, int clientH,
                                  int workX, int workY, int workW, int workH,
                                  int displaySlots, float prefixRegionWidth, nint fontFace, int fontGeneration,
                                  float dpiX, float dpiY,
                                  string[] candidates, float[] lengths, long[] glyphGeometry,
                                  string[] semanticFields, string[] slices, string[] engineSelection,
                                  int candidateCount, int lengthCount, int shapedCount, int semanticCount, int sliceCount,
                                  List<float> lengthsPayload,
                                  List<List<ManjuShaperCore.FinalGlyph>> shapedPayload,
                                  List<Messaging.SemanticCandidateItem>? semanticPayload,
                                  bool fusionFloorApplies)
            {
                _lengthsPayload = lengthsPayload;
                _shapedPayload = shapedPayload;
                _semanticPayload = semanticPayload;
                _lengthsView = new System.Collections.ObjectModel.ReadOnlyCollection<float>(lengthsPayload);
                var shapedRo = new List<IReadOnlyList<ManjuShaperCore.FinalGlyph>>(shapedPayload.Count);
                foreach (var inner in shapedPayload)
                {
                    shapedRo.Add(new System.Collections.ObjectModel.ReadOnlyCollection<ManjuShaperCore.FinalGlyph>(
                        inner ?? new List<ManjuShaperCore.FinalGlyph>()));
                }
                _shapedView = new System.Collections.ObjectModel.ReadOnlyCollection<IReadOnlyList<ManjuShaperCore.FinalGlyph>>(shapedRo);
                _semanticView = semanticPayload == null
                    ? null
                    : new System.Collections.ObjectModel.ReadOnlyCollection<Messaging.SemanticCandidateItem>(semanticPayload);
                FusionFloorApplies = fusionFloorApplies;
                RawInput = rawInput; FocusedRow = focusedRow; Upem = upem; X = x; Y = y;
                ScreenW = screenW; ScreenH = screenH; ClientW = clientW; ClientH = clientH;
                WorkX = workX; WorkY = workY; WorkW = workW; WorkH = workH;
                DisplaySlots = displaySlots; PrefixRegionWidth = prefixRegionWidth;
                FontFace = fontFace; FontGeneration = fontGeneration; DpiX = dpiX; DpiY = dpiY;
                _candidates = (string[])candidates.Clone();
                _lengths = (float[])lengths.Clone();
                _glyphGeometry = (long[])glyphGeometry.Clone();
                _semanticFields = (string[])semanticFields.Clone();
                _slices = (string[])slices.Clone();
                _engineSelection = (string[])engineSelection.Clone();
                CandidateCount = candidateCount; LengthCount = lengthCount; ShapedCount = shapedCount;
                SemanticCount = semanticCount; SliceCount = sliceCount;
            }

            /// <summary>The SAME approved snapshot, carrying the client extents this
            /// layout just wrote — and nothing else re-approved.
            ///
            /// ClientW/ClientH are read from GetClientRect, which is exactly what CalculateAndResizeWindow
            /// has just set with SetWindowPos. They are also part of Equals. So without a re-base the gate
            /// would compare its own OUTPUT against its own INPUT: relayout resizes 200 -> 276, the next
            /// update captures 276 against an approved snapshot still saying 200, rules "changed", and lays
            /// out identical content a second time.
            ///
            /// This is deliberately NOT a re-capture. Re-approving the whole snapshot would let any input
            /// that drifted since approval slip through under the cover of a resize.
            /// Only the two values the layout itself authored are re-based, every other input stays under
            /// the gate, and the fields are
            /// copied by name below so a future field added to LayoutSnapshot cannot be silently swept in.
            ///
            /// The source is another LayoutSnapshot: its arrays are private, never handed out except as
            /// read-only views, and readonly for its lifetime, so sharing the references is safe here in
            /// a way it is not for the public constructor (which clones what its CALLER hands it).</summary>
            public LayoutSnapshot WithClientSize(int clientW, int clientH) => new LayoutSnapshot(this, clientW, clientH);

            private LayoutSnapshot(LayoutSnapshot o, int clientW, int clientH)
            {
                ClientW = clientW; ClientH = clientH;   // the ONLY two fields this copy does not carry over
                RawInput = o.RawInput; FocusedRow = o.FocusedRow; Upem = o.Upem; X = o.X; Y = o.Y;
                ScreenW = o.ScreenW; ScreenH = o.ScreenH;
                WorkX = o.WorkX; WorkY = o.WorkY; WorkW = o.WorkW; WorkH = o.WorkH;   // copied BY NAME
                DisplaySlots = o.DisplaySlots; PrefixRegionWidth = o.PrefixRegionWidth;
                FontFace = o.FontFace; FontGeneration = o.FontGeneration; DpiX = o.DpiX; DpiY = o.DpiY;
                CandidateCount = o.CandidateCount; LengthCount = o.LengthCount; ShapedCount = o.ShapedCount;
                SemanticCount = o.SemanticCount; SliceCount = o.SliceCount;
                FusionFloorApplies = o.FusionFloorApplies;
                _candidates = o._candidates; _lengths = o._lengths; _glyphGeometry = o._glyphGeometry;
                _semanticFields = o._semanticFields; _slices = o._slices; _engineSelection = o._engineSelection;
                _lengthsPayload = o._lengthsPayload; _shapedPayload = o._shapedPayload; _semanticPayload = o._semanticPayload;
                _lengthsView = o._lengthsView; _shapedView = o._shapedView; _semanticView = o._semanticView;
            }

            public bool Equals(LayoutSnapshot? o) =>
                o != null && RawInput == o.RawInput && FocusedRow == o.FocusedRow && Upem == o.Upem
                && X == o.X && Y == o.Y && ScreenW == o.ScreenW && ScreenH == o.ScreenH
                && WorkX == o.WorkX && WorkY == o.WorkY && WorkW == o.WorkW && WorkH == o.WorkH
                && ClientW == o.ClientW && ClientH == o.ClientH
                && DisplaySlots == o.DisplaySlots && PrefixRegionWidth.Equals(o.PrefixRegionWidth)
                && CandidateCount == o.CandidateCount && LengthCount == o.LengthCount
                && ShapedCount == o.ShapedCount && SemanticCount == o.SemanticCount && SliceCount == o.SliceCount
                && FusionFloorApplies == o.FusionFloorApplies
                // Payload counts participate too, because editing the payload lists of two
                // captured snapshots (lengths 1→0, shaped 1→0) leaves Equals still saying True.
                && _lengthsPayload.Count == o._lengthsPayload.Count
                && _shapedPayload.Count == o._shapedPayload.Count
                // INNER counts too, because mutating an inner list is something the
                // outer-count comparison cannot see.
                && InnerShapedCountsEqual(o)
                && (_semanticPayload?.Count ?? -1) == (o._semanticPayload?.Count ?? -1)
                && FontFace == o.FontFace && FontGeneration == o.FontGeneration
                && DpiX.Equals(o.DpiX) && DpiY.Equals(o.DpiY)
                && _candidates.AsSpan().SequenceEqual(o._candidates)
                && _lengths.AsSpan().SequenceEqual(o._lengths)
                && _glyphGeometry.AsSpan().SequenceEqual(o._glyphGeometry)
                && _semanticFields.AsSpan().SequenceEqual(o._semanticFields)
                && _slices.AsSpan().SequenceEqual(o._slices)
                && _engineSelection.AsSpan().SequenceEqual(o._engineSelection);

            private bool InnerShapedCountsEqual(LayoutSnapshot o)
            {
                if (_shapedPayload.Count != o._shapedPayload.Count) return false;
                for (int i = 0; i < _shapedPayload.Count; i++)
                    if ((_shapedPayload[i]?.Count ?? -1) != (o._shapedPayload[i]?.Count ?? -1)) return false;
                return true;
            }

            public override bool Equals(object? o) => Equals(o as LayoutSnapshot);
            public override int GetHashCode() => HashCode.Combine(RawInput, FocusedRow, Upem, CandidateCount, DisplaySlots, PrefixRegionWidth, X, Y);
        }

        private LayoutSnapshot? _lastLayoutSnapshot;

        /// <summary> Every collection the layout math or the equality comparison touches,
        /// copied in ONE pass under _resourceLock. Taking the payload under the lock while building the
        /// comparison arrays (candidates / lengths / glyph geometry / slices) from a SECOND, unlocked read
        /// of the same live fields lets equality describe a different instant than the payload the
        /// layout then consumes, and equality has to match the payload actually consumed. Both derive from this one
        /// copy, so they are the same bytes by construction.</summary>
        private sealed class ConsumedState
        {
            public List<string> Candidates = new();
            public List<float> Lengths = new();
            public List<List<ManjuShaperCore.FinalGlyph>> Shaped = new();
            public List<Messaging.SemanticCandidateItem>? Semantic;
            public List<string> Slices = new();
        }

        private ConsumedState CopyConsumedState()
        {
            lock (_resourceLock)
            {
                var c = new ConsumedState();
                c.Candidates = _candidates == null ? new List<string>() : new List<string>(_candidates);
                c.Lengths = new List<float>(_candidateLengths);
                // DEEP copy. Copying only the outer list leaves every element as
                // the live inner List<FinalGlyph>, so mutating an inner list of one captured
                // snapshot still leaves Equals reporting the two snapshots equal.
                // FinalGlyph is a value type, so copying each inner list detaches the payload completely.
                c.Shaped = new List<List<ManjuShaperCore.FinalGlyph>>(_shapedCandidates.Count);
                foreach (var inner in _shapedCandidates)
                    c.Shaped.Add(inner == null ? new List<ManjuShaperCore.FinalGlyph>() : new List<ManjuShaperCore.FinalGlyph>(inner));
                c.Semantic = _semanticItems.Count > 0 ? new List<Messaging.SemanticCandidateItem>(_semanticItems) : null;
                c.Slices = new List<string>(_slicesAtFocus);
                return c;
            }
        }

        internal LayoutSnapshot CaptureLayoutSnapshot()
        {
            GetLayoutDpi(out float dx, out float dy);

            // ONE locked read feeds both the comparison arrays and the consumed payload.
            var consumed = CopyConsumedState();

            int n = consumed.Candidates.Count;
            var candidates = new string[n];
            var lengths = new float[n];
            var geom = new List<long>(n * 8);
            for (int i = 0; i < n; i++)
            {
                candidates[i] = consumed.Candidates[i] ?? "";
                lengths[i] = i < consumed.Lengths.Count ? consumed.Lengths[i] : float.NaN;
                if (i < consumed.Shaped.Count)
                {
                    var gl = consumed.Shaped[i];
                    geom.Add(gl.Count);
                    foreach (var g in gl)
                    {
                        geom.Add(g.GlyphId);
                        geom.Add(g.Cluster);
                        geom.Add(((long)g.XOffset << 32) | (uint)g.YOffset);
                        geom.Add(((long)g.XAdvance << 32) | (uint)g.YAdvance);
                        geom.Add(g.HAdvance);
                    }
                }
                else geom.Add(-1);
            }

            var semanticSource = consumed.Semantic ?? new List<Messaging.SemanticCandidateItem>();
            var semantic = new string[semanticSource.Count * 3];
            for (int i = 0; i < semanticSource.Count; i++)
            {
                var it = semanticSource[i];
                semantic[i * 3 + 0] = it?.SemanticName ?? "";
                semantic[i * 3 + 1] = it?.CodePointSequence ?? "";
                semantic[i * 3 + 2] = it?.VariantLabel ?? "";
            }

            var slices = new string[consumed.Slices.Count];
            for (int i = 0; i < consumed.Slices.Count; i++) slices[i] = consumed.Slices[i] ?? "";

            // Engine-side selection state — what ApplyFusionCardWidthFloor / GetFocusedCanonicalFusion
            // actually read. Without it the card-0 width need can go 132 -> 100
            // under an identical signature.
            var engineSel = Array.Empty<string>();
            if (_engine != null)
            {
                int units = GetCurrentRowCount();
                engineSel = new string[units];
                for (int i = 0; i < units; i++)
                {
                    var cg = _engine.GetChosenGlyphAt(i);
                    engineSel[i] = $"{(_engine.IsPositionSelected(i) ? 1 : 0)}|{_engine.GetPositionHighlight(i)}|{_engine.GetChosenSliceAt(i) ?? ""}|{(cg.HasValue ? cg.Value.GlyphId.ToString() : "-")}";
                }
            }

            // Client size: the live layout reads GetClientRect, so it is a layout input.
            int clientW = 0, clientH = 0;
            if (_hwnd != 0 && NativeMethods.GetClientRect(_hwnd, out RECT cr))
            { clientW = cr.Right - cr.Left; clientH = cr.Bottom - cr.Top; }

            // The work area of the monitor this window is on — approved with everything
            // else the layout consumes, so the size bound and the gate cannot disagree about which screen
            // they are talking about.
            RECT work = GetWorkAreaForWindow();

            return new LayoutSnapshot(
                _rawInput ?? "", _focusedRowIndex, _upem, _x, _y,
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN),
                clientW, clientH,
                work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top,
                DisplaySlotCount,          // drives prefix rows / scroll space
                PrefixRegionWidth,         // directly added to the target width
                _nativeFontFace, FontGeneration, dx, dy,
                candidates, lengths, geom.ToArray(), semantic, slices, engineSel,
                // counts come from the SAME copy the arrays and the payload came from.
                n, consumed.Lengths.Count, consumed.Shaped.Count, semanticSource.Count, consumed.Slices.Count,
                // payload the layout math consumes — the very same copy the comparison arrays
                // above were built from, so equality and consumption cannot describe different instants.
                consumed.Lengths, consumed.Shaped, consumed.Semantic,
                // Decide the fusion floor HERE, from the state being approved. The layout math
                // must not re-derive it from live raw input / focus / engine selection / font data.
                FusionFloorAppliesNow());
        }


        /// <summary> The work area of the monitor this window is on, physical px.
        ///
        /// Keyed on the window's own top-left rather than the primary monitor, so a candidate window that
        /// followed the caret onto a second screen is bounded by THAT screen — including one whose origin
        /// is negative, which is the ordinary left-of-primary arrangement. Falls back to the primary
        /// monitor's full bounds if the query fails: a bound that is too generous is no worse than no
        /// bound at all.
        ///
        /// MonitorFromPoint + GetMonitorInfo are in NativeMethods and used this way by
        /// TextService and the dictionary window too; here the same call bounds the
        /// candidate window's SIZE.</summary>
        private RECT GetWorkAreaForWindow()
        {
            try
            {
                var pt = new POINT { X = _x, Y = _y };
                nint mon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
                if (mon != 0)
                {
                    var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                    if (NativeMethods.GetMonitorInfo(mon, ref mi) &&
                        mi.rcWork.Right > mi.rcWork.Left && mi.rcWork.Bottom > mi.rcWork.Top)
                    {
                        return mi.rcWork;
                    }
                }
            }
            catch (Exception)
            {
            }
            int w = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int h = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            return new RECT { Left = 0, Top = 0, Right = w > 0 ? w : 1920, Bottom = h > 0 ? h : 1080 };
        }

        /// <summary> PURE. Bound a wanted window size by the work area it must live in.
        ///
        /// Everything here is DIP; the caller converts. Zero or negative work extents mean "unknown", and
        /// an unknown bound must not shrink the window to nothing: it returns the wanted size unchanged.
        ///
        /// This is a CLAMP and nothing more. It stops the window running off the monitor; it does not make
        /// the candidates that no longer fit reachable. Reaching them is the job of the horizontal
        /// candidate viewport (ComputeCandidateStrip).</summary>
        public static (float w, float h, bool widthClamped, bool heightClamped) ClampWindowSizeToWorkArea(
            float wantedWDip, float wantedHDip, float workWDip, float workHDip)
        {
            float w = wantedWDip, h = wantedHDip;
            bool cw = false, ch = false;
            if (workWDip > 0f && w > workWDip) { w = workWDip; cw = true; }
            if (workHDip > 0f && h > workHDip) { h = workHDip; ch = true; }
            return (w, h, cw, ch);
        }


        /// <summary> The fusion card-0 width-floor decision, evaluated at capture time.
        /// Isolated so a shaping failure cannot break snapshot capture.</summary>
        private bool FusionFloorAppliesNow()
        {
            try { return GetFocusedCanonicalFusion().HasValue; }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary> The authoritative gate: has any COMPLETE layout input changed by value?
        /// Compares by value, not by hash. Updates the stored snapshot when it has.</summary>
        /// <summary> The gate's three distinct outcomes. With only a bool,
        /// "I could not read the inputs" would be indistinguishable from "nothing changed": safe, but the
        /// pending layout would be dropped with nothing scheduled to come back for it.</summary>
        internal enum LayoutGateResult
        {
            /// <summary>Inputs verified and identical to the approved snapshot — nothing to do.</summary>
            Unchanged,
            /// <summary>Inputs verified and different — a new snapshot is approved.</summary>
            Changed,
            /// <summary>Inputs could not be read consistently — NOTHING is approved and the caller must
            /// stay dirty and retry.</summary>
            Unstable,
        }

        private LayoutGateResult LayoutInputsChanged()
        {
            var now = CaptureLayoutSnapshotStable();
            if (now == null)
            {
                // No verified snapshot ⇒ nothing is approved and nothing is recomputed.
                // The previously approved snapshot stays in force, so the window keeps its last good size
                // rather than being resized from a torn read.
                return LayoutGateResult.Unstable;
            }
            if (_lastLayoutSnapshot != null && _lastLayoutSnapshot.Equals(now)) return LayoutGateResult.Unchanged;
            _lastLayoutSnapshot = now;
            return LayoutGateResult.Changed;
        }

        // Bounded, throttled self-repost so an unstable read cannot leave the window
        // sized for the previous frame until some unrelated event happens along. Bounded because a retry
        // storm on a genuinely busy producer would be worse than the stale geometry it is fixing; the
        // counter resets on any completed layout, so normal use never approaches the cap.
        private const int MaxConsecutiveLayoutRetries = 8;
        private int _consecutiveLayoutRetries;
        private long _lastLayoutRetryTicks;

        /// <summary> Set when THIS update could not read the layout inputs, so the same
        /// turn does not go on to lay out from the previously approved snapshot.
        ///
        /// CLEARED AT THE TOP OF EVERY <c>HandleUpdateMessage</c>. Cleared only where it is
        /// consumed, any visibility/resource/cache early-return in between would carry a stale <c>true</c>
        /// into the NEXT call, and that call, even holding a freshly approved <c>Changed</c> snapshot,
        /// would skip its legitimate layout and leave dirty set with nothing rescheduled. One instability
        /// landing while resources are not yet ready is enough to reach it.</summary>
        private bool _layoutUnstableThisTurn;


        private void ScheduleLayoutRetry()
        {
            if (_consecutiveLayoutRetries >= MaxConsecutiveLayoutRetries)
            {
                return;
            }
            // Coalesce: at most one retry per ~16 ms, so a burst of unstable frames posts one message.
            // Coalescing must LEAVE A TIMER, not drop the retry. Returning early here having only
            // incremented a counter would mean that, if the already-posted update comes back unstable
            // again inside the window, the chain dies and recovery waits on an unrelated event.
            long now = Environment.TickCount64;
            if (now - _lastLayoutRetryTicks < 16 && _consecutiveLayoutRetries > 0)
            {
                ArmLayoutRetryTimer();
                return;
            }
            _lastLayoutRetryTicks = now;
            _consecutiveLayoutRetries++;

            if (_hwnd != 0)
            {
                NativeMethods.PostMessage(_hwnd, WM_APP_UPDATE, 0, 0);
            }
        }

        /// <summary> A completed layout ends the retry sequence.</summary>
        private void ResetLayoutRetryBudget()
        {
            _consecutiveLayoutRetries = 0;
            // CANCEL the OS timer, do not merely forget it. Clearing the flag alone
            // would leave a live SetTimer behind: a WM_TIMER would keep arriving for a retry sequence that is
            // already over, and at teardown it would outlive the state it refers to.
            if (_layoutRetryTimerArmed && _hwnd != 0) NativeMethods.KillTimer(_hwnd, LAYOUT_RETRY_TIMER_ID);
            _layoutRetryTimerArmed = false;
            _lastLayoutRetryTicks = 0;
        }

        /// <summary> Composition/window lifecycle boundaries end a retry sequence: the
        /// layout it was chasing no longer exists. Called from teardown and error reset, in addition
        /// to the reset on a SUCCESSFUL layout.</summary>
        private void CancelLayoutRetriesForLifecycleChange(string? reason)
        {
            if (_consecutiveLayoutRetries == 0 && !_layoutRetryTimerArmed) return;
            ResetLayoutRetryBudget();
            _layoutUnstableThisTurn = false;
        }

        // The deferred half of the coalescing window. A retry suppressed by the 16 ms
        // gate is not discarded — a one-shot timer guarantees the chain continues even if the update that
        // was already in flight comes back unstable inside that window.
        private const nuint LAYOUT_RETRY_TIMER_ID = 7;
        private bool _layoutRetryTimerArmed;


        private void ArmLayoutRetryTimer()
        {
            if (_layoutRetryTimerArmed) return;   // one outstanding timer is enough
            if (_hwnd == 0) return;
            // Mark armed only if the timer WAS created. Setting the flag first
            // and ignoring the result would let a failed SetTimer leave a phantom armed timer that never
            // fires and permanently suppresses further arming.
            nuint created = NativeMethods.SetTimer(_hwnd, LAYOUT_RETRY_TIMER_ID, 16, 0);
            if (created == 0)
            {
                return;
            }
            _layoutRetryTimerArmed = true;
        }

        /// <summary> The deferred retry fired. Kill the one-shot and ask for the
        /// update pass the coalescing window suppressed.</summary>
        private void OnLayoutRetryTimer()
        {
            _layoutRetryTimerArmed = false;
            if (_hwnd != 0) NativeMethods.KillTimer(_hwnd, LAYOUT_RETRY_TIMER_ID);
            if (!_layoutDirty) return;   // something else already completed the layout
            _lastLayoutRetryTicks = 0;   // the coalescing window has elapsed by definition
            ScheduleLayoutRetry();
        }

        /// <summary> Capture that is verified NOT TORN.
        ///
        /// `CopyConsumedState()` takes the collections under `_resourceLock`, but DPI, engine selection,
        /// window position, client rect, raw input / focus, the font identity and the fusion-floor
        /// decision are all read outside it, and their producers do not hold that lock — so a single
        /// pass could mix values from two different instants, because a reader-side lock alone
        /// cannot prove a fully atomic snapshot).
        ///
        /// Rather than force every unrelated producer onto one lock — which would put UI, worker and
        /// window-message threads into a new lock order — this re-reads and requires two consecutive
        /// passes to agree by value. Two equal passes mean nothing observable moved across either of
        /// them, which is exactly the property "one instant" is needed for. Disagreement means a producer
        /// really did move mid-capture; retry. That is detection, and detection is sufficient here
        /// because a torn snapshot is discarded rather than used.</summary>
        internal LayoutSnapshot? CaptureLayoutSnapshotStable()
        {
            const int MaxAttempts = 3;
            var previous = CaptureLayoutSnapshot();
            for (int attempt = 1; attempt < MaxAttempts; attempt++)
            {
                var again = CaptureLayoutSnapshot();
                if (previous.Equals(again)) return again;   // two passes agree ⇒ not torn
                previous = again;
            }
            // FAIL CLOSED. Returning the last unverified pass here would let the caller
            // store it as approved and compute from it in the same turn. Returning null instead means
            // "no verified snapshot exists right now": the gate does not update _lastLayoutSnapshot, does
            // not mark the layout dirty, and the next update re-gates.
            return null;
        }


        /// <summary> What to do when the live font face is compared against the one the
        /// layout gate approved.</summary>
        internal enum FontFaceApproval
        {
            /// <summary>Identities match — compute the layout.</summary>
            Proceed,
            /// <summary>Nothing was approved yet (face created after capture) — re-capture, then compute.</summary>
            ReSnapshot,
            /// <summary>The face changed (or vanished) after approval — do NOT size the window from it.</summary>
            Reject,
        }

        /// <summary> Pure decision for the approved-vs-live font face. Kept separate from
        /// CalculateAndResizeWindow so the three transitions are directly exercisable.
        /// The RAW POINTER IS NOT AN IDENTITY. Comparing only the pointer means a
        /// released face whose address is reused by the next allocation compares equal and the layout
        /// wrongly proceeds against a different font, which is classic ABA. Every create/release bumps
        /// a generation counter which is captured with the pointer and compared here: the pair is the
        /// identity, and a reused address carries a different generation.</summary>
        internal static FontFaceApproval ReconcileApprovedFontFace(nint approvedFace, int approvedGen,
                                                                   nint liveFace, int liveGen)
        {
            if (approvedFace == liveFace && approvedGen == liveGen) return FontFaceApproval.Proceed;
            if (approvedFace == 0 && approvedGen == 0) return FontFaceApproval.ReSnapshot;
            return FontFaceApproval.Reject;
        }

        /// <summary> Bumped on every font-face creation and release so a recycled pointer
        /// cannot masquerade as the approved face. Consumed by ReconcileApprovedFontFace.</summary>
        private int _fontGeneration;
        internal int FontGeneration => System.Threading.Volatile.Read(ref _fontGeneration);
        private void BumpFontGeneration() => System.Threading.Interlocked.Increment(ref _fontGeneration);


        private int ComputeLayoutSignature()
        {
            unchecked
            {
                int h = 17;
                h = h * 31 + (_rawInput?.Length ?? 0);
                h = h * 31 + (_rawInput?.GetHashCode() ?? 0);
                h = h * 31 + _candidates.Count;
                h = h * 31 + _focusedRowIndex;
                h = h * 31 + _upem;                       // design-units → DIP scale factor
                // A presence bit is not identity: a font-face
                // SWAP (same non-null) changes every glyph metric without moving a presence bit. Fold the
                // face identity itself; and fold the DPI the layout math scales by (monitor moves).
                h = h * 31 + _nativeFontFace.GetHashCode();
                GetLayoutDpi(out float sigDpiX, out float sigDpiY);
                h = h * 31 + BitConverter.SingleToInt32Bits(sigDpiX);
                h = h * 31 + BitConverter.SingleToInt32Bits(sigDpiY);
                // Without the position, _y=100 and _y=900 would hash identically
                // while CalculateAndResizeWindow's height budget (screenBottom − _y) comes out 970 vs 170, so
                // the same content moved to the bottom of the screen could skip a REQUIRED shrink. Window
                // position and the screen extents are layout INPUTS, so they belong in the signature.
                h = h * 31 + _x;
                h = h * 31 + _y;
                h = h * 31 + NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                h = h * 31 + NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
                // Per-position chosen slices feed the card contents (fusion/degrade selections change
                // what is drawn and measured) — fold them by value.
                h = h * 31 + _slicesAtFocus.Count;
                for (int i = 0; i < _slicesAtFocus.Count; i++)
                    h = h * 31 + (_slicesAtFocus[i]?.GetHashCode() ?? 0);

                // ApplyFusionCardWidthFloor →
                // GetFocusedCanonicalFusion reads the ENGINE's per-position selection + chosen-glyph
                // state directly; picking a non-fusion glyph flips the card-0 width need 132→100 (raw='bu'),
                // which a signature without that state does not see. _slicesAtFocus is per-candidate-at-focus, not
                // that engine snapshot — fold the engine state itself, per position.
                if (_engine != null)
                {
                    int units = GetCurrentRowCount();
                    h = h * 31 + units;
                    for (int i = 0; i < units; i++)
                    {
                        h = h * 31 + (_engine.IsPositionSelected(i) ? 1 : 0);
                        h = h * 31 + _engine.GetPositionHighlight(i);
                        h = h * 31 + (_engine.GetChosenSliceAt(i)?.GetHashCode() ?? 0);
                        var cg = _engine.GetChosenGlyphAt(i);
                        h = h * 31 + (cg.HasValue ? (int)cg.Value.GlyphId : -1);
                    }
                }

                // Candidate identity + the ACTUAL geometry the layout consumes — every field of every
                // shaped glyph, not just the count. With the count alone, a different
                // XOffset/XAdvance would give an identical signature and suppress a legitimate resize.
                for (int i = 0; i < _candidates.Count; i++)
                {
                    h = h * 31 + (_candidates[i]?.GetHashCode() ?? 0);
                    if (i < _shapedCandidates.Count)
                    {
                        var glyphs = _shapedCandidates[i];
                        h = h * 31 + glyphs.Count;
                        for (int gi = 0; gi < glyphs.Count; gi++)
                        {
                            var fg = glyphs[gi];
                            h = h * 31 + (int)fg.GlyphId;
                            h = h * 31 + fg.XOffset;
                            h = h * 31 + fg.YOffset;
                            h = h * 31 + fg.XAdvance;
                            h = h * 31 + fg.YAdvance;
                            h = h * 31 + fg.HAdvance;
                        }
                    }
                    // Measured length is what the width calculation consumes; float → exact bits so a
                    // sub-pixel change is not silently rounded away.
                    h = h * 31 + (i < _candidateLengths.Count
                        ? BitConverter.SingleToInt32Bits(_candidateLengths[i])
                        : 0);
                }

                // Metadata rows contribute to width (content-governed width).
                h = h * 31 + _semanticItems.Count;
                for (int i = 0; i < _semanticItems.Count; i++)
                {
                    var s = _semanticItems[i];
                    if (s == null) { h = h * 31; continue; }
                    h = h * 31 + (s.SemanticName?.GetHashCode() ?? 0);
                    h = h * 31 + (s.CodePointSequence?.GetHashCode() ?? 0);
                    h = h * 31 + (s.VariantLabel?.GetHashCode() ?? 0);
                }
                return h;
            }
        }

        private bool _positionDirty = false;
        private bool _updatePosted = false;
        private bool _updatePending = false;
        private bool _timerArmed = false;
        private bool _windowVisible = false;
        public bool IsVisible => _windowVisible;
        private long _lastUpdateTick = 0;
        private int _lastCandidateSignature = 0;
        private const uint WM_APP_UPDATE = 0x8001;
        // Frame deliveries queued from the worker, drained on the window thread.
        private const uint WM_APP_DELIVER = 0x8006;
        // A LIST under _dispatcherLifecycleGate, not a ConcurrentQueue. Holding the items by identity
        // makes "remove exactly this work item" expressible.
        private readonly List<Action> _deliveryQueue = new();


        /// <summary> The retained queue is BOUNDED. Without a limit, continued typing
        /// while nothing drains it would grow it until
        /// teardown. Frames are superseded by newer frames anyway — every consumer re-validates the epoch
        /// — so dropping the OLDEST when full loses nothing a later frame would not have replaced.</summary>
        private const int MaxRetainedDeliveries = 32;

        /// <summary>Caller must hold <see cref="_dispatcherLifecycleGate"/>.</summary>
        private void RetainDelivery(Action work)
        {
            while (_deliveryQueue.Count >= MaxRetainedDeliveries)
            {
                _deliveryQueue.RemoveAt(0);   // oldest first — a newer frame supersedes it anyway
            }
            _deliveryQueue.Add(work);
        }

        /// <summary> Remove EXACTLY this work item. Dequeuing the head on the post-failure
        /// path would drop an older delivery and keep the one just announced as dropped.
        /// Caller must hold the gate.</summary>
        private bool RemoveRetainedDelivery(Action work)
        {
            for (int i = _deliveryQueue.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(_deliveryQueue[i], work)) { _deliveryQueue.RemoveAt(i); return true; }
            }
            return false;
        }

        /// <summary> Run every retained delivery on THIS thread. Only ever called from
        /// the window thread (WM_APP_DELIVER), which is the thread TSF calls Clear() on.</summary>
        internal void DrainRetainedDeliveries()
        {
            while (true)
            {
                Action work;
                lock (_dispatcherLifecycleGate)
                {
                    if (_deliveryQueue.Count == 0) return;
                    work = _deliveryQueue[0];
                    _deliveryQueue.RemoveAt(0);
                }
                _engine?.RunDelivery(work);   // outside the gate: never hold a lock across foreign code
            }
        }

        /// <summary> Point ManjuEngine's delivery at this window's thread. Enqueue + post;
        /// never block the worker.
        ///
        /// AND NEVER RUN THE WORK INLINE, not even when the HWND is zero, the presenter is in its
        /// error state, or PostMessage fails. Inline means the WORKER enters the subscriber, so the window
        /// between the epoch check and the subscriber call reopens and a cancelled composition can be delivered.
        /// A frame that is not painted costs one repaint; a frame from a cancelled composition must never
        /// be delivered. These branches retain the work and report a drop.</summary>
        /// <summary> False once this presenter's routing is retired, so a closure the
        /// worker already captured cannot enqueue into a queue that will never be drained.</summary>
        private bool _dispatcherAccepting;


        /// <summary> Highest FrameId already applied in this composition. Frames not newer than
        /// this are refused, so the display never walks backwards. Reset at composition end.</summary>
        private int _lastAppliedFrameId;


        /// <summary> Serialises {accepting check + retain} against {retire + discard}.</summary>
        private readonly object _dispatcherLifecycleGate = new();


        private void InstallFrameDeliveryDispatcher()
        {
            lock (_dispatcherLifecycleGate) { _dispatcherAccepting = true; }
            // Installed ON THIS ENGINE and owned by THIS presenter. With a
            // process-global static, a second presenter's install would silently replace the first's, and
            // the first's Dispose would then clear routing for the second, after which its deliveries go
            // back to the worker thread with nobody noticing.
            _engine?.InstallFrameDeliveryDispatcher(this, work =>
            {
                // A RETIRED dispatcher accepts nothing. The worker can read the
                // dispatcher reference, the UI thread can then uninstall and discard the queue, and the
                // worker can still invoke the closure it already holds — re-populating a queue nobody
                // will ever drain. The accepting flag is cleared by uninstall, so that late call is
                // refused instead.
                // The accepting CHECK and the ENQUEUE are ONE transaction, taken under
                // the same gate teardown uses. A Volatile read only buys visibility:
                // the worker could read accepting=true, the UI thread could then retire the dispatcher and
                // empty the queue, and the worker could still enqueue into a queue nothing would drain
                // again. Holding the gate across {check + retain} closes that interleaving.
                lock (_dispatcherLifecycleGate)
                {
                    if (!_dispatcherAccepting)
                    {
                        return;
                    }
                    // BETWEEN the check and the retain is the exact instant a teardown must not be
                    // able to slip into. Whatever runs here runs with the gate held, so an uninstall
                    // on another thread waits until the retain is done.
                    RetainDelivery(work);
                }
                nint hwnd = _hwnd;
                if (hwnd == 0)
                {
                    // No window at all — there is nothing to post to and nothing to paint into. Take back
                    // what we just retained: a queue that never wakes is not a retention, it is a leak.
                    lock (_dispatcherLifecycleGate) { RemoveRetainedDelivery(work); }
                    return;
                }
                // Error state RETAINS AND POSTS. WndProc drains WM_APP_DELIVER ahead of the error guard,
                // but only when a message arrives: retaining and returning WITHOUT posting would let every
                // frame after an error state accumulate to the 32-item cap and be discarded at teardown,
                // because nothing asks for a pump. Posting is safe precisely because the drain sits before the guard.
                if (NativeMethods.PostMessage(hwnd, WM_APP_DELIVER, 0, 0)) return;

                // One bounded retry, then DROP the work just retained. Nothing guarantees another
                // pump, so "the next pump drains it" would be a claim, not a mechanism.
                // Dropping is honest and costs one repaint; the frame is
                // superseded by the next one anyway. The work must never run here.
                if (NativeMethods.PostMessage(hwnd, WM_APP_DELIVER, 0, 0))
                {
                    return;
                }
                lock (_dispatcherLifecycleGate) { RemoveRetainedDelivery(work); }
            });
        }

        /// <summary> Stop routing through a window that is going away, and drain whatever
        /// is still queued so nothing is silently lost. Owner-checked: a presenter that never installed
        /// (or was already superseded) cannot strand a live one.</summary>
        private void UninstallFrameDeliveryDispatcher()
        {
            // RETIRE AND DISCARD AS ONE TRANSACTION, under the gate the dispatcher's
            // {check + retain} also takes. With the flag written and the queue discarded in two separate
            // steps, a worker that has already passed the check could enqueue between them, into a queue
            // that has just been emptied and will never be drained again.
            //
            // DISCARD, do not run. Draining inline here means Destroy/Dispose
            // is called from whatever thread is tearing down, which is exactly the wrong-thread callback
            // this dispatcher exists to prevent. The
            // window is going away; a frame for it has nothing left to paint into.
            lock (_dispatcherLifecycleGate)
            {
                _dispatcherAccepting = false;
                _deliveryQueue.Clear();
            }
            bool wasOwner = _engine?.UninstallFrameDeliveryDispatcher(this) ?? false;
        }
        private const uint WM_APP_HIDE = 0x8002; // hides the window across threads
        private const uint WM_TIMER = 0x0113;
        private const nuint UPDATE_TIMER_ID = 1;
        private const int UPDATE_THROTTLE_MS = 10;
        private const int CS_DROPSHADOW = 0x00020000;
        private const int CS_IME = 0x00010000; // crucial: it tells Windows this is an IME window

        private static readonly Guid IID_ID2D1Factory = new Guid("06152247-6f50-465a-9245-118bfd3b6007");

        private static int _classCounter = 0;
        private Stopwatch _paintStopwatch = new Stopwatch(); // Paint timeout protection
        
        private bool _inErrorState = false; // Prevent operation after critical failure
        private int _createFontFaceAttempts = 0;
        private const int MAX_CREATE_ATTEMPTS = 3;

        // A Lock protects the resources
        private readonly Lock _resourceLock = new();

        private readonly struct OccupiedWidthMetrics
        {
            internal OccupiedWidthMetrics(float scale, float minLeft, float maxRight, float rendererAdvanceY, float occupiedWidth, float readabilityBodyWidth)
            {
                Scale = scale;
                MinLeft = minLeft;
                MaxRight = maxRight;
                RendererAdvanceY = rendererAdvanceY;
                OccupiedWidth = occupiedWidth;
                ReadabilityBodyWidth = readabilityBodyWidth;
            }

            internal float Scale { get; }
            internal float MinLeft { get; }
            internal float MaxRight { get; }
            internal float RendererAdvanceY { get; }
            internal float OccupiedWidth { get; }
            internal float ReadabilityBodyWidth { get; }
        }

        private readonly struct CardWidthMetrics
        {
            internal CardWidthMetrics(
                float baseCardWidth,
                float baselineBodyWidth,
                float metadataOccupiedWidth,
                float metadataBaselineWidth,
                float occupiedWidth,
                float bodyOvershoot,
                float metadataOvershoot,
                float rawIncrement,
                float incrementQuantum,
                float maximumIncrement,
                float boundedIncrement,
                float finalCardWidth)
            {
                BaseCardWidth = baseCardWidth;
                BaselineBodyWidth = baselineBodyWidth;
                MetadataOccupiedWidth = metadataOccupiedWidth;
                MetadataBaselineWidth = metadataBaselineWidth;
                OccupiedWidth = occupiedWidth;
                BodyOvershoot = bodyOvershoot;
                MetadataOvershoot = metadataOvershoot;
                RawIncrement = rawIncrement;
                IncrementQuantum = incrementQuantum;
                MaximumIncrement = maximumIncrement;
                BoundedIncrement = boundedIncrement;
                FinalCardWidth = finalCardWidth;
            }

            internal float BaseCardWidth { get; }
            internal float BaselineBodyWidth { get; }
            internal float MetadataOccupiedWidth { get; }
            internal float MetadataBaselineWidth { get; }
            internal float OccupiedWidth { get; }
            internal float BodyOvershoot { get; }
            internal float MetadataOvershoot { get; }
            internal float RawIncrement { get; }
            internal float IncrementQuantum { get; }
            internal float MaximumIncrement { get; }
            internal float BoundedIncrement { get; }
            internal float FinalCardWidth { get; }
        }

        private readonly struct BodyClipMetrics
        {
            internal BodyClipMetrics(float minimumClipWidth, float occupiedDrivenWidth, float finalClipWidth)
            {
                MinimumClipWidth = minimumClipWidth;
                OccupiedDrivenWidth = occupiedDrivenWidth;
                FinalClipWidth = finalClipWidth;
            }

            internal float MinimumClipWidth { get; }
            internal float OccupiedDrivenWidth { get; }
            internal float FinalClipWidth { get; }
        }

        private readonly struct PaintSpanMetrics
        {
            internal PaintSpanMetrics(
                float left,
                float right,
                float spanWidth,
                float visibleLeft,
                float visibleRight,
                float visibleWidth,
                float residualLeft,
                float residualRight)
            {
                Left = left;
                Right = right;
                SpanWidth = spanWidth;
                VisibleLeft = visibleLeft;
                VisibleRight = visibleRight;
                VisibleWidth = visibleWidth;
                ResidualLeft = residualLeft;
                ResidualRight = residualRight;
            }

            internal float Left { get; }
            internal float Right { get; }
            internal float SpanWidth { get; }
            internal float VisibleLeft { get; }
            internal float VisibleRight { get; }
            internal float VisibleWidth { get; }
            internal float ResidualLeft { get; }
            internal float ResidualRight { get; }
        }

        private readonly struct MetadataPaintMetrics
        {
            internal MetadataPaintMetrics(
                int glyphCount,
                float totalAdvanceWidth,
                float textLeft,
                float textRight,
                PaintSpanMetrics paintSpan)
            {
                GlyphCount = glyphCount;
                TotalAdvanceWidth = totalAdvanceWidth;
                TextLeft = textLeft;
                TextRight = textRight;
                PaintSpan = paintSpan;
            }

            internal int GlyphCount { get; }
            internal float TotalAdvanceWidth { get; }
            internal float TextLeft { get; }
            internal float TextRight { get; }
            internal PaintSpanMetrics PaintSpan { get; }
        }


        private readonly struct WindowWidthMetrics
        {
            internal WindowWidthMetrics(float outerPaddingWidth, float gapWidthTotal, float cardWidthsTotal, float finalWindowWidth)
            {
                OuterPaddingWidth = outerPaddingWidth;
                GapWidthTotal = gapWidthTotal;
                CardWidthsTotal = cardWidthsTotal;
                FinalWindowWidth = finalWindowWidth;
            }

            internal float OuterPaddingWidth { get; }
            internal float GapWidthTotal { get; }
            internal float CardWidthsTotal { get; }
            internal float FinalWindowWidth { get; }
        }

        private readonly struct WindowHeightBudgetMetrics
        {
            internal WindowHeightBudgetMetrics(float minimumCardHeight, float preferredVisibleWindowHeight, float screenBudget, float finalMaxWindowHeight, bool usedScreenBudget)
            {
                MinimumCardHeight = minimumCardHeight;
                PreferredVisibleWindowHeight = preferredVisibleWindowHeight;
                ScreenBudget = screenBudget;
                FinalMaxWindowHeight = finalMaxWindowHeight;
                UsedScreenBudget = usedScreenBudget;
            }

            internal float MinimumCardHeight { get; }
            internal float PreferredVisibleWindowHeight { get; }
            internal float ScreenBudget { get; }
            internal float FinalMaxWindowHeight { get; }
            internal bool UsedScreenBudget { get; }
        }

        private readonly struct HorizontalWindowLayoutMetrics
        {
            internal HorizontalWindowLayoutMetrics(int cardCount, int columnCount, float minimumCardHeight, float tallestCardHeight, float requiredHeight, float targetWidth, float targetHeight)
            {
                CardCount = cardCount;
                ColumnCount = columnCount;
                MinimumCardHeight = minimumCardHeight;
                TallestCardHeight = tallestCardHeight;
                RequiredHeight = requiredHeight;
                TargetWidth = targetWidth;
                TargetHeight = targetHeight;
            }

            internal int CardCount { get; }
            internal int ColumnCount { get; }
            internal float MinimumCardHeight { get; }
            internal float TallestCardHeight { get; }
            internal float RequiredHeight { get; }
            internal float TargetWidth { get; }
            internal float TargetHeight { get; }
        }

        private readonly struct SecondPassResizeMetrics
        {
            internal SecondPassResizeMetrics(int widthDelta, int heightDelta, bool requiresCorrection, int correctedWidth, int correctedHeight)
            {
                WidthDelta = widthDelta;
                HeightDelta = heightDelta;
                RequiresCorrection = requiresCorrection;
                CorrectedWidth = correctedWidth;
                CorrectedHeight = correctedHeight;
            }

            internal int WidthDelta { get; }
            internal int HeightDelta { get; }
            internal bool RequiresCorrection { get; }
            internal int CorrectedWidth { get; }
            internal int CorrectedHeight { get; }
        }

        static CandidateListUIPresenter()
        {
            unsafe
            {
                _sharedWndProcPtr = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&StaticWndProc;
            }
        }

        public CandidateListUIPresenter(ManjuEngine engine)
        {
            _engine = engine;
            // The settings summary lives on the engine, but the palette lives HERE — so the
            // engine asks for the theme name instead of the summary being assembled in two places.
            // Without this the theme row still exists; it reads ManjuEngine.ThemeValueWithNoPresenter,
            // which is deliberately not a plausible theme name.
            engine.ThemeNameProvider = () => ThemeName(_themeIndex);
            // The theme row of the settings menu moves the palette that lives here.
            engine.ThemeCycler = CycleTheme;
            // The settings file's appearance.theme moves the same palette, from the start.
            engine.ThemeSetter = ApplyTheme;
            engine.ApplyConfiguredTheme();
            // Where the engine's switch toasts land. Wired the same way and in the same
            // place as ThemeNameProvider; when no window is attached the sink is null and the cycles
            // show no toast.
            engine.SwitchToastSink = ShowSwitchToast;
            // A settings file read again may have changed the two switches cached below.
            engine.ConfigurationChanged += OnConfigurationChanged;
            int counter = System.Threading.Interlocked.Increment(ref _classCounter);
            _className = $"ManjuCandidateWindow_{Environment.ProcessId}_{counter}";
            try 
            {
                RegisterWindowClass();
            }
            catch (Exception)
            {
            }
        }

        private void ResetErrorState()
        {
            if (_inErrorState || _createFontFaceAttempts > 0)
            {
                _inErrorState = false;
                _createFontFaceAttempts = 0;
            }
        }

        private static int ComputeCandidateSignature(List<string> candidates, int selectedIndex)
        {
            unchecked
            {
                int hash = 17;
                hash = (hash * 31) + candidates.Count;
                hash = (hash * 31) + selectedIndex;
                for (int i = 0; i < candidates.Count; i++)
                {
                    hash = (hash * 31) + (candidates[i]?.GetHashCode() ?? 0);
                }
                return hash;
            }
        }

        private static float GetReadableBodyBaselineWidth(float fontSize)
        {
            return fontSize;
        }

        private static float GetReadableCardWidth(float fontSize, float padding)
        {
            return fontSize * ReadableCardBodyWidthMultiplier + padding * 2;
        }

        // Minimum OUTER width for a fused candidate card that carries the separated-form comparison
        // (DrawCard0FentiStructure): the fused centre is left-shifted by FontSize*0.6, the separated column sits at
        // +FontSize*1.6, and its ink extends ~compSize*0.6 (compSize=FontSize*0.78) further right. For the two
        // columns to lay out at NATURAL spacing (no inward clamp / cramped look) the card half-width must clear
        // (0.6+1.6)*FS + margin ≈ (card centre → the separated column's right). Floor at 3.5*FS + 2*padding = 132 (vs base 108) so the
        // fusion card's outer size matches a normal multi-letter candidate rather than shrinking to its 1-glyph minimum.
        private static float GetFusionCardWidthFloor(float fontSize, float padding)
        {
            return fontSize * 3.5f + padding * 2f;
        }

        internal static float GetCardWidthIncrementStep(float fontSize)
        {
            return fontSize * CardWidthIncrementQuantumMultiplier;
        }

        internal static float GetMaximumCardWidthIncrement(float fontSize)
        {
            return fontSize * MaximumCardWidthIncrementMultiplier;
        }

        internal static float GetCardContentHorizontalInset(float padding)
        {
            return Math.Max(4f, padding * 0.5f);
        }

        internal static float GetBodyClipHorizontalInset(float padding)
        {
            return Math.Max(1f, padding * 0.1f);
        }

        internal static float ComputeBodyClipRegionWidth(float cardWidth, float padding)
        {
            return Math.Max(0f, cardWidth - (GetBodyClipHorizontalInset(padding) * 2f));
        }

        private static BodyClipMetrics ComputeBodyClipMetrics(float cardWidth, float occupiedWidth, float padding)
        {
            float minimumClipWidth = ComputeBodyClipRegionWidth(cardWidth, padding);
            float occupiedDrivenWidth = Math.Max(0f, occupiedWidth) + Math.Max(4f, padding * 0.6f);
            float finalClipWidth = Math.Min(cardWidth, Math.Max(minimumClipWidth, occupiedDrivenWidth));
            return new BodyClipMetrics(minimumClipWidth, occupiedDrivenWidth, finalClipWidth);
        }

        internal static float ComputeBodyClipWidth(float cardWidth, float occupiedWidth, float padding)
        {
            return ComputeBodyClipMetrics(cardWidth, occupiedWidth, padding).FinalClipWidth;
        }

        private static PaintSpanMetrics ComputePaintSpanMetrics(float left, float right, float containerLeft, float containerRight)
        {
            float normalizedLeft = Math.Min(left, right);
            float normalizedRight = Math.Max(left, right);
            float spanWidth = Math.Max(0f, normalizedRight - normalizedLeft);
            float visibleLeft = Math.Max(containerLeft, normalizedLeft);
            float visibleRight = Math.Min(containerRight, normalizedRight);
            if (visibleRight < visibleLeft)
            {
                visibleRight = visibleLeft;
            }

            float visibleWidth = Math.Max(0f, visibleRight - visibleLeft);
            float residualLeft = Math.Max(0f, visibleLeft - containerLeft);
            float residualRight = Math.Max(0f, containerRight - visibleRight);
            return new PaintSpanMetrics(
                normalizedLeft,
                normalizedRight,
                spanWidth,
                visibleLeft,
                visibleRight,
                visibleWidth,
                residualLeft,
                residualRight);
        }

        private static PaintSpanMetrics ComputeUnionPaintSpanMetrics(float containerLeft, float containerRight, params PaintSpanMetrics[] spans)
        {
            bool hasVisibleSpan = false;
            float unionLeft = 0f;
            float unionRight = 0f;

            foreach (PaintSpanMetrics span in spans)
            {
                if (span.VisibleWidth <= 0f)
                {
                    continue;
                }

                if (!hasVisibleSpan)
                {
                    unionLeft = span.VisibleLeft;
                    unionRight = span.VisibleRight;
                    hasVisibleSpan = true;
                    continue;
                }

                if (span.VisibleLeft < unionLeft)
                {
                    unionLeft = span.VisibleLeft;
                }

                if (span.VisibleRight > unionRight)
                {
                    unionRight = span.VisibleRight;
                }
            }

            return hasVisibleSpan
                ? ComputePaintSpanMetrics(unionLeft, unionRight, containerLeft, containerRight)
                : ComputePaintSpanMetrics(containerLeft, containerLeft, containerLeft, containerRight);
        }


        private static string FormatRectF(in RECT_F rect)
        {
            return $"[{rect.left:F1},{rect.top:F1},{rect.right:F1},{rect.bottom:F1}]";
        }

        private static string FormatLedgerFloat(float value)
        {
            return value.ToString("F1");
        }

        private static string FormatLedgerPair(float first, float second)
        {
            return $"[{first:F1},{second:F1}]";
        }

        private static string FormatLedgerUnavailable()
        {
            return UnavailableOnLivePath;
        }

        private static string FormatLedgerBound(string sourceName, float value)
        {
            return $"BOUND_TO_{sourceName}({value:F1})";
        }

        private static string FormatLedgerBound(string sourceName, string value)
        {
            return $"BOUND_TO_{sourceName}({value})";
        }


        internal static float EstimateMetadataTextWidth(string? text, float metadataFontSize)
        {
            if (string.IsNullOrWhiteSpace(text) || metadataFontSize <= 0f)
            {
                return 0f;
            }

            float totalWidth = 0f;
            foreach (char ch in text.Trim())
            {
                if (char.IsWhiteSpace(ch))
                {
                    totalWidth += metadataFontSize * 0.35f;
                }
                else if (char.IsDigit(ch))
                {
                    totalWidth += metadataFontSize * 0.55f;
                }
                else if (ch <= 0x7F)
                {
                    totalWidth += metadataFontSize * 0.6f;
                }
                else
                {
                    totalWidth += metadataFontSize * 0.9f;
                }
            }

            return totalWidth;
        }

        internal static float ComputeMetadataOccupiedWidth(int selectionNumber, string? semanticName, string? codePointSequence, float metadataFontSize)
        {
            float selectionWidth = EstimateMetadataTextWidth(selectionNumber > 0 ? selectionNumber.ToString() : string.Empty, metadataFontSize);
            float semanticWidth = EstimateMetadataTextWidth(NormalizeSemanticNameForCard(semanticName), metadataFontSize);
            float codePointWidth = EstimateMetadataTextWidth(NormalizeCodePointSequenceForCard(codePointSequence), metadataFontSize);

            return Math.Max(selectionWidth, Math.Max(semanticWidth, codePointWidth));
        }

        private static int GetBodyGlyphCount(float textLength, float fontSize)
        {
            if (fontSize <= 0f)
            {
                return 1;
            }

            float normalizedLength = Math.Max(0f, textLength);
            return Math.Max(1, (int)Math.Ceiling(normalizedLength / fontSize));
        }

        internal static float GetSingleGlyphBodyHeight(float fontSize)
        {
            return fontSize * SingleGlyphBodyHeightMultiplier;
        }

        internal static float GetBodyHeightIncrement(float fontSize)
        {
            return fontSize;
        }

        private static float ComputeWindowWidthForColumnCount(int columnCount, float columnWidth, float padding)
        {
            if (columnCount <= 0)
            {
                return 0f;
            }

            return columnCount * columnWidth + Math.Max(0, columnCount - 1) * padding + padding * 2;
        }

        private static float ComputeWindowHeightForRowCount(int rowCount, float cardHeight, float padding)
        {
            if (rowCount <= 0)
            {
                return 0f;
            }

            // Trailing bottom padding so the card region never reaches the window's
            // outer border. Without it, height = padding + cardHeight (rowCount=1), so cardBottom
            // (= cardTop[Padding] + cardHeight) == windowHeight, making the card's grey bottom
            // border coincide EXACTLY with the window's grey outer border (same brush), so the two
            // bottom edges, in the same colour, would merge. Symmetric (top + cards + bottom), matching
            // the prefix path's 2*Padding, giving one Padding of clearance below the cards.
            return padding + rowCount * cardHeight + Math.Max(0, rowCount - 1) * padding + padding;
        }

        internal static float NormalizeDpi(float dpi)
        {
            return dpi > 0f ? dpi : 96.0f;
        }

        internal static float ConvertPixelsToDips(float pixels, float dpi)
        {
            return pixels * 96.0f / NormalizeDpi(dpi);
        }

        internal static int ConvertDipsToPixels(float dips, float dpi)
        {
            return (int)Math.Ceiling(dips * NormalizeDpi(dpi) / 96.0f);
        }

        private static OccupiedWidthMetrics ComputePrimaryBodyOccupiedWidthMetrics(IReadOnlyList<ManjuShaperCore.FinalGlyph>? glyphs, float fontSize, ushort upem)
        {
            return ComputePrimaryBodyOccupiedWidthMetricsWithFontFace(glyphs, fontSize, upem, fontFace: null);
        }

        private static OccupiedWidthMetrics ComputePrimaryBodyOccupiedWidthMetricsWithFontFace(IReadOnlyList<ManjuShaperCore.FinalGlyph>? glyphs, float fontSize, ushort upem, NativeMethods.IDWriteFontFace? fontFace)
        {
            ManjuDirectWriteRenderer.VerticalBodyLayoutMetrics layoutMetrics = ManjuDirectWriteRenderer.MeasureVerticalBodyLayout(glyphs, fontSize, upem, fontFace);
            return new OccupiedWidthMetrics(
                layoutMetrics.Scale,
                layoutMetrics.MinLeft,
                layoutMetrics.MaxRight,
                layoutMetrics.TotalAdvanceYDip,
                layoutMetrics.OccupiedWidth,
                Math.Max(fontSize, layoutMetrics.OccupiedWidth));
        }


        internal static float ComputePrimaryBodyOccupiedWidthWithFontFace(IReadOnlyList<ManjuShaperCore.FinalGlyph>? glyphs, float fontSize, ushort upem, NativeMethods.IDWriteFontFace? fontFace)
        {
            return ComputePrimaryBodyOccupiedWidthMetricsWithFontFace(glyphs, fontSize, upem, fontFace).OccupiedWidth;
        }

        /// <summary>
        /// Unified readable card width.
        /// Local cards share one base width and may only grow through one bounded
        /// body-driven increment; metadata stays subordinate and there is no
        /// separate global column-width clamp.
        /// </summary>
        private static CardWidthMetrics ComputeContentCardWidthMetrics(
            int selectionNumber,
            string? semanticName,
            string? codePointSequence,
            float primaryBodyWidth,
            float fontSize,
            float metadataFontSize,
            float padding)
        {
            float baseCardWidth = GetReadableCardWidth(fontSize, padding);
            float baselineBodyWidth = GetReadableBodyBaselineWidth(fontSize);
            float metadataOccupiedWidth = ComputeMetadataOccupiedWidth(selectionNumber, semanticName, codePointSequence, metadataFontSize);
            float metadataBaselineWidth = metadataFontSize * 4.5f;
            float occupiedWidth = Math.Max(fontSize, primaryBodyWidth);
            float bodyOvershoot = Math.Max(0f, occupiedWidth - baselineBodyWidth);
            float metadataOvershoot = Math.Max(0f, metadataOccupiedWidth - metadataBaselineWidth);
            float rawIncrement = 0f;
            float incrementQuantum = GetCardWidthIncrementStep(fontSize);
            float maximumIncrement = GetMaximumCardWidthIncrement(fontSize);
            float boundedIncrement = 0f;
            float finalCardWidth = baseCardWidth;

            if (fontSize > 0f && (bodyOvershoot > 0f || metadataOvershoot > 0f))
            {
                rawIncrement =
                    (bodyOvershoot * CardWidthBodyGainMultiplier) +
                    (metadataOvershoot * CardWidthMetadataGainMultiplier);
                if (rawIncrement > 0f)
                {
                    boundedIncrement = Math.Min(
                        maximumIncrement,
                        (float)Math.Ceiling(rawIncrement / incrementQuantum) * incrementQuantum);
                    finalCardWidth = baseCardWidth + boundedIncrement;
                }
            }

            return new CardWidthMetrics(
                baseCardWidth,
                baselineBodyWidth,
                metadataOccupiedWidth,
                metadataBaselineWidth,
                occupiedWidth,
                bodyOvershoot,
                metadataOvershoot,
                rawIncrement,
                incrementQuantum,
                maximumIncrement,
                boundedIncrement,
                finalCardWidth);
        }

        internal static float ComputeContentCardWidth(
            int selectionNumber,
            string? semanticName,
            string? codePointSequence,
            float primaryBodyWidth,
            float fontSize,
            float metadataFontSize,
            float padding)
        {
            return ComputeContentCardWidthMetrics(
                selectionNumber,
                semanticName,
                codePointSequence,
                primaryBodyWidth,
                fontSize,
                metadataFontSize,
                padding).FinalCardWidth;
        }

        internal static string NormalizeSemanticNameForCard(string? semanticName)
        {
            if (string.IsNullOrWhiteSpace(semanticName))
            {
                return string.Empty;
            }

            string trimmed = semanticName.Trim();
            if (trimmed.Length <= MaxSemanticNameDisplayChars)
            {
                return trimmed;
            }

            return trimmed[..MaxSemanticNameDisplayChars] + "…";
        }

        internal static string NormalizeCodePointSequenceForCard(string? codePointSequence)
        {
            if (string.IsNullOrWhiteSpace(codePointSequence))
            {
                return string.Empty;
            }

            MatchCollection matches = CodePointPattern.Matches(codePointSequence);
            if (matches.Count == 0)
            {
                return codePointSequence.Trim();
            }

            // Show all individual code points (e.g. "U+1820 U+180B" instead of "U+1820 x2")
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < matches.Count; i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(matches[i].Value.ToUpperInvariant());
            }
            return sb.ToString();
        }


        internal static List<float> ComputeAllCardWidthsFromShapedCandidatesWithFontFace(
            IReadOnlyList<List<ManjuShaperCore.FinalGlyph>> shapedCandidates,
            IReadOnlyList<Messaging.SemanticCandidateItem>? semanticItems,
            float fontSize,
            float padding,
            ushort upem,
            NativeMethods.IDWriteFontFace? fontFace)
        {
            var result = new List<float>();
            int count = shapedCandidates.Count;

            for (int i = 0; i < count; i++)
            {
                string? semanticName = null;
                string? codePointSequence = null;

                if (semanticItems != null && i < semanticItems.Count)
                {
                    semanticName = semanticItems[i].SemanticName;
                    codePointSequence = semanticItems[i].CodePointSequence;
                }

                float occupiedWidth = ComputePrimaryBodyOccupiedWidthWithFontFace(shapedCandidates[i], fontSize, upem, fontFace);

                float cardWidth = ComputeContentCardWidth(
                    selectionNumber: i + 1,
                    semanticName: semanticName,
                    codePointSequence: codePointSequence,
                    primaryBodyWidth: occupiedWidth,
                    fontSize: fontSize,
                    metadataFontSize: MetadataFontSize,
                    padding: padding);

                result.Add(cardWidth);
            }

            return result;
        }

        internal static float GetMinimumReadableBodySpan(float fontSize)
        {
            return GetSingleGlyphBodyHeight(fontSize);
        }

        private static float NormalizeVerticalContentLength(float textLength, float fontSize)
        {
            int glyphCount = GetBodyGlyphCount(textLength, fontSize);
            float singleGlyphBodyHeight = GetSingleGlyphBodyHeight(fontSize);
            float glyphIncrement = GetBodyHeightIncrement(fontSize);
            return singleGlyphBodyHeight + Math.Max(0, glyphCount - 1) * glyphIncrement;
        }

        private static float ComputeItemHeight(float textLength, float fontSize)
        {
            return NormalizeVerticalContentLength(textLength, fontSize);
        }

        private static SecondPassResizeMetrics ComputeSecondPassResizeMetrics(int targetWidth, int targetHeight, int realizedClientWidth, int realizedClientHeight)
        {
            int widthDelta = targetWidth - realizedClientWidth;
            int heightDelta = targetHeight - realizedClientHeight;
            if (Math.Abs(widthDelta) <= 1 && Math.Abs(heightDelta) <= 1)
            {
                return new SecondPassResizeMetrics(widthDelta, heightDelta, false, targetWidth, targetHeight);
            }

            int correctedWidth = Math.Max(1, targetWidth + widthDelta);
            int correctedHeight = Math.Max(1, targetHeight + heightDelta);
            return new SecondPassResizeMetrics(widthDelta, heightDelta, true, correctedWidth, correctedHeight);
        }


        // Window height budget. When the screen budget is known (>0), it is used directly.
        // preferredVisibleWindowHeight is a "comfortable default" cap intended for the unknown-screen
        // case (DefaultVisibleRowCountWhenScreenUnknown rows) and applies only as a fallback when the
        // screen is genuinely unknown (screenHeight <= 0). Math.Min(screenBudget, preferredVisibleWindowHeight)
        // would let this cap dominate a known screen, so the window would never fill the available
        // screen budget even when input grows long. The card-height
        // rounding still kicks in downstream so the final window snaps to N-card rows.
        private static WindowHeightBudgetMetrics ComputeWindowHeightBudgetMetrics(float screenHeight, float currentHeight, float locationY, float fontSize, float padding)
        {
            _ = currentHeight;
            float minimumCardHeight = ComputeCardTotalHeight(ComputeItemHeight(fontSize, fontSize), fontSize, padding);
            float preferredVisibleWindowHeight = ComputeWindowHeightForRowCount(DefaultVisibleRowCountWhenScreenUnknown, minimumCardHeight, padding);
            if (screenHeight > 0)
            {
                float screenBudget = screenHeight - Math.Max(0f, locationY) - padding;
                // Use the full screen budget, NOT capped by preferredVisibleWindowHeight.
                float finalMaxWindowHeight = Math.Max(0f, screenBudget);
                return new WindowHeightBudgetMetrics(minimumCardHeight, preferredVisibleWindowHeight, screenBudget, finalMaxWindowHeight, true);
            }

            // Screen unknown: fall back to the preferred comfortable default (no screen to honor).
            return new WindowHeightBudgetMetrics(minimumCardHeight, preferredVisibleWindowHeight, 0f, preferredVisibleWindowHeight, false);
        }


        private static float GetTextBaselineY(float itemTop, float fontSize)
        {
            return itemTop + fontSize;
        }


        // Card-body column breaks driven by MEASURED glyph Y-centers, not the 1-em nominal
        // grid. A nominal estimate (glyphsPerColumn = bodyHeight / FontSize) assumes 1 em of ink per glyph;
        // vertical Manchu glyphs really occupy ~1.5-3 em (+ ink-edge gap), so a "25-per-column" plan at
        // bodyHeight≈800dip would stack ~1200+dip of real ink → the tail clips at bodyBottom, running
        // downward instead of moving to the next column in time.
        // Greedy fill by real cumulative ink height (same measurement DrawShapedText uses → frames agree).
        // yCenters: per-glyph Y centers measured at baseline 0 (only DELTAS are used). emFallback: nominal
        // cell for the half-cell caps at both ends; also the fallback grid when measurement is unavailable.
        // Returns the start glyph index of each column (always contains 0).
        // continuationInsetDip : continuation columns (2nd+) start one letter lower (yielding a cell — the
        // first column sits flush at the body top, continuation columns are visually indented to read as
        // "continued"), so their ink budget is smaller by the inset.
        public static List<int> ComputeCardColumnBreaks(List<float>? yCenters, int glyphCount, float emFallback, float availableBodyHeight, float continuationInsetDip = 0f)
        {
            var starts = new List<int> { 0 };
            if (glyphCount <= 1) return starts;
            if (yCenters == null || yCenters.Count < glyphCount)
            {
                int per = Math.Max(1, (int)Math.Floor(availableBodyHeight / Math.Max(1f, emFallback)));
                for (int g = per; g < glyphCount; g += per) starts.Add(g);
                return starts;
            }
            int s = 0;
            for (int g = 1; g < glyphCount; g++)
            {
                // Column ink height if glyph g joined: first-center→g-center span + half a cell at each end.
                float budget = availableBodyHeight - (starts.Count > 1 ? continuationInsetDip : 0f);
                float colHeight = (yCenters[g] - yCenters[s]) + emFallback;
                if (colHeight > budget)
                {
                    starts.Add(g);
                    s = g;
                }
            }
            return starts;
        }

        // Column index of a glyph under the break list (last start ≤ glyphIndex).
        private static int ColumnOfGlyphIndex(List<int> colStarts, int glyphIndex)
        {
            int col = 0;
            for (int c = 1; c < colStarts.Count; c++)
                if (colStarts[c] <= glyphIndex) col = c; else break;
            return col;
        }

        /// <summary>
        /// Calculate the total height needed for supporting metadata rows.
        /// Returns height for: number row + description row + code-point sequence row.
        /// </summary>
        internal static float GetMetadataRowsTotalHeight()
        {
            // 3 rows with spacing between each
            return (MetadataRowHeight * 3) + (MetadataRowSpacing * 2);
        }

        /// <summary>
        /// Calculate the vertical start position for the primary body region (Row 4).
        /// Metadata rows 1-3 occupy the top region.
        /// </summary>
        internal static float GetBodyRegionTop(float padding)
        {
            return padding + GetMetadataRowsTotalHeight() + MetadataRowSpacing;
        }

        /// <summary>
        /// Calculate vertical offset to center underfilled content within available card height.
        /// </summary>
        internal static float GetVerticalCenteringOffset(float contentHeight, float availableHeight)
        {
            if (contentHeight >= availableHeight) return 0f;
            return (availableHeight - contentHeight) / 2f;
        }

        /// <summary>
        /// Calculate total card height from the same body-region chain that paint uses.
        /// This keeps card assembly and body clip space on one source of truth:
        /// card height = body-region top + primary body height + bottom padding.
        /// </summary>
        internal static float ComputeCardTotalHeight(float primaryContentHeight, float fontSize, float padding)
        {
            _ = fontSize;
            return GetBodyRegionTop(padding) + primaryContentHeight + padding;
        }

        private static WindowWidthMetrics ComputeWindowWidthMetrics(IReadOnlyList<float> cardWidths, float padding)
        {
            if (cardWidths.Count == 0)
            {
                return new WindowWidthMetrics(0f, 0f, 0f, 0f);
            }

            float cardWidthsTotal = 0f;
            for (int i = 0; i < cardWidths.Count; i++)
            {
                cardWidthsTotal += cardWidths[i];
            }

            float outerPaddingWidth = padding * 2f;
            float gapWidthTotal = Math.Max(0, cardWidths.Count - 1) * padding;
            return new WindowWidthMetrics(outerPaddingWidth, gapWidthTotal, cardWidthsTotal, outerPaddingWidth + gapWidthTotal + cardWidthsTotal);
        }


        internal static float ComputeWindowWidthForCardWidths(IReadOnlyList<float> cardWidths, float padding)
        {
            return ComputeWindowWidthMetrics(cardWidths, padding).FinalWindowWidth;
        }

        private void LogWindowWidthComposition(string stage, IReadOnlyList<float> cardWidths, float padding, float requestedTargetWidthDip, int requestedTargetWidthPx, float dpiX, int columnCount)
        {
            ComputeWindowWidthMetrics(cardWidths, padding);
        }

        private void LogResizeAttempt(string stage, int currentWidthPx, int currentHeightPx, float currentWidthDip, float currentHeightDip, int targetWidthPx, int targetHeightPx, float targetWidthDip, float targetHeightDip, bool sizeChanged, bool setWindowPosAttempted, bool setWindowPosSucceeded)
        {
        }

        private static HorizontalWindowLayoutMetrics ComputeHorizontalWindowLayoutMetrics(
            IReadOnlyList<float> lengths,
            IReadOnlyList<float> cardWidths,
            float fontSize,
            float padding,
            float maxWindowHeight)
        {
            int cardCount = Math.Min(lengths.Count, cardWidths.Count);
            float minimumCardHeight = ComputeCardTotalHeight(ComputeItemHeight(0f, fontSize), fontSize, padding);
            if (cardCount == 0)
            {
                return new HorizontalWindowLayoutMetrics(0, 0, minimumCardHeight, minimumCardHeight, minimumCardHeight, 0f, minimumCardHeight);
            }

            float tallestCardHeight = minimumCardHeight;
            for (int i = 0; i < cardCount; i++)
            {
                float cardHeight = ComputeCardTotalHeight(ComputeItemHeight(lengths[i], fontSize), fontSize, padding);
                if (cardHeight > tallestCardHeight)
                {
                    tallestCardHeight = cardHeight;
                }
            }

            int columnCount = cardCount;
            float targetWidth = ComputeWindowWidthForCardWidths(cardWidths, padding);
            float requiredHeight = ComputeWindowHeightForRowCount(1, tallestCardHeight, padding);
            float targetHeight = maxWindowHeight > 0f ? Math.Min(maxWindowHeight, requiredHeight) : requiredHeight;
            return new HorizontalWindowLayoutMetrics(cardCount, columnCount, minimumCardHeight, tallestCardHeight, requiredHeight, targetWidth, targetHeight);
        }


        private bool IsCacheReady()
        {
            // Protected by main thread logic, ensuring consistency
            return _candidates.Count > 0 &&
                _shapedCandidates.Count == _candidates.Count &&
                _candidateLengths.Count == _candidates.Count;
        }

        private bool ShouldPostNow(long nowTicks)
        {
            if (_lastUpdateTick == 0) return true;
            long elapsedMs = (nowTicks - _lastUpdateTick) * 1000 / Stopwatch.Frequency;
            return elapsedMs >= UPDATE_THROTTLE_MS;
        }

        private void ArmUpdateTimer()
        {
            if (_timerArmed || _hwnd == 0) return;
            nuint timer = NativeMethods.SetTimer(_hwnd, UPDATE_TIMER_ID, UPDATE_THROTTLE_MS, 0);
            if (timer == 0)
            {
                int err = Marshal.GetLastPInvokeError();
            }
            else
            {
                _timerArmed = true;
            }
        }

        private void PostUpdate(long nowTicks)
        {
            _lastUpdateTick = nowTicks;
            if (_updatePosted) return;
            bool ok = NativeMethods.PostMessage(_hwnd, WM_APP_UPDATE, 0, 0);
            if (!ok)
            {
                int err = Marshal.GetLastPInvokeError();
            }
            else
            {
                _updatePosted = true;
            }
        }

        private void RequestUpdate(bool force = false)
        {
            if (_hwnd == 0) return;
            long now = Stopwatch.GetTimestamp();
            if (!force && !ShouldPostNow(now))
            {
                _updatePending = true;
                ArmUpdateTimer();
                return;
            }
            PostUpdate(now);
        }

        private void ApplyWindowPosition()
        {
            if (_hwnd == 0) return;
            bool ok = NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, _x, _y, 0, 0, NativeMethods.SWP_NOACTIVATE | 0x0001);
            if (!ok)
            {
                int err = Marshal.GetLastPInvokeError();
            }
            _positionDirty = false;
        }

        private bool HasActiveComposition()
        {
            return !string.IsNullOrEmpty(_engine.GetRawBuffer());
        }

        private static bool CanEnterVisibleState(bool hasCandidates, bool hasActiveComposition, bool resourcesReady, bool cacheReady)
        {
            return hasCandidates && hasActiveComposition && resourcesReady && cacheReady;
        }

        private static bool ShouldPrepareResourcesForActivationUpdate(bool hasWindowHandle, bool hasCandidates, bool hasActiveComposition, bool resourcesReady)
        {
            return hasWindowHandle && hasCandidates && hasActiveComposition && !resourcesReady;
        }

        private static bool CanAcceptFrameReadySignal(bool hasWindowHandle, bool inErrorState)
        {
            return hasWindowHandle && !inErrorState;
        }


        /// <summary> Is the window ACTUALLY on screen — the OS's answer, not ours.
        ///
        /// `_windowVisible` is our record of the last ShowWindow WE called. It is right until something
        /// else changes real visibility, and then it is wrong forever after, because nothing ever corrects
        /// it. Every correctness decision about showing or hiding reads this instead; the cached flag stays
        /// only where it can shortcut work the OS agrees is unnecessary.</summary>
        private bool WindowIsOnScreen() => _hwnd != 0 && NativeMethods.IsWindowVisible(_hwnd);

        // HIDING IS NOT GATED ON WHAT WE REMEMBER.
        //
        // With `if (!_windowVisible) return;` here, a flag saying "already hidden" would skip the hide, so
        // ShowWindow(SW_HIDE) would never be called, and a window the OS put back on screen behind our back
        // would stay there with stale content.
        //
        // A cached bool cannot represent state that something outside this process can change. So this
        // does not ask the flag; it asks the OS.
        //
        // The early-out applies ONLY where both agree there is nothing on screen, which is what makes it
        // an optimisation, not a guard: it can never suppress a hide that would have done something.
        private void HideWindowState(string? reason)
        {
            if (_hwnd == 0)
            {
                _windowVisible = false;
                return;
            }

            bool osVisible = NativeMethods.IsWindowVisible(_hwnd);
            if (!osVisible && !_windowVisible)
            {
                return;   // optimisation: the OS confirms there is nothing to hide
            }


            NativeMethods.ShowWindow(_hwnd, 0); // SW_HIDE

            _windowVisible = false;
        }

        private void ResetLocationState(string? reason)
        {
            _x = DefaultLocationX;
            _y = DefaultLocationY;
            _positionDirty = false;
        }

        private void HandleUpdateMessage()
        {
            _updatePosted = false;
            // Per-CALL, not per-instance: whatever the previous call left behind must
            // not decide this one. Only the gate below may set it.
            _layoutUnstableThisTurn = false;
            if (_hwnd == 0)
            {
                return;
            }
            if (_inErrorState)
            {
                return;
            }
            // Consume the frame the engine DELIVERED to this presenter, not "whatever is newest in
            // the process-global slot". The delivered object is authoritative; GlobalState is only the
            // fallback for paths that never went through the event (timer retries before the first
            // delivery, the windowless layout pass). Either way the frame must pass IsFrameCurrent (owner engine
            // AND generation), which rejects both a frame from another engine and a ghost frame
            // from an earlier composition.
            var frame = System.Threading.Volatile.Read(ref _deliveredFrame) ?? GlobalState.GetLatestFrame();
            if (frame != null && _engine != null && !_engine.IsFrameCurrent(frame))
            {
                frame = null;
            }
            // THE DISPLAY NEVER WALKS BACKWARDS.
            //
            // The owner/epoch check above only proves the frame belongs to THIS composition. It says
            // nothing about ORDER: within one composition an older frame arriving late would be applied on
            // top of a newer one already displayed, and the screen would walk backwards.
            //
            // FrameId will NOT serve as the ordering key: a job enqueued later can legitimately carry an
            // older snapshot (that is exactly what the candidate-rebuild request does), so frame ids rise
            // while the snapshot they describe goes backwards.
            //
            // The authoritative order is the ENGINE'S OWN COMPOSING BUFFER. A frame describes the state
            // the worker was asked about; if that no longer matches what the engine currently holds, the
            // frame is by definition superseded and must not be painted over a newer one. The
            // rule, stated exactly: an older state must never repaint the interface after a newer one.
            //
            // Nothing perceivable is dropped: a frame for a superseded snapshot has obsolete content, and
            // the frame for the current buffer is either already applied or still in flight.
            //
            // Equality with the LIVE buffer is too strong. During a burst the buffer
            // advances faster than frames are produced, so every in-flight frame is already behind it and
            // would be thrown away, and nothing would be drawn while frames kept arriving and being
            // refused. Such a stall is not computation; it is a display discarding what it could show.
            //
            // So the check is MONOTONIC, not exact, and the guarantee holds: a frame
            // not on the path forward is still refused, so an abandoned edit cannot come back and nothing
            // can move the display backwards. The settle signature is keyed the same way, because a
            // fixed wall-clock window would mistake a BOUNDED DRAIN being drawn for work that never ends.
            if (frame != null && _engine != null)
            {
                string live = _engine.GetRawBuffer() ?? string.Empty;
                string carried = frame.RawInput ?? string.Empty;
                string shown = _rawInput ?? string.Empty;
                if (live.Length > 0 && !IsFrameShowable(carried, shown, live))
                {
                    // Before discarding it: a NEWER frame may already have been published while this one
                    // waited in the message queue. Showing the newest available is not the same as
                    // replaying every one that arrived — replaying a state another frame has already
                    // overtaken is work with nothing to show for it.
                    var newest = GlobalState.GetLatestFrame();
                    string newestRaw = newest?.RawInput ?? string.Empty;
                    if (newest != null && _engine.IsFrameCurrent(newest) && IsFrameShowable(newestRaw, shown, live))
                    {
                        frame = newest;
                    }
                    else
                    {
                        frame = null;
                    }
                }
                else if (live.Length > 0) string.Equals(live, carried, StringComparison.Ordinal);
            }
            bool hasCandidates = false;
            if (frame != null)
            {
                _activeFrameId = frame.FrameId;

                if (frame.FrameId > _lastAppliedFrameId) _lastAppliedFrameId = frame.FrameId;   // monotonic

                // Race between flash-hide tick decrement and
                // new-frame arrival. Sequence:
                //   T0: pick → _candidatesFlashHideTicks = 1 + EnqueueFocusChange + RequestUpdate(force)
                //   T1: WM_PAINT (forced) → flash hide consumed (ticks → 0) BUT new candidates not yet
                //       in cache (worker still shaping) → cards NOT drawn
                //   T2: worker pushes new frame → HandleUpdateMessage → InvalidateRect → WM_PAINT
                //   *coalescence*: Win32 may merge T1's pending WM_PAINT with T2's InvalidateRect,
                //   producing a SINGLE paint where flashHideThisCycle is still true → cards skipped
                //   despite new data being in cache.
                // So pending flash-hide ticks are cleared here. By the time a new frame arrives, the
                // visual feedback purpose of flash is satisfied (data changed); no need to suppress.
                if (_candidatesFlashHideTicks > 0 && frame.Candidates.Count > 0)
                {
                    _candidatesFlashHideTicks = 0;
                }

                _shapedCandidates = frame.ShapedResults;
                _candidateLengths = frame.CandidateLengths;
                _candidates = frame.Candidates;
                // Store semantic items for supporting (metadata) row rendering
                _semanticItems = frame.SemanticItems;
                // Pull per-candidate slice list.
                _slicesAtFocus = frame.SlicesAtFocus ?? new List<string>();
                // THE IDENTITY OF THESE CANDIDATES, recorded WITH them.
                // Candidates/SlicesAtFocus/ShapedResults are the candidates OF ONE UNIT — frame.FocusResolved
                // says which. Storing it here, in the same assignment block, is the whole point: the index
                // and the payload cannot drift apart, because nothing can update one without the other.
                _candidateTargetIndex = frame.FocusResolved;
                _upem = frame.Upem; // store the UPEM for rendering
                // Backend-sourced Latin echo refresh + focus lifecycle reset.
                // Frame.RawInput is source-consistent with ShapedResults (single atomic frame swap).
                // When input changes (user typed/erased), focus resets to "follow last" (the default).
                string newRawInput = frame.RawInput ?? "";
                if (newRawInput != _rawInput)
                {
                    string prevRawInput = _rawInput ?? string.Empty;
                    _rawInput = newRawInput;
                    _userScrollOverride = false;   // typing resumes auto-follow (tail/focus)
                    // unit view derives lazily from _rawInput (EnsureUnitsCache).
                    // On an append or a tail delete the focus **follows** to the new last item, and the
                    // sentinel resolves to the tail. Everything before is taken as already chosen for the user,
                    // unless they go back to choose themselves, so the item just typed is the one the user is
                    // looking at, and the band belongs on it. Moving the FOCUS costs the user nothing they had
                    // chosen: append only adds at the tail, 0..n-1 keep their indices, and the engine's
                    // PruneStaleSelections keeps the earlier picks.
                    //
                    // The release below is confined to append / tail-erase. insertedInPlace
                    // keeps the focus where it is.
                    bool appended = prevRawInput.Length > 0
                        && newRawInput.Length > prevRawInput.Length
                        && newRawInput.StartsWith(prevRawInput, StringComparison.Ordinal);
                    // A tail delete (Backspace removing input characters from the end, so the new string
                    // is a true prefix of the previous one) works like an append. A mid-string edit or a
                    // whole-string replacement still goes through ResetFocus.
                    bool erasedTail = prevRawInput.Length > 0
                        && newRawInput.Length < prevRawInput.Length
                        && prevRawInput.StartsWith(newRawInput, StringComparison.Ordinal);
                    // A MID-WORD INSERTION IS NOT A NEW WORD.
                    //
                    // `appended` requires the new text to START WITH the old one, so a separator or a
                    // Shift adapter typed in the MIDDLE fails it. Without insertedInPlace it would fall
                    // through to the else branch and be treated as "the input changed beyond recognition"
                    // → ResetFocus → sentinel, and the user would lose the focused item.
                    //
                    // But nothing about the sequence was replaced: one contiguous run was inserted and
                    // everything else is exactly where it was. The units BEFORE the insertion keep their
                    // indices, and InsertAtFocus puts the mark at the focused unit's trailing edge, so the
                    // focused unit keeps its index too. Keeping the focus is therefore not a guess — it is
                    // the only reading under which the index still means what it meant.
                    //
                    // When the insertion creates an item of its own, the focus stays on the ORIGINAL item,
                    // which is the choice that changes nothing. It must not jump to the first item, which is
                    // neither.
                    bool insertedInPlace = IsSingleContiguousInsertion(prevRawInput, newRawInput);
                    if (appended || erasedTail || insertedInPlace)
                    {
                        int rc = GetCurrentRowCount();
                        if (appended || erasedTail)
                        {
                            // Release a manually-moved focus so the band follows the tail.
                            // Sentinel rather than rc-1 for the same reason ManjuEngine.AddInput uses -1:
                            // it keeps the "no manual focus" state single-valued, so the band,
                            // the candidate cards and the engine all reach the tail through ONE rule
                            // instead of three places each having pinned their own copy of it.
                            if (_focusedRowIndex >= 0) SetFocusedRow(-1);
                        }
                        else if (_focusedRowIndex >= rc) SetFocusedRow(rc - 1);   // out-of-range clamp (insertedInPlace; rc=0 → the -1 sentinel)
                        // After an append or a tail delete, **always** rebuild the candidate column from the
                        // resolved focus, the sentinel included: without the enqueue its candidates stay on the
                        // old snapshot. EnqueueFocusChange only refreshes candidates; it does not change focus semantics.
                        //
                        // The model is two INDEPENDENT states: selection focus may sit on item 1 while the
                        // visible content follows the latest input, so a sentinel focus resolves to the latest
                        // unit here. Resolving it to 0 would bind the two together: after typing 80 characters
                        // the candidate column would show the candidates of character 1, and the user could not
                        // see what they are typing.
                        //
                        // The Preview grid uses the same dual-state form:
                        //   scrollTarget = (_focusedRowIndex >= 0) ? focusSlot : slotCount - 1
                        // and this is the single point where the candidate column is requested. Preview's scroll
                        // target and Candidate's content are derived from the same rule, in the same tick,
                        // from the same append event: one driver, not two.
                        //
                        // Not SetFocusedRow: that publishes selection focus. EnqueueFocusChange only
                        // rebuilds the candidate column, so _focusedRowIndex stays sentinel and the arrow
                        // keys still start from where the user left them.
                        int latestUnit = rc - 1;                  // the item the user has just typed
                        int resolvedFocus = (_focusedRowIndex >= 0) ? _focusedRowIndex : (rc > 0 ? latestUnit : -1);
                        if (resolvedFocus >= 0)
                        {
                            // THE EDGE THAT CAN CLOSE A LOOP.
                            //
                            // This branch asks the engine to rebuild the candidate column after an
                            // append / tail-erase. It fires whenever the ARRIVING FRAME's raw input
                            // differs from the presenter's, and frames are exactly what change the
                            // presenter's raw input. So the trigger condition can satisfy itself:
                            //
                            //   frame(X) consumed -> raw differs -> EnqueueFocusChange(X)
                            //     -> worker job -> frame(X') published -> raw differs -> ...
                            //
                            // Each turn would consume one job and produce one job, so the queue would never
                            // drain and the composition would walk a closed cycle of snapshots forever, with
                            // no key pressed. That is a repeated append-and-rollback loop, and the window
                            // shakes: every turn is a real relayout of a real new state.
                            //
                            // The loop is cut where frames are ACCEPTED (see the superseded-frame guard in
                            // HandleUpdateMessage), not where work is requested: a stale frame cannot be
                            // adopted, so it never reaches this branch. No per-(snapshot, focus) dedupe of
                            // this request is needed for that, and one would quietly narrow the candidate
                            // refresh.
                            //
                            // Monotonic acceptance does reach this branch on every intermediate frame, and
                            // each request would ask the engine to catch up to a state the engine is
                            // ALREADY PAST. That echo is work with nothing to show for it, and it keeps
                            // a fast burst from draining: jobs stay queued long after the last key.
                            //
                            // So: ask only when the engine is NOT already ahead of what was just shown. If
                            // the live buffer extends it, the frames that will show the rest are already on
                            // their way and nothing needs requesting. This narrows a REQUEST, never a
                            // display — an intermediate frame is still shown; it just does not ask for work
                            // that is already in flight.
                            string liveNow = _engine?.GetRawBuffer() ?? string.Empty;
                            string shownNow = _rawInput ?? string.Empty;
                            bool engineAlreadyAhead = liveNow.Length > shownNow.Length
                                && liveNow.StartsWith(shownNow, StringComparison.Ordinal);
                            if (!engineAlreadyAhead)
                            { if (!(frame != null && frame.FocusResolved == resolvedFocus))
                            {
                                try { _engine?.EnqueueFocusChange(shownNow, resolvedFocus); } catch { }
                            } }
                        }
                    }
                    else if (_focusedRowIndex != -1)
                    {
                        SetFocusedRow(-1);
                    }
                }
                // Mark the layout dirty only when an input that **really affects layout** changed.
                //
                // Setting _layoutDirty = true on every update message would run CalculateAndResizeWindow()
                // every time, which may SetWindowPos or resize the render target. It would recompute
                // even when a timer retry consumes the same frame again, so the window's size and position
                // would jitter over identical content, which is a flicker. Fast consecutive typing and a
                // longer sequence both make it worse: each raises the density of messages and retries.
                //
                // Only LAYOUT is gated; repainting (_dataDirty / InvalidateRect) is not, so every real
                // update is painted, including the Preview's focus scrolling, which is computed at paint time.
                // Gate on the COMPLETE immutable snapshot compared by value
                // (a digest is not a snapshot).
                // Unstable is its OWN outcome. Collapsed into "unchanged", a torn capture would be safe but
                // SILENT: nothing would stay dirty, nothing would be rescheduled, and the window could keep
                // the previous frame's geometry until some unrelated later event happened to repaint it.
                switch (LayoutInputsChanged())
                {
                    case LayoutGateResult.Changed:
                        _lastLayoutSignature = ComputeLayoutSignature();
                        _layoutDirty = true;
                        break;
                    case LayoutGateResult.Unstable:
                        _layoutDirty = true;          // keep the intent…
                        _layoutUnstableThisTurn = true;   // …and do NOT let this turn consume the OLD snapshot
                        ScheduleLayoutRetry();        // …and make sure something comes back for it
                        break;
                    default:
                        break;
                }
                hasCandidates = _candidates.Count > 0;
            }
            else
            {
                // "NO NEW FRAME" IS NOT "NO CANDIDATES".
                //
                // hasCandidates starts at false. If this branch left it there, a turn that merely found
                // nothing new to draw would fall straight through to the hide below and report "no
                // candidates", and every miss would be a blink. The legitimate
                // hide has its own route (Worker: empty frame -> WM_APP_HIDE, on Esc and commit) and is not
                // involved here; two different facts must not drive the same action.
                //
                // A miss happens for two reasons and neither means the candidates are gone: the frame was
                // already consumed by an earlier turn, or it was refused above as superseded. What the
                // presenter is showing is still valid, so the answer is what it is holding.
                hasCandidates = _candidates.Count > 0;
            }

            if (_positionDirty)
            {
                ApplyWindowPosition();
            }

            bool hasActiveComposition = HasActiveComposition();
            bool resourcesReady = _renderTarget != null && _fontFace != null;
            bool shouldPrepareResources = ShouldPrepareResourcesForActivationUpdate(_hwnd != 0, hasCandidates, hasActiveComposition, resourcesReady);
            if (shouldPrepareResources)
            {
                EnsureResources();
                resourcesReady = _renderTarget != null && _fontFace != null;
            }

            bool cacheReady = IsCacheReady();
            bool canEnterVisibleState = CanEnterVisibleState(hasCandidates, hasActiveComposition, resourcesReady, cacheReady);

            if (!canEnterVisibleState)
            {
                // `|| WindowIsOnScreen()`: the second copy of the same guard. Checking only the cached
                // flag here would decide not to call HideWindowState at all, and the window
                // would still be sitting on the screen.
                if (_windowVisible || WindowIsOnScreen())
                {
                    string? hiddenReason = null;
                    HideWindowState(hiddenReason);
                }

                if (!hasCandidates)
                {
                    // The composition this retry sequence was chasing is gone. Cancel here,
                    // not only at window teardown: a retry budget and a live OS timer must not survive
                    // into the NEXT composition.
                    CancelLayoutRetriesForLifecycleChange(null);
                    return;
                }

                if (!hasActiveComposition)
                {
                    CancelLayoutRetriesForLifecycleChange(null);
                    return;
                }

                if (shouldPrepareResources && !resourcesReady)
                {
                    return;
                }

                if (!cacheReady)
                {
                    return;
                }

                return;
            }

            // If THIS turn could not read the layout inputs, it must not fall through
            // and lay out from the previously approved snapshot. Falling through would set dirty,
            // schedule a retry, then a few lines later pick `_lastLayoutSnapshot`, computed with the
            // OLD geometry, report success, clear dirty and reset the retry budget: the call would undo its
            // own recovery.
            if (_layoutUnstableThisTurn)
            {
                _layoutUnstableThisTurn = false;
            }
            else if (_layoutDirty)
            {
                // Resize from the EXACT snapshot the gate approved. _lastLayoutSnapshot is
                // written by LayoutInputsChanged() at the moment the gate said "this differs"; passing
                // it here removes the window in which live state could drift between decision and
                // computation. Null only if the gate never ran (defensive capture).
                var snap = _lastLayoutSnapshot ?? CaptureLayoutSnapshotStable();
                if (snap == null)
                {
                    // Fail closed here too: no verified snapshot ⇒ no resize this turn,
                    // and the dirty flag STAYS set so the next update retries rather than dropping it.
                    // Staying dirty is not enough on its own — something has to come
                    // back. Schedule the retry instead of waiting for an unrelated event.
                    ScheduleLayoutRetry();
                }
                else
                {
                    // The layout reports whether it COMPLETED. It returns early for a
                    // rejected font identity, an unstable re-snapshot or a caught exception; clearing
                    // the dirty flag regardless silently loses the pending layout on those turns.
                    if (CalculateAndResizeWindow(snap))
                    {
                        _layoutDirty = false;
                    }
                    else
                    {
                        ScheduleLayoutRetry();
                    }
                }
            }

            // The mirror of the hide case: if the OS hid the window behind our back while the flag still
            // said visible, a check on the flag alone would never run this branch and the window would
            // never come back. Reaching here already means
            // the update path decided the window SHOULD be visible,
            // so calling ShowWindow when the OS says it is not on screen cannot resurrect anything.
            if (!_windowVisible || !NativeMethods.IsWindowVisible(_hwnd))
            {
                NativeMethods.ShowWindow(_hwnd, 8); // SW_SHOWNA
                _windowVisible = true;
            }

            NativeMethods.InvalidateRect(_hwnd, 0, true);
        }

        public void SetLocation(int x, int y)
        {
            if (_inErrorState) return;

            try 
            {
                _x = x;
                _y = y;
                _positionDirty = true;
                if (_hwnd != 0)
                {
                    RequestUpdate();
                }
            } 
            catch (Exception)
            {
                _inErrorState = true;
            }
        }

        private unsafe void RegisterWindowClass()
        {
            if (_classAtom != 0) return;

            try
            {
                nint hInst = NativeMethods.GetModuleHandle(null);
                // Use static function pointer
                
                fixed (char* pClassName = _className)
                {
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
                    WNDCLASSEX wndClass = new ()
                    {
                        Size_CbSize = ( UInt32 ) Marshal.SizeOf<WNDCLASSEX>(),
                        Style_Style = 0x0003 | CS_DROPSHADOW | CS_IME, // CS_HREDRAW | CS_VREDRAW | CS_DROPSHADOW | CS_IME
                        Ptr_LpfnWndProc = _sharedWndProcPtr,
                        Handle_HInstance = hInst,
                        Name_LpszClassName = (nint)pClassName,
                        Handle_HbrBackground = 0
                    };
#pragma warning restore CS8600
                    
                    _classAtom = NativeMethods.RegisterClassEx(&wndClass);
                    if (_classAtom == 0)
                    {
                        int err = Marshal.GetLastPInvokeError();
                    }

                    // initialise the D2D factory
                    // ONE factory for the whole process, NOT one per presenter.
                    // D2D1CreateFactory must not be called here, which would give every presenter a
                    // private factory. A D3D device belongs to the
                    // FACTORY, so one factory with ten render targets costs 16 threads ONCE, while ten
                    // factories with one render target each cost 15 apiece (150) and never give them
                    // back. Per-presenter factories can grow the thread count until the process crashes.
                    {
                        _d2dFactory = GetSharedD2DFactory();
                    }

                }
            }
            catch (Exception)
            {
            }
        }

        private unsafe void PrewarmDWriteFactory()
        {
            // Executed synchronously on UI thread
            // File-based loading only; memory-based loading has COM deadlock issues
            try
            {
                string? fontPath = _engine.GetFontFilePath();
                if (fontPath != null)
                {
                    lock (_resourceLock)
                    {
                        _fontFace = ManjuDirectWriteRenderer.CreateFontFaceFromFile(fontPath);
                        if (_fontFace != null)
                        {
                            _nativeFontFace = (nint)ComInterfaceMarshaller<NativeMethods.IDWriteFontFace>.ConvertToUnmanaged(_fontFace);
                            BumpFontGeneration();   // the pointer alone is not an identity
                        }
                    }
                }
            }
            catch (Exception)
            {
                _inErrorState = true;
            }
        }

        private unsafe void EnsureResources()
        {
            if (_inErrorState) return;
            if (_hwnd == 0 || _d2dFactory == null) return;
            
            // Fast check without lock
            // ReferenceEquals, not just "not null": the brushes below are created BY a
            // render target and mean nothing with a different one.
            if (_renderTarget != null && _fontFace != null && ReferenceEquals(_brushTarget, _renderTarget)) return;

            lock (_resourceLock)
            {
                // Double-check inside lock
                if (_renderTarget != null && _fontFace != null && ReferenceEquals(_brushTarget, _renderTarget)) return;

                var factory = _d2dFactory;
                if (factory == null) return;

                NativeMethods.ID2D1HwndRenderTarget? tempRenderTarget = null;
                NativeMethods.ID2D1SolidColorBrush? tempTextBrush = null;
                NativeMethods.ID2D1SolidColorBrush? tempBorderBrush = null;
                NativeMethods.ID2D1SolidColorBrush? tempHighlightBrush = null;
                // Brushes for the 3-tier hierarchy.
                NativeMethods.ID2D1SolidColorBrush? tempSelectedCardHighlightBrush = null;
                NativeMethods.ID2D1SolidColorBrush? tempInnerGlyphHighlightBrush = null;
                NativeMethods.ID2D1SolidColorBrush? tempFocusGlyphOverlayBrush = null;
                // Main-window dim overlay when knowledge window has focus.
                NativeMethods.ID2D1SolidColorBrush? tempUnfocusedOverlayBrush = null;
                NativeMethods.IDWriteFontFace? tempFontFace = null;

                try
                {

                    RECT rect;
                    NativeMethods.GetClientRect(_hwnd, out rect);
                    uint width = (uint)(rect.Right - rect.Left);
                    uint height = (uint)(rect.Bottom - rect.Top);

                    // Prevent 0x0 RenderTarget
                    if (width == 0) width = 1;
                    if (height == 0) height = 1;

                    // Only recreate RenderTarget if missing
                    if (_renderTarget == null)
                    {
                        D2D1_RENDER_TARGET_PROPERTIES rtProps = new D2D1_RENDER_TARGET_PROPERTIES();
                        D2D1_HWND_RENDER_TARGET_PROPERTIES hwndProps = new D2D1_HWND_RENDER_TARGET_PROPERTIES {
                            hwnd = _hwnd,
                            pixelSize = new D2D1_SIZE_U { width = width, height = height },
                            presentOptions = D2D1_PRESENT_OPTIONS.D2D1_PRESENT_OPTIONS_NONE
                        };

                        int hr = factory.CreateHwndRenderTarget(rtProps, hwndProps, out tempRenderTarget);
                        if (hr != 0 || tempRenderTarget == null)
                        {
                             return;
                        }

                        factory.GetDesktopDpi(out float desktopDpiX, out float desktopDpiY);
                        tempRenderTarget.SetDpi(NormalizeDpi(desktopDpiX), NormalizeDpi(desktopDpiY));

                        // Brush colors come from the active theme (_themes[_themeIndex]).
                        // [0] = the default scheme.
                        // _bgColor feeds Clear(); CycleTheme() later recolors these brushes in-place via SetColor.
                        var th = _themes[(_themeIndex >= 0 && _themeIndex < _themes.Length) ? _themeIndex : 0];
                        _bgColor = th.Bg;
                        tempRenderTarget.CreateSolidColorBrush(th.Text, 0, out tempTextBrush);
                        tempRenderTarget.CreateSolidColorBrush(th.Border, 0, out tempBorderBrush);
                        tempRenderTarget.CreateSolidColorBrush(th.Band, 0, out tempHighlightBrush);          // Layer 1 row band
                        tempRenderTarget.CreateSolidColorBrush(th.Card, 0, out tempSelectedCardHighlightBrush); // Layer 2 selected card
                        tempRenderTarget.CreateSolidColorBrush(th.Grid, 0, out tempInnerGlyphHighlightBrush);  // Layer 3a manuscript-grid line
                        tempRenderTarget.CreateSolidColorBrush(th.FocusGlyph, 0, out tempFocusGlyphOverlayBrush); // Layer 3b focus-glyph tint
                        tempRenderTarget.CreateSolidColorBrush(th.Unfocused, 0, out tempUnfocusedOverlayBrush); // KW-focus dim overlay
                        tempRenderTarget.CreateSolidColorBrush(th.Ribbon, 0, out var tempRibbonBrush);        // first-column ribbon
                        tempRenderTarget.CreateSolidColorBrush(DimOf(th.Text), 0, out var tempMetaDimBrush);  // row 3, de-emphasised
                        tempRenderTarget.CreateSolidColorBrush(OpaqueOf(th.Bg), 0, out var tempSettingsPanelBrush); // overlay backing panel

                        _renderTarget = tempRenderTarget;
                        _brushTarget = tempRenderTarget;      // the brushes below belong to THIS one
                        _brushThemeIndex = _themeIndex;
                        _textBrush = tempTextBrush;
                        _borderBrush = tempBorderBrush;
                        _highlightBrush = tempHighlightBrush;
                        _selectedCardHighlightBrush = tempSelectedCardHighlightBrush;
                        _innerGlyphHighlightBrush = tempInnerGlyphHighlightBrush;
                        _focusGlyphOverlayBrush = tempFocusGlyphOverlayBrush;
                        _unfocusedOverlayBrush = tempUnfocusedOverlayBrush;
                        _ribbonBrush = tempRibbonBrush;
                        _metaDimBrush = tempMetaDimBrush;
                        _settingsPanelBrush = tempSettingsPanelBrush;

                        // Cache native pointers
                        if (_textBrush != null)
                            _nativeTextBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_textBrush);
                        if (_borderBrush != null)
                            _nativeBorderBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_borderBrush);
                        if (_highlightBrush != null)
                            _nativeHighlightBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_highlightBrush);
                        if (_selectedCardHighlightBrush != null)
                            _nativeSelectedCardHighlightBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_selectedCardHighlightBrush);
                        if (_innerGlyphHighlightBrush != null)
                            _nativeInnerGlyphHighlightBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_innerGlyphHighlightBrush);
                        if (_focusGlyphOverlayBrush != null)
                            _nativeFocusGlyphOverlayBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_focusGlyphOverlayBrush);
                        if (_unfocusedOverlayBrush != null)
                            _nativeUnfocusedOverlayBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_unfocusedOverlayBrush);
                        if (_ribbonBrush != null)
                            _nativeRibbonBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_ribbonBrush);
                        if (_metaDimBrush != null)
                            _nativeMetaDimBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_metaDimBrush);
                        if (_settingsPanelBrush != null)
                            _nativeSettingsPanelBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_settingsPanelBrush);
                    }

                    // Attempt to create FontFace if missing (and not exceeded retries)
                    // File-based loading ONLY; memory-based loading has COM deadlock issues
                    if (_fontFace == null && _createFontFaceAttempts < MAX_CREATE_ATTEMPTS)
                    {
                        _createFontFaceAttempts++;

                        String? fontPath = _engine.GetFontFilePath();
                        if (fontPath != null)
                        {
                            tempFontFace = ManjuDirectWriteRenderer.CreateFontFaceFromFile(fontPath);
                        }

                        if (tempFontFace == null)
                        {
                            if (_createFontFaceAttempts >= MAX_CREATE_ATTEMPTS)
                            {
                                _inErrorState = true;
                            }
                        }
                        else
                        {
                            _fontFace = tempFontFace;
                            _nativeFontFace = (nint)ComInterfaceMarshaller<NativeMethods.IDWriteFontFace>.ConvertToUnmanaged(_fontFace);
                            BumpFontGeneration();   // the pointer alone is not an identity
                        }
                    }
                }
                catch (Exception)
                {
                    // Don't set error state on transient errors unless severe
                }
            }
        }

        // A theme's name.
        public static string ThemeName(int i) => _themes[(i >= 0 && i < _themes.Length) ? i : 0].Name;


        // ══════════════════════════════════════════════════════════════════════════════════════════
        // THE SHARED D2D FACTORY
        //
        // A D3D DEVICE BELONGS TO THE FACTORY, NOT TO THE RENDER TARGET. Thread cost by configuration:
        //
        //   ten factories, no render target        0 threads   (a factory alone is free)
        //   ONE factory, ten render targets       +16 total    (first one pays, the next nine are free)
        //   ten factories, one render target each +150         (15.0 apiece, exactly linear)
        //
        // If each presenter called D2D1CreateFactory for itself, every presenter that rendered would
        // bring up its own D3D device and its driver's worker pool (about one thread per core), and
        // nothing in the process gives those threads back: not Dispose, not a GC with its finalizers,
        // not a ComObject.FinalRelease of the render target, the factory and every brush. Across
        // many presenters the thread count keeps climbing until the process dies.
        //
        // MULTI_THREADED is what makes sharing legitimate: D2D serialises calls through a factory
        // created this way, which is exactly why the type exists.
        // It is never released — one factory for the life of the process is the intended lifetime,
        // and releasing it in any one presenter's Dispose would break every other presenter.
        // ══════════════════════════════════════════════════════════════════════════════════════════
        private static NativeMethods.ID2D1Factory? s_sharedD2DFactory;
        private static readonly object s_sharedD2DFactoryLock = new object();

        internal static unsafe NativeMethods.ID2D1Factory? GetSharedD2DFactory()
        {
            var existing = s_sharedD2DFactory;
            if (existing != null) return existing;

            lock (s_sharedD2DFactoryLock)
            {
                if (s_sharedD2DFactory != null) return s_sharedD2DFactory;
                fixed (Guid* iid = &IID_ID2D1Factory)
                {
                    // Use MULTI_THREADED factory
                    int hr = NativeMethods.D2D1CreateFactory(
                        D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, iid, 0,
                        out NativeMethods.ID2D1Factory created);
                    if (hr != 0)
                    {
                        return null;
                    }
                    s_sharedD2DFactory = created;
                }
                return s_sharedD2DFactory;
            }
        }


        public void EnsureWindowCreated()
        {
            if (_hwnd != 0) return;
            if (_inErrorState) 
            {
                return; // Fail fast
            }
            
            nint hInst = NativeMethods.GetModuleHandle(null);
            
            // Allocate GCHandle
            if (!_selfHandle.IsAllocated)
            {
                _selfHandle = GCHandle.Alloc(this);
            }
            
            // Use Atom if available for best reliability
            if (_classAtom != 0)
            {
                _hwnd = NativeMethods.CreateWindowEx(
                    NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW,
                    (nint)_classAtom, 
                    "ManjuCandidates", NativeMethods.WS_POPUP,
                    _x, _y, 200, 500, 0, 0, hInst, 0);
            }
            else
            {
                _hwnd = NativeMethods.CreateWindowEx(
                    NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOPMOST | NativeMethods.WS_EX_TOOLWINDOW,
                    _className, "ManjuCandidates", NativeMethods.WS_POPUP,
                    _x, _y, 200, 500, 0, 0, hInst, 0);
            }
                
            if (_hwnd == 0)
            {
                int err = Marshal.GetLastPInvokeError();
                _inErrorState = true;
                if (_selfHandle.IsAllocated) _selfHandle.Free();
                return;
            }

            // Set USERDATA
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));

            // Now that there is a window thread to marshal through, route frame delivery
            // through it. Until this point delivery is inline (best-effort); from here on a subscriber is
            // only ever entered on this thread, which is the thread TSF cancels compositions on — so a
            // cancelled composition can no longer be delivered at all.
            // Only route through this presenter once it actually HAS a window.
            // Installing unconditionally would let a presenter with no HWND accept deliveries and
            // retain them forever: nothing runs off-thread, but nothing is ever painted either.
            // With no dispatcher the engine falls back to its own rule: a Caller-origin publish runs
            // inline (safe, same thread), a Worker-origin publish is dropped fail-closed.
            if (_hwnd != 0) InstallFrameDeliveryDispatcher();

        }

        public void Update_Candidates(List<string> newList)
        {
            newList ??= new List<string>();

            // THE REAL COMPOSITION-END CHOKEPOINT. Putting the cancel in the two
            // HandleUpdateMessage abort branches is not enough, because every normal end runs
            // TextService.ResetCandidateAnchoringCycle() → Update_Candidates(empty) → _layoutDirty=false
            // → PostHide(), which never enters HandleUpdateMessage at all. So without this cancel a live
            // OS timer and a retry budget would survive the end of the word and could drive a retry for the
            // NEXT one.
            if (newList.Count == 0)
            {
                CancelLayoutRetriesForLifecycleChange(null);
                _lastAppliedFrameId = 0;   // new word, new ordering domain
            }

            // Reset error state if starting a new composition cycle or recovering
            if (newList.Count > 0 && (_candidates.Count == 0 || _inErrorState))
            {
                ResetErrorState();
            }

            _candidates = newList;
            
            // Auto-select first candidate
            if (_candidates.Count > 0)
            {
                _selectedIndex = 0;
            }

            int signature = ComputeCandidateSignature(_candidates, _selectedIndex);
            bool candidatesChanged = signature != _lastCandidateSignature;
            if (candidatesChanged)
            {
                _lastCandidateSignature = signature;
                _dataDirty = true;
                _layoutDirty = true;
            }

            if (_candidates.Count > 0)
            {
                EnsureWindowCreated();
                if (_hwnd == 0) return;

                // Visibility is handled in HandleUpdateMessage with resource guards.
                // We just ensure an update is requested.
                if (candidatesChanged || _positionDirty || _layoutDirty || _dataDirty || !_windowVisible)
                {
                    RequestUpdate();
                }
            }
            else
            {
                ResetLocationState(null);
                _dataDirty = false;
                _layoutDirty = false;
                
                // Clear cache (Sync)
                lock (_resourceLock)
                {
                    _shapedCandidates.Clear();
                    _candidateLengths.Clear();
                }

                if (_hwnd != 0)
                {
                    // Use PostHide for consistent state machine transition
                    PostHide();
                }
            }
        }


        public void AdjustSelectedIndex(int delta)
        {
            if (_candidates == null || _candidates.Count == 0) return;
            int newIndex = Math.Clamp(_selectedIndex + delta, 0, _candidates.Count - 1);
            if (newIndex == _selectedIndex) return;
            _selectedIndex = newIndex;
            if (_hwnd != 0) NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
        }

        // PgUp/PgDn page the PREVIEW viewport (PagePreviewViewport, below); candidate navigation uses
        // the arrow keys and the digit row.

        /// <summary> PgUp / PgDn page the PREVIEW viewport.
        ///
        /// The Preview grid has a vertical viewport: _prefixScrollOffset over DisplaySlotCount rows, with
        /// the visible capacity recomputed on every paint. Paging what actually scrolls is the right home
        /// for the keys.
        ///
        /// The contract, all four parts:
        ///   PgDn  viewport forward one page - ITS OWN visible-row capacity, not a constant
        ///         focus lands on the FIRST unit of the new page
         ///         the candidate window follows via the same EnqueueFocusChange the arrow keys and clicks
        ///         use, so this does not add a second focus mechanism
        ///   PgUp  the same, backwards
        ///   both  clamp at the ends, no wrap
        ///
        /// Returns true when the viewport actually moved.</summary>
        public bool PagePreviewViewport(int direction)
        {
            if (direction == 0) return false;
            EnsureUnitsCache();
            int slots = DisplaySlotCount;
            int visible = Math.Max(1, _lastVisibleRowsForScroll);
            if (slots <= 0) return false;
            if (slots <= visible)
            {
                return false;
            }
            int newOffset = ApplyManualScrollDelta(_prefixScrollOffset, direction * visible, slots, visible);
            if (newOffset == _prefixScrollOffset)
            {
                return false;
            }
            _prefixScrollOffset = newOffset;

            // Focus the first UNIT of the new page. Separator slots carry unit -1 and are not focusable,
            // so walk forward to the first real unit instead of landing focus on a display-only row.
            int focusUnit = -1;
            for (int slot = newOffset; slot < _displaySlotUnit.Count && slot < newOffset + visible; slot++)
                if (_displaySlotUnit[slot] >= 0) { focusUnit = _displaySlotUnit[slot]; break; }

            if (focusUnit >= 0)
            {
                _focusedRowIndex = focusUnit;
                try { _engine?.EnqueueFocusChange(_rawInput ?? string.Empty, focusUnit); } catch { }
            }
            if (_hwnd != 0) NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
            return true;
        }


        // [ROTATION-DIAG] Force repaint without changing selection
        public void ForceRepaint()
        {
            if (_hwnd != 0) NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
        }

        // Four-column UI focus state public API.
        //
        // TextService's key path calls Presenter_Candidate.TryHandleNavigationKey(virtualKey):
        //   - returns true  → pfEaten=1 and standard composition routing is skipped
        //   - returns false → the standard TSF flow continues (composition append / commit / etc.)
        // With no focus established, the presenter's focus state defaults to "follow last" (the
        // default while typing).

        /// <summary>
        /// Pure predicate: would the four-column UI consume this virtual-key for focus
        /// navigation? No side effects. Used by TextService.OnTestKeyDown to claim ownership.
        /// </summary>
        public bool WouldHandleNavigationKey(uint virtualKey)
        {
            int rowCount = GetCurrentRowCount();
            if (rowCount <= 0) return false;

            // Two-axis key map. Both axes are always live.
            //   ↑ (VK_UP=0x26) / ↓ (VK_DOWN=0x28) → outer focus (which char)
            //   ← (VK_LEFT=0x25) / → (VK_RIGHT=0x27) → inner highlight (which candidate)
            //   0-9 main row (VK_0..VK_9 = 0x30..0x39) → explicit pick at focus (0=10th)
            //   Numpad 0-9 (VK_NUMPAD0..VK_NUMPAD9 = 0x60..0x69) → same as main row
            if (virtualKey == 0x26 || virtualKey == 0x28) return true; // ↑↓
            if (virtualKey == 0x25 || virtualKey == 0x27) return true; // ←→
            if (virtualKey >= 0x30 && virtualKey <= 0x39) return true; // VK_0..VK_9
            if (virtualKey >= 0x60 && virtualKey <= 0x69) return true; // VK_NUMPAD0..VK_NUMPAD9
            return false;
        }

        /// <summary>
        /// Two-axis key handling. ↑↓ outer (which character), ←→ inner (which
        /// candidate), 0-9 (main + numpad) explicit pick. Returns true if consumed.
        /// </summary>
        public bool TryHandleNavigationKey(uint virtualKey)
        {
            // When the knowledge window owns focus (after Tab), do NOT consume keys for the
            // MAIN candidate window — defer to the engine, which routes arrows to the KW highlight and digits
            // to KW word-commit. Without this the main window would eat the arrows after Tab.
            try { if (_engine?.DictionaryModule?.HasFocusContext == true) return false; } catch { }
            int rowCount = GetCurrentRowCount();
            if (rowCount <= 0) return false;

            // ↑↓: outer focus
            if (virtualKey == 0x26) { MoveFocus(-1); return true; }
            if (virtualKey == 0x28) { MoveFocus(+1); return true; }

            // ←→: inner highlight (NOT a pick)
            if (virtualKey == 0x25) { MoveInnerHighlight(-1); return true; }
            if (virtualKey == 0x27) { MoveInnerHighlight(+1); return true; }

            // 0-9 main row: explicit pick. Map: VK_1..VK_9 → idx 0..8, VK_0 → idx 9.
            if (virtualKey >= 0x30 && virtualKey <= 0x39)
            {
                int idx = (virtualKey == 0x30) ? 9 : (int)(virtualKey - 0x31);
                ExplicitPickAtFocus(idx);
                return true;
            }
            // VK_NUMPAD0..VK_NUMPAD9 → same mapping
            if (virtualKey >= 0x60 && virtualKey <= 0x69)
            {
                int idx = (virtualKey == 0x60) ? 9 : (int)(virtualKey - 0x61);
                ExplicitPickAtFocus(idx);
                return true;
            }
            return false;
        }


        // Explicit pick at the currently focused outer position.
        // - Marks position as selected with given highlight idx (engine-side)
        // - Flash hide candidate cards 1 frame (visual feedback)
        // - Smart auto-advance: K+1 unselected → advance to K+1; K+1 already selected, or K is the
        //   last position → stay; every position selected → trigger commit-to-host
        public void ExplicitPickAtFocus(int highlightIdx)
        {
            int rowCount = GetCurrentRowCount();
            if (rowCount <= 0) return;
            // The item being decided is the one whose candidates are on screen. See
            // ResolveWritebackTarget: ResolveFocusIndex(rowCount) is the selection focus,
            // a DIFFERENT state, and using it would let a pick land on the wrong unit.
            int currentFocus = ResolveWritebackTarget();
            if (currentFocus < 0) return;

            // Out-of-range digit guard. The scenario: only 2 candidates visible,
            // user presses '4' → digit-to-idx mapping gives highlightIdx=3 which is invalid; without the
            // guard MarkPositionSelected would take that bad idx and (at last position) auto-commit
            // with a meaningless choice. If idx is out of the current candidate list bounds, this is a
            // no-op. The TryHandleNavigationKey path still returns true (key consumed), so the
            // digit doesn't leak to the host application — composition window stays open and user
            // can press a valid number.
            // NOTE: guard only fires when _candidates is non-empty (i.e., we have a known candidate
            // list to validate against). If _candidates is null/empty (e.g., no frame loaded yet),
            // we accept the pick; at runtime this never happens because the composition
            // window only intercepts digit keys when candidates are visible.
            int candidateCount = _candidates?.Count ?? 0;
            if (highlightIdx < 0)
            {
                return;
            }
            if (candidateCount > 0 && highlightIdx >= candidateCount)
            {
                return;
            }

            // Step 1: mark position selected with the given highlight index AND origin char (for
            // content-aware prune on subsequent input changes — only what is edited moves).
            // currentFocus is a UNIT index; origin char = the unit's FIRST
            // raw char in the snapshot (engine prune compares against units[pos].snapshotStart).
            char originChar = (_rawInput != null && currentFocus < _unitStarts.Count && _unitStarts[currentFocus] < _rawInput.Length)
                ? _rawInput[_unitStarts[currentFocus]] : '\0';

            // Read the chosen slice DIRECTLY from the frame's
            // SlicesAtFocus list — the worker built this list from CandidateMatrix.Cells, and
            // each entry is the exact codepoint string that THIS candidate contributes AT THIS
            // POSITION. No shared-prefix subtraction (it would drop base letters for FVS variants).
            //
            // For FVS variant of "n": _slicesAtFocus[hl] = "ᠨ᠋" (base + FVS, NOT just FVS)
            // For plain alt: _slicesAtFocus[hl] = "ᠩ"
            // For primary:   _slicesAtFocus[hl] = "ᠨ"
            string chosenSlice = "";
            ManjuShaperCore.FinalGlyph chosenGlyph = default;
            bool sliceCaptured = false;
            try
            {
                if (_slicesAtFocus != null && highlightIdx < _slicesAtFocus.Count)
                {
                    chosenSlice = _slicesAtFocus[highlightIdx] ?? "";
                }
                if (_shapedCandidates != null && highlightIdx < _shapedCandidates.Count)
                {
                    var shapedSeq = _shapedCandidates[highlightIdx];
                    if (shapedSeq != null && shapedSeq.Count > 0)
                    {
                        chosenGlyph = shapedSeq[shapedSeq.Count - 1];
                    }
                }
                sliceCaptured = !string.IsNullOrEmpty(chosenSlice);
            }
            catch (Exception)
            {
                sliceCaptured = false;
            }

            if (sliceCaptured)
            {
                _engine.MarkPositionSelected(currentFocus, highlightIdx, originChar, chosenSlice, chosenGlyph);
            }
            else
            {
                _engine.MarkPositionSelected(currentFocus, highlightIdx, originChar);
            }

            // Step 2: set flash hide for visual refresh.
            _candidatesFlashHideTicks = 1;

            // Step 3: Auto-advance focus to the next pending position after a
            // pick (finishing a selection moves down to the next position awaiting one).
            // Auto-commit ONLY when EVERY position is selected, regardless of which was just picked (the last position
            // is not special: it can still be chosen freely, it just does not commit merely for being last).
            int selectedNow = _engine.GetSelectedPositionsSnapshot().Count;
            if (selectedNow >= rowCount)
            {
                // KW hide handled centrally by TextService after the auto-commit completes.
                _pendingAutoCommit = true;
                RequestUpdate(force: true);
            }
            else
            {
                int next = currentFocus + 1;
                if (next >= rowCount)
                {
                    // last position picked out-of-order while earlier ones still unpicked → stay (don't run past
                    // the end, don't commit — the last position is selectable but does not commit just for being last).
                    RequestUpdate(force: true);
                }
                else if (_engine.IsPositionSelected(next))
                {
                    // next already picked (the user walked back) → stay, don't overwrite a chosen position.
                    RequestUpdate(force: true);
                }
                else
                {
                    SetFocusedRow(next);
                    _selectedIndex = _engine.GetPositionHighlight(next);
                    _engine.EnqueueFocusChange(_rawInput ?? string.Empty, next);
                    RequestUpdate(force: true);
                }
            }
        }

        // ←/→ navigate inner highlight at current focus position. NOT a pick —
        // just changes which candidate is highlighted. _selectedIndex
        // tracks UI highlight; engine SetPositionHighlight tracks per-position memory.
        public void MoveInnerHighlight(int delta)
        {
            int rowCount = GetCurrentRowCount();
            if (rowCount <= 0) return;
            int focus = ResolveFocusIndex(rowCount);
            if (focus < 0) return;

            int candidateCount = _candidates?.Count ?? 0;
            if (candidateCount <= 0) return;

            int newSel = Math.Clamp(_selectedIndex + delta, 0, candidateCount - 1);
            if (newSel == _selectedIndex) return;

            _selectedIndex = newSel;
            _engine.SetPositionHighlight(focus, newSel);
            if (_hwnd != 0) NativeMethods.InvalidateRect(_hwnd, nint.Zero, false);
        }

        /// <summary> Move focus by delta rows; clamps. Signals worker so
        /// candidates refresh for new focus.</summary>
        public void MoveFocus(int delta)
        {
            int rowCount = GetCurrentRowCount();
            if (rowCount <= 0) return;
            int currentFocus = ResolveFocusIndex(rowCount);
            int newFocus = Math.Clamp(currentFocus + delta, 0, rowCount - 1);
            if (newFocus == currentFocus) return;
            SetFocusedRow(newFocus);
            _userScrollOverride = false;   // explicit navigation resumes auto-follow
            // Memory consistency: revisit a previously-selected position → card
            // highlight should land on the historically-chosen idx, not stay at stale _selectedIndex.
            _selectedIndex = _engine.GetPositionHighlight(newFocus);
            // Signal worker so candidates regenerate for the new focus position.
            _engine.EnqueueFocusChange(_rawInput ?? string.Empty, newFocus);
            RequestUpdate(force: true);
        }

        /// <summary> Jump focus to row; signals worker;
        /// syncs _selectedIndex from engine memory.</summary>
        public void JumpFocus(int rowIndex)
        {
            int rowCount = GetCurrentRowCount();
            if (rowCount <= 0 || rowIndex < 0 || rowIndex >= rowCount) return;
            if (rowIndex == _focusedRowIndex) return;
            SetFocusedRow(rowIndex);
            _userScrollOverride = false;   // explicit jump resumes auto-follow
            // Memory consistency on jump.
            _selectedIndex = _engine.GetPositionHighlight(rowIndex);
            _engine.EnqueueFocusChange(_rawInput ?? string.Empty, rowIndex);
            RequestUpdate(force: true);
        }

        // ↑↓/←→/0-9 are always live; no mode gates them. The highlight band shows
        // whenever focus is set (focusIdx >= 0).

        /// <summary>Cycle to the next colour theme (the settings menu's theme row). Recolors the
        /// EXISTING brushes in-place via ID2D1SolidColorBrush::SetColor — no COM release/recreate, so it
        /// cannot freeze the host (the freeze hazard is brush lifetime; SetColor only sets a value). If
        /// resources aren't built yet, it just advances the index (the next resource-create reads it).</summary>
        public void CycleTheme() => ApplyTheme((_themeIndex + 1) % _themes.Length);

        /// <summary> Moves to theme <paramref name="index"/>: the settings menu's theme row
        /// moves to the next one, and the settings file's appearance.theme names one. The theme in
        /// force already is left as it is.</summary>
        private void ApplyTheme(int index)
        {
            if (index == _themeIndex || index < 0 || index >= _themes.Length) return;
            _themeIndex = index;
            var th = _themes[_themeIndex];
            _bgColor = th.Bg;
            // The count comes from _themes.Length, never from a number written here —
            // the palette table is the only thing that knows how many themes there are.
            ShowSwitchToast(ManjuEngine.SwitchToastLineFor(
                ManjuEngine.ToastAxisTheme, th.Name, _themeIndex + 1, _themes.Length, deferred: false));
            try
            {
                _textBrush?.SetColor(th.Text);
                _borderBrush?.SetColor(th.Border);
                _highlightBrush?.SetColor(th.Band);
                _selectedCardHighlightBrush?.SetColor(th.Card);
                _innerGlyphHighlightBrush?.SetColor(th.Grid);
                _focusGlyphOverlayBrush?.SetColor(th.FocusGlyph);
                _unfocusedOverlayBrush?.SetColor(th.Unfocused);
                _ribbonBrush?.SetColor(th.Ribbon);
                _metaDimBrush?.SetColor(DimOf(th.Text));   // row 3's de-emphasis follows the theme
                _settingsPanelBrush?.SetColor(OpaqueOf(th.Bg));   // the toast's panel, under the theme's text
            }
            catch (Exception) { }

            PushThemeToDictionary(th);

            if (_hwnd != 0) NativeMethods.PostMessage(_hwnd, WM_APP_UPDATE, 0, 0);
        }

        // Propagate the palette to the dictionary window so
        // the two surfaces never sit in different palettes. Called from CycleTheme, and once when the
        // dictionary module first becomes reachable (EnsureInitialDictionaryTheme); without that first
        // push the dictionary window would sit in its hardcoded defaults until the first theme cycle
        // while the main window wears theme[_themeIndex].
        // Goes through the IDictionaryModule interface; no-op when the module is absent/disabled.
        private bool _dictThemePushed;
        private void PushThemeToDictionary(Theme th)
        {
            try
            {
                var module = _engine?.DictionaryModule;
                if (module == null) return;
                module.OnThemeChanged(
                    PackArgb(th.Bg), PackArgb(th.Text), PackArgb(th.Border), PackArgb(th.Band), PackArgb(th.Ribbon));
                _dictThemePushed = true;
            }
            catch (Exception) { }
        }

        /// <summary> Ensure the CURRENT theme has been pushed to the dictionary module
        /// at least once: the initial palette, not just theme changes. Called from the frame-ready path
        /// (cheap flag check) because the module attaches after the presenter is constructed.</summary>
        private void EnsureInitialDictionaryTheme()
        {
            if (_dictThemePushed) return;
            PushThemeToDictionary(_themes[_themeIndex]);
        }

        /// <summary> Pack a theme colour as 0xAARRGGBB for the primitives-only module interface.</summary>
        private static uint PackArgb(D2D1_COLOR_F c)
        {
            static uint B(float v) => (uint)(Math.Clamp(v, 0f, 1f) * 255f + 0.5f);
            return (B(c.a) << 24) | (B(c.r) << 16) | (B(c.g) << 8) | B(c.b);
        }

        /// <summary> Reset focus state — called on composition end
        /// (ResetCandidateAnchoringCycle, every end path). _rawInput must reset WITH the
        /// focus, or the next same-prefix word is classified as an APPEND of the dead word and the stale
        /// focus is re-sent to the worker (e.g. 'ka'→'kak').</summary>
        public void ResetFocus()
        {
            _rawInput = string.Empty;
            if (_focusedRowIndex == -1) return;
            SetFocusedRow(-1);
        }

        private int GetCurrentRowCount()
        {
            // Row count = tokenized UNIT count (digraph-aware; "ng"/"zh"/"tsh"
            // = ONE row). Rows equal the engine's position model (BuildCommitString /
            // candidate matrix iterate units), so focus/pick indices match engine positions exactly;
            // a per-keystroke approximation would misalign digraph words (e.g. "n+g").
            EnsureUnitsCache();
            return _latinUnits.Count;
        }

        private int ResolveFocusIndex(int rowCount)
        {
            if (rowCount <= 0) return -1;
            // SENTINEL RESOLVES TO THE TAIL: everything before is taken as already
            // chosen for you, unless you go back to choose yourself, so the default selection focus is the item just typed.
            //
            // THIS SIDE AND THE ENGINE SIDE MUST AGREE. If the presenter resolved to 0 while the engine
            // resolved to the tail, the candidate window would show last-character variants while the band
            // is on the first row. ManjuEngine.ResolveFocusUnitSentinel carries the
            // mirror of this comment. Changing one without the other creates that mismatch.
            if (_focusedRowIndex < 0 || _focusedRowIndex >= rowCount)
            {
                if (_lastLoggedSentinelResolve != rowCount - 1)
                {
                    _lastLoggedSentinelResolve = rowCount - 1;
                }
                return rowCount - 1;
            }
            return _focusedRowIndex;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
        private static nint StaticWndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
        {
            try
            {
                nint userData = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWLP_USERDATA);
                if (userData != 0)
                {
                    GCHandle handle = GCHandle.FromIntPtr(userData);
                    if (handle.IsAllocated && handle.Target is CandidateListUIPresenter presenter)
                    {
                        return presenter.InstanceWndProc(hWnd, msg, wParam, lParam);
                    }
                }
            }
            catch
            {
                // Suppress
            }
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private nint InstanceWndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
        {

            // Prevent window activation on mouse click
            if (msg == 0x0021) // WM_MOUSEACTIVATE
            {
                // This window is exactly as no-activate and exactly
                // as clickable as the dictionary window (preview grid moves focus on click), so a click
                // here also produces NO OnSetFocus(0), and without the interruption "hold Shift → click
                // preview → release" would fire a false lone-tap. Same interruption wire as the dictionary window.
                try { _engine?.RaisePointerInterruption(); } catch { }
                return 3; // MA_NOACTIVATE - Don't activate this window
            }

            // WM_GESTURE / GID_PAN — touch pan scrolls the preview prefix rows (on a touchscreen).
            // Vertical pan distance accumulates in DIP; each full row height shifts _prefixScrollOffset by ±1
            // under _userScrollOverride (suppresses auto-follow until the next focus/input change).
            // Unhandled gesture ids fall through to DefWindowProc so tap still synthesizes WM_LBUTTONDOWN.
            if (msg == NativeMethods.WM_GESTURE)
            {
                var gi = new NativeMethods.GESTUREINFO();
                gi.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.GESTUREINFO>();
                if (NativeMethods.GetGestureInfo(lParam, ref gi))
                {
                    try
                    {
                        if (gi.dwID == NativeMethods.GID_BEGIN)
                        {
                            _gesturePanActive = false;
                            _gesturePanAccumDip = 0f;
                            _gestureLastYPhys = gi.ptsLocationY;
                        }
                        else if (gi.dwID == NativeMethods.GID_PAN)
                        {
                            if (!_gesturePanActive)
                            {
                                _gesturePanActive = true;
                                _gestureLastYPhys = gi.ptsLocationY;
                            }
                            int dyPhys = gi.ptsLocationY - _gestureLastYPhys;
                            _gestureLastYPhys = gi.ptsLocationY;
                            float dpiYg = 96f;
                            try { _renderTarget?.GetDpi(out _, out dpiYg); } catch { dpiYg = 96f; }
                            if (dpiYg <= 0f) dpiYg = 96f;
                            _gesturePanAccumDip += dyPhys * 96f / dpiYg;
                            int rowsMoved = (int)(_gesturePanAccumDip / NewColumnRowHeight);
                            if (rowsMoved != 0)
                            {
                                _gesturePanAccumDip -= rowsMoved * NewColumnRowHeight;
                                EnsureUnitsCache();
                                int rc = DisplaySlotCount;   // scroll clamp in display-slot space
                                // finger down = content follows finger → offset moves OPPOSITE to pan delta
                                int newOff = ApplyManualScrollDelta(_prefixScrollOffset, -rowsMoved, rc, Math.Max(1, _lastVisibleRowsForScroll));
                                if (newOff != _prefixScrollOffset)
                                {
                                    _prefixScrollOffset = newOff;
                                    _userScrollOverride = true;
                                    NativeMethods.InvalidateRect(_hwnd, 0, false);
                                }
                                else if (rowsMoved != 0)
                                {
                                    _userScrollOverride = true;   // at the edge — still user-owned until next focus/input
                                }
                            }
                            return 0;   // pan handled (suppress legacy synthesis for this gesture)
                        }
                        else if (gi.dwID == NativeMethods.GID_END)
                        {
                            _gesturePanActive = false;
                            _gesturePanAccumDip = 0f;
                        }
                    }
                    finally
                    {
                        NativeMethods.CloseGestureInfoHandle(lParam);
                    }
                }
                return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
            }

            // WM_LBUTTONDOWN — the four-column PREVIEW grid IS a mouse target
            // (click a unit row → JumpFocus there = full focus move, same as arrow-key navigation).
            // Candidate CARDS are intentionally NOT a mouse target (a click on a card does not
            // pick it). Click off the preview grid → no-op here; raw-commit on
            // a real defocus is the TSF focus-loss path, not this handler.
            if (msg == 0x0201) // WM_LBUTTONDOWN
            {
                // lParam low/high word = client x/y in PHYSICAL px; the preview grid is in DIP (paint
                // uses SetDpi(desktopDpi)). Convert physical → DIP first (on a 200%
                // display physical = 2× DIP → wrong-row hits otherwise).
                int xPhys = (short)((long)lParam & 0xFFFF);
                int yPhys = (short)(((long)lParam >> 16) & 0xFFFF);
                float dpiX = 96f, dpiY = 96f;
                try { _renderTarget?.GetDpi(out dpiX, out dpiY); } catch { dpiX = 96f; dpiY = 96f; }
                if (dpiX <= 0f) dpiX = 96f;
                if (dpiY <= 0f) dpiY = 96f;
                float xDip = xPhys * 96f / dpiX;
                float yDip = yPhys * 96f / dpiY;
                // PREVIEW grid (Index/Latin/Mongolian columns at (Padding,Padding), NewColumnRowHeight
                // rows). Map click → unit row, JumpFocus (full focus move + card regen + force repaint).
                // NOTE: candidate cards are deliberately NOT hit-tested here (a card is not a mouse target).
                // Layout is in DISPLAY-SLOT space (unit rows + separator rows). Map the
                // click to a slot, then to its engine UNIT; a click on a separator slot (unit == -1) is a
                // no-op (separators are display-only, non-focusable). JumpFocus still takes a UNIT index.
                EnsureUnitsCache();
                int prefixSlots = DisplaySlotCount;
                int visRows = Math.Max(0, prefixSlots - _prefixScrollOffset);
                if (prefixSlots > 0
                    && xDip >= Padding && xDip <= Padding + PrefixRegionWidth
                    && yDip >= Padding && yDip < Padding + visRows * NewColumnRowHeight)
                {
                    int slot = (int)((yDip - Padding) / NewColumnRowHeight) + _prefixScrollOffset;
                    if (slot < 0) slot = 0;
                    if (slot >= prefixSlots) slot = prefixSlots - 1;
                    int unit = (slot < _displaySlotUnit.Count) ? _displaySlotUnit[slot] : -1;
                    if (unit >= 0) JumpFocus(unit);   // separator slot → ignore
                }
                return 0;
            }

            // Always report as client area to prevent resize cursor
            if (msg == 0x0084) // WM_NCHITTEST
            {
                return 1; // HTCLIENT - Report as client area, not border
            }

            // Drain BEFORE the error guard. Work can be queued while the presenter is in its error
            // state, and the guard below returns early for every later message, so draining after it
            // would let the queue only ever grow. Draining here on the window thread is
            // still the right thread; DeliverFrameOnOwningThread re-checks the epoch, so anything stale
            // is rejected rather than shown.
            if (msg == WM_APP_DELIVER)
            {
                DrainRetainedDeliveries();
                return 0;
            }
            if (_inErrorState) return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

            try
            {
                if (msg == 0x000F) // WM_PAINT
                {
                    PAINTSTRUCT ps;
#pragma warning disable CS8600 // Converting null literal or possible null value to non-nullable type.
                    NativeMethods.BeginPaint(hWnd, out ps);
#pragma warning restore CS8600
                    try
                    {
                        // Only paint if resources already exist; don't create during WM_PAINT
                        if (_renderTarget != null)
                        {
                            HandlePaint();
                        }
                        else
                        {
                            // Resources not ready yet, post update to trigger resource creation outside WM_PAINT
                            NativeMethods.PostMessage(_hwnd, WM_APP_UPDATE, 0, 0);
                        }
                    }
                    finally
                    {
                        NativeMethods.EndPaint(hWnd, ref ps);
                    }
                    return 0;
                }
                if (msg == WM_APP_UPDATE)
                {
                    HandleUpdateMessage();
                    return 0;
                }
                // Drain queued frame deliveries HERE, on the window thread — the same
                // thread TSF calls Clear() on. That is what makes the epoch check inside DeliverFrame
                // exact: a cancel cannot land between the check and the subscriber body, because both
                // run on this thread. See ManjuEngine.FrameDeliveryDispatcher.
                // (WM_APP_DELIVER is handled above, ahead of the error guard.)
                if (msg == WM_APP_HIDE) // handle a cross-thread hide request
                {
                    HideWindowState(null);
                    return 0;
                }
                if (msg == WM_TIMER)
                {
                    if ((nuint)wParam == LAYOUT_RETRY_TIMER_ID)
                    {
                        OnLayoutRetryTimer();   // the deferred retry the 16 ms window suppressed
                        return 0;
                    }
                    if ((nuint)wParam == UPDATE_TIMER_ID)
                    {
                        NativeMethods.KillTimer(_hwnd, UPDATE_TIMER_ID);
                        _timerArmed = false;
                        if (_updatePending)
                        {
                            _updatePending = false;
                            RequestUpdate(force: true);
                        }
                        return 0;
                    }
                }
                if (msg == 0x02E0) // WM_DPICHANGED
                {
                    return HandleDpiChanged(wParam, lParam);
                }
                if (msg == 0x0002) // WM_DESTROY
                {
                    // Cancel the retry sequence BEFORE the HWND is cleared — KillTimer
                    // needs it. Cancelling only from Dispose() leaves a window destroyed without a
                    // Dispose holding a live OS timer and a retry budget chasing a layout that is gone.
                    CancelLayoutRetriesForLifecycleChange(null);
                    _hwnd = 0;
                    _windowVisible = false;
                    _updatePosted = false;
                    _timerArmed = false;
                    _updatePending = false;
                }
            }
            catch (Exception)
            {
                _inErrorState = true;
            }
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private unsafe nint HandleDpiChanged(nuint wParam, nint lParam)
        {
            if (_renderTarget != null)
            {
                uint dpiX = (uint)wParam & 0xFFFF;
                uint dpiY = ((uint)wParam >> 16) & 0xFFFF;
                _renderTarget.SetDpi((float)dpiX, (float)dpiY);
            }

            RECT* pRect = (RECT*)lParam;
            NativeMethods.SetWindowPos(_hwnd, 0, 
                pRect->Left, pRect->Top, 
                pRect->Right - pRect->Left, pRect->Bottom - pRect->Top, 
                NativeMethods.SWP_NOACTIVATE | 0x0004); // SWP_NOZORDER

            return 0;
        }

        // Receive pre-calculated results from Backend Worker
        // This runs on Worker Thread, but locks briefly to update data, then Posts message to UI.
        // Signal from Worker that new frame is available in GlobalState
        // The frame the ENGINE HANDED THIS PRESENTER, as opposed to whatever is newest in the
        // process-global GlobalState. A "parameterless event -> read global twice" path could
        // consume a second engine's frame.
        // TextService subscribes to OnFrameReadyFrame and passes the object down here; the window
        // message that follows carries no payload, so the frame is parked in this field and
        // HandleUpdateMessage consumes THIS, validated by owner + epoch.
        private Messaging.RenderFrame? _deliveredFrame;

        public void NotifyFrameReady(Messaging.RenderFrame frame)
        {
            if (_engine != null && !_engine.IsFrameCurrent(frame))
            {
                return;
            }
            System.Threading.Volatile.Write(ref _deliveredFrame, frame);
            NotifyFrameReady();
        }

        public void NotifyFrameReady()
        {
            EnsureInitialDictionaryTheme();   // first reachable moment for the module
            bool canAccept = CanAcceptFrameReadySignal(_hwnd != 0, _inErrorState);
            if (canAccept)
            {
                NativeMethods.PostMessage(_hwnd, WM_APP_UPDATE, 0, 0);
            }
        }

        // The layout math CONSUMES the immutable snapshot the gate
        // approved: re-reading live state here would let the size be computed from inputs
        // the gate has never seen. Every input below
        // comes from `snap`; the only live reads are resources (font face / brushes), which
        // are not layout inputs.
        // Returns TRUE only when a layout actually completed. Every early exit
        // below (rejected font identity, unverified re-snapshot, missing resources, degenerate
        // payload, caught exception) reports failure so the caller keeps the layout dirty and
        // schedules a retry, instead of clearing the flag unconditionally.
        private unsafe bool CalculateAndResizeWindow(LayoutSnapshot snap)
        {
            // Set by any native step the OS refused. Without it the method
            // would return true while ignoring both SetWindowPos results and the D2D Resize HRESULT, so
            // "did it complete" would not mean anything.
            bool nativeStepFailed = false;
            try
            {
                EnsureResources();

                // snap.FontFace has a CONSUMER here. Width math against the LIVE face would let a
                // font swap between approval and computation size the window from a face the gate
                // never saw. Resolve the
                // three cases explicitly, then compute only against the approved identity.
                nint liveFace = System.Threading.Volatile.Read(ref _nativeFontFace);
                int liveGen = FontGeneration;
                switch (ReconcileApprovedFontFace(snap.FontFace, snap.FontGeneration, liveFace, liveGen))
                {
                    case FontFaceApproval.Reject:
                        return false;   // [LAYOUT-RETRY] did not complete — caller stays dirty and retries
                    case FontFaceApproval.ReSnapshot:
                        // Snapshot predates resource creation (approved face == 0, EnsureResources has
                        // since made one). Re-capture so the face we compute with IS an approved input,
                        // instead of silently using an unapproved one.
                        var rescued = CaptureLayoutSnapshotStable();
                        if (rescued == null)
                        {
                            // Fail closed rather than compute from an unverified read.
                            return false;   // [LAYOUT-RETRY] did not complete — caller stays dirty and retries
                        }
                        snap = rescued;
                        _lastLayoutSnapshot = snap;
                        break;
                }
                if (_fontFace == null) return false;

                // Snapshot payloads are handed out as READ-ONLY views (they cannot be
                // mutated through the snapshot). Materialise local working copies for the helpers that
                // take concrete lists — copying, never aliasing back into the approved snapshot.
                List<float> lengthsCopy = new List<float>(snap.LengthsPayload);
                // Inner views are IReadOnlyList, so materialise each one.
                List<List<ManjuShaperCore.FinalGlyph>> shapedCopy = new List<List<ManjuShaperCore.FinalGlyph>>(snap.ShapedPayload.Count);
                foreach (var inner in snap.ShapedPayload) shapedCopy.Add(new List<ManjuShaperCore.FinalGlyph>(inner));
                List<Messaging.SemanticCandidateItem>? semanticCopy =
                    snap.SemanticPayload == null ? null : new List<Messaging.SemanticCandidateItem>(snap.SemanticPayload);
                ushort upemCopy = (ushort)snap.Upem;
                if (snap.LengthCount != snap.CandidateCount || snap.ShapedCount != snap.CandidateCount || snap.CandidateCount == 0) return false;

                int currentWidth = snap.ClientW;
                int currentHeight = snap.ClientH;
                float dpiX = snap.DpiX, dpiY = snap.DpiY;
                float currentWidthDips = ConvertPixelsToDips(currentWidth, dpiX);
                float currentHeightDips = ConvertPixelsToDips(currentHeight, dpiY);
                // The height budget is taken against the WORK AREA, not the screen. SM_CYSCREEN and
                // SM_CYVIRTUALSCREEN are only fallbacks: they give the monitor's full height with the
                // taskbar included, and on a multi-monitor desktop the union of every
                // monitor. Both over-state how much room this window has by construction.
                int screenHeight = snap.WorkH > 0 ? snap.WorkH : snap.ScreenH;
                if (screenHeight <= 0)
                {
                    screenHeight = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN);
                }
                float screenHeightDip = ConvertPixelsToDips(screenHeight, dpiY);
                float locationYDip = ConvertPixelsToDips(snap.Y, dpiY);
                WindowHeightBudgetMetrics heightBudgetMetrics = ComputeWindowHeightBudgetMetrics(
                    screenHeightDip,
                    currentHeightDips,
                    locationYDip,
                    FontSize,
                    Padding);
                float maxWindowHeight = heightBudgetMetrics.FinalMaxWindowHeight;

            // _fontFace is identity-verified against snap.FontFace above, so this IS the
                // approved face — the managed wrapper for the pointer the gate signed off on.
                var cardWidths = ComputeAllCardWidthsFromShapedCandidatesWithFontFace(shapedCopy, semanticCopy, FontSize, Padding, upemCopy, _fontFace);
                ApplyFusionCardWidthFloor(cardWidths, snap?.FusionFloorApplies); // snapshot decision, not a live re-derive
                HorizontalWindowLayoutMetrics layoutMetrics = ComputeHorizontalWindowLayoutMetrics(lengthsCopy, cardWidths, FontSize, Padding, maxWindowHeight);
                float targetWidthRaw = layoutMetrics.TargetWidth;
                float targetHeightRaw = layoutMetrics.TargetHeight;
                int columnCount = layoutMetrics.ColumnCount;

                // Extend target width additively for the four-column UI prefix
                // (Index | Latin | Mongolian columns). The card width chain (ComputeAllCardWidths /
                // ComputeWindowWidthMetrics / ComputeOccupiedWidthMetrics bodies) does NOT include it.
                // Add PrefixRegionWidth (per-frame: base, plus the separated col4 only when the word has a fusion) ONLY when
                // there is content to display (avoid widening empty windows). No-fusion words → no empty separated col4.
                if (lengthsCopy.Count > 0)
                {
                    targetWidthRaw += snap.PrefixRegionWidth;   // from the snapshot, not live
                }

                // Index/Latin/Mongolian prefix area also has height needs that scale with
                // _rawInput.Length. A requiredHeight from candidate-card heights alone would clip
                // a 12+ char input whenever the candidate cards are short. So take the
                // max of (candidate cards' tallest height) and (prefix-area rows total height).
                // Cap at maxWindowHeight to respect screen-budget. The result feeds the same resize
                // path as the card height.
                int prefixRowCount = snap.DisplaySlots; // snapshot value, not a live re-read
                if (prefixRowCount > 0)
                {
                    float prefixRequiredHeight = prefixRowCount * NewColumnRowHeight + 2f * Padding;
                    float boundedPrefixHeight = maxWindowHeight > 0f
                        ? Math.Min(maxWindowHeight, prefixRequiredHeight)
                        : prefixRequiredHeight;
                    if (boundedPrefixHeight > targetHeightRaw)
                    {
                        targetHeightRaw = boundedPrefixHeight;
                    }

                    // THE TWO PHASES.
                    //
                    // The window grows with content, and when it meets the work-area
                    // bottom it stops growing ONCE and the internal viewport carries everything after
                    // that. prefixRequiredHeight scales with the row count, and the Math.Min above is
                    // the cap. The transition is an event with a count, so "did the transition happen?"
                    // is answered by a number.
                    //
                    // For example, 40 DIP rows meet a 694 DIP budget at 17 rows
                    // (17*40+20 = 700 > 694). A longer input reaches the transition early, and from then
                    // on the window stays at the screen edge while the viewport scrolls. The event below
                    // records the transition, so it is counted, not inferred.
                    bool atBudget = maxWindowHeight > 0f && prefixRequiredHeight >= maxWindowHeight;
                    if (atBudget != _heightAtBudget)
                    {
                        _heightAtBudget = atBudget;
                    }
                }

                // THE SIZE BOUND. Unbounded, the width is the sum of
                // the candidate cards plus the prefix region, and the candidate list is laid out as ONE
                // horizontal row (ComputeWindowHeightForRowCount(1, ...)). So a long list would produce a
                // window wider than the monitor, and the position clamp, doing its job, would pin it to the
                // left work edge and leave the right-hand cards past the right edge: visible to nobody,
                // clickable by nobody.
                //
                // WHAT THIS DOES AND DOES NOT DO: it
                // stops the window leaving the screen. It does NOT make the candidates that no longer fit
                // reachable; the horizontal candidate viewport (ComputeCandidateStrip) does that at paint time.
                float workWDip = ConvertPixelsToDips(snap.WorkW, dpiX);
                float workHDip = ConvertPixelsToDips(snap.WorkH, dpiY);
                var bounded = ClampWindowSizeToWorkArea(targetWidthRaw, targetHeightRaw, workWDip, workHDip);
                targetWidthRaw = bounded.w;
                targetHeightRaw = bounded.h;

                int targetWidth = ConvertDipsToPixels(targetWidthRaw, dpiX);
                int targetHeight = ConvertDipsToPixels(targetHeightRaw, dpiY);
                LogWindowWidthComposition("CalculateAndResizeWindow/Requested", cardWidths, Padding, targetWidthRaw, targetWidth, dpiX, columnCount);
                bool sizeChanged = Math.Abs(currentWidth - targetWidth) > 1 || Math.Abs(currentHeight - targetHeight) > 1;
                bool resizeAttempted = false;
                bool resizeSucceeded = false;
                if (sizeChanged)
                {
                    resizeAttempted = true;
                    resizeSucceeded = NativeMethods.SetWindowPos(_hwnd, 0, 0, 0, targetWidth, targetHeight,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    // A resize the OS refused is not a completed layout.
                    if (!resizeSucceeded) nativeStepFailed = true;
                }
                LogResizeAttempt("CalculateAndResizeWindow/FirstPass", currentWidth, currentHeight, currentWidthDips, currentHeightDips, targetWidth, targetHeight, targetWidthRaw, targetHeightRaw, sizeChanged, resizeAttempted, resizeSucceeded);



                if (sizeChanged &&
                    NativeMethods.GetClientRect(_hwnd, out RECT realizedClientRect))
                {
                    int realizedClientWidth = realizedClientRect.Right - realizedClientRect.Left;
                    int realizedClientHeight = realizedClientRect.Bottom - realizedClientRect.Top;
                    SecondPassResizeMetrics secondPassMetrics = ComputeSecondPassResizeMetrics(targetWidth, targetHeight, realizedClientWidth, realizedClientHeight);
                    if (secondPassMetrics.RequiresCorrection)
                    {
                        bool correctiveResizeSucceeded = NativeMethods.SetWindowPos(_hwnd, 0, 0, 0, secondPassMetrics.CorrectedWidth, secondPassMetrics.CorrectedHeight,
                            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                        if (!correctiveResizeSucceeded) nativeStepFailed = true;
                        LogResizeAttempt("CalculateAndResizeWindow/SecondPassApply", realizedClientWidth, realizedClientHeight, ConvertPixelsToDips(realizedClientWidth, dpiX), ConvertPixelsToDips(realizedClientHeight, dpiY), secondPassMetrics.CorrectedWidth, secondPassMetrics.CorrectedHeight, targetWidthRaw, targetHeightRaw, true, true, correctiveResizeSucceeded);
                    }
                }

                // A change detector must not read back its own output. This layout just
                // wrote the window's client size; unless the APPROVED snapshot is told, the very next
                // update will read that size back, find it different from what was approved, and lay out
                // identical content again.
                //
                // Placement is load-bearing in two ways. It sits AFTER both resize passes — the first pass
                // above and the corrective second pass — so the value re-based is the one finally realized,
                // not the one first requested. And it sits BEFORE the nativeStepFailed / exception returns:
                // re-basing at the end of the method would miss every wasted relayout that takes a path
                // returning before it.
                //
                // Narrow by construction: WithClientSize re-bases the two fields this layout authored and
                // carries everything else across unchanged, so every other layout input stays under the
                // gate. Guarded on sizeChanged, so a layout that moved nothing re-approves nothing, and on
                // a non-null approved snapshot, so this can never approve what the gate declined to.
                if (sizeChanged && _lastLayoutSnapshot != null &&
                    NativeMethods.GetClientRect(_hwnd, out RECT rebasedClientRect))
                {
                    int rebasedW = rebasedClientRect.Right - rebasedClientRect.Left;
                    int rebasedH = rebasedClientRect.Bottom - rebasedClientRect.Top;
                    if (rebasedW != _lastLayoutSnapshot.ClientW || rebasedH != _lastLayoutSnapshot.ClientH)
                    {
                        _lastLayoutSnapshot = _lastLayoutSnapshot.WithClientSize(rebasedW, rebasedH);
                    }
                }

                // Resize the D2D render target pixel buffer to match the realized HWND client area.
                // Without this, the render target keeps its initial pixel size and D2D stretches
                // the small buffer to fill the larger window, which distorts the card widths.
                if (sizeChanged && _renderTarget != null &&
                    NativeMethods.GetClientRect(_hwnd, out RECT finalClientRect))
                {
                    uint finalWidth = (uint)(finalClientRect.Right - finalClientRect.Left);
                    uint finalHeight = (uint)(finalClientRect.Bottom - finalClientRect.Top);
                    if (finalWidth == 0) finalWidth = 1;
                    if (finalHeight == 0) finalHeight = 1;
                    var newPixelSize = new D2D1_SIZE_U { width = finalWidth, height = finalHeight };
                    // Resize is HwndRenderTarget-only.
                    int resizeHr = (_renderTarget is NativeMethods.ID2D1HwndRenderTarget _hwndRt) ? _hwndRt.Resize(in newPixelSize) : 0;
                    if (resizeHr != 0) nativeStepFailed = true;   // a failed D2D resize is not success
                }
            }
            catch (Exception)
            {
                return false;   // [LAYOUT-RETRY] a thrown layout did NOT complete
            }
            if (nativeStepFailed)
            {
                return false;
            }
            ResetLayoutRetryBudget();
            return true;
        }

        private unsafe void HandlePaint()
        {
            _paintStopwatch.Restart(); // Start timer

            try
            {
                // Do not call EnsureResources here
                if (_renderTarget == null) return;

                _renderTarget.BeginDraw();

                // 1. Draw Background (opaque, from the active theme)
                _renderTarget.Clear(_bgColor); // active theme background

                float dpiX, dpiY, width, height;
                {
                    RECT clientRect;
                    NativeMethods.GetClientRect(_hwnd, out clientRect);
                    GetLayoutDpi(out dpiX, out dpiY);
                    width = ConvertPixelsToDips(clientRect.Right - clientRect.Left, dpiX);
                    height = ConvertPixelsToDips(clientRect.Bottom - clientRect.Top, dpiY);
                }

                // Protect access to shaped candidates during paint
                List<List<ManjuShaperCore.FinalGlyph>>? shapedCopy = null;
                List<float>? lengthsCopy = null;
                // Copy semantic items for supporting (metadata) row rendering
                List<Messaging.SemanticCandidateItem>? semanticCopy = null;
                bool cacheReady = false;
                
                lock (_resourceLock)
                {
                    // Use helper to ensure consistent state
                    if (_fontFace != null && IsCacheReady())
                    {
                        cacheReady = true;
                        // Shallow copy lists reference for iteration
                        shapedCopy = _shapedCandidates; 
                        lengthsCopy = _candidateLengths;
                        semanticCopy = _semanticItems;
                    }
                }

                // 2. Render Candidates (if cache ready) - horizontal multi-card exposure with shaped-body width alignment
                if (cacheReady && _nativeFontFace != 0 && shapedCopy != null && lengthsCopy != null)
                {

                    var cardWidths = ComputeAllCardWidthsFromShapedCandidatesWithFontFace(shapedCopy, semanticCopy, FontSize, Padding, _upem, _fontFace);
                    ApplyFusionCardWidthFloor(cardWidths); // same floor as window-sizing pass so paint card[0] matches reserved window width
                    ComputeWindowWidthMetrics(cardWidths, Padding);
                    GetLayoutDpi(out float paintDpiX, out _);
                    float realizedClientWidthDip = width;
                    if (_hwnd != 0 && NativeMethods.GetClientRect(_hwnd, out RECT paintClientRect))
                    {
                        realizedClientWidthDip = ConvertPixelsToDips(Math.Max(0, paintClientRect.Right - paintClientRect.Left), paintDpiX);
                    }


                    // Render four-column prefix block (Index | Latin | Mongolian)
                    // BEFORE candidate cards. paint discipline: highlight band drawn as background
                    // first, text on top (z-order). _rawInput from frame.RawInput (atomic frame swap).
                    // Cards then start at currentX = Padding + PrefixRegionWidth (the window is widened
                    // additively in CalculateAndResizeWindow).
                    DrawFourColumnPrefix(shapedCopy, height);   // pass the real window height → the internal scroll is computed from the visible capacity

                    float currentX = Padding + PrefixRegionWidth;
                    float currentY = Padding;

                    // Flash hide: when set (just after variant pick), skip drawing
                    // candidate cards for one paint cycle. Four-column prefix (Index/Latin/Mongolian)
                    // already drawn above so user keeps spatial context. Decrement here so next paint
                    // shows the new candidates (regenerated by worker via EnqueueFocusChange).
                    bool flashHideThisCycle = _candidatesFlashHideTicks > 0;
                    if (flashHideThisCycle)
                    {
                        _candidatesFlashHideTicks--;
                    }

                    // Reset hit-test rectangles at start of paint loop;
                    // we re-record per-card geometry for the upcoming WM_LBUTTONDOWN handler.
                    // Pre-filled to the FULL candidate count with empty rects, because the
                    // strip may start past card 0. A card's INDEX in this list is its candidate index,
                    // so the list has to stay parallel to _candidates or a click would
                    // select the wrong candidate. An empty rect (L==R) cannot be hit.
                    _lastCardRects.Clear();
                    for (int i = 0; i < shapedCopy.Count; i++) _lastCardRects.Add((0f, 0f, 0f, 0f));

                    // The visible strip. The window keeps the size the layout gave it; this
                    // only decides WHICH cards are painted into it and where they sit.
                    float stripStartX = Padding + PrefixRegionWidth;
                    float stripAvailable = Math.Max(0f, realizedClientWidthDip - stripStartX - Padding);
                    var strip = ComputeCandidateStrip(cardWidths, Padding, stripStartX, stripAvailable,
                                                      _selectedIndex, _candidateViewportOffset);
                    _candidateViewportOffset = strip.Offset;

                    for (int si = 0; !flashHideThisCycle && si < strip.Cards.Count; si++)
                    {
                        int i = strip.Cards[si].Index;
                        float cardWidth = cardWidths[i];
                        float itemHeight = ComputeItemHeight(lengthsCopy[i], FontSize);
                        float cardHeight = ComputeCardTotalHeight(itemHeight, FontSize, Padding);


                        float cardLeft = strip.Cards[si].Left;   // from the strip, not a running cursor
                        float cardTop = currentY;
                        float cardRight = cardLeft + cardWidth;
                        float cardBottom = cardTop + cardHeight;

                        // Record card rect for WM_LBUTTONDOWN hit-test. Parallel to
                        // _candidates / _shapedCandidates / _slicesAtFocus by index.
                        // Written AT its index, not appended: cards before the strip keep the
                        // empty rect placed above so the list stays index-parallel.
                        _lastCardRects[i] = (cardLeft, cardTop, cardRight, cardBottom);
                        float contentInset = GetCardContentHorizontalInset(Padding);
                        float contentLeft = cardLeft + contentInset;
                        float contentRight = cardRight - contentInset;
                        float contentWidth = Math.Max(0f, contentRight - contentLeft);
                        float metadataY = cardTop + Padding;

                        // Selected-card highlight uses the dedicated Layer-2 brush
                        // (_nativeSelectedCardHighlightBrush) instead of the band brush.
                        // Layer hierarchy: band (Layer 1) > card (Layer 2) > inner-glyph (Layer 3).
                        if (i == _selectedIndex && _nativeSelectedCardHighlightBrush != 0)
                        {
                            RECT_F rectHighlight = new RECT_F {
                                left = cardLeft + 1,
                                top = cardTop + 1,
                                right = cardRight - 1,
                                bottom = cardBottom - 1
                            };
                            _renderTarget.FillRectangle(rectHighlight, _nativeSelectedCardHighlightBrush);
                        }

                        if (_nativeBorderBrush != 0)
                        {
                            RECT_F cardRect = new RECT_F
                            {
                                left = cardLeft + 0.5f,
                                top = cardTop + 0.5f,
                                right = cardRight - 0.5f,
                                bottom = cardBottom - 0.5f
                            };
                            _renderTarget.DrawRectangle(cardRect, _nativeBorderBrush, 1.0f, (nint)0);
                        }

                        // Row 1: Selection number (1-indexed)
                        string numberText = (i + 1).ToString();
                        MetadataPaintMetrics numberPaintMetrics = DrawMetadataText(numberText, contentLeft, metadataY, contentWidth);
                        metadataY += MetadataRowHeight + MetadataRowSpacing;

                        // Row 2: Enriched semantic name (from backend semantic item)
                        string semanticName = "";
                        if (semanticCopy != null && i < semanticCopy.Count)
                        {
                            semanticName = NormalizeSemanticNameForCard(semanticCopy[i].SemanticName);
                        }
                        MetadataPaintMetrics semanticPaintMetrics = DrawMetadataText(semanticName, contentLeft, metadataY, contentWidth);
                        metadataY += MetadataRowHeight + MetadataRowSpacing;

                        // Row 3: Code-point sequence (from backend semantic item)
                        // The Unicode ground truth is supplementary information → presented de-emphasised
                        // (theme Text @55% alpha), with less weight than row 2's description. A specialist can verify the code points without the row adding much visual weight.
                        // Draw-only gate — the row height stays reserved.
                        string codePointText = "";
                        if (ShowDetailedUnicodeInfo() && semanticCopy != null && i < semanticCopy.Count)
                        {
                            codePointText = NormalizeCodePointSequenceForCard(semanticCopy[i].CodePointSequence);
                        }
                        MetadataPaintMetrics codePointPaintMetrics = DrawMetadataText(codePointText, contentLeft, metadataY, contentWidth, _nativeMetaDimBrush);

                        float textLength = lengthsCopy[i];
                        OccupiedWidthMetrics occupiedMetrics = ComputePrimaryBodyOccupiedWidthMetricsWithFontFace(shapedCopy[i], FontSize, _upem, _fontFace);
                        float occupiedWidth = occupiedMetrics.OccupiedWidth;
                        ComputeContentCardWidthMetrics(
                            i + 1,
                            semanticName,
                            codePointText,
                            occupiedWidth,
                            FontSize,
                            MetadataFontSize,
                            Padding);
                        float bodyTop = cardTop + GetBodyRegionTop(Padding);
                        float bodyBottom = cardBottom - Padding;
                        float cardCenterX = cardLeft + (cardWidth / 2.0f);
                        BodyClipMetrics bodyClipMetrics = ComputeBodyClipMetrics(cardWidth, occupiedWidth, Padding);
                        float bodyClipWidth = bodyClipMetrics.FinalClipWidth;
                        float bodyClipLeft = cardLeft + Math.Max(0f, (cardWidth - bodyClipWidth) / 2f);
                        float bodyClipRight = bodyClipLeft + bodyClipWidth;
                        Math.Max(0f, cardWidth - bodyClipWidth);
                        Math.Max(0f, bodyClipWidth - occupiedWidth);
                        Math.Max(0f, width - cardRight);
                        PaintSpanMetrics bodyPaintMetrics = ComputePaintSpanMetrics(
                            cardCenterX + occupiedMetrics.MinLeft,
                            cardCenterX + occupiedMetrics.MaxRight,
                            bodyClipLeft,
                            bodyClipRight);
                        ComputeUnionPaintSpanMetrics(
                            contentLeft,
                            contentRight,
                            numberPaintMetrics.PaintSpan,
                            semanticPaintMetrics.PaintSpan,
                            codePointPaintMetrics.PaintSpan);
                        ComputeUnionPaintSpanMetrics(
                            cardLeft,
                            cardRight,
                            bodyPaintMetrics,
                            numberPaintMetrics.PaintSpan,
                            semanticPaintMetrics.PaintSpan,
                            codePointPaintMetrics.PaintSpan);


                        if (_nativeTextBrush != 0)
                        {
                            RECT_F bodyClipRect = new RECT_F {
                                left = bodyClipLeft,
                                top = bodyTop,
                                right = bodyClipRight,
                                bottom = bodyBottom
                            };

                            int clipDepth = 0;
                            FormatRectF(bodyClipRect);
                            // Push card-local body-bounds clip
                            _renderTarget.PushAxisAlignedClip(bodyClipRect, 1); // D2D1_ANTIALIAS_MODE_PER_PRIMITIVE = 1
                            clipDepth++;

                            try
                            {
                                List<ManjuShaperCore.FinalGlyph> shaped = shapedCopy[i];
                                // Wrap-within-card when content overflows body region.
                                // Available body height = bodyBottom - bodyTop. Estimate glyph count per column as
                                // availableBodyHeight / cellHeight (32px at default). If total > fits, split glyphs
                                // into columns and draw each at its own X offset. First column gets amber ribbon.
                                // The card's width boundary is respected; columns
                                // may overflow visually if card too narrow.
                                float availableBodyHeight = Math.Max(1f, bodyBottom - bodyTop);
                                float cellHeightDip = FontSize; // nominal cell (fallbacks + half-cell caps only)
                                int totalGlyphs = shaped.Count;
                                // Implicit cap
                                int cappedGlyphCount = Math.Min(totalGlyphs, ImplicitMaxTotalGlyphs);
                                // Column breaks by MEASURED ink heights (a 1-em estimate under-counts
                                // vertical Manchu ink ~2-3× → columns would overflow bodyBottom and clip).
                                List<ManjuShaperCore.FinalGlyph> cappedShaped = (cappedGlyphCount < totalGlyphs)
                                    ? shaped.GetRange(0, cappedGlyphCount) : shaped;
                                List<float>? colPlanCenters = (_nativeFontFace != 0 && cappedGlyphCount > 0)
                                    ? ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(_nativeFontFace, cappedShaped, 0f, FontSize, _upem, 0f, CardBodyInkEdgeGapDesignUnits)
                                    : null;
                                // continuation columns yield one letter height (the first column starts flush).
                                List<int> colStarts = ComputeCardColumnBreaks(colPlanCenters, cappedGlyphCount, cellHeightDip, availableBodyHeight, FontSize);
                                int columnCount = colStarts.Count;
                                float ColTopOf(int col) => bodyTop + (col > 0 ? FontSize : 0f);
                                // Compute last-glyph (= focus codepoint)
                                // position. An EM-height approximation cellHeightDip * (idx+0.5)
                                // would diverge from DrawShapedText, which accumulates Y via ink-width
                                // metrics (variable per glyph), and misalign the highlight. So measure
                                // each glyph's actual screen Y
                                // center using ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters,
                                // index to the last glyph, and place the highlight rect there.
                                int lastGlyphCardIndex = cappedGlyphCount - 1;
                                if (columnCount <= 1)
                                {
                                    // If card #0's focused unit is a REAL fusion, shift the fused form LEFT so the separated
                                    // comparison to its right is not crammed against the card edge (a centred fused form looks cramped).
                                    // bodyCenterX drives the fused form's grid line + glyph + focus overlay; non-fusion/other
                                    // cards keep cardCenterX unchanged. The separated form sits a fixed gap right of bodyCenterX,
                                    // so both move left together → gap (and the grid-line↔dot clearance) preserved.
                                    var card0Fusion = (i == 0) ? GetFocusedCanonicalFusion() : null;
                                    float bodyCenterX = cardCenterX - (card0Fusion.HasValue ? FontSize * 0.6f : 0f);
                                    // Two-part highlight:
                                    //   (3a) narrow vertical GRID LINE to the LEFT of the focus glyph row
                                    //   (3b) FOCUS GLYPH OVERLAY — re-draw just the focus glyph in overlay color
                                    // A box does not work because Mongolian glyph ink extends beyond
                                    // a FontSize×FontSize square (XOffset/right-swung tails / etc).
                                    float baselineYForMeasure = GetTextBaselineY(bodyTop, FontSize);
                                    List<float>? glyphYCenters = (lastGlyphCardIndex >= 0 && _nativeFontFace != 0)
                                        ? ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(_nativeFontFace, shaped, baselineYForMeasure, FontSize, _upem, 0f, CardBodyInkEdgeGapDesignUnits)
                                        : null;

                                    // THE CARD BODY IS A VIEWPORT TOO, for the same reason the
                                    // prefix grid is.
                                    //
                                    // A card body holds the WHOLE hypothetical word (CandidateMatrix: the cell
                                    // is "pre-shaped via HarfBuzz on the FULL hypothetical input"), and the
                                    // ACTIVE glyph is the last one. Column wrapping already handles a body that
                                    // cannot fit the run at all, but a long run can "fit" one column by a whisker,
                                    // so the wrap never fires while the tail still runs past the bottom of the window.
                                    // Rendered, that is the active character drawn below the visible area,
                                    // although the one item being looked at must be fully
                                    // visible, not partially.
                                    //
                                    // So the run is offset until the active glyph is inside the body, the same
                                    // way the prefix grid offsets its slots. The head scrolls off the top, which
                                    // is the right end to lose: it is what the user typed a hundred keystrokes
                                    // ago. Every consumer below (grid line, focus overlay, separated-form alignment) reads
                                    // glyphYCenters, so shifting the measurements themselves keeps all of them
                                    // on the glyphs they are marking instead of needing four separate adjustments.
                                    float cardBodyScrollDy = 0f;
                                    if (glyphYCenters != null && lastGlyphCardIndex >= 0 && lastGlyphCardIndex < glyphYCenters.Count)
                                    {
                                        // bodyBottom is the CARD's bottom, and the card is sized from its
                                        // content — it is not bounded by the window, so it can sit below the
                                        // client area entirely: bodyBottom can report room while the run is already
                                        // at the window's last scanline. The
                                        // bound that matters to a user is the one they can see, so it is the
                                        // CLIENT bottom, and the card's own bottom only when that is smaller.
                                        float visibleBottom = Math.Min(bodyBottom, height - Padding);
                                        float activeCenter = glyphYCenters[lastGlyphCardIndex];
                                        float lowestVisibleCenter = visibleBottom - cellHeightDip * 0.5f;
                                        if (activeCenter > lowestVisibleCenter)
                                        {
                                            cardBodyScrollDy = activeCenter - lowestVisibleCenter;
                                            for (int gi = 0; gi < glyphYCenters.Count; gi++) glyphYCenters[gi] -= cardBodyScrollDy;
                                        }
                                    }

                                    if (lastGlyphCardIndex >= 0 && _nativeInnerGlyphHighlightBrush != 0)
                                    {
                                        // The vertical line covers the whole focus unit (an empty rime = initial consonant + tail),
                                        // the same range as the tint (3b), not only the last glyph. gridFocusN = that unit's glyph count (= FocusUnitGlyphCount, as in 3b).
                                        int gridFocusN = FocusUnitGlyphCount(i, cappedGlyphCount);
                                        int gridFocusStart = Math.Max(0, lastGlyphCardIndex - (gridFocusN - 1));
                                        float innerTop = (glyphYCenters != null && gridFocusStart < glyphYCenters.Count)
                                            ? glyphYCenters[gridFocusStart] : bodyTop + (gridFocusStart + 0.5f) * cellHeightDip;
                                        float innerBot = (glyphYCenters != null && lastGlyphCardIndex < glyphYCenters.Count)
                                            ? glyphYCenters[lastGlyphCardIndex] : bodyTop + (lastGlyphCardIndex + 0.5f) * cellHeightDip;
                                        // Grid line — manuscript-grid style. ~3 px wide, hugging the LEFT of the
                                        // focus cell. Height spans the whole focus UNIT (top of first glyph → bottom of last).
                                        float gridLineLeft = bodyCenterX - FontSize * 0.55f; // slight outward bias so line sits OUTSIDE glyph ink ( bodyCenterX = left-shifted for card0 fusion)
                                        RECT_F gridLineRect = new RECT_F {
                                            left = gridLineLeft,
                                            top = innerTop - cellHeightDip * 0.5f,
                                            right = gridLineLeft + 3f,
                                            bottom = innerBot + cellHeightDip * 0.5f
                                        };
                                        _renderTarget.FillRectangle(gridLineRect, _nativeInnerGlyphHighlightBrush);
                                    }

                                    // First pass: full shaped text in normal colour. ( bodyCenterX left-shifts the fused form for card0 fusion)
                                    ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, shaped, bodyCenterX, GetTextBaselineY(bodyTop, FontSize) - cardBodyScrollDy, FontSize, _nativeTextBrush, _upem, 0f, CardBodyInkEdgeGapDesignUnits);   // body viewport

                                    // Second pass: re-draw the focus glyph alone
                                    // with the overlay brush so it appears tinted in place. Y is
                                    // adjusted so the single-glyph render lands on the same Y the full
                                    // pass produced for that glyph (mirror of Mongolian-column
                                    // adjustedBaseline math).
                                    if (lastGlyphCardIndex >= 0 && _nativeFocusGlyphOverlayBrush != 0 && _nativeFontFace != 0
                                        && glyphYCenters != null && lastGlyphCardIndex < glyphYCenters.Count)
                                    {
                                        // Tint the WHOLE focus unit, not just the last glyph. An
                                        // empty-rime unit (dzy=ᡯᡳ) is initial consonant + tail = 2 glyphs → tinting only the last would leave the consonant
                                        // un-marked beside the marked tail. focusN = focus-unit glyph count.
                                        int focusN = FocusUnitGlyphCount(i, cappedGlyphCount);
                                        int focusStart = Math.Max(0, lastGlyphCardIndex - (focusN - 1));
                                        var focusOnly = new List<ManjuShaperCore.FinalGlyph>(lastGlyphCardIndex - focusStart + 1);
                                        for (int gi = focusStart; gi <= lastGlyphCardIndex; gi++) focusOnly.Add(shaped[gi]);
                                        var subAtZero = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(
                                            _nativeFontFace, focusOnly, 0f, FontSize, _upem, 0f, CardBodyInkEdgeGapDesignUnits);
                                        if (subAtZero != null && subAtZero.Count > 0 && focusStart < glyphYCenters.Count)
                                        {
                                            float adjustedBaseline = glyphYCenters[focusStart] - subAtZero[0];
                                            ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, focusOnly, bodyCenterX, adjustedBaseline, FontSize, _nativeFocusGlyphOverlayBrush, _upem, 0f, CardBodyInkEdgeGapDesignUnits);
                                        }
                                    }

                                    // Card #0 = the fused first candidate → bring out its separated structure to the
                                    // right of the (left-shifted) fused form + mark the current focus component (the comparison).
                                    if (card0Fusion.HasValue)
                                    {
                                        float hetiCenterY = (glyphYCenters != null && lastGlyphCardIndex >= 0 && lastGlyphCardIndex < glyphYCenters.Count)
                                            ? glyphYCenters[lastGlyphCardIndex] : (bodyTop + bodyBottom) * 0.5f;   // align the separated form to the fused row
                                        DrawCard0FentiStructure(bodyCenterX, cardRight, hetiCenterY, card0Fusion.Value.head, card0Fusion.Value.ligGid);
                                    }
                                }
                                else
                                {
                                    // Multi-column wrap
                                    float columnStride = FontSize * 1.2f + ColumnGap; // compact column width estimate; column gap separator
                                    float ribbonLeftEdge = cardCenterX - (columnCount * columnStride) / 2f;
                                    // Draw ribbon on left edge of first column
                                    if (_nativeRibbonBrush != 0)
                                    {
                                        RECT_F ribbonRect = new RECT_F {
                                            left = ribbonLeftEdge,
                                            top = bodyTop,
                                            right = ribbonLeftEdge + RibbonWidth,
                                            bottom = bodyBottom
                                        };
                                        _renderTarget.FillRectangle(ribbonRect, _nativeRibbonBrush);
                                    }
                                    // Inner-glyph highlight in the column
                                    // the last glyph lands in (multi-column case). Use per-glyph
                                    // measure of the LAST column's slice — that slice contains the
                                    // last glyph and the measurement is exact for those glyphs.
                                    // column membership/bounds come from the measured break list.
                                    int lastGlyphColumn = (lastGlyphCardIndex >= 0) ? ColumnOfGlyphIndex(colStarts, lastGlyphCardIndex) : -1;
                                    int lastGlyphRowInColumn = (lastGlyphCardIndex >= 0) ? (lastGlyphCardIndex - colStarts[lastGlyphColumn]) : -1;
                                    float lastColCenterX = (lastGlyphCardIndex >= 0)
                                        ? (ribbonLeftEdge + RibbonWidth + ColumnGap + lastGlyphColumn * columnStride + columnStride * 0.5f)
                                        : 0f;
                                    float lastFocusInnerY = 0f;
                                    bool lastFocusValid = false;
                                    if (lastGlyphCardIndex >= 0 && _nativeFontFace != 0)
                                    {
                                        // Measure the slice that contains the focus glyph.
                                        int sliceStartLast = colStarts[lastGlyphColumn];
                                        int sliceEndLast = (lastGlyphColumn + 1 < colStarts.Count) ? colStarts[lastGlyphColumn + 1] : cappedGlyphCount;
                                        var lastColSlice = new List<ManjuShaperCore.FinalGlyph>(sliceEndLast - sliceStartLast);
                                        for (int gi = sliceStartLast; gi < sliceEndLast; gi++) lastColSlice.Add(shaped[gi]);
                                        float baselineYForMeasure = GetTextBaselineY(ColTopOf(lastGlyphColumn), FontSize);   // yielding a cell
                                        List<float> sliceYCenters = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(
                                            _nativeFontFace, lastColSlice, baselineYForMeasure, FontSize, _upem);
                                        if (sliceYCenters != null && lastGlyphRowInColumn < sliceYCenters.Count)
                                        {
                                            lastFocusInnerY = sliceYCenters[lastGlyphRowInColumn];
                                            lastFocusValid = true;
                                        }
                                        else
                                        {
                                            lastFocusInnerY = ColTopOf(lastGlyphColumn) + (lastGlyphRowInColumn + 0.5f) * cellHeightDip;
                                            lastFocusValid = true;
                                        }
                                    }

                                    // Grid line on left of focus column row.
                                    if (lastFocusValid && _nativeInnerGlyphHighlightBrush != 0)
                                    {
                                        float gridLineLeft = lastColCenterX - FontSize * 0.55f;
                                        RECT_F gridLineRect = new RECT_F {
                                            left = gridLineLeft,
                                            top = lastFocusInnerY - cellHeightDip * 0.5f,
                                            right = gridLineLeft + 3f,
                                            bottom = lastFocusInnerY + cellHeightDip * 0.5f
                                        };
                                        _renderTarget.FillRectangle(gridLineRect, _nativeInnerGlyphHighlightBrush);
                                    }

                                    // Draw each column's glyph slice (first pass, normal text color).
                                    // slices follow the measured break list (variable per column).
                                    // continuation columns start one letter lower (yielding a cell; the first column starts flush).
                                    for (int col = 0; col < columnCount; col++)
                                    {
                                        int sliceStart = colStarts[col];
                                        int sliceEnd = (col + 1 < colStarts.Count) ? colStarts[col + 1] : cappedGlyphCount;
                                        if (sliceStart >= sliceEnd) break;
                                        var slice = new List<ManjuShaperCore.FinalGlyph>(sliceEnd - sliceStart);
                                        for (int gi = sliceStart; gi < sliceEnd; gi++) slice.Add(shaped[gi]);
                                        float colCenterX = ribbonLeftEdge + RibbonWidth + ColumnGap + col * columnStride + columnStride * 0.5f;
                                        ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, slice, colCenterX, GetTextBaselineY(ColTopOf(col), FontSize), FontSize, _nativeTextBrush, _upem);
                                    }

                                    // Second pass: re-draw the focus glyph alone
                                    // with the overlay brush in its actual column position.
                                    if (lastFocusValid && _nativeFocusGlyphOverlayBrush != 0 && _nativeFontFace != 0)
                                    {
                                        var focusOnly = new List<ManjuShaperCore.FinalGlyph> { shaped[lastGlyphCardIndex] };
                                        var singleAtZero = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(
                                            _nativeFontFace, focusOnly, 0f, FontSize, _upem, 0f, CardBodyInkEdgeGapDesignUnits);
                                        if (singleAtZero != null && singleAtZero.Count > 0)
                                        {
                                            float adjustedBaseline = lastFocusInnerY - singleAtZero[0];
                                            ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, focusOnly, lastColCenterX, adjustedBaseline, FontSize, _nativeFocusGlyphOverlayBrush, _upem, 0f, CardBodyInkEdgeGapDesignUnits);
                                        }
                                    }
                                }
                            }
                            finally
                            {
                                _renderTarget.PopAxisAlignedClip();
                                clipDepth = Math.Max(0, clipDepth - 1);
                            }
                        }

                        currentX = cardRight + Padding;   // running cursor only; card placement reads the strip
                    }

                    // "There is more off this end." A viewport with no such mark
                    // is worse than no viewport: the user cannot tell a short list from a scrolled one.
                    // Direction AND count, because the count is what says whether it is worth navigating.
                    if (!flashHideThisCycle && (strip.MoreBefore || strip.MoreAfter) && _nativeTextBrush != 0)
                    {
                        float indH = MetadataRowHeight;
                        float indY = currentY + Math.Max(0f, ComputeCardTotalHeight(ComputeItemHeight(0f, FontSize), FontSize, Padding)) - indH;
                        if (strip.MoreBefore)
                        {
                            ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                                _renderTarget, $"<{strip.Offset}", stripStartX - Padding, indY,
                                Padding * 2f + 10f, indH, PrefixLatinFontSize, _nativeTextBrush, true, true);
                        }
                        if (strip.MoreAfter)
                        {
                            int after = cardWidths.Count - 1 - strip.Cards[^1].Index;
                            ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                                _renderTarget, $"{after}>", stripStartX + stripAvailable - (Padding * 2f + 10f), indY,
                                Padding * 2f + 10f, indH, PrefixLatinFontSize, _nativeTextBrush, true, true);
                        }
                    }
                }

                // Dim overlay over the entire candidate region when the floating
                // Knowledge Window has focus context. The other side dims to a medium grey.
                // Drawn AFTER cards (so it overlays them) but BEFORE border (so the window's
                // outer border stays crisp).
                // Read focus state via the dictionary-module interface
                // (null when the module isn't compiled in → no dim).
                bool kwHasFocus = false;
                try { kwHasFocus = _engine?.DictionaryModule?.HasFocusContext == true; } catch { }
                if (kwHasFocus && _nativeUnfocusedOverlayBrush != 0)
                {
                    RECT_F dimRect = new RECT_F { left = 0, top = 0, right = width, bottom = height };
                    _renderTarget.FillRectangle(dimRect, _nativeUnfocusedOverlayBrush);
                }

                // 3. Draw Border (Always)
                if (_nativeBorderBrush != 0)
                {
                    RECT_F rectBorder = new RECT_F { left = 0.5f, top = 0.5f, right = width - 0.5f, bottom = height - 0.5f };
                    _renderTarget.DrawRectangle(rectBorder, _nativeBorderBrush, 1.0f, (nint)0);
                }

                // The switch toast, for a few seconds after a setting changes.
                if (_toastLine != null && _renderTarget != null
                    && Environment.TickCount64 < _toastUntilTick)
                {
                    DrawOverlayPanel(new[] { _toastLine }, width);
                }

                // Bottom-left corner: the ACTIVE theme's name (close light palettes
                // are hard to tell apart). Small + dim; gated by appearance.show_theme_name
                // (default false).
                if (ShowThemeName() && _renderTarget != null)
                {
                    nint tnBrush = _nativeMetaDimBrush != 0 ? _nativeMetaDimBrush : _nativeTextBrush;
                    if (tnBrush != 0)
                    {
                        string tn = "Theme: " + ThemeName(_themeIndex);
                        ManjuDirectWriteRenderer.DrawTextWithSystemFont(_renderTarget, tn, Padding + 1f, height - 16f, 170f, 14f, 9.5f, tnBrush);
                    }
                }


                _renderTarget.EndDraw(out _, out _);
            }
            catch (Exception)
            {
            }
        }

        // Draw four-column prefix block.
        //
        // Alignment: all three columns
        // (Index / Latin / Mongolian) share ONE fixed-row grid, rowYCenter(i) = baseY + (i + 0.5) *
        // NewColumnRowHeight. All three columns use this one alignment mechanism; a
        // per-glyph anchor would mix two reference frames and drift visually.
        //
        // Row model = tokenized UNITS (_latinUnits), not raw
        // keystrokes: a digraph ("ng"/"zh"/"tsh") is ONE row, Latin cell shows its canonical
        // Möllendorff form, and row indices equal the engine's unit positions (BuildCommitString /
        // candidate matrix / MarkPositionSelected all iterate units), so Latin cannot show
        // n+g on two rows against one ᠩ glyph.
        //
        // Index and Latin columns ALWAYS render across the full rowCount; the Mongolian
        // column is blank until selected (only selected positions render).
        //
        // highlight band shows whenever focusIdx >= 0.
        //
        // Edge case: a multi-codepoint unit (tsy/cy/jy → base+IY/I) shapes to 2 glyphs, so the
        // primary-candidate fallback glyph indices can drift after such a unit; the chosen-glyph
        // path (GetChosenGlyphAt, keyed by unit) is unaffected. Rare loan syllables only.
        // Reverse map of the font's GSUB ligatures (ligature gid → component gids),
        // cached. Used to decompose a velar + feminine-vowel fusion in the Mongolian preview column into its REAL components
        // (Gh + I) — the same GSUB type-4 source of truth as the dictionary's per-character column, NOT an FVS lookalike.
        private System.Collections.Generic.Dictionary<uint, uint[]>? _ligReverseMap;
        private System.Collections.Generic.Dictionary<uint, uint[]> GetLigatureReverseMap()
        {
            if (_ligReverseMap != null) return _ligReverseMap;
            var built = new System.Collections.Generic.Dictionary<uint, uint[]>();
            try
            {
                byte[]? fontData = _engine.GetFontData();
                if (fontData != null)
                {
                    unsafe
                    {
                        fixed (byte* pFont = fontData)
                        {
                            var reader = new ManjuFontReader(pFont, fontData.Length);
                            built = new ManjuGsubEngine(reader).BuildLigatureReverseMap();
                        }
                    }
                }
            }
            catch (Exception) { }
            _ligReverseMap = built;
            return _ligReverseMap;
        }

        // The decompose is font-table-driven: any GSUB ligature via the reverse map IS the gate → an empty
        // rime is exempt automatically, and it ports across fonts. A hardcoded velar+feminine Latin-unit gate
        // would be a font-specific hazard.

        // Shape a per-position codepoint slice (the unit's full codepoints, e.g. an
        // empty-rime base + tail ᡮᡟ) so the Mongolian preview column can render the WHOLE unit, not just the captured
        // last glyph. Vertical, same as the candidate path. Returns empty on failure (caller falls back to single).
        private List<ManjuShaperCore.FinalGlyph> ShapeSliceVertical(string codepoints)
        {
            try
            {
                byte[]? fontData = _engine.GetFontData();
                if (fontData != null && !string.IsNullOrEmpty(codepoints))
                {
                    unsafe
                    {
                        fixed (byte* pFont = fontData)
                            return ManjuShaperCore.FullShape(codepoints, pFont, fontData.Length, isVertical: true);
                    }
                }
            }
            catch (Exception) { }
            return new List<ManjuShaperCore.FinalGlyph>();
        }

        // How many glyphs the FOCUS unit contributes at the END of candidate `cardIndex`'s
        // shaped sequence — normally 1, but an empty-rime unit (a sibilant base + the empty-rime tail, e.g. dzy=ᡯᡳ) is 2 glyphs at
        // ONE unit. A focus-glyph tint on only the LAST glyph would mark only the tail of an empty rime;
        // this lets the tint cover the WHOLE focus unit. Read from the per-candidate focus
        // slice (the codepoints this candidate contributes at the focus) and shape it; clamp to available glyphs.
        private int FocusUnitGlyphCount(int cardIndex, int availableGlyphs)
        {
            try
            {
                if (_slicesAtFocus != null && cardIndex >= 0 && cardIndex < _slicesAtFocus.Count)
                {
                    string? slice = _slicesAtFocus[cardIndex];
                    if (!string.IsNullOrEmpty(slice) && slice.Length > 1)
                    {
                        int n = ShapeSliceVertical(slice).Count;
                        if (n > 1) return Math.Min(n, Math.Max(1, availableGlyphs));
                    }
                }
            }
            catch { }
            return 1;
        }

        private unsafe void DrawFourColumnPrefix(List<List<ManjuShaperCore.FinalGlyph>> shapedCopy, float windowHeightDip)
        {
            if (_renderTarget == null) return;
            EnsureUnitsCache(); // one row per tokenized unit
            int rowCount = _latinUnits.Count;
            if (rowCount == 0) return;

            int focusIdx = ResolveFocusIndex(rowCount);

            float baseX = Padding;
            float baseY = Padding;

            // Internal scroll: rows beyond the (screen-capped) window do not fall off
            // the canvas: compute the visible-row capacity from the ACTUAL window height, then scroll so the
            // current item is always visible. Target = explicit focus row (focus-stays semantics) or the TAIL
            // row while focus is sentinel (user typing without selecting yet).
            // Scroll / visibility / row-Y operate in DISPLAY-SLOT space (unit rows
            // + standalone separator rows). focusIdx stays a UNIT index; its slot is SlotOfUnit(focusIdx).
            int slotCount = DisplaySlotCount;
            int focusSlot = SlotOfUnit(focusIdx);
            int visibleRows = Math.Max(1, (int)((windowHeightDip - baseY - Padding) / NewColumnRowHeight));
            _lastVisibleRowsForScroll = visibleRows;   // pan handler reads the real capacity
            int scrollTarget = (_focusedRowIndex >= 0) ? focusSlot : slotCount - 1;
            // While the user is touch-panning (until the next focus/input change), keep the manual
            // offset — only clamp; the auto-follow target would yank the view back mid-swipe.
            if (_userScrollOverride)
                _prefixScrollOffset = ApplyManualScrollDelta(_prefixScrollOffset, 0, slotCount, visibleRows);
            else
                _prefixScrollOffset = ComputePrefixScrollOffset(_prefixScrollOffset, scrollTarget, slotCount, visibleRows);
            int scrollOff = _prefixScrollOffset;
            int firstVisible = scrollOff, lastVisible = Math.Min(slotCount - 1, scrollOff + visibleRows - 1);
            bool SlotVisible(int slot) => slot >= firstVisible && slot <= lastVisible;
            bool RowVisible(int unitIndex) => SlotVisible(SlotOfUnit(unitIndex));   // unit-space convenience

            _lastViewportFirstVisible = scrollOff;
            _lastViewportLastVisible = Math.Min(slotCount - 1, scrollOff + visibleRows - 1);
            _lastLatestInputUnit = rowCount - 1;

            // unified fixed-row grid. Same mechanism for all three columns. shifted by scroll
            // offset. i is a DISPLAY SLOT — unit content is drawn at rowYCenter(SlotOfUnit(unit)).
            float rowYCenter(int slot) => baseY + (slot - scrollOff + 0.5f) * NewColumnRowHeight;

            int shapedFirstCountForLog = (shapedCopy != null && shapedCopy.Count > 0 && shapedCopy[0] != null) ? shapedCopy[0].Count : 0;

            // Highlight band at focused row: shows whenever focus is set.
            // Band height = NewColumnRowHeight (one full row).
            bool bandWillDraw = (focusIdx >= 0 && focusIdx < rowCount && _nativeHighlightBrush != 0 && RowVisible(focusIdx));
            if (bandWillDraw)
            {
                float focusY = rowYCenter(focusSlot);   // focused UNIT drawn at its display slot
                float bandTop = focusY - NewColumnRowHeight * 0.5f;
                float bandBottom = focusY + NewColumnRowHeight * 0.5f;
                float bandLeft = baseX;
                // Extend the band right edge to cover right-extending Mongolian
                // glyphs, but back off a few px so it does NOT sit flush against the candidate cards
                // or press into the border. baseX + PrefixRegionWidth is exactly
                // the cards' start X; subtract a small gap so there's visible breathing room without
                // widening the window. ~3px leaves a visible gap while still covering the glyphs.
                const float BandRightInset = 3f;
                float bandRight = baseX + PrefixRegionWidth - BandRightInset;
                RECT_F bandRect = new RECT_F
                {
                    left = bandLeft,
                    top = bandTop,
                    right = bandRight,
                    bottom = bandBottom,
                };
                _renderTarget.FillRectangle(bandRect, _nativeHighlightBrush);
            }

            // Index column — always renders, one row per input character.
            // Y param is the metadata bbox top, centered on rowYCenter(i).
            float colX = baseX + NewColumnGap;
            for (int slot = firstVisible; slot <= lastVisible; slot++)   // draw visible rows only slot space
            {
                int unit = _displaySlotUnit[slot];
                if (unit < 0) continue;   // separator slot — drawn in the separator pass below
                string idxText = (unit + 1).ToString();   // number = engine UNIT position (separators are not numbered)
                float rowYTop = rowYCenter(slot) - MetadataRowHeight * 0.5f;
                DrawMetadataText(idxText, colX, rowYTop, IndexColumnWidth, baseFontSize: PrefixLatinFontSize, bold: true);   // digits enlarged and bold
            }
            colX += IndexColumnWidth + NewColumnGap;

            // Latin column — one row per tokenized UNIT, showing the canonical
            // Möllendorff display form ("ng" one row; "zh"→"ž", "tsh"→"c", "v"→"ū") — the public-facing
            // romanization, regardless of which input alias was typed.
            var latinUnitsSafe = _latinUnits ?? new List<string>();
            // Shared per-unit iteration (same helper the knowledge window uses, so a
            // per-unit change lands in one place). Main's display units span 1 code point each
            // (cpLen=1).
            var latinUnitsForLayout = new List<(string text, int cpLen)>(latinUnitsSafe.Count);
            foreach (var u in latinUnitsSafe) latinUnitsForLayout.Add((u, 1));
            float latinColX = colX;   // capture: colX is reassigned right after the loop
            CSharpTSFInput.UI.LatinUnitLayout.ForEachUnit(latinUnitsForLayout, rowCount, (text, row) =>
            {
                // Main candidate Latin column shows UPPERCASE (vertical-script convention;
                // the Knowledge Window uppercases too). Display-only (underlying data unchanged).
                if (RowVisible(row))   // draw visible rows only row is a UNIT → draw at its display slot
                    DrawMetadataText((text ?? string.Empty).ToUpperInvariant(), latinColX, rowYCenter(SlotOfUnit(row)) - MetadataRowHeight * 0.5f, LatinColumnWidth, baseFontSize: PrefixLatinFontSize, bold: true);   // Latin and apostrophe enlarged and bold
                return true;
            });
            colX += LatinColumnWidth + NewColumnGap;

            // Draw each REAL separator on ITS OWN row, CENTERED across the
            // prefix region, not glued to the tail of the previous letter. Consecutive separators already
            // collapsed to one row in the engine model. This is a display-only row (non-focusable): the band,
            // the number column and the candidate window never target it.
            if (_nativeTextBrush != 0)
            {
                float sepRegionRight = baseX + PrefixRegionWidth;
                for (int slot = firstVisible; slot <= lastVisible; slot++)
                {
                    if (slot < 0 || slot >= _displaySlotUnit.Count) continue;
                    if (_displaySlotUnit[slot] >= 0) continue;   // unit slot — not a separator
                    string sepGlyph = (slot < _displaySlotSep.Count) ? _displaySlotSep[slot] : "·";
                    float sepYTop = rowYCenter(slot) - MetadataRowHeight * 0.5f;
                    ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                        _renderTarget, sepGlyph, baseX, sepYTop, sepRegionRight - baseX, MetadataRowHeight,
                        PrefixLatinFontSize, _nativeTextBrush, centerHorizontally: true, bold: true);
                }
            }

            // Mongolian column: render ONLY selected positions (blank-until-selected).
            // Each selected glyph renders as a single-glyph DrawShapedText with adjustedBaseline so the
            // glyph's visual center lands at the unified rowYCenter(i) — same grid as Index/Latin.
            // ink-width still applies within the single-glyph render (just at offset 0 since
            // we measure the single glyph at baseline 0 and offset to the target row).
            List<ManjuShaperCore.FinalGlyph>? shapedFirst = null;
            if (shapedCopy != null && shapedCopy.Count > 0 && shapedCopy[0] != null && shapedCopy[0].Count > 0)
            {
                shapedFirst = shapedCopy[0];
            }
            if (shapedFirst != null && _nativeFontFace != 0 && _nativeTextBrush != 0)
            {
                float colCenterX = colX + MongolianColumnWidth / 2f;                                    // col3 (the main column) = fused
                float col4CenterX = colX + MongolianColumnWidth + NewColumnGap + HefiColumnWidth * 0.5f; // col4 = separated

                // Applies to ALL true-fusion ligatures.
                // ANY selected position whose chosen glyph is a GSUB ligature (feminine velar+vowel, b/p+vowel incl. BU/BA,
                // ng+consonant incl. ng+g, a masculine loan velar) is decomposed via the font's ligature reverse map into
                // its REAL components (Gh+I etc., NOT FVS lookalikes) across the spanned positions, so the preview shows
                // separated per-letter components, UNIFORMLY with the dictionary's per-character column (dictionary and main window agree). An empty rime is NOT a GSUB
                // ligature (2 separate glyphs) → never in the map → stays fused. Commit codepoints are
                // unchanged (a display-only override) → the host re-shapes the fused syllable on commit.
                // CANONICAL fusion map — the single source of truth for "is this position a REAL fusion".
                // A glyph in the full-word shape whose cluster span >1 codepoint is a real font merge. The GSUB
                // ligature reverse-map (ligMap) OVER-REPORTS: single-letter medial forms (y=gid170, a=gid10 …) are
                // flagged as "ligatures" though they never merge in context. So gate BOTH the separated decompose (col3,
                // below) AND the fused column (col4) on THIS canonical map, not the raw ligMap. An empty rime = 2 separate glyphs
                // (span 1 each) → not in the map → stays fused. Note: judge fusion by render /
                // cluster-span, NOT the GSUB table.
                var canonFusions = new Dictionary<int, (uint ligGid, int span)>();
                {
                    string cpsCanon = _engine.GetTranslatedTextFromSnapshot(_rawInput ?? string.Empty);
                    byte[]? fontDataCanon = _engine.GetFontData();
                    if (!string.IsNullOrEmpty(cpsCanon) && fontDataCanon != null && fontDataCanon.Length > 0)
                    {
                        List<ManjuShaperCore.FinalGlyph> canon;
                        fixed (byte* pFontCanon = fontDataCanon)
                            canon = ManjuShaperCore.FullShape(cpsCanon, pFontCanon, fontDataCanon.Length, isVertical: true);
                        for (int ci = 0; ci < canon.Count; ci++)
                        {
                            int startCl = (int)canon[ci].Cluster;
                            int endCl = (ci + 1 < canon.Count) ? (int)canon[ci + 1].Cluster : cpsCanon.Length;
                            int sp = endCl - startCl;
                            if (sp >= 2 && startCl >= 0 && startCl < rowCount)
                                canonFusions[startCl] = (canon[ci].GlyphId, Math.Min(sp, rowCount - startCl));
                        }
                    }
                }

                // col3, the main column, shows the form actually chosen; col4 shows the
                // **per-element progress** of that choice (whichever element is chosen is the row shown), not a whole-group separated comparison: the fourth column reflects only the currently selected element's own separated form and progress.
                // The gate for taking part in a fusion is that the chosen gid **exactly equals the group's canonical
                // fusion gid** (canonFusions[head].ligGid). Never use ligMap membership to judge fusion: the GSUB
                // reverse table over-reports (B's FVS variants gid=83/1194 are in it too), so a non-fusion pick would be taken for a fusion, go through group rendering, centre across rows and swallow the neighbouring position's display. ligMap is only for decomposition.
                //   fusionPickers[head] = the positions in the group that **actually chose the fused form**; a separate set records whether any position in the group chose a non-fused form.
                //   absorbed = the fusionPickers positions (only those are drawn by the group render); a position with a non-fusion pick goes through the main loop and draws on its own row, so it neither spans rows nor overwrites a neighbour.
                var ligMap = GetLigatureReverseMap();
                var fusionPickers = new Dictionary<int, List<int>>();    // fusion-group head → the positions that chose the fused form
                var groupHasNonFusionPick = new HashSet<int>();          // the group contains a non-fusion pick (→ mixed mode, the fused form does not span rows)
                var absorbedByHefi = new HashSet<int>();                 // positions drawn by the group render (= the fusion pickers)
                foreach (var kv in canonFusions)
                {
                    int head = kv.Key; int span = kv.Value.span; uint canonGid = kv.Value.ligGid;
                    for (int p = head; p < head + span && p < rowCount; p++)
                    {
                        if (!_engine.IsPositionSelected(p)) continue;
                        var cg = _engine.GetChosenGlyphAt(p);
                        uint g = cg.HasValue ? cg.Value.GlyphId : 0u;
                        if (g == canonGid)                                    // exactly the canonical fusion gid = the fused form really was chosen
                        {
                            if (!fusionPickers.TryGetValue(head, out var lst)) { lst = new List<int>(); fusionPickers[head] = lst; }
                            lst.Add(p);
                            absorbedByHefi.Add(p);
                        }
                        else
                        {
                            groupHasNonFusionPick.Add(head);                  // a non-fusion pick → the main loop draws it on its own row
                            if (g != 0 && ligMap != null) ligMap.ContainsKey(g);
                        }
                    }
                }

                // Main loop: a **selected** position that is not in a fusion group, or did not choose the fused form → col3 renders what was actually chosen (an empty rime's whole-syllable sequence included).
                // Positions covered by a group that chose the fused form are skipped (the group render below handles them). Walk the full rowCount and render each position's persistent GetChosenGlyphAt, independent of focus.
                for (int i = 0; i < rowCount; i++)
                {
                    if (!RowVisible(i)) continue;                          // draw visible rows only
                    if (absorbedByHefi.Contains(i)) continue;              // rendered together by the fused-form group below
                    if (!_engine.IsPositionSelected(i)) continue;          // blank-until-selected
                    ManjuShaperCore.FinalGlyph glyphToRender;
                    ManjuShaperCore.FinalGlyph? chosen = _engine.GetChosenGlyphAt(i);
                    if (chosen.HasValue) { glyphToRender = chosen.Value; }
                    else if (i < shapedFirst.Count) { glyphToRender = shapedFirst[i]; }
                    else continue;
                    // An empty rime is a whole syllable: re-shape the chosen slice (initial consonant + tail together); a single letter degrades to one glyph.
                    List<ManjuShaperCore.FinalGlyph> rowGlyphs;
                    string? cpSlice = _engine.GetChosenSliceAt(i);
                    var seq = (cpSlice != null && cpSlice.Length > 1) ? ShapeSliceVertical(cpSlice) : null;
                    if (seq != null && seq.Count > 1) { rowGlyphs = seq; }
                    else rowGlyphs = new List<ManjuShaperCore.FinalGlyph> { glyphToRender };
                    var rowCenters = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(_nativeFontFace, rowGlyphs, 0f, FontSize, _upem, 0f);
                    if (rowCenters.Count == 0) continue;
                    float groupMid = (rowCenters[0] + rowCenters[rowCenters.Count - 1]) * 0.5f;
                    float adjustedBaseline = rowYCenter(SlotOfUnit(i)) - groupMid;   // unit → display slot
                    ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, rowGlyphs, colCenterX, adjustedBaseline, FontSize, _nativeTextBrush, _upem, 0f);
                }

                // Fusion-group rendering, with per-element semantics:
                //   · SPAN mode (there is a fused pick and the group has no non-fusion pick): col3 renders the fused glyph
                //     centred canonically across head..head+span-1 (it does not follow which side was picked); col4 renders the fused pick's own separated component (whichever was chosen is what is shown, as a progress comparison).
                //   · DEGRADE mode (a fused pick and a non-fusion pick coexist): **the whole group
                //     degrades to pure separated forms**; no fused form can stand in the group any more (after commit the FVS
                //     breaks the ligature, so the fused form would never appear), and col3 renders **each fused pick's own
                //     separated component** (comps[p-head]), matching what commit actually produces. Rendering the fused glyph on a single row is forbidden: it would cram the whole fused character into one row, which shows as stray short strokes between letters. col4 is left blank, because col3 is already the separated form and drawing the same component twice is redundant.
                foreach (var kv in fusionPickers)
                {
                    int head = kv.Key; List<int> pickers = kv.Value;
                    int span = canonFusions[head].span; uint hgid = canonFusions[head].ligGid;
                    bool degraded = groupHasNonFusionPick.Contains(head);
                    uint[]? comps = null;
                    if (ligMap != null) ligMap.TryGetValue(hgid, out comps);
                    if (!degraded)
                    {
                        var fused = new List<ManjuShaperCore.FinalGlyph> { new ManjuShaperCore.FinalGlyph { GlyphId = hgid, Cluster = (uint)head } };
                        var frc = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(_nativeFontFace, fused, 0f, FontSize, _upem, 0f);
                        if (frc.Count > 0 && SlotOfUnit(head) <= lastVisible && SlotOfUnit(head + span - 1) >= firstVisible)   // render only when the group intersects the visible window slot space
                        {
                            float rowMid = (rowYCenter(SlotOfUnit(head)) + rowYCenter(SlotOfUnit(head + span - 1))) * 0.5f;   // centred on the canonical group's rows
                            ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, fused, colCenterX, rowMid - frc[0], FontSize, _nativeTextBrush, _upem, 0f);
                        }
                        if (comps != null && comps.Length >= 2)   // col4: only the fused pick's own separated component
                        {
                            foreach (int p in pickers)
                            {
                                int k = p - head;
                                if (k < 0 || k >= comps.Length || p >= rowCount) continue;
                                if (!RowVisible(p)) continue;
                                var fg = new List<ManjuShaperCore.FinalGlyph> { new ManjuShaperCore.FinalGlyph { GlyphId = comps[k], Cluster = (uint)p } };
                                var crc = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(_nativeFontFace, fg, 0f, FontSize, _upem, 0f);
                                if (crc.Count == 0) continue;
                                ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, fg, col4CenterX, rowYCenter(SlotOfUnit(p)) - crc[0], FontSize, _nativeTextBrush, _upem, 0f);
                            }
                        }
                    }
                    else
                    {
                        // The whole group degrades: a fused pick renders its own separated component in col3 (with
                        // no comps it falls back to that position's primary form, and never renders a fragment of the fused glyph). col4 is not drawn.
                        foreach (int p in pickers)
                        {
                            if (!RowVisible(p)) continue;
                            int k = p - head;
                            ManjuShaperCore.FinalGlyph cell;
                            if (comps != null && k >= 0 && k < comps.Length)
                                cell = new ManjuShaperCore.FinalGlyph { GlyphId = comps[k], Cluster = (uint)p };
                            else if (p < shapedFirst.Count)
                                cell = shapedFirst[p];
                            else
                                continue;
                            var cellList = new List<ManjuShaperCore.FinalGlyph> { cell };
                            var crc = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(_nativeFontFace, cellList, 0f, FontSize, _upem, 0f);
                            if (crc.Count == 0) continue;
                            ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, cellList, colCenterX, rowYCenter(SlotOfUnit(p)) - crc[0], FontSize, _nativeTextBrush, _upem, 0f);
                        }
                    }
                }
            }

            // No separate current-unit marker: the focus band and the candidate column already express
            // which letter is being worked on.
        }

        // Detect whether the focused unit is part of a REAL canonical fusion (full-word shape, cluster-span >1).
        // Returns (head, span, fused ligature gid) or null. Over-report-immune (fusion is judged by render/cluster-span,
        // NOT GSUB). Used to (a) left-shift card #0's fused form to make room (a centred fused form looks cramped) and (b) drive the separated comparison.
        // Focus is RESOLVED (the sentinel -1 goes through ResolveFocusIndex), the same source as the candidate
        // window's content (worker focusResolved) and every other consumer (band/pick/nav all go through
        // ResolveFocusIndex). A raw `_focusedRowIndex < 0 → null` would make the fused + separated special first card
        // silently skip during ANY fresh composition typed without touching focus (IME switch-back, Esc-restart).
        private unsafe (int head, int span, uint ligGid)? GetFocusedCanonicalFusion()
        {
            int focusIdx = ResolveFocusIndex(GetCurrentRowCount());
            if (focusIdx < 0) return null;
            string cps = _engine.GetTranslatedTextFromSnapshot(_rawInput ?? string.Empty);
            byte[]? fontData = _engine.GetFontData();
            if (string.IsNullOrEmpty(cps) || fontData == null || fontData.Length == 0) return null;
            List<ManjuShaperCore.FinalGlyph> canon;
            fixed (byte* pf = fontData) canon = ManjuShaperCore.FullShape(cps, pf, fontData.Length, isVertical: true);
            for (int ci = 0; ci < canon.Count; ci++)
            {
                int s = (int)canon[ci].Cluster;
                int e = (ci + 1 < canon.Count) ? (int)canon[ci + 1].Cluster : cps.Length;
                if (e - s >= 2 && focusIdx >= s && focusIdx < e)
                {
                    uint ligGid = canon[ci].GlyphId;
                    // DEGRADE: if any position in this fusion group is already chosen as a
                    // NON-fusion (separated) form, the fused form no longer exists → card #0 must NOT show the fused + separated two-
                    // column shell: a shell whose contents are separated forms is useless and misleading; fall straight back to the ordinary separated candidate and leave no empty shell.
                    // Same gate as the prefix DEGRADE: a selected position whose chosen glyph ≠ the
                    // canonical fusion gid. Return null → card #0 renders as a plain (separated) candidate, no shell.
                    int rc = GetCurrentRowCount();
                    for (int p = s; p < e && p < rc; p++)
                    {
                        if (!_engine.IsPositionSelected(p)) continue;
                        var cg = _engine.GetChosenGlyphAt(p);
                        if (cg.HasValue && cg.Value.GlyphId != ligGid)
                        {
                            return null;
                        }
                    }
                    return (s, e - s, ligGid);
                }
            }
            return null;
        }


        // When card #0 will carry the separated comparison (the focused unit is a canonical fusion), floor its
        // width so the two columns (fused + separated) lay out at natural spacing inside a normal-sized card, instead of
        // the comparison being clamped into a content-minimum (1-glyph) card. Only raises; a naturally-wider card (e.g.
        // long metadata) is left as-is. Applied to BOTH the window-sizing pass and the paint pass so the reserved
        // width is consistent (window is wide enough for the card it will draw).
        // `applies` comes from the APPROVED SNAPSHOT (LayoutSnapshot.FusionFloorApplies).
        // Passing null falls back to a live probe, which is only correct for paint-time callers that are
        // already reading live state; the sizing path must always pass the snapshot's decision, or a
        // snapshot approved with a 132 DIP floor can be computed against later live state needing 100.
        private void ApplyFusionCardWidthFloor(List<float> cardWidths, bool? applies = null)
        {
            if (cardWidths == null || cardWidths.Count == 0) return;
            bool floorApplies = applies ?? GetFocusedCanonicalFusion().HasValue;
            if (!floorApplies) return;
            float floor = GetFusionCardWidthFloor(FontSize, Padding);
            if (cardWidths[0] < floor)
            {
                cardWidths[0] = floor;
            }
        }

        // Candidate card #0 (the fused first candidate) brings out its separated structure to the RIGHT of the
        // (left-shifted) fused form, with the current focus component marked the SAME way as the main current-letter focus (manuscript-grid
        // line + focus-overlay tint, NOT a separate box). Components = the SAME real reverse-map parts the separated column
        // uses → the card's comparison matches the prefix's separated column. hetiCenterX = the fused form's (shifted) centre; hetiCenterY = its vertical
        // centre, at the same height. Drawn in the card's right portion, which ApplyFusionCardWidthFloor reserves. Skips if there is no room.
        private unsafe void DrawCard0FentiStructure(float hetiCenterX, float cardRight, float hetiCenterY, int head, uint ligGid)
        {
            if (_renderTarget == null || _nativeFontFace == 0 || _nativeTextBrush == 0) return;
            var ligMap = GetLigatureReverseMap();
            if (ligMap == null || !ligMap.TryGetValue(ligGid, out var comps) || comps.Length < 2) return;
            // resolved focus (the sentinel -1 goes through ResolveFocusIndex), same source as GetFocusedCanonicalFusion, else the
            // current-component marker vanishes during sentinel typing while the card itself renders.
            int focusComp = ResolveFocusIndex(GetCurrentRowCount()) - head;
            float compSize = FontSize * 0.78f;
            float colCenterX = hetiCenterX + FontSize * 1.6f;               // a wider gap so the focus rule can sit at its NORMAL distance from the separated form yet still clear the fused form's right-hand dot
            if (colCenterX + compSize * 0.6f > cardRight - 2f)             // tight card → clamp inward; skip only if truly none
            {
                colCenterX = cardRight - 2f - compSize * 0.6f;
                if (colCenterX < hetiCenterX + FontSize * 0.9f) { return; }
            }
            // The separated components are stacked as ONE run at the 80 ink-edge gap (compact, the dictionary's per-character spacing); centred
            // vertically on the fused form, at the same height.
            var compGlyphs = new List<ManjuShaperCore.FinalGlyph>(comps.Length);
            for (int k = 0; k < comps.Length; k++) compGlyphs.Add(new ManjuShaperCore.FinalGlyph { GlyphId = comps[k], Cluster = (uint)k });
            var centers = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(
                _nativeFontFace, compGlyphs, 0f, compSize, _upem, 0f, ManjuDirectWriteRenderer.InkEdgeGapDesignUnits);
            if (centers.Count == 0) return;
            float stackMid = (centers[0] + centers[centers.Count - 1]) * 0.5f;
            float baseline = hetiCenterY - stackMid;
            // Focus = the SAME drawing as the current-letter focus (grid line + tint). The grid line hugs the separated form's LEFT edge
            // (just left of its ink), clear of the fused form's right-extending dot.
            if (focusComp >= 0 && focusComp < centers.Count && _nativeInnerGlyphHighlightBrush != 0)
            {
                float cyf = baseline + centers[focusComp];
                float gridLineLeft = colCenterX - compSize * 0.55f;         // NORMAL distance (the same bias as the card-body bu grid line); the wider gap above keeps it clear of the fused form's dot
                var gridLineRect = new RECT_F { left = gridLineLeft, top = cyf - compSize * 0.5f, right = gridLineLeft + 3f, bottom = cyf + compSize * 0.5f };
                _renderTarget.FillRectangle(gridLineRect, _nativeInnerGlyphHighlightBrush);
            }
            ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, compGlyphs, colCenterX, baseline, compSize, _nativeTextBrush, _upem, 0f, ManjuDirectWriteRenderer.InkEdgeGapDesignUnits);
            if (focusComp >= 0 && focusComp < compGlyphs.Count && _nativeFocusGlyphOverlayBrush != 0)
            {
                var focusOnly = new List<ManjuShaperCore.FinalGlyph> { compGlyphs[focusComp] };
                var subC = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(_nativeFontFace, focusOnly, 0f, compSize, _upem, 0f);
                if (subC.Count > 0)
                {
                    float fBaseline = baseline + centers[focusComp] - subC[0];
                    ManjuDirectWriteRenderer.DrawShapedText(_renderTarget, _nativeFontFace, focusOnly, colCenterX, fBaseline, compSize, _nativeFocusGlyphOverlayBrush, _upem, 0f);
                }
            }
        }

        /// <summary>
        /// Draw metadata text using system font with auto-sizing.
        /// When text exceeds card width, font size is reduced proportionally (floor: MinMetadataFontSize).
        /// </summary>
        private unsafe MetadataPaintMetrics DrawMetadataText(string text, float x, float y, float cardWidth, nint brushOverride = 0,
            float baseFontSize = 0f, bool bold = false)
        {
            // baseFontSize/bold let the prefix Latin+digit+apostrophe columns render LARGER
            // and BOLD, enlarged so a learner can tell them apart; the default 0 uses MetadataFontSize and normal weight, as card metadata does.
            float baseSize = baseFontSize > 0.01f ? baseFontSize : MetadataFontSize;
            if (string.IsNullOrEmpty(text) || _nativeTextBrush == 0 || _renderTarget == null || _nativeFontFace == 0)
            {
                return default;
            }

            // Use real shaping pipeline for width estimation
            byte[]? fontData = _engine.GetFontData();
            if (fontData == null)
            {
                return default;
            }

            try
            {
                List<ManjuShaperCore.FinalGlyph> glyphs;
                fixed (byte* pFont = fontData)
                {
                    // Shape horizontally (isVertical: false) for width estimation
                    glyphs = ManjuShaperCore.FullShape(text, pFont, fontData.Length, isVertical: false);
                }

                if (glyphs.Count == 0)
                {
                    return default;
                }

                // Calculate total width at default font size for width estimation
                float totalWidth = 0;
                float defaultScale = baseSize / (float)_upem;
                foreach (var g in glyphs)
                {
                    totalWidth += g.XAdvance * defaultScale;
                }

                // Auto-size: reduce font if text overflows card width (with padding margin)
                float usableWidth = cardWidth - 4.0f; // 2px margin each side
                float effectiveFontSize = baseSize;
                if (totalWidth > usableWidth && usableWidth > 0)
                {
                    effectiveFontSize = baseSize * (usableWidth / totalWidth);
                    if (effectiveFontSize < MinMetadataFontSize)
                        effectiveFontSize = MinMetadataFontSize;
                }

                // Recalculate width at effective font size for centering
                float effectiveScale = effectiveFontSize / (float)_upem;
                float effectiveWidth = 0;
                foreach (var g in glyphs)
                {
                    effectiveWidth += g.XAdvance * effectiveScale;
                }

                // Center text horizontally in the card
                float textX = x + (cardWidth - effectiveWidth) / 2.0f;
                PaintSpanMetrics paintSpanMetrics = ComputePaintSpanMetrics(textX, textX + effectiveWidth, x, x + cardWidth);

                // Use system font with fallback for metadata (avoids tofu for CJK/Latin text)
                // brushOverride: row 3 (Unicode ground truth) draws with the theme-following de-emphasis brush.
                ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                    _renderTarget,
                    text,
                    x,
                    y,
                    cardWidth,
                    MetadataRowHeight,
                    effectiveFontSize,
                    brushOverride != 0 ? brushOverride : _nativeTextBrush,
                    centerHorizontally: true,
                    bold: bold
                );
                return new MetadataPaintMetrics(glyphs.Count, effectiveWidth, textX, textX + effectiveWidth, paintSpanMetrics);
            }
            catch (Exception)
            {
                return default;
            }
        }

        private void GetLayoutDpi(out float dpiX, out float dpiY)
        {
            dpiX = 96.0f;
            dpiY = 96.0f;

            if (_renderTarget != null)
            {
                try
                {
                    _renderTarget.GetDpi(out dpiX, out dpiY);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MANJU_IME: [SUPPRESSED] GetLayoutDpi fell back to 96 DPI after render-target getter failure: {ex.GetType().Name}: {ex.Message}");
                }
            }
            else if (_d2dFactory != null)
            {
                try
                {
                    _d2dFactory.GetDesktopDpi(out dpiX, out dpiY);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"MANJU_IME: [SUPPRESSED] GetLayoutDpi fell back to 96 DPI after factory getter failure: {ex.GetType().Name}: {ex.Message}");
                }
            }

            dpiX = NormalizeDpi(dpiX);
            dpiY = NormalizeDpi(dpiY);
        }

        private float GetLayoutDpiX()
        {
            GetLayoutDpi(out float dpiX, out _);
            return dpiX;
        }


        private void PostHide()
        {
            // Third copy of the guard. If this refuses to post, the hide never reaches
            // the UI thread at all and HideWindowState's OS check above never runs.
            if (_hwnd != 0 && (_windowVisible || NativeMethods.IsWindowVisible(_hwnd)))
            {
                // Ensure UI operation on correct thread via PostMessage
                NativeMethods.PostMessage(_hwnd, WM_APP_HIDE, 0, 0);
            }
        }

        public void Destroy()
        {
            Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed) return;
            _engine.ConfigurationChanged -= OnConfigurationChanged;

            // Stop routing deliveries through a window thread that is about to stop
            // pumping. Must come before the HWND teardown below.
            CancelLayoutRetriesForLifecycleChange(null);
            UninstallFrameDeliveryDispatcher();

            if (_hwnd != 0)
            {
                // Clear USERDATA to prevent callbacks during destruction
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_USERDATA, 0);
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = 0;
            }

            // Free GCHandle
            if (_selfHandle.IsAllocated)
            {
                _selfHandle.Free();
            }

            // Release native pointers
            if (_nativeTextBrush != 0) { Marshal.Release(_nativeTextBrush); _nativeTextBrush = 0; }
            if (_nativeBorderBrush != 0) { Marshal.Release(_nativeBorderBrush); _nativeBorderBrush = 0; }
            if (_nativeHighlightBrush != 0) { Marshal.Release(_nativeHighlightBrush); _nativeHighlightBrush = 0; }
            // Release the highlight-tier brushes too.
            if (_nativeSelectedCardHighlightBrush != 0) { Marshal.Release(_nativeSelectedCardHighlightBrush); _nativeSelectedCardHighlightBrush = 0; }
            if (_nativeInnerGlyphHighlightBrush != 0) { Marshal.Release(_nativeInnerGlyphHighlightBrush); _nativeInnerGlyphHighlightBrush = 0; }
            if (_nativeUnfocusedOverlayBrush != 0) { Marshal.Release(_nativeUnfocusedOverlayBrush); _nativeUnfocusedOverlayBrush = 0; }
            if (_nativeFocusGlyphOverlayBrush != 0) { Marshal.Release(_nativeFocusGlyphOverlayBrush); _nativeFocusGlyphOverlayBrush = 0; }
            if (_nativeMetaDimBrush != 0) { Marshal.Release(_nativeMetaDimBrush); _metaDimBrush = null; _nativeMetaDimBrush = 0; }
            // Release bumps too — that is what makes a recycled address distinguishable.
            if (_nativeFontFace != 0) { Marshal.Release(_nativeFontFace); _nativeFontFace = 0; BumpFontGeneration(); }
            
            // Release render target and factory
            // _d2dFactory is the PROCESS-WIDE SHARED factory (see
            // GetSharedD2DFactory). Dropping the reference here is right; releasing the COM object
            // would tear the factory out from under every other presenter.
            _renderTarget = null;
            _d2dFactory = null;
            _textBrush = null;
            _borderBrush = null;
            _highlightBrush = null;
            _fontFace = null;

            if (_classAtom != 0)
            {
                try 
                {
                    nint hInst = NativeMethods.GetModuleHandle(null);
                    NativeMethods.UnregisterClass(_className, hInst);
                    _classAtom = 0;
                }
                catch (Exception)
                {
                }
            }

            _disposed = true;
        }
    }
}

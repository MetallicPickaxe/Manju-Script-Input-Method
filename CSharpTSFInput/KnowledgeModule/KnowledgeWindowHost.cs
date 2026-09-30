using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CSharpTSFInput.ManjuShaper;

namespace CSharpTSFInput.UI
{
    /// <summary>
    /// Real HWND host for the floating Knowledge Window.
    ///
    /// Owns its own Win32 window class + HWND, separate from the main candidate window:
    /// a second window that floats beside the main input UI at a small distance.
    ///
    /// Scope: HWND lifecycle + paint. The multi-column layout (digit row, dual sub-columns,
    /// gloss bar, spillover highlight) is drawn in the Render method from DirectWrite-friendly
    /// primitives. Where ManjuDirectWriteRenderer helpers are sufficient (DrawShapedText,
    /// DrawTextWithSystemFont) the host uses them; where it needs more primitives (e.g. precise
    /// inter-column dividers, gloss bar composition) it uses D2D FillRectangle + DrawRectangle
    /// directly.
    ///
    /// Mouse:
    ///   - WM_MOUSEACTIVATE: MA_NOACTIVATE (do not steal focus)
    ///   - WM_NCHITTEST: HTCLIENT (no resize cursor)
    ///   - WM_LBUTTONDOWN: hit-test column groups, set highlight, set focus context, repaint
    ///
    /// AOT note: static WndProc + GCHandle (same pattern as CandidateListUIPresenter).
    /// </summary>
    public sealed unsafe class KnowledgeWindowHost : IDisposable
    {
        // ---------- Static infra ----------
        private static readonly nint _sharedWndProcPtr;
        private static int _classCounter;

        // Layout constants (matches KnowledgeWindow static math; duplicated here for ease)
        // The Latin is an annotation set around the Manchu, not a second script standing beside
        // it: a narrow sub-column in a smaller font, to the right of each Manchu glyph, not a
        // parallel half-width column.
        // ALL of these are DIP (device-independent px). The render target has
        // SetDpi(desktopDpi), so paint coordinates are DIP. The physical window size is DIP*scale
        // (see _dpiScale / RecomputeLayoutAndSize). A window sized with these magnitudes as physical px
        // while paint treats them as DIP would draw the gloss bar off the visible canvas at 200%.
        private const float Padding = 8f;
        private const float DigitRowHeight = 22f;
        private const float DigitRowGap = 4f;
        // Column group, left to right, mirroring the main-window preview's col3 (fused) + col4 (separated)
        // comparison:
        //   34 spine (1st col, native) + 8 + 30 col3 fused + 8 + 30 col4 separated + 8 + 30 Latin = 148. (tunable)
        // The explicit 8 DIP Manchu↔Latin gap keeps the Latin clearly separated from the Manchu.
        // The Latin annotation is 30 wide so MULTI-LETTER units (NG/DZ/TS'/Ū…) draw
        // horizontally in one row (the dictionary's Latin column must fit several letters across).
        // The width gives the joined column room, so entries don't overlap.
        private const float ColGroupWidth = 148f;
        private const float ManchuSubColWidth = 30f;
        private const float ManchuLatinGap = 8f;          // explicit gap so Latin isn't occluded
        private const float LatinAnnotationWidth = 30f;
        // The joined sub-column: the whole word as one connected piece, always shown.
        private const float CursiveColGap = 8f;
        private const float CursiveColWidth = 34f;
        private const float DividerThickness = 1f;
        private const float GlossBarHeight = 64f;
        private const float MinWindowWidth = 360f;
        private const float ManchuFontSize = 22f;
        private const float LatinFontSize = 11f;          // distinct color (_latinBrush)
        private const float DigitFontSize = 12f;
        private const float GlossFontSize = 12f;
        private const float MaxBodyGlyphRows = 12f;       // cap body height; longer words clip
        private const int VisibleSlots = KnowledgeWindow.MaxEntriesDisplayed;

        static KnowledgeWindowHost()
        {
            _sharedWndProcPtr = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&StaticWndProc;
        }

        // ---------- Instance ----------
        private readonly KnowledgeWindow _state;
        private readonly ManjuEngine _engine;
        private readonly string _className;
        private GCHandle _selfHandle;
        private nint _hwnd;
        private ushort _classAtom;
        private bool _disposed;
        private bool _visible;
        private int _windowX = 0;
        private int _windowY = 0;
        // Physical px window size (for CreateWindowEx / SetWindowPos / screen
        // clamp). Derived from DIP layout × _dpiScale.
        private int _windowW = 360;
        private int _windowH = 220;
        // DIP layout extent — what Render() draws against. = _windowW/_dpiScale.
        private float _layoutWDip = 360f;
        private float _layoutHDip = 220f;
        // DPI scale (dpi/96). Queried from desktop DPI after factory creation.
        private float _dpiScale = 1f;

        // Window-op marshalling. OnFrameReady runs on the WORKER thread, but
        // ALL Win32 window ops (SetWindowPos / ShowWindow / InvalidateRect) must run on the
        // window's OWNING (UI) thread — exactly like the main candidate window, which marshals via
        // PostMessage(WM_APP_UPDATE). Done cross-thread, the window appears but never paints: a white
        // window, or no dictionary at all. The worker stashes the desired geometry +
        // visibility and PostMessages WM_APP_KW_REFRESH; the WndProc applies them on the UI thread.
        private const uint WM_APP_KW_REFRESH = 0x8000 + 0x21; // WM_APP + 0x21
        private volatile int _pendingX;
        private volatile int _pendingY;
        private volatile bool _pendingShow;
        // Set by the first placement against the main window. Until then the window is where it was
        // created, (0,0), a place no host gave: it is neither moved there nor shown.
        private volatile bool _placed;

        // D2D + DWrite handles
        private NativeMethods.ID2D1Factory? _d2dFactory;
        private NativeMethods.ID2D1HwndRenderTarget? _renderTarget;
        private NativeMethods.IDWriteFontFace? _fontFace;
        private nint _nativeFontFace;
        private nint _nativeBgBrush;
        private nint _nativeBorderBrush;
        private nint _nativeTextBrush;
        private nint _nativeDigitBrush;
        private nint _nativeHighlightBrush;
        private nint _nativeLatinBrush;   // distinct color for Latin annotation
        private NativeMethods.ID2D1SolidColorBrush? _bgBrush;
        private NativeMethods.ID2D1SolidColorBrush? _borderBrush;
        private NativeMethods.ID2D1SolidColorBrush? _textBrush;
        private NativeMethods.ID2D1SolidColorBrush? _digitBrush;
        private NativeMethods.ID2D1SolidColorBrush? _highlightBrush;
        private NativeMethods.ID2D1SolidColorBrush? _latinBrush;
        private ushort _upem = 1000;
        private byte[]? _fontData;
        private readonly List<(int idx, float L, float T, float R, float B)> _columnHitRects = new();

        public KnowledgeWindowHost(ManjuEngine engine, KnowledgeWindow state)
        {
            _engine = engine;
            _state = state;
            int n = System.Threading.Interlocked.Increment(ref _classCounter);
            _className = $"ManjuKnowledgeWindow_{Environment.ProcessId}_{n}";
            _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
            try
            {
                RegisterClass();
                CreateHwnd();
            }
            catch (Exception)
            {
            }
        }


        // Clamp + STASH the desired position. Does NOT touch the window — the
        // actual SetWindowPos happens on the UI thread in the WM_APP_KW_REFRESH handler. (Called
        // from the worker thread via SetAdaptiveLocation.)
        public void SetLocation(int x, int y)
        {
            // Clamp against the work area of the monitor that contains the REQUESTED rect, so the
            // consumer preserves what the placement algorithm decided. ComputeAdaptiveSlot can return a
            // perfectly valid slot on a negative-origin monitor (e.g. (-1900,716)); a primary-screen clamp
            // with a hard x<0→0 would drag it back onto the primary, re-covering the main window the slot
            // scan had just avoided.
            RECT wa = GetWorkAreaForRect(x, y, _windowW, _windowH);
            if (wa.Right - wa.Left <= 0 || wa.Bottom - wa.Top <= 0)
            {
                int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
                wa = new RECT { Left = 0, Top = 0, Right = screenW > 0 ? screenW : 1920, Bottom = screenH > 0 ? screenH : 1080 };
            }
            (x, y) = ClampToWork(x, y, _windowW, _windowH, wa.Left, wa.Top, wa.Right, wa.Bottom);

            _windowX = x;
            _windowY = y;
            _pendingX = x;
            _pendingY = y;
            _placed = true;
        }

        /// <summary> The first-frame-DPI placement decision, as ONE function.
        ///
        /// The sequence {clamp into the work area → if a main-window rect is known, immediately rescan
        /// for a free slot → commit} lives in <see cref="ResolveFirstFrameDpiPlacement"/>, and
        /// EnsureRenderTarget's first-frame branch reaches it only through
        /// <see cref="CommitFirstFrameDpiPlacement"/>.</summary>
        internal readonly struct FirstFrameDpiPlacement
        {
            public readonly int X, Y;
            public readonly bool Rescanned;
            public readonly bool Free;
            public readonly long Overlap;
            public FirstFrameDpiPlacement(int x, int y, bool rescanned, bool free, long overlap)
            { X = x; Y = y; Rescanned = rescanned; Free = free; Overlap = overlap; }
        }

        /// <summary> What the first-frame DPI transaction actually did — the decided
        /// position, whether it came from a rescan, and whether the window was really moved there.</summary>
        internal readonly struct FirstFrameDpiCommit
        {
            public readonly int X, Y, W, H;
            public readonly bool Rescanned;
            public readonly bool Moved;
            public FirstFrameDpiCommit(int x, int y, int w, int h, bool rescanned, bool moved)
            { X = x; Y = y; W = w; H = h; Rescanned = rescanned; Moved = moved; }
        }

        /// <summary> The first-frame DPI placement TRANSACTION: decide, adopt the decision,
        /// commit it to window state, and move the window. Caller must hold <c>_placementGate</c>.
        ///
        /// The decision from <see cref="ResolveFirstFrameDpiPlacement"/> is worthless unless the caller
        /// acts on it: the call, its return value, the state commit and SetWindowPos all have to happen.
        /// Those four steps are inseparable, so they are one transaction in this one method.</summary>
        internal FirstFrameDpiCommit CommitFirstFrameDpiPlacement(RECT work)
        {
            var placed = ResolveFirstFrameDpiPlacement(
                _windowX, _windowY, _windowW, _windowH,
                work.Left, work.Top, work.Right, work.Bottom,
                _lastMainRectValid, _lastMainLeft, _lastMainTop, _lastMainWidth, _lastMainHeight);

            int nx = placed.X, ny = placed.Y;

            // Move FIRST, commit second. Writing the internal coordinates and then
            // calling SetWindowPos means a real Win32 failure leaves _windowX/_pendingX claiming a
            // position the HWND was never moved to, and every later decision builds on that.
            bool moved = MoveWindowTo(nx, ny, _windowW, _windowH);
            if (!moved)
            {
                _lastPickedX = int.MinValue;   // force the next scan to re-decide rather than trust a slot we never took
                return new FirstFrameDpiCommit(_windowX, _windowY, _windowW, _windowH, placed.Rescanned, false);
            }

            if (placed.Rescanned) { _lastPickedX = nx; _lastPickedY = ny; }
            _windowX = nx; _windowY = ny;
            _pendingX = nx; _pendingY = ny;
            return new FirstFrameDpiCommit(nx, ny, _windowW, _windowH, placed.Rescanned, true);
        }


        private bool MoveWindowTo(int x, int y, int w, int h)
        {
            return NativeMethods.SetWindowPos(_hwnd, 0, x, y, w, h, 0x0010 /*SWP_NOACTIVATE*/);
        }

        internal static FirstFrameDpiPlacement ResolveFirstFrameDpiPlacement(
            int windowX, int windowY, int windowW, int windowH,
            int workL, int workT, int workR, int workB,
            bool mainRectValid, int mainLeft, int mainTop, int mainWidth, int mainHeight)
        {
            int nx = windowX, ny = windowY;
            if (workR - workL > 0 && workB - workT > 0)
                (nx, ny) = ClampToWork(nx, ny, windowW, windowH, workL, workT, workR, workB);

            // Clamping alone only makes the window LEGAL, not unobstructed: the clamped rect can still
            // cover the main window while a free slot exists elsewhere. Waiting for the next frame to fix
            // that would leave the dictionary window sitting on top of the user's text until then.
            if (mainRectValid)
            {
                var slot = ComputeAdaptiveSlot(mainLeft, mainTop, mainWidth, mainHeight,
                                               windowW, windowH, workL, workT, workR, workB);
                return new FirstFrameDpiPlacement(slot.X, slot.Y, true, slot.Free, slot.Overlap);
            }
            return new FirstFrameDpiPlacement(nx, ny, false, false, -1);
        }

        /// <summary> Pure clamp of a window rect into a work rect: negative coordinates are
        /// legitimate positions on multi-monitor desks, NOT out-of-range values. Static and pure:
        /// the work rect comes in as arguments, and no monitor is queried.</summary>
        public static (int x, int y) ClampToWork(int x, int y, int w, int h, int workL, int workT, int workR, int workB)
        {
            if (x + w > workR) x = workR - w;
            if (x < workL) x = workL;
            if (y + h > workB) y = workB - h;
            if (y < workT) y = workT;
            return (x, y);
        }

        // Free-slot adaptive positioning finds a free position anywhere on the screen:
        //   - Scan the ENTIRE screen on a coarse grid
        //   - For each grid cell, KW must (a) fit on screen, (b) not overlap main IME window
        //     (with a small visual gap around main as exclusion zone)
        //   - Pick the cell CLOSEST to main IME window center (so user's eye doesn't jump
        //     across the whole screen)
        //   - Stability: if last-picked position is still valid, keep it (don't re-pick)
        //   - No fixed-direction fallback.
        private int _lastPickedX = int.MinValue;
        private int _lastPickedY = int.MinValue;

        // Cached GSUB ligature reverse-map {fused glyph -> components} from the bundled font.
        private Dictionary<uint, uint[]>? _ligReverseMap;
        private unsafe Dictionary<uint, uint[]> GetLigatureReverseMap()
        {
            if (_ligReverseMap != null) return _ligReverseMap;
            var built = new Dictionary<uint, uint[]>();
            try
            {
                if (_fontData != null && _fontData.Length > 0)
                    fixed (byte* pFont = _fontData)
                    {
                        var reader = new ManjuFontReader(pFont, _fontData.Length);
                        var gsub = new ManjuGsubEngine(reader);
                        built = gsub.BuildLigatureReverseMap();
                    }
            }
            catch (Exception) { }
            _ligReverseMap = built;
            return _ligReverseMap;
        }

        // Dict-window per-cell: decompose ANY GSUB ligature into
        // its real font components across consecutive clusters so the per-cell loop renders them as separate cells —
        // a uniform separated form for every joined type (b/p+vowel incl. BU/BA, velar+vowel, ng+consonant incl. ng+g, loan velar). The
        // components are the fused glyph's REAL forms (Gh+I etc., NOT FVS substitutes). An empty rime = 2 separate glyphs, never a GSUB
        // ligature → not in the map → untouched (stays fused). Same font reverse-map as the main-window preview.
        // The criterion is CANONICAL cluster-span >1, NOT ligMap membership alone, because the GSUB reverse-map OVER-REPORTS
        // single-letter medial forms (y/a etc.) as "ligatures", and they would be wrongly split apart. A glyph in the whole-word
        // FullShape that spans >1 codepoint (next glyph's cluster − this cluster > 1) is a REAL font merge → decompose;
        // span-1 glyphs stay. Mirrors the candidate window's canonFusions → main window and dictionary agree. An empty rime = 2 span-1
        // glyphs → never decomposed (stays fused). N = total code points (gives the last glyph's span).
        // PURE row-alignment for the dict col3 (fused) column. Given the UNDECOMPOSED whole-word
        // glyph clusters (ascending, from FullShape before the separated-form decompose) and the separated (col4) cells' vertical
        // spans (keyed by start cluster), return each fused glyph's vertical CENTER = midpoint of the cells whose
        // cluster falls in that glyph's [cluster, nextCluster) range. A multi-cluster fusion centers across its
        // component cells (centred across its separated rows); a plain letter aligns to its single cell → mirrors the candidate-window
        // preview's col3/col4 row correspondence. Static and pure; it needs no render target.
        // n = total code points (upper bound for the last glyph's cluster range).
        public static List<(int glyphIndex, float mid)> ComputeCol3RowCenters(List<int> rawClusters, int n, List<(int cl, float top, float bot)> cellRows)
        {
            var result = new List<(int, float)>();
            if (rawClusters == null || cellRows == null) return result;
            for (int gi = 0; gi < rawClusters.Count; gi++)
            {
                int gc = rawClusters[gi];
                int gcNext = (gi + 1 < rawClusters.Count) ? rawClusters[gi + 1] : n;
                float top = float.MaxValue, bot = float.MinValue;
                foreach (var cr in cellRows)
                    if (cr.cl >= gc && cr.cl < gcNext) { if (cr.top < top) top = cr.top; if (cr.bot > bot) bot = cr.bot; }
                if (bot < top) continue;   // no visible cell for this glyph (clipped past body)
                result.Add((gi, (top + bot) * 0.5f));
            }
            return result;
        }

        // Which code-point positions are covered by a REAL fusion glyph in the undecomposed
        // whole-word shaping. The criterion is cluster-span≥2 AND the glyph has ≥2 components in the ligature reverse-map,
        // the same criterion as DecomposeVelarLigaturesForPerCell / the col3 pass (fusion by render/cluster-span, never
        // by ligMap membership alone). Cells at these positions are the separated components of a fusion → they render in the
        // RIGHT comparison column; all other cells are plain letters → the LEFT main column (the main window's col3/col4 direction).
        // Static and pure; it needs no render target.
        public static HashSet<int> ComputeFusionCoveredClusters(List<ManjuShaperCore.FinalGlyph> rawGlyphs, int n, Dictionary<uint, uint[]>? ligMap)
        {
            var covered = new HashSet<int>();
            if (rawGlyphs == null || rawGlyphs.Count == 0 || ligMap == null) return covered;
            for (int gi = 0; gi < rawGlyphs.Count; gi++)
            {
                int cl = (int)rawGlyphs[gi].Cluster;
                int nextCl = (gi + 1 < rawGlyphs.Count) ? (int)rawGlyphs[gi + 1].Cluster : n;
                if (nextCl - cl < 2) continue;
                if (!ligMap.TryGetValue(rawGlyphs[gi].GlyphId, out var comps) || comps.Length < 2) continue;
                for (int q = cl; q < nextCl && q < n; q++) covered.Add(q);
            }
            return covered;
        }

        private List<ManjuShaperCore.FinalGlyph> DecomposeVelarLigaturesForPerCell(List<ManjuShaperCore.FinalGlyph> glyphs, string ms, int N)
        {
            if (glyphs == null || glyphs.Count == 0) return glyphs;
            var map = GetLigatureReverseMap();
            if (map == null || map.Count == 0) return glyphs;
            var outp = new List<ManjuShaperCore.FinalGlyph>(glyphs.Count + 4);
            for (int gi = 0; gi < glyphs.Count; gi++)
            {
                var g = glyphs[gi];
                int cl = (int)g.Cluster;
                int nextCl = (gi + 1 < glyphs.Count) ? (int)glyphs[gi + 1].Cluster : N;   // canonical span = nextCluster − thisCluster
                int span = nextCl - cl;
                if (span >= 2 && map.TryGetValue(g.GlyphId, out var comps) && comps.Length >= 2)
                {
                    for (int k = 0; k < comps.Length; k++)
                        outp.Add(new ManjuShaperCore.FinalGlyph { GlyphId = comps[k], Cluster = (uint)(cl + k) });
                }
                else outp.Add(g);
            }
            return outp;
        }

        // Placement generation. WM_DPICHANGED bumps it after applying the system's
        // suggested position; a SetAdaptiveLocation computed from a PRE-move main-window rect (worker
        // read GetWindowRect before the host actually moved) detects the bump and
        // aborts instead of overwriting the new-monitor position with old-screen coordinates.
        private int _kwPlacementGen;

        // Last main-window rect a placement was computed against. The first-frame DPI path
        // needs it to re-run the adaptive scan immediately instead of deferring the rescan to a later
        // frame, which would leave the window clamped but overlapping the main window while a free
        // slot exists.
        private int _lastMainLeft, _lastMainTop, _lastMainWidth, _lastMainHeight;
        private bool _lastMainRectValid;

        // The WHOLE placement transaction runs under _placementGate — width budget,
        // sizing, slot scan, keep-last fast path and the coordinate commit alike. Wrapping only
        // the slow path's {check + commit} is not enough: the keep-last branch then writes
        // SetLocation(old) OUTSIDE the lock, so this ordering still holds: worker passes keep-last →
        // WM_DPICHANGED bumps and commits the new monitor's position → worker writes the old pending
        // coordinates → the UI refresh drags the window back to the old screen. DPI scale, budget, size
        // and position are one atomic state, so they are committed as one.
        public void SetAdaptiveLocation(int mainLeft, int mainTop, int mainWidth, int mainHeight)
        {
            lock (_placementGate) { SetAdaptiveLocationLocked(mainLeft, mainTop, mainWidth, mainHeight); }
        }

        private void SetAdaptiveLocationLocked(int mainLeft, int mainTop, int mainWidth, int mainHeight)
        {
            int placementGenAtStart = _kwPlacementGen;
            // Remember the rect we are placing against, so the first-frame DPI path can re-scan
            // immediately rather than waiting for the next frame to supply one.
            _lastMainLeft = mainLeft; _lastMainTop = mainTop;
            _lastMainWidth = mainWidth; _lastMainHeight = mainHeight;
            _lastMainRectValid = true;
            // Use the WORK AREA of the monitor the main IME window is actually on. SM_CXSCREEN /
            // SM_CYSCREEN give the PRIMARY monitor's full bounds anchored at (0,0), which is wrong twice
            // over on a real desk: a secondary monitor left of / above the primary has NEGATIVE
            // coordinates (an x>=0 && y>=0 check would reject them outright, so the window could never be
            // placed there), and the full bounds include the taskbar, so a "fitting" slot could land
            // under it.
            int workL, workT, workR, workB;
            {
                RECT wa = GetWorkAreaForRect(mainLeft, mainTop, mainWidth, mainHeight);
                workL = wa.Left; workT = wa.Top; workR = wa.Right; workB = wa.Bottom;
            }
            if (workR - workL <= 0 || workB - workT <= 0)
            {
                workL = 0; workT = 0;
                workR = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
                workB = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
                if (workR <= 0) workR = 1920;
                if (workB <= 0) workB = 1080;
            }

            // The COLUMN cap must not use the PRIMARY screen's
            // SM_CXSCREEN: a window sized for a 3840px primary (10 cols ≈ 1506px) can never fit a 1280px
            // secondary, so staying on the same secondary monitor would be impossible by construction. Feed the sizing pass the
            // work-area width of the monitor the main window is ON, computed above, BEFORE sizing.
            _screenWidthBudgetPx = workR - workL;

            // Size the window to current content (DIP→px) BEFORE choosing a slot,
            // because slot-fit depends on _windowW/_windowH.
            RecomputeLayoutAndSize();
            const int gap = 16;
            const int gridStep = 60; // grid resolution in px; small enough for variety, big enough to be cheap

            // Exclusion zone = main IME window expanded by `gap`. KW must not intersect this.
            int exclLeft = mainLeft - gap;
            int exclTop = mainTop - gap;
            int exclRight = mainLeft + mainWidth + gap;
            int exclBottom = mainTop + mainHeight + gap;

            bool FitsScreen(int x, int y) =>
                x >= workL && y >= workT && x + _windowW <= workR && y + _windowH <= workB;

            bool OverlapsMain(int x, int y) =>
                !(x + _windowW <= exclLeft || x >= exclRight ||
                  y + _windowH <= exclTop || y >= exclBottom);

            bool IsValid(int x, int y) => FitsScreen(x, y) && !OverlapsMain(x, y);

            // 1. Stability: if last-picked still valid, keep it. The generation is checked on
            //    this path too — a DPI/monitor change that landed while this call was computing must
            //    invalidate the keep-last decision exactly as it invalidates a fresh scan.
            if (_lastPickedX != int.MinValue && IsValid(_lastPickedX, _lastPickedY))
            {
                if (placementGenAtStart != _kwPlacementGen)
                {
                    return;
                }
                SetLocation(_lastPickedX, _lastPickedY);
                return;
            }

            // 2. Candidate set = coarse grid PLUS explicit edge-aligned positions.
            //    The grid ALONE is unsound: when the KW is wide, the only non-overlapping band can be
            //    narrower than gridStep and get skipped entirely. Example:
            //    main=(668,630,1208x400), KW=1522x756, screen=2736x1824: left/right are both too narrow
            //    (right leaves 860px < 1522), so the ONLY legal band is y in [1046,1068] (22px), while
            //    the grid steps ...,1020,1080 and misses it.
            //
            //    So (a) the edge/main-adjacent lines join the candidate set, and those narrow
            //    bands are always probed; (b) the minimum-OVERLAP slot is tracked as well, so when nothing is
            //    truly free the window degrades to "covers the least" instead of blindly covering the main.
            // The pure geometry lives in the static ComputeAdaptiveSlot. With no free slot it picks the
            // least-overlapping one; a window at (0,0) would cover the main window.
            var slot = ComputeAdaptiveSlot(mainLeft, mainTop, mainWidth, mainHeight, _windowW, _windowH, workL, workT, workR, workB, gap, gridStep);
            // A DPI move happened while this placement was computing from the OLD
            // main-window rect — writing it now would drag the window back to the old screen. Drop it;
            // the next frame re-places from a fresh rect.
            // The generation check and the write must not be two steps, or
            // a WM_DPICHANGED can land in between and be overwritten by this stale placement.
            // Check + commit are one atomic block under _placementGate; WM_DPICHANGED takes the
            // same lock around {bump generation + apply suggested rect}.
            // Already inside _placementGate (see SetAdaptiveLocation): the whole transaction
            // is one critical section, so this is a plain check + commit.
            if (placementGenAtStart != _kwPlacementGen)
            {
                return;
            }
            _lastPickedX = slot.X;
            _lastPickedY = slot.Y;
            SetLocation(slot.X, slot.Y);
        }

        // Serialises {generation check + placement commit} against
        // {generation bump + suggested-rect apply} in WM_DPICHANGED.
        private readonly object _placementGate = new();

        // The dictionary-window placement geometry, PURE (no HWND / screen-metrics /
        // instance state). Returns the chosen slot + whether it is fully free +
        // how much it overlaps the main window's exclusion zone. When no zero-overlap slot exists, returns
        // the MINIMUM-overlap slot, NOT (0,0), which can drop the dictionary window straight onto the
        // main window.
        /// <summary> Work area of the monitor the main IME window sits on
        /// (taskbar excluded, MONITOR_DEFAULTTONEAREST so an off-screen rect still resolves). Returns an
        /// empty RECT on failure so the caller can fall back to primary-screen metrics. Mirrors the
        /// TextService.GetWorkAreaForPoint pattern.</summary>
        private static RECT GetWorkAreaForRect(int left, int top, int width, int height)
        {
            try
            {
                var centre = new POINT { X = left + width / 2, Y = top + height / 2 };
                nint mon = NativeMethods.MonitorFromPoint(centre, NativeMethods.MONITOR_DEFAULTTONEAREST);
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
            return default;
        }

        public readonly struct AdaptiveSlot
        {
            public readonly int X, Y;
            public readonly long Overlap;   // px^2 with main exclusion zone (0 = fully free)
            public readonly bool Free;
            public AdaptiveSlot(int x, int y, long overlap, bool free) { X = x; Y = y; Overlap = overlap; Free = free; }
        }

        // Coordinates are WORK-AREA absolute, not primary-screen relative:
        // workLeft/workTop may be negative on a multi-monitor desk (secondary display left of / above the
        // primary), and the work rect already excludes the taskbar. A signature taking only
        // (screenW, screenH) with a hard x >= 0 && y >= 0 check would silently make every negative-origin
        // monitor unusable and allow slots underneath the taskbar.
        public static AdaptiveSlot ComputeAdaptiveSlot(int mainLeft, int mainTop, int mainWidth, int mainHeight,
                                                       int kwW, int kwH,
                                                       int workLeft, int workTop, int workRight, int workBottom,
                                                       int gap = 16, int gridStep = 60)
        {
            int exclLeft = mainLeft - gap, exclTop = mainTop - gap;
            int exclRight = mainLeft + mainWidth + gap, exclBottom = mainTop + mainHeight + gap;
            bool FitsScreen(int x, int y) => x >= workLeft && y >= workTop && x + kwW <= workRight && y + kwH <= workBottom;
            bool OverlapsMain(int x, int y) => !(x + kwW <= exclLeft || x >= exclRight || y + kwH <= exclTop || y >= exclBottom);
            long OverlapArea(int x, int y)
            {
                long ox = Math.Max(0, Math.Min(x + kwW, exclRight) - Math.Max(x, exclLeft));
                long oy = Math.Max(0, Math.Min(y + kwH, exclBottom) - Math.Max(y, exclTop));
                return ox * oy;
            }
            int mainCenterX = mainLeft + mainWidth / 2, mainCenterY = mainTop + mainHeight / 2;
            int bestX = int.MinValue, bestY = int.MinValue;
            double bestSqDistance = double.MaxValue;
            // Fallback seed is the work-area origin, NOT (0,0): on a negative-origin
            // monitor (0,0) is not even on this display. When the KW is LARGER than the work area the
            // candidate lists come out empty, and returning a hard-coded (0,0) while claiming "minimum
            // overlap" is wrong twice; seeding here means the degenerate case still lands somewhere
            // real and still reports its true overlap.
            int fbX = workLeft, fbY = workTop;
            long fbOverlap = OverlapArea(workLeft, workTop);
            double fbSqDistance = double.MaxValue;

            var xs = new List<int>(); var ys = new List<int>();
            void AddX(int v) { if (v >= workLeft && v + kwW <= workRight && !xs.Contains(v)) xs.Add(v); }
            void AddY(int v) { if (v >= workTop && v + kwH <= workBottom && !ys.Contains(v)) ys.Add(v); }
            for (int x = workLeft; x + kwW <= workRight; x += gridStep) xs.Add(x);
            for (int y = workTop; y + kwH <= workBottom; y += gridStep) ys.Add(y);
            AddX(workLeft); AddX(workRight - kwW); AddX(exclRight); AddX(exclLeft - kwW);
            AddY(workTop); AddY(workBottom - kwH); AddY(exclBottom); AddY(exclTop - kwH);

            foreach (int y in ys)
                foreach (int x in xs)
                {
                    if (!FitsScreen(x, y)) continue;
                    long dx = (x + kwW / 2) - mainCenterX, dy = (y + kwH / 2) - mainCenterY;
                    double sq = (double)(dx * dx + dy * dy);
                    if (!OverlapsMain(x, y))
                    {
                        if (sq < bestSqDistance) { bestSqDistance = sq; bestX = x; bestY = y; }
                        continue;
                    }
                    long ov = OverlapArea(x, y);
                    if (ov < fbOverlap || (ov == fbOverlap && sq < fbSqDistance)) { fbOverlap = ov; fbSqDistance = sq; fbX = x; fbY = y; }
                }

            if (bestX != int.MinValue) return new AdaptiveSlot(bestX, bestY, 0, true);
            return new AdaptiveSlot(fbX, fbY, fbOverlap, false);
        }

        // Stash desired visibility + marshal to the UI thread. The actual
        // ShowWindow / SetWindowPos / InvalidateRect run in WM_APP_KW_REFRESH on the owning thread.
        /// <summary> The USER's show/hide choice, as a separate axis from "is there
        /// anything to show".
        ///
        /// HideForCancel CLEARS the lookup state, so a user hide that called it on every frame would
        /// wipe the dictionary result continuously while hidden, and the next show would reveal a
        /// window with nothing in it until another lookup happened to run. Hiding a window must not
        /// throw away what it was showing.</summary>
        public bool UserHidden { get; set; }

        public void SyncVisibility()
        {
            _pendingShow = _state.Entries.Count > 0 && !UserHidden;
            if (_hwnd != 0)
            {
                NativeMethods.PostMessage(_hwnd, WM_APP_KW_REFRESH, 0, 0);
            }
        }

        // UI-thread application of the stashed geometry + visibility. Runs in the
        // window's own WndProc, so all Win32 window ops are on the owning thread → reliable paint.
        private void ApplyRefreshOnUiThread()
        {
            if (_hwnd == 0) return;
            // The UI consumer is part of the placement protocol too. Reading
            // _pendingX/_pendingY/_windowW/_windowH without _placementGate could pick up a half-applied
            // transaction (new position with old size, or vice versa), which is an unserialised
            // writer/reader pair. Take the geometry as ONE consistent tuple under the
            // lock, then do the Win32 call outside it (never hold a lock across a system call that
            // can re-enter this window's WndProc).
            int px, py, pw, ph;
            lock (_placementGate) { px = _pendingX; py = _pendingY; pw = _windowW; ph = _windowH; }
            bool placed = _placed;
            // Always position first (size may have changed via RecomputeLayoutAndSize).
            bool posOk = placed && NativeMethods.SetWindowPos(_hwnd, 0, px, py, pw, ph,
                0x0010u | 0x0004u /* SWP_NOACTIVATE | SWP_NOZORDER */);
            // Both arms read the OS (IsWindowVisible). `_visible` is a cached record of the last
            // ShowWindow this code called, and it goes wrong the moment something outside this process
            // changes real visibility: a hide arm gated on it would stop firing, and the window would
            // stay on screen with stale entries.
            //
            // This method does not modify `_pendingShow`. It is computed in SyncVisibility
            // as `_state.Entries.Count > 0 && !UserHidden`, which separates "the
            // user chose to hide it" from "there is something to show". Hiding must never clear entries,
            // and nothing here does.
            bool osVisible = NativeMethods.IsWindowVisible(_hwnd);
            // No composition, no dictionary window: whatever the entries and the user's
            // choice say, the window is not shown without one, and a window on screen is hidden.
            bool show = _pendingShow && _engine.GetRawBuffer().Length > 0;
            if (!(show && !placed))
            { if (show && !osVisible)
            {
                NativeMethods.ShowWindow(_hwnd, 8 /* SW_SHOWNA */);
                _visible = true;
                NativeMethods.GetWindowRect(_hwnd, out RECT wr);
            }
            else if (!show && (osVisible || _visible))
            {
                NativeMethods.ShowWindow(_hwnd, 0 /* SW_HIDE */);
                _visible = false;
            } }
            if (_visible)
            {
                NativeMethods.InvalidateRect(_hwnd, 0, true);
            }
        }

        public void HideForCancel()
        {
            _state.Clear();
            SyncVisibility();
        }

        private void RegisterClass()
        {
            nint hInst = NativeMethods.GetModuleHandle(null);
            fixed (char* pClassName = _className)
            {
                WNDCLASSEX wc = new()
                {
                    Size_CbSize = (UInt32)Marshal.SizeOf<WNDCLASSEX>(),
                    Style_Style = 0x0003u | 0x00020000u /* CS_HREDRAW|CS_VREDRAW|CS_DROPSHADOW */,
                    Ptr_LpfnWndProc = _sharedWndProcPtr,
                    Handle_HInstance = hInst,
                    Name_LpszClassName = (nint)pClassName,
                    Handle_HbrBackground = 0
                };
                _classAtom = NativeMethods.RegisterClassEx(&wc);
                if (_classAtom == 0)
                {
                    int err = Marshal.GetLastPInvokeError();
                }
            }
            fixed (Guid* iid = &IID_ID2D1Factory)
            {
                NativeMethods.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, iid, 0, out _d2dFactory);
            }
            // Query desktop DPI now so window sizing (DIP→px) is correct from
            // the first CreateHwnd. 200% display → scale 2.0; window px = DIP layout × 2.
            if (_d2dFactory != null)
            {
                try
                {
                    _d2dFactory.GetDesktopDpi(out float dx, out float dy);
                    if (dx > 0f && !float.IsNaN(dx) && !float.IsInfinity(dx)) _dpiScale = dx / 96f;
                }
                catch { _dpiScale = 1f; }
            }
            RecomputeLayoutAndSize();
        }

        private static readonly Guid IID_ID2D1Factory = new Guid("06152247-6F50-465A-9245-118BFD3B6007");

        // Compute DIP layout extent from current entry content, then derive the
        // physical-px window size = DIP × _dpiScale. Render() draws against _layoutWDip/_layoutHDip;
        // CreateWindowEx/SetWindowPos/screen-clamp use _windowW/_windowH (physical px).
        // Columns to actually lay out / render — capped so the window never grows wider
        // than the screen can place it. Uncapped, with many
        // dict matches the window can grow wider than the screen (10 cols × 148 DIP at DPI 2.0 is about
        // 3012px) → the free-slot placement scan (SetAdaptiveLocation) can NEVER fit it, and the window
        // ends up overlapping the main window. Cap to ~62% of the caret monitor's
        // WORK-AREA width so there's always room to place the KW beside the main window. Both the sizing pass
        // and the paint pass call this so they never disagree (over-wide paint would clip).
        // Width budget = work-area width of the monitor the MAIN window is on,
        // stashed by SetAdaptiveLocation before sizing. 0 (never placed yet) falls back to the primary.
        private int _screenWidthBudgetPx;

        private int EffectiveColumnCount()
            => ScreenCappedColumns(Math.Min(_state.Entries.Count, VisibleSlots), _dpiScale,
                                   _screenWidthBudgetPx > 0 ? _screenWidthBudgetPx
                                                            : NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN));

        // PURE column-cap math. Given the desired columns, DPI scale and physical screen
        // width, return the columns that keep the window ≤ ~62% of the screen width (so it can always be placed
        // beside the main window).
        public static int ScreenCappedColumns(int desiredCols, float dpiScale, int screenWpx)
        {
            if (desiredCols <= 0) return 1;
            float scale = dpiScale > 0.1f ? dpiScale : 1f;
            if (screenWpx <= 0) screenWpx = 1920;
            float screenWdip = screenWpx / scale;
            float maxWDip = screenWdip * 0.62f;
            int maxCols = Math.Max(1, (int)((maxWDip - Padding * 2) / (ColGroupWidth + DividerThickness)));
            return Math.Min(desiredCols, maxCols);
        }

        private void RecomputeLayoutAndSize()
        {
            int n = EffectiveColumnCount();
            if (n <= 0) n = 1;
            float wDip = Padding * 2 + n * (ColGroupWidth + DividerThickness);
            if (wDip < MinWindowWidth) wDip = MinWindowWidth;

            int maxGlyphs = 1;
            foreach (var e in _state.Entries)
            {
                int gc = string.IsNullOrEmpty(e.ManchuScript) ? 0 : e.ManchuScript.Length;
                if (gc > maxGlyphs) maxGlyphs = gc;
            }
            if (maxGlyphs > MaxBodyGlyphRows) maxGlyphs = (int)MaxBodyGlyphRows;
            float bodyDip = maxGlyphs * ManchuFontSize + 8f;
            float hDip = Padding + DigitRowHeight + DigitRowGap + bodyDip + GlossBarHeight + Padding;

            _layoutWDip = wDip;
            _layoutHDip = hDip;
            _windowW = (int)Math.Ceiling(wDip * _dpiScale);
            _windowH = (int)Math.Ceiling(hDip * _dpiScale);
        }

        private void CreateHwnd()
        {
            nint hInst = NativeMethods.GetModuleHandle(null);
            const UInt32 WS_POPUP = 0x80000000;
            const UInt32 WS_EX_TOOLWINDOW = 0x00000080;
            const UInt32 WS_EX_NOACTIVATE = 0x08000000;
            const UInt32 WS_EX_TOPMOST = 0x00000008;
            _hwnd = NativeMethods.CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                _className, "",
                WS_POPUP,
                _windowX, _windowY, _windowW, _windowH,
                0, 0, hInst, GCHandle.ToIntPtr(_selfHandle));
            if (_hwnd == 0)
            {
                int err = Marshal.GetLastPInvokeError();
            }
            else
            {
                NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        private static nint StaticWndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
        {
            try
            {
                nint userdata = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWLP_USERDATA);
                if (userdata != 0)
                {
                    var handle = GCHandle.FromIntPtr(userdata);
                    if (handle.IsAllocated && handle.Target is KnowledgeWindowHost host)
                    {
                        return host.InstanceWndProc(hWnd, msg, wParam, lParam);
                    }
                }
            }
            catch (Exception)
            {
            }
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private nint InstanceWndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
        {
            // Worker → UI-thread refresh (position + show/hide + repaint).
            if (msg == WM_APP_KW_REFRESH)
            {
                try { ApplyRefreshOnUiThread(); }
                catch (Exception) { }
                return 0;
            }
            switch (msg)
            {
                case 0x0021: // WM_MOUSEACTIVATE → MA_NOACTIVATE
                    // Because this window never activates, a click here produces
                    // NO OnSetFocus(0) in TextService, and Shift-hold + click-here + release then
                    // fires a false lone-tap escape (k → k'). Tell the engine a pointer interruption
                    // happened so the Shift tracker treats the following release as NOT a tap.
                    try { _engine.RaisePointerInterruption(); } catch { }
                    return 3;
                case 0x0084: return 1; // WM_NCHITTEST → HTCLIENT
                case 0x0201: // WM_LBUTTONDOWN
                {
                    // lParam is PHYSICAL px; _columnHitRects are in DIP
                    // (paint space, render target has SetDpi). Convert before hit-test. The main
                    // candidate window does the same conversion.
                    int xPhys = (short)((long)lParam & 0xFFFF);
                    int yPhys = (short)(((long)lParam >> 16) & 0xFFFF);
                    float dpiX = 96f, dpiY = 96f;
                    try { _renderTarget?.GetDpi(out dpiX, out dpiY); } catch { dpiX = 96f; dpiY = 96f; }
                    if (dpiX <= 0f) dpiX = 96f;
                    if (dpiY <= 0f) dpiY = 96f;
                    int x = (int)(xPhys * 96f / dpiX);
                    int y = (int)(yPhys * 96f / dpiY);
                    int hitIdx = HitTestColumn(x, y);
                    if (hitIdx >= 0)
                    {
                        // Click = SELECT ONLY (move highlight + show that entry's
                        // gloss), and nothing else.
                        // Do NOT move the focus context here — that would dim the main window and reroute
                        // number keys to word-commit, which is a Tab-only mode switch, not a click
                        // side effect. Pure preview.
                        _state.SetHighlight(hitIdx);
                        NativeMethods.InvalidateRect(_hwnd, 0, true);
                    }
                    return 0;
                }
                case 0x000F: // WM_PAINT
                {
                    NativeMethods.BeginPaint(hWnd, out PAINTSTRUCT ps);
                    try
                    {
                        EnsureRenderTarget();
                        Render();
                    }
                    finally
                    {
                        NativeMethods.EndPaint(hWnd, ref ps);
                    }
                    return 0;
                }
                case 0x0005: // WM_SIZE — resize the D2D render target to match new client size
                {
                    if (_renderTarget != null)
                    {
                        if (NativeMethods.GetClientRect(_hwnd, out RECT rc))
                        {
                            uint w = (uint)Math.Max(1, rc.Right - rc.Left);
                            uint h = (uint)Math.Max(1, rc.Bottom - rc.Top);
                            try
                            {
                                _renderTarget.Resize(new D2D1_SIZE_U { width = w, height = h });
                            }
                            catch (Exception)
                            {
                                ReleaseGraphics();
                            }
                        }
                    }
                    return 0;
                }
                case 0x02E0: // WM_DPICHANGED — Win10+ per-monitor DPI change
                {
                    // wParam HIWORD/LOWORD both contain new DPI; use LOWORD (X) for symmetric.
                    float newDpi = (float)((nuint)wParam & 0xFFFF);
                    if (newDpi <= 0f) newDpi = 96f;
                    // DPI scale, width budget, window size, clamp and position are
                    // ONE atomic state transition. Leaving most of them outside _placementGate lets an
                    // in-flight SetAdaptiveLocation interleave with a half-applied DPI change.
                    // The entire apply runs inside _placementGate, and the generation is bumped
                    // FIRST so any placement already in flight fails its check and discards itself.
                    try
                    {
                      lock (_placementGate)
                      {
                        _kwPlacementGen++;
                        // Updating only the render target's DPI is not enough:
                        // _dpiScale (which converts the DIP layout to the PHYSICAL
                        // window size) would keep the OLD monitor's scale, so the window arrives on the new
                        // monitor wrongly sized. Adopt the scale, re-derive px extents, and take the
                        // system-suggested rect (it is already in the new monitor's coordinate space).
                        _dpiScale = newDpi / 96f;
                        // RecomputeLayoutAndSize otherwise consumes the
                        // width budget of the OLD monitor, so the column count (and therefore the
                        // window width) is still sized for the screen we just left. Refresh the budget
                        // from the monitor the SUGGESTED rect lands on BEFORE sizing.
                        unsafe
                        {
                            RECT* pre = (RECT*)lParam;
                            if (pre != null)
                            {
                                RECT waNew = GetWorkAreaForRect(pre->Left, pre->Top, Math.Max(1, pre->Right - pre->Left), Math.Max(1, pre->Bottom - pre->Top));
                                if (waNew.Right - waNew.Left > 0) _screenWidthBudgetPx = waNew.Right - waNew.Left;
                            }
                        }
                        RecomputeLayoutAndSize();
                        _renderTarget?.SetDpi(newDpi, newDpi);
                        unsafe
                        {
                            RECT* suggested = (RECT*)lParam;
                            if (suggested != null)
                            {
                                // The suggested rect's SIZE reflects the old content
                                // scaled — our size is content-derived (just recomputed above), so take
                                // the suggested POSITION but our size, then CLAMP into the new monitor's
                                // work area: switching from a high-DPI 2-col layout to a low-DPI 5-col
                                // one can widen past the edge or back over the main window.
                                // The generation bump makes any in-flight adaptive placement (still
                                // computed from the old screen) discard itself instead of overwriting us.
                                RECT wa = GetWorkAreaForRect(suggested->Left, suggested->Top, _windowW, _windowH);
                                int nx = suggested->Left, ny = suggested->Top;
                                if (wa.Right - wa.Left > 0 && wa.Bottom - wa.Top > 0)
                                    (nx, ny) = ClampToWork(nx, ny, _windowW, _windowH, wa.Left, wa.Top, wa.Right, wa.Bottom);
                                // already inside _placementGate (generation bumped at the top
                                // of the handler), so this is a plain commit of the atomic state.
                                _windowX = nx; _windowY = ny;
                                _pendingX = nx; _pendingY = ny;
                                _lastPickedX = int.MinValue;   // force a fresh adaptive scan next frame
                                NativeMethods.SetWindowPos(_hwnd, 0, nx, ny,
                                    _windowW, _windowH, 0x0010 /*SWP_NOACTIVATE*/);
                            }
                        }
                        NativeMethods.InvalidateRect(_hwnd, 0, true);
                      }
                    }
                    catch (Exception) { }
                    return 0;
                }
                case 0x0002: // WM_DESTROY
                    ReleaseGraphics();
                    return 0;
            }
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private int HitTestColumn(int x, int y)
        {
            for (int i = 0; i < _columnHitRects.Count; i++)
            {
                var r = _columnHitRects[i];
                if (x >= r.L && x <= r.R && y >= r.T && y <= r.B) return r.idx;
            }
            return -1;
        }

        private void EnsureRenderTarget()
        {
            if (_d2dFactory == null) return;
            if (_renderTarget == null)
            {
                if (!NativeMethods.GetClientRect(_hwnd, out RECT rc)) return;
                var rtProps = new D2D1_RENDER_TARGET_PROPERTIES();
                var hwndProps = new D2D1_HWND_RENDER_TARGET_PROPERTIES
                {
                    hwnd = _hwnd,
                    pixelSize = new D2D1_SIZE_U { width = (uint)(rc.Right - rc.Left), height = (uint)(rc.Bottom - rc.Top) },
                    presentOptions = 0
                };
                _d2dFactory.CreateHwndRenderTarget(rtProps, hwndProps, out _renderTarget);
                if (_renderTarget != null)
                {
                    // Sync DPI to the WINDOW's monitor, not the
                    // desktop/primary. GetDesktopDpi returns the system DPI, so the FIRST render target
                    // created while the window sits on a secondary monitor comes up at the wrong scale
                    // (outer px frame vs D2D DIP mismatch; Microsoft deprecates GetDesktopDpi
                    // for desktop apps). Also adopt _dpiScale here so sizing and paint agree from the
                    // first frame, not only after a WM_DPICHANGED.
                    try
                    {
                        float dpiX = NativeMethods.GetDpiForWindow(_hwnd);
                        if (dpiX <= 0f || float.IsNaN(dpiX) || float.IsInfinity(dpiX))
                        {
                            _d2dFactory.GetDesktopDpi(out dpiX, out float _);
                        }
                        if (dpiX <= 0f || float.IsNaN(dpiX) || float.IsInfinity(dpiX)) dpiX = 96f;
                        _renderTarget.SetDpi(dpiX, dpiX);

                        // Adopting the scale is not enough on its own: the
                        // HWND was created (and the RT sized) from the OLD scale, so the outer pixel
                        // frame and the D2D DIP extent stay inconsistent on the first frame. Re-derive
                        // the physical size from the DIP layout and resize the window AND the render
                        // target whenever the window's real DPI differs from what we had assumed.
                        // This is a PLACEMENT WRITER: it changes DPI scale, window
                        // size AND window position. Run outside _placementGate with no
                        // generation bump, no work-area clamp and no rescan, a slot that was legal at
                        // 100% (0,316 / 300x200) becomes 600x400 at the same origin and runs past the work
                        // area (bottom 716 > 700). It joins the same placement transaction as
                        // SetAdaptiveLocation and WM_DPICHANGED: bump the generation (so any in-flight
                        // placement discards itself), resize, CLAMP into the window's work area, commit,
                        // and force a fresh adaptive scan.
                        float newScale = dpiX / 96f;
                        if (Math.Abs(newScale - _dpiScale) > 0.001f)
                        {
                            lock (_placementGate)
                            {
                                _kwPlacementGen++;
                                _dpiScale = newScale;
                                RECT waFirst = GetWorkAreaForRect(_windowX, _windowY, Math.Max(1, _windowW), Math.Max(1, _windowH));
                                if (waFirst.Right - waFirst.Left > 0) _screenWidthBudgetPx = waFirst.Right - waFirst.Left;
                                RecomputeLayoutAndSize();
                                _lastPickedX = int.MinValue;   // the old slot was sized for the old scale
                                // The WHOLE transaction (decide, adopt, commit state, move the
                                // window) is one method. The decision is worthless if a caller ignores
                                // it by dropping the call, its return value, the state commit or
                                // SetWindowPos, so all of it lives behind this one entry.
                                var committed = CommitFirstFrameDpiPlacement(waFirst);
                                int nx = committed.X, ny = committed.Y;
                                if (NativeMethods.GetClientRect(_hwnd, out RECT rc2))
                                {
                                    try { _renderTarget.Resize(new D2D1_SIZE_U { width = (uint)(rc2.Right - rc2.Left), height = (uint)(rc2.Bottom - rc2.Top) }); }
                                    catch (Exception) { }
                                }
                            }
                        }
                        else _dpiScale = newScale;
                    }
                    catch { /* leave at default 96 DPI */ }
                    // The brush palette matches CandidateListUIPresenter so
                    // KW reads as part of the same IME visual system. Matched values:
                    //   text   = (0, 0, 0, 1)              ← same as main _textBrush
                    //   border = (0.5, 0.5, 0.5, 1)        ← same as main _borderBrush
                    //   highlight blue = (0.30, 0.55, 0.95, 0.55)  ← main band brush
                    //   bg     = (0.98, 0.98, 1.0, 1.0)    ← opaque off-white (alpha 1:
                    //                                         HwndRenderTarget alpha-IGNORE mode
                    //                                         expects opaque draws)
                    //   digit  = same as text (kept as a separate handle)
                    _bgBrush = CreateBrush(_renderTarget, 0.98f, 0.98f, 1.00f, 1.00f);
                    _borderBrush = CreateBrush(_renderTarget, 0.50f, 0.50f, 0.50f, 1.00f);
                    _textBrush = CreateBrush(_renderTarget, 0.00f, 0.00f, 0.00f, 1.00f);
                    _digitBrush = CreateBrush(_renderTarget, 0.00f, 0.00f, 0.00f, 1.00f);
                    _highlightBrush = CreateBrush(_renderTarget, 0.30f, 0.55f, 0.95f, 0.55f);
                    // The values above are only the cold-start defaults. If the main
                    // window already switched theme while this window was torn down, re-apply the stashed
                    // palette now so the two surfaces never come back up in different themes.
                    _themePending = _hasTheme;
                    // Latin annotation in a distinct muted blue so it reads as
                    // a subordinate gloss decorating the Manchu glyph, not a parallel black letter.
                    _latinBrush = CreateBrush(_renderTarget, 0.20f, 0.40f, 0.70f, 1.00f);
                    _nativeBgBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_bgBrush!);
                    _nativeBorderBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_borderBrush!);
                    _nativeTextBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_textBrush!);
                    _nativeDigitBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_digitBrush!);
                    _nativeHighlightBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_highlightBrush!);
                    _nativeLatinBrush = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_latinBrush!);
                    // Brushes now exist — replay any theme that arrived while they did not.
                    RecolorNow();
                }
                string? fontPath = _engine.GetFontFilePath();
                if (fontPath != null)
                {
                    _fontFace = ManjuDirectWriteRenderer.CreateFontFaceFromFile(fontPath);
                    if (_fontFace != null)
                    {
                        _nativeFontFace = (nint)ComInterfaceMarshaller<NativeMethods.IDWriteFontFace>.ConvertToUnmanaged(_fontFace);
                    }
                    var fd = _engine.GetFontData();
                    if (fd != null) _fontData = fd;
                }
            }
        }

        // Main-window palette mirrored onto this companion window.
        // The Dictionary Window is a companion of the main input surface, so its theme follows the main window's.
        // Stashed as packed 0xAARRGGBB so a theme switch that happens while the window is destroyed is
        // replayed on the next ResourcesReady() instead of being lost.
        private bool _hasTheme;
        private bool _themePending;
        private uint _thBg, _thText, _thBorder, _thHighlight, _thAccent;

        public void ApplyTheme(uint bgArgb, uint textArgb, uint borderArgb, uint highlightArgb, uint accentArgb)
        {
            _thBg = bgArgb; _thText = textArgb; _thBorder = borderArgb;
            _thHighlight = highlightArgb; _thAccent = accentArgb;
            _hasTheme = true;
            _themePending = true;
            RecolorNow();
            // Repaint through the UI-thread marshalling path (never touch the HWND from here).
            SyncVisibility();
        }

        /// <summary>
        /// The colour Render clears the window with.
        ///
        /// The background follows the stashed palette. ApplyTheme recolours a background BRUSH, but
        /// Render never draws with that brush (its only uses are create / SetColor / release): the
        /// window is cleared with this colour. A fixed clear colour would leave the dictionary
        /// window's background near-white under every theme.
        ///
        /// Alpha is forced opaque on purpose: this is an HwndRenderTarget with alpha-IGNORE, and a
        /// translucent clear produces black-undefined pixels on some hosts.
        /// A theme colour carrying a &lt; 1 alpha is therefore drawn opaque.
        /// </summary>
        private D2D1_COLOR_F ActiveBackgroundColor()
        {
            if (!_hasTheme)
                return new D2D1_COLOR_F { r = 0.98f, g = 0.98f, b = 1.00f, a = 1.00f };
            return new D2D1_COLOR_F
            {
                r = ((_thBg >> 16) & 0xFF) / 255f,
                g = ((_thBg >> 8) & 0xFF) / 255f,
                b = (_thBg & 0xFF) / 255f,
                a = 1.00f,
            };
        }

        /// <summary> Recolour the EXISTING brushes in place (SetColor only — never release /
        /// recreate, brush lifetime is the host-freeze hazard). Safe no-op before resources exist.</summary>
        private void RecolorNow()
        {
            if (!_hasTheme || !_themePending) return;
            if (_bgBrush == null || _renderTarget == null) return;   // replay later from ResourcesReady
            static D2D1_COLOR_F U(uint v) => new D2D1_COLOR_F
            {
                a = ((v >> 24) & 0xFF) / 255f,
                r = ((v >> 16) & 0xFF) / 255f,
                g = ((v >> 8) & 0xFF) / 255f,
                b = (v & 0xFF) / 255f,
            };
            try
            {
                _bgBrush?.SetColor(U(_thBg));
                _borderBrush?.SetColor(U(_thBorder));
                _textBrush?.SetColor(U(_thText));
                _digitBrush?.SetColor(U(_thText));
                _highlightBrush?.SetColor(U(_thHighlight));
                _latinBrush?.SetColor(U(_thAccent));
                _themePending = false;
            }
            catch (Exception) { }
        }

        private static NativeMethods.ID2D1SolidColorBrush CreateBrush(NativeMethods.ID2D1HwndRenderTarget rt, float r, float g, float b, float a)
        {
            var color = new D2D1_COLOR_F { r = r, g = g, b = b, a = a };
            rt.CreateSolidColorBrush(color, 0, out var brush);
            return brush!;
        }

        private void Render()
        {
            if (_renderTarget == null) return;

            string step = "begin-draw";
            try
            {
                _renderTarget.BeginDraw();

                // Opaque clear. HwndRenderTarget defaults to alpha-IGNORE
                // surface; a=0 is effectively black-undefined and leads to mismatched
                // pixel format expectations on some hosts.
                step = "clear";
                _renderTarget.Clear(ActiveBackgroundColor());

                // Paint against DIP layout extent, NOT physical _windowW/_windowH.
                // The render target is in DIP (SetDpi); using physical magnitudes here would push
                // the gloss bar off the visible canvas at 200% scaling.
                float wDip = _layoutWDip;
                float hDip = _layoutHDip;

                // Border (no separate bg fill needed; Clear handled it)
                step = "border";
                if (_nativeBorderBrush != 0)
                {
                    _renderTarget.DrawRectangle(new RECT_F { left = 0.5f, top = 0.5f, right = wDip - 0.5f, bottom = hDip - 0.5f }, _nativeBorderBrush, 1.0f, (nint)0);
                }

                int n = EffectiveColumnCount();   // same screen-capped column count as the sizing pass
                _columnHitRects.Clear();
                if (n == 0)
                {
                    step = "end-draw-empty";
                    _renderTarget.EndDraw(out _, out _);
                    return;
                }

                float digitRowY = Padding;
                float bodyTop = digitRowY + DigitRowHeight + DigitRowGap;
                float glossBarTop = hDip - Padding - GlossBarHeight;
                float bodyBottom = glossBarTop - 2;
                var entriesSnap = _state.Entries; // single snapshot; thread-safe copy from lock
                int highlightedIdx = _state.HighlightedIdx;
                bool kwHasFocus = _state.HasFocusContext;

                for (int i = 0; i < n && i < entriesSnap.Count; i++)
                {
                    float colLeft = Padding + i * (ColGroupWidth + DividerThickness);
                    float colRight = colLeft + ColGroupWidth;
                    _columnHitRects.Add((i, colLeft, digitRowY, colRight, bodyBottom));

                    // The gloss-shown column (highlightedIdx, default 0) is ALWAYS
                    // visually marked: the panel below shows the first candidate's detail by default, so
                    // the highlight must be consistent with the gloss bar.
                    // Focus (Tab) is signalled separately by dimming the MAIN window
                    // (presenter's _unfocusedOverlayBrush); this pass only draws the selected-column band.
                    bool isHighlighted = (i == highlightedIdx);
                    if (isHighlighted && _nativeHighlightBrush != 0)
                    {
                        step = $"col[{i}]-highlight-fill";
                        _renderTarget.FillRectangle(new RECT_F { left = colLeft, top = digitRowY, right = colRight, bottom = bodyBottom }, _nativeHighlightBrush);
                    }

                    // Digit label
                    string digit = KnowledgeWindow.DisplayDigitForIdx(i);
                    if (_nativeDigitBrush != 0 && !string.IsNullOrEmpty(digit))
                    {
                        step = $"col[{i}]-digit";
                        ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                            _renderTarget, digit, colLeft, digitRowY, ColGroupWidth, DigitRowHeight, DigitFontSize, _nativeDigitBrush);
                    }

                    var entry = entriesSnap[i];

                    // Manchu vertical column + Latin annotation right-side.
                    // Latin is not a parallel sub-column; it's small annotation per glyph.
                    if (_nativeFontFace != 0 && _fontData != null && !string.IsNullOrEmpty(entry.ManchuScript) && _nativeTextBrush != 0)
                    {
                        // Shape the WHOLE word ONCE → correct CONTEXTUAL forms
                        // (initial/medial/final, NOT isolated), then lay it out as a REAL vertical column via the
                        // renderer's own ink-width accumulation (MeasureShapedTextRowYCenters on the full word →
                        // each glyph's ACTUAL center + height). Per glyph: draw it at its measured center, and
                        // STACK its romanization letters vertically WITHIN that glyph's actual height (height-
                        // aligned to the Manchu cluster). A merged glyph (consonant+vowel fused into one glyph,
                        // e.g. b+a) shows b OVER a: two SEPARATE Latin letters spread down the glyph's height,
                        // NEVER a merged "ba" label (merging is ordinary Manchu; the romanization
                        // stays two letters, not one compound letter). Single-letter glyphs → one centred letter (sparser).
                        // Anchor = glyph count; Cluster maps each romanization unit to the glyph that wraps it.
                        step = $"col[{i}]-shape";
                        // `display.cursive` (configuration.yaml, default false) selects
                        // how the dict window renders the Manchu word — DICT WINDOW DISPLAY ONLY (the candidate
                        // window does not read it). Both values select a live path:
                        //   false = per-character: one code point per cell, each shaped with ZWJ join-context so it comes
                        //           out in its CONNECTED (init/medi/fina) form — NOT isolated — as exactly ONE
                        //           glyph. Merging needs two real adjacent code points together, so it cannot fire →
                        //           clean uniform per-letter cells, hard 1:1 with the romanization.
                        //   true  = joined: shape the WHOLE word → letters join and the velar/labial+vowel pairs
                        //           merge into one glyph; each glyph's romanization unit(s) stack
                        //           vertically within that glyph's actual height.
                        bool cursive = _engine.GetConfigStore().GetBool("display.cursive", false);
                        // The joined column is LEFTMOST. Per-letter Manchu
                        // + Latin follow it: [joined] [gap] [per-letter] [gap] [Latin].
                        float cursiveLeftX = colLeft;                                                   // 1st col: the fused, natively vertical whole-word spine
                        float analysisLeft = colLeft + CursiveColWidth + CursiveColGap;
                        // Analytical area = the main preview's col3 (main) + col4 (comparison) pair
                        // Main and secondary must not be reversed: in the main window the left
                        // is the main column (carrying everything: fused glyphs spanning rows, plus plain letters) and the right is secondary (only the separated components at fusing positions). A dict layout with the complete per-character column on the right and the sparse fused comparison on the left would make
                        // the two windows read in opposite directions. This is aligned with the main window: the left column is the main one (plain-letter cells + fused glyphs spanning their rows), the right holds only a fusion's separated components.
                        float hefiCenterX = analysisLeft + ManchuSubColWidth / 2f;                                      // col3, the main column (left: plain letters + fused glyphs centred across their rows)
                        float manchuCenterX = analysisLeft + ManchuSubColWidth + CursiveColGap + ManchuSubColWidth / 2f; // col4, the comparison column (right: only a fusion's separated components)
                        float latinLeft = analysisLeft + 2f * ManchuSubColWidth + CursiveColGap + ManchuLatinGap;        // the Latin annotation (to the right of col3 and col4)
                        var latBrush = _nativeLatinBrush != 0 ? _nativeLatinBrush : _nativeTextBrush;
                        // Romanization units (digraph-aware), each with the code-point count it spans.
                        var segs = entry.RomanSegments;
                        if ((segs == null || segs.Count == 0) && !string.IsNullOrEmpty(entry.Mollendorff))
                            segs = new[] { (entry.Mollendorff, entry.ManchuScript.Length) };

                        if (cursive)
                        {
                            // ===== joined: whole-word shape; letters join and merge; Latin stacked within each cluster =====
                            List<ManjuShaperCore.FinalGlyph> shaped;
                            fixed (byte* pFont = _fontData)
                                shaped = ManjuShaperCore.FullShape(entry.ManchuScript, pFont, _fontData.Length, isVertical: true);
                            if (shaped != null && shaped.Count > 0)
                            {
                                // Group each romanization UNIT onto the glyph whose Cluster wraps its start code
                                // point. Units sharing one merged glyph (e.g. b + a) land in the SAME list → stacked.
                                var glyphLatin = new List<string>[shaped.Count];
                                for (int g = 0; g < shaped.Count; g++) glyphLatin[g] = new List<string>();
                                if (segs != null)
                                {
                                    int cpOffset = 0;
                                    foreach (var sg in segs)
                                    {
                                        int gIdx = 0; long bestC = -1;
                                        for (int g = 0; g < shaped.Count; g++)
                                        {
                                            long c = shaped[g].Cluster;
                                            if (c <= cpOffset && c > bestC) { bestC = c; gIdx = g; }
                                        }
                                        if (!string.IsNullOrEmpty(sg.latin) && gIdx >= 0 && gIdx < glyphLatin.Length)
                                            glyphLatin[gIdx].Add(sg.latin);
                                        cpOffset += Math.Max(1, sg.cpLen);
                                    }
                                }

                                // Real column layout: measure every glyph's center via the renderer's own ink-width
                                // accumulation (variable per-glyph height); fall back to a uniform grid on failure.
                                float colTop = bodyTop + 2f;
                                var colCenters = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(
                                    _nativeFontFace, shaped, 0f, ManchuFontSize, _upem);
                                var centerY = new float[shaped.Count];
                                if (colCenters != null && colCenters.Count == shaped.Count)
                                {
                                    float firstHalf = shaped.Count > 1 ? (colCenters[1] - colCenters[0]) * 0.5f : ManchuFontSize * 0.5f;
                                    for (int g = 0; g < shaped.Count; g++) centerY[g] = colTop + firstHalf + (colCenters[g] - colCenters[0]);
                                }
                                else
                                {
                                    for (int g = 0; g < shaped.Count; g++) centerY[g] = colTop + (g + 0.5f) * ManchuFontSize;
                                }
                                float CellTop(int g) => g == 0
                                    ? centerY[0] - (shaped.Count > 1 ? (centerY[1] - centerY[0]) * 0.5f : ManchuFontSize * 0.5f)
                                    : (centerY[g - 1] + centerY[g]) * 0.5f;
                                float CellBottom(int g) => g == shaped.Count - 1
                                    ? centerY[g] + (centerY[g] - CellTop(g))
                                    : (centerY[g] + centerY[g + 1]) * 0.5f;

                                for (int g = 0; g < shaped.Count; g++)
                                {
                                    float yc = centerY[g];
                                    float cTop = CellTop(g), cBot = CellBottom(g);
                                    if (cTop > bodyBottom) break; // glyph cell past the visible body

                                    var single = new List<ManjuShaperCore.FinalGlyph> { shaped[g] };
                                    var atZero = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(
                                        _nativeFontFace, single, 0f, ManchuFontSize, _upem);
                                    if (atZero.Count > 0)
                                        ManjuDirectWriteRenderer.DrawShapedText(
                                            _renderTarget, _nativeFontFace, single, manchuCenterX, yc - atZero[0], ManchuFontSize, _nativeTextBrush, _upem);

                                    var letters = glyphLatin[g];
                                    int nLat = letters.Count;
                                    float span = Math.Max(cBot - cTop, LatinFontSize + 2f);
                                    for (int k = 0; k < nLat; k++)
                                    {
                                        if (string.IsNullOrEmpty(letters[k])) continue;
                                        float lyc = cTop + span * (k + 0.5f) / nLat;
                                        float boxH = LatinFontSize + 4f;
                                        float annTop = lyc - boxH * 0.5f;
                                        if (annTop > bodyBottom) continue;
                                        ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                                            _renderTarget, letters[k].ToUpperInvariant(),
                                            latinLeft, annTop, LatinAnnotationWidth, boxH, LatinFontSize,
                                            latBrush, centerHorizontally: true);
                                    }
                                }
                            }
                        }
                        else
                        {
                            // ===== per-character: one code point per cell, ZWJ join-context → contextual form, NO merging =====
                            string ms = entry.ManchuScript ?? string.Empty;
                            int N = ms.Length;
                            // Latin per romanization unit, anchored on the unit's FIRST code point.
                            var cpLatin = new string[N];
                            if (segs != null)
                            {
                                int off = 0;
                                foreach (var sg in segs)
                                {
                                    if (off >= 0 && off < N && !string.IsNullOrEmpty(sg.latin)) cpLatin[off] = sg.latin;
                                    off += Math.Max(1, sg.cpLen);
                                }
                            }
                            float colTop = bodyTop + 2f;
                            // Stack cells by INK edges with the shared constant gap (a fixed
                            // 1.15× rowH grid → the between-cell ink gap would vary with glyph size: uneven pitch).
                            // Each cell's ink-top sits at cursorTop; advance by the cell's ink height + InkEdgeGapDesignUnits.
                            // Fused cells stay single.
                            float gapPx = ManjuDirectWriteRenderer.InkEdgeGapDesignUnits * (ManchuFontSize / _upem);
                            float wordGapPx = ManchuFontSize * 0.6f;   // inter-word gap (whitespace boundary)
                            float cursorTop = colTop;
                            // Keep the SHAPE exactly as the preview does: shape the WHOLE word
                            // ONCE (real neighbour context → the velar's harmony class + every contextual form correct), then take
                            // each cell's glyphs from THAT shaping by cluster range. A per-cell ZWJ stub would shape every
                            // letter blind to its neighbours: vowel harmony would be lost and the ink would come out
                            // isolated. This is the same FullShape(wholeWord) the candidate window and the joined column already use.
                            List<ManjuShaperCore.FinalGlyph> wordGlyphs;
                            fixed (byte* pFont = _fontData)
                                wordGlyphs = ManjuShaperCore.FullShape(ms, pFont, _fontData.Length, isVertical: true);
                            // Keep the UNDECOMPOSED whole-word shaping (fusions intact) for the col3 fused
                            // column BEFORE the separated-form decompose below. This is the same fused form the candidate-window preview col3 uses.
                            var rawGlyphs = (wordGlyphs != null) ? new List<ManjuShaperCore.FinalGlyph>(wordGlyphs) : new List<ManjuShaperCore.FinalGlyph>();
                            // Dict window ONLY: decompose velar + feminine-vowel fused glyphs
                            // (e.g. GhI) back into the font's own ligature components (Gh + I) so each letter gets its
                            // own cell with the fused glyph's REAL i form, which is semantically cleaner. The main candidate/preview window shows the
                            // fused form.
                            wordGlyphs = DecomposeVelarLigaturesForPerCell(wordGlyphs, ms, N);
                            var glyphClusters = new HashSet<int>();
                            if (wordGlyphs != null) foreach (var wg in wordGlyphs) glyphClusters.Add((int)wg.Cluster);
                            // Record each separated (col4) cell's vertical span keyed by its start cluster, so
                            // the col3 fused pass below can center each fusion glyph across the cells it covers (row-aligned).
                            var cellRows = new List<(int cl, float top, float bot)>();
                            // The cluster coverage set of a fusion group (the same criterion as the col3 pass below:
                            // cluster-span ≥ 2 and the decompose table has components — fusion by render / cluster span, NOT ligMap
                            // membership). It puts plain-letter cells in the left main column and a fusion's separated components in the right comparison column, so the dictionary reads in the same direction as the main window.
                            var dictLigMap = GetLigatureReverseMap();
                            var fusionCovered = ComputeFusionCoveredClusters(rawGlyphs, N, dictLigMap);
                            int c = 0;
                            while (c < N)
                            {
                                // Gather the base letter + any trailing FVS/MVS (U+180B..U+180F) into ONE cell so
                                // those modifiers fold into the base instead of taking an empty row.
                                int start = c, end = c + 1;
                                // Fold to the ROMANIZATION-UNIT boundary: a cp with no
                                // own Latin (cpLatin[end]==null) is a continuation of the current unit — the empty-rime
                                // tail (ᡟ U+185F for ci/si, ᡳ U+1873 for zi/chi/zhi) OR FVS/MVS (180B-180F) — so it
                                // folds into the base cell. Each per-character cell is then ONE unit with its Latin, never an
                                // orphan tail-cell with null Latin.
                                // Native base+ᡳ (ᠰᡳ in silhi, ᡧᡳ in shimin) are SEPARATE units, so stay per-letter.
                                if (segs != null)
                                    while (end < N && cpLatin[end] == null) end++;
                                else
                                    while (end < N && ((ms[end] >= (char)0x180B && ms[end] <= (char)0x180F) || ms[end] == (char)0x185F)) end++;
                                // Swallow following unit(s) whose glyph was ABSORBED into this cell's
                                // ligature (velar + feminine vowel: g+i → ONE GI glyph; the i has no glyph of its own). Such a
                                // position has no glyph cluster → it belongs in THIS cell (its Latin folds in here) so
                                // it never leaves an empty row. Never swallow a real whitespace word-gap.
                                while (end < N && !glyphClusters.Contains(end) && !char.IsWhiteSpace(ms[end])) end++;
                                string cluster = ms.Substring(start, end - start);
                                // Phrase boundary: a whitespace separator between
                                // words must (a) reset joining so the NEXT word's first letter renders INITIAL (not
                                // medial) — leftJoin/rightJoin never ZWJ across a space — AND (b) keep a VISIBLE
                                // inter-word gap so the per-character column keeps the source phrase's word boundaries. The
                                // separator's own cell is not drawn, but the cursor DOES advance by the inter-word gap;
                                // a bare `continue` would advance no row and merge the words into one run.
                                if (cluster.Length == 1 && char.IsWhiteSpace(cluster[0]))
                                {
                                    c = end;
                                    cursorTop += wordGapPx;   // inter-word gap
                                    continue;
                                }
                                // This cell's glyphs = the whole-word shaping's glyphs whose source
                                // cluster falls in [start,end). Same shapes the preview shows; no per-cell re-shaping.
                                var ug = new List<ManjuShaperCore.FinalGlyph>();
                                if (wordGlyphs != null)
                                    foreach (var wg in wordGlyphs)
                                        if (wg.Cluster >= (uint)start && wg.Cluster < (uint)end) ug.Add(wg);
                                // place this cell's ink-top at cursorTop; yc = cell ink-center (for Latin).
                                var (cellTopOff, cellH) = ManjuDirectWriteRenderer.MeasureGlyphsInkBoxDip(_nativeFontFace, ug, ManchuFontSize, _upem, ManjuDirectWriteRenderer.InkEdgeGapDesignUnits);
                                if (cellH <= 0f) cellH = ManchuFontSize;
                                float yc = cursorTop + cellH * 0.5f;
                                if (cursorTop > bodyBottom) break;
                                if (ug != null && ug.Count > 0)
                                {
                                    // A plain-letter cell is the main body → left main column; a fusion's separated component cell is the comparison → right column (the same main/secondary direction as the main window).
                                    float cellX = fusionCovered.Contains(start) ? manchuCenterX : hefiCenterX;
                                    ManjuDirectWriteRenderer.DrawShapedText(
                                        _renderTarget, _nativeFontFace, ug, cellX, cursorTop - cellTopOff, ManchuFontSize, _nativeTextBrush, _upem, 0f, ManjuDirectWriteRenderer.InkEdgeGapDesignUnits);
                                }
                                // Latin spans the whole cell (a ligature cell like GI covers g+i → "gi").
                                string lat = string.Empty;
                                for (int u = start; u < end && u < N; u++) if (cpLatin[u] != null) lat += cpLatin[u];
                                if (!string.IsNullOrEmpty(lat))
                                {
                                    float boxH = LatinFontSize + 4f;
                                    float annTop = yc - boxH * 0.5f;
                                    if (annTop <= bodyBottom)
                                        ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                                            _renderTarget, lat.ToUpperInvariant(),
                                            latinLeft, annTop, LatinAnnotationWidth, boxH, LatinFontSize,
                                            latBrush, centerHorizontally: true);
                                }
                                cellRows.Add((start, cursorTop, cursorTop + cellH));   // col4 cell span for col3 row-alignment
                                c = end;
                                cursorTop += cellH + gapPx;
                            }

                            // col3, the fused main column: render each UNDECOMPOSED whole-word glyph (rawGlyphs) row-aligned
                            // across the separated (col4) cells it covers — a fusion glyph centres over its component cells,
                            // a plain letter aligns to its one cell. Mirrors the candidate-window preview's col3 (fused) / col4 (separated) comparison.
                            // The 1st-column native spine is drawn separately; this pass reuses the SAME DrawShapedText primitive as col4 (no COM beyond what col4 uses).
                            if (rawGlyphs.Count > 0 && cellRows.Count > 0 && _nativeFontFace != 0 && _nativeTextBrush != 0)
                            {
                                var rawClusters = new List<int>(rawGlyphs.Count);
                                foreach (var rg in rawGlyphs) rawClusters.Add((int)rg.Cluster);
                                var col3LigMap = dictLigMap;   // the same table and the same criterion as fusionCovered
                                int col3Drawn = 0;
                                foreach (var (gi, mid) in ComputeCol3RowCenters(rawClusters, N, cellRows))
                                {
                                    // col3 renders only a **real fusion** glyph (cluster-span ≥ 2 and present in the
                                    // decompose table, the same criterion as DecomposeVelarLigaturesForPerCell). A plain letter stays in the
                                    // per-character (col4) column only, because rendering all rawGlyphs into col3 would show every plain letter twice, and only the fused/separated comparison needs two columns.
                                    int spanCl = ((gi + 1 < rawClusters.Count) ? rawClusters[gi + 1] : N) - rawClusters[gi];
                                    if (spanCl < 2) continue;
                                    if (col3LigMap == null || !col3LigMap.TryGetValue(rawGlyphs[gi].GlyphId, out var col3Comps) || col3Comps.Length < 2) continue;
                                    var one = new List<ManjuShaperCore.FinalGlyph> { rawGlyphs[gi] };
                                    var oc = ManjuDirectWriteRenderer.MeasureShapedTextRowYCenters(_nativeFontFace, one, 0f, ManchuFontSize, _upem);
                                    if (oc.Count > 0)
                                    {
                                        ManjuDirectWriteRenderer.DrawShapedText(
                                            _renderTarget, _nativeFontFace, one, hefiCenterX, mid - oc[0], ManchuFontSize, _nativeTextBrush, _upem);
                                        col3Drawn++;
                                    }
                                }
                            }
                        }

                        // The joined column: the whole word rendered by
                        // the OS-native DirectWrite VERTICAL layout with OUR font — a continuous, gapless connected
                        // spine: take the dictionary's code points, apply our font, and hand it to the system to lay out vertically.
                        // The font collection is built via IDWriteFontSetBuilder1::AddFontFile; building it through
                        // the CreateFontFaceReference overload hits a COM overload bug that freezes the host. Falls
                        // back to the shaper render on any setup failure, so it can never crash or blank the column.
                        {
                            float colTopNV = bodyTop + 2f;
                            string? fontPathNV = _engine.GetFontFilePath();
                            bool nativeOk = false;
                            if (!string.IsNullOrEmpty(fontPathNV) && _nativeTextBrush != 0 && !string.IsNullOrEmpty(entry.ManchuScript) && _renderTarget != null)
                            {
                                nativeOk = ManjuDirectWriteRenderer.DrawNativeVerticalText(
                                    _renderTarget, fontPathNV, entry.ManchuScript!,
                                    cursiveLeftX, colTopNV, CursiveColWidth, Math.Max(0f, bodyBottom - colTopNV),
                                    ManchuFontSize, _nativeTextBrush);
                            }
                            if (!nativeOk && _renderTarget != null && _nativeTextBrush != 0)
                            {
                                // NO shaper fallback for the joined column — pure native.
                                // On a native-render failure show a visible placeholder (□) so the failure is
                                // SEEN, never masked by the shaper render.
                                ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                                    _renderTarget, "□", cursiveLeftX, bodyTop + 2f, CursiveColWidth, ManchuFontSize + 4f,
                                    ManchuFontSize, _nativeTextBrush, centerHorizontally: true);
                            }
                        }
                    }

                    // Divider on right edge (skip last; skip highlighted column to create "spillover")
                    if (i < n - 1 && _nativeBorderBrush != 0)
                    {
                        step = $"col[{i}]-divider";
                        float divTop = bodyTop;
                        float divBottom = isHighlighted ? bodyBottom - 6 : bodyBottom;
                        _renderTarget.FillRectangle(new RECT_F { left = colRight, top = divTop, right = colRight + DividerThickness, bottom = divBottom }, _nativeBorderBrush);
                    }
                }

                // Gloss bar — always show if any entry exists; defaults to entry 0 when no
                // explicit highlight.
                int glossIdx = (highlightedIdx >= 0 && highlightedIdx < entriesSnap.Count) ? highlightedIdx : 0;
                if (entriesSnap.Count > 0 && glossIdx < entriesSnap.Count)
                {
                    var glossEntry = entriesSnap[glossIdx];
                    // [GLOSS-SEPARATOR] Separator between our Möllendorff romanization and the English gloss.
                    // The bracketed tag above marks this plain-comma separator.
                    // The IME adds this separator itself (it is not from the source dictionary), so it
                    // is a plain comma: a middle dot is a Japanese convention, irrelevant to Manchu/Mongolian.
                    string glossText = string.IsNullOrEmpty(glossEntry.Gloss)
                        ? glossEntry.Mollendorff
                        : (glossEntry.Mollendorff + ", " + glossEntry.Gloss);
                    step = "gloss-bar-fill";
                    if (_nativeHighlightBrush != 0)
                    {
                        _renderTarget.FillRectangle(new RECT_F { left = Padding, top = glossBarTop, right = wDip - Padding, bottom = hDip - Padding }, _nativeHighlightBrush);
                    }
                    if (_nativeTextBrush != 0 && !string.IsNullOrEmpty(glossText))
                    {
                        step = "gloss-bar-text";
                        ManjuDirectWriteRenderer.DrawTextWithSystemFont(
                            _renderTarget, glossText,
                            Padding + 4, glossBarTop + 4, wDip - 2 * Padding - 8, GlossBarHeight - 8, GlossFontSize, _nativeTextBrush);
                    }
                }

                step = "end-draw";
                _renderTarget.EndDraw(out _, out _);
            }
            catch (Exception)
            {
                // If BeginDraw was called but EndDraw failed, the render target may be in a bad
                // state. Discard it; next paint will recreate.
                try { _renderTarget?.EndDraw(out _, out _); } catch { /* swallow */ }
                ReleaseGraphics();
            }
        }

        private void ReleaseGraphics()
        {
            _bgBrush = null;
            _borderBrush = null;
            _textBrush = null;
            _digitBrush = null;
            _highlightBrush = null;
            _latinBrush = null;
            _renderTarget = null;
            _fontFace = null;
            _nativeFontFace = 0;
            _nativeBgBrush = 0;
            _nativeBorderBrush = 0;
            _nativeTextBrush = 0;
            _nativeDigitBrush = 0;
            _nativeHighlightBrush = 0;
            _nativeLatinBrush = 0;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseGraphics();
            if (_hwnd != 0)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = 0;
            }
            if (_selfHandle.IsAllocated) _selfHandle.Free();
        }
    }
}

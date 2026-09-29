using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;

namespace CSharpTSFInput.ManjuShaper
{
    public unsafe partial class ManjuDirectWriteRenderer
    {
        private const string UnavailableOnComFailure = "UNAVAILABLE_ON_COM_FAILURE";

        internal readonly struct VerticalBodyLayoutGlyphMetrics
        {
            internal VerticalBodyLayoutGlyphMetrics(
                int rotationMode,
                float scale,
                float accumulatedAdvanceYDip,
                float advanceOffset,
                float ascenderOffset,
                float screenCenterX,
                float measuredGlyphWidth,
                float outlineScreenWidth,
                float outlineScreenHeight,
                bool usedOutlineBounds,
                float left,
                float right)
            {
                RotationMode = rotationMode;
                Scale = scale;
                AccumulatedAdvanceYDip = accumulatedAdvanceYDip;
                AdvanceOffset = advanceOffset;
                AscenderOffset = ascenderOffset;
                ScreenCenterX = screenCenterX;
                MeasuredGlyphWidth = measuredGlyphWidth;
                OutlineScreenWidth = outlineScreenWidth;
                OutlineScreenHeight = outlineScreenHeight;
                UsedOutlineBounds = usedOutlineBounds;
                Left = left;
                Right = right;
            }

            internal int RotationMode { get; }
            internal float Scale { get; }
            internal float AccumulatedAdvanceYDip { get; }
            internal float AdvanceOffset { get; }
            internal float AscenderOffset { get; }
            internal float ScreenCenterX { get; }
            internal float MeasuredGlyphWidth { get; }
            internal float OutlineScreenWidth { get; }
            internal float OutlineScreenHeight { get; }
            internal bool UsedOutlineBounds { get; }
            internal float Left { get; }
            internal float Right { get; }
        }

        internal readonly struct VerticalBodyLayoutMetrics
        {
            internal VerticalBodyLayoutMetrics(float scale, float minLeft, float maxRight, float totalAdvanceYDip, float occupiedWidth)
            {
                Scale = scale;
                MinLeft = minLeft;
                MaxRight = maxRight;
                TotalAdvanceYDip = totalAdvanceYDip;
                OccupiedWidth = occupiedWidth;
            }

            internal float Scale { get; }
            internal float MinLeft { get; }
            internal float MaxRight { get; }
            internal float TotalAdvanceYDip { get; }
            internal float OccupiedWidth { get; }
        }

        internal enum VerticalBodyWidthSourceTier
        {
            OutlineBounds = 1,
            Unavailable = 2
        }

        internal readonly struct VerticalBodyWidthSourceSelection
        {
            internal VerticalBodyWidthSourceSelection(
                VerticalBodyWidthSourceTier sourceTier,
                int glyphCount,
                bool fontFaceAvailable,
                int outlineAttemptedGlyphCount,
                int outlineExtractionSucceededGlyphCount)
            {
                SourceTier = sourceTier;
                GlyphCount = glyphCount;
                FontFaceAvailable = fontFaceAvailable;
                OutlineAttemptedGlyphCount = outlineAttemptedGlyphCount;
                OutlineExtractionSucceededGlyphCount = outlineExtractionSucceededGlyphCount;
            }

            internal VerticalBodyWidthSourceTier SourceTier { get; }
            internal int GlyphCount { get; }
            internal bool FontFaceAvailable { get; }
            internal int OutlineAttemptedGlyphCount { get; }
            internal int OutlineExtractionSucceededGlyphCount { get; }
        }

        // Rotation mode toggle
        // 0=none, 1=CW90, 2=180, 3=CCW90
        private static int _rotationMode = 1; // default 1: Clockwise 90° orientation

        // Shared uniform INK-EDGE gap (design units) for BOTH the candidate-window card
        // body AND the dict-window separated-form column, so their letter spacing is consistent. Chosen as 80 (≈2px @
        // FontSize 32). Layout keeps the VISUAL gap between consecutive glyphs' INK constant (accounts for LSB
        // bearing), so the pitch is even. Fused glyphs stay single (no decompose in the candidate).
        public const float InkEdgeGapDesignUnits = 80f;
        public static int RotationMode => _rotationMode;
        public static void CycleRotationMode()
        {
            _rotationMode = (_rotationMode + 1) % 4;
            string[] modeNames = { "No rotation (0°)", "Clockwise 90°", "180°", "Counter-clockwise 90°" };
        }

        private static volatile NativeMethods.IDWriteFactory? _factory;
        private static volatile NativeMethods.ID2D1Factory? _d2dFactory;
        private static readonly Guid IID_IDWriteFactory = new Guid("b859ee5a-d838-4b5b-a2e8-1adc7d93db48");
        private static readonly Guid IID_ID2D1Factory = new Guid("06152247-6f50-465a-9245-118bfd3b6007");

        // OS-native vertical text path for the dictionary's joined column.
        // Renders the dictionary's prepared Manchu codepoints by applying the bundled font and letting
        // DirectWrite lay them out vertically, NOT through ManjuShaper. The custom shaper's
        // DrawShapedText accumulates per-glyph ink-width + buffer, which is the source of the inter-glyph
        // gap; DirectWrite's native vertical layout uses the font's own designed connections/advances
        // → no manual gap. To name the bundled (non-system) font in CreateTextFormat, it is registered
        // in a private IDWriteFontCollection via the IDWriteFactory3 font-set-builder. The IIDs and
        // vtable slots come from dwrite_3.h (Windows SDK 10.0.26100.0).
        private static readonly Guid IID_IDWriteFactory3 = new Guid("9A1B41C3-D3BB-466A-87FC-FE67556A3B65");
        private static readonly Guid IID_IDWriteFontSetBuilder1 = new Guid("3FF7715F-3CDC-4DC6-9B72-EC5621DCCAFD");
        private static volatile nint _privateFontCollection;   // IDWriteFontCollection1* — built once, app lifetime
        private static volatile bool _privateFontCollectionTried; // don't re-attempt every paint after a failure
        private static nint _verticalTextFormat;               // IDWriteTextFormat* (vertical reading dir), cached per size
        private static float _verticalTextFormatSize = -1f;
        private const string MONGOLIAN_FAMILY = "Noto Sans Mongolian"; // family name of NotoSansMongolian-Regular.ttf

        private static void EnsureFactory()
        {
            if (_factory != null) return;

            // Lock-free initialization using Interlocked
            NativeMethods.IDWriteFactory? tempFactory = null;
            fixed (Guid* iid = &IID_IDWriteFactory)
            {
                int hr = NativeMethods.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, iid, out tempFactory);
                if (hr != 0 || tempFactory == null)
                {
                    return;
                }
            }

            // Attempt to set the factory. If another thread beat us, release our temp one.
            if (Interlocked.CompareExchange(ref _factory, tempFactory, null) != null)
            {
                // Another thread initialized it first, tempFactory will be GCed
                return;
            }
        }

        private static void EnsureD2DFactory()
        {
            if (_d2dFactory != null) return;

            NativeMethods.ID2D1Factory? tempFactory = null;
            fixed (Guid* iid = &IID_ID2D1Factory)
            {
                int hr = NativeMethods.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_SINGLE_THREADED, iid, 0, out tempFactory);
                if (hr != NativeMethods.S_OK || tempFactory == null)
                {
                    return;
                }
            }

            if (Interlocked.CompareExchange(ref _d2dFactory, tempFactory, null) != null)
            {
                return;
            }
        }

        /// <summary>
        /// Creates a font face from a file path.
        /// This is the ONLY supported method: memory-based loading is not supported,
        /// because COM threading issues cause deadlocks with it in NativeAOT.
        /// </summary>
        public static NativeMethods.IDWriteFontFace? CreateFontFaceFromFile(string fontFilePath)
        {
            if (string.IsNullOrEmpty(fontFilePath)) return null;

            EnsureFactory();
            if (_factory == null) return null;

            try
            {
                // Use CreateFontFileReference which doesn't require custom COM loaders
                nint pFilePath = Marshal.StringToCoTaskMemUni(fontFilePath);
                try
                {
                    int hrCreate = _factory.CreateFontFileReference(pFilePath, 0, out NativeMethods.IDWriteFontFile? fontFile);

                    if (hrCreate != NativeMethods.S_OK || fontFile == null)
                    {
                        return null;
                    }

                    // Get native pointer for vtable call
                    nint pFontFile = (nint)ComInterfaceMarshaller<NativeMethods.IDWriteFontFile>.ConvertToUnmanaged(fontFile);

                    // Call Analyze via vtable
                    // IDWriteFontFile vtable: 0=QueryInterface, 1=AddRef, 2=Release, 3=GetReferenceKey, 4=GetLoader, 5=Analyze
                    nint* vtable = *(nint**)pFontFile;
                    nint analyzePtr = vtable[5];

                    int isSupported = 0;
                    uint fileType = 0;
                    uint faceType = 0;
                    uint numFaces = 0;

                    int hrAnalyze = ((delegate* unmanaged[Stdcall]<nint, int*, uint*, uint*, uint*, int>)analyzePtr)(
                        pFontFile, &isSupported, &fileType, &faceType, &numFaces);

                    if (hrAnalyze != NativeMethods.S_OK || isSupported == 0)
                    {
                        Marshal.Release(pFontFile);
                        return null;
                    }

                    // Create FontFace
                    nint[] fontFileArray = { pFontFile };
                    fixed (nint* pFontFiles = fontFileArray)
                    {
                        int hrFace = _factory.CreateFontFace(faceType, 1, (nint)pFontFiles, 0, 0, out NativeMethods.IDWriteFontFace? fontFace);
                        Marshal.Release(pFontFile);

                        if (hrFace != NativeMethods.S_OK || fontFace == null)
                        {
                            return null;
                        }
                        return fontFace;
                    }
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pFilePath);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        // upem: the font's real UPEM.
        // Measure per-glyph Y centers using the SAME accumulation logic as DrawShapedText
        // (ink-width + buffer). Returns screen Y center of each glyph after CW 90° rotation,
        // suitable for aligning external column elements (Latin/Index columns in the four-column UI) to
        // each Mongolian glyph's actual visual row position. Without this, parallel columns drift relative
        // to the ink-width grid because they would otherwise use a fixed row height.
        //
        // Returns: list of screen Y centers parallel to glyphs[i]; empty list on metric fetch failure
        // (caller should fall back to fixed-row layout).
        public static List<float> MeasureShapedTextRowYCenters(
            nint pFontFace,
            List<ManjuShaperCore.FinalGlyph> glyphs,
            float baselineY,
            float fontSize,
            ushort upem = 1000,
            float uniformVerticalPitch = 0f,
            float uniformInkEdgeGapDesignUnits = 0f)
        {
            var result = new List<float>();
            if (glyphs == null || glyphs.Count == 0 || pFontFace == 0) return result;

            // Uniform INK-EDGE gap layout: a fused glyph counts as single, only the ink-edge gap is held, nothing is decomposed.
            // advance_i = gap + inkEnd_i - inkStart_{i+1} so consecutive glyphs' INK edges sit a CONSTANT gap
            // apart regardless of per-glyph bearing/size. Center = ink midpoint (highlight). Draw mirrors this.
            if (uniformInkEdgeGapDesignUnits > 0f &&
                FetchGlyphInkBoxesDesignUnits(pFontFace, glyphs, upem, out var ieStart, out var ieExt))
            {
                float sIE = ComputeScale(fontSize, upem);
                float runOriginIE = RunVerticalOriginYOffset(glyphs);
                float accIE = 0f;
                for (int i = 0; i < glyphs.Count; i++)
                {
                    float centerDU = accIE + ieStart[i] + ieExt[i] * 0.5f + EffectiveYOffset(glyphs[i], runOriginIE);
                    result.Add(baselineY + centerDU * sIE);
                    float inkEnd = ieStart[i] + ieExt[i];
                    float inkStartNext = (i + 1 < glyphs.Count) ? ieStart[i + 1] : 0f;
                    float adv = uniformInkEdgeGapDesignUnits + inkEnd - inkStartNext;
                    accIE += adv < 0f ? uniformInkEdgeGapDesignUnits : adv;
                }
                return result;
            }

            float scale = ComputeScale(fontSize, upem);
            // Under uniform pitch the layout MUST NOT depend on per-glyph ink (that
            // dependency is exactly what makes the separated-form stack uneven). Skip the ink fetch and use the fixed
            // pitch for advance AND a uniform half-pitch for centering, so the measure matches DrawShapedText's
            // uniform-pitch accumulation glyph-for-glyph (Draw + Measure must agree or highlight/columns drift).
            bool uniform = uniformVerticalPitch > 0f;
            // Mirrors DrawShapedText's default branch exactly: same ink BOXES, same advance, and a
            // centre taken at the item's own ink midpoint. A centre of origin + extent/2 would report
            // the midpoint of a box starting at the ORIGIN, true only for a glyph whose leading bearing is
            // zero, and every consumer of these centres (row grid, Latin/Index alignment, highlight band)
            // would be told an item sat where its ink did not.
            float[]? mStart = null, mExt = null;
            bool boxMode = !uniform && FetchGlyphInkBoxesDesignUnits(pFontFace, glyphs, upem, out mStart, out mExt);
            float runOrigin = RunVerticalOriginYOffset(glyphs);
            const float BUFFER_UPEM_FRACTION = 0.02f;
            float bufferDesignUnits = upem * BUFFER_UPEM_FRACTION;

            // Build a per-row Y-advance list scaled to DIP, then delegate the
            // centers-from-baseline math to the shared CSharpTSFInput.Rendering.MongolianVertical
            // library. Both the main candidate window AND the floating Knowledge Window funnel
            // through the same external library for vertical-column layout.
            var yAdvancesScaled = new List<float>(glyphs.Count);
            var halfInkWidthsScaled = new List<float>(glyphs.Count);
            var yOffsetsScaled = new List<float>(glyphs.Count);
            for (int i = 0; i < glyphs.Count; i++)
            {
                float advanceDelta;
                float halfInkWidth;
                if (uniform)
                {
                    // separated-form path: fixed pitch + uniform centering (half-pitch), no
                    // per-glyph ink dependence, no first-glyph extra → perfectly even component stack.
                    advanceDelta = uniformVerticalPitch;
                    halfInkWidth = uniformVerticalPitch * 0.5f;
                    yAdvancesScaled.Add(advanceDelta * scale);
                    halfInkWidthsScaled.Add(halfInkWidth * scale);
                    yOffsetsScaled.Add(EffectiveYOffset(glyphs[i], runOrigin) * scale);
                    continue;
                }
                if (boxMode)
                {
                    float inkEnd = mStart![i] + mExt![i];
                    float inkStartNext = (i + 1 < glyphs.Count) ? mStart[i + 1] : 0f;
                    advanceDelta = bufferDesignUnits + inkEnd - inkStartNext;
                    if (advanceDelta < 0f) advanceDelta = bufferDesignUnits;
                    halfInkWidth = mStart[i] + mExt[i] * 0.5f;   // the item's OWN ink midpoint
                }
                else
                {
                    advanceDelta = GetVerticalAdvanceMagnitude(glyphs[i].YAdvance);
                    halfInkWidth = advanceDelta * 0.5f;
                }
                // Widen ONLY the first gap (char0↔char1). The extra does not touch the global buffer
                // (0.02 ≈ sub-pixel): ONLY the first glyph's advance gets it, and it is added to the
                // ADVANCE only, so char0's own center holds and char1+ shift down.
                // FIRST_GLYPH_EXTRA_UPEM_FRACTION is the single tunable knob.
                // The target is char0↔char1 about 1px BIGGER than the rest, and 0.02 (~0.64px) is
                // close enough.
                const float FIRST_GLYPH_EXTRA_UPEM_FRACTION = 0.02f;
                if (i == 0) advanceDelta += upem * FIRST_GLYPH_EXTRA_UPEM_FRACTION;
                yAdvancesScaled.Add(advanceDelta * scale);
                halfInkWidthsScaled.Add(halfInkWidth * scale);
                yOffsetsScaled.Add(EffectiveYOffset(glyphs[i], runOrigin) * scale);
            }

            // Shared lib computes baseline + accumulated advance per row.
            // There is no baseY shift and no column-top buffer. First-glyph
            // spacing is carried entirely by the extra buffer added to yAdvancesScaled[0]
            // above; Draw and Measure apply it identically, so highlight / Index-Latin rows
            // track the glyphs with no drift.
            var rowCenters = CSharpTSFInput.Rendering.MongolianVertical.MongolianVerticalColumn
                .ComputeRowYCenters(yAdvancesScaled, baselineY);

            // Adjust each row center by glyph-specific YOffset + halfInkWidth correction (these
            // are per-glyph adjustments the lib doesn't know about; lib only does pure column
            // accumulation). The pattern is: lib gives us "row top + half advance"; we need
            // "row top + YOffset + halfInk".
            for (int i = 0; i < rowCenters.Count; i++)
            {
                float halfAdvance = yAdvancesScaled[i] * 0.5f;
                // Subtract lib's halfAdvance, add our halfInk + yOffset
                result.Add(rowCenters[i] - halfAdvance + halfInkWidthsScaled[i] + yOffsetsScaled[i]);
            }
            return result;
        }

        // uniformVerticalPitch (design units, default 0 = OFF): when > 0, EVERY glyph in
        // the run advances by this fixed pitch INSTEAD of its per-glyph ink width — used ONLY by the separated-form
        // (decomposed-ligature) render path, whose components are bare FinalGlyphs with ZERO metrics and whose
        // per-glyph ink widths DIFFER (→ uneven vertical gaps). A fixed pitch gives a UNIFORM small gap that
        // visually matches the natural single-glyph row feel. Natural fused / per-character runs pass pitch=0 and
        // use the ink-width layout. The first-glyph extra is suppressed under uniform pitch so the
        // separated-form stack is perfectly even (no widened first gap).
        public static void DrawShapedText(
            NativeMethods.ID2D1RenderTarget renderTarget,
            nint pFontFace,
            List<ManjuShaperCore.FinalGlyph> glyphs,
            float baselineX, float baselineY,
            float fontSize,
            nint pBrush,
            ushort upem = 1000,
            float uniformVerticalPitch = 0f,
            float uniformInkEdgeGapDesignUnits = 0f,
            int isolateGlyphIndex = -1)
        {
            if (glyphs == null || glyphs.Count == 0 || pFontFace == 0 || pBrush == 0)
            {
                return;
            }

            int count = glyphs.Count;
            
            ushort* pIndices = null;
            float* pAdvances = null;
            DWRITE_GLYPH_OFFSET* pOffsets = null;

            try
            {
                pIndices = (ushort*)NativeMemory.Alloc((nuint)(sizeof(ushort) * count));
                pAdvances = (float*)NativeMemory.Alloc((nuint)(sizeof(float) * count));
                pOffsets = (DWRITE_GLYPH_OFFSET*)NativeMemory.Alloc((nuint)(sizeof(DWRITE_GLYPH_OFFSET) * count));

                float scale = ComputeScale(fontSize, upem); // use the font's real UPEM

                // Vertical stacking accumulates per-glyph ink width, not the em-height advance.
                // Accumulating |YAdvance| takes the full em-height (1000 units),
                // which leaves the font's designed whitespace WITHIN each em-cell visible as gap between
                // adjacent glyphs. Here accumulatedY += inkWidth + smallBuffer, where inkWidth =
                // AdvanceWidth - LSB - RSB (glyph's horizontal ink extent in design units). After CW 90°
                // canvas rotation, horizontal ink extent becomes vertical stacking extent, so adjacent
                // glyphs' ink ranges abut, with a small safety buffer. The constraint is never to cross a
                // glyph outline boundary, so ink ranges must not overlap and only ink extent + buffer is
                // added. Buffer default = upem × 2% ≈ 20 units.
                // The ink fetch is skipped entirely under uniform pitch (the separated-form path does not
                // use it). The default path lays out by each item's OWN ink BOX (start + extent), not by
                // its origin plus an extent. Origin-plus-extent silently assumes every glyph's ink begins
                // at the same distance from its origin; it does not, and the leading bearing it drops is
                // what makes an item land in a neighbour's space.
                float[]? dfStart = null, dfExt = null;
                bool defaultBoxMode = uniformVerticalPitch <= 0f && uniformInkEdgeGapDesignUnits <= 0f
                                      && FetchGlyphInkBoxesDesignUnits(pFontFace, glyphs, upem, out dfStart, out dfExt);
                // The DRAW path must use the same effective origin the MEASURE path uses.
                // Normalising upright glyphs onto the run's vertical origin when MEASURING while the
                // draw reads glyphs[i].YOffset raw makes the two disagree by the 577-design-unit origin
                // gap (~16 DIP at 28pt), for exactly those upright characters. The measurement alone
                // can then look correct while the host still stacks Q on top of its neighbour.
                float runOriginDraw = RunVerticalOriginYOffset(glyphs);
                const float BUFFER_UPEM_FRACTION = 0.02f;
                float bufferDesignUnits = upem * BUFFER_UPEM_FRACTION;
                // per-glyph ink box (inkStart=LSB, inkExt=ink width) for the uniform
                // ink-edge-gap layout; advance below keeps consecutive INK edges a constant gap apart.
                float[]? ieStart = null, ieExt = null;
                bool inkEdgeMode = uniformInkEdgeGapDesignUnits > 0f && FetchGlyphInkBoxesDesignUnits(pFontFace, glyphs, upem, out ieStart, out ieExt);

                // First-character spacing.
                // accumulatedY starts at 0 (no column-top margin). The gap that would otherwise be too
                // CRAMPED is char0↔char1, and a margin above glyph0 does not touch it, so the extra
                // buffer goes on glyph0's ADVANCE (below): it pushes char1+ down and widens the first
                // inter-glyph gap. glyph0 itself is unaffected, because it is already drawn before its
                // advance is consumed.
                float accumulatedY = 0;
                for (int i = 0; i < count; i++)
                {
                    pIndices[i] = (ushort)glyphs[i].GlyphId;
                    pAdvances[i] = 0; // We drive positioning via offsets

                    // Pre-rotation offset layout for CW 90° render target rotation.
                    //
                    // The render target applies CW 90° rotation: (dx, dy) → screen (-dy, dx)
                    // We want screen positions: (XOffset*scale horizontal, (accY+YOff)*scale downward)
                    // Therefore pre-rotation: dx = (accY+YOff)*scale, dy = -XOffset*scale
                    // In DirectWrite: AdvanceOffset = dx, AscenderOffset = -dy
                    //
                    // Result: glyphs form a downward vertical column AFTER rotation.
                    if (_rotationMode != 0)
                    {
                        // Pre-rotation horizontal layout → CW 90° → vertical downward flow
                        pOffsets[i].AdvanceOffset = ComputePreRotationAdvanceOffset(accumulatedY, (int)EffectiveYOffset(glyphs[i], runOriginDraw), scale);
                        pOffsets[i].AscenderOffset = ComputePreRotationAscenderOffset(glyphs[i].XOffset, glyphs[i].HAdvance, scale);
                    }
                    else
                    {
                        // Direct vertical positioning (no rotation applied)
                        pOffsets[i].AdvanceOffset = ComputeHorizontalCenteringOffset(glyphs[i].XOffset, scale);
                        pOffsets[i].AscenderOffset = ComputeVerticalPositionOffset(accumulatedY, (int)EffectiveYOffset(glyphs[i], runOriginDraw), scale);
                    }

                    // Update accumulation for the next glyph.
                    float advanceDelta;
                    if (inkEdgeMode)
                    {
                        // advance so the NEXT glyph's ink TOP sits a constant gap below
                        // THIS glyph's ink BOTTOM: advance = gap + inkEnd_i - inkStart_{i+1}. Accounts for each
                        // glyph's bearing (LSB) → the VISUAL ink-edge gap is constant even though glyph sizes /
                        // bearings differ (an un-accounted bearing shows up as uneven pitch).
                        float inkEnd = ieStart![i] + ieExt![i];
                        float inkStartNext = (i + 1 < count) ? ieStart[i + 1] : 0f;
                        advanceDelta = uniformInkEdgeGapDesignUnits + inkEnd - inkStartNext;
                        if (advanceDelta < 0f) advanceDelta = uniformInkEdgeGapDesignUnits;
                    }
                    else if (uniformVerticalPitch > 0f)
                    {
                        // separated-form path: fixed per-glyph pitch for ALL glyphs, bypassing the
                        // ink-width branch entirely. Components have differing ink widths → ink-width spacing is
                        // uneven; a constant pitch gives a UNIFORM small gap. No
                        // first-glyph extra here — the separated-form stack must be perfectly even.
                        advanceDelta = uniformVerticalPitch;
                    }
                    else
                    {
                        // Ink-BOX layout: advance so the NEXT item's ink TOP sits one buffer below
                        // THIS item's ink BOTTOM, each measured on its own box. Fallback to full |YAdvance| if
                        // metrics are unavailable (fontFace null / API failure).
                        if (defaultBoxMode)
                        {
                            float inkEnd = dfStart![i] + dfExt![i];
                            float inkStartNext = (i + 1 < count) ? dfStart[i + 1] : 0f;
                            advanceDelta = bufferDesignUnits + inkEnd - inkStartNext;
                            if (advanceDelta < 0f) advanceDelta = bufferDesignUnits;
                        }
                        else
                        {
                            advanceDelta = GetVerticalAdvanceMagnitude(glyphs[i].YAdvance);
                        }
                        // Widen ONLY the first gap (char0↔char1). The extra does not touch the global
                        // buffer: it goes on the FIRST glyph's advance only, so char0 holds its place
                        // and char1+ shift down. Single tunable knob.
                        // Target: char0↔char1 about 1px BIGGER than the rest; 0.02 (~0.64px) is close enough.
                        const float FIRST_GLYPH_EXTRA_UPEM_FRACTION = 0.02f;
                        if (i == 0) advanceDelta += upem * FIRST_GLYPH_EXTRA_UPEM_FRACTION;
                    }
                    accumulatedY += advanceDelta;
                }

                // Per-item pixel bounds. The whole run's layout above is computed in full either way;
                // only the EMISSION is narrowed to one item, so the pixels that come back are the ones
                // that item contributes to the real composite.
                // Default -1 emits everything.
                bool isolate = isolateGlyphIndex >= 0 && isolateGlyphIndex < count;

				DWRITE_GLYPH_RUN glyphRun = new ()
				{
                    FontFace = pFontFace,
                    FontEmSize = fontSize,
                    GlyphCount = isolate ? 1u : (uint)count,
                    GlyphIndices = isolate ? pIndices + isolateGlyphIndex : pIndices,
                    GlyphAdvances = isolate ? pAdvances + isolateGlyphIndex : pAdvances,
                    GlyphOffsets = isolate ? pOffsets + isolateGlyphIndex : pOffsets,
                    IsSideways = 0, // the render target's -90deg rotation is used instead of IsSideways=1
                    BidiLevel = 0
                };

				D2D1_POINT_2F origin = new ()
				{ x = baselineX, y = baselineY };

                // Apply rotation based on _rotationMode
                D2D1_MATRIX_3X2_F savedTransform = default;
                bool hasRotation = _rotationMode != 0;
                if (hasRotation)
                {
                    renderTarget.GetTransform(out savedTransform);
                    float cx = baselineX, cy = baselineY;
                    D2D1_MATRIX_3X2_F rot = _rotationMode switch
                    {
                        1 => new() { m11 = 0, m12 = 1, m21 = -1, m22 = 0, dx = cx + cy, dy = cy - cx },   // CW 90
                        2 => new() { m11 = -1, m12 = 0, m21 = 0, m22 = -1, dx = 2*cx, dy = 2*cy },         // 180
                        3 => new() { m11 = 0, m12 = -1, m21 = 1, m22 = 0, dx = cx - cy, dy = cx + cy },    // CCW 90
                        _ => new() { m11 = 1, m12 = 0, m21 = 0, m22 = 1, dx = 0, dy = 0 }                  // identity
                    };
                    renderTarget.SetTransform(rot);
                }

                renderTarget.DrawGlyphRun(origin, glyphRun, pBrush, 0);

                if (hasRotation)
                {
                    renderTarget.SetTransform(savedTransform);
                }
            }
            finally
            {
                NativeMemory.Free(pIndices);
                NativeMemory.Free(pAdvances);
                NativeMemory.Free(pOffsets);
            }
        }

        private static float ComputeScale(float fontSize, ushort upem)
        {
            return fontSize / (float)upem;
        }

        private static float ComputeGlyphBlackBoxWidthDip(in DWRITE_GLYPH_METRICS metrics, float scale)
        {
            float widthDesignUnits = Math.Max(0f, metrics.AdvanceWidth - metrics.LeftSideBearing - metrics.RightSideBearing);
            return widthDesignUnits * scale;
        }

        private static float ComputeGlyphBlackBoxHeightDip(in DWRITE_GLYPH_METRICS metrics, float scale)
        {
            float heightDesignUnits = Math.Max(0f, metrics.AdvanceHeight - metrics.TopSideBearing - metrics.BottomSideBearing);
            return heightDesignUnits * scale;
        }

        private static unsafe int OpenPathGeometry(nint pathGeometry, out nint geometrySink)
        {
            geometrySink = 0;
            if (pathGeometry == 0)
            {
                return -1;
            }

            nint* vtable = *(nint**)pathGeometry;
            nint openPtr = vtable[17];
            fixed (nint* pSink = &geometrySink)
            {
                return ((delegate* unmanaged[Stdcall]<nint, nint*, int>)openPtr)(pathGeometry, pSink);
            }
        }

        private static unsafe int CloseGeometrySink(nint geometrySink)
        {
            if (geometrySink == 0)
            {
                return NativeMethods.S_OK;
            }

            nint* vtable = *(nint**)geometrySink;
            nint closePtr = vtable[9];
            return ((delegate* unmanaged[Stdcall]<nint, int>)closePtr)(geometrySink);
        }

        private static unsafe int GetGeometryBounds(nint geometry, out RECT_F bounds)
        {
            bounds = default;
            if (geometry == 0)
            {
                return -1;
            }

            nint* vtable = *(nint**)geometry;
            nint getBoundsPtr = vtable[4];
            fixed (RECT_F* pBounds = &bounds)
            {
                return ((delegate* unmanaged[Stdcall]<nint, nint, RECT_F*, int>)getBoundsPtr)(geometry, 0, pBounds);
            }
        }

        private static void ReleaseComObject(nint comPointer)
        {
            if (comPointer != 0)
            {
                Marshal.Release(comPointer);
            }
        }

        private static void TransformPoint(float x, float y, int rotationMode, out float screenX, out float screenY)
        {
            switch (rotationMode)
            {
                case 1:
                    screenX = -y;
                    screenY = x;
                    break;
                case 2:
                    screenX = -x;
                    screenY = -y;
                    break;
                case 3:
                    screenX = y;
                    screenY = -x;
                    break;
                default:
                    screenX = x;
                    screenY = y;
                    break;
            }
        }

        private static void ComputeTransformedBounds(in RECT_F bounds, int rotationMode, out float minX, out float maxX, out float minY, out float maxY)
        {
            TransformPoint(bounds.left, bounds.top, rotationMode, out float x1, out float y1);
            TransformPoint(bounds.right, bounds.top, rotationMode, out float x2, out float y2);
            TransformPoint(bounds.left, bounds.bottom, rotationMode, out float x3, out float y3);
            TransformPoint(bounds.right, bounds.bottom, rotationMode, out float x4, out float y4);

            minX = Math.Min(Math.Min(x1, x2), Math.Min(x3, x4));
            maxX = Math.Max(Math.Max(x1, x2), Math.Max(x3, x4));
            minY = Math.Min(Math.Min(y1, y2), Math.Min(y3, y4));
            maxY = Math.Max(Math.Max(y1, y2), Math.Max(y3, y4));
        }

        private static bool TryMeasureGlyphOutlineScreenBounds(
            ManjuShaperCore.FinalGlyph glyph,
            float accumulatedY,
            float fontSize,
            ushort upem,
            int rotationMode,
            NativeMethods.IDWriteFontFace? fontFace,
            out float outlineScreenWidth,
            out float outlineScreenHeight)
        {
            outlineScreenWidth = 0f;
            outlineScreenHeight = 0f;

            if (fontFace == null)
            {
                return false;
            }

            EnsureD2DFactory();
            if (_d2dFactory == null)
            {
                return false;
            }

            ushort normalizedUpem = upem == 0 ? (ushort)1 : upem;
            float scale = ComputeScale(fontSize, normalizedUpem);
            float advanceOffset;
            float ascenderOffset;

            if (rotationMode != 0)
            {
                advanceOffset = ComputePreRotationAdvanceOffset(accumulatedY, glyph.YOffset, scale);
                ascenderOffset = ComputePreRotationAscenderOffset(glyph.XOffset, glyph.HAdvance, scale);
            }
            else
            {
                advanceOffset = ComputeHorizontalCenteringOffset(glyph.XOffset, scale);
                ascenderOffset = ComputeVerticalPositionOffset(accumulatedY, glyph.YOffset, scale);
            }

            ushort* glyphIndices = stackalloc ushort[1];
            float* glyphAdvances = stackalloc float[1];
            DWRITE_GLYPH_OFFSET* glyphOffsets = stackalloc DWRITE_GLYPH_OFFSET[1];
            glyphIndices[0] = (ushort)glyph.GlyphId;
            glyphAdvances[0] = 0f;
            glyphOffsets[0].AdvanceOffset = advanceOffset;
            glyphOffsets[0].AscenderOffset = ascenderOffset;

            nint pathGeometry = 0;
            nint geometrySink = 0;

            try
            {
                int hr = _d2dFactory.CreatePathGeometry(out pathGeometry);
                if (hr != NativeMethods.S_OK || pathGeometry == 0)
                {
                    return false;
                }

                hr = OpenPathGeometry(pathGeometry, out geometrySink);
                if (hr != NativeMethods.S_OK || geometrySink == 0)
                {
                    return false;
                }

                if (fontFace == null)
                {
                    return false;
                }

                hr = fontFace.GetGlyphRunOutline(
                    fontSize,
                    glyphIndices,
                    glyphAdvances,
                    glyphOffsets,
                    1,
                    0,
                    0,
                    geometrySink);
                int closeHr = CloseGeometrySink(geometrySink);
                if (hr != NativeMethods.S_OK)
                {
                    return false;
                }

                if (closeHr != NativeMethods.S_OK)
                {
                    return false;
                }

                hr = GetGeometryBounds(pathGeometry, out RECT_F bounds);
                if (hr != NativeMethods.S_OK)
                {
                    return false;
                }

                ComputeTransformedBounds(bounds, rotationMode, out float minX, out float maxX, out float minY, out float maxY);
                outlineScreenWidth = Math.Max(0f, maxX - minX);
                outlineScreenHeight = Math.Max(0f, maxY - minY);
                return true;
            }
            finally
            {
                ReleaseComObject(geometrySink);
                ReleaseComObject(pathGeometry);
            }
        }

        internal static VerticalBodyWidthSourceSelection ResolveVerticalBodyWidthSourceSelection(
            IReadOnlyList<ManjuShaperCore.FinalGlyph>? glyphs,
            float fontSize,
            ushort upem,
            NativeMethods.IDWriteFontFace? fontFace = null)
        {
            int glyphCount = glyphs?.Count ?? 0;
            if (glyphCount == 0)
            {
                return new VerticalBodyWidthSourceSelection(
                    VerticalBodyWidthSourceTier.Unavailable,
                    0,
                    fontFaceAvailable: false,
                    outlineAttemptedGlyphCount: 0,
                    outlineExtractionSucceededGlyphCount: 0);
            }

            bool fontFaceAvailable = fontFace != null;
            if (!fontFaceAvailable)
            {
                return new VerticalBodyWidthSourceSelection(
                    VerticalBodyWidthSourceTier.Unavailable,
                    glyphCount,
                    fontFaceAvailable: false,
                    outlineAttemptedGlyphCount: 0,
                    outlineExtractionSucceededGlyphCount: 0);
            }

            ushort normalizedUpem = upem == 0 ? (ushort)1 : upem;
            int rotationMode = _rotationMode;
            float accumulatedY = 0f;
            int outlineExtractionSucceededGlyphCount = 0;
            bool allGlyphsSupportOutline = true;

            for (int i = 0; i < glyphCount; i++)
            {
                if (TryMeasureGlyphOutlineScreenBounds(glyphs![i], accumulatedY, fontSize, normalizedUpem, rotationMode, fontFace, out _, out _))
                {
                    outlineExtractionSucceededGlyphCount++;
                }
                else
                {
                    allGlyphsSupportOutline = false;
                }

                accumulatedY += GetVerticalAdvanceMagnitude(glyphs![i].YAdvance);
            }

            if (allGlyphsSupportOutline)
            {
                return new VerticalBodyWidthSourceSelection(
                    VerticalBodyWidthSourceTier.OutlineBounds,
                    glyphCount,
                    fontFaceAvailable: true,
                    outlineAttemptedGlyphCount: glyphCount,
                    outlineExtractionSucceededGlyphCount: outlineExtractionSucceededGlyphCount);
            }

            return new VerticalBodyWidthSourceSelection(
                VerticalBodyWidthSourceTier.Unavailable,
                glyphCount,
                fontFaceAvailable: true,
                outlineAttemptedGlyphCount: glyphCount,
                outlineExtractionSucceededGlyphCount: outlineExtractionSucceededGlyphCount);
        }

        private static float ComputeScreenHorizontalGlyphWidth(
            ManjuShaperCore.FinalGlyph glyph,
            float accumulatedY,
            float fontSize,
            ushort upem,
            int rotationMode,
            NativeMethods.IDWriteFontFace? fontFace,
            VerticalBodyWidthSourceTier sourceTier,
            out float outlineScreenWidthDip,
            out float outlineScreenHeightDip,
            out bool usedOutlineBounds)
        {
            outlineScreenWidthDip = 0f;
            outlineScreenHeightDip = 0f;
            usedOutlineBounds = false;

            switch (sourceTier)
            {
                case VerticalBodyWidthSourceTier.OutlineBounds:
                    if (TryMeasureGlyphOutlineScreenBounds(glyph, accumulatedY, fontSize, upem, rotationMode, fontFace, out outlineScreenWidthDip, out outlineScreenHeightDip))
                    {
                        usedOutlineBounds = true;
                        return outlineScreenWidthDip;
                    }
                    return 0f;
            }

            return 0f;
        }

        private static float ComputeScreenCenterXFromOffsets(float advanceOffset, float ascenderOffset, int rotationMode)
        {
            float preRotationX = advanceOffset;
            float preRotationY = -ascenderOffset;

            return rotationMode switch
            {
                1 => -preRotationY,
                2 => -preRotationX,
                3 => preRotationY,
                _ => preRotationX
            };
        }

        private static float GetVerticalAdvanceMagnitude(int yAdvance)
        {
            return Math.Abs(yAdvance);
        }

        // Batch-fetch per-glyph ink-width in font design units.
        // inkWidth = AdvanceWidth - LeftSideBearing - RightSideBearing (horizontal extent of the painted ink,
        // excluding the whitespace bearings the designer placed inside the em cell). In our vertical rendering
        // pipeline, glyphs are drawn at IsSideways=0 and the canvas is CW 90° rotated, so horizontal ink
        // extent becomes the vertical stacking extent. Using this instead of full AdvanceHeight (= em height)
        // lets adjacent glyph ink ranges abut tightly without crossing boundaries.
        // Returns null on any failure (caller falls back to em-height |YAdvance|).
        /// <summary> Is this glyph laid out UPRIGHT rather than rotated into the vertical run?
        ///
        /// Every measurement below reads the glyph's HORIZONTAL ink box and uses it as the VERTICAL
        /// advance. That is not a shortcut — this font draws Mongolian/Manchu glyphs rotated 90° CW, so
        /// their horizontal extent IS the space they occupy down the column. Latin letters, digits and
        /// punctuation are NOT rotated: their horizontal extent is their width, which has nothing to do
        /// with how much column they need.
        ///
        /// The font itself says which is which. Measured on NotoSansMongolian, a Manchu glyph carries
        /// YAdvance = -1000 while 'q', '.' and '1' all carry YAdvance = 0 — no vertical advance at all,
        /// because they were never meant to be stacked. Feeding those through the rotated path misplaces
        /// them: in "anqa", 'q' lands ABOVE the 'n' before it. A negative advance is not tight spacing,
        /// it is one character drawn on top of another.
        ///
        /// Deliberately keyed on the font's own answer, not on a Unicode-block check: a script the font
        /// does rotate would then be handled correctly without this code knowing the script exists.</summary>
        public static bool IsUprightGlyph(ManjuShaperCore.FinalGlyph glyph) => glyph.YAdvance == 0;

        /// <summary> The vertical-origin offset the ROTATED glyphs of this run are positioned on,
        /// or NaN if the run has none.
        ///
        /// Sizing the upright glyphs correctly does not place them on its own. The shaper also hands every glyph a
        /// YOffset, and the two kinds do not share an origin: measured on this font, Manchu glyphs all carry
        /// -880 while 'q', '.' and '1' carry -1457. The Manchu value is constant across the run, so it just
        /// shifts the whole column; the upright value differs by 577 design units, which lands as a 16 DIP
        /// RELATIVE displacement of that one character, enough on its own to put '.' on top of the
        /// letter before it.
        ///
        /// That 577 is not positioning information. It is the gap between the origin the font defines for a
        /// rotated glyph and the one the shaper synthesises for a glyph that has no vertical metrics at all.
        /// A column has ONE origin convention, so upright glyphs are normalised onto the run's.</summary>
        private static float RunVerticalOriginYOffset(List<ManjuShaperCore.FinalGlyph> glyphs)
        {
            if (glyphs != null)
                for (int i = 0; i < glyphs.Count; i++)
                    if (!IsUprightGlyph(glyphs[i])) return glyphs[i].YOffset;
            return float.NaN;   // all-upright run: it is already internally consistent, leave it alone
        }

        private static float EffectiveYOffset(ManjuShaperCore.FinalGlyph glyph, float runOrigin)
            => (IsUprightGlyph(glyph) && !float.IsNaN(runOrigin)) ? runOrigin : glyph.YOffset;


        /// <summary> The vertical ink extent is not a measurement of anything here.
        ///
        /// Measuring upright glyphs on the font's VERTICAL metric axis (AdvanceHeight, TopSideBearing,
        /// BottomSideBearing) assumes a Latin letter is not rotated. It is. The canvas carries
        /// ONE CW-90 rotation for the whole run (see _rotationMode), applied to every glyph in the
        /// glyph run alike, so the column footprint of ANY glyph is its PRE-ROTATION HORIZONTAL box.
        ///
        /// Worse than a wrong axis: for these glyphs the vertical axis is not defined at all. Measured on
        /// NotoSansMongolian for 'q' (gid 1849): AdvanceHeight = 0, TopSideBearing = 911,
        /// BottomSideBearing = -1697, which is DirectWrite's synthesis for a glyph with no vertical
        /// metrics. The "extent" that comes out of it, 786, is an artefact; the real column box is
        /// (lsb 55, width 475). Using 911 as the ink START then places q's ink 856 design units
        /// (27.4 DIP at 32px) off its own row, which is what makes Q intrude into the cell the
        /// neighbouring Manchu character occupies.</summary>
        // Per-glyph ink box in the vertical STACKING direction (post-90°CW rotation, where
        // the glyph's horizontal extent maps to vertical): inkStart = LeftSideBearing (where ink begins from the
        // origin), inkExt = AdvanceWidth - LSB - RSB (ink height). Design units. Lets the layout keep the VISUAL
        // gap between consecutive glyphs' INK constant: an un-accounted LSB shows up as uneven pitch.
        private static unsafe bool FetchGlyphInkBoxesDesignUnits(nint pFontFace, List<ManjuShaperCore.FinalGlyph> glyphs, ushort upem, out float[] inkStart, out float[] inkExt)
        {
            int count = glyphs?.Count ?? 0;
            inkStart = new float[count];
            inkExt = new float[count];
            if (pFontFace == 0 || count == 0) return false;
            try
            {
                ushort* idx = stackalloc ushort[count];
                for (int i = 0; i < count; i++) idx[i] = (ushort)glyphs[i].GlyphId;
                DWRITE_GLYPH_METRICS* m = stackalloc DWRITE_GLYPH_METRICS[count];
                nint* vtable = *(nint**)pFontFace;
                int hr = ((delegate* unmanaged[Stdcall]<nint, ushort*, uint, void*, int, int>)vtable[10])(
                    pFontFace, idx, (uint)count, m, 0 /* isSideways */);
                if (hr != 0) return false;
                for (int i = 0; i < count; i++)
                {
                    // ONE rule for every glyph: the ink starts at the left side bearing and is the
                    // advance minus both side bearings. Reading TopSideBearing as the ink START of an
                    // upright glyph would give 911 for 'q' against a real bearing of 55: 27.4 DIP,
                    // enough to put Q on its neighbour.
                    int advance = (int)m[i].AdvanceWidth;
                    int lsb = m[i].LeftSideBearing;
                    int rsb = m[i].RightSideBearing;
                    int w = advance - lsb - rsb;
                    if (w <= 0) { w = advance > 0 ? advance : upem; lsb = 0; }
                    inkStart[i] = lsb;
                    inkExt[i] = w;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // Total INK box of a glyph group laid out with the uniform ink-edge gap:
        // returns (topOffsetDip, heightDip) — the group's ink top relative to the draw baseline, and its ink
        // height, in DIP. Lets a caller (the dictionary's per-character column) stack CELLS with a CONSTANT ink-edge gap between
        // cells: draw at baseline = cursorTop - topOffsetDip (with the same uniformInkEdgeGapDesignUnits), then
        // advance cursorTop += heightDip + gapDip. Mirrors MeasureShapedTextRowYCenters' ink-edge accumulation.
        public static unsafe (float topOffsetDip, float heightDip) MeasureGlyphsInkBoxDip(
            nint pFontFace, List<ManjuShaperCore.FinalGlyph> glyphs, float fontSize, ushort upem, float uniformInkEdgeGapDesignUnits)
        {
            if (pFontFace == 0 || glyphs == null || glyphs.Count == 0) return (0f, 0f);
            if (!FetchGlyphInkBoxesDesignUnits(pFontFace, glyphs, upem, out var iS, out var iE)) return (0f, 0f);
            float scale = ComputeScale(fontSize, upem);
            float accDU = 0f, top = float.MaxValue, bot = float.MinValue;
            for (int i = 0; i < glyphs.Count; i++)
            {
                float t = accDU + glyphs[i].YOffset + iS[i];
                float b = t + iE[i];
                if (t < top) top = t;
                if (b > bot) bot = b;
                float inkEnd = iS[i] + iE[i];
                float inkStartNext = (i + 1 < glyphs.Count) ? iS[i + 1] : 0f;
                float adv = (uniformInkEdgeGapDesignUnits > 0f ? uniformInkEdgeGapDesignUnits : 0f) + inkEnd - inkStartNext;
                accDU += adv < 0f ? System.Math.Max(0f, uniformInkEdgeGapDesignUnits) : adv;
            }
            if (top > bot) return (0f, 0f);
            return (top * scale, (bot - top) * scale);
        }

        internal static VerticalBodyLayoutGlyphMetrics MeasureVerticalBodyLayoutGlyph(ManjuShaperCore.FinalGlyph glyph, float accumulatedY, float fontSize, ushort upem, NativeMethods.IDWriteFontFace? fontFace = null)
        {
            var singleGlyph = new[] { glyph };
            VerticalBodyWidthSourceSelection sourceSelection = ResolveVerticalBodyWidthSourceSelection(singleGlyph, fontSize, upem, fontFace);
            return MeasureVerticalBodyLayoutGlyph(glyph, accumulatedY, fontSize, upem, fontFace, sourceSelection.SourceTier);
        }

        internal static VerticalBodyLayoutGlyphMetrics MeasureVerticalBodyLayoutGlyph(
            ManjuShaperCore.FinalGlyph glyph,
            float accumulatedY,
            float fontSize,
            ushort upem,
            NativeMethods.IDWriteFontFace? fontFace,
            VerticalBodyWidthSourceTier sourceTier)
        {
            ushort normalizedUpem = upem == 0 ? (ushort)1 : upem;
            float scale = ComputeScale(fontSize, normalizedUpem);
            int rotationMode = _rotationMode;

            float advanceOffset;
            float ascenderOffset;
            if (rotationMode != 0)
            {
                advanceOffset = ComputePreRotationAdvanceOffset(accumulatedY, glyph.YOffset, scale);
                ascenderOffset = ComputePreRotationAscenderOffset(glyph.XOffset, glyph.HAdvance, scale);
            }
            else
            {
                advanceOffset = ComputeHorizontalCenteringOffset(glyph.XOffset, scale);
                ascenderOffset = ComputeVerticalPositionOffset(accumulatedY, glyph.YOffset, scale);
            }

            float screenCenterX = ComputeScreenCenterXFromOffsets(advanceOffset, ascenderOffset, rotationMode);
            float measuredGlyphWidth = ComputeScreenHorizontalGlyphWidth(
                glyph,
                accumulatedY,
                fontSize,
                normalizedUpem,
                rotationMode,
                fontFace,
                sourceTier,
                out float outlineScreenWidth,
                out float outlineScreenHeight,
                out bool usedOutlineBounds);
            float halfWidth = measuredGlyphWidth / 2f;

            return new VerticalBodyLayoutGlyphMetrics(
                rotationMode,
                scale,
                accumulatedY * scale,
                advanceOffset,
                ascenderOffset,
                screenCenterX,
                measuredGlyphWidth,
                outlineScreenWidth,
                outlineScreenHeight,
                usedOutlineBounds,
                screenCenterX - halfWidth,
                screenCenterX + halfWidth);
        }

        internal static VerticalBodyLayoutMetrics MeasureVerticalBodyLayout(IReadOnlyList<ManjuShaperCore.FinalGlyph>? glyphs, float fontSize, ushort upem, NativeMethods.IDWriteFontFace? fontFace = null)
        {
            ushort normalizedUpem = upem == 0 ? (ushort)1 : upem;
            float scale = ComputeScale(fontSize, normalizedUpem);

            if (glyphs == null || glyphs.Count == 0)
            {
                return new VerticalBodyLayoutMetrics(scale, 0f, 0f, 0f, 0f);
            }

            VerticalBodyWidthSourceSelection sourceSelection = ResolveVerticalBodyWidthSourceSelection(glyphs, fontSize, normalizedUpem, fontFace);
            float totalAdvanceY = 0f;
            for (int i = 0; i < glyphs.Count; i++)
            {
                totalAdvanceY += GetVerticalAdvanceMagnitude(glyphs[i].YAdvance);
            }

            if (sourceSelection.SourceTier != VerticalBodyWidthSourceTier.OutlineBounds)
            {
                return new VerticalBodyLayoutMetrics(scale, 0f, 0f, totalAdvanceY * scale, 0f);
            }

            float minLeft = float.PositiveInfinity;
            float maxRight = float.NegativeInfinity;
            float accumulatedY = 0f;

            for (int i = 0; i < glyphs.Count; i++)
            {
                VerticalBodyLayoutGlyphMetrics glyphMetrics = MeasureVerticalBodyLayoutGlyph(glyphs[i], accumulatedY, fontSize, normalizedUpem, fontFace, sourceSelection.SourceTier);
                if (glyphMetrics.Left < minLeft)
                {
                    minLeft = glyphMetrics.Left;
                }

                if (glyphMetrics.Right > maxRight)
                {
                    maxRight = glyphMetrics.Right;
                }

                accumulatedY += GetVerticalAdvanceMagnitude(glyphs[i].YAdvance);
            }

            if (float.IsNaN(minLeft) || float.IsInfinity(minLeft) || float.IsNaN(maxRight) || float.IsInfinity(maxRight))
            {
                return new VerticalBodyLayoutMetrics(scale, 0f, 0f, totalAdvanceY * scale, 0f);
            }

            return new VerticalBodyLayoutMetrics(scale, minLeft, maxRight, totalAdvanceY * scale, Math.Max(0f, maxRight - minLeft));
        }


        // Vertical positioning offset helpers (no rotation needed)

        private static float ComputeVerticalPositionOffset(float accumulatedY, int yOffset, float scale)
        {
            // Negative ascenderOffset = move DOWN from baseline for vertical column
            return -(accumulatedY + yOffset) * scale;
        }

        private static float ComputeHorizontalCenteringOffset(int xOffset, float scale)
        {
            return xOffset * scale;
        }

        // Pre-rotation offset helpers for CW 90° rotation
        
        /// <summary>
        /// Compute AdvanceOffset for pre-rotation layout.
        /// After CW 90° rotation: (AdvOff, -AscOff) → screen (-AscOff, AdvOff)
        /// We want vertical downward flow, so AdvOff encodes the Y position.
        /// </summary>
        private static float ComputePreRotationAdvanceOffset(float accumulatedY, int yOffset, float scale)
        {
            return (accumulatedY + yOffset) * scale;
        }

        /// <summary>
        /// Compute AscenderOffset for pre-rotation layout.
        /// After CW 90° rotation: -AscOff becomes screen X position.
        /// We want XOffset centering, so AscOff = XOffset * scale.
        /// </summary>
        /// <summary>
        /// Computes pre-rotation ascender offset for vertical text rendered via canvas rotation.
        ///
        /// In HarfBuzz's vertical layout, XOffset = -(hAdvance/2) centers each glyph's horizontal
        /// origin so its visual center lands on the vertical baseline. This per-glyph centering
        /// works for standard vertical renderers that draw from the horizontal origin.
        ///
        /// But in our CW 90° rotation model, AscenderOffset maps to screen-X after rotation.
        /// Different XOffset values (from different hAdvance widths per positional form) produce
        /// different screen-X positions, misaligning the vertical stem.
        ///
        /// So the centering component (-(hAdvance/2)) is subtracted, keeping only GPOS adjustments.
        /// Result: (XOffset + hAdvance/2) * scale = GPOS_delta * scale (0 for unmodified glyphs).
        /// </summary>
        private static float ComputePreRotationAscenderOffset(int xOffset, int hAdvance, float scale)
        {
            // Remove the per-glyph -(hAdvance/2) centering; keep only GPOS position adjustments.
            // For base glyphs without GPOS: (-(hAdv/2) + hAdv/2) = 0 → same baseline for all.
            // For GPOS-adjusted glyphs: preserves the cross-direction adjustment.
            return (xOffset + hAdvance / 2) * scale;
        }

        /// <summary>
        /// Draw text horizontally (e.g. for metadata rows).
        /// Expects glyphs to be shaped with isVertical=false.
        /// </summary>
        public static void DrawHorizontalText(
            NativeMethods.ID2D1RenderTarget renderTarget,
            nint pFontFace,
            List<ManjuShaperCore.FinalGlyph> glyphs,
            float x, float y,
            float fontSize,
            nint pBrush,
            ushort upem = 1000)
        {
            if (glyphs == null || glyphs.Count == 0 || pFontFace == 0 || pBrush == 0)
            {
                return;
            }

            int count = glyphs.Count;
            
            ushort* pIndices = null;
            float* pAdvances = null;
            DWRITE_GLYPH_OFFSET* pOffsets = null;

            try
            {
                pIndices = (ushort*)NativeMemory.Alloc((nuint)(sizeof(ushort) * count));
                pAdvances = (float*)NativeMemory.Alloc((nuint)(sizeof(float) * count));
                pOffsets = (DWRITE_GLYPH_OFFSET*)NativeMemory.Alloc((nuint)(sizeof(DWRITE_GLYPH_OFFSET) * count));

                float scale = ComputeScale(fontSize, upem);

                for (int i = 0; i < count; i++)
                {
                    pIndices[i] = (ushort)glyphs[i].GlyphId;
                    
                    // Horizontal layout uses XAdvance
                    pAdvances[i] = glyphs[i].XAdvance * scale;

                    // Horizontal positioning offsets
                    // AdvanceOffset (+Right) = XOffset
                    // AscenderOffset (+Up) = YOffset (Assuming shaper Y is Up)
                    pOffsets[i].AdvanceOffset = glyphs[i].XOffset * scale;
                    pOffsets[i].AscenderOffset = glyphs[i].YOffset * scale; 
                }

                DWRITE_GLYPH_RUN glyphRun = new ()
                {
                    FontFace = pFontFace,
                    FontEmSize = fontSize,
                    GlyphCount = (uint)count,
                    GlyphIndices = pIndices,
                    GlyphAdvances = pAdvances,
                    GlyphOffsets = pOffsets,
                    IsSideways = 0,
                    BidiLevel = 0
                };

                D2D1_POINT_2F origin = new () { x = x, y = y };

                // No rotation for horizontal text
                renderTarget.DrawGlyphRun(origin, glyphRun, pBrush, 0);
            }
            finally
            {
                NativeMemory.Free(pIndices);
                NativeMemory.Free(pAdvances);
                NativeMemory.Free(pOffsets);
            }
        }

        // --- System font text rendering (for metadata with CJK/Latin fallback) ---

        private static volatile nint _systemTextFormat;
        private static volatile float _systemTextFormatSize;
        // Separate BOLD cache slot so bold (prefix Latin/digit/apostrophe) and normal
        // (card metadata) can alternate within one paint without thrashing a single-slot cache.
        private static volatile nint _systemTextFormatBold;
        private static volatile float _systemTextFormatBoldSize;

        /// <summary>
        /// Draw text using DirectWrite's DrawText with a system font (supports CJK/Latin fallback).
        /// Used for metadata rows where Mongolian-only font would produce tofu.
        /// </summary>
        public static unsafe void DrawTextWithSystemFont(
            NativeMethods.ID2D1RenderTarget renderTarget,
            string text,
            float x, float y,
            float width, float height,
            float fontSize,
            nint pBrush,
            bool centerHorizontally = false,
            bool bold = false)
        {
            if (string.IsNullOrEmpty(text) || pBrush == 0) return;

            EnsureFactory();
            if (_factory == null) return;

            nint textFormat = GetOrCreateSystemTextFormat(fontSize, bold);
            if (textFormat == 0) return;

            // Horizontal alignment.
            // Set it per-call on the shared format via IDWriteTextFormat::SetTextAlignment — vtable
            // slot 3 (IUnknown QI/AddRef/Release = 0,1,2; SetTextAlignment is the first IDWriteTextFormat
            // method). CENTER=2 when requested, else LEADING=0. Paint is single-threaded/sequential so
            // setting alignment right before each DrawText is safe. Default is LEADING;
            // only the vertical Latin annotation opts into CENTER (Western convention
            // for stacked Latin: centering evens out the i-vs-m width variance).
            {
                nint vtbl = Marshal.ReadIntPtr(textFormat);
                nint pSetAlign = Marshal.ReadIntPtr(vtbl, 3 * IntPtr.Size);
                ((delegate* unmanaged[Stdcall]<nint, int, int>)pSetAlign)(textFormat, centerHorizontally ? 2 : 0);
            }

            try
            {
                RECT_F layoutRect = new RECT_F
                {
                    left = x,
                    top = y,
                    right = x + width,
                    bottom = y + height
                };

                fixed (char* pText = text)
                {
                    renderTarget.DrawText(
                        (IntPtr)pText,
                        (uint)text.Length,
                        textFormat,
                        (IntPtr)(&layoutRect),
                        pBrush,
                        centerHorizontally ? 0 : 0, // D2D1_DRAW_TEXT_OPTIONS_NONE
                        0 // DWRITE_MEASURING_MODE_NATURAL
                    );
                }
            }
            catch (Exception)
            {
            }
        }

        // THE FAMILY LIST THE PREVIEW'S TEXT IS DRAWN WITH: ONE COPY.
        //
        // The list sits outside GetOrCreateSystemTextFormat so the shape tables (connector /
        // separator / apostrophe) can be checked against the SAME list the drawing actually uses. A
        // second copy of { "Segoe UI", … } would drift from this one, and a check against a drifted
        // copy reports on a font list nothing is drawn with.
        //
        // WHAT THIS LIST DOES AND DOES NOT DO. CreateTextFormat does not verify that a family exists, so
        // in practice the loop below takes the first entry and the other two are not a fallback chain at
        // all. A code point Segoe UI lacks is resolved at DRAW time by DirectWrite's own font fallback,
        // which picks some other font with a weight and a baseline this project does not control. This
        // list does not change that; it is only read.
        internal static readonly string[] SystemTextFontFamilies = { "Segoe UI", "Microsoft YaHei", "Arial" };


        private static unsafe nint GetOrCreateSystemTextFormat(float fontSize, bool bold = false)
        {
            // two-slot cache: bold vs normal kept separately so alternating draws don't thrash.
            if (bold)
            {
                if (_systemTextFormatBold != 0 && Math.Abs(_systemTextFormatBoldSize - fontSize) < 0.01f)
                    return _systemTextFormatBold;
                if (_systemTextFormatBold != 0) { Marshal.Release(_systemTextFormatBold); _systemTextFormatBold = 0; }
            }
            else
            {
                if (_systemTextFormat != 0 && Math.Abs(_systemTextFormatSize - fontSize) < 0.01f)
                    return _systemTextFormat;
                if (_systemTextFormat != 0) { Marshal.Release(_systemTextFormat); _systemTextFormat = 0; }
            }

            nint newFormat = 0;

            foreach (var family in SystemTextFontFamilies)
            {
                fixed (char* pFamily = family)
                fixed (char* pLocale = "")
                {
                    int hr = _factory!.CreateTextFormat(
                        (nint)pFamily,
                        0, // system font collection
                        (uint)(bold ? 700 : 400), // DWRITE_FONT_WEIGHT_BOLD : NORMAL
                        0,   // DWRITE_FONT_STYLE_NORMAL
                        5,   // DWRITE_FONT_STRETCH_NORMAL
                        fontSize,
                        (nint)pLocale,
                        out newFormat);
                    if (hr == 0 && newFormat != 0)
                        break;
                }
            }

            if (newFormat != 0)
            {
                if (bold) { _systemTextFormatBold = newFormat; _systemTextFormatBoldSize = fontSize; }
                else      { _systemTextFormat = newFormat;     _systemTextFormatSize = fontSize; }
            }
            return newFormat;
        }

        // ===================== OS-native vertical rendering =====================

        /// <summary>Read function pointer at vtable <paramref name="slot"/> of a raw COM object pointer.</summary>
        private static unsafe nint VtblFn(nint pObj, int slot) => (*(nint**)pObj)[slot];

        /// <summary>QueryInterface (vtable slot 0). Returns the new interface pointer (+1 ref) or 0.</summary>
        private static unsafe nint ComQI(nint pUnk, Guid iid)
        {
            if (pUnk == 0) return 0;
            nint ppv = 0;
            int hr = ((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)VtblFn(pUnk, 0))(pUnk, &iid, &ppv);
            return hr == 0 ? ppv : 0;
        }

        /// <summary>
        /// Builds (once) a private IDWriteFontCollection containing the bundled Noto Sans Mongolian so
        /// CreateTextFormat can reference it by family name. Returns the collection pointer (cached) or 0.
        /// Slots (from dwrite_3.h, base before IDWriteFactory3 = 3 IUnknown + 21 F + 2 F1 + 5 F2 = 31):
        ///   IDWriteFactory3::CreateFontFaceReference(fontFile)         = 31+2 = 33
        ///   IDWriteFactory3::CreateFontSetBuilder                      = 31+5 = 36
        ///   IDWriteFactory3::CreateFontCollectionFromFontSet           = 31+6 = 37
        ///   IDWriteFontSetBuilder::AddFontFaceReference(simple)        = 3
        ///   IDWriteFontSetBuilder::CreateFontSet                       = 6
        /// </summary>
        private static unsafe nint EnsurePrivateFontCollection(string fontPath)
        {
            if (_privateFontCollection != 0) return _privateFontCollection;
            if (_privateFontCollectionTried) return 0;
            _privateFontCollectionTried = true;

            EnsureFactory();
            if (_factory == null || string.IsNullOrEmpty(fontPath)) return 0;

            nint pFactory = 0, pFactory3 = 0, pFontFile = 0, pBuilder = 0, pFontSet = 0, pFilePath = 0;
            NativeMethods.IDWriteFontFile? fontFile = null;
            try
            {
                pFactory = (nint)ComInterfaceMarshaller<NativeMethods.IDWriteFactory>.ConvertToUnmanaged(_factory);
                if (pFactory == 0) return 0;
                pFactory3 = ComQI(pFactory, IID_IDWriteFactory3);
                if (pFactory3 == 0) { return 0; }

                // AVOID IDWriteFactory3::CreateFontFaceReference: BOTH overloads crash (access violation)
                // in this build. Build the set from the font FILE directly instead: CreateFontFileReference
                // (the call CreateFontFaceFromFile uses) → IDWriteFontSetBuilder1::AddFontFile.
                pFilePath = Marshal.StringToCoTaskMemUni(fontPath);
                int hr = _factory.CreateFontFileReference(pFilePath, 0, out fontFile);
                if (hr != 0 || fontFile == null) { return 0; }
                pFontFile = (nint)ComInterfaceMarshaller<NativeMethods.IDWriteFontFile>.ConvertToUnmanaged(fontFile);

                hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)VtblFn(pFactory3, 36))(pFactory3, &pBuilder);
                if (hr != 0 || pBuilder == 0) { return 0; }

                nint pBuilder1 = ComQI(pBuilder, IID_IDWriteFontSetBuilder1);
                if (pBuilder1 == 0) { return 0; }

                hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)VtblFn(pBuilder1, 7))(pBuilder1, pFontFile);
                Marshal.Release(pBuilder1);
                if (hr != 0) { return 0; }

                hr = ((delegate* unmanaged[Stdcall]<nint, nint*, int>)VtblFn(pBuilder, 6))(pBuilder, &pFontSet);
                if (hr != 0 || pFontSet == 0) { return 0; }

                nint pColl = 0;
                hr = ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)VtblFn(pFactory3, 37))(pFactory3, pFontSet, &pColl);
                if (hr != 0 || pColl == 0) { return 0; }

                _privateFontCollection = pColl;
                return pColl;
            }
            catch (Exception) { return 0; }
            finally
            {
                if (pBuilder != 0) Marshal.Release(pBuilder);
                if (pFontSet != 0) Marshal.Release(pFontSet);
                if (pFontFile != 0) Marshal.Release(pFontFile);
                if (pFactory3 != 0) Marshal.Release(pFactory3);
                if (pFactory != 0) Marshal.Release(pFactory);
                if (pFilePath != 0) Marshal.FreeCoTaskMem(pFilePath);
            }
        }

        /// <summary>
        /// Creates (cached per size) a vertical IDWriteTextFormat over the private collection.
        /// IDWriteTextFormat slots (from dwrite.h): SetReadingDirection=6, SetParagraphAlignment=4.
        /// Vertical = reading TOP_TO_BOTTOM(2) + flow LEFT_TO_RIGHT(2) (perpendicular ⇒ valid). Returns 0
        /// (→ caller falls back to the shaper) if vertical reading direction is unsupported.
        /// </summary>
        private static unsafe nint GetOrCreateVerticalTextFormat(float fontSize, nint collection)
        {
            if (_verticalTextFormat != 0 && Math.Abs(_verticalTextFormatSize - fontSize) < 0.01f)
                return _verticalTextFormat;
            if (_verticalTextFormat != 0) { Marshal.Release(_verticalTextFormat); _verticalTextFormat = 0; }

            EnsureFactory();
            if (_factory == null) return 0;

            nint fmt = 0;
            fixed (char* pFamily = MONGOLIAN_FAMILY)
            fixed (char* pLocale = "")
            {
                int hr = _factory.CreateTextFormat((nint)pFamily, collection, 400, 0, 5, fontSize, (nint)pLocale, out fmt);
                if (hr != 0 || fmt == 0) { return 0; }
            }

            int hrR = ((delegate* unmanaged[Stdcall]<nint, int, int>)VtblFn(fmt, 6))(fmt, 2); // SetReadingDirection TOP_TO_BOTTOM
            int hrF = ((delegate* unmanaged[Stdcall]<nint, int, int>)VtblFn(fmt, 7))(fmt, 2); // SetFlowDirection LEFT_TO_RIGHT
            if (hrR != 0 || hrF != 0)
            {
                Marshal.Release(fmt);
                return 0;
            }
            // No-wrap so a long word clips at the column bottom instead of spilling into a 2nd vertical
            // line to the side (flow is LEFT_TO_RIGHT). SetWordWrapping = slot 5, NO_WRAP = 1.
            ((delegate* unmanaged[Stdcall]<nint, int, int>)VtblFn(fmt, 5))(fmt, 1);
            // Center the column horizontally within the layout rect (perpendicular-to-reading axis).
            ((delegate* unmanaged[Stdcall]<nint, int, int>)VtblFn(fmt, 4))(fmt, 2); // SetParagraphAlignment CENTER

            _verticalTextFormat = fmt;
            _verticalTextFormatSize = fontSize;
            return fmt;
        }

        /// <summary>
        /// Used by the dictionary's joined column. The font collection is built via
        /// IDWriteFontSetBuilder1::AddFontFile, because IDWriteFactory3::CreateFontFaceReference crashes
        /// (access violation) in this build.
        ///
        /// Draws <paramref name="text"/> as OS-native vertical Manchu (the bundled font, DirectWrite layout) inside
        /// the rect (x,y,width,height). Returns true on success; false (collection/format unavailable) tells
        /// the caller to fall back to the custom shaper path. No catastrophic failure: identity transform is
        /// saved/restored, and a failed setup never enters the D2D draw batch.
        /// </summary>
        public static unsafe bool DrawNativeVerticalText(
            NativeMethods.ID2D1RenderTarget renderTarget,
            string fontPath, string text,
            float x, float y, float width, float height,
            float fontSize, nint pBrush)
        {
            if (renderTarget == null || string.IsNullOrEmpty(text) || pBrush == 0 || string.IsNullOrEmpty(fontPath))
                return false;

            nint coll = EnsurePrivateFontCollection(fontPath);
            if (coll == 0) return false;
            nint fmt = GetOrCreateVerticalTextFormat(fontSize, coll);
            if (fmt == 0) return false;

            renderTarget.GetTransform(out D2D1_MATRIX_3X2_F saved);
            var ident = new D2D1_MATRIX_3X2_F { m11 = 1, m12 = 0, m21 = 0, m22 = 1, dx = 0, dy = 0 };
            try
            {
                renderTarget.SetTransform(ident);
                var layoutRect = new RECT_F { left = x, top = y, right = x + width, bottom = y + height };
                fixed (char* pText = text)
                {
                    // options = D2D1_DRAW_TEXT_OPTIONS_CLIP (2): keep the column within its rect.
                    renderTarget.DrawText((nint)pText, (uint)text.Length, fmt, (nint)(&layoutRect), pBrush, 2, 0);
                }
                return true;
            }
            catch (Exception) { return false; }
            finally { renderTarget.SetTransform(saved); }
        }

    }
}

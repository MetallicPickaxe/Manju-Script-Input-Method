using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CSharpTSFInput
{
    /// <summary>
    /// Read-only EditSession to call GetTextExt with a valid edit cookie.
    /// Modeled after Weasel's CGetTextExtentEditSession.
    /// OnLayoutChange requests this session; DoEditSession calls GetTextExt and
    /// updates the candidate window position via TextService.
    /// </summary>
    [GeneratedComClass]
    public partial class GetTextExtEditSession : NativeMethods.ITfEditSession
    {
        private readonly TextService _owner;
        private readonly NativeMethods.ITfContextView _contextView;
        private readonly NativeMethods.ITfComposition? _composition;

        public GetTextExtEditSession(
            TextService owner,
            NativeMethods.ITfContextView contextView,
            NativeMethods.ITfComposition? composition)
        {
            _owner = owner;
            _contextView = contextView;
            _composition = composition;
        }

        public Int32 DoEditSession(UInt32 ec)
        {
            try
            {
                NativeMethods.ITfRange? range = null;
                if (_composition != null && _composition.GetRange(out range) == NativeMethods.S_OK && range != null)
                {
                    // Measure the FULL composition range (vertical growth → rect.Bottom follows the word).
                }
                if (range == null)
                {
                    return NativeMethods.S_OK;
                }

                // The live-caret fallback (GetGUIThreadInfo) lives in TextService. Here:
                //   Full-range probe: the whole composition range (the primary source).
                //   Walk-back probe: the ZERO-LENGTH range at composition START. That anchor exists
                //     earlier and is usually laid out even when the growing full range returns
                //     {0,0,0,0}/TS_E_NOLAYOUT (the documented Word vertical-TSF behavior).
                //   GetScreenExt safety net: ITfContextView::GetScreenExt. If the doc is visible, its
                //     extent is still a valid region → seed a non-authoritative anchor inside it so the
                //     candidate window never flashes at (0,0). Pixel-perfect anchoring is unreachable in
                //     Word by design; this only mitigates.

                if (TryQueryRange(ec, range, null)) return NativeMethods.S_OK;

                // Walk-back probe: the composition START (zero-length), more likely laid out.
                try
                {
                    if (range.Clone(out NativeMethods.ITfRange startRange) == NativeMethods.S_OK && startRange != null)
                    {
                        int hc = startRange.Collapse(ec, 0 /* TF_ANCHOR_START */);
                        if (hc == NativeMethods.S_OK)
                        {
                            if (TryQueryRange(ec, startRange, null)) return NativeMethods.S_OK;
                        }
                    }
                }
                catch (Exception) { }

                // GetScreenExt safety net: clamp into the doc's screen extent (never flash 0,0 while doc is visible).
                try
                {
                    int hs = _contextView.GetScreenExt(out RECT prc);
                    if (hs == NativeMethods.S_OK && (prc.Left != 0 || prc.Top != 0 || prc.Right != 0 || prc.Bottom != 0))
                    {
                        _owner.StoreScreenExtFallbackPos(prc);
                        _owner.ApplyCandidateWindowLocation();
                        return NativeMethods.S_OK;
                    }
                }
                catch (Exception) { }

                // All probes failed → keep last valid position (Weasel pattern).
            }
            catch (Exception)
            {
            }

            return NativeMethods.S_OK;
        }

        // Query one range; on a non-zero rect store it
        // as the authoritative anchor, and reposition. Returns true if a usable rect was obtained.
        /// <summary> Is this extent result usable? PURE, and separated out because it holds the one
        /// rule that must never soften.
        ///
        /// The rule is that an extent query must never fail silently: the query may fail (for example
        /// on a composition the host has not laid out yet), but a failure must never look like a
        /// success. Two ways it could: accepting a non-S_OK HRESULT, and accepting the all-zero
        /// rectangle that Word returns for a vertical-TSF range it has not laid out.</summary>
        public static bool IsUsableExtent(int hr, RECT rect)
            => hr == NativeMethods.S_OK
               && (rect.Left != 0 || rect.Top != 0 || rect.Right != 0 || rect.Bottom != 0);

        private bool TryQueryRange(UInt32 ec, NativeMethods.ITfRange range, string? tag)
        {
            int hr = _contextView.GetTextExt(ec, range, out RECT rect, out int clipped);
            if (IsUsableExtent(hr, rect))
            {

                _owner.StoreCandidatePos(rect);
                _owner.ApplyCandidateWindowLocation();
                return true;
            }
            return false;
        }
    }
}
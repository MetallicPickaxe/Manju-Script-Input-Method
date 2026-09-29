using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CSharpTSFInput
{
    /// <summary>
    /// A read-only edit session that measures the selection: ITfContext::GetSelection for the
    /// default selection, then ITfContextView::GetTextExt on its range. With nothing selected the selection
    /// is the insertion point. The settings menu opens under it while nothing is being composed.
    /// </summary>
    [GeneratedComClass]
    public partial class SelectionExtentEditSession : NativeMethods.ITfEditSession
    {
        private readonly NativeMethods.ITfContext _context;
        private readonly NativeMethods.ITfContextView _view;

        public SelectionExtentEditSession(NativeMethods.ITfContext context, NativeMethods.ITfContextView view)
        {
            _context = context;
            _view = view;
        }

        /// <summary> The selection's rectangle on screen, once the session has run and the host gave one
        /// that can be used; null otherwise.</summary>
        public RECT? Extent { get; private set; }

        public unsafe Int32 DoEditSession(UInt32 ec)
        {
            TF_SELECTION selection = default;
            int hr = _context.GetSelection(ec, NativeMethods.TF_DEFAULT_SELECTION, 1, &selection, out uint fetched);
            if (hr != NativeMethods.S_OK || fetched == 0 || selection.Range_Range == 0)
            {
                return NativeMethods.S_OK;
            }
            try
            {
                // The caller releases the range of a selection it obtained.
                var range = ComInterfaceMarshaller<NativeMethods.ITfRange>.ConvertToManaged((void*)selection.Range_Range);
                int hx = _view.GetTextExt(ec, range!, out RECT rect, out _);
                // A rectangle that is all zero (text not on screen) or upside down is not a place.
                if (GetTextExtEditSession.IsUsableExtent(hx, rect) && rect.Left <= rect.Right && rect.Top <= rect.Bottom)
                    Extent = rect;
            }
            finally
            {
                Marshal.Release(selection.Range_Range);
            }
            return NativeMethods.S_OK;
        }
    }
}

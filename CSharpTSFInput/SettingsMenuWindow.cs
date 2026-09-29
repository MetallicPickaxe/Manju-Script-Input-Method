using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CSharpTSFInput
{
    /// <summary>
    /// The window the settings menu is drawn in: a small topmost popup that never takes focus. It is
    /// drawn by <see cref="CandidateListUIPresenter.DrawPanel"/>, the routine that draws the toast on the
    /// candidate window, in the colours of the active theme, on the process's shared D2D factory. It is
    /// created on the thread that first shows it, the TSF thread, and lives until <see cref="Destroy"/>.
    /// </summary>
    internal sealed unsafe class SettingsMenuWindow
    {
        private static readonly nint s_wndProcPtr;
        private static int s_classCounter;

        static SettingsMenuWindow()
        {
            s_wndProcPtr = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&StaticWndProc;
        }

        private const uint WS_POPUP = 0x80000000;
        private const uint WS_EX_TOOLWINDOW = 0x00000080;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SW_HIDE = 0;
        private const int SW_SHOWNA = 8;
        private const uint WM_PAINT = 0x000F;
        private const uint WM_MOUSEACTIVATE = 0x0021;
        private const uint WM_TIMER = 0x0113;
        private const nuint PollTimerId = 1;
        private const nint MA_NOACTIVATE = 3;
        private const int D2DERR_RECREATE_TARGET = unchecked((int)0x8899000C);

        private readonly string _className;
        private GCHandle _selfHandle;
        private nint _hwnd;
        private bool _classRegistered;

        private NativeMethods.ID2D1HwndRenderTarget? _rt;
        private NativeMethods.ID2D1SolidColorBrush? _panelBrush, _edgeBrush, _textBrush, _highlightBrush;
        private nint _nativePanel, _nativeEdge, _nativeText, _nativeHighlight;

        private IReadOnlyList<string> _lines = Array.Empty<string>();
        private int _highlight = -1;
        private (D2D1_COLOR_F Panel, D2D1_COLOR_F Edge, D2D1_COLOR_F Text, D2D1_COLOR_F Highlight) _colors;
        private float _widthDip;

        public SettingsMenuWindow()
        {
            int n = System.Threading.Interlocked.Increment(ref s_classCounter);
            _className = $"ManjuSettingsMenu_{Environment.ProcessId}_{n}";
        }

        /// <summary> The window handle, 0 before the first <see cref="Show"/> and after
        /// <see cref="Destroy"/>.</summary>
        public nint Handle => _hwnd;

        /// <summary> Called on the window's thread every ManjuEngine.ConfigurationPollMs while the window
        /// is shown.</summary>
        public Action? OnTick { get; set; }

        public bool IsVisible => _hwnd != 0 && NativeMethods.IsWindowVisible(_hwnd);

        /// <summary> Show <paramref name="lines"/> with row <paramref name="highlight"/> banded, the
        /// top-left corner at <paramref name="anchor"/> (screen pixels), kept inside that monitor's work
        /// area. Called again with new lines, it redraws in place.</summary>
        public bool Show(IReadOnlyList<string> lines, int highlight,
            (D2D1_COLOR_F Panel, D2D1_COLOR_F Edge, D2D1_COLOR_F Text, D2D1_COLOR_F Highlight) colors, POINT? anchor)
        {
            // No anchor: the window stays where it is, and one that does not exist yet is not created.
            if (anchor == null && (_hwnd == 0 || !NativeMethods.GetWindowRect(_hwnd, out _)))
                return false;
            _lines = lines;
            _highlight = highlight;
            _colors = colors;
            POINT corner;
            if (anchor != null) corner = anchor.Value;
            else
            {
                NativeMethods.GetWindowRect(_hwnd, out RECT now);
                corner = new POINT { X = now.Left, Y = now.Top };
            }
            if (!EnsureWindow(corner)) return false;

            var (wDip, hDip) = CandidateListUIPresenter.PanelWindowSize(lines);
            _widthDip = wDip;
            // Place once at the current DPI, then again if the monitor under the anchor has another.
            uint dpi = Place(corner, wDip, hDip, NativeMethods.GetDpiForWindow(_hwnd));
            uint after = NativeMethods.GetDpiForWindow(_hwnd);
            if (after != 0 && after != dpi) dpi = Place(corner, wDip, hDip, after);

            if (_rt != null)
            {
                NativeMethods.GetClientRect(_hwnd, out RECT rc);
                _rt.SetDpi(dpi, dpi);
                _rt.Resize(new D2D1_SIZE_U { width = (uint)Math.Max(1, rc.Right - rc.Left), height = (uint)Math.Max(1, rc.Bottom - rc.Top) });
            }
            if (!NativeMethods.IsWindowVisible(_hwnd)) NativeMethods.ShowWindow(_hwnd, SW_SHOWNA);
            NativeMethods.SetTimer(_hwnd, PollTimerId, (uint)ManjuEngine.ConfigurationPollMs, 0);
            NativeMethods.InvalidateRect(_hwnd, 0, false);
            return true;
        }

        public void Hide()
        {
            if (_hwnd != 0 && NativeMethods.IsWindowVisible(_hwnd))
            {
                NativeMethods.KillTimer(_hwnd, PollTimerId);
                NativeMethods.ShowWindow(_hwnd, SW_HIDE);
            }
        }

        /// <summary> Destroy the window and let go of its graphics. The brushes and the render target
        /// are dropped, not released, the same as the candidate window's brushes. The D2D factory is
        /// shared and stays.</summary>
        public void Destroy()
        {
            ForgetGraphics();
            if (_hwnd != 0)
            {
                NativeMethods.DestroyWindow(_hwnd);
                _hwnd = 0;
            }
            if (_classRegistered)
            {
                NativeMethods.UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
                _classRegistered = false;
            }
            if (_selfHandle.IsAllocated) _selfHandle.Free();
        }

        /// <summary> Size the window for the DIP extent at <paramref name="dpi"/> and move it to the
        /// anchor, clamped into the work area. Returns the DPI it used.</summary>
        private uint Place(POINT anchor, float wDip, float hDip, uint dpi)
        {
            if (dpi == 0) dpi = 96;
            int w = (int)Math.Ceiling(wDip * dpi / 96f);
            int h = (int)Math.Ceiling(hDip * dpi / 96f);
            int x = anchor.X, y = anchor.Y;
            nint mon = NativeMethods.MonitorFromPoint(anchor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (mon != 0 && NativeMethods.GetMonitorInfo(mon, ref mi))
            {
                x = Math.Max(mi.rcWork.Left, Math.Min(x, mi.rcWork.Right - w));
                y = Math.Max(mi.rcWork.Top, Math.Min(y, mi.rcWork.Bottom - h));
            }
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE);
            return dpi;
        }

        private bool EnsureWindow(POINT anchor)
        {
            if (_hwnd != 0) return true;
            nint hInst = NativeMethods.GetModuleHandle(null);
            if (!_classRegistered)
            {
                fixed (char* pClassName = _className)
                {
                    WNDCLASSEX wc = new()
                    {
                        Size_CbSize = (UInt32)Marshal.SizeOf<WNDCLASSEX>(),
                        Style_Style = 0x0003u | 0x00020000u /* CS_HREDRAW|CS_VREDRAW|CS_DROPSHADOW */,
                        Ptr_LpfnWndProc = s_wndProcPtr,
                        Handle_HInstance = hInst,
                        Name_LpszClassName = (nint)pClassName,
                        Handle_HbrBackground = 0
                    };
                    if (NativeMethods.RegisterClassEx(&wc) == 0)
                    {
                        int err = Marshal.GetLastPInvokeError();
                        if (err != 1410) // ERROR_CLASS_ALREADY_EXISTS
                        {
                            return false;
                        }
                    }
                    _classRegistered = true;
                }
            }
            if (!_selfHandle.IsAllocated) _selfHandle = GCHandle.Alloc(this, GCHandleType.Weak);
            _hwnd = NativeMethods.CreateWindowEx(
                WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
                _className, "",
                WS_POPUP,
                anchor.X, anchor.Y, 1, 1,
                0, 0, hInst, GCHandle.ToIntPtr(_selfHandle));
            if (_hwnd == 0)
            {
                return false;
            }
            NativeMethods.SetWindowLongPtr(_hwnd, NativeMethods.GWLP_USERDATA, GCHandle.ToIntPtr(_selfHandle));
            return true;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        private static nint StaticWndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
        {
            try
            {
                if (msg == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
                if (msg == WM_TIMER && wParam == PollTimerId)
                {
                    nint data = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWLP_USERDATA);
                    var target = data != 0 ? GCHandle.FromIntPtr(data) : default;
                    if (target.IsAllocated && target.Target is SettingsMenuWindow timed) timed.OnTick?.Invoke();
                    return 0;
                }
                if (msg == WM_PAINT)
                {
                    nint userdata = NativeMethods.GetWindowLongPtr(hWnd, NativeMethods.GWLP_USERDATA);
                    var handle = userdata != 0 ? GCHandle.FromIntPtr(userdata) : default;
                    NativeMethods.BeginPaint(hWnd, out PAINTSTRUCT ps);
                    try
                    {
                        if (handle.IsAllocated && handle.Target is SettingsMenuWindow w) w.Paint();
                    }
                    finally
                    {
                        NativeMethods.EndPaint(hWnd, ref ps);
                    }
                    return 0;
                }
            }
            catch (Exception)
            {
            }
            return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void Paint()
        {
            if (!EnsureGraphics() || _rt == null) return;
            _panelBrush!.SetColor(_colors.Panel);
            _edgeBrush!.SetColor(_colors.Edge);
            _textBrush!.SetColor(_colors.Text);
            _highlightBrush!.SetColor(_colors.Highlight);

            _rt.BeginDraw();
            try
            {
                PaintBody(_rt, _lines, _highlight, _colors.Panel, _widthDip,
                    _nativePanel, _nativeEdge, _nativeText, _nativeHighlight);
            }
            finally
            {
                int hr = _rt.EndDraw(out _, out _);
                if (hr == D2DERR_RECREATE_TARGET)
                {
                    ForgetGraphics();
                }
            }
        }

        /// <summary> Draws the menu between BeginDraw and EndDraw: the whole target in the panel colour,
        /// then the panel with its lines and the band behind row <paramref name="highlight"/>.</summary>
        internal static void PaintBody(NativeMethods.ID2D1RenderTarget rt, IReadOnlyList<string> lines, int highlight,
            D2D1_COLOR_F panelColor, float widthDip, nint panelBrush, nint edgeBrush, nint textBrush, nint highlightBrush)
        {
            rt.Clear(panelColor);
            CandidateListUIPresenter.DrawPanel(rt, lines, widthDip, panelBrush, edgeBrush, textBrush, highlight, highlightBrush);
        }

        /// <summary> The render target and its four brushes, made once on the shared factory.</summary>
        private bool EnsureGraphics()
        {
            if (_rt != null) return true;
            var factory = CandidateListUIPresenter.GetSharedD2DFactory();
            if (factory == null || _hwnd == 0) return false;
            NativeMethods.GetClientRect(_hwnd, out RECT rc);
            var rtProps = new D2D1_RENDER_TARGET_PROPERTIES();
            var hwndProps = new D2D1_HWND_RENDER_TARGET_PROPERTIES
            {
                hwnd = _hwnd,
                pixelSize = new D2D1_SIZE_U { width = (uint)Math.Max(1, rc.Right - rc.Left), height = (uint)Math.Max(1, rc.Bottom - rc.Top) },
                presentOptions = 0
            };
            int hr = factory.CreateHwndRenderTarget(rtProps, hwndProps, out var rt);
            if (hr != 0 || rt == null)
            {
                return false;
            }
            uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
            if (dpi == 0) dpi = 96;
            rt.SetDpi(dpi, dpi);
            rt.CreateSolidColorBrush(_colors.Panel, 0, out _panelBrush);
            rt.CreateSolidColorBrush(_colors.Edge, 0, out _edgeBrush);
            rt.CreateSolidColorBrush(_colors.Text, 0, out _textBrush);
            rt.CreateSolidColorBrush(_colors.Highlight, 0, out _highlightBrush);
            if (_panelBrush == null || _edgeBrush == null || _textBrush == null || _highlightBrush == null)
            {
                ForgetGraphics();
                return false;
            }
            _nativePanel = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_panelBrush);
            _nativeEdge = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_edgeBrush);
            _nativeText = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_textBrush);
            _nativeHighlight = (nint)ComInterfaceMarshaller<NativeMethods.ID2D1SolidColorBrush>.ConvertToUnmanaged(_highlightBrush);
            _rt = rt;
            return true;
        }

        private void ForgetGraphics()
        {
            _panelBrush = _edgeBrush = _textBrush = _highlightBrush = null;
            _nativePanel = _nativeEdge = _nativeText = _nativeHighlight = 0;
            _rt = null;
        }
    }
}

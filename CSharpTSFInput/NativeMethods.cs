using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
// The engine-agnostic TSF COM-bootstrap/registration interop lives in the shared
// Core (super-layer). Engine-coupled interfaces (ITfThreadMgr / ITf*Processor /
// composition / range / sink graph) + D2D/DWrite + UI interop live here (Manju specialization).
using CSharpTSFInput.Core.Interop;

namespace CSharpTSFInput
{
	#region Structs

	[StructLayout (LayoutKind.Sequential)]
	public struct TF_SELECTION
	{
		public nint Range_Range; // ITfRange*
		public TF_SELECTIONSTYLE Style_Style;
	}

	public enum TfActiveSelEnd : UInt32
	{
		TF_AE_NONE = 0,
		TF_AE_START = 1,
		TF_AE_END = 2
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct TF_SELECTIONSTYLE
	{
		public TfActiveSelEnd Ase_Ase;
		public Int32 fInterimChar;  // BOOL is 4 bytes (Int32)
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct TF_STATUS
	{
		public UInt32 dwDynamicFlags;
		public UInt32 dwStaticFlags;
	}

	public enum TfLayoutCode : UInt32
	{
		TF_LC_CREATE = 0,
		TF_LC_CHANGE = 1,
		TF_LC_DESTROY = 2
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct TF_DISPLAYATTRIBUTE
	{
		public TF_DA_COLOR Color_Text;
		public TF_DA_COLOR Color_Bk;
		public TF_DA_LINESTYLE Attr_LsStyle;
		public Int32 fBoldLine;  // BOOL is 4 bytes (Int32)
		public TF_DA_COLOR Color_Line;
		public TF_DA_ATTR_INFO Attr_Attr;
	}

	public enum TF_DA_LINESTYLE : Int32
	{
		TF_LS_NONE = 0,
		TF_LS_SOLID = 1,
		TF_LS_DOT = 2,
		TF_LS_DASH = 3,
		TF_LS_SQUIGGLE = 4
	}

	public enum TF_DA_COLORTYPE : Int32
	{
		TF_CT_NONE = 0,
		TF_CT_SYSCOLOR = 1,
		TF_CT_COLORREF = 2
	}

	public enum TF_DA_ATTR_INFO : Int32
	{
		TF_ATTR_INPUT = 0,
		TF_ATTR_TARGET_CONVERTED = 1,
		TF_ATTR_CONVERTED = 2,
		TF_ATTR_TARGET_NOTCONVERTED = 3,
		TF_ATTR_INPUT_ERROR = 4,
		TF_ATTR_FIXEDCONVERTED = 5,
		TF_ATTR_OTHER = -1
	}

	[StructLayout (LayoutKind.Explicit)]
	public struct TF_DA_COLOR
	{
		[FieldOffset(0)]
		public TF_DA_COLORTYPE Type_Type;
		[FieldOffset(4)]
		public Int32 Index_Index;
		[FieldOffset(4)]
		public UInt32 ColorRef;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct TF_PRESERVEDKEY
	{
		public UInt32 uVKey;
		public UInt32 uModifiers;
	}

	// TF_INPUTPROCESSORPROFILE lives in CSharpTSFInput.Core.Interop (shared
	// engine-agnostic registration interop). Used by Core's ITfInputProcessorProfileMgr.

	[StructLayout (LayoutKind.Sequential)]
	public struct RECT
	{
		public Int32 Left;
		public Int32 Top;
		public Int32 Right;
		public Int32 Bottom;
	}

	// Monitor info for work-area-aware window placement (taskbar avoidance +
	// per-monitor boundary flip). cbSize MUST be set to sizeof(MONITORINFO) before GetMonitorInfoW.
	[StructLayout (LayoutKind.Sequential)]
	public struct MONITORINFO
	{
		public Int32 cbSize;
		public RECT rcMonitor;   // full monitor bounds
		public RECT rcWork;      // work area (excludes taskbar / appbars)
		public UInt32 dwFlags;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct POINT
	{
		public Int32 X;
		public Int32 Y;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct DWRITE_GLYPH_OFFSET
	{
		public float AdvanceOffset;
		public float AscenderOffset;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct DWRITE_GLYPH_METRICS
	{
		public Int32 LeftSideBearing;
		public UInt32 AdvanceWidth;
		public Int32 RightSideBearing;
		public Int32 TopSideBearing;
		public UInt32 AdvanceHeight;
		public Int32 BottomSideBearing;
		public Int32 VerticalOriginY;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct DWRITE_GLYPH_RUN
	{
		public nint FontFace; // IDWriteFontFace
		public float FontEmSize;
		public UInt32 GlyphCount; // Must be UINT32 (4 bytes) per dwrite.h
		public unsafe UInt16* GlyphIndices;
		public unsafe float* GlyphAdvances;
		public unsafe DWRITE_GLYPH_OFFSET* GlyphOffsets;
		public Int32 IsSideways; // BOOL is 4 bytes (Int32)
		public UInt32 BidiLevel;
	}

	// UI Structs
	[StructLayout (LayoutKind.Sequential)]
	public unsafe struct PAINTSTRUCT
	{
		public nint Handle_Hdc;
		public Int32 fErase; // BOOL is 4 bytes
		public RECT Rect_RcPaint;
		public Int32 fRestore; // BOOL is 4 bytes
		public Int32 fIncUpdate; // BOOL is 4 bytes
		public fixed Byte Data_RgbReserved[32];
	}

	public delegate nint WndProc (nint hWnd, UInt32 msg, nuint wParam, nint lParam);

	[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public struct WNDCLASSEX
	{
		public UInt32 Size_CbSize;
		public UInt32 Style_Style;
		public nint Ptr_LpfnWndProc;
		public Int32 Id_CbClsExtra;
		public Int32 Id_CbWndExtra;
		public nint Handle_HInstance;
		public nint Handle_HIcon;
		public nint Handle_HCursor;
		public nint Handle_HbrBackground;
		public nint Name_LpszMenuName;
		public nint Name_LpszClassName;
		public nint Handle_HIconSm;
	}

	// D2D Structs
	[StructLayout (LayoutKind.Sequential)]
	public struct RECT_F { public float left, top, right, bottom; }

	public enum D2D1_RENDER_TARGET_TYPE : Int32
	{
		D2D1_RENDER_TARGET_TYPE_DEFAULT = 0,
		D2D1_RENDER_TARGET_TYPE_SOFTWARE = 1,
		D2D1_RENDER_TARGET_TYPE_HARDWARE = 2,
		D2D1_RENDER_TARGET_TYPE_FORCE_DWORD = unchecked((Int32)0xFFFFFFFF)
	}

	public enum D2D1_FACTORY_TYPE : Int32
	{
		D2D1_FACTORY_TYPE_SINGLE_THREADED = 0,
		D2D1_FACTORY_TYPE_MULTI_THREADED = 1,
		D2D1_FACTORY_TYPE_FORCE_DWORD = unchecked((Int32)0xFFFFFFFF)
	}

	public enum D2D1_FEATURE_LEVEL : Int32
	{
		D2D1_FEATURE_LEVEL_DEFAULT = 0,
		D2D1_FEATURE_LEVEL_9 = unchecked((Int32)0x9100),  // D3D_FEATURE_LEVEL_9_1
		D2D1_FEATURE_LEVEL_10 = unchecked((Int32)0xA000), // D3D_FEATURE_LEVEL_10_0
		D2D1_FEATURE_LEVEL_FORCE_DWORD = unchecked((Int32)0xFFFFFFFF)
	}

	public enum D2D1_RENDER_TARGET_USAGE : Int32
	{
		D2D1_RENDER_TARGET_USAGE_NONE = 0x00000000,
		D2D1_RENDER_TARGET_USAGE_FORCE_BITMAP_REMOTING = 0x00000001,
		D2D1_RENDER_TARGET_USAGE_GDI_COMPATIBLE = 0x00000002,
		D2D1_RENDER_TARGET_USAGE_FORCE_DWORD = unchecked((Int32)0xFFFFFFFF)
	}

	public enum D2D1_PRESENT_OPTIONS : Int32
	{
		D2D1_PRESENT_OPTIONS_NONE = 0x00000000,
		D2D1_PRESENT_OPTIONS_RETAIN_CONTENTS = 0x00000001,
		D2D1_PRESENT_OPTIONS_IMMEDIATELY = 0x00000002,
		D2D1_PRESENT_OPTIONS_FORCE_DWORD = unchecked((Int32)0xFFFFFFFF)
	}

	public enum D2D1_WINDOW_STATE : Int32
	{
		D2D1_WINDOW_STATE_NONE = 0x00000000,
		D2D1_WINDOW_STATE_OCCLUDED = 0x00000001,
		D2D1_WINDOW_STATE_FORCE_DWORD = unchecked((Int32)0xFFFFFFFF)
	}

	public enum D2D1_ALPHA_MODE : Int32
	{
		D2D1_ALPHA_MODE_UNKNOWN = 0,
		D2D1_ALPHA_MODE_PREMULTIPLIED = 1,
		D2D1_ALPHA_MODE_STRAIGHT = 2,
		D2D1_ALPHA_MODE_IGNORE = 3,
		D2D1_ALPHA_MODE_FORCE_DWORD = unchecked((Int32)0xFFFFFFFF)
	}

	public enum DXGI_FORMAT : Int32
	{
		DXGI_FORMAT_UNKNOWN = 0,
		DXGI_FORMAT_B8G8R8A8_UNORM = 87
	}

	public enum DWRITE_FACTORY_TYPE : Int32
	{
		DWRITE_FACTORY_TYPE_SHARED = 0,
		DWRITE_FACTORY_TYPE_ISOLATED = 1
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct D2D1_PIXEL_FORMAT
	{
		public DXGI_FORMAT format;
		public D2D1_ALPHA_MODE alphaMode;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct D2D1_SIZE_U
	{
		public UInt32 width;
		public UInt32 height;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct D2D1_SIZE_F
	{
		public float width;
		public float height;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct D2D1_RENDER_TARGET_PROPERTIES
	{
		public D2D1_RENDER_TARGET_TYPE type;
		public D2D1_PIXEL_FORMAT pixelFormat;
		public float dpiX;
		public float dpiY;
		public D2D1_RENDER_TARGET_USAGE usage;
		public D2D1_FEATURE_LEVEL minLevel;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct D2D1_HWND_RENDER_TARGET_PROPERTIES
	{
		public nint hwnd;
		public D2D1_SIZE_U pixelSize;
		public D2D1_PRESENT_OPTIONS presentOptions;
	}

	[StructLayout (LayoutKind.Sequential)]
	public struct D2D1_COLOR_F { public float r, g, b, a; }

	[StructLayout (LayoutKind.Sequential)]
	public struct D2D1_POINT_2F { public float x, y; }

	[StructLayout (LayoutKind.Sequential)]
	public struct D2D1_MATRIX_3X2_F { public float m11, m12, m21, m22, dx, dy; }

	#endregion Structs

	public static partial class NativeMethods
	{
		#region Constants

		// These universal COM HRESULTs + registry constants are OWNED by the shared
		// Core (CSharpTSFInput.Core.Interop.TsfNative); re-exported here as single-sourced aliases so
		// the engine's NativeMethods.* references resolve to Core's values.
		// (NOT a copy: the value's single source of truth is Core — change Core, Manju recompiles to match.)
		public const Int32 S_OK = TsfNative.S_OK;
		public const Int32 S_FALSE = TsfNative.S_FALSE;
		public const Int32 E_FAIL = TsfNative.E_FAIL;
		public const Int32 E_INVALIDARG = TsfNative.E_INVALIDARG;
		public const Int32 E_NOINTERFACE = TsfNative.E_NOINTERFACE;
		public const Int32 E_POINTER = TsfNative.E_POINTER;
		public const Int32 E_NOTIMPL = TsfNative.E_NOTIMPL;
		public const Int32 CLASS_E_NOAGGREGATION = TsfNative.CLASS_E_NOAGGREGATION;
		public const Int32 CLASS_E_CLASSNOTAVAILABLE = TsfNative.CLASS_E_CLASSNOTAVAILABLE;
		// Engine-specific HRESULTs (advise/edit-lock) are Manju-local and live here.
		public const Int32 CONNECT_E_ADVISELIMIT = unchecked ((Int32)0x80040201);
		public const Int32 CONNECT_E_CANNOTCONNECT = unchecked ((Int32)0x80040202);
		public const Int32 TF_E_NOLOCK = unchecked ((Int32)0x80040206);
		// msctf.h: MAKE_HRESULT(SEVERITY_SUCCESS, FACILITY_ITF, 0x0300). What RequestEditSession puts in its
		// last parameter when an asynchronous session was queued.
		public const Int32 TF_S_ASYNC = 0x00040300;

		public const UInt32 REG_SZ = TsfNative.REG_SZ;
		public const UInt32 KEY_WRITE = TsfNative.KEY_WRITE;
		public const UInt32 CLSCTX_INPROC_SERVER = TsfNative.CLSCTX_INPROC_SERVER;
		public static readonly nint HKEY_CLASSES_ROOT = TsfNative.HKEY_CLASSES_ROOT;

		public const UInt32 TF_ES_ASYNCDONTCARE = 0x0;
		public const UInt32 TF_ES_SYNC = 0x1;
		public const UInt32 TF_ES_READ = 0x2;
		public const UInt32 TF_ES_READWRITE = 0x6;
		public const UInt32 TF_ES_ASYNC = 0x8;

		public const UInt32 TF_AE_NONE = 0;
		public const UInt32 TF_AE_START = 1;
		public const UInt32 TF_AE_END = 2;

		public const UInt32 TF_DEFAULT_SELECTION = unchecked((UInt32)0xFFFFFFFF);

		public const UInt32 TF_IAS_NOQUERY = 0x1;
		public const UInt32 TF_IAS_QUERYONLY = 0x2;
		public const UInt32 TF_IAS_NO_DEFAULT_COMPOSITION = 0x80000000;

		// TSF registration GUIDs/CLSIDs OWNED by Core; re-exported as single-sourced aliases.
		public static readonly Guid GUID_TFCAT_TIP_KEYBOARD = TsfNative.GUID_TFCAT_TIP_KEYBOARD;
		public static readonly Guid GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT = TsfNative.GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT;
		public static readonly Guid GUID_TFCAT_TIPCAP_UIELEMENTENABLED = TsfNative.GUID_TFCAT_TIPCAP_UIELEMENTENABLED;
		public static readonly Guid CLSID_TF_CategoryMgr = TsfNative.CLSID_TF_CategoryMgr;
		public static readonly Guid CLSID_TF_InputProcessorProfiles = TsfNative.CLSID_TF_InputProcessorProfiles;
		// Engine sink interface IIDs are Manju-local and live here.
		public static readonly Guid IID_ITfCompositionView = new Guid ("D7540241-F9A1-4364-BEFC-DBCD2C4395B7");
		public static readonly Guid IID_IEnumITfCompositionView = new Guid ("5EFD22BA-7838-46CB-88E2-CADB14124F8F");
		public static readonly Guid IID_ITfThreadMgrEventSink = new Guid ("aa80e80e-2021-11d2-93e0-0060b067b86e");
		public static readonly Guid IID_ITfThreadFocusSink = new Guid ("c0f1db0c-3a20-405c-a303-96b6010a885f");
		public static readonly Guid IID_ITfTextLayoutSink = new Guid ("2af2d06a-dd5b-4927-a0b4-54f19c91fade");
		public static readonly Guid IID_ITfSource = new Guid ("4ea48a35-60ae-446f-8fd6-e6a8d82459f7");
		public static readonly Guid IID_ITfContextOwnerCompositionSink = new Guid ("5F20AA40-B57A-4F34-96AB-3576F377CC79");

		#endregion Constants

		#region Constants (UI)

		public const UInt32 WS_POPUP = 0x80000000;
		public const UInt32 WS_EX_TOPMOST = 0x00000008;
		public const UInt32 WS_EX_TOOLWINDOW = 0x00000080;
		public const UInt32 WS_EX_NOACTIVATE = 0x08000000;
		public const UInt32 WS_EX_LAYERED = 0x00080000;

		public static readonly nint HWND_TOPMOST = (nint)(-1);
		public const UInt32 SWP_NOACTIVATE = 0x0010;
		public const UInt32 SWP_SHOWWINDOW = 0x0040;
		public const UInt32 SWP_NOMOVE = 0x0002;
		public const UInt32 SWP_NOZORDER = 0x0004;

		#endregion Constants (UI)

		#region Win32 API


		// GetModuleFileName lives in Core (CSharpTSFInput.Core.Interop.TsfNative).

        [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
        public static partial int MessageBox(nint hWnd, string text, string caption, uint type);

		// RegCreateKeyEx / RegSetValueEx / RegCloseKey / RegDeleteTree / CoCreateInstance
		// live in the shared Core (CSharpTSFInput.Core.Interop.TsfNative): engine-agnostic
		// registration/COM-creation interop. Manju bootstrap (DllExports/Register) calls them via TsfNative.

		[LibraryImport ("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
		public static partial nint CreateWindowEx (
			UInt32 Style_Ex,
			String Name_Class,
			String Name_Window,
			UInt32 Style,
			Int32 X,
			Int32 Y,
			Int32 Width,
			Int32 Height,
			nint Handle_Parent,
			nint Handle_Menu,
			nint Handle_Instance,
			nint Ptr_Param);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        public static partial nint CreateWindowEx(
            UInt32 Style_Ex,
            nint Atom_Class,
            String Name_Window,
            UInt32 Style,
            Int32 X,
            Int32 Y,
            Int32 Width,
            Int32 Height,
            nint Handle_Parent,
            nint Handle_Menu,
            nint Handle_Instance,
            nint Ptr_Param);

		[LibraryImport ("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		// No [SuppressGCTransition]: DestroyWindow dispatches WM_DESTROY synchronously
		public static partial Boolean DestroyWindow (nint Handle_Hwnd);

		[LibraryImport ("user32.dll", EntryPoint = "ShowWindow", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		// No [SuppressGCTransition]: ShowWindow dispatches WM_PAINT synchronously
		public static partial Boolean ShowWindow (nint Handle_Hwnd, Int32 Command_Show);

		// THE OS's OWN ANSWER TO "is this window on screen".
		//
		// Gating a ShowWindow call behind a CACHED bool cannot work: a cached bool cannot express
		// state that something else can change, so the moment the OS alters real visibility behind
		// the IME's back the flag lies, and every later hide becomes a no-op that leaves a stale window
		// on the screen. This is the value the correctness decisions read; the cached flag serves only
		// as an optimisation.
		[LibraryImport ("user32.dll", EntryPoint = "IsWindowVisible")]
		[return: MarshalAs (UnmanagedType.Bool)]
		public static partial Boolean IsWindowVisible (nint Handle_Hwnd);

        public const int GWLP_USERDATA = -21;
        public const int GWLP_WNDPROC = -4;

        [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static partial nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static partial nint GetWindowLongPtr(nint hWnd, int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "CallWindowProcW")]
        public static partial nint CallWindowProc(nint lpPrevWndFunc, nint hWnd, uint Msg, nuint wParam, nint lParam);

		[LibraryImport ("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
		public static unsafe partial UInt16 RegisterClassEx (void* WindowClass_Unk);

		[LibraryImport ("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
		[return: MarshalAs (UnmanagedType.Bool)]
		public static partial Boolean UnregisterClass (String Name_Class, nint Handle_Instance);

		[LibraryImport ("user32.dll", EntryPoint = "DefWindowProcW", StringMarshalling = StringMarshalling.Utf16)]
		// No [SuppressGCTransition]: DefWindowProc may call other window procedures
		public static partial nint DefWindowProc (nint Handle_Hwnd, UInt32 Id_Msg, nuint WParam_WParam, nint LParam_LParam);

		[LibraryImport ("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
		[SuppressGCTransition]
		public static partial nint GetModuleHandle (String? Name_ModuleName);

		// alias (single source = Core); GetModuleHandleEx lives in Core.Interop.TsfNative.
		public const UInt32 GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = TsfNative.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS;

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;
        public const int SM_CYVIRTUALSCREEN = 79;

        [LibraryImport("user32.dll", EntryPoint = "GetSystemMetrics")]
        public static partial int GetSystemMetrics(int nIndex);

        // Keyboard repeat DELAY — the point at which Windows itself decides a key is held
        // rather than pressed. Used as the lone-Shift tap ceiling so the threshold matches the feel of
        // the user's own keyboard instead of a number picked here. Returns 0..3 = 250/500/750/1000 ms.
        public const uint SPI_GETKEYBOARDDELAY = 0x0016;

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SystemParametersInfoW(uint uiAction, uint uiParam, out int pvParam, uint fWinIni);

        // Work-area-aware placement. MonitorFromPoint(MONITOR_DEFAULTTONEAREST=2)
        // → the monitor containing (or nearest to) the caret; GetMonitorInfoW fills rcWork (taskbar-excluded).
        public const uint MONITOR_DEFAULTTONEAREST = 2;

        [LibraryImport("user32.dll", EntryPoint = "MonitorFromPoint")]
        public static partial nint MonitorFromPoint(POINT pt, uint dwFlags);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial Boolean GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        // Per-WINDOW DPI (Win10 1607+). Microsoft deprecates GetDesktopDpi for
        // desktop apps: it returns the PRIMARY/system DPI, so a window on a secondary monitor would
        // get a mis-scaled render target.
        [LibraryImport("user32.dll", EntryPoint = "GetDpiForWindow")]
        public static partial UInt32 GetDpiForWindow(nint hWnd);

        // Touch pan-to-scroll. WM_GESTURE + GID_PAN via GetGestureInfo: the window
        // receives WM_GESTURE by default for touch (no RegisterTouchWindow); unhandled gestures pass to
        // DefWindowProc which synthesizes the legacy mouse messages (tap → WM_LBUTTONDOWN → pick).
        public const UInt32 WM_GESTURE = 0x0119;
        public const UInt32 GID_BEGIN = 1;
        public const UInt32 GID_END = 2;
        public const UInt32 GID_PAN = 4;

        [StructLayout(LayoutKind.Sequential)]
        public struct GESTUREINFO
        {
            public UInt32 cbSize;
            public UInt32 dwFlags;
            public UInt32 dwID;
            public nint hwndTarget;
            public Int16 ptsLocationX;   // POINTS: screen coords, physical px
            public Int16 ptsLocationY;
            public UInt32 dwInstanceID;
            public UInt32 dwSequenceID;
            public UInt64 ullArguments;
            public UInt32 cbExtraArgs;
        }

        [LibraryImport("user32.dll", EntryPoint = "GetGestureInfo", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial Boolean GetGestureInfo(nint hGestureInfo, ref GESTUREINFO pGestureInfo);

        [LibraryImport("user32.dll", EntryPoint = "CloseGestureInfoHandle", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial Boolean CloseGestureInfoHandle(nint hGestureInfo);

		[LibraryImport ("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		// No [SuppressGCTransition]: SetWindowPos dispatches WM_SIZE/WM_MOVE synchronously
		public static partial Boolean SetWindowPos (nint Handle_Hwnd, nint Handle_InsertAfter, Int32 X, Int32 Y, Int32 Cx, Int32 Cy, UInt32 Flags_Flags);

		[LibraryImport ("user32.dll", EntryPoint = "GetClientRect", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		[SuppressGCTransition]
		public static partial Boolean GetClientRect (nint Handle_Hwnd, out RECT Rect_Rc);

		[LibraryImport ("user32.dll", EntryPoint = "InvalidateRect", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		// No [SuppressGCTransition]: may trigger WM_PAINT in some scenarios
		public static partial Boolean InvalidateRect (nint Handle_Hwnd, nint Rect_Rc, [MarshalAs (UnmanagedType.Bool)] Boolean Flag_Erase);

		[LibraryImport ("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		[SuppressGCTransition]
		public static partial Boolean PostMessage (nint Handle_Hwnd, UInt32 Id_Msg, nuint WParam_WParam, nint LParam_LParam);

		[LibraryImport ("user32.dll", EntryPoint = "SetTimer", SetLastError = true)]
		[SuppressGCTransition]
		public static partial nuint SetTimer (nint Handle_Hwnd, nuint Id_Event, UInt32 Ms_Elapse, nint Proc_Timer);

		[LibraryImport ("user32.dll", EntryPoint = "KillTimer", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		[SuppressGCTransition]
		public static partial Boolean KillTimer (nint Handle_Hwnd, nuint Id_Event);

		[LibraryImport ("user32.dll", EntryPoint = "BeginPaint", SetLastError = true)]
		// No [SuppressGCTransition]: managed code runs between BeginPaint and EndPaint and can trigger a GC
		public static partial nint BeginPaint (nint Handle_Hwnd, out PAINTSTRUCT Ptr_Paint);

		[LibraryImport ("user32.dll", EntryPoint = "EndPaint", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		// No [SuppressGCTransition]: paired with BeginPaint above
		public static partial Boolean EndPaint (nint Handle_Hwnd, ref PAINTSTRUCT Ptr_Paint);

		[LibraryImport ("user32.dll", EntryPoint = "GetKeyState")]
		[SuppressGCTransition]
		public static partial Int16 GetKeyState (Int32 Id_VirtKey);

		// Unicode normalization by Windows, from normaliz.dll, the export .NET itself calls when it
		// uses NLS. The DLL is built with InvariantGlobalization, where string.Normalize returns non-ASCII
		// text as it is; ManjuEngine.CanonicalDecomposition calls this instead.
		internal const int NormalizationD = 2;
		internal const int ERROR_INSUFFICIENT_BUFFER = 122;

		[LibraryImport ("normaliz.dll", EntryPoint = "NormalizeString", SetLastError = true)]
		internal static unsafe partial int NormalizeString (int NormForm, char* lpSrcString, int cwSrcLength, char* lpDstString, int cwDstLength);

		// The text a key produces on a keyboard layout (Keycap).
		[LibraryImport ("user32.dll", EntryPoint = "ToUnicodeEx")]
		public static unsafe partial Int32 ToUnicodeEx (UInt32 wVirtKey, UInt32 wScanCode, byte* lpKeyState, char* pwszBuff, Int32 cchBuff, UInt32 wFlags, nint dwhkl);

		[LibraryImport ("user32.dll", EntryPoint = "GetKeyboardLayout")]
		public static partial nint GetKeyboardLayout (UInt32 idThread);

		[LibraryImport ("user32.dll", EntryPoint = "MapVirtualKeyExW")]
		public static partial UInt32 MapVirtualKeyEx (UInt32 uCode, UInt32 uMapType, nint dwhkl);


		// GetForegroundWindow + GetWindowRect for caret bounds validation
		[LibraryImport ("user32.dll", EntryPoint = "GetForegroundWindow")]
		public static partial nint GetForegroundWindow ();

		[LibraryImport ("user32.dll", EntryPoint = "GetWindowRect", SetLastError = true)]
		[return: MarshalAs (UnmanagedType.Bool)]
		public static partial Boolean GetWindowRect (nint Handle_Hwnd, out RECT Rect_Rc);

#pragma warning disable CA1420
        [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetClassName(nint Handle_Hwnd, [Out] char[] Buffer_ClassName, int MaxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowTextLength(nint Handle_Hwnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int GetWindowText(nint Handle_Hwnd, [Out] char[] Buffer_Text, int MaxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(nint Handle_Hwnd, out uint ProcessId);
#pragma warning restore CA1420

    // ClientToScreen for caret coordinate conversion
    [LibraryImport ("user32.dll", EntryPoint = "ClientToScreen", SetLastError = true)]
    [return: MarshalAs (UnmanagedType.Bool)]
    public static partial Boolean ClientToScreen (nint Handle_Hwnd, ref POINT Point_Pt);

    // GetGUIThreadInfo for caret window handle
    [LibraryImport ("user32.dll", EntryPoint = "GetGUIThreadInfo", SetLastError = true)]
    [return: MarshalAs (UnmanagedType.Bool)]
    public static partial Boolean GetGUIThreadInfo (uint IdThread, ref GUITHREADINFO Info_Gui);

    // GUITHREADINFO struct
    [StructLayout (LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize;
        public int flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }

    // The caret of the thread named by threadId, as a screen rectangle. Thread id 0 asks about the
    // FOREGROUND thread, which after the Win key, Alt+Tab or a click elsewhere belongs to another program.
    /// <summary>
    /// Gets the text caret position in screen coordinates.
    /// Uses GetGUIThreadInfo to find the caret window, then ClientToScreen to convert.
    /// </summary>
    public static bool TryGetCaretScreenRect(uint threadId, out RECT screenRect)
    {
        screenRect = default;
        GUITHREADINFO info = new () { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<GUITHREADINFO>() };
        if (!ReadGuiThreadInfo(threadId, ref info) || info.hwndCaret == 0)
            return false;
        POINT topLeft = new POINT { X = info.rcCaret.Left, Y = info.rcCaret.Top };
        if (!ClientToScreen(info.hwndCaret, ref topLeft))
            return false;
        screenRect = new RECT
        {
            Left = topLeft.X,
            Top = topLeft.Y,
            Right = topLeft.X + (info.rcCaret.Right - info.rcCaret.Left),
            Bottom = topLeft.Y + (info.rcCaret.Bottom - info.rcCaret.Top)
        };
        return true;
    }


    internal static bool ReadGuiThreadInfo(uint threadId, ref GUITHREADINFO info)
    {
        return GetGUIThreadInfo(threadId, ref info);
    }

    [LibraryImport ("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    public static partial UInt32 GetCurrentThreadId ();

    // winuser.h. An out-of-context hook is called on the thread that installed it, from that thread's
    // message loop; UnhookWinEvent has to be called on that thread too.
    public const UInt32 EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const UInt32 WINEVENT_OUTOFCONTEXT = 0x0000;

    [LibraryImport ("user32.dll", EntryPoint = "SetWinEventHook")]
    public static partial nint SetWinEventHook (UInt32 EventMin, UInt32 EventMax, nint Module_WinEventProc, nint Proc_WinEventProc, UInt32 IdProcess, UInt32 IdThread, UInt32 Flags);

    [LibraryImport ("user32.dll", EntryPoint = "UnhookWinEvent")]
    [return: MarshalAs (UnmanagedType.Bool)]
    public static partial Boolean UnhookWinEvent (nint Hook_WinEvent);

    // winnt.h TOKEN_INFORMATION_CLASS: TokenUser = 1 … TokenIsAppContainer = 29. The token is the
    // pseudo-handle GetCurrentProcessToken() returns, (HANDLE)(LONG_PTR)-4 in processthreadsapi.h.
    public const Int32 TokenIsAppContainer = 29;
    public static readonly nint CurrentProcessToken = -4;

    [LibraryImport ("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)]
    [return: MarshalAs (UnmanagedType.Bool)]
    public static unsafe partial Boolean GetTokenInformation (nint Handle_Token, Int32 Class_Information, void* Buffer_Information, UInt32 Length_Information, out UInt32 Length_Return);

		public const Int32 VK_SHIFT = 0x10;

		[LibraryImport ("d2d1.dll", EntryPoint = "D2D1CreateFactory")]
		public static unsafe partial Int32 D2D1CreateFactory (D2D1_FACTORY_TYPE Type_FactoryType, Guid* Iid_Riid, nint Ptr_FactoryOptions, out ID2D1Factory Ppv_Factory);

		[LibraryImport ("dwrite.dll", EntryPoint = "DWriteCreateFactory")]
		public static unsafe partial Int32 DWriteCreateFactory (DWRITE_FACTORY_TYPE Type_FactoryType, Guid* Iid_Riid, out IDWriteFactory Ppv_Factory);

		#endregion Win32 API

		#region COM Interfaces

		[GeneratedComInterface]
		[Guid ("2cd90694-12e2-11dc-9fed-001143a055f9")]
		public partial interface ID2D1RenderTarget
		{
            // ID2D1Resource (Inherited)
            // 3
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void GetFactory(out ID2D1Factory factory);

            // ID2D1RenderTarget
            // 4
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateBitmap(IntPtr size, IntPtr srcData, uint pitch, IntPtr format, out IntPtr bitmap);
            // 5
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateBitmapFromWicBitmap(IntPtr wicBitmapSource, IntPtr bitmapProperties, out IntPtr bitmap);
            // 6
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateSharedBitmap(ref Guid riid, IntPtr data, IntPtr bitmapProperties, out IntPtr bitmap);
            // 7
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateBitmapBrush(IntPtr bitmap, IntPtr bitmapBrushProperties, IntPtr brushProperties, out IntPtr bitmapBrush);
            
            // 8: CreateSolidColorBrush (USED)
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int CreateSolidColorBrush (in D2D1_COLOR_F color, nint brushProperties, out ID2D1SolidColorBrush brush);

            // 9
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateGradientStopCollection(IntPtr stops, uint stopsCount, int gamma, int extendMode, out IntPtr gradientStopCollection);
            // 10
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateLinearGradientBrush(IntPtr linearGradientBrushProperties, IntPtr brushProperties, IntPtr gradientStopCollection, out IntPtr linearGradientBrush);
            // 11
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateRadialGradientBrush(IntPtr radialGradientBrushProperties, IntPtr brushProperties, IntPtr gradientStopCollection, out IntPtr radialGradientBrush);
            // 12
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateCompatibleRenderTarget(IntPtr desiredSize, IntPtr desiredPixelSize, IntPtr desiredFormat, int options, out IntPtr bitmapRenderTarget);
            // 13
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateLayer(IntPtr size, out IntPtr layer);
            // 14
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateMesh(out IntPtr mesh);
            // 15
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void DrawLine(D2D1_POINT_2F point0, D2D1_POINT_2F point1, IntPtr brush, float strokeWidth, IntPtr strokeStyle);
            // 16
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void DrawRectangle(in RECT_F rect, IntPtr brush, float strokeWidth, IntPtr strokeStyle);
            
            // 17: FillRectangle (USED)
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void FillRectangle (in RECT_F rect, nint brush);

            // 18
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void DrawRoundedRectangle(IntPtr roundedRect, IntPtr brush, float strokeWidth, IntPtr strokeStyle);
            // 19
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void FillRoundedRectangle(IntPtr roundedRect, IntPtr brush);
            // 20
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void DrawEllipse(IntPtr ellipse, IntPtr brush, float strokeWidth, IntPtr strokeStyle);
            // 21
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void FillEllipse(IntPtr ellipse, IntPtr brush);
            // 22
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void DrawGeometry(IntPtr geometry, IntPtr brush, float strokeWidth, IntPtr strokeStyle);
            // 23
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void FillGeometry(IntPtr geometry, IntPtr brush, IntPtr opacityBrush);
            // 24
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void FillMesh(IntPtr mesh, IntPtr brush);
            // 25
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void FillOpacityMask(IntPtr opacityMask, IntPtr brush, int content, IntPtr destinationRectangle, IntPtr sourceRectangle);
            // 26
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void DrawBitmap(IntPtr bitmap, IntPtr destinationRectangle, float opacity, int interpolationMode, IntPtr sourceRectangle);
            // 27
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void DrawText(IntPtr start, uint length, IntPtr format, IntPtr layoutRect, IntPtr defaultFillBrush, int options, int measuringMode);
            // 28
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void DrawTextLayout(D2D1_POINT_2F origin, IntPtr textLayout, IntPtr defaultFillBrush, int options);
            
            // 29: DrawGlyphRun (USED)
            // a COM vtable has no overloads, so only the nint form is declared
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void DrawGlyphRun (D2D1_POINT_2F baselineOrigin, in DWRITE_GLYPH_RUN glyphRun, nint brush, UInt32 measuringMode);

            // 30
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void SetTransform(in D2D1_MATRIX_3X2_F transform);
            // 31
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void GetTransform(out D2D1_MATRIX_3X2_F transform);
            // 32
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void SetAntialiasMode(int antialiasMode);
            // 33
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int GetAntialiasMode();
            // 34
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void SetTextAntialiasMode(int textAntialiasMode);
            // 35
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int GetTextAntialiasMode();
            // 36
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void SetTextRenderingParams(IntPtr textRenderingParams);
            // 37
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void GetTextRenderingParams(out IntPtr textRenderingParams);
            // 38
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void SetTags(ulong tag1, ulong tag2);
            // 39
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void GetTags(out ulong tag1, out ulong tag2);
            // 40
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void PushLayer(IntPtr layerParameters, IntPtr layer);
            // 41
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void PopLayer();
            // 42
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int Flush(out ulong tag1, out ulong tag2);
            // 43
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void SaveDrawingState(IntPtr drawingStateBlock);
            // 44
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void RestoreDrawingState(IntPtr drawingStateBlock);
            // 45
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void PushAxisAlignedClip(in RECT_F clipRect, int antialiasMode);
            // 46
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void PopAxisAlignedClip();
            
            // 47: Clear (USED)
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void Clear (in D2D1_COLOR_F clearColor);

            // 48: BeginDraw (USED)
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void BeginDraw ();

            // 49: EndDraw (USED)
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			Int32 EndDraw (out UInt64 tag1, out UInt64 tag2);

            // 50
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            D2D1_PIXEL_FORMAT GetPixelFormat();

            // 51: SetDpi (USED)
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void SetDpi (float dpiX, float dpiY);

            // 52
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void GetDpi(out float dpiX, out float dpiY);
            // 53
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            D2D1_SIZE_F GetSize();
            // 54
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            D2D1_SIZE_U GetPixelSize();
            // 55
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            uint GetMaximumBitmapSize();
            // 56
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int IsSupported(IntPtr renderTargetProperties);
		}

		[GeneratedComInterface]
		[Guid ("2cd90698-12e2-11dc-9fed-001143a055f9")]
		public partial interface ID2D1HwndRenderTarget : ID2D1RenderTarget
		{
			// Additional methods after ID2D1RenderTarget
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			D2D1_WINDOW_STATE CheckWindowState();
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int Resize(in D2D1_SIZE_U pixelSize);
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			nint GetHwnd();
		}

		[GeneratedComInterface]
		[Guid ("1c51bc64-de61-46fd-9899-63a5d8f03950")]
		public partial interface ID2D1DCRenderTarget : ID2D1RenderTarget
		{
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int BindDC(nint hdc, in RECT pSubRect);
		}

		[GeneratedComInterface]
		[Guid ("2cd90691-12e2-11dc-9fed-001143a055f9")]
		public partial interface ID2D1Resource
		{
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void GetFactory(out ID2D1Factory factory);
		}

		[GeneratedComInterface]
		[Guid ("2cd906a8-12e2-11dc-9fed-001143a055f9")]
		public partial interface ID2D1Brush : ID2D1Resource
		{
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void SetOpacity(float opacity);
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void SetTransform(in D2D1_MATRIX_3X2_F transform);
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			float GetOpacity();
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void GetTransform(out D2D1_MATRIX_3X2_F transform);
		}

		[GeneratedComInterface]
		[Guid ("2cd906a9-12e2-11dc-9fed-001143a055f9")]
		public partial interface ID2D1SolidColorBrush : ID2D1Brush
		{
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void SetColor(in D2D1_COLOR_F color);
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			D2D1_COLOR_F GetColor();
		}

		[GeneratedComInterface]
		[Guid ("bdc2a500-6649-4b8c-8108-394c3a0d591c")]
		public partial interface ID2D1Factory7 : ID2D1Factory
		{
			// No methods beyond the base ID2D1Factory slots are declared.
		}

		[GeneratedComInterface]
		[Guid ("06152247-6f50-465a-9245-118bfd3b6007")]
		public partial interface ID2D1Factory
		{
            // VTable order MUST be preserved.
            // 3: ReloadSystemMetrics
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int ReloadSystemMetrics();
            // 4: GetDesktopDpi
            [PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
            void GetDesktopDpi(out float dpiX, out float dpiY);
            // 5: CreateRectangleGeometry
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateRectangleGeometry(in RECT_F rectangle, out IntPtr geometry);
            // 6: CreateRoundedRectangleGeometry
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateRoundedRectangleGeometry(IntPtr roundedRectangle, out IntPtr geometry);
            // 7: CreateEllipseGeometry
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateEllipseGeometry(IntPtr ellipse, out IntPtr geometry);
            // 8: CreateGeometryGroup
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateGeometryGroup(int fillMode, IntPtr geometries, int geometriesCount, out IntPtr geometryGroup);
            // 9: CreateTransformedGeometry
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateTransformedGeometry(IntPtr sourceGeometry, IntPtr transform, out IntPtr transformedGeometry);
            // 10: CreatePathGeometry
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreatePathGeometry(out IntPtr pathGeometry);
            // 11: CreateStrokeStyle
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateStrokeStyle(IntPtr strokeStyleProperties, IntPtr dashes, int dashesCount, out IntPtr strokeStyle);
            // 12: CreateDrawingStateBlock
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateDrawingStateBlock(IntPtr drawingStateDescription, IntPtr textRenderingParams, out IntPtr drawingStateBlock);
            // 13: CreateWicBitmapRenderTarget
            [PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
            int CreateWicBitmapRenderTarget(IntPtr target, in D2D1_RENDER_TARGET_PROPERTIES renderTargetProperties, out ID2D1RenderTarget renderTarget);

            // 14: CreateHwndRenderTarget (Target) -> HRESULT
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int CreateHwndRenderTarget (in D2D1_RENDER_TARGET_PROPERTIES renderTargetProperties, in D2D1_HWND_RENDER_TARGET_PROPERTIES hwndRenderTargetProperties, out ID2D1HwndRenderTarget hwndRenderTarget);

			// 15: CreateDxgiSurfaceRenderTarget
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int CreateDxgiSurfaceRenderTarget(nint dxgiSurface, in D2D1_RENDER_TARGET_PROPERTIES renderTargetProperties, out ID2D1RenderTarget renderTarget);

			// 16: CreateDCRenderTarget
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int CreateDCRenderTarget(in D2D1_RENDER_TARGET_PROPERTIES renderTargetProperties, out ID2D1DCRenderTarget dcRenderTarget);
		}

		// IUnknown + IClassFactory live in CSharpTSFInput.Core.Interop (shared COM
		// bootstrap). Manju DllExports/ClassFactory consume Core's versions.

		[GeneratedComInterface]
		[Guid ("b859ee5a-d838-4b5b-a2e8-1adc7d93db48")]  // IDWriteFactory GUID
		public partial interface IDWriteFactory
		{
			// 3
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int GetSystemFontCollection (out nint collection, Int32 checkForUpdates);
			// 4
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateCustomFontCollection (nint loader, nint key, UInt32 keySize, out nint collection);
			// 5
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int RegisterFontCollectionLoader (nint loader);
			// 6
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int UnregisterFontCollectionLoader (nint loader);
			// 7
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateFontFileReference (nint filePath, nint lastWriteTime, out IDWriteFontFile fontFile);
			// 8
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateCustomFontFileReference (nint fontFileReferenceKey, UInt32 fontFileReferenceKeySize, IDWriteFontFileLoader fontFileLoader, out IDWriteFontFile fontFile);
			// 9
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateFontFace (UInt32 fontFaceType, UInt32 numberOfFiles, nint fontFiles, UInt32 faceIndex, UInt32 fontFaceSimulationFlags, out IDWriteFontFace fontFace);
			// 10
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateRenderingParams (out nint renderingParams);
			// 11
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateMonitorRenderingParams (nint monitor, out nint renderingParams);
			// 12
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateCustomRenderingParams (float gamma, float enhancedContrast, float clearTypeLevel, UInt32 pixelGeometry, UInt32 renderingMode, out nint renderingParams);
			// 13
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int RegisterFontFileLoader (IDWriteFontFileLoader fontFileLoader);
			// 14
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int UnregisterFontFileLoader (IDWriteFontFileLoader fontFileLoader);
			// 15
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateTextFormat (nint fontFamilyName, nint fontCollection, UInt32 fontWeight, UInt32 fontStyle, UInt32 fontStretch, float fontSize, nint localeName, out nint textFormat);
			// 16
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateTypography (out nint typography);
			// 17
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int GetGdiInterop (out nint gdiInterop);
			// 18
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateTextLayout (nint @string, UInt32 stringLength, nint textFormat, float maxWidth, float maxHeight, out nint textLayout);
			// 19
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateGdiCompatibleTextLayout (nint @string, UInt32 stringLength, nint textFormat, float layoutWidth, float layoutHeight, float pixelsPerDip, nint transform, Int32 useGdiNatural, out nint textLayout);
			// 20
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateEllipsisTrimmingSign (nint textFormat, out nint trimmingSign);
			// 21
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateTextAnalyzer (out nint textAnalyzer);
			// 22
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateNumberSubstitution (UInt32 substitutionMethod, nint localeName, Int32 ignoreUserOverride, out nint numberSubstitution);
			// 23
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateGlyphRunAnalysis (nint glyphRun, float pixelsPerDip, nint transform, UInt32 renderingMode, UInt32 measuringMode, float baselineOriginX, float baselineOriginY, out nint glyphRunAnalysis);
		}

		[GeneratedComInterface]
		[Guid ("727cad4e-d6af-4c9e-8a08-d695b11caa49")]
		public partial interface IDWriteFontFileLoader
		{
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int CreateStreamFromKey (nint fontFileReferenceKey, UInt32 fontFileReferenceKeySize, out IDWriteFontFileStream fontFileStream);
		}

		[GeneratedComInterface]
		[Guid ("6d4865fe-0ab8-4d91-8f62-5dd6be34a3e0")]
		public partial interface IDWriteFontFileStream
		{
			// 3
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int ReadFileFragment (out nint fragmentStart, UInt64 fileOffset, UInt64 fragmentSize, out nint fragmentContext);
			// 4
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void ReleaseFileFragment (nint fragmentContext);
			// 5
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int GetFileSize (out UInt64 fileSize);
			// 6
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int GetLastWriteTime (out UInt64 lastWriteTime);
		}

		[GeneratedComInterface]
		[Guid ("739d886a-cef5-47dc-8769-1a8b41bebbb0")]
		public partial interface IDWriteFontFile
		{
			// 3
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int GetReferenceKey (out nint fontFileReferenceKey, out UInt32 fontFileReferenceKeySize);
			// 4
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int GetLoader (out IDWriteFontFileLoader fontFileLoader);
			// 5
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int Analyze (out Int32 isSupportedFontType, out UInt32 fileType, out UInt32 faceType, out UInt32 numberOfFaces);
		}

		[GeneratedComInterface]
		[Guid ("5f49804d-7024-4d43-bfa9-d25984f53849")]
		public partial interface IDWriteFontFace
		{
			// 3
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			UInt32 GetType ();
			// 4
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int GetFiles (ref UInt32 numberOfFiles, void** fontFiles);
			// 5
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			UInt32 GetIndex ();
			// 6
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			UInt32 GetSimulations ();
			// 7
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			Int32 IsSymbolFont ();
			// 8
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void GetMetrics (nint fontFaceMetrics);
			// 9
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			UInt16 GetGlyphCount ();
			// 10
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int GetDesignGlyphMetrics (UInt16* glyphIndices, UInt32 glyphCount, void* glyphMetrics, Int32 isSideways);
			// 11
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int GetGlyphIndices (UInt32* codePoints, UInt32 codePointCount, UInt16* glyphIndices);
			// 12
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int TryGetFontTable (UInt32 openTypeTableTag, out nint tableData, out UInt32 tableSize, out nint tableContext, out Int32 exists);
			// 13
			[PreserveSig]   // native returns void; without this the generator reads a garbage return register as HRESULT and throws
			void ReleaseFontTable (nint tableContext);
			// 14
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int GetGlyphRunOutline (float emSize, UInt16* glyphIndices, float* glyphAdvances, DWRITE_GLYPH_OFFSET* glyphOffsets, UInt32 glyphCount, Int32 isSideways, Int32 isRightToLeft, nint geometrySink);
			// 15
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			int GetRecommendedRenderingMode (float emSize, float pixelsPerDip, UInt32 measuringMode, nint renderingParams, out UInt32 renderingMode);
			// 16
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int GetGdiCompatibleMetrics (float emSize, float pixelsPerDip, nint transform, nint fontFaceMetrics);
			// 17
			[PreserveSig]   // hand-declared to match the native vtable 1:1 — the generator must not synthesise HRESULT translation or a retval out-param
			unsafe int GetGdiCompatibleGlyphMetrics (float emSize, float pixelsPerDip, nint transform, Int32 useGdiNatural, UInt16* glyphIndices, UInt32 glyphCount, void* glyphMetrics, Int32 isSideways);
		}

		[GeneratedComInterface]
		[Guid ("aa80e801-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfThreadMgr
		{
			[PreserveSig]
			Int32 Activate (out UInt32 Id_ClientId);
			[PreserveSig]
			Int32 Deactivate ();
			[PreserveSig]
			Int32 CreateDocumentMgr (out ITfDocumentMgr Mgr_DocMgr);
			[PreserveSig]
			Int32 EnumDocumentMgrs (out nint Enum_EnumDocMgrs);
			[PreserveSig]
			Int32 GetFocus (out ITfDocumentMgr Mgr_DocMgr);
			[PreserveSig]
			Int32 SetFocus (ITfDocumentMgr Mgr_DocMgr);
			[PreserveSig]
			Int32 AssociateFocus (nint Handle_Hwnd, ITfDocumentMgr Mgr_NewDocMgr, out ITfDocumentMgr Mgr_PrevDocMgr);
			[PreserveSig]
			Int32 IsThreadFocus (out Int32 pfFocus);
            // 11
            [PreserveSig]
            Int32 GetFunctionProvider(in Guid clsid, out nint ppFuncProv);
            // 12
            [PreserveSig]
            Int32 EnumFunctionProviders(out nint ppEnum);
            // 13
            [PreserveSig]
            Int32 GetGlobalCompartment(out nint ppCompMgr);
		}

		[GeneratedComInterface]
		[Guid ("4ea48a35-60ae-446f-8fd6-e6a8d82459f7")]
		public partial interface ITfSource
		{
			[PreserveSig]
			Int32 AdviseSink (in Guid riid, nint punk, out UInt32 pdwCookie);
			[PreserveSig]
			Int32 UnadviseSink (UInt32 dwCookie);
		}

		[GeneratedComInterface]
		[Guid ("aa80e80e-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfThreadMgrEventSink
		{
			[PreserveSig]
			Int32 OnInitDocumentMgr (ITfDocumentMgr pdim);
			[PreserveSig]
			Int32 OnUninitDocumentMgr (ITfDocumentMgr pdim);
			[PreserveSig]
			Int32 OnSetFocus (ITfDocumentMgr pdimFocus, ITfDocumentMgr pdimPrevFocus);
			[PreserveSig]
			Int32 OnPushContext (nint pic);
			[PreserveSig]
			Int32 OnPopContext (nint pic);
		}

		// msctf.h: MIDL_INTERFACE("c0f1db0c-3a20-405c-a303-96b6010a885f") ITfThreadFocusSink : public IUnknown,
		// OnSetThreadFocus then OnKillThreadFocus.
		[GeneratedComInterface]
		[Guid ("c0f1db0c-3a20-405c-a303-96b6010a885f")]
		public partial interface ITfThreadFocusSink
		{
			[PreserveSig]
			Int32 OnSetThreadFocus ();
			[PreserveSig]
			Int32 OnKillThreadFocus ();
		}

		[GeneratedComInterface]
		[Guid ("aa80e7f7-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfTextInputProcessor
		{
			[PreserveSig]
			Int32 Activate (ITfThreadMgr Mgr_ThreadMgr, UInt32 Id_ClientId);
			[PreserveSig]
			Int32 Deactivate ();
		}

		// Prefer this interface on Windows 10/11
		[GeneratedComInterface]
		[Guid ("6e4e2102-f9cd-433d-b496-303ce03a6507")]
		public partial interface ITfTextInputProcessorEx : ITfTextInputProcessor
		{
			[PreserveSig]
			Int32 ActivateEx (ITfThreadMgr Mgr_ThreadMgr, UInt32 Id_ClientId, UInt32 Flags_Activate);
		}

		// ITfInputProcessorProfiles lives in CSharpTSFInput.Core.Interop (shared
		// engine-agnostic registration interop).

		[GeneratedComInterface]
		[Guid ("aa80e7f0-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfKeystrokeMgr
		{
			// 3
			[PreserveSig]
			Int32 AdviseKeyEventSink (UInt32 tid, ITfKeyEventSink pSink, Int32 fForeground);
			// 4
			[PreserveSig]
			Int32 UnadviseKeyEventSink (UInt32 tid);
			// 5
			[PreserveSig]
			Int32 GetForeground (out Guid pclsid);
			// 6
			[PreserveSig]
			Int32 TestKeyDown (nuint wParam, nint lParam, out Int32 pfEaten);
			// 7
			[PreserveSig]
			Int32 TestKeyUp (nuint wParam, nint lParam, out Int32 pfEaten);
			// 8
			[PreserveSig]
			Int32 KeyDown (nuint wParam, nint lParam, out Int32 pfEaten);
			// 9
			[PreserveSig]
			Int32 KeyUp (nuint wParam, nint lParam, out Int32 pfEaten);
			// 10
			[PreserveSig]
			Int32 GetPreservedKey (nint pic, in TF_PRESERVEDKEY pprekey, out Guid pguid);
			// 11
			[PreserveSig]
			Int32 IsPreservedKey (in Guid rguid, in TF_PRESERVEDKEY pprekey, out Int32 pfRegistered);
			// 12
			[PreserveSig]
			unsafe Int32 PreserveKey (UInt32 tid, in Guid rguid, in TF_PRESERVEDKEY prekey, Char* pszDesc, UInt32 cchDesc);
			// 13
			[PreserveSig]
			Int32 UnpreserveKey (in Guid rguid, in TF_PRESERVEDKEY pprekey);
			// 14
			[PreserveSig]
			unsafe Int32 SetPreservedKeyDescription (in Guid rguid, Char* pszDesc, UInt32 cchDesc);
			// 15
			[PreserveSig]
			Int32 GetPreservedKeyDescription (in Guid rguid, out nint ppbstrDesc);
			// 16
			[PreserveSig]
			Int32 SimulatePreservedKey (nint pic, in Guid rguid, out Int32 pfEaten);
		}

		// ABI: BOOL = Int32, ITfContext → nint pic, UIntPtr/IntPtr → nuint/nint
		[GeneratedComInterface]
		[Guid ("aa80e7f5-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfKeyEventSink
		{
			// 3
			[PreserveSig]
			Int32 OnSetFocus (Int32 fForeground);
			// 4
			[PreserveSig]
			Int32 OnTestKeyDown (nint Context_Context, nuint wParam, nint lParam, out Int32 pfEaten);
			// 5
			[PreserveSig]
			Int32 OnTestKeyUp (nint Context_Context, nuint wParam, nint lParam, out Int32 pfEaten);
			// 6
			[PreserveSig]
			Int32 OnKeyDown (nint Context_Context, nuint wParam, nint lParam, out Int32 pfEaten);
			// 7
			[PreserveSig]
			Int32 OnKeyUp (nint Context_Context, nuint wParam, nint lParam, out Int32 pfEaten);
			// 8
			[PreserveSig]
			Int32 OnPreservedKey (nint Context_Context, in Guid rguid, out Int32 pfEaten);
		}

		[GeneratedComInterface]
		[Guid ("aa80e7fd-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfContext
		{
			// 3
			[PreserveSig]
			Int32 RequestEditSession (UInt32 Id_ClientId, ITfEditSession Session_EditSession, UInt32 Flags_Sync, out Int32 Result_HrSession);
			// 4
			[PreserveSig]
			Int32 InWriteSession (UInt32 Id_ClientId, out Int32 pfInWriteSession);
			// 5
			[PreserveSig]
			unsafe Int32 GetSelection (UInt32 Id_Ec, UInt32 Index_Index, UInt32 Count_Count, TF_SELECTION* Buffer_Selection, out UInt32 Count_Fetched);
			// 6
			[PreserveSig]
			unsafe Int32 SetSelection (UInt32 Id_Ec, UInt32 ulCount, TF_SELECTION* pSelection);
			// 7
			[PreserveSig]
			Int32 GetStart (UInt32 Id_Ec, out ITfRange Range_Start);
			// 8
			[PreserveSig]
			Int32 GetEnd (UInt32 Id_Ec, out ITfRange Range_End);
			// 9
			[PreserveSig]
			Int32 GetActiveView (out ITfContextView View_View);
			// 10
			[PreserveSig]
			Int32 EnumViews (out nint Enum_Views);
			// 11
			[PreserveSig]
			Int32 GetStatus (out TF_STATUS Status_Status);
			// 12
			[PreserveSig]
			Int32 GetProperty (in Guid Guid_Prop, out ITfProperty Prop_Prop);
			// 13
			[PreserveSig]
			Int32 GetAppProperty (in Guid Guid_Prop, out ITfReadOnlyProperty Prop_Prop);
			// 14
			[PreserveSig]
			Int32 TrackProperties (nint Ptr_Prop, UInt32 Count_Prop, nint Ptr_AppProp, UInt32 Count_AppProp, out ITfReadOnlyProperty Prop_Prop);
			// 15
			[PreserveSig]
			Int32 EnumProperties (out nint Enum_Props);
			// 16
			[PreserveSig]
			Int32 GetDocumentMgr (out ITfDocumentMgr DocMgr_DocMgr);
			// 17
			[PreserveSig]
			Int32 CreateRangeBackup (UInt32 Id_Ec, ITfRange Range_Range, out nint RangeBackup_Backup);
		}

		[GeneratedComInterface]
		[Guid ("2433bf8e-0f9b-435c-ba2c-180611978c30")]
		public partial interface ITfContextView
		{
			[PreserveSig]
			Int32 GetRangeFromPoint (UInt32 Id_Ec, in POINT Point_Pt, UInt32 Flags_Flags, out ITfRange Range_Range);
			[PreserveSig]
			Int32 GetTextExt (UInt32 Id_Ec, ITfRange Range_Range, out RECT Rect_Rc, out Int32 pfClipped);
			// 5
			[PreserveSig]
			Int32 GetScreenExt (out RECT prc);
			[PreserveSig]
			Int32 GetWnd (out nint Handle_Hwnd);
		}

		[GeneratedComInterface]
		[Guid ("2af2d06a-dd5b-4927-a0b4-54f19c91fade")]
		public partial interface ITfTextLayoutSink
		{
			[PreserveSig]
			Int32 OnLayoutChange (nint pic, TfLayoutCode lcode, ITfContextView pView);
		}

		[GeneratedComInterface]
		[Guid ("aa80e803-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfEditSession
		{
			[PreserveSig]
			Int32 DoEditSession (UInt32 Id_Ec);
		}

		[GeneratedComInterface]
		[Guid ("aa80e7f4-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfDocumentMgr
		{
			// 3
			[PreserveSig]
			Int32 CreateContext (UInt32 Id_ClientId, UInt32 Flags_Flags, nint Unknown_Unk, out ITfContext Context_Context, out UInt32 Id_EcTextStore);
			// 4
			[PreserveSig]
			Int32 Push (ITfContext pic);
			// 5
			[PreserveSig]
			Int32 Pop (UInt32 dwFlags);
			// 6
			[PreserveSig]
			Int32 GetTop (out ITfContext ppic);
			// 7
			[PreserveSig]
			Int32 GetBase (out ITfContext ppic);
			// 8
			[PreserveSig]
			Int32 EnumContexts (out nint ppEnum);
		}

		[GeneratedComInterface]
		[Guid ("a781718c-579a-4b15-a280-32b8577acc5e")]
		public partial interface ITfCompositionSink
		{
			[PreserveSig]
			Int32 OnCompositionTerminated (UInt32 Id_EcWrite, nint Composition_Composition);
		}

		[GeneratedComInterface]
		[Guid ("20168d64-5a8f-4a5a-b7bd-cfa29f4d0fd9")]
		public partial interface ITfComposition
		{
			[PreserveSig]
			Int32 GetRange (out ITfRange Range_Range);
			[PreserveSig]
			Int32 ShiftStart (UInt32 Id_EcWrite, ITfRange Range_NewStart);
			[PreserveSig]
			Int32 ShiftEnd (UInt32 Id_EcWrite, ITfRange Range_NewEnd);
			[PreserveSig]
			Int32 EndComposition (UInt32 Id_EcWrite);
		}

		[GeneratedComInterface]
		[Guid ("D7540241-F9A1-4364-BEFC-DBCD2C4395B7")]
		public partial interface ITfCompositionView
		{
			[PreserveSig]
			Int32 GetOwnerClsid (out Guid pclsid);
			[PreserveSig]
			Int32 GetRange (out ITfRange ppRange);
		}

		[GeneratedComInterface]
		[Guid ("5EFD22BA-7838-46CB-88E2-CADB14124F8F")]
		public partial interface IEnumITfCompositionView
		{
			[PreserveSig]
			Int32 Clone (out IEnumITfCompositionView ppEnum);
			[PreserveSig]
			unsafe Int32 Next (UInt32 ulCount, nint* rgCompositionView, out UInt32 pcFetched);
			[PreserveSig]
			Int32 Reset ();
			[PreserveSig]
			Int32 Skip (UInt32 ulCount);
		}

		[GeneratedComInterface]
		[Guid ("D40C8AAE-AC92-4FC7-9A11-0EE0E23AA39B")]
		public partial interface ITfContextComposition
		{
			[PreserveSig]
			Int32 StartComposition (UInt32 Id_EcWrite, ITfRange Range_Range, ITfCompositionSink Sink_Sink, out ITfComposition Composition_Composition);
			[PreserveSig]
			Int32 EnumCompositions (out IEnumITfCompositionView Enum_Enum);
			[PreserveSig]
			Int32 FindComposition (UInt32 Id_EcRead, ITfRange Range_Range, out IEnumITfCompositionView Enum_Enum);
			[PreserveSig]
			Int32 TakeOwnership (UInt32 Id_EcWrite, ITfCompositionView Composition_Composition, ITfCompositionSink Sink_Sink, out ITfComposition Composition_NewComposition);
		}

		[GeneratedComInterface]
		[Guid ("5F20AA40-B57A-4F34-96AB-3576F377CC79")]
		public partial interface ITfContextOwnerCompositionSink
		{
			[PreserveSig]
			Int32 OnStartComposition (ITfCompositionView pComposition, out Int32 pfOk);
			[PreserveSig]
			Int32 OnUpdateComposition (ITfCompositionView pComposition, ITfRange pRangeNew);
			[PreserveSig]
			Int32 OnEndComposition (ITfCompositionView pComposition);
		}

		[GeneratedComInterface]
		[Guid ("463a506d-6992-49d2-9b88-93d55e70bb16")]
		public partial interface ITfRangeBackup
		{
			[PreserveSig]
			Int32 Restore (UInt32 Id_Ec, ITfRange Range_Range);
		}

		[GeneratedComInterface]
		[Guid ("aa80e7ff-2021-11d2-93e0-0060b067b86e")]
		public partial interface ITfRange
		{
			// 3
			[PreserveSig]
			unsafe Int32 GetText (UInt32 Id_Ec, UInt32 Flags_Flags, void* Buffer_Text, UInt32 Count_CchMax, out UInt32 Count_Cch);
			// 4
			[PreserveSig]
			Int32 SetText (UInt32 Id_Ec, UInt32 Flags_Flags, nint Ptr_Text, Int32 Count_Cch);
			// 5
			[PreserveSig]
			Int32 GetFormattedText (UInt32 Id_Ec, out nint Data_DataObject);
			// 6
			[PreserveSig]
			Int32 GetEmbedded (UInt32 Id_Ec, in Guid Guid_Service, in Guid Guid_Riid, out nint Unknown_Unknown);
			// 7
			[PreserveSig]
			Int32 InsertEmbedded (UInt32 Id_Ec, UInt32 Flags_Flags, nint Unknown_Unknown);
			// 8
			[PreserveSig]
			Int32 ShiftStart (UInt32 Id_Ec, Int32 Count_Cch, out Int32 Count_Halt, ITfRangeBackup? Range_Backup);
			// 9
			[PreserveSig]
			Int32 ShiftEnd (UInt32 Id_Ec, Int32 Count_Cch, out Int32 Count_Halt, ITfRangeBackup? Range_Backup);
			// 10
			[PreserveSig]
			Int32 ShiftStartToRange (UInt32 Id_Ec, ITfRange Range_Range, UInt32 Anchor_A);
			// 11
			[PreserveSig]
			Int32 ShiftEndToRange (UInt32 Id_Ec, ITfRange Range_Range, UInt32 Anchor_A);
			// 12
			[PreserveSig]
			Int32 ShiftStartRegion (UInt32 Id_Ec, UInt32 Region_Dir, out Int32 pfNoRegion);
			// 13
			[PreserveSig]
			Int32 ShiftEndRegion (UInt32 Id_Ec, UInt32 Region_Dir, out Int32 pfNoRegion);
			// 14
			[PreserveSig]
			Int32 IsEmpty (UInt32 Id_Ec, out Int32 pfEmpty);
			// 15
			[PreserveSig]
			Int32 Collapse (UInt32 Id_Ec, UInt32 Anchor_A);
			// 16
			[PreserveSig]
			Int32 IsEqualStart (UInt32 Id_Ec, ITfRange Range_Range, UInt32 Anchor_A, out Int32 pfEqual);
			// 17
			[PreserveSig]
			Int32 IsEqualEnd (UInt32 Id_Ec, ITfRange Range_Range, UInt32 Anchor_A, out Int32 pfEqual);
			// 18
			[PreserveSig]
			Int32 CompareStart (UInt32 Id_Ec, ITfRange Range_Range, UInt32 Anchor_A, out Int32 Result_Result);
			// 19
			[PreserveSig]
			Int32 CompareEnd (UInt32 Id_Ec, ITfRange Range_Range, UInt32 Anchor_A, out Int32 Result_Result);
			// 20
			[PreserveSig]
			Int32 AdjustForInsert (UInt32 Id_Ec, UInt32 Count_Cch, out Int32 pfInsertOk);
			// 21
			[PreserveSig]
			Int32 GetGravity (out UInt32 Anchor_Start, out UInt32 Anchor_End);
			// 22
			[PreserveSig]
			Int32 SetGravity (UInt32 Id_Ec, UInt32 Anchor_Start, UInt32 Anchor_End);
			// 23
			[PreserveSig]
			Int32 Clone (out ITfRange Range_Clone);
			// 24
			[PreserveSig]
			Int32 GetContext (out ITfContext Context_Context);
		}

		[GeneratedComInterface]
		[Guid ("55ce16ba-3014-41c1-9ceb-fade1446ac6c")]
		public partial interface ITfInsertAtSelection
		{
			// 3
			[PreserveSig]
			Int32 InsertTextAtSelection (UInt32 Id_Ec, UInt32 Flags_Flags, nint Ptr_Text, UInt32 Count_Cch, out ITfRange Range_Range);
			// 4
			[PreserveSig]
			Int32 InsertEmbeddedAtSelection (UInt32 Id_Ec, UInt32 Flags_Flags, nint Unknown_Unknown, out ITfRange Range_Range);
		}

		[GeneratedComInterface]
		[Guid ("fee47777-163c-4769-996a-6e9c50ad8f54")]
		public partial interface ITfDisplayAttributeProvider
		{
			// 3
			[PreserveSig]
			Int32 EnumDisplayAttributeInfo (out IEnumTfDisplayAttributeInfo Enum_Info);
			// 4
			[PreserveSig]
			Int32 GetDisplayAttributeInfo (in Guid Guid_Info, out ITfDisplayAttributeInfo Info_Info);
		}

		[GeneratedComInterface]
		[Guid ("70528852-2f26-4aea-8c96-215150578932")]
		public partial interface ITfDisplayAttributeInfo
		{
			// 3
			[PreserveSig]
			Int32 GetGUID (out Guid Guid_Guid);
			// 4
			[PreserveSig]
			Int32 GetDescription (out nint Desc_Desc);
			// 5
			[PreserveSig]
			Int32 GetAttributeInfo (out TF_DISPLAYATTRIBUTE Info_Info);
			// 6
			[PreserveSig]
			Int32 SetAttributeInfo (in TF_DISPLAYATTRIBUTE Info_Info);
			// 7
			[PreserveSig]
			Int32 Reset ();
		}

		[GeneratedComInterface]
		[Guid ("7cef04d7-cb75-4e80-a7ab-5f5bc7d332de")]
		public partial interface IEnumTfDisplayAttributeInfo
		{
			// 3
			[PreserveSig]
			Int32 Clone (out IEnumTfDisplayAttributeInfo Enum_Enum);
			// 4
			[PreserveSig]
			unsafe Int32 Next (UInt32 Count_UlCount, void* Buffer_RgInfo, out UInt32 Count_PcFetched);
			// 5
			[PreserveSig]
			Int32 Reset ();
			// 6
			[PreserveSig]
			Int32 Skip (UInt32 Count_UlCount);
		}

		[GeneratedComInterface]
		[Guid ("17d49a3d-f8b8-4b2f-b254-52319dd64c53")]
		public partial interface ITfReadOnlyProperty
		{
			// 3
			[PreserveSig]
			Int32 GetPropertyType (out Guid pguid); 
			// 4
			[PreserveSig]
			Int32 EnumRanges (UInt32 ec, out nint ppEnum, ITfRange pTargetRange);
			// 5
			[PreserveSig]
			Int32 GetValue (UInt32 Id_Ec, ITfRange Range_Range, nint Var_Value);
			// 6
			[PreserveSig]
			Int32 GetContext (out nint ppContext);
		}

		[GeneratedComInterface]
		[Guid ("e2449660-9542-11d2-bf46-00105a2799b5")]
		public partial interface ITfProperty : ITfReadOnlyProperty
		{
			// 7
			[PreserveSig]
			Int32 FindRange (UInt32 Id_Ec, ITfRange Range_Range, out ITfRange Range_Empty, UInt32 Anchor_A);
			// 8
			[PreserveSig]
			Int32 SetValueStore (UInt32 Id_Ec, ITfRange Range_Range, nint Ptr_PropStore);
			// 9
			[PreserveSig]
			Int32 SetValue (UInt32 Id_Ec, ITfRange Range_Range, nint Var_Value);
			// 10
			[PreserveSig]
			Int32 Clear (UInt32 Id_Ec, ITfRange Range_Range);
		}

		// ITfCategoryMgr lives in CSharpTSFInput.Core.Interop (shared registration interop).

		// ITfInputProcessorProfileMgr lives in CSharpTSFInput.Core.Interop (shared
		// registration interop; uses Core's TF_INPUTPROCESSORPROFILE).

		#endregion COM Interfaces
	}
}

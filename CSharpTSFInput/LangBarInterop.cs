using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

// Language-bar (system-tray) input-mode button + the keyboard conversion-mode
// compartment. EVERY vtable method order and signature below matches the Windows SDK 10.0.26100
// headers (ctfutb.h / msctf.h) VERBATIM: a wrong slot order or param width crashes the HOST
// process (Word/Explorer) when Windows calls into this DLL's CCW.
//   - ctfutb.h: ITfLangBarItem / ITfLangBarItemButton / ITfLangBarItemSink / ITfLangBarItemMgr
//                + TF_LANGBARITEMINFO + TF_LBI_* flags + TfLBIClick.
//   - msctf.h:  ITfCompartmentMgr / ITfCompartment + VARIANT (VT_I4 conversion mode).
// guidItem = GUID_LBI_INPUTMODE is what makes Windows render the item in the tray; GetIcon returns
// the mode-specific HICON.

namespace CSharpTSFInput
{
	#region Structs / enums (namespace-level, matching RECT/POINT/TF_SELECTION)

	/// <summary>ctfutb.h TF_LANGBARITEMINFO — 104 bytes (CLSID 16 + GUID 16 + DWORD 4 + ULONG 4 + WCHAR[32] 64).</summary>
	[StructLayout (LayoutKind.Sequential, CharSet = CharSet.Unicode)]
	public unsafe struct TF_LANGBARITEMINFO
	{
		public Guid clsidService;
		public Guid guidItem;
		public uint dwStyle;
		public uint ulSort;
		public fixed char szDescription[32];
	}

	/// <summary>OLE VARIANT (x64 = 24 bytes). Only VT_I4 (lVal at offset 8) is used here.</summary>
	[StructLayout (LayoutKind.Explicit, Size = 24)]
	public struct VARIANT
	{
		[FieldOffset (0)] public ushort vt;
		[FieldOffset (8)] public int lVal;
	}

	public enum TfLBIClick : uint
	{
		TF_LBI_CLK_RIGHT = 1,
		TF_LBI_CLK_LEFT = 2
	}

	#endregion

	public static partial class NativeMethods
	{
		#region GUIDs / flags (SDK 10.0.26100 — locked against the SDK headers)

		public static readonly Guid GUID_LBI_INPUTMODE = new Guid ("2C77A81E-41CC-4178-A3A7-5F8A987568E6");
		public static readonly Guid GUID_COMPARTMENT_KEYBOARD_INPUTMODE_CONVERSION = new Guid ("CCF05DD8-4A87-11D7-A6E2-00065B84435C");
		// IIDs match SDK 10.0.26100 ctfutb.h VERBATIM, each by its own MIDL_INTERFACE→name
		// pairing (the Sink, Mgr and Item IIDs are easy to rotate by mistake).
		public static readonly Guid IID_ITfLangBarItemMgr = new Guid ("ba468c55-9956-4fb1-a59d-52a7dd7cc6aa");
		public static readonly Guid IID_ITfLangBarItemSink = new Guid ("57dbe1a0-de25-11d2-afdd-00105a2799b5");

		public const uint TF_LBI_STYLE_BTN_BUTTON = 0x00010000;
		public const uint TF_LBI_STYLE_SHOWNINTRAY = 0x00000002;
		public const uint TF_LBI_ICON = 0x00000001;
		public const uint TF_LBI_TEXT = 0x00000002;
		public const uint TF_LBI_STATUS = 0x00010000;

		public const int TF_CONVERSIONMODE_ALPHANUMERIC = 0; // EN passthrough
		public const int TF_CONVERSIONMODE_NATIVE = 1;        // Manchu (manju)
		public const ushort VT_I4 = 3;
		public const uint LR_DEFAULTCOLOR = 0x00000000;

		#endregion

		#region COM interfaces (ctfutb.h / msctf.h — DO NOT reorder methods)

		[GeneratedComInterface]
		[Guid ("73540d69-edeb-4ee9-96c9-23aa30b25916")]
		public partial interface ITfLangBarItem
		{
			[PreserveSig] Int32 GetInfo (out TF_LANGBARITEMINFO pInfo);
			[PreserveSig] Int32 GetStatus (out uint pdwStatus);
			[PreserveSig] Int32 Show (Int32 fShow);
			[PreserveSig] Int32 GetTooltipString (out nint pbstrToolTip); // BSTR*
		}

		[GeneratedComInterface]
		[Guid ("28c7f1d0-de25-11d2-afdd-00105a2799b5")]
		public partial interface ITfLangBarItemButton : ITfLangBarItem
		{
			[PreserveSig] Int32 OnClick (TfLBIClick click, POINT pt, in RECT prcArea);
			[PreserveSig] Int32 InitMenu (nint pMenu);   // ITfMenu* — unused (button has no menu)
			[PreserveSig] Int32 OnMenuSelect (uint wID);
			[PreserveSig] Int32 GetIcon (out nint phIcon); // HICON*
			[PreserveSig] Int32 GetText (out nint pbstrText); // BSTR*
		}

		[GeneratedComInterface]
		[Guid ("57dbe1a0-de25-11d2-afdd-00105a2799b5")]
		public partial interface ITfLangBarItemSink
		{
			[PreserveSig] Int32 OnUpdate (uint dwFlags);
		}

		// We only CALL AddItem / RemoveItem, but every preceding slot must exist in exact order so the
		// vtable offsets line up. Unused methods use blittable placeholder params (never invoked).
		[GeneratedComInterface]
		[Guid ("ba468c55-9956-4fb1-a59d-52a7dd7cc6aa")]
		public partial interface ITfLangBarItemMgr
		{
			[PreserveSig] Int32 EnumItems (out nint ppEnum);
			[PreserveSig] Int32 GetItem (in Guid rguid, out nint ppItem);
			[PreserveSig] Int32 AddItem (nint plbi);     // ITfLangBarItem*
			[PreserveSig] Int32 RemoveItem (nint plbi);  // ITfLangBarItem*
			[PreserveSig] Int32 AdviseItemSink (nint punk, out uint pdwCookie, in Guid rguidItem);
			[PreserveSig] Int32 UnadviseItemSink (uint dwCookie);
			[PreserveSig] Int32 GetItemFloatingRect (uint dwThreadId, in Guid rguid, out RECT prc);
			[PreserveSig] Int32 GetItemsStatus (uint ulCount, nint pguidItem, nint pdwStatus);
			[PreserveSig] Int32 GetItemNum (out uint pulCount);
			[PreserveSig] Int32 GetItems (uint ulCount, nint ppItem, nint pInfo, nint pdwStatus, out uint pcFetched);
			[PreserveSig] Int32 AdviseItemsSink (uint ulCount, nint ppunk, nint pguidItem, nint pdwCookie);
			[PreserveSig] Int32 UnadviseItemsSink (uint ulCount, nint pdwCookie);
		}

		[GeneratedComInterface]
		[Guid ("7dcf57ac-18ad-438b-824d-979bffb74b7c")]
		public partial interface ITfCompartmentMgr
		{
			[PreserveSig] Int32 GetCompartment (in Guid rguid, out ITfCompartment ppcomp);
			[PreserveSig] Int32 ClearCompartment (uint tid, in Guid rguid);
			[PreserveSig] Int32 EnumCompartments (out nint ppEnum);
		}

		[GeneratedComInterface]
		[Guid ("bb08f7a9-607a-4384-8623-056892b64371")]
		public partial interface ITfCompartment
		{
			[PreserveSig] Int32 SetValue (uint tid, in VARIANT pvarValue);
			[PreserveSig] Int32 GetValue (out VARIANT pvarValue);
		}

		#endregion

		#region icon / BSTR p/invokes

		[LibraryImport ("oleaut32.dll")]
		public static partial nint SysAllocString (nint psz);

		[LibraryImport ("user32.dll")]
		public static partial nint CreateIconFromResourceEx (nint presbits, uint dwResSize, int fIcon, uint dwVer, int cxDesired, int cyDesired, uint flags);

		[LibraryImport ("user32.dll")]
		public static partial int DestroyIcon (nint hIcon);

		// SM_CXSMICON = tray small-icon size (GetSystemMetrics is declared in NativeMethods.cs).
		// Used so the mode icon is built at the size the indicator actually shows (per-process DPI) instead of
		// a fixed 16×16 that gets upscaled (= blurry).
		public const int SM_CXSMICON = 49;

		#endregion
	}
}

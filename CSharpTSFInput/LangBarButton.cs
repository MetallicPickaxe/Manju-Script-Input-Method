using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CSharpTSFInput
{
	/// <summary>
	/// System-tray input-mode indicator (manju ↔ EN). A language-bar item button
	/// with guidItem = GUID_LBI_INPUTMODE so Windows renders it in the tray; <see cref="GetIcon"/>
	/// returns the mode-specific HICON. Also implements ITfSource so the language bar can register
	/// its ITfLangBarItemSink on us — we call <c>sink.OnUpdate(ICON|TEXT|STATUS)</c> from
	/// <see cref="NotifyUpdate"/> whenever the mode toggles (lone-Shift OR a click on this button)
	/// to force the tray to re-query the icon/text.
	///
	/// Mode state lives in <see cref="TextService"/> (backed by the keyboard conversion-mode
	/// compartment); this button only READS it via the injected delegate and asks TextService to
	/// toggle on click. A tray click is a reliable toggle even where the Shift-tap gesture isn't
	/// delivered to the TIP (e.g. Explorer's transient rename edit).
	///
	/// COM contract: the vtable order/signatures are fixed by ctfutb.h (see LangBarInterop.cs). All
	/// methods are [PreserveSig] HRESULT returns; never throw across the COM boundary.
	/// </summary>
	[GeneratedComClass]
	internal sealed partial class LangBarButton :
		NativeMethods.ITfLangBarItemButton,
		NativeMethods.ITfSource
	{
		private readonly Func<bool> _isEnglish;   // true = EN passthrough, false = manju
		private readonly Action _onClickToggle;
		private NativeMethods.ITfLangBarItemSink? _sink;
		private uint _sinkCookie;

		public LangBarButton (Func<bool> isEnglish, Action onClickToggle)
		{
			_isEnglish = isEnglish;
			_onClickToggle = onClickToggle;
		}

		/// <summary>Tell the language bar this item changed → it re-queries GetIcon/GetText/GetStatus.</summary>
		public void NotifyUpdate ()
		{
			try { _sink?.OnUpdate (NativeMethods.TF_LBI_ICON | NativeMethods.TF_LBI_TEXT | NativeMethods.TF_LBI_STATUS); }
			catch (Exception) { }
		}

		// ----------------------------- ITfLangBarItem -----------------------------

		public unsafe Int32 GetInfo (out TF_LANGBARITEMINFO pInfo)
		{
			pInfo = default;
			pInfo.clsidService = Globals.Clsid_TextService;
			pInfo.guidItem = NativeMethods.GUID_LBI_INPUTMODE;
			pInfo.dwStyle = NativeMethods.TF_LBI_STYLE_BTN_BUTTON | NativeMethods.TF_LBI_STYLE_SHOWNINTRAY;
			pInfo.ulSort = 0;
			const string desc = "Manju Input Mode";
			int n = Math.Min (desc.Length, 31);
			fixed (char* d = pInfo.szDescription)
			{
				for (int i = 0; i < n; i++) d[i] = desc[i];
				d[n] = '\0';
			}
			return NativeMethods.S_OK;
		}

		public Int32 GetStatus (out uint pdwStatus)
		{
			pdwStatus = 0; // not hidden / not disabled
			return NativeMethods.S_OK;
		}

		public Int32 Show (Int32 fShow) => NativeMethods.S_OK; // always shown in tray

		public Int32 GetTooltipString (out nint pbstrToolTip)
		{
			pbstrToolTip = AllocBstr (_isEnglish () ? "Input mode: English (passthrough)" : "Input mode: Manchu");
			return NativeMethods.S_OK;
		}

		// -------------------------- ITfLangBarItemButton --------------------------

		public Int32 OnClick (TfLBIClick click, POINT pt, in RECT prcArea)
		{
			// Either mouse button toggles the mode — a tray click that works regardless of whether
			// the host delivers the lone-Shift gesture to the TIP.
			try { _onClickToggle (); }
			catch (Exception) { }
			return NativeMethods.S_OK;
		}

		public Int32 InitMenu (nint pMenu) => NativeMethods.S_OK;   // button has no drop menu
		public Int32 OnMenuSelect (uint wID) => NativeMethods.S_OK;

		public Int32 GetIcon (out nint phIcon)
		{
			phIcon = IconLoader.LoadTrayIcon (_isEnglish () ? "en.ico" : "manju.ico");
			return phIcon != 0 ? NativeMethods.S_OK : NativeMethods.E_FAIL;
		}

		public Int32 GetText (out nint pbstrText)
		{
			pbstrText = AllocBstr (_isEnglish () ? "EN" : "ᠮ"); // short tray caption
			return NativeMethods.S_OK;
		}

		// ----- ITfSource: the language bar registers its ITfLangBarItemSink on this button -----

		public unsafe Int32 AdviseSink (in Guid riid, nint punk, out uint pdwCookie)
		{
			pdwCookie = 0;
			if (riid == NativeMethods.IID_ITfLangBarItemSink && punk != 0)
			{
				_sink = ComInterfaceMarshaller<NativeMethods.ITfLangBarItemSink>.ConvertToManaged ((void*)punk);
				_sinkCookie = 1;
				pdwCookie = _sinkCookie;
				return NativeMethods.S_OK;
			}
			return NativeMethods.CONNECT_E_CANNOTCONNECT;
		}

		public Int32 UnadviseSink (uint dwCookie)
		{
			if (dwCookie == _sinkCookie && _sinkCookie != 0)
			{
				_sink = null;
				_sinkCookie = 0;
				return NativeMethods.S_OK;
			}
			return NativeMethods.E_FAIL;
		}

		private static unsafe nint AllocBstr (string s)
		{
			fixed (char* p = s) { return NativeMethods.SysAllocString ((nint)p); }
		}
	}

	/// <summary>
	/// Builds an HICON from an embedded .ico (manifest resource). Parses the ICONDIR
	/// + first ICONDIRENTRY and hands the image bits to CreateIconFromResourceEx (no temp file, no
	/// rc.exe / Win32Resource — AOT-friendly via manifest resources). The caller (the language bar)
	/// owns and destroys the returned icon, per the ITfLangBarItemButton::GetIcon contract, so a
	/// fresh icon is created on each GetIcon call.
	/// </summary>
	internal static class IconLoader
	{
		public static unsafe nint LoadTrayIcon (string logicalName)
		{
			try
			{
				var asm = typeof (IconLoader).Assembly;
				using var s = asm.GetManifestResourceStream (logicalName);
				if (s == null) { return 0; }
				long len = s.Length;
				if (len < 22 || len > 1_000_000) return 0;
				byte[] buf = new byte[(int)len];
				int read = 0;
				while (read < buf.Length)
				{
					int r = s.Read (buf, read, buf.Length - read);
					if (r <= 0) break;
					read += r;
				}
				// ICONDIR: idReserved(2)=0, idType(2)=1, idCount(2). ICONDIRENTRY[i] at 6 + i*16:
				//   bWidth(1) bHeight(1) bColorCount(1) bReserved(1) wPlanes(2) wBitCount(2) dwBytesInRes(4) dwImageOffset(4)
				ushort idCount = BitConverter.ToUInt16 (buf, 4);
				if (idCount < 1 || 6 + idCount * 16 > buf.Length) return 0;

				// Build the icon at the tray's ACTUAL small-icon size (per-process DPI), and
				// pick the .ico image that best matches — the smallest image whose width >= the wanted size
				// (so we DOWN-sample, which is sharp), else the largest available. Always taking the
				// FIRST entry (16×16) as a 16×16 HICON would make Windows UP-sample it on a 20/24/32px
				// indicator (blurry, smeared). The .ico ships 16/20/24/32/40/48, so common DPIs get an exact size.
				int want = NativeMethods.GetSystemMetrics (NativeMethods.SM_CXSMICON);
				if (want <= 0) want = 16;
				int bestOff = -1; uint bestBytes = 0; int bestW = int.MaxValue;   // smallest width >= want
				int largeOff = -1; uint largeBytes = 0; int largeW = -1;          // largest width (fallback)
				for (int i = 0; i < idCount; i++)
				{
					int e = 6 + i * 16;
					int w = buf[e]; if (w == 0) w = 256;                          // bWidth 0 == 256
					uint bytes = BitConverter.ToUInt32 (buf, e + 8);
					uint off = BitConverter.ToUInt32 (buf, e + 12);
					if (off < 22 || bytes == 0 || (long)off + bytes > buf.Length) continue;
					if (w > largeW) { largeW = w; largeOff = (int)off; largeBytes = bytes; }
					if (w >= want && w < bestW) { bestW = w; bestOff = (int)off; bestBytes = bytes; }
				}
				int chosenOff = bestOff >= 0 ? bestOff : largeOff;
				uint chosenBytes = bestOff >= 0 ? bestBytes : largeBytes;
				if (chosenOff < 0) return 0;
				fixed (byte* p = &buf[chosenOff])
				{
					// 0x00030000 = icon image format version 3.0; fIcon = TRUE; create at the wanted size.
					nint h = NativeMethods.CreateIconFromResourceEx ((nint)p, chosenBytes, 1, 0x00030000, want, want, NativeMethods.LR_DEFAULTCOLOR);
					return h;
				}
			}
			catch (Exception) { return 0; }
		}
	}
}

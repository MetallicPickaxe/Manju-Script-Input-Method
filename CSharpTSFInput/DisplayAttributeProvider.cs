using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace CSharpTSFInput
{
	public partial class TextService : NativeMethods.ITfDisplayAttributeProvider
	{
		#region ITfDisplayAttributeProvider

		public Int32 EnumDisplayAttributeInfo (out NativeMethods.IEnumTfDisplayAttributeInfo Enum_Info)
		{
			Enum_Info = new ManjuEnumDisplayAttributeInfo ();
			return NativeMethods.S_OK;
		}

		public Int32 GetDisplayAttributeInfo (in Guid Guid_Info, out NativeMethods.ITfDisplayAttributeInfo Info_Info)
		{
			if (Guid_Info == Globals.Guid_DisplayAttribute)
			{
				Info_Info = new ManjuDisplayAttributeInfo ();
				return NativeMethods.S_OK;
			}
			else
			{
				Info_Info = null!;
				return NativeMethods.E_INVALIDARG;
			}
		}

		#endregion ITfDisplayAttributeProvider

		#region Helper Classes

		[GeneratedComClass]
		internal partial class ManjuDisplayAttributeInfo : NativeMethods.ITfDisplayAttributeInfo
		{
			public Int32 GetGUID (out Guid Guid_Guid) { Guid_Guid = Globals.Guid_DisplayAttribute; return NativeMethods.S_OK; }
			public Int32 GetDescription (out nint Desc_Desc)
			{
				Desc_Desc = Marshal.StringToBSTR ("Manju Display Attribute");
				return NativeMethods.S_OK;
			}

			/// <summary>
			/// The one thing this IME asks a host to draw around its composition.
			///
			/// THE LINE STYLE IS NOT DECORATION HERE. Apart from the composing text itself, this display
			/// attribute is the only thing the IME puts into a live composition, so it is the IME's only
			/// influence on the outline a host such as Word draws around the composition.
			///
			/// TF_LS_DOT, and why not the alternatives:
			///   · TF_LS_SOLID is a full-height rule, the most box-like style the IME could ask for.
			///   · TF_LS_NONE removes the signal for EVERY host, so the user could not see which
			///     text is still uncommitted. That trades one host's appearance problem for a global
			///     usability one.
			///   · TF_LS_DASH would also lighten the line, but a dashed rule is the heavier of the two
			///     and several hosts use dash/solid to distinguish the CONVERTED or TARGET clause from
			///     the raw one. This IME has no clause model, everything in the range is equally
			///     uncommitted, so borrowing that vocabulary would say something untrue.
			///   · TF_LS_DOT is the conventional "this is unconverted input" underline (it is what
			///     MS-IME's own un-converted clause looks like), and it is the lightest style that still
			///     reads as a line. It carries the semantic that matters: this run is still being
			///     composed.
			///
			/// WHETHER A HOST DRAWS A BOX around the composition is up to the host. This method decides
			/// only the request: the style is DOT, it is a line (never NONE), and it carries TF_ATTR_INPUT.
			/// </summary>
			public Int32 GetAttributeInfo (out TF_DISPLAYATTRIBUTE Info_Info)
			{
				Info_Info = new TF_DISPLAYATTRIBUTE {
					Color_Text = new TF_DA_COLOR { Type_Type = TF_DA_COLORTYPE.TF_CT_NONE, ColorRef = 0 },
					Color_Bk = new TF_DA_COLOR { Type_Type = TF_DA_COLORTYPE.TF_CT_NONE, ColorRef = 0 },
					Attr_LsStyle = TF_DA_LINESTYLE.TF_LS_DOT,   // the lightest style that still reads as a line
					fBoldLine = 0,    // FALSE (Int32)
					Color_Line = new TF_DA_COLOR { Type_Type = TF_DA_COLORTYPE.TF_CT_NONE, ColorRef = 0 },
					Attr_Attr = TF_DA_ATTR_INFO.TF_ATTR_INPUT
				};
				return NativeMethods.S_OK;
			}

			public Int32 SetAttributeInfo (in TF_DISPLAYATTRIBUTE Info_Info) { return NativeMethods.S_OK; }
			public Int32 Reset () { return NativeMethods.S_OK; }
		}

		[GeneratedComClass]
		internal partial class ManjuEnumDisplayAttributeInfo : NativeMethods.IEnumTfDisplayAttributeInfo
		{
			private int _index = 0;
			private ManjuDisplayAttributeInfo? _cachedInfo; // hold a reference so the GC cannot collect it
			
			public Int32 Clone (out NativeMethods.IEnumTfDisplayAttributeInfo Enum_Enum) { Enum_Enum = new ManjuEnumDisplayAttributeInfo (); return NativeMethods.S_OK; }
			
			public unsafe Int32 Next (UInt32 Count_UlCount, void* Buffer_RgInfo, out UInt32 Count_PcFetched)
			{
				Count_PcFetched = 0;
				if (_index == 0 && Count_UlCount > 0)
				{
					_cachedInfo ??= new ManjuDisplayAttributeInfo (); // created once, then held by _cachedInfo
					IntPtr pInfo = (IntPtr)ComInterfaceMarshaller<NativeMethods.ITfDisplayAttributeInfo>.ConvertToUnmanaged (_cachedInfo);
					((IntPtr*)Buffer_RgInfo)[0] = pInfo;
					_index++;
					Count_PcFetched = 1;
				}
				return NativeMethods.S_OK;
			}

			public Int32 Reset () { _index = 0; return NativeMethods.S_OK; }
			public Int32 Skip (UInt32 Count_UlCount) { _index += (int)Count_UlCount; return NativeMethods.S_OK; }
		}

		#endregion Helper Classes
	}
}

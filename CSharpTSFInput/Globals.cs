using System;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
// GetModuleHandleEx / GetModuleFileName live in the shared Core.Interop.
using CSharpTSFInput.Core.Interop;

// Runtime marshalling is disabled for the whole assembly, which avoids SYSLIB1051
[assembly: DisableRuntimeMarshalling]

namespace CSharpTSFInput
{
	internal static class Globals
	{
		#region Constant

		// 11A06A2B-EA6D-43D9-870C-ADF907750ACA
		public static readonly Guid Clsid_TextService = new (0x11A06A2B, 0xEA6D, 0x43D9, 0x87, 0x0C, 0xAD, 0xF9, 0x07, 0x75, 0x0A, 0xCA);

		// 1490E7F2-8B6B-41FA-B70A-BBEA2BA7A244
		public static readonly Guid Guid_Profile = new (0x1490E7F2, 0x8B6B, 0x41FA, 0xB7, 0x0A, 0xBB, 0xEA, 0x2B, 0xA7, 0xA2, 0x44);

		// Display Attribute GUID: 12EF9401-4C36-49A5-A228-F07DD6DD016B
		public static readonly Guid Guid_DisplayAttribute = new (0x12EF9401, 0x4C36, 0x49A5, 0xA2, 0x28, 0xF0, 0x7D, 0xD6, 0xDD, 0x01, 0x6B);

		// TSF Standard Property: GUID_PROP_ATTRIBUTE {83062428-5521-492e-bdad-afa4245f2045}
		public static readonly Guid Guid_Prop_Attribute = new (0x83062428, 0x5521, 0x492e, 0xbd, 0xad, 0xaf, 0xa4, 0x24, 0x5f, 0x20, 0x45);

		// The settings menu's preserved key, Ctrl+Shift+`: 512B8E50-8612-4546-AC58-C04919ED2FC2
		public static readonly Guid Guid_PreservedKey_SettingsMenu = new (0x512B8E50, 0x8612, 0x4546, 0xAC, 0x58, 0xC0, 0x49, 0x19, 0xED, 0x2F, 0xC2);

		public const String Desc_TextService = "Manju IME";
		public const String Model_TextService = "Apartment";
		/// <summary>The built-in fallback LANGID.
		/// Changing THIS value would strand an installed copy: it is
		/// registered under the old number and would no longer uninstall cleanly. To register elsewhere,
		/// set `install.language_identifier` in the settings file instead.</summary>
		public const UInt16 Id_Lang_Fallback = 0x0804;

		/// <summary>The LANGID the TSF profile is registered under.
		///
		/// THE NUMBER COMES FROM SETTINGS, NOT FROM THIS FILE: `install.language_identifier` in
		/// `Resource/Configuration/configuration.yaml`, four hex digits. Resolved ONCE, on first use,
		/// because registration and unregistration inside one process must not disagree.
		/// When the setting is absent or unreadable the fallback applies and
		/// <see cref="Id_Lang_Source"/> says which — it never falls back silently.</summary>
		public static UInt16 Id_Lang => _idLang ??= ResolveLangId ();

		/// <summary>Where <see cref="Id_Lang"/> came from.</summary>
		public static String Id_Lang_Source { get; private set; } = String.Empty;

		private static UInt16? _idLang;

		private static UInt16 ResolveLangId ()
		{
			UInt16 id;
			String source;
			String? readFrom = null;
			try
			{
				id = ResolveLangIdIn (System.IO.Path.GetDirectoryName (GetModulePath ()), out source, out readFrom);
			}
			catch (Exception ex)
			{
				id = Id_Lang_Fallback;
				source = $"FALLBACK - reading the setting threw: {ex.Message}";
			}
			Id_Lang_Source = source;
			return id;
		}

		/// <summary>The resolution itself, for the settings under <paramref name="dllDir"/>.
		///
		/// It reads and writes no static state, so one process can ask it about more than one
		/// directory. <see cref="Id_Lang"/> keeps the once-per-process cache; this is what that cache
		/// is filled from.</summary>
		/// <param name="source">"settings", or a sentence starting with "FALLBACK" that says why.</param>
		/// <param name="readFrom">The settings file the value came from; null on a fallback.</param>
		public static UInt16 ResolveLangIdIn (String? dllDir, out String source, out String? readFrom)
		{
			readFrom = null;
			try
			{
				if (dllDir == null)
				{
					source = "FALLBACK - the module directory could not be resolved";
					return Id_Lang_Fallback;
				}
				var store = new Configuration.ConfigurationStore ();
				store.Load (System.IO.Path.Combine (dllDir, "Resource", "Configuration"));
				String raw = store.GetString ("install.language_identifier", String.Empty).Trim ();
				if (raw.Length > 0 && UInt16.TryParse (raw, System.Globalization.NumberStyles.HexNumber,
													  System.Globalization.CultureInfo.InvariantCulture,
													  out UInt16 parsed))
				{
					source = "settings";
					readFrom = store.LoadedPath;
					return parsed;
				}
				source = raw.Length > 0
					? $"FALLBACK - install.language_identifier was '{raw}', which is not four hex digits"
					: "FALLBACK - install.language_identifier is not set";
			}
			catch (Exception ex)
			{
				source = $"FALLBACK - reading the setting threw: {ex.Message}";
			}
			return Id_Lang_Fallback;
		}
		public const Int32 Index_Icon = 0;

		public const String Name_InProcServer32 = "InProcServer32";
		public const String Name_ThreadingModel = "ThreadingModel";

		#endregion Constant

		#region Helper

		public static unsafe String GetModulePath ()
		{
			// In NativeAOT, the address of this method can be used to find the base address of the DLL
			IntPtr hModule;
			
			// GetModuleHandleEx with the FROM_ADDRESS flag returns the module that contains the
			// address of GetModulePathAddress, a static unmanaged method in this DLL
			delegate* unmanaged<void> pFunc = &GetModulePathAddress;
			if (TsfNative.GetModuleHandleEx (NativeMethods.GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, pFunc, out hModule))
			{
				Char[] Buffer_ModulePath = new Char[260];
				TsfNative.GetModuleFileName (hModule, Buffer_ModulePath, 260);
				return new String (Buffer_ModulePath).TrimEnd ('\0');
			}

			// Fallback for non-AOT environments
			return AppContext.BaseDirectory;
		}

		[UnmanagedCallersOnly]
		private static void GetModulePathAddress() { }

		#endregion Helper
	}
}

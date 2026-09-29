using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Runtime.CompilerServices;
// Shared engine-agnostic TSF bootstrap/registration interop lives in Core.
using CSharpTSFInput.Core.Interop;

namespace CSharpTSFInput
{
	public class DllExports
	{
		#region Field

		// hold the class factory permanently, so NativeAOT's GC cannot collect it
		private static readonly ClassFactory _factory = new ClassFactory ();

		#endregion Field
		
		#region VTable Definitions for direct COM calls
		
		// ITfCategoryMgr vtable (after IUnknown: QueryInterface=0, AddRef=1, Release=2)
		// Methods start at index 3
		private const int VTABLE_ITfCategoryMgr_RegisterCategory = 3;
		private const int VTABLE_ITfCategoryMgr_UnregisterCategory = 4;
		
		// ITfInputProcessorProfileMgr vtable (after IUnknown: 0=QueryInterface, 1=AddRef, 2=Release)
		// Methods per SDK msctf.h order:
		// 3: ActivateProfile
		// 4: DeactivateProfile
		// 5: GetProfile
		// 6: EnumProfiles
		// 7: ReleaseInputProcessor
		// 8: RegisterProfile
		// 9: UnregisterProfile
		// 10: GetActiveProfile
		private const int VTABLE_ITfInputProcessorProfileMgr_ActivateProfile = 3;
		private const int VTABLE_ITfInputProcessorProfileMgr_DeactivateProfile = 4;
		private const int VTABLE_ITfInputProcessorProfileMgr_GetProfile = 5;
		private const int VTABLE_ITfInputProcessorProfileMgr_EnumProfiles = 6;
		private const int VTABLE_ITfInputProcessorProfileMgr_ReleaseInputProcessor = 7;
		private const int VTABLE_ITfInputProcessorProfileMgr_RegisterProfile = 8;
		private const int VTABLE_ITfInputProcessorProfileMgr_UnregisterProfile = 9;
		private const int VTABLE_ITfInputProcessorProfileMgr_GetActiveProfile = 10;
		
		#endregion VTable Definitions

		#region Export

		[UnmanagedCallersOnly (EntryPoint = "DllGetClassObject", CallConvs = new[] { typeof (CallConvStdcall) })]
		public static unsafe Int32 DllGetClassObject (Guid* Ptr_Rclsid, Guid* Ptr_Riid, void** Ptr_Ppv)
		{
			try
			{
				if (Ptr_Ppv == null) return NativeMethods.E_INVALIDARG;
				*Ptr_Ppv = null;

				if (*Ptr_Rclsid != Globals.Clsid_TextService)
				{
					return NativeMethods.CLASS_E_CLASSNOTAVAILABLE;
				}

				// standard COM requirement: support IClassFactory or IUnknown
				if (*Ptr_Riid == typeof (IClassFactory).GUID || *Ptr_Riid == typeof (IUnknown).GUID)
				{
					// a static instance, so the lifetime is guaranteed
					void* Ptr_Factory = ComInterfaceMarshaller<IClassFactory>.ConvertToUnmanaged (_factory);
					*Ptr_Ppv = (void*)Ptr_Factory;
					return NativeMethods.S_OK;
				}

				return NativeMethods.E_NOINTERFACE;
			}
			catch (Exception)
			{
				return NativeMethods.E_FAIL;
			}
		}

		[UnmanagedCallersOnly (EntryPoint = "DllCanUnloadNow", CallConvs = new[] { typeof (CallConvStdcall) })]
		public static Int32 DllCanUnloadNow ()
		{
			// return S_FALSE (1) to stop the system unloading the DLL at will
			return NativeMethods.S_FALSE;
		}

		[UnmanagedCallersOnly (EntryPoint = "DllRegisterServer", CallConvs = new[] { typeof (CallConvStdcall) })]
		public static unsafe Int32 DllRegisterServer ()
		{
			try
			{
				if (!Register_Server ()) return NativeMethods.E_FAIL;
				if (!Register_Profiles ()) return NativeMethods.E_FAIL;
				if (!Register_Categories ()) return NativeMethods.E_FAIL;
				return NativeMethods.S_OK;
			}
			catch (Exception)
			{
				return NativeMethods.E_FAIL;
			}
		}

		[UnmanagedCallersOnly (EntryPoint = "DllUnregisterServer", CallConvs = new[] { typeof (CallConvStdcall) })]
		public static Int32 DllUnregisterServer ()
		{
			try
			{
				Unregister_Categories ();
				Unregister_Profiles ();
				Unregister_Server ();
				return NativeMethods.S_OK;
			}
			catch (Exception)
			{
				return NativeMethods.E_FAIL;
			}
		}

		#endregion Export

		#region Helper
		
		// Helper to get a vtable function pointer from a COM interface
		private static unsafe void* GetVTableMethod(void* pInterface, int methodIndex)
		{
			// COM interface pointer -> vtable pointer -> method pointer
			void** vtable = *(void***)pInterface;
			return vtable[methodIndex];
		}

		private static unsafe Boolean Register_Categories ()
		{
			Guid Clsid_CategoryMgr = NativeMethods.CLSID_TF_CategoryMgr;
			Guid Iid_CategoryMgr = typeof (ITfCategoryMgr).GUID;

			void* pCategoryMgr;
			Int32 hr = TsfNative.CoCreateInstance (&Clsid_CategoryMgr, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, &Iid_CategoryMgr, &pCategoryMgr);
			if (hr != NativeMethods.S_OK)
			{
				return false;
			}

			try
			{
				// Get vtable method pointer for RegisterCategory
				delegate* unmanaged[Stdcall]< void* , Guid* , Guid* , Guid* , Int32 > pRegisterCategory = (delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, Guid*, Int32>)GetVTableMethod(pCategoryMgr, VTABLE_ITfCategoryMgr_RegisterCategory);
				
				Guid clsid = Globals.Clsid_TextService;
				Guid cat1 = NativeMethods.GUID_TFCAT_TIP_KEYBOARD;
				Guid cat2 = NativeMethods.GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT;
				Guid cat3 = NativeMethods.GUID_TFCAT_TIPCAP_UIELEMENTENABLED;
				
				hr = pRegisterCategory(pCategoryMgr, &clsid, &cat1, &clsid);

				hr = pRegisterCategory(pCategoryMgr, &clsid, &cat2, &clsid);

				hr = pRegisterCategory(pCategoryMgr, &clsid, &cat3, &clsid);
			}
			finally
			{
				Marshal.Release ((IntPtr)pCategoryMgr);
			}

			return true;
		}

		private static unsafe void Unregister_Categories ()
		{
			Guid Clsid_CategoryMgr = NativeMethods.CLSID_TF_CategoryMgr;
			Guid Iid_CategoryMgr = typeof (ITfCategoryMgr).GUID;

			void* pCategoryMgr;
			if (TsfNative.CoCreateInstance (&Clsid_CategoryMgr, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, &Iid_CategoryMgr, &pCategoryMgr) != NativeMethods.S_OK)
				return;

			try
			{
				// Get vtable method pointer for UnregisterCategory
				delegate* unmanaged[Stdcall]< void* , Guid* , Guid* , Guid* , Int32 > pUnregisterCategory = (delegate* unmanaged[Stdcall]<void*, Guid*, Guid*, Guid*, Int32>)GetVTableMethod(pCategoryMgr, VTABLE_ITfCategoryMgr_UnregisterCategory);
				
				Guid clsid = Globals.Clsid_TextService;
				Guid cat1 = NativeMethods.GUID_TFCAT_TIP_KEYBOARD;
				Guid cat2 = NativeMethods.GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT;
				Guid cat3 = NativeMethods.GUID_TFCAT_TIPCAP_UIELEMENTENABLED;
				
				pUnregisterCategory(pCategoryMgr, &clsid, &cat1, &clsid);
				pUnregisterCategory(pCategoryMgr, &clsid, &cat2, &clsid);
				pUnregisterCategory(pCategoryMgr, &clsid, &cat3, &clsid);
			}
			finally
			{
				Marshal.Release ((IntPtr)pCategoryMgr);
			}
		}

		private static unsafe Boolean Register_Server ()
		{
			String Str_Clsid = Globals.Clsid_TextService.ToString ("B").ToUpper ();
			String Key_Name = $@"CLSID\{Str_Clsid}";

			nint hKey;
			UInt32 disposition;
			// 1. create the CLSID key
			if (TsfNative.RegCreateKeyEx (NativeMethods.HKEY_CLASSES_ROOT, Key_Name, 0, null, 0, NativeMethods.KEY_WRITE, 0, out hKey, out disposition) != NativeMethods.S_OK)
				return false;

			// set the default value to the description
			ReadOnlySpan<Char> desc = Globals.Desc_TextService;
			fixed (Char* pDesc = desc)
			{
				TsfNative.RegSetValueEx (hKey, null, 0, NativeMethods.REG_SZ, (nint)pDesc, (UInt32)(desc.Length + 1) * 2);
			}

			// 2. create the InProcServer32 subkey
			nint hSubKey;
			if (TsfNative.RegCreateKeyEx (hKey, Globals.Name_InProcServer32, 0, null, 0, NativeMethods.KEY_WRITE, 0, out hSubKey, out disposition) != NativeMethods.S_OK)
			{
				TsfNative.RegCloseKey (hKey);
				return false;
			}

			// get this DLL's own path
			String Str_Path = Globals.GetModulePath ();

			// set the DLL path
			ReadOnlySpan<Char> path = Str_Path;
			fixed (Char* pPath = path)
			{
				TsfNative.RegSetValueEx (hSubKey, null, 0, NativeMethods.REG_SZ, (nint)pPath, (UInt32)(path.Length + 1) * 2);
			}

			// set the ThreadingModel
			ReadOnlySpan<Char> model = Globals.Model_TextService;
			fixed (Char* pModel = model)
			{
				TsfNative.RegSetValueEx (hSubKey, Globals.Name_ThreadingModel, 0, NativeMethods.REG_SZ, (nint)pModel, (UInt32)(model.Length + 1) * 2);
			}

			TsfNative.RegCloseKey (hSubKey);
			TsfNative.RegCloseKey (hKey);

			return true;
		}

		private static unsafe Boolean Register_Profiles ()
		{
			Guid Clsid_Profiles = NativeMethods.CLSID_TF_InputProcessorProfiles;
			Guid Iid_ProfileMgr = typeof (ITfInputProcessorProfileMgr).GUID;

			void* pProfileMgr;
			Int32 hr = TsfNative.CoCreateInstance (&Clsid_Profiles, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, &Iid_ProfileMgr, &pProfileMgr);
			if (hr != NativeMethods.S_OK)
			{
				return false;
			}

			String Str_Path = Globals.GetModulePath ();
			String Str_Desc = Globals.Desc_TextService;

			// Manual string marshalling
			IntPtr Ptr_Desc = IntPtr.Zero;
			IntPtr Ptr_Path = IntPtr.Zero;

			try
			{
				Ptr_Desc = Marshal.StringToCoTaskMemUni (Str_Desc);
				Ptr_Path = Marshal.StringToCoTaskMemUni (Str_Path);

				// Get vtable method pointer for RegisterProfile
				delegate* unmanaged[Stdcall]< void* , Guid* , UInt16 , Guid* , nint , UInt32 , nint , UInt32 , UInt32 , nint , UInt32 , Int32 , UInt32 , Int32 > pRegisterProfile = (delegate* unmanaged[Stdcall]<void*, Guid*, UInt16, Guid*, IntPtr, UInt32, IntPtr, UInt32, UInt32, IntPtr, UInt32, Int32, UInt32, Int32>)
					GetVTableMethod(pProfileMgr, VTABLE_ITfInputProcessorProfileMgr_RegisterProfile);
				
				Guid clsid = Globals.Clsid_TextService;
				Guid profile = Globals.Guid_Profile;

				hr = pRegisterProfile(pProfileMgr, &clsid, Globals.Id_Lang, &profile,
					Ptr_Desc, (UInt32)Str_Desc.Length,
					Ptr_Path, (UInt32)Str_Path.Length,
					Globals.Index_Icon, IntPtr.Zero, 0, 1, 0);

				if (hr != NativeMethods.S_OK)
				{
					return false;
				}
			}
			finally
			{
				if (Ptr_Desc != IntPtr.Zero) Marshal.FreeCoTaskMem (Ptr_Desc);
				if (Ptr_Path != IntPtr.Zero) Marshal.FreeCoTaskMem (Ptr_Path);
				Marshal.Release ((IntPtr)pProfileMgr);
			}

			return true;
		}

		private static void Unregister_Server ()
		{
			String Str_Clsid = Globals.Clsid_TextService.ToString ("B").ToUpper ();
			String Key_Name = $@"CLSID\{Str_Clsid}";

			TsfNative.RegDeleteTree (NativeMethods.HKEY_CLASSES_ROOT, Key_Name);
		}

		private static unsafe void Unregister_Profiles ()
		{
			Guid Clsid_Profiles = NativeMethods.CLSID_TF_InputProcessorProfiles;
			Guid Iid_ProfileMgr = typeof (ITfInputProcessorProfileMgr).GUID;

			void* pProfileMgr;
			if (TsfNative.CoCreateInstance (&Clsid_Profiles, IntPtr.Zero, NativeMethods.CLSCTX_INPROC_SERVER, &Iid_ProfileMgr, &pProfileMgr) != NativeMethods.S_OK)
				return;

			try
			{
				// Get vtable method pointer for UnregisterProfile
				delegate* unmanaged[Stdcall]< void* , Guid* , UInt16 , Guid* , UInt32 , Int32 > pUnregisterProfile = (delegate* unmanaged[Stdcall]<void*, Guid*, UInt16, Guid*, UInt32, Int32>)
					GetVTableMethod(pProfileMgr, VTABLE_ITfInputProcessorProfileMgr_UnregisterProfile);
				
				Guid clsid = Globals.Clsid_TextService;
				Guid profile = Globals.Guid_Profile;
				
				pUnregisterProfile(pProfileMgr, &clsid, Globals.Id_Lang, &profile, 0);
			}
			finally
			{
				Marshal.Release ((IntPtr)pProfileMgr);
			}
		}

		#endregion Helper
	}
}

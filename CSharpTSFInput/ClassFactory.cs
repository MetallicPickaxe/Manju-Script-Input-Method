using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading;
// IClassFactory / IUnknown live in the shared Core. The activation interfaces
// (ITfTextInputProcessor[Ex]) are Manju-local (engine-coupled) and use the NativeMethods. prefix.
using CSharpTSFInput.Core.Interop;

namespace CSharpTSFInput
{
	/// <summary>
	/// A global strong-reference holder that keeps managed objects from being collected
	/// Under NativeAOT a COM object only stays alive while managed code holds a strong reference to it
	/// </summary>
	internal static class GCProtection
	{
		private static readonly HashSet<Object> _aliveObjects = [];
		private static readonly Lock _lock = new (); // .NET 9 Lock

		/// <summary>
		/// Add an object to the protection list so the GC cannot collect it
		/// </summary>
		public static void KeepAlive (Object obj)
		{
			if (obj == null) return;
			lock (_lock)
			{
				_aliveObjects.Add (obj);
			}
		}

		/// <summary>
		/// Remove from the protection list (the GC may collect it again)
		/// </summary>
		public static void Release (Object obj)
		{
			if (obj == null) return;
			lock (_lock)
			{
				_aliveObjects.Remove (obj);
			}
		}
	}

	[GeneratedComClass]
	public partial class ClassFactory : IClassFactory
	{
		#region Method

		public unsafe Int32 CreateInstance (IntPtr Ptr_UnkOuter, Guid* Ptr_Riid, void** Ptr_PpvObject)
		{
			try
			{
				if (Ptr_UnkOuter != IntPtr.Zero)
				{
					return NativeMethods.CLASS_E_NOAGGREGATION;
				}

				if (Ptr_PpvObject == null)
				{
					return NativeMethods.E_POINTER;
				}

				*Ptr_PpvObject = null;


				// Check if the requested IID is IUnknown, ITfTextInputProcessor, or ITfTextInputProcessorEx
				Guid iidUnknown = typeof (IUnknown).GUID;
				Guid iidProcessor = typeof (NativeMethods.ITfTextInputProcessor).GUID;
				Guid iidProcessorEx = typeof (NativeMethods.ITfTextInputProcessorEx).GUID;
				
				if (*Ptr_Riid == iidUnknown || *Ptr_Riid == iidProcessor || *Ptr_Riid == iidProcessorEx || *Ptr_Riid == Globals.Clsid_TextService)
				{

					// Create our TextService
					TextService Service_Text = new();

					// The key GC protection: keep the TextService instance from being collected
					GCProtection.KeepAlive (Service_Text);

					// Get the ITfTextInputProcessorEx pointer (it includes ITfTextInputProcessor methods)
					void* Ptr_Service = ComInterfaceMarshaller<NativeMethods.ITfTextInputProcessorEx>.ConvertToUnmanaged (Service_Text);

					
					*Ptr_PpvObject = (void*)Ptr_Service;
					return NativeMethods.S_OK;
				}

				return NativeMethods.E_NOINTERFACE;
			}
			catch (Exception)
			{
				return NativeMethods.E_FAIL;
			}
		}

		public Int32 LockServer (Int32 Flag_Lock)
		{
			// Server locking is not implemented: LockServer always returns S_OK
			return NativeMethods.S_OK;
		}

		#endregion Method
	}
}
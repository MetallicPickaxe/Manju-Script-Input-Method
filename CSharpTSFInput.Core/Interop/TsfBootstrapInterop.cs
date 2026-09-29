using System;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

// NOTE: every interop declaration below is [GeneratedComInterface] / [LibraryImport] — i.e. uses
// source-generated marshalling, which is independent of the assembly-level DisableRuntimeMarshalling
// switch. So this library does NOT need that attribute; the generated marshalling is byte-identical
// with or without it. (Each AOT TIP host keeps its own [assembly: DisableRuntimeMarshalling].)

namespace CSharpTSFInput.Core.Interop
{
    // ------------------------------------------------------------------------------------------
    // Engine-agnostic TSF COM-bootstrap + registration interop.
    //
    // This is the shared, engine-NEUTRAL subset of TSF/COM interop that ANY in-proc TIP needs to
    // (a) expose its class object (IClassFactory / IUnknown) and (b) register itself as a text
    // service (profile + category managers, registry, module path). It has ZERO dependency on any
    // engine type (no document-manager / context / range / composition / key-sink graph), so it is
    // cleanly shareable and locally verifiable via regsvr32 register/unregister round-trip.
    //
    // Deliberately NOT here: the ACTIVATION interfaces (ITfThreadMgr / ITfTextInputProcessor[Ex])
    // and the whole edit/composition/range/sink engine graph. Those are engine-coupled in the host
    // (the host stores a typed ITfThreadMgr and QIs it to ITfSource / ITfKeystrokeMgr / event sinks),
    // so they belong to the host.
    // A passthrough TIP that just needs to be activated declares its own minimal nint-based
    // ITfTextInputProcessorEx (3 trivial methods) — that is not a copy of the host's engine face.
    // ------------------------------------------------------------------------------------------

    /// <summary>TF_INPUTPROCESSORPROFILE — blittable; used by <see cref="ITfInputProcessorProfileMgr"/>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TF_INPUTPROCESSORPROFILE
    {
        public UInt32 dwProfileType;
        public UInt16 langid;
        public UInt16 pad0;
        public Guid clsid;
        public Guid guidProfile;
        public Guid catid;
        public nint hklSubstitute;
        public UInt32 dwCaps;
        public UInt32 pad1; // align HKL to 8-byte boundary
        public nint hkl;
        public UInt32 dwFlags;
        public UInt32 pad2; // total size 88 bytes for x64
    }

    /// <summary>
    /// Engine-agnostic native constants, TSF registration GUIDs, and COM/registry/module P/Invokes
    /// </summary>
    public static partial class TsfNative
    {
        // --- COM HRESULTs ---
        public const Int32 S_OK = 0;
        public const Int32 S_FALSE = 1;
        public const Int32 E_FAIL = unchecked((Int32)0x80004005);
        public const Int32 E_INVALIDARG = unchecked((Int32)0x80070057);
        public const Int32 E_NOINTERFACE = unchecked((Int32)0x80004002);
        public const Int32 E_POINTER = unchecked((Int32)0x80004003);
        public const Int32 E_NOTIMPL = unchecked((Int32)0x80004001);
        public const Int32 CLASS_E_NOAGGREGATION = unchecked((Int32)0x80040110);
        public const Int32 CLASS_E_CLASSNOTAVAILABLE = unchecked((Int32)0x80040111);

        // --- COM activation / registry ---
        public const UInt32 CLSCTX_INPROC_SERVER = 0x1;
        public const UInt32 REG_SZ = 1;
        public const UInt32 KEY_WRITE = 0x20006;
        public static readonly nint HKEY_CLASSES_ROOT = unchecked((Int32)0x80000000);
        public const UInt32 GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS = 0x00000004;

        // --- TSF category / well-known CLSIDs (for registration) ---
        public static readonly Guid GUID_TFCAT_TIP_KEYBOARD = new Guid("34745c63-b2f0-4784-8b67-5e12c8701a31");
        public static readonly Guid GUID_TFCAT_TIPCAP_IMMERSIVESUPPORT = new Guid("13A016DF-560B-46CD-947A-4C3AF1E0E35D");
        public static readonly Guid GUID_TFCAT_TIPCAP_UIELEMENTENABLED = new Guid("49D2F9CE-1F5E-11D7-A6D3-00065B84435C");
        public static readonly Guid CLSID_TF_CategoryMgr = new Guid("A4B544A1-438D-4B41-9325-869523E2D6C7");
        public static readonly Guid CLSID_TF_InputProcessorProfiles = new Guid("33C53A50-F456-4884-B049-85FD643ECFED");

        // --- COM creation ---
        [LibraryImport("ole32.dll", EntryPoint = "CoCreateInstance", SetLastError = true)]
        public static unsafe partial Int32 CoCreateInstance(
            Guid* Clsid_Rclsid,
            nint Unknown_PUnkOuter,
            UInt32 Context_DwClsContext,
            Guid* Iid_Riid,
            void** Ppv_Ppv);

        // --- Registry (registration / unregistration) ---
        [LibraryImport("advapi32.dll", EntryPoint = "RegCreateKeyExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        public static partial Int32 RegCreateKeyEx(
            nint hKey,
            String lpSubKey,
            UInt32 Reserved,
            String? lpClass,
            UInt32 dwOptions,
            UInt32 samDesired,
            nint lpSecurityAttributes,
            out nint phkResult,
            out UInt32 lpdwDisposition);

        [LibraryImport("advapi32.dll", EntryPoint = "RegSetValueExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        public static partial Int32 RegSetValueEx(
            nint hKey,
            String? lpValueName,
            UInt32 Reserved,
            UInt32 dwType,
            nint lpData,
            UInt32 cbData);

        [LibraryImport("advapi32.dll", EntryPoint = "RegCloseKey", SetLastError = true)]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        public static partial Int32 RegCloseKey(nint hKey);

        [LibraryImport("advapi32.dll", EntryPoint = "RegDeleteTreeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
        public static partial Int32 RegDeleteTree(nint hKey, String? lpSubKey);

        // --- Module path (each host gets its OWN module: Core is AOT-linked into each TIP DLL,
        //     so the from-address probe resolves to the calling host's native image). ---
        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        [System.Runtime.InteropServices.SuppressGCTransition]
        public static unsafe partial Boolean GetModuleHandleEx(UInt32 Flags, void* Ptr_Address, out nint Handle_Module);

        [LibraryImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [System.Runtime.InteropServices.SuppressGCTransition]
        public static partial UInt32 GetModuleFileName(nint Handle_Module, [Out] Char[] Buffer_Filename, UInt32 Size_Size);
    }

    // ------------------------------------------------------------------------------------------
    // COM interfaces (engine-agnostic). Do NOT reorder methods or change signatures or
    // marshalling: the TSF/COM ABI is frozen.
    // ------------------------------------------------------------------------------------------

    [GeneratedComInterface]
    [Guid("00000000-0000-0000-C000-000000000046")]
    public partial interface IUnknown { }

    [GeneratedComInterface]
    [Guid("00000001-0000-0000-C000-000000000046")]
    public partial interface IClassFactory
    {
        [PreserveSig]
        unsafe Int32 CreateInstance(nint Unknown_UnkOuter, Guid* Iid_Riid, void** Ppv_Object);
        [PreserveSig]
        Int32 LockServer(Int32 Flag_FLock);
    }

    [GeneratedComInterface]
    [Guid("1F02B6C5-7842-4EE6-8A0B-9A24183A95CA")]
    public partial interface ITfInputProcessorProfiles
    {
        [PreserveSig]
        Int32 Register(in Guid rclsid);
        [PreserveSig]
        Int32 Unregister(in Guid rclsid);
        [PreserveSig]
        Int32 AddLanguageProfile(
            in Guid rclsid,
            UInt16 langid,
            in Guid guidProfile,
            nint pchDesc,
            UInt32 cchDesc,
            nint pchIconFile,
            UInt32 cchFile,
            UInt32 uIconIndex);
        [PreserveSig]
        Int32 RemoveLanguageProfile(in Guid rclsid, UInt16 langid, in Guid guidProfile);
        [PreserveSig]
        Int32 EnumInputProcessorInfo(out nint ppEnum);
        [PreserveSig]
        Int32 GetDefaultLanguageProfile(UInt16 langid, in Guid catid, out Guid pclsid, out Guid pguidProfile);
        [PreserveSig]
        Int32 SetDefaultLanguageProfile(UInt16 langid, in Guid rclsid, in Guid guidProfile);
        [PreserveSig]
        Int32 ActivateLanguageProfile(in Guid rclsid, UInt16 langid, in Guid guidProfile);
        [PreserveSig]
        Int32 GetActiveLanguageProfile(in Guid rclsid, out UInt16 plangid, out Guid pguidProfile);
        [PreserveSig]
        Int32 GetLanguageProfileDescription(in Guid rclsid, UInt16 langid, in Guid guidProfile, out nint pbstrDesc);
        [PreserveSig]
        Int32 GetCurrentLanguage(out UInt16 plangid);
        [PreserveSig]
        Int32 ChangeCurrentLanguage(UInt16 langid);
        [PreserveSig]
        Int32 GetLanguageList(out nint ppLangId, out UInt32 pulCount);
        [PreserveSig]
        Int32 EnumLanguageProfiles(UInt16 langid, out nint ppEnum);
        [PreserveSig]
        Int32 EnableLanguageProfile(in Guid rclsid, UInt16 langid, in Guid guidProfile, Int32 fEnable);
        [PreserveSig]
        Int32 IsEnabledLanguageProfile(in Guid rclsid, UInt16 langid, in Guid guidProfile, out Int32 pfEnabled);
        [PreserveSig]
        Int32 EnableLanguageProfileByDefault(in Guid rclsid, UInt16 langid, in Guid guidProfile, Int32 fEnable);
        [PreserveSig]
        Int32 SubstituteKeyboardLayout(in Guid rclsid, UInt16 langid, in Guid guidProfile, nint hkl);
    }

    [GeneratedComInterface]
    [Guid("c3acefb5-f69d-4905-938f-fcadcf4be830")]
    public partial interface ITfCategoryMgr
    {
        [PreserveSig]
        Int32 RegisterCategory(in Guid rclsid, in Guid rcatid, in Guid rguid);
        [PreserveSig]
        Int32 UnregisterCategory(in Guid rclsid, in Guid rcatid, in Guid rguid);
        [PreserveSig]
        Int32 EnumCategoriesInItem(in Guid rclsid, out nint ppEnum);
        [PreserveSig]
        Int32 EnumItemsInCategory(in Guid rcatid, out nint ppEnum);
        [PreserveSig]
        unsafe Int32 FindClosestCategory(in Guid rguid, out Guid pcatid, Guid** ppcatidList, UInt32 ulCount);
        [PreserveSig]
        Int32 RegisterGUIDDescription(in Guid rclsid, in Guid rguid, nint pszDesc, UInt32 cch);
        [PreserveSig]
        Int32 UnregisterGUIDDescription(in Guid rclsid, in Guid rguid);
        [PreserveSig]
        Int32 GetGUIDDescription(in Guid rguid, out nint pbstrDesc);
        [PreserveSig]
        Int32 RegisterGUIDDWORD(in Guid rclsid, in Guid rguid, UInt32 dw);
        [PreserveSig]
        Int32 UnregisterGUIDDWORD(in Guid rclsid, in Guid rguid);
        [PreserveSig]
        Int32 GetGUIDDWORD(in Guid rguid, out UInt32 pdw);
        [PreserveSig]
        Int32 RegisterGUID(in Guid rguid, out UInt32 pguidid);
        [PreserveSig]
        Int32 GetGUID(UInt32 guidid, out Guid pguid);
        [PreserveSig]
        Int32 IsEqualTfGuidAtom(UInt32 guidid, in Guid rguid, out Int32 pfEqual);
    }

    [GeneratedComInterface]
    [Guid("71c6e74c-0f28-11d8-a82a-00065b84435c")]
    public partial interface ITfInputProcessorProfileMgr
    {
        [PreserveSig]
        Int32 ActivateProfile(UInt32 dwProfileType, UInt16 langid, in Guid rclsid, in Guid guidProfile, nint hkl, UInt32 dwFlags);
        [PreserveSig]
        Int32 DeactivateProfile(UInt32 dwProfileType, UInt16 langid, in Guid rclsid, in Guid guidProfile, nint hkl, UInt32 dwFlags);
        [PreserveSig]
        Int32 GetProfile(UInt32 dwProfileType, UInt16 langid, in Guid rclsid, in Guid guidProfile, nint hkl, out TF_INPUTPROCESSORPROFILE pProfile);
        [PreserveSig]
        Int32 EnumProfiles(UInt16 langid, out nint ppEnum);
        [PreserveSig]
        Int32 ReleaseInputProcessor(in Guid rclsid, UInt32 dwFlags);
        [PreserveSig]
        Int32 RegisterProfile(
            in Guid rclsid,
            UInt16 langid,
            in Guid guidProfile,
            nint pszDesc,
            UInt32 cchDesc,
            nint pszIconFile,
            UInt32 cchIconFile,
            UInt32 uIconIndex,
            nint hklDefault,
            UInt32 dwPreferredLayout,
            Int32 bEnabledByDefault,
            UInt32 dwFlags);
        [PreserveSig]
        Int32 UnregisterProfile(in Guid rclsid, UInt16 langid, in Guid guidProfile, UInt32 dwFlags);
        [PreserveSig]
        Int32 GetActiveProfile(in Guid catid, out TF_INPUTPROCESSORPROFILE pProfile);
    }
}

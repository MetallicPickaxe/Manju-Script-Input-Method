using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using CSharpTSFInput.Core.Interop;

namespace ManjuInstaller
{
    /// <summary>
    /// ACTIVATE THE PROFILE THROUGH TSF, so no sign-out is needed.
    ///
    /// Activating TSF uses hand-written COM interop, and this repository is hand-written interop
    /// end to end, so that is the ordinary road.
    ///
    /// THE INTEROP IS NOT DECLARED HERE, DELIBERATELY. `CSharpTSFInput.Core.Interop` already carries
    /// `ITfInputProcessorProfileMgr` with `[PreserveSig]` on every one of its eight methods, in vtable
    /// order, plus `CoCreateInstance` and the CLSID. A second declaration would risk the two drifting
    /// and would sit outside the solution-wide ABI check that already covers the Core one.
    /// This file consumes that interop; it adds no COM declarations of its own.
    ///
    /// WHY `[PreserveSig]` MATTERS AND IS NOT COSMETIC: without it the source generator invents an
    /// HRESULT check and a retval pointer, so the value that comes back differs from run to run. Every
    /// method on the Core interface carries it, and a method added there needs it too.
    ///
    /// THERE IS NO `IsAlreadyActive` SHORT-CUT HERE. The script's `IsAlreadyActive` calls
    /// `GetActiveProfile` with the category GUID `{34745C3B-38C2-11D2-93E5-0060B067B86E}`, while the
    /// documented `GUID_TFCAT_TIP_KEYBOARD` that Core registers with, out of msctf.h, is
    /// `{34745c63-b2f0-4784-8b67-5e12c8701a31}`. Those are different GUIDs, so the script's check can
    /// only ever fail and fall through to activating. The observable behaviour of the script is
    /// "always activate", and that is what this does. Activating an already-active profile is a no-op.
    /// </summary>
    public static class TsfActivation
    {
        private const UInt32 TF_PROFILETYPE_INPUTPROCESSOR = 0x0001;

        /// <summary>FORSESSION is what makes this take effect without a sign-out, which is why the
        /// script has a step 5 at all instead of telling the user to log out.</summary>
        private const UInt32 TF_IPPMF_FORSESSION = 0x20000000;

        private static readonly StrategyBasedComWrappers Com = new();

        /// <summary>Returns the HRESULT. Never throws: the script treats a failed activation as a
        /// warning ("you may need to switch to it by hand"), not as a failed install, and so does this.</summary>
        public static unsafe Int32 Activate(Guid clsid, Guid profile, UInt16 langid, List<string> log)
        {
            Guid clsidProfiles = TsfNative.CLSID_TF_InputProcessorProfiles;
            Guid iid = typeof(ITfInputProcessorProfileMgr).GUID;

            void* raw = null;
            Int32 hr = TsfNative.CoCreateInstance(
                &clsidProfiles, 0, TsfNative.CLSCTX_INPROC_SERVER, &iid, &raw);
            if (hr != TsfNative.S_OK || raw == null)
            {
                log.Add($"activate: CoCreateInstance(TF_InputProcessorProfiles) failed, HR=0x{hr:X8}");
                return hr;
            }

            object? rcw = null;
            try
            {
                rcw = Com.GetOrCreateObjectForComInstance((nint)raw, CreateObjectFlags.UniqueInstance);
                var mgr = (ITfInputProcessorProfileMgr)rcw;
                hr = mgr.ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, langid,
                                         in clsid, in profile, 0, TF_IPPMF_FORSESSION);
                log.Add(hr == TsfNative.S_OK
                    ? "activate: active, and showing in the language bar"
                    : $"activate: ActivateProfile HR=0x{hr:X8} — it may need to be switched to by hand");
                return hr;
            }
            catch (Exception ex)
            {
                log.Add($"activate: skipped, {ex.Message}");
                return TsfNative.E_FAIL;
            }
            finally
            {
                // Two references exist and both go back: the one CoCreateInstance handed out, and the
                // one the wrapper took. Dropping the managed reference alone releases neither.
                if (rcw is ComObject co) { try { co.FinalRelease(); } catch { } }
                if (raw != null) Marshal.Release((nint)raw);
            }
        }
    }
}

# Fallback uninstaller. The normal route is Uninstall Manju IME.exe in the Installer folder. This
# script exists for two reasons: it is the fallback when the exe cannot run, and Install_MANJU_IME.ps1
# runs it first to remove any previous installation.
# It takes no parameters. CmdletBinding makes any argument given to it an error that stops it
# before it changes anything; without it PowerShell would pass such an argument over.
[CmdletBinding()]
param()
$ErrorActionPreference = "Stop"

# ── WHICH COPY IS INSTALLED ─────────────────────────────────────────────────────
# The DLL Windows has registered as this input method’s COM server, or $null when none is. That copy
# is the one removed, wherever it is: setup can install a copy into another folder.
function Get-ManjuRegisteredDll {
    param([string]$Clsid)
    $key = "Registry::HKEY_CLASSES_ROOT\CLSID\$Clsid\InProcServer32"
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $dll = (Get-ItemProperty -LiteralPath $key).'(default)'
    if ([string]::IsNullOrWhiteSpace($dll)) { return $null }
    return $dll.Trim()
}

# ── WHICH WINDOWS LANGUAGE THIS INPUT METHOD IS REGISTERED UNDER ─────────────────
# Setup writes it into the settings file beside the DLL it registers (install.language_identifier), so it is read
# from there: the registered DLL’s settings file first, then the ones this script can reach.
# A missing or unreadable setting falls back to the built-in default AND SAYS SO, never silently.
# The language the registration itself carries: the first subkey of
# CTF\TIP\{CLSID}\LanguageProfile (0x00000c09 is 0C09). $null when there is no registration.
function Get-ManjuRegisteredLangId {
    param([string]$Clsid)
    $key = "Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\CTF\TIP\$Clsid\LanguageProfile"
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $name = @(Get-ChildItem -LiteralPath $key -ErrorAction SilentlyContinue | ForEach-Object { $_.PSChildName } |
              Where-Object { $_ -match '^0x[0-9A-Fa-f]{8}$' }) | Select-Object -First 1
    if ($name) { return $name.Substring($name.Length - 4).ToUpperInvariant() }
    return $null
}

function Resolve-ManjuLangId {
    param([string]$RegisteredDll, [string]$Clsid)
    $fallback = '0804'
    $pattern = '(?m)^\s*language_identifier\s*:\s*"?([0-9A-Fa-f]{4})"?\s*(?:#.*)?$'
    #   0. beside the registered DLL, and when that copy’s settings name no language (its folder was
    #      deleted after the install, say) the registration itself: a fallback here would leave the
    #      language-list entry of a copy registered under another language
    #   1. beside me:           a flat delivery
    #   2. ..\Runtime:          the ONE shared payload of a three-directory delivery
    if ($RegisteredDll) {
        $own = Join-Path (Split-Path $RegisteredDll -Parent) 'Resource\Configuration\configuration.yaml'
        if (Test-Path -LiteralPath $own) {
            $m = [regex]::Match((Get-Content -Raw -LiteralPath $own), $pattern)
            # SAY WHICH FILE IT CAME FROM, on success too.
            if ($m.Success) {
                Write-Host "    [langid] $($m.Groups[1].Value) from settings ($own)" -ForegroundColor Gray
                return $m.Groups[1].Value
            }
        }
        $registered = Get-ManjuRegisteredLangId -Clsid $Clsid
        if ($registered) {
            Write-Host "    [langid] $registered from the registration (CTF\TIP LanguageProfile): no language in $own" -ForegroundColor Gray
            return $registered
        }
        if (Test-Path -LiteralPath $own) {
            Write-Host "    [langid] no install.language_identifier in $own - falling back to $fallback" -ForegroundColor Yellow
            return $fallback
        }
    }
    $probes = @(
        (Join-Path $PSScriptRoot 'Resource\Configuration\configuration.yaml'),
        (Join-Path $PSScriptRoot '..\Runtime\Resource\Configuration\configuration.yaml')
    )
    foreach ($probe in $probes) {
        if (-not (Test-Path -LiteralPath $probe)) { continue }
        $m = [regex]::Match((Get-Content -Raw -LiteralPath $probe), $pattern)
        if ($m.Success) {
            Write-Host "    [langid] $($m.Groups[1].Value) from settings ($probe)" -ForegroundColor Gray
            return $m.Groups[1].Value
        }
        Write-Host "    [langid] no install.language_identifier in $probe - falling back to $fallback" -ForegroundColor Yellow
        return $fallback
    }
    Write-Host "    [langid] no settings file found - falling back to $fallback" -ForegroundColor Yellow
    return $fallback
}


function Resolve-FirstExistingPath {
    param(
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates | Where-Object { $_ } | Select-Object -Unique) {
        if (Test-Path $candidate) {
            # ProviderPath, not Path: on a network share Path is prefixed with
            # Microsoft.PowerShell.Core\FileSystem::, which regsvr32 cannot open. There the `..` also
            # stays in, so GetFullPath takes it out.
            return [IO.Path]::GetFullPath((Resolve-Path $candidate).ProviderPath)
        }
    }

    return $null
}


# The Windows programs this script starts, by their full paths in System32. Started by the file
# name alone, a program would be looked for first in the current folder, where a file of that name
# could have been placed.
$System32 = Join-Path $env:SystemRoot 'System32'

# --- Elevation check ---
$IsAdmin =([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin) {
    Write-Host "[ERROR] Run this script as administrator." -ForegroundColor Red
    exit 1
}

$Clsid = "{11A06A2B-EA6D-43D9-870C-ADF907750ACA}"
$Profile = "{1490E7F2-8B6B-41FA-B70A-BBEA2BA7A244}"
$RegisteredDll = Get-ManjuRegisteredDll -Clsid $Clsid
if ($RegisteredDll) {
    Write-Host "    [installed] $RegisteredDll" -ForegroundColor Gray
} else {
    Write-Host "    [installed] nothing is registered; clearing what a removed copy may have left" -ForegroundColor Gray
}
$LangID = Resolve-ManjuLangId -RegisteredDll $RegisteredDll -Clsid $Clsid
$Tip = "${LangID}:${Clsid}${Profile}"
$DllName = "CSharpTSFInput.dll"

Write-Host ">>> [Manju IME] Uninstalling..." -ForegroundColor Yellow


# --- 0. Switch away through the TSF API, so the active IME is not the one removed ---
Write-Host ">>> Switching to a system input method..."
try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

[ComImport, Guid("71C6E74C-0F28-11D8-A82A-00065B84435C")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface ITfInputProcessorProfileMgr {
    int ActivateProfile(uint dwProfileType, ushort langid, [In] ref Guid clsid, [In] ref Guid guidProfile, IntPtr hkl, uint dwFlags);
    int DeactivateProfile(uint dwProfileType, ushort langid, [In] ref Guid clsid, [In] ref Guid guidProfile, IntPtr hkl, uint dwFlags);
    int GetProfile(uint dwProfileType, ushort langid, [In] ref Guid clsid, [In] ref Guid guidProfile, IntPtr hkl, out IntPtr ppProfile);
    int EnumProfiles(ushort langid, out IntPtr ppEnum);
    int ReleaseInputProcessor([In] ref Guid clsid);
    int RegisterProfile([In] ref Guid clsid, ushort langid, [In] ref Guid guidProfile, [In, MarshalAs(UnmanagedType.LPWStr)] string pchDesc, uint cchDesc, [In, MarshalAs(UnmanagedType.LPWStr)] string pchIconFile, uint cchIconFile, uint uIconIndex, IntPtr hklsubstitute, uint dwPreferredLayout, int fEnabledByDefault, uint dwFlags);
    int UnregisterProfile([In] ref Guid clsid, ushort langid, [In] ref Guid guidProfile, uint dwFlags);
    int GetActiveProfile([In] ref Guid guidCat, out TF_INPUTPROCESSORPROFILE pProfile);
}

[StructLayout(LayoutKind.Sequential)]
public struct TF_INPUTPROCESSORPROFILE {
    public uint dwProfileType;
    public ushort langid;
    public Guid clsid;
    public Guid guidProfile;
    public Guid catid;
    public IntPtr hklSubstitute;
    public uint dwCaps;
    public IntPtr hkl;
    public uint dwFlags;
}

[ComImport, Guid("33C53A50-F456-4884-B049-85FD643ECFED")]
public class TF_InputProcessorProfiles { }

public static class TsfDeactivator {
    public const uint TF_PROFILETYPE_INPUTPROCESSOR = 0x0001;
    public const uint TF_IPPMF_FORSESSION = 0x20000000;
    public static readonly Guid GUID_TFCAT_TIP_KEYBOARD = new Guid("34745C3B-38C2-11D2-93E5-0060B067B86E");

    public static bool IsCurrentlyActive(string clsidStr, string profileStr) {
        try {
            var mgr = (ITfInputProcessorProfileMgr)new TF_InputProcessorProfiles();
            TF_INPUTPROCESSORPROFILE active;
            var guidCat = GUID_TFCAT_TIP_KEYBOARD;
            int hr = mgr.GetActiveProfile(ref guidCat, out active);
            if (hr == 0) {
                return active.clsid.ToString("B").Equals(clsidStr, StringComparison.OrdinalIgnoreCase) &&
                       active.guidProfile.ToString("B").Equals(profileStr, StringComparison.OrdinalIgnoreCase);
            }
        } catch { }
        return false;
    }
    
    public static int DeactivateForSession(string clsidStr, string profileStr, ushort langid) {
        var mgr = (ITfInputProcessorProfileMgr)new TF_InputProcessorProfiles();
        var clsid = new Guid(clsidStr);
        var profile = new Guid(profileStr);
        return mgr.DeactivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, langid, ref clsid, ref profile, IntPtr.Zero, TF_IPPMF_FORSESSION);
    }

    public static void CleanUpProfile(string clsidStr, string profileStr, ushort langid) {
        var mgr = (ITfInputProcessorProfileMgr)new TF_InputProcessorProfiles();
        var clsid = new Guid(clsidStr);
        var profile = new Guid(profileStr);
        
        try {
            mgr.UnregisterProfile(ref clsid, langid, ref profile, 0);
        } catch { }
        
        try {
            mgr.ReleaseInputProcessor(ref clsid);
        } catch { }
    }
}
"@ -ErrorAction SilentlyContinue

    if ([TsfDeactivator]::IsCurrentlyActive($Clsid, $Profile)) {
        $hr = [TsfDeactivator]::DeactivateForSession($Clsid.Trim('{}'), $Profile.Trim('{}'), [Convert]::ToUInt16($LangID, 16))
        if ($hr -eq 0) {
            Write-Host "    Manju IME deactivated." -ForegroundColor Green
        }
    } else {
        Write-Host "    Not active. Nothing to deactivate." -ForegroundColor Gray
    }
    
    Write-Host "    Deep cleanup (UnregisterProfile / ReleaseInputProcessor)..." -ForegroundColor Gray
    [TsfDeactivator]::CleanUpProfile($Clsid.Trim('{}'), $Profile.Trim('{}'), [Convert]::ToUInt16($LangID, 16))
    
} catch {
    # Ignore the error and keep uninstalling
}

# --- 1. Remove from the language list ---
Write-Host ">>> Removing from the language list..."
try {
    $langList = Get-WinUserLanguageList
    $modified = $false
    foreach ($lang in $langList) {
        if ($lang.InputMethodTips -contains $Tip) {
            $lang.InputMethodTips.Remove($Tip) | Out-Null
            $modified = $true
        }
    }
    if ($modified) {
        Set-WinUserLanguageList $langList -Force 2>$null
        Write-Host "    Removed from the language list." -ForegroundColor Green
    } else {
        Write-Host "    Not present in the language list." -ForegroundColor Gray
    }
} catch {
    Write-Warning "Language-list step skipped: $($_.Exception.Message)"
}

# --- Locate the DLL: the registered one first, then several fallback paths ---
$DllCandidates = @(
    $RegisteredDll,
    (Join-Path $PSScriptRoot $DllName),
    (Join-Path $PSScriptRoot "..\Runtime\$DllName")
)
$DllPath = Resolve-FirstExistingPath $DllCandidates

# 2. Standard unregister (regsvr32 /u)
if ($DllPath -and (Test-Path $DllPath)) {
    Write-Host ">>> Unregistering (regsvr32 /u)..."
    $Proc = Start-Process (Join-Path $System32 'regsvr32.exe') -ArgumentList "/u /s `"$DllPath`"" -PassThru -Wait
    if ($Proc.ExitCode -eq 0) {
        Write-Host "    Done." -ForegroundColor Green
    } else {
        Write-Warning "    Exit code $($Proc.ExitCode). Falling back to a forced cleanup."
    }
} else {
    Write-Warning "    DLL not found. Skipping the standard unregister."
}

# 3. Clear HKCU leftovers. This one matters: a stale Enable=0 overrides a later install.
Write-Host ">>> Clearing HKCU leftovers..."
$HkcuPath = "HKCU:\SOFTWARE\Microsoft\CTF\TIP\$Clsid"
if (Test-Path $HkcuPath) {
    Remove-Item -Path $HkcuPath -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "    Deleted: $HkcuPath" -ForegroundColor Yellow
}
# Clear this input method’s entry under whichever language of the User Profile still holds it
$UserProfilePath = "HKCU:\Control Panel\International\User Profile"
if (Test-Path $UserProfilePath) {
    foreach ($languageKey in (Get-ChildItem -LiteralPath $UserProfilePath -ErrorAction SilentlyContinue)) {
        Remove-ItemProperty -LiteralPath $languageKey.PSPath -Name $Tip -ErrorAction SilentlyContinue
    }
}

# 4. Forced HKLM cleanup, as a backstop
Write-Host ">>> Clearing HKLM leftovers..."
$RegKeys = @(
    "HKLM\SOFTWARE\Classes\CLSID\$Clsid",
    "HKLM\SOFTWARE\Microsoft\CTF\TIP\$Clsid"
)

foreach ($Key in $RegKeys) {
    $PsPath = $Key -replace "HKLM", "Registry::HKEY_LOCAL_MACHINE"
    if (Test-Path $PsPath) {
        Write-Host "    Deleted: $Key" -ForegroundColor Yellow
        & (Join-Path $System32 'reg.exe') delete "$Key" /f 2>$null | Out-Null
    }
}

# Force the shell language bar to refresh
try {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public class ShellNotify {
    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint Msg, IntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
    public const uint WM_SETTINGCHANGE = 0x001A;
    public const uint SMTO_ABORTIFHUNG = 0x0002;
    public static void NotifyLanguageChange() {
        IntPtr result;
        SendMessageTimeout((IntPtr)0xFFFF, WM_SETTINGCHANGE, IntPtr.Zero, "intl", SMTO_ABORTIFHUNG, 1000, out result);
    }
}
"@ -ErrorAction SilentlyContinue
    [ShellNotify]::NotifyLanguageChange()
} catch { }

# Restart ctfmon to force a TSF cache refresh.
# Only the ctfmon of this script’s own session: Get-Process lists the ctfmon of every signed-in
# user, and the others are left alone.
Write-Host ">>> Restarting ctfmon in this session to refresh the cache..."
$Session = (Get-Process -Id $PID).SessionId
$ctfmon = @(Get-Process -Name ctfmon -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $Session })
if ($ctfmon.Count -gt 0) {
    Stop-Process -Id $ctfmon.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}
Start-Process (Join-Path $System32 'ctfmon.exe') -ErrorAction SilentlyContinue

Write-Host ">>> Uninstalled." -ForegroundColor Green
Write-Host "Note: if a system process still holds the DLL, sign out or restart to release the file." -ForegroundColor Yellow

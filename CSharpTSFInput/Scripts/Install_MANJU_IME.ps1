# Fallback installer. The normal route is Install Manju IME.exe in the Installer folder: a wizard that
# asks for the language and the folder. This script installs where the delivery is or into the folder
# given with -InstallPath, under the language given with -LanguageIdentifier or the default one, and is
# the fallback when the exe cannot run.
# CmdletBinding makes a parameter this script does not know, a misspelled one included, an error
# that stops it before it changes anything; without it PowerShell would pass such an argument over.
# -L and -P are the short names of -LanguageIdentifier and -InstallPath.
[CmdletBinding()]
param(
    # Print the ordered operation plan and do nothing else.
    [switch]$WhatIf,
    # The Windows language to register under, as the four hex digits of its language identifier
    # (LANGID: 0409, 0804, …). It must already be in this user’s Windows language list. Default: the
    # Windows display language when it is in the list, otherwise the first language of the list.
    [Alias('L')]
    [string]$LanguageIdentifier,
    # The folder to install into, as a full path. Default: where this delivery is. A folder given here
    # first receives a copy of the delivery’s Runtime, Installer and Script folders; the language is
    # written into that copy’s settings file and that copy is registered.
    [Alias('P')]
    [string]$InstallPath
)

# One plan line: Op|Target|Value. The emitter sits next to each real operation and the
# operation itself is guarded by $WhatIf, so what is printed is what would be done.
$script:PlanLines = New-Object System.Collections.Generic.List[string]
function Emit-Step {
    param([string]$Op, [string]$Target, [string]$Value = "")
    $script:PlanLines.Add("$Op|$Target|$Value")
}
function Write-PlanAndExit {
    foreach ($l in $script:PlanLines) { Write-Output $l }
    exit 0
}

$ErrorActionPreference = "Stop"

# The Windows programs this script starts, by their full paths in System32. Started by the file
# name alone, a program would be looked for first in the current folder, where a file of that name
# could have been placed.
$System32 = Join-Path $env:SystemRoot 'System32'

# ── WHICH WINDOWS LANGUAGE THIS INPUT METHOD REGISTERS UNDER ───────────────────
# Only a language already in this user’s Windows language list, never a new one: adding a language
# makes Windows download and install it. The list is read with Get-WinUserLanguageList, the cmdlet
# step 4 writes back through, so the entry chosen here is the entry step 4 finds by its tag.
# The LANGID of a language is the one its own input methods are registered under: the part of each
# InputMethodTips entry before the colon. A language that has no language identifier of its own is left
# out: its LANGID is one of the fourteen of [MS-LCID] 2.2.1, "Locale Names without LCIDs" (the transient
# ones Windows assigns per user, and the two custom ones). Windows gives such a language a temporary
# identifier and does not keep an input method under it.
$NoOwnLangIds = @('2000', '2400', '2800', '2C00', '3000', '3400', '3800', '3C00', '4000', '4400', '4800', '4C00', '0C00', '1000')
function Get-ManjuInstalledLanguages {
    $list = Get-WinUserLanguageList
    foreach ($lang in $list) {
        $id = @($lang.InputMethodTips | ForEach-Object { ($_ -split ':')[0] })[0]
        if ($id -notmatch '^[0-9A-Fa-f]{4}$') { continue }
        $id = $id.ToUpperInvariant()
        if ($id -in $NoOwnLangIds) { continue }
        [pscustomobject]@{ Tag = $lang.LanguageTag; LangId = $id; Name = $lang.LocalizedName }
    }
}

# The Windows display language of this user, as a LANGID: the default when it is in the list.
function Get-ManjuDisplayLanguage {
    Add-Type -Namespace ManjuSetup -Name Native -ErrorAction SilentlyContinue -MemberDefinition `
        '[DllImport("kernel32.dll")] public static extern ushort GetUserDefaultUILanguage();'
    return [int][ManjuSetup.Native]::GetUserDefaultUILanguage()
}

# Sets install.language_identifier in the settings file beside the DLL; the DLL reads it while it registers itself,
# and the uninstaller reads it back. Only the value of that one line changes: every other byte stays,
# the line endings and a byte order mark included. A file without the line gets it under its install:
# key, or a new install: block at the end.
function Set-ManjuLangIdSetting {
    param([string]$Path, [string]$LangId)
    # Assigned in two statements: an empty array that comes out of an if expression arrives as $null.
    $bytes = [byte[]]@()
    if (Test-Path -LiteralPath $Path) { $bytes = [IO.File]::ReadAllBytes($Path) }
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $skip = if ($bom) { 3 } else { 0 }
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $text = $utf8.GetString($bytes, $skip, $bytes.Length - $skip)
    $nl = if ($text.Contains("`r`n")) { "`r`n" } else { "`n" }
    $value = '"' + $LangId.ToUpperInvariant() + '"'
    $line = [regex]::Match($text, '(?m)^([ \t]*language_identifier[ \t]*:[ \t]*)[^\r\n]*')
    if ($line.Success) {
        $text = $text.Substring(0, $line.Index) + $line.Groups[1].Value + $value + $text.Substring($line.Index + $line.Length)
    } else {
        $install = [regex]::Match($text, '(?m)^install[ \t]*:[^\r\n]*')
        if ($install.Success) {
            $end = $install.Index + $install.Length
            $text = $text.Substring(0, $end) + $nl + '  language_identifier: ' + $value + $text.Substring($end)
        } else {
            $lead = if ($text.Length -eq 0 -or $text.EndsWith("`n")) { '' } else { $nl }
            $text = $text + $lead + 'install:' + $nl + '  language_identifier: ' + $value + $nl
        }
    }
    New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force | Out-Null
    $out = New-Object System.Collections.Generic.List[byte]
    if ($bom) { $out.AddRange([byte[]](0xEF, 0xBB, 0xBF)) }
    $out.AddRange($utf8.GetBytes($text))
    [IO.File]::WriteAllBytes($Path, $out.ToArray())
}

# ── WHERE THE INPUT METHOD IS INSTALLED: IN PLACE, OR A COPY IN -InstallPath ──────────────
# The paths are made with .NET, not with
# Join-Path or Resolve-Path: those ask the file system, and under -WhatIf the copy does not exist yet.
$ManjuDeliveryFolders = 'Runtime', 'Installer', 'Script'
# Written out
# here, because Windows PowerShell’s older .NET gives three more.
$ManjuInvalidPathCharacters = [char[]](@([char]0x7C) + @(0..31 | ForEach-Object { [char]$_ }))

# The DLL Windows has registered as this input method’s COM server, or $null when none is.
function Get-ManjuRegisteredDll {
    param([string]$Clsid)
    $key = "Registry::HKEY_CLASSES_ROOT\CLSID\$Clsid\InProcServer32"
    if (-not (Test-Path -LiteralPath $key)) { return $null }
    $dll = (Get-ItemProperty -LiteralPath $key).'(default)'
    if ([string]::IsNullOrWhiteSpace($dll)) { return $null }
    return $dll.Trim()
}

# The full form of a path, without a trailing separator.
function Get-ManjuFullPath {
    param([string]$Path)
    $full = [IO.Path]::GetFullPath($Path)
    if ($full.Length -gt 3) { $full = $full.TrimEnd('\') }
    return $full
}

function Test-ManjuSamePath {
    param([string]$A, [string]$B)
    return [string]::Equals((Get-ManjuFullPath $A), (Get-ManjuFullPath $B), [StringComparison]::OrdinalIgnoreCase)
}

# True when $Path is $Root or lies inside it.
function Test-ManjuPathUnder {
    param([string]$Path, [string]$Root)
    $p = (Get-ManjuFullPath $Path).TrimEnd('\') + '\'
    $r = (Get-ManjuFullPath $Root).TrimEnd('\') + '\'
    return $p.StartsWith($r, [StringComparison]::OrdinalIgnoreCase)
}

# $Path relative to $Root, which holds it.
function Get-ManjuRelativePath {
    param([string]$Root, [string]$Path)
    return (Get-ManjuFullPath $Path).Substring(((Get-ManjuFullPath $Root).TrimEnd('\') + '\').Length)
}

# A path Windows does not resolve against a current folder: a drive letter, a colon and a separator,
# or two leading separators (a network share, or \\?\).
function Test-ManjuFullyQualified {
    param([string]$Path)
    # -cmatch: a case-insensitive match would let a letter such as the Kelvin sign pass as a drive.
    return ($Path -cmatch '^[\\/][\\/?]') -or ($Path -cmatch '^[A-Za-z]:[\\/]')
}

# $InstalledDll is the DLL Windows has registered, or $null.
function Get-ManjuTargetRefusal {
    param([string]$Source, [string]$Dll, [string]$Target, [string]$InstalledDll)
    if ([string]::IsNullOrWhiteSpace($Target) -or -not (Test-ManjuFullyQualified $Target.Trim())) {
        return 'Enter a full path, such as D:\Manju IME.'
    }
    $Target = $Target.Trim()
    $invalid = 'The folder name contains characters Windows does not allow.'
    if ($Target.IndexOfAny($ManjuInvalidPathCharacters) -ge 0 -or $Target.Contains('*') -or $Target.Contains('?')) {
        return $invalid
    }
    try {
        if (Test-ManjuSamePath $Source $Target) { return $null }
        foreach ($folder in $ManjuDeliveryFolders) {
            if (-not [IO.Directory]::Exists([IO.Path]::Combine($Source, $folder))) {
                return "This copy has no $folder folder, so it can only be installed where it is."
            }
        }
        if (-not (Test-ManjuPathUnder $Dll $Source)) {
            return 'The input method of this copy is not inside it, so it can only be installed where it is.'
        }
        foreach ($folder in $ManjuDeliveryFolders) {
            if (Test-ManjuPathUnder $Target ([IO.Path]::Combine($Source, $folder))) {
                return "That folder is inside the $folder folder of this copy. Choose a folder outside it."
            }
        }
        if ($InstalledDll -and (Test-ManjuPathUnder $InstalledDll $Target)) {
            return 'Manju IME is installed in that folder, and Windows may still be using its files. Choose another folder, or install where this copy is.'
        }
        if ([IO.File]::Exists($Target)) { return 'That is a file, not a folder.' }
    } catch {
        # Windows PowerShell’s .NET also refuses < > and " while it makes a full path.
        return $invalid
    }
    return $null
}

# Copies the delivery’s three folders from $Source into $Target, file by file, replacing files of the
# same name, and returns how many files it copied. It deletes nothing, writes nothing outside $Target,
# and follows no junction or symbolic link out of the delivery.
function Copy-ManjuDelivery {
    param([string]$Source, [string]$Target)
    $root = Get-ManjuFullPath $Target
    $count = 0
    foreach ($folder in $ManjuDeliveryFolders) {
        $count += Copy-ManjuTree -From ([IO.Path]::Combine($Source, $folder)) -To ([IO.Path]::Combine($root, $folder)) -Root $root
    }
    return $count
}

function Copy-ManjuTree {
    param([string]$From, [string]$To, [string]$Root)
    if (-not (Test-ManjuPathUnder $To $Root)) { throw "$To is outside $Root; nothing is written there." }
    [void][IO.Directory]::CreateDirectory($To)
    $count = 0
    foreach ($file in [IO.Directory]::EnumerateFiles($From)) {
        [IO.File]::Copy($file, [IO.Path]::Combine($To, [IO.Path]::GetFileName($file)), $true)
        $count++
    }
    foreach ($dir in [IO.Directory]::EnumerateDirectories($From)) {
        if (([IO.File]::GetAttributes($dir) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
        $count += Copy-ManjuTree -From $dir -To ([IO.Path]::Combine($To, [IO.Path]::GetFileName($dir))) -Root $Root
    }
    return $count
}


function Resolve-FirstExistingPath {
    param(
        [string[]]$Candidates
    )

    foreach ($candidate in $Candidates | Where-Object { $_ } | Select-Object -Unique) {
        if (Test-Path $candidate) {
            # ProviderPath, not Path: on a network share Path is prefixed with
            # Microsoft.PowerShell.Core\FileSystem::, which regsvr32 and .NET cannot open.
            return [IO.Path]::GetFullPath((Resolve-Path $candidate).ProviderPath)
        }
    }

    return $null
}


function Add-DirectoryAccessRule {
    param(
        [string]$Path,
        [System.Security.Principal.SecurityIdentifier]$Sid,
        [string]$Rights
    )

    $acl = Get-Acl $Path
    $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
        $Sid,
        $Rights,
        "ContainerInherit,ObjectInherit",
        "None",
        "Allow")
    $acl.SetAccessRule($rule)
    Set-Acl $Path $acl
}




# --- 1. Elevation check ---
Emit-Step 'RequireAdmin' 'HKLM'
$IsAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $IsAdmin -and -not $WhatIf) {
    Write-Host "[ERROR] Run this script as administrator. It writes to HKLM." -ForegroundColor Red
    exit 1
}

$Clsid = "{11A06A2B-EA6D-43D9-870C-ADF907750ACA}"
$Profile = "{1490E7F2-8B6B-41FA-B70A-BBEA2BA7A244}"

# The language, checked before anything is done: one that is not in the list stops the script here.
$Languages = @(Get-ManjuInstalledLanguages)
if ($Languages.Count -eq 0) {
    Write-Host "[ERROR] The Windows language list of this user has no language an input method can be added to." -ForegroundColor Red
    exit 1
}
if ($LanguageIdentifier) {
    $Requested = $LanguageIdentifier.Trim().ToUpperInvariant()
    $Chosen = @($Languages | Where-Object { $_.LangId -eq $Requested })[0]
    if (-not $Chosen) {
        $Offered = ($Languages | ForEach-Object { "$($_.LangId) $($_.Tag)" }) -join ', '
        if ($Requested -in $NoOwnLangIds) {
            Write-Host "[ERROR] LANGID $Requested belongs to a language that has no language identifier of its own. Windows gives such a language a temporary identifier and does not keep an input method under it, so setup does not register under it. Choose one of: $Offered." -ForegroundColor Red
        } else {
            Write-Host "[ERROR] LANGID $Requested is not in the Windows language list of this user, and setup does not add languages to Windows. Add the language in Windows Settings first, or choose one of: $Offered." -ForegroundColor Red
        }
        exit 1
    }
    $Why = 'requested'
} else {
    $Display = Get-ManjuDisplayLanguage
    $Chosen = @($Languages | Where-Object { [Convert]::ToInt32($_.LangId, 16) -eq $Display })[0]
    $Why = 'the Windows display language (0x{0:X4})' -f $Display
    if (-not $Chosen) {
        $Chosen = $Languages[0]
        $Why = 'the first language of the list (the Windows display language 0x{0:X4} is not in it)' -f $Display
    }
}
$LangID = $Chosen.LangId
$LangTag = $Chosen.Tag
Write-Host "    [language] $LangID $LangTag ($($Chosen.Name)): $Why" -ForegroundColor Gray
$Desc = "Manju IME"

Write-Host ">>> [Manju IME] Installing $Desc..." -ForegroundColor Cyan


# --- Locate the DLL of this delivery (several fallback paths) ---
# The second candidate is the SHARED PAYLOAD one level up.
$DllCandidates = @(
    (Join-Path $PSScriptRoot "CSharpTSFInput.dll"),
    (Join-Path $PSScriptRoot "..\Runtime\CSharpTSFInput.dll")
)
$DllPath = Resolve-FirstExistingPath $DllCandidates
if (-not $DllPath) { $DllPath = Join-Path $PSScriptRoot "CSharpTSFInput.dll" }
$InstallRoot = (Resolve-Path -LiteralPath $PSScriptRoot).ProviderPath

# --- In place, or a copy in the folder given with -InstallPath ---
# The delivery is the folder that holds Runtime\, Installer\ and Script\. Given another folder, the copy
# comes right after the rights check and before anything is removed, so a copy that fails leaves an
# installed copy as it was; every path after it points into the copy. Checked before anything is done:
# a folder that is refused stops the script here, with -WhatIf too.
$DeliveryRoot = Get-ManjuFullPath ([IO.Path]::Combine($InstallRoot, '..'))
$CopyTo = $null
if ($PSBoundParameters.ContainsKey('InstallPath')) {
    $Refusal = Get-ManjuTargetRefusal -Source $DeliveryRoot -Dll $DllPath -Target $InstallPath -InstalledDll (Get-ManjuRegisteredDll -Clsid $Clsid)
    if ($Refusal) {
        Write-Host "[ERROR] $Refusal" -ForegroundColor Red
        exit 1
    }
    if (-not (Test-ManjuSamePath $InstallPath $DeliveryRoot)) {
        $CopyTo = Get-ManjuFullPath $InstallPath
        $DllPath = [IO.Path]::Combine($CopyTo, (Get-ManjuRelativePath -Root $DeliveryRoot -Path $DllPath))
        $InstallRoot = [IO.Path]::Combine($CopyTo, (Get-ManjuRelativePath -Root $DeliveryRoot -Path $InstallRoot))
    }
}
if ($CopyTo) {
    Emit-Step 'CopyDelivery' $CopyTo $DeliveryRoot
    if (-not $WhatIf) {
        Write-Host ">>> Copying Manju IME to $CopyTo..." -ForegroundColor Cyan
        try {
            $Copied = Copy-ManjuDelivery -Source $DeliveryRoot -Target $CopyTo
        } catch {
            Write-Host "[ERROR] Copying to $CopyTo failed: $($_.Exception.Message) Nothing was removed or registered." -ForegroundColor Red
            exit 1
        }
        Write-Host "    Copied $Copied file(s)." -ForegroundColor Green
    }
}

# --- Remove any previous installation first ---
$UninstallScript = Join-Path $PSScriptRoot "Uninstall_MANJU_IME.ps1"
Emit-Step 'PreUninstall' $Clsid
if ((Test-Path $UninstallScript) -and -not $WhatIf) {
    Write-Host ">>> Pre-install cleanup: the uninstall script, which also stops and starts ctfmon in this session..." -ForegroundColor Gray
    & (Join-Path $System32 'WindowsPowerShell\v1.0\powershell.exe') -NoProfile -File "$UninstallScript" 2>$null | Out-Null
}

Emit-Step 'LocateDll' $DllPath
if (-not (Test-Path $DllPath) -and -not $WhatIf) {
    Write-Host "CSharpTSFInput.dll not found." -ForegroundColor Red
    exit 1
}
$DllDir = Split-Path $DllPath -Parent

# --- the chosen language, into the settings file the DLL reads while it registers itself ---
$SettingsFile = [IO.Path]::Combine($DllDir, 'Resource\Configuration\configuration.yaml')
Emit-Step 'WriteSetting' $SettingsFile "install.language_identifier=$LangID"
if (-not $WhatIf) {
    Set-ManjuLangIdSetting -Path $SettingsFile -LangId $LangID
    Write-Host "    Language setting: install.language_identifier = $LangID in $SettingsFile" -ForegroundColor Green
}

$DoneTarget = Split-Path $InstallRoot -Parent

Write-Host "    DLL path: $DllPath" -ForegroundColor Gray




# --- Step 1: check the signature ---
Write-Host ">>> Step 1: checking the digital signature..." -ForegroundColor Cyan
Emit-Step 'VerifySignature' $DllPath
if ($WhatIf) { $Sig = $null } else {
try {
    $Sig = Get-AuthenticodeSignature $DllPath -ErrorAction SilentlyContinue
    # What was found, and that the install goes on either way: Windows registers and loads an
    # input method without a signature.
    if (-not $Sig -or $Sig.Status -ne "Valid") {
        Write-Host "    Not signed, or the signature is not valid ($(if ($Sig) { $Sig.Status } else { 'no status' })). Windows does not require one; the install goes on." -ForegroundColor Yellow
    } else {
        Write-Host "    Signature is valid." -ForegroundColor Green
    }
} catch {
    Write-Warning "Signature check skipped: $($_.Exception.Message)"
}
}

# --- Step 2: fix permissions on the directory, with inheritance ---
Write-Host ">>> Step 2: fixing ACL permissions..." -ForegroundColor Cyan
Emit-Step 'GrantDirectoryAccess' $DllDir "S-1-15-2-1:ReadAndExecute"
if ($WhatIf) { } else {
try {
    $Acl = Get-Acl $DllDir
    # ALL APPLICATION PACKAGES (S-1-15-2-1), needed by UWP and Store apps
    $Sid = New-Object System.Security.Principal.SecurityIdentifier("S-1-15-2-1")
    $Rule = New-Object System.Security.AccessControl.FileSystemAccessRule($Sid, "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
    $Acl.AddAccessRule($Rule)
    Set-Acl $DllDir $Acl
    Write-Host "    ACL updated, inheritance included." -ForegroundColor Green
} catch {
    Write-Warning "ACL fix skipped: $($_.Exception.Message)"
}
}

# --- Step 3: register the COM server ---
Write-Host ">>> Step 3: registering with the system (regsvr32)..." -ForegroundColor Cyan
Emit-Step 'RegisterServer' $DllPath
$RegArgs = "/s `"$DllPath`""
$SvrProc = if ($WhatIf) { $null } else { Start-Process (Join-Path $System32 'regsvr32.exe') -ArgumentList $RegArgs -Wait -PassThru }

if ($SvrProc -and $SvrProc.ExitCode -ne 0) {
    Write-Host ">>> Registration failed, exit code: $($SvrProc.ExitCode)" -ForegroundColor Red
    exit 1
}
# With -WhatIf regsvr32 never ran, so there is no success to report.
if ($WhatIf) {
    Write-Host "    would register (not run with -WhatIf)." -ForegroundColor Gray
} else {
    Write-Host "    regsvr32 succeeded." -ForegroundColor Green
}


# --- Step 4: add to the language list ---
# Only the entry whose tag was chosen is touched, and it must already be in the list: when it is not,
# the step stops before Set-WinUserLanguageList and nothing is changed. Setup never adds a language.
# Get-WinUserLanguageList can return a language’s InputMethodTips as a fixed-size IList wrapper,
# so .Add() throws "Collection was of a fixed size." The entry is therefore rebuilt via
# New-WinUserLanguageList for the same tag (which produces a mutable Tips collection): its tips are
# cleared and the existing ones copied, so it keeps exactly the input methods it had, the new tip is
# appended, then the entry is SWAPPED into the list before Set-WinUserLanguageList.
Write-Host ">>> Step 4: adding to the language list..." -ForegroundColor Cyan
$tip = "{0}:{1}{2}" -f $LangID, $Clsid, $Profile
Emit-Step 'AddLanguageTip' $LangTag $tip
Emit-Step 'InvokeLanguageListCmdlets' 'Get-WinUserLanguageList;New-WinUserLanguageList;Set-WinUserLanguageList' $tip
if ($WhatIf) { Emit-Step 'ActivateProfile' $Clsid $Profile; Emit-Step 'BroadcastSettingChange' 'intl'; Emit-Step 'EnsureCtfmon' 'ctfmon.exe'; Emit-Step 'Done' $DoneTarget; Write-PlanAndExit }
try {
    $oldList = Get-WinUserLanguageList
    $newList = [System.Collections.Generic.List[Microsoft.InternationalSettings.Commands.WinUserLanguage]]::new()

    $langFound = $false
    foreach ($lang in $oldList) {
        if ($lang.LanguageTag -eq $LangTag) {
            $langFound = $true
            if ($lang.InputMethodTips -contains $tip) {
                # Already present: keep it as it is, no rebuild needed.
                $newList.Add($lang)
                Write-Host "    Already in the language list." -ForegroundColor Gray
            } else {
                # Rebuild the language entry so the Tips collection is mutable.
                $rebuilt = (New-WinUserLanguageList -Language $lang.LanguageTag)[0]
                # The rebuilt entry keeps exactly the input methods this language had: the defaults
                # New-WinUserLanguageList puts in are cleared, the existing tips are copied.
                $rebuilt.InputMethodTips.Clear()
                foreach ($existingTip in $lang.InputMethodTips) {
                    $rebuilt.InputMethodTips.Add($existingTip)
                }
                # Append our tip.
                $rebuilt.InputMethodTips.Add($tip)
                $newList.Add($rebuilt)
                Write-Host "    Added to the existing language entry. The entry is rebuilt, because that collection can be fixed-size." -ForegroundColor Green
            }
        } else {
            # Every other language passes through unchanged.
            $newList.Add($lang)
        }
    }

    if (-not $langFound) {
        Write-Host "[ERROR] $LangTag is not in the Windows language list of this user. The language list was not changed." -ForegroundColor Red
        exit 1
    }

    Set-WinUserLanguageList -LanguageList $newList -Force
} catch {
    $exType = $_.Exception.GetType().FullName
    $exMsg = $_.Exception.Message
    Write-Warning "Language-list step skipped: $exMsg"
    Write-Host "    You can add the input method by hand in Settings." -ForegroundColor Yellow
}

# --- Step 5: activate through the TSF API, no sign-out needed ---
Write-Host ">>> Step 5: activating the input method (TSF API)..." -ForegroundColor Cyan
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

public static class TsfActivator {
    public const uint TF_PROFILETYPE_INPUTPROCESSOR = 0x0001;
    public const uint TF_IPPMF_FORSESSION = 0x20000000;
    public static readonly Guid GUID_TFCAT_TIP_KEYBOARD = new Guid("34745C3B-38C2-11D2-93E5-0060B067B86E");

    public static bool IsAlreadyActive(string clsidStr, string profileStr) {
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
    
    public static int ActivateForSession(string clsidStr, string profileStr, ushort langid) {
        var mgr = (ITfInputProcessorProfileMgr)new TF_InputProcessorProfiles();
        var clsid = new Guid(clsidStr);
        var profile = new Guid(profileStr);
        return mgr.ActivateProfile(TF_PROFILETYPE_INPUTPROCESSOR, langid, ref clsid, ref profile, IntPtr.Zero, TF_IPPMF_FORSESSION);
    }
}
"@ -ErrorAction SilentlyContinue

    if ([TsfActivator]::IsAlreadyActive($Clsid, $Profile)) {
        Write-Host "    Already active. Skipping." -ForegroundColor Gray
    } else {
        $hrActivate = [TsfActivator]::ActivateForSession($Clsid.Trim('{}'), $Profile.Trim('{}'), [Convert]::ToUInt16($LangID, 16))
        if ($hrActivate -eq 0) {
            Write-Host "    Active, and showing in the language bar." -ForegroundColor Green
        } else {
            Write-Host "    ActivateProfile HRESULT: 0x$($hrActivate.ToString('X8')). You may need to switch to it by hand." -ForegroundColor Yellow
        }
    }
} catch {
    Write-Host "    TSF API call skipped: $($_.Exception.Message)" -ForegroundColor Yellow
}

# Force the shell language bar to refresh, through WM_SETTINGCHANGE
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
        // Tell the system the intl settings changed, which refreshes the language bar
        SendMessageTimeout((IntPtr)0xFFFF, WM_SETTINGCHANGE, IntPtr.Zero, "intl", SMTO_ABORTIFHUNG, 1000, out result);
    }
}
"@ -ErrorAction SilentlyContinue
    [ShellNotify]::NotifyLanguageChange()
} catch { }

# ctfmon in this session: started when it is not running, left as it is when it is. This step does
# not restart it; the pre-install cleanup above ran the uninstall script, which stops and starts it.
Write-Host ">>> Checking ctfmon in this session..."
$Session = (Get-Process -Id $PID).SessionId
$ctfmon = @(Get-Process -Name ctfmon -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $Session })
if ($ctfmon.Count -gt 0) {
    Write-Host "    ctfmon is running in this session; this step leaves it as it is." -ForegroundColor Gray
} else {
    Write-Host "    ctfmon is not running in this session. Starting it..."
    Start-Process (Join-Path $System32 'ctfmon.exe') -ErrorAction SilentlyContinue
}

Write-Host "`n[DONE] Installed. Press Win+Space to switch to it." -ForegroundColor Green

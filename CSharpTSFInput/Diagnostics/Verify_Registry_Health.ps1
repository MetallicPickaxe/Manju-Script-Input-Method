$ErrorActionPreference = "Continue"

# ── WHICH WINDOWS LANGUAGE THIS INPUT METHOD REGISTERS UNDER ───────────────────
# THE NUMBER IS A SETTING, NOT A LITERAL IN THIS FILE. `install.language_identifier` in
# `Resource/Configuration/configuration.yaml`, the same key the exe and the DLL read, so the three
# cannot disagree about which language entry the input method is filed under.
# The tag is DERIVED from the number by Windows, never written down a second time.
# A missing or unreadable setting falls back to the built-in default AND SAYS SO, never silently.
# This helper is duplicated in the other deployment scripts, which share no module. If it is edited
#   here, edit it there too.
function Resolve-ManjuLangId {
    $fallback = '0804'
    # Same shape, same order as the DLL candidates in this script:
    #   1. beside me            — a flat delivery
    #   2. ..\Runtime           — the ONE shared payload of a three-directory delivery
    # A different order here would let the settings and the DLL come from different places.
    $probes = @(
        (Join-Path $PSScriptRoot 'Resource\Configuration\configuration.yaml'),
        (Join-Path $PSScriptRoot '..\Runtime\Resource\Configuration\configuration.yaml'),
        (Join-Path $PSScriptRoot '..\Resource\Configuration\configuration.yaml')
    )
    foreach ($probe in $probes) {
        if (-not (Test-Path -LiteralPath $probe)) { continue }
        $m = [regex]::Match((Get-Content -Raw -LiteralPath $probe),
                            '(?m)^\s*language_identifier\s*:\s*"?([0-9A-Fa-f]{4})"?\s*(?:#.*)?$')
        # SAY WHICH FILE IT CAME FROM, on success too. A helper that speaks only when it falls
        #   back leaves a successful run proving nothing about WHERE it looked, and with a
        #   three-directory delivery that is exactly the question.
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

# Windows answers this, we do not keep a table. An unknown number stops the script instead of
#   attaching the input method to the wrong language entry.
function Resolve-ManjuLangTag {
    param([string]$LangIdHex)
    $tag = [System.Globalization.CultureInfo]::new([Convert]::ToInt32($LangIdHex, 16)).Name
    if ([string]::IsNullOrWhiteSpace($tag)) {
        throw "Windows knows no locale name for LANGID 0x$LangIdHex - check install.language_identifier in the settings file."
    }
    return $tag
}

$LangID = Resolve-ManjuLangId
$LangTag = Resolve-ManjuLangTag $LangID
$LangFamily = $LangTag.Split('-')[0]

$CLSID = "{11A06A2B-EA6D-43D9-870C-ADF907750ACA}"
$ProfileGUID = "{1490E7F2-8B6B-41FA-B70A-BBEA2BA7A244}"
$Category_Keyboard = "{34745c63-b2f0-4784-8b67-5e12c8701a31}"
$Category_DisplayAttr = "{f4c73063-b952-4359-92f2-13db8d481856}"

Write-Host "=== Manju IME Registry Verification ===" -ForegroundColor Cyan
Write-Host "CLSID: $CLSID" -ForegroundColor Gray
Write-Host "Profile: $ProfileGUID" -ForegroundColor Gray
Write-Host ""

# 1. check the COM CLSID registration
$clsidPath = "HKLM:\SOFTWARE\Classes\CLSID\$CLSID"
if (Test-Path $clsidPath) {
    Write-Host "[PASS] COM CLSID Registered" -ForegroundColor Green
    $inproc = Get-ItemProperty -Path "$clsidPath\InProcServer32" -ErrorAction SilentlyContinue
    if ($inproc) {
        Write-Host "       DLL Path: $($inproc.'(default)')" -ForegroundColor Gray
        Write-Host "       ThreadingModel: $($inproc.ThreadingModel)" -ForegroundColor Gray
        
        # check that the DLL exists
        $dllPath = $inproc.'(default)'
        if ($dllPath -and (Test-Path $dllPath)) {
            Write-Host "       [OK] DLL file exists" -ForegroundColor Green
        } else {
            Write-Host "       [WARN] DLL file NOT found at registered path!" -ForegroundColor Yellow
        }
    } else {
        Write-Host "[FAIL] InProcServer32 key missing!" -ForegroundColor Red
    }
} else {
    Write-Host "[FAIL] COM CLSID NOT Found!" -ForegroundColor Red
}

# 2. check the TSF TIP registration
$tipPath = "HKLM:\SOFTWARE\Microsoft\CTF\TIP\$CLSID"
if (Test-Path $tipPath) {
    Write-Host "[PASS] TSF TIP Registered" -ForegroundColor Green
    
    # check the Enable value
    $tipProps = Get-ItemProperty -Path $tipPath -ErrorAction SilentlyContinue
    if ($tipProps -and $tipProps.Enable -eq 1) {
        Write-Host "       Enable: 1 (Active)" -ForegroundColor Green
    } else {
        Write-Host "       [WARN] Enable flag missing or disabled" -ForegroundColor Yellow
    }
    
    # check the LanguageProfile
    $langProfile = Get-ItemProperty -Path "$tipPath\LanguageProfile\0x0000$LangID\$ProfileGUID" -ErrorAction SilentlyContinue
    if ($langProfile) {
        Write-Host "[PASS] Language Profile (registered under $LangTag, $LangID) Found" -ForegroundColor Green
        Write-Host "       Description: $($langProfile.Description)" -ForegroundColor Gray
    } else {
        Write-Host "[WARN] Language Profile for $LangTag ($LangID) missing or GUID mismatch" -ForegroundColor Yellow
    }
} else {
    Write-Host "[FAIL] TSF TIP Key NOT Found!" -ForegroundColor Red
}

# 3. check the HKCU TIP registration (per user)
$tipPathUser = "HKCU:\SOFTWARE\Microsoft\CTF\TIP\$CLSID"
if (Test-Path $tipPathUser) {
    Write-Host "[PASS] HKCU TIP Registered (User-level)" -ForegroundColor Green
} else {
    Write-Host "[INFO] HKCU TIP Key NOT Found (normal for machine-level install)" -ForegroundColor Gray
}

# 4. check the category registration
Write-Host ""
Write-Host "--- Category Registration ---" -ForegroundColor Cyan

$catKeyboardPath = "HKLM:\SOFTWARE\Microsoft\CTF\TIP\$CLSID\Category\Category\$Category_Keyboard"
if (Test-Path $catKeyboardPath) {
    Write-Host "[PASS] Keyboard Category Registered" -ForegroundColor Green
} else {
    Write-Host "[WARN] Keyboard Category Key missing (Input might not show in list)" -ForegroundColor Yellow
}

$catDisplayPath = "HKLM:\SOFTWARE\Microsoft\CTF\TIP\$CLSID\Category\Category\$Category_DisplayAttr"
if (Test-Path $catDisplayPath) {
    Write-Host "[PASS] DisplayAttribute Category Registered" -ForegroundColor Green
} else {
    Write-Host "[INFO] DisplayAttribute Category not found (optional)" -ForegroundColor Gray
}

# 5. check the language list
Write-Host ""
Write-Host "--- Language List ---" -ForegroundColor Cyan
try {
    $list = Get-WinUserLanguageList
    $tip = "${LangID}:$CLSID$ProfileGUID"
    $found = $false
    foreach ($lang in $list) {
        if ($lang.InputMethodTips -contains $tip) {
            Write-Host "[PASS] IME found in $($lang.LanguageTag) language" -ForegroundColor Green
            $found = $true
        }
    }
    if (-not $found) {
        Write-Host "[WARN] IME not found in any language's InputMethodTips" -ForegroundColor Yellow
        Write-Host "       Add manually via Settings > Language > Keyboard" -ForegroundColor Gray
    }
} catch {
    Write-Host "[SKIP] Cannot check language list: $($_.Exception.Message)" -ForegroundColor Gray
}

# 6. check the DLL signature status
Write-Host ""
Write-Host "--- Signature Status ---" -ForegroundColor Cyan
if ($inproc -and $inproc.'(default)' -and (Test-Path $inproc.'(default)')) {
    try {
        $sig = Get-AuthenticodeSignature $inproc.'(default)'
        if ($sig.Status -eq "Valid") {
            Write-Host "[PASS] Digital signature: Valid (Trusted)" -ForegroundColor Green
        } elseif ($sig.Status -eq "UnknownError" -or $sig.Status -eq "NotSigned") {
            Write-Host "[WARN] Digital signature: Not signed or invalid" -ForegroundColor Yellow
        } else {
            Write-Host "[INFO] Digital signature: $($sig.Status)" -ForegroundColor Gray
        }
    } catch {
        Write-Host "[SKIP] Cannot verify signature" -ForegroundColor Gray
    }
}

Write-Host ""
Write-Host "=== Verification Complete ===" -ForegroundColor Cyan

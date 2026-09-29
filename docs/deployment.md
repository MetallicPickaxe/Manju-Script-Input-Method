# Deploying Manju IME

This guide is for IT staff who deploy Manju IME, an input method editor (IME) for typing the Manchu
script, to managed Windows computers. [Installing Manju IME](installation.md) is the guide for users.

## Requirements

- Windows 11, version 21H2 or later, on a 64-bit Intel or AMD processor. Windows 10 version 22H2 has not
  been tested.
- Windows PowerShell 5.1, the command shell and scripting language that is part of Windows 10 and
  Windows 11.
- Administrator rights, to deploy and to uninstall. [What Manju IME does to a computer](#what-manju-ime-does-to-a-computer)
  says why.

## What a release contains

A release is a zip file with three folders. Keep them together.

| Folder | Contents |
|---|---|
| `Runtime` | `CSharpTSFInput.dll`, the input method: a dynamic-link library (DLL), code that Windows loads into the programs you type in. With it, its font, its input schemes and display tables (the spellings it types and shows), its settings file, and the third-party notices |
| `Installer` | `Install Manju IME.exe` and `Uninstall Manju IME.exe`: the setup program and the uninstaller, programs (`.exe` files) that open a wizard |
| `Script` | `Install_MANJU_IME.ps1` and `Uninstall_MANJU_IME.ps1`: PowerShell scripts for unattended deployment |

The setup program and the uninstaller in `Installer` have no silent mode: they install and uninstall only
through their wizard. The scripts in `Script` ask no questions, so an unattended deployment uses them.

## Deploy

Run this in an elevated PowerShell, or from a deployment tool that runs it as an administrator:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "\\server\software\Manju IME\Script\Install_MANJU_IME.ps1" -LanguageIdentifier 0409 -InstallPath "C:\Program Files\Manju IME"
```

The script copies the release from the share into `C:\Program Files\Manju IME` and registers that
copy under English (United States). The release folder can be on a local disk or on a network share.

- `-LanguageIdentifier`, or `-L`, takes the four hexadecimal digits of a Windows language identifier, for
  example `0409` for English (United States) or `0809` for English (United Kingdom). The language must
  already be in the Windows language list of the account that runs the script: the script does not add
  languages to Windows. Without this option, the script uses the Windows display language when it is in
  that list, and otherwise the first language of the list.
- `-InstallPath`, or `-P`, takes the full path of a folder. The script copies `Runtime`, `Installer` and `Script`
  into it, file by file, deletes nothing in it, and registers the copy. Without this option, the input
  method is registered in the release folder itself and runs from there.
- `-WhatIf` prints the steps the script would take, one per line, and changes nothing. It needs no
  administrator rights.

PowerShell also takes any start of a parameter name that fits only one parameter, such as `-Lang`. The
script refuses a parameter it does not have, a misspelled one included, and then changes nothing.

The script refuses a folder that is not a full path, a folder inside the release’s own `Runtime`,
`Installer` or `Script` folder, the folder Manju IME is installed in now unless it is the release folder
itself, and a file; it then stops and changes nothing. To deploy another release into the current install
folder, uninstall first.

The script exits with code 1 when it refuses or when a required step fails: no administrator rights, a
language that is not in the list, a refused folder, a failed copy, a missing DLL, or a failed
`regsvr32`, the Windows command that registers a DLL. Otherwise it exits with code 0, and its last line is
`[DONE] Installed. Press Win+Space to switch to it.` Four steps only print a warning when they fail and
leave the exit code at 0: reading the signature of the DLL, the folder rights for Store apps, the language
list, and switching the current session to the input method.

## The language list is per user

The registration is for the whole computer, but each user account has its own Windows language list, and
the script changes only the list of the account that runs it. When you deploy with an administrator
account or a service account, each user adds Manju IME once, in **Settings**: open **Time & language**,
then **Language & region**, open the **Language options** of the language Manju IME was registered
under, and choose **Add a keyboard**. This needs no administrator rights.

## Check a deployment

This prints the path of the DLL that Windows has registered:

```
(Get-ItemProperty 'Registry::HKEY_CLASSES_ROOT\CLSID\{11A06A2B-EA6D-43D9-870C-ADF907750ACA}\InProcServer32').'(default)'
```

This prints the language list entry of the current account, such as
`0409:{11A06A2B-EA6D-43D9-870C-ADF907750ACA}{1490E7F2-8B6B-41FA-B70A-BBEA2BA7A244}`, where the four
digits before the colon are the language:

```
(Get-WinUserLanguageList).InputMethodTips | Where-Object { $_ -like '*11A06A2B-EA6D-43D9-870C-ADF907750ACA*' }
```

A user then presses **Win+Space** and picks Manju IME.

## Uninstall

Run this in an elevated PowerShell:

```
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\Manju IME\Script\Uninstall_MANJU_IME.ps1"
```

The script removes the copy that Windows has registered, wherever it is, using the language written in
that copy’s settings file, or the language of its registration when that file is gone, and prints
`>>> Uninstalled.` when it finishes. Without administrator rights it stops at once with exit code 1. It
takes no parameters and refuses any; to see the steps without running them, run the uninstaller with
`--whatif`, or `-w`:

```
& "C:\Program Files\Manju IME\Installer\Uninstall Manju IME.exe" --whatif
```

The setup program takes `--whatif` (`-w`) too, with `--language-identifier` (`-l`) and `--location` (`-p`)
to print the steps for a language and a folder; its wizard asks for both, so without `--whatif` it
refuses them. Both programs refuse an option they do not have, list the ones they do, and change
nothing.

Uninstalling deletes no files. Delete the install folder afterwards; if Windows says a file is in use,
sign out or restart first.

## What Manju IME does to a computer

This section lists what the setup programs, the scripts and the input method do to a computer. It is for
users as well as IT staff; the other guides link here.

### Installing

Installing needs administrator rights, because it registers Manju IME for the whole computer: the setup
program starts itself again through the Windows elevation prompt, and the install script stops without
them. The setup program and the install script then take these steps, in this order:

1. When a folder other than the release folder is chosen, they copy `Runtime`, `Installer` and `Script`
   into it, replacing files of the same name, the settings file included, and deleting nothing.
2. They remove the copy of Manju IME that Windows has registered, if there is one, as
   [Uninstalling](#uninstalling) describes; no file is deleted. The install script does this by running
   the uninstall script, which stops `ctfmon.exe`, the Windows program that runs input methods on the
   desktop, in its own session and starts it again there.
3. They write the chosen language into the settings file, `Runtime\Resource\Configuration\configuration.yaml`,
   as `install.language_identifier`.
4. They give the ALL APPLICATION PACKAGES group read and execute rights on the `Runtime` folder, inherited by
   everything in it, so that apps from the Microsoft Store can load the input method.
5. They run `regsvr32` on `Runtime\CSharpTSFInput.dll`. The DLL registers itself in the registry, the
   Windows database of settings: as a Component Object Model (COM) class, the way Windows finds a DLL by its
   number, under `HKEY_LOCAL_MACHINE\SOFTWARE\Classes\CLSID\{11A06A2B-EA6D-43D9-870C-ADF907750ACA}`, and as
   a text service of the Text Services Framework (TSF), the part of Windows that runs input methods, under
   `HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\CTF\TIP\{11A06A2B-EA6D-43D9-870C-ADF907750ACA}`.
6. They add the input method to the chosen language in the Windows language list of the account they run
   as, which Windows keeps in that account’s part of the registry, `HKEY_CURRENT_USER`, and switch that
   account’s current session to it. When a standard user types an administrator’s name and password at the
   elevation prompt, that is the administrator’s account: see
   [The language list is per user](#the-language-list-is-per-user).
7. They tell running programs that the input settings changed, and start `ctfmon.exe` when it is not
   running in their session.

The setup program starts `powershell.exe` to read the language list as soon as its wizard opens, and again
to change the list; it also starts `regsvr32.exe` and `ctfmon.exe`. It starts all three by their full path
in the Windows `System32` folder, so a program of the same name in its own folder or in the current folder
is never the one that runs; the two scripts do the same for these programs and for `reg.exe`. The
two scripts compile small parts of C# code with `Add-Type`. Neither the setup programs nor the scripts add
Manju IME to the Windows list of installed apps, and none of them creates a service, a scheduled task or a
shortcut.

### While you type

- Windows loads `CSharpTSFInput.dll` into each program you type in while Manju IME is the input method,
  from the folder where it was registered, and the DLL stays loaded until that program exits.
- The input method gets the keys you press through TSF, and only while it is the input method of the
  window you type in. While it is, it also watches which window of any program is in the foreground, so
  that it closes its windows and drops the letters of an unfinished word when you switch to another
  program. Inside apps from the Microsoft Store it does not watch the foreground; Windows tells it when you
  switch away.
- It reads its settings file, its input schemes, its display tables and its font from the `Runtime` folder;
  the settings file can name a font in another folder. When you press a key, it checks when the settings
  file was last saved, at most every half second, and reads the file again when it has changed.
- Its own code writes no file, keeps no log, does not read or write the registry, and makes no network
  connection; the parts of Windows it uses, such as TSF, read the registry themselves. It saves nothing you
  type: the letters of a word are held in memory while you type it.
- It creates windows of its own, such as the candidate window, and one thread of its own in each program
  it is loaded into.
- In Microsoft Edge, Google Chrome and the other programs built on Chromium, and in programs that show web
  pages with Microsoft Edge WebView2, the first time you type a word there it sets two Windows message
  hooks on that program’s window thread, removed when the input method is switched off in that program.
  When one of these programs ends a word itself, it leaves a dot, the placeholder of the word, in the
  document. So the input method ends the word first and puts it in as you typed it: before it passes the
  program a key, other than Shift, Ctrl, Alt or Win alone and Caps Lock, Num Lock and Scroll Lock; and,
  with the hooks, when a mouse button goes down in the program, when Alt is pressed with another key such
  as D, and when another window of the same program becomes active. On Alt+Tab and Alt+Esc it drops the
  word, as when you switch away.

### Uninstalling

The uninstaller and the uninstall script remove the input method from the language list of the account they
run as, run `regsvr32 /u` on the registered DLL, which removes its COM class and its text service, and
delete the `CTF\TIP` key of Manju IME under `HKEY_LOCAL_MACHINE` and under `HKEY_CURRENT_USER` of that
account. When the folder of the registered copy is gone, they take the language from the registration
itself, and the uninstaller deletes the COM class key, which `regsvr32 /u` then cannot remove. The
uninstall script also unregisters the text service through TSF, removes the input method from
the settings under `HKEY_CURRENT_USER\Control Panel\International\User Profile`, and deletes the COM class
key with `reg.exe`. Then it stops `ctfmon.exe` in its own session and starts it again there; the
uninstaller does not stop `ctfmon.exe`.

Both leave every file and folder, the settings file included, the rights of ALL APPLICATION PACKAGES on
`Runtime`, and the language lists and `CTF\TIP` keys of other accounts.

### Signing

The DLL, the two setup programs and the two scripts are not signed: they carry no Authenticode signature,
the digital signature that names the publisher of a program. Neither the setup programs nor the scripts
require one, and Windows installs and loads the input method without one. The setup program does not check
a signature, and its progress page says so; the install script reads the signature of the DLL, prints what
it found, and goes on either way.

- `-ExecutionPolicy Bypass` lets PowerShell run the unsigned scripts. An execution policy set by Group
  Policy takes precedence over it, and a policy that requires signed scripts refuses them.
- Where an application control policy such as AppLocker applies, the files can be allowed by path or by
  file hash.
- A user who starts the downloaded `Install Manju IME.exe` may see a warning of Microsoft Defender
  SmartScreen, the part of Windows that checks programs downloaded from the internet: [Installing Manju
  IME](installation.md#windows-protected-your-pc) says how to go on.
- Smart App Control, turned on and off in the settings of the Windows Security app, blocks a program that
  is not signed, and Microsoft says that at present no single program can be let through. While it is
  on, the setup programs cannot run.

### The settings file

Every user and every program uses the one settings file beside the registered DLL. In `C:\Program Files`,
changing it needs administrator rights.

## References

Microsoft Learn:

- [regsvr32](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/regsvr32)
- [About Text Services Framework](https://learn.microsoft.com/en-us/windows/win32/tsf/about-text-services-framework)
- [Text Service Registration](https://learn.microsoft.com/en-us/windows/win32/tsf/text-service-registration)
- [InprocServer32](https://learn.microsoft.com/en-us/windows/win32/com/inprocserver32)
- [HKEY_CLASSES_ROOT Key](https://learn.microsoft.com/en-us/windows/win32/sysinfo/hkey-classes-root-key)
- [Language Identifiers](https://learn.microsoft.com/en-us/windows/win32/intl/language-identifiers)
- [[MS-LCID]: Windows Language Code Identifier (LCID) Reference](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-lcid/70feba9f-294e-491e-b6eb-56532684c37f)
- [Get-WinUserLanguageList](https://learn.microsoft.com/en-us/powershell/module/international/get-winuserlanguagelist)
- [Set-WinUserLanguageList](https://learn.microsoft.com/en-us/powershell/module/international/set-winuserlanguagelist)
- [about_Execution_Policies](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.core/about/about_execution_policies)
- [AppLocker](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/applocker/applocker-overview)
- [Microsoft Defender SmartScreen](https://learn.microsoft.com/en-us/windows/security/operating-system-security/virus-and-threat-protection/microsoft-defender-smartscreen/)
- [Authenticode digital signatures](https://learn.microsoft.com/en-us/windows-hardware/drivers/install/authenticode)
- [64-Bit Considerations](https://learn.microsoft.com/en-us/windows/win32/tsf/64-bit-platform-considerations), which describes `ctfmon.exe`

Microsoft Support:

- [Smart App Control Frequently Asked Questions](https://support.microsoft.com/en-us/windows/smart-app-control-frequently-asked-questions-285ea03d-fa88-4d56-882e-6698afdb7003)

## License

This guide is licensed under the Creative Commons Attribution 4.0 International license (CC BY 4.0):
see [LICENSE](LICENSE).

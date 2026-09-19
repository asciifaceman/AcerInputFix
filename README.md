# AcerInputFix

A small Windows tray utility that works around an Acer HID consumer-control bug where Windows can enter a bad input state (stuck injected `VK_MEDIA_NEXT_TRACK`, broken taskbar previews, flaky selection/terminal input).

It keeps the Acer Col07 device enabled: quarantine/suppress the bogus Next Track stream, then optionally reset Col07 (disable/enable) after each repair so taskbar previews recover.

![AcerInputFix tray application](static/image.png)

In-repo version: **0.2.0** (GitHub Release tags set the shipped version).

## AI Disclaimer

This project was created after an Acer mainboard replacement exposed a keyboard issue that could make VS Code, PowerShell, the Windows key, and other input unreliable.

AI agents were used to investigate the issue through event logging and help assemble this workaround. It may be useful to others experiencing similar behavior.

## Requirements

- Windows 10 or later
- A Windows desktop session (low-level keyboard hook)
- **Administrator** during install (elevated logon task + Col07 auto-reset)

Building from source needs the .NET 8 SDK. Building the installer locally also needs [Inno Setup 6](https://jrsoftware.org/isinfo.php).

## Install / upgrade

1. Download `AcerInputFix-<version>-win-x64-setup.exe` from [Releases](https://github.com/asciifaceman/AcerInputFix/releases).
2. Run the setup wizard and accept the UAC prompt (Administrator is required).
3. Choose an action: **Install/upgrade**, **Uninstall** (keep stats/logs), or **Uninstall and purge**.
4. On **Consent**, read what that action will do, check the confirmation box, then **Next**.
5. For install: choose the folder, then **Ready** → **Next** (tracked tasks) → summary → **Finish**.
6. For uninstall/purge: confirm removal → **Next** (tracked tasks) → summary → **Finish**.

The application binary lives under Program Files. Logs and `stats.json` are stored in `%LOCALAPPDATA%\AcerInputFix` so they remain writable without elevating every file write. Start Menu / desktop shortcuts prefer the elevated logon task when that option was installed.

## Run

AcerInputFix lives in the notification area.

- **Enabled** — Layer-1 B0 quarantine/suppression  
- **Auto-reset Col07 on repair** — Layer-2 HID cycle after each repair (needs Admin)  
- **Layer-2 diagnostics** — optional logging ([DIAGNOSTICS.md](DIAGNOSTICS.md))

## Build from source

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Output: `bin\Release\net8.0-windows\win-x64\publish\AcerInputFix.exe`

### Installer (local)

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\installer\Build-Installer.ps1 -Version 0.2.0
```

Produces `artifacts\installer\AcerInputFix-0.2.0-win-x64-setup.exe`.

## Cutting a release

1. Push the commits you want shipped.
2. Create a GitHub Release with tag `vMAJOR.MINOR.PATCH` (example: `v0.2.0`).
3. The [Release workflow](.github/workflows/release.yml) builds the app, compiles the Inno Setup wizard, and uploads:
   - `AcerInputFix-<version>-win-x64-setup.exe`
   - `AcerInputFix-<version>-win-x64-setup.exe.sha256`

Code signing is not part of the pipeline yet.

## Dev helpers

PowerShell scripts (`Install.ps1`, `Install-StartupTask.ps1`, etc.) remain for manual/dev installs. Normal use is the setup wizard.

## Safety and limitations

- Windows-only; specific to the Acer Col07 / stuck media-key behavior described in-repo.
- Uses a low-level keyboard hook and (when elevated) temporary HID device disable/enable.
- Review the source before running where keyboard input is security-sensitive.
- Only the injected `VK_MEDIA_NEXT_TRACK` path is filtered; other keys pass through.

## License

License intentionally deferred — not chosen yet. Treat the code as unpublished for redistribution until a license is added.

# AcerInputFix

A small Windows tray utility that works around an Acer keyboard issue where `VK_MEDIA_NEXT_TRACK` can remain held after a media-key press. The app watches the low-level keyboard stream, releases the key after 350 ms when no matching key-up arrives, and suppresses the resulting bogus repeat events.

![AcerInputFix tray application](static/image.png)


## AI Disclaimer

I was personally affected by this issue after Acer replaced my mainboard, and it was debilitating making vscode/powershell unsuable, the win key not work, and other things unusable etc etc.

I worked with AI Agents to track down the issue with a lot of event logging and had it put together this software as a workaround. I figured it might be helpful to others as well, AI created or not. I certainly wouldn't have been able to fix it on my own.

## Requirements

- Windows 10 or later
- .NET 8 SDK
- A Windows desktop session, because the app installs a low-level keyboard hook

## Build and publish

From the repository root:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

The published executable will be under:

```text
bin\Release\net8.0-windows\win-x64\publish\AcerInputFix.exe
```

Copy `AcerInputFix.exe` to the folder where you want it to live. The application writes `AcerInputFix.log` and `stats.json` beside the executable, so the folder must be writable by the signed-in user.

## Run

Start `AcerInputFix.exe`. It runs in the notification area and begins monitoring immediately. Right-click its tray icon to pause or resume the fix, open the log folder, or exit.

The utility is intentionally quiet when no stuck input is detected. A repair is recorded in the tray menu, log file, and lifetime `stats.json` counter.

## Start with Windows

Place `AcerInputFix.exe` beside the PowerShell scripts, then run PowerShell as the signed-in user:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-StartupTask.ps1
```

This registers an interactive scheduled task that starts the utility when that user signs in. To remove it:

```powershell
.\Remove-StartupTask.ps1
```

The installer uses the executable next to `Install-StartupTask.ps1`; it does not require a fixed installation path.

## Safety and limitations

- This is a Windows-only utility and is specific to the Acer input behavior described above.
- It uses a low-level keyboard hook and synthetic key-up input. Review the source before running it on a machine where keyboard input is security-sensitive.
- Only the `VK_MEDIA_NEXT_TRACK` path is modified. Other keyboard input is passed through.

## License

This is the `I don't care` license, I didn't write it, who knows where the AI got its knowledge from.

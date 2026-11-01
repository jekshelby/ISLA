# ISLA

ISLA is a Windows desktop Dynamic Island companion built with C# and WPF.

## Release 1.0.0

Release 1.0.0 provides a compact, always-on-top island at the top-center of the
desktop with expandable controls and Windows notification integration.

### Features

- Dynamic Island-style floating interface with hover expansion and smooth animations.
- Media player controls using Windows Global System Media Transport Controls.
- Current media title, artist, playback state, and progress display.
- Quick Settings for Wi-Fi, Bluetooth, and screen brightness.
- Pomodoro timer with focus, short-break, and long-break phases.
- Battery status, charging indicator, clock, and Caps Lock state.
- Windows system notification capture for apps such as Discord and Snipping Tool.
- Notification preview displayed directly on the compact island.
- Tray icon with Show ISLA, Start with Windows, notification permission, test toast,
  and exit actions.
- Windows toast notification test through `CommunityToolkit.WinUI.Notifications`.
- EXE installer with Start Menu shortcut and sparse package registration for
  notification access.

## Requirements

- Windows 10 version 19041 or newer.
- .NET 10 Desktop Runtime for running the installed application.
- Windows notification access granted to ISLA.

## Build

Build the application and installer from PowerShell:

```powershell
.\build-installer.ps1
```

The generated installer is written to:

```text
release\ISLA-Setup.exe
```

The application project is located at `src\ISLA`, and the installer project is
located at `installer`.

## Notification access

ISLA uses the Windows `UserNotificationListener` API. The installer registers a
small sparse package to provide the required package identity while keeping the
main application as a regular executable.

After installation:

1. Start ISLA.
2. Open the tray menu.
3. Ensure **Allow Windows notifications** is enabled.
4. Grant notification access in Windows Settings if requested.

## License

This project does not currently declare a license.

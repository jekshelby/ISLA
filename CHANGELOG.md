# Changelog

## 1.5.0 - 2026-11-08

### Added

- Game HUD: when a full-screen or borderless game takes the foreground, the
  dynamic island turns into a single-line stats bar with a gamepad icon, a `GAME`
  label, and live FPS, CPU, RAM, GPU, and VRAM readings.
- The HUD samples once per second and stays visible for the whole game session.
  Focused banners and the expanded menu take priority; the HUD returns when they
  finish. Closing the game or switching to another window hides it.
- FPS is read from PresentMon when available (including the copy bundled with AMD
  Adrenalin), without requiring elevation. Without PresentMon the HUD shows
  `FPS --` and keeps the system stats.

### Changed

- The idle notch is suppressed while a game is detected, so the HUD is not
  replaced by the battery notch after 30 seconds of no interaction.
- Media continues to play during a game without covering the HUD with the compact
  spectrum; the spectrum returns with the clock when the game closes.

### Install

Run `release\ISLA-Setup-v1.5.0.exe`. The build runs on Windows 10 1809+ and
Windows 11 with the .NET 10 desktop runtime.

## 1.4.0 - 2026-11-08

### Added

- Calendar tab: month grid with Monday-first weeks, previous/next month
  navigation, a per-day reminder list, and a compact add form (title + 24-hour
  time).
- Reminders are stored in `%LOCALAPPDATA%\ISLA\reminders.json` and survive
  restarts.
- Due reminders appear as a persistent island notification. The banner stays
  until it is acknowledged in the Calendar tab; opening the menu hides it and it
  returns on collapse if it is still unacknowledged.
- Days with pending reminders show a small accent dot. Due reminders can only be
  dismissed with the acknowledge check; future reminders can be deleted.
- Fifth dock tab for Calendar.

### Changed

- The notification queue keeps persistent reminder banners when the queue is
  full, dropping ordinary banners first instead.
- Reminder banners use the ISLA app icon in the notification circle.

### Install

Run `release\ISLA-Setup-v1.4.0.exe`. The build runs on Windows 10 1809+ and
Windows 11 with the .NET 10 desktop runtime.

## 1.3.2 - 2026-11-08

### Added

- Weather tab with current conditions (temperature, feels-like, humidity, wind)
  and a four-day forecast.
- Weather uses vector condition icons and refreshes every 15 minutes, with a
  manual refresh button.
- Location is detected from the IP address on first use (`ipapi.co`, with
  `ipwho.is` as a fallback) and stored in `settings.json` so later launches do
  not need to look it up again.

### Changed

- Hovering the idle notch now returns to the compact dynamic island first. The
  menu opens on a second hover instead of jumping straight into the full panel.
- Weather is available as a fourth dock tab.
- The expanded menu starts directly with content: the brand header and close
  button are gone, reducing the panel height from 280 px to 252 px.
- The idle battery track is drawn with a faded version of the battery colour
  (`Opacity = 0.35`) so the charge indicator is less distracting.
- The app icon is embedded as a WPF resource in addition to being copied to the
  output folder, so the window icon resolves consistently in every host.

### Fixed

- The idle notch's black body now follows the outward-flaring battery outline
  instead of keeping a square top edge.
- Flare wings render outside the island border, so the silhouette is no longer
  clipped or mismatched against the green outline.

### Install

Run `release\ISLA-Setup-v1.3.2.exe`. The build runs on Windows 10 1809+ and
Windows 11 with the .NET 10 desktop runtime.

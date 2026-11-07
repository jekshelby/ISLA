# Changelog

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

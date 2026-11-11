# Changelog

## 0.5.0

- Per-room volume sliders under the global group slider.
- App logo and icon (exe, window, tray, Start menu).
- Built-in per-user installer (`--install` / `--uninstall`), Start menu shortcut and *Installed apps* entry.
- Single-instance behaviour: launching the app again shows the running window.

## 0.4.0

- On-screen volume indicator (room, source, volume).
- Rooms and groups: auto-select the room playing line-in, link/unlink rooms (experimental), source display.
- Windows output awareness: keys control Sonos only while the chosen output is active; optional "line-in only" mode; keeps the chosen output at 100 %.

## 0.3.0

- Global capture of the volume keys, step setting, tray icon, start with Windows.
- Dark Sonos-style UI.
- Self-contained executable (no SDK required).

## 0.2.0

- Prototype: discovery, stereo-pair group volume via `GroupRenderingControl`.

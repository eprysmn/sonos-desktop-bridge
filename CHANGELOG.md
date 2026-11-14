# Changelog

## 0.6.1

- Every option in the settings card now has a short explanation, in particular "only if Sonos is on the local (line-in) source".

## 0.6.0

- The Sonos room and the Windows output are now explicit settings that can be locked. The app no longer picks a room automatically at startup (it could select whichever room happened to be playing).
- Volume keys stay with Windows until a Windows output has been chosen.
- Saved Windows output is re-matched by name if Windows changes its device id; an unavailable saved output stays visible in the list.
- Automatic retry (every 10 s) when the locked room is unreachable, e.g. right after boot.
- `SONOSBRIDGE_SETTINGS_DIR` environment variable to keep settings in another folder.

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

<p align="center">
  <img src="assets/logo.png" width="120" alt="Sonos Desktop Bridge logo">
</p>

<h1 align="center">Sonos Desktop Bridge</h1>

<p align="center">
  Use your keyboard's volume keys to control the real volume of your Sonos speakers from Windows, with an on-screen volume indicator.
</p>

> **Unofficial project.** Sonos Desktop Bridge is not affiliated with, endorsed by, or sponsored by Sonos, Inc. "Sonos" is a trademark of Sonos, Inc.

## Why this exists

If you play your PC's audio through a Sonos speaker using the **Sonos Line-In Adapter** (or any analog/USB line-in), Windows has no idea that the speaker has its own volume. The usual workaround is to leave the Windows volume at 100 % and turn the speaker up or down in the Sonos app, so the volume keys on your keyboard end up doing nothing useful.

Sonos Desktop Bridge closes that gap:

- The **volume up / volume down / mute** keys control the **Sonos group volume**.
- Windows stays at **100 %** on the output wired to the Sonos, so you never double-attenuate.
- If Windows switches to another output (for example a Bluetooth headset), the keys go **back to Windows** automatically.

## Features

- **Hardware volume keys → Sonos.** Global hook for the three media keys, with a configurable step (1–5 %) and fast, debounced updates.
- **On-screen volume indicator** in the style of the Sonos app: room name, current source, volume and a progress bar. Click-through, never steals focus.
- **Output awareness.** Pick the Windows playback device wired to your Sonos; the app only intercepts the keys while that device is the default output. It can also keep that device pinned at 100 % and unmuted.
- **Optional "line-in only" mode.** Only intercept the keys while the Sonos is actually playing its line-in (local source).
- **Group and per-room volume.** One global slider plus one slider per room, like the Sonos app.
- **Stereo pairs and groups handled correctly.** The app resolves the group coordinator from the Sonos topology, so a stereo pair is controlled as a single room.
- **Link / unlink rooms** from the app (experimental, see [Limitations](#limitations)).
- **Source display** (line-in, streaming, TV, …).
- **System tray app**, optional start with Windows, single instance.
- **Per-user installer built in** (Start menu entry + *Installed apps* entry, uninstall support, no admin rights).
- **Local only.** No Sonos account, no cloud, no telemetry.

## Requirements

- Windows 10 or 11 (x64)
- One or more Sonos players on the **same local network** as the PC (multicast/SSDP discovery must not be blocked, and Wi-Fi client isolation must be off)
- For the line-in scenario: a Sonos player with line-in (e.g. Era 100/300 with the Sonos Line-In Adapter, Five, Port…)

The release build is self-contained: **no .NET SDK or runtime needs to be installed.**

## Install

1. Download `SonosDesktopBridge.exe` from the [latest release](../../releases/latest).
2. Run it. The exe is **not code-signed**, so Windows SmartScreen may warn you: click *More info* → *Run anyway*.
3. Allow the app on private networks if the Windows firewall asks.
4. In the **Application** card, click **Install in Windows** (the UI is currently in French: *Installer dans Windows*). This copies the app to `%LOCALAPPDATA%\Programs\Sonos Desktop Bridge`, adds a Start menu shortcut and an entry in *Settings → Apps*.

Command line alternatives:

```text
SonosDesktopBridge.exe --install      # install for the current user
SonosDesktopBridge.exe --uninstall    # uninstall (add --quiet to skip the prompt)
SonosDesktopBridge.exe --tray         # start minimized to the notification area
```

## Usage

1. Start the app. It discovers your Sonos players and selects the room that is currently playing its line-in; otherwise pick one from the list.
2. In **Sortie Windows** (*Windows output*), choose the playback device connected to your Sonos adapter. From then on Windows is kept at 100 % on that device.
3. Press the volume keys. The indicator appears and the Sonos volume changes.
4. Closing the window keeps the app running in the notification area; right-click the icon → *Quitter* (*Quit*) to exit.

Settings are stored in `%APPDATA%\SonosDesktopBridge\settings.json`.

## How it works

- **Discovery:** an SSDP `M-SEARCH` finds players; `ZoneGroupTopology.GetZoneGroupState` gives rooms, groups and coordinators (a stereo pair is one room).
- **Group volume:** `GroupRenderingControl` (`SetRelativeGroupVolume`, `SetGroupVolume`, `GetGroupVolume`, `SetGroupMute`) on the group coordinator.
- **Per-room volume:** `RenderingControl` `GetVolume` / `SetVolume` on each room.
- **Source:** `AVTransport.GetMediaInfo` (`x-rincon-stream:` means line-in).
- **Linking rooms:** `AVTransport.SetAVTransportURI` with `x-rincon:<coordinator>` and `BecomeCoordinatorOfStandaloneGroup`.
- **Windows side:** a low-level keyboard hook (`WH_KEYBOARD_LL`) swallows only `VK_VOLUME_UP`, `VK_VOLUME_DOWN` and `VK_VOLUME_MUTE`, and only while the Sonos output is active. Core Audio (via [NAudio](https://github.com/naudio/NAudio)) is used to read the default playback device and pin it at 100 %.

All traffic stays on your local network, using the same UPnP/SOAP interface the Sonos controller apps use.

## Limitations

- **Windows' own volume slider does not move.** The keys are intercepted, so the Windows OSD does not reflect the Sonos volume. Replicating a native Windows volume slider would need a virtual audio driver, which is out of scope.
- **Line-in latency.** Audio through a Sonos line-in has an inherent delay (roughly 75 ms or more). That is a Sonos characteristic, not something this app can remove.
- **Link/unlink rooms is experimental.** It has not been tested across a wide range of setups.
- **Tested hardware:** a stereo pair of Era 100 plus a Beam and a Play on one network. Other models should work through the same standard interface but have not been verified.
- **The user interface is currently in French.** Localization is welcome (see [Contributing](#contributing)).
- **Unsigned executable.** SmartScreen will warn on first launch.

## Build from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
cd src/SonosDesktopBridge
dotnet run -c Release
```

Create the self-contained single-file executable:

```powershell
dotnet publish src/SonosDesktopBridge -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o out
```

Project layout:

| File | Role |
|------|------|
| `Sonos.cs` | UPnP/SOAP client: discovery, topology, volume, source, grouping |
| `Platform.cs` | Settings, Windows output devices, autostart, global key hook |
| `Osd.cs` | On-screen volume indicator window |
| `Install.cs` | Per-user install / uninstall |
| `MainWindow.xaml(.cs)` | Main UI, tray icon, application logic |
| `App.xaml(.cs)` | Styles, single-instance handling, command-line options |

## Privacy and security

- The app talks only to Sonos players on your local network. There is no telemetry and no network access beyond that.
- The keyboard hook does not record keystrokes: it looks only at the three media volume keys and ignores everything else.
- The source code is small and self-contained; please read it before running an unsigned binary.

## Contributing

Issues and pull requests are welcome, especially:

- English (and other) localization of the UI
- Testing with other Sonos models and group setups
- Code signing / winget packaging

## License

[MIT](LICENSE)

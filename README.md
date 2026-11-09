# YABE for macOS

An Avalonia GUI for [Yet Another BACnet Explorer](https://sourceforge.net/projects/yetanotherbacnetexplorer/).
The original SVN source is fetched on demand and its protocol logic is compiled in place;
nothing from it is copied or patched.

    ./setup.sh [--install-deps] [--revision N]   # svn checkout -> external/yabe-svn
    ./run.sh                                     # build + launch

## Layout
- `setup.sh`: checks out the SVN trunk (`external/`, git-ignored). `--install-deps` installs svn (brew) and the .NET 8 SDK (~/.dotnet).
- `src/YabeMac.Core`: net8.0 library. Links the original `BACnetBase/Client/Transport/TransportIPv6/Device.cs` via `<Compile Include=... Link=...>`.
- `src/YabeMac.App`: Avalonia UI. Only view code lives here.

## Status
Ported from the original (see the per-area files under `src/YabeMac.App`):
channels and settings (all transports), device tree and object tree views, property editor (typed editors,
date/time wildcards), subscriptions / COV / polling, live and historical plots, trend log, schedule and calendar
editors, notification editor, alarm summary and ack, device actions (create/delete object, file transfer, time sync,
device control), object-name database, export (device DB, EDE), user commands, plugins (incl. GlobalCommander and
FindPrioritiesGlobal), Yabe-as-a-device, MS/TP capture tool.

Differences from the original: object-type icons are glyphs; ZedGraph is replaced by a custom Avalonia plot; the
Windows named-pipe transport is unavailable; the MS/TP capture feeds Wireshark through a FIFO (`/tmp/bacnet`).

macOS look: translucent dark-grey window with rounded glass panels and a native menu bar. Settings in
`~/Library/Application Support/YabeMac/settings.json`: `UseGlassTheme`, `GlassDark`, `GlassMergedTitleBar`, `UseNativeMenu`.

## Testing
`tools/sim/sim_server.py` is a simulated BACnet/IP device (127.0.0.1:47809, device 599); `tools/probe` is a headless
smoke test. `YABE_SHOT=out.png ./run.sh` renders the window offscreen against the simulator.

## Proper app bundle

`./make-app.sh --open` builds `build/Yabe.app` (Dock/menu-bar name "Yabe" and the Yabe icon, `assets/Yabe-1024.png`); `./run.sh` still runs it through `dotnet` for development, where macOS shows the process name "dotnet".
- `tools/sim/sim_trendlog.py` — device 604 @ 127.0.0.1:47814 with a 120-record Trend Log (ReadRange by position).

## Simulator environment

```
python3 -m venv tools/sim/.venv && tools/sim/.venv/bin/pip install -r tools/sim/requirements.txt
```

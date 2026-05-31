# POE2Radar

An external, mostly read-only **map/radar overlay for Path of Exile 2**.

It attaches to the PoE2 client, reads game state directly out of process memory (no injection, no
hooks), and draws a terrain + entity overlay on top of the game's map — plus an optional auto-flask
quality-of-life feature.

> ⚠️ **Use at your own risk.** This reads another process's memory and can send keystrokes to the
> game. Automating input may violate Path of Exile's Terms of Service and could put your account at
> risk. This is a personal/educational tool — you are responsible for how you use it.

## Features

- **Map overlay** — when the in-game map is open, draws the walkable-terrain mask + entity dots,
  projected player-centered onto the game's map.
- **Entity radar** — alive enemies (red), NPCs, chests, area transitions, other players, and
  **POIs** (anything the game flags with a minimap icon) shown with a ring.
- **Tile landmarks** — static features pulled from terrain tile data (boss arenas, area
  transitions, …), shown the moment you enter an area, with community-curated friendly names.
- **Navigation** — pick any landmark, POI, or entity as a destination and the overlay draws a
  smoothed A* route to it: on the in-game map when it's open, or as waypoints on the world ground
  when it's closed. Multi-select (each route its own color). Auto-nav patterns (e.g. the expedition
  encounter) re-acquire their target automatically in each new zone.
- **Auto-flask** (opt-in input) — presses the life/mana flask key below a HP/mana threshold.
  Hard-gated: only when PoE2 is the foreground window, with cooldowns and an **F8 kill-switch**.
- **Web dashboard** (`http://localhost:7777`, or **F12** in-game) — a local control panel: a
  searchable list of every entity/landmark you can click to navigate to, plus a settings tab
  (radar display toggles, monster HP-bar visibility, map calibration, auto-flask tuning). Served
  same-origin only; setting/navigation writes are loopback-gated. Read endpoints: `GET /state`,
  `/entities`, `/landmarks`.

## Download (no build required)

Grab the latest **`POE2Radar-vX.Y.Z-win-x64.zip`** from the
[Releases page](https://github.com/Sikaka/POE2Radar/releases), unzip, and run `POE2Radar.Overlay.exe`
**as Administrator** (reading another process's memory requires it) with PoE2 already running.
The build is self-contained — no .NET install needed.

Notes:
- Windows SmartScreen may warn about an unsigned exe (expected for a community tool) — "More info →
  Run anyway".
- Antivirus may flag it because it reads game memory and (optionally) sends keystrokes; that's
  inherent to what the tool does.

## Build from source

Requires the **.NET 10 SDK**, Windows x64.

```
dotnet build POE2Radar.slnx
# launch with PoE2 already running and you in a zone:
src\POE2Radar.Overlay\bin\Debug\net10.0-windows\POE2Radar.Overlay.exe
```

Reading another process generally requires running the overlay **as Administrator**.

To **exit**: right-click the **POE2Radar system-tray icon → Exit**, or press **F9** (or close the
console window).

Hotkeys: **F8** toggles auto-flask; **F9** quits; **F12** opens the web dashboard; **F6** routes to
the nearest landmark/POI and **F7** clears routes. Map calibration and all other settings live in
the dashboard (there are no calibration hotkeys, to avoid accidental presses).

## Architecture

Three projects:

- `src/POE2Radar.Core` — memory plumbing (`OpenProcess` + `ReadProcessMemory`), the PoE2 offset
  table (`Game/Poe2Offsets.cs`), and the live read layer (`Game/Poe2Live.cs`).
- `src/POE2Radar.Overlay` — the radar `.exe`: attaches, AOB-resolves the game roots, runs the tick
  loop, renders the Direct2D overlay, serves the API, and (opt-in) drives auto-flask input.
- `src/POE2Radar.Research` — dev-time offset discovery/validation tools (AOB scan, HP value-scan,
  entity/tile/UI probes, an area-change watcher). Never linked into the overlay binary.

## Offsets & patches

PoE2 memory offsets drift with game patches. Validated offsets live in `Game/Poe2Offsets.cs`
(each marked `✓` when confirmed live). After a patch that breaks reads, use the `POE2Radar.Research`
probes to re-discover them. There is no live oracle for PoE2, so validation is value-scan / manual.

## Credits

Memory-layout research and the AOB approach draw heavily on the open-source **GameHelper2** project
(its `GameOffsets` were the starting reference for PoE2's struct shapes). GameHelper2 is not
redistributed here; only independently re-validated offset values are recorded in this repo.

## License

MIT — see [LICENSE](LICENSE).

using System.Linq;
using System.Runtime.InteropServices;
using NumVec2 = System.Numerics.Vector2;
using POE2Radar.Core;
using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Input;
using POE2Radar.Overlay.Native;
using POE2Radar.Overlay.Navigation;
using POE2Radar.Overlay.Web;

namespace POE2Radar.Overlay;

/// <summary>
/// Drives the PoE2 radar: per-tick resolve chain → read player/entities/terrain/map → render.
/// Read-only. Render rate is configurable (RadarSettings.FpsCap, default 60 Hz; player blip tracks
/// live); the heavier entity/terrain walk runs at ~30 Hz. Projection scale/offset are tweakable live
/// via hotkeys for calibration.
/// </summary>
public sealed class RadarApp : IDisposable
{
    private const int WorldHz = 30;

    private readonly ProcessHandle _process;
    private readonly MemoryReader _reader;
    private readonly Poe2Live _live;
    private readonly Poe2Atlas _atlas;
    private readonly OverlayWindow _window;
    private readonly OverlayRenderer _renderer;
    private readonly ApiServer _api;
    private readonly RadarSettings _settings;
    private readonly HiddenEntities _hidden;
    private readonly WatchedEntities _watched;
    private readonly LandmarkPatterns _landmarkPatterns;
    private readonly DisplayRules _displayRules;
    private readonly LandmarkStore _landmarkStore;
    private int _landmarkGen;
    private int _displayRulesGen;
    private int _landmarkStoreGen;
    private int _appliedClusterGap;
    private nint _areaInstanceForApi;   // current AreaInstance, for the /api/tiles tile-path lookup
    private nint _inGameStateForApi;    // current InGameState, for the /api/atlas node read
    private volatile RadarState _state = RadarState.Empty;

    // ── Atlas overlay: live node highlights (takes precedence over the radar when the atlas is open). ──
    private readonly object _atlasLock = new();
    private readonly HashSet<nint> _atlasSel = new();   // selected node element addresses (from the dashboard)
    private bool _atlasOpen;
    private List<AtlasMark> _atlasMarks = new();
    // In-game atlas calibration: hover a tile + F10 captures (relPos→cursor); F11 solves the homography
    // and applies it; Shift+F10 resets. A FIXED rough projection identifies the tile under the cursor
    // (stable — never uses the live homography, so a bad fit can't poison subsequent picks).
    private readonly List<double[]> _atlasCalibPts = new();
    private DateTime _nextCalibKeyAt = DateTime.MinValue;
    // Live atlas zoom (= canvas/node scale @ +0x130; 0.85 max-out … larger zoomed in). The calibrated
    // transform's linear part scales by liveZoom/calibZoom each frame: screen = (UIscale×zoom)·relPos +
    // offset, and relPos is read live, so this tracks pan (relPos) AND zoom (this ratio) automatically.
    private volatile float _atlasZoom = 0.85f;
    private volatile UpdateChecker.Result? _update;   // GitHub version check (best-effort, set async at startup)
    // Reference projection measured at 1080p with the atlas zoomed fully out (zoom 0.85): scale = UIscale×zoom
    // = (1080/1600)×0.85 = 0.574, canvas-origin offset ≈ (15,13)px. BOTH terms scale linearly with UIscale
    // (= winH/1600) and the live zoom, so the whole projection can be DERIVED from the live window height at
    // any resolution — no per-resolution hand-calibration. See AutoAtlasProjection. (F10/F11 still overrides.)
    private const float AtlasRefScale = (1080f / 1600f) * 0.85f; // 0.574 = UIscale×zoom at the reference
    private static readonly (float X, float Y) AtlasRefOffset = (15f, 13f); // px, at AtlasRefScale

    /// <summary>Directory holding the user config files (shared with <see cref="RadarSettings"/>).</summary>
    private static string ConfigDir => Path.Combine(AppContext.BaseDirectory, "config");

    private DateTime _worldAt = DateTime.MinValue;
    private List<Poe2Live.EntityDot> _entities = new();
    private IReadOnlyList<Poe2Live.Landmark> _landmarks = Array.Empty<Poe2Live.Landmark>();
    private Poe2Live.TerrainData? _terrain;
    private uint _areaHash;
    private nint _lastAreaInstance;
    private nint _gameHwnd;
    private volatile bool _shutdown;

    // ── Auto-flask (opt-in input). Foreground + in-game gated; F8 master kill-switch.
    //    Flask keys are configurable in RadarSettings (LifeKey/ManaKey). ──
    private bool _autoFlask = true;                        // auto-on; toggle with F8
    private DateTime _lifeFiredAt = DateTime.MinValue, _manaFiredAt = DateTime.MinValue;
    private DateTime _nextToggleAt = DateTime.MinValue;
    private DateTime _nextPathKeyAt = DateTime.MinValue;
    private DateTime _nextBrowserAt = DateTime.MinValue;
    private float _hpPct = 100f, _manaPct = 100f;
    private string _flaskNote = "";
    private string _areaCode = "", _charName = "";
    private int _charLevel;
    private float[]? _cameraMatrix;

    // Render inputs rebuilt at world rate (30 Hz), not per render frame: they only change with the
    // selection / nav-target list. _overlayHadContent gates the present so we skip the (resolution-
    // proportional) UpdateLayeredWindow blit while PoE2 isn't foreground — but still push ONE blank
    // frame on focus-loss so a stale overlay never lingers over other apps.
    private List<string> _selectedSnapshot = new();
    private IReadOnlyList<LegendEntry> _legend = Array.Empty<LegendEntry>();
    private bool _overlayHadContent;

    // ── Phase 1: exploration fog + draw-only path guidance (all gated by RadarSettings flags). ──
    // Unified navigation targets: a single list built each world tick from BOTH terrain-tile
    // landmarks AND entity POIs (bosses, expedition, waypoints…), each addressed by a STABLE STRING
    // id ("t:<path>" / "e:<entityId>"). Multi-select: each selected target draws its OWN full A*
    // route in its OWN color (by selection-order slot). F6 adds the nearest not-yet-selected target;
    // F7 clears the whole selection; clicking a legend row toggles that target. Selection is capped
    // at the palette size so colors stay distinct (and per-tick planning stays bounded). On a zone
    // change the selection is cleared, then the persistent auto-nav patterns re-select matching
    // targets in the new zone.
    private const int AddNearestVk = 0x75; // F6
    private const int ClearPathsVk = 0x76; // F7
    private const int MaxSelectedTargets = 8; // == OverlayRenderer.PathPalette.Length
    // Background A* replanner (single reused PathPlanner on a worker thread) + one RouteTracker per
    // selected id. The tick thread does only CHEAP per-tick maintenance (cursor advance) and rebuilds
    // _selectedPaths from the trackers; the worker owns all A*. See BackgroundReplanner / RouteTracker.
    private readonly BackgroundReplanner _replanner = new();
    private readonly Dictionary<string, RouteTracker> _trackers = new(); // one per selected id; OWNED by the tick thread
    private List<NavTarget> _navTargets = new();                         // unified targets, rebuilt each world tick
    // The ONLY state shared with the HTTP/API thread. Every read/iterate/mutate of _selectedIds is
    // done under _navLock (snapshot to a local, then work outside the lock). Trackers are reconciled
    // from this list on the tick thread only — mutators (in-game + API) just edit _selectedIds.
    private readonly object _navLock = new();
    private readonly List<string> _selectedIds = new();                  // selected target ids (order drives the color slot)
    private List<SelectedPath> _selectedPaths = new();                   // one route per selected target (from trackers)
    private bool _selectionCapWarned;                                    // log the "cap reached" notice once
    private nint _navTargetsArea = -1;                                   // AreaInstance the auto-nav was applied for
    // Per-instance nav memory: the nav selection for each AreaInstance hash, so returning to a zone
    // (e.g. after a town trip, which re-resolves a fresh AreaInstance) RESTORES what was selected
    // instead of clearing it. AreaHash is the stable per-instance id (same instance → same hash;
    // a re-rolled map → new hash → fresh auto-nav). In-session only and capped (LRU) so a long
    // session can't grow it unbounded. _selectionAreaHash is the hash _selectedIds belong to now.
    private readonly Dictionary<uint, List<string>> _zoneSelections = new();
    private readonly List<uint> _zoneOrder = new();                      // insertion order, for LRU eviction
    private uint _selectionAreaHash;
    private const int MaxRememberedZones = 64;

    // ── Collapsible "POE2Radar" navigation menu widget state (drawn always-on; persisted corner). ──
    private bool _navMenuExpanded;                                       // dropdown open? (default collapsed)

    public void RequestShutdown() => _shutdown = true;

    public RadarApp(ProcessHandle process, MemoryReader reader, nint gameStateSlot)
    {
        _process = process;
        _reader = reader;
        _settings = RadarSettings.Load();
        Console.WriteLine($"Settings: {RadarSettings.FilePath}");
        Console.WriteLine($"Entity names: {EntityNameResolver.Shared.Count} mappings; zones: {ZoneGuide.Shared.Count}");
        _live = new Poe2Live(reader, gameStateSlot);
        _atlas = new Poe2Atlas(reader);
        _window = OverlayWindow.Create();
        _renderer = new OverlayRenderer(_window);
        // Clicking a legend row toggles that landmark in the path selection. Purely local UI — the
        // click lands on our own overlay window (never forwarded to the game). See UpdateClickThrough.
        _window.OnClientClick = OnOverlayClick;
        _hidden = new HiddenEntities(Path.Combine(ConfigDir, "hidden_entities.json"));
        _watched = new WatchedEntities(Path.Combine(ConfigDir, "watched_entities.json"));
        _landmarkPatterns = new LandmarkPatterns(Path.Combine(ConfigDir, "landmark_patterns.json"));
        _live.CustomLandmarkMatch = TileLandmarkMatch; // surface tiles via landmark patterns + Tile rules
        _landmarkGen = _landmarkPatterns.Generation;
        _live.LandmarkClusterGap = _settings.LandmarkClusterGap;
        _appliedClusterGap = _settings.LandmarkClusterGap;
        // Unified display ruleset — single source of truth for the entity dot decision. On first run
        // (no display_rules.json) seed it from the legacy category styles + mechanics + watched rules
        // so behavior is identical; thereafter it's the authoritative, editable, ordered ruleset.
        _displayRules = new DisplayRules(Path.Combine(ConfigDir, "display_rules.json"));
        if (_displayRules.Count == 0)
        {
            _displayRules.Replace(DisplayRules.BuildDefault(
                _settings.Styles, _settings.ShowMonsters, _watched.All));
            Console.WriteLine($"Display rules: seeded {_displayRules.Count} from legacy config (first run).");
        }
        // One-time: fold any user landmark-tile patterns into Tile display rules (the unified system),
        // then clear the old config so it's retired and won't double-apply or re-migrate.
        if (_landmarkPatterns.All.Count > 0)
        {
            var rules = _displayRules.All.ToList();
            var seen = new HashSet<string>(
                rules.Where(r => r.Categories.Contains("Tile")).SelectMany(r => r.Match), StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var lp in _landmarkPatterns.All)
            {
                if (!seen.Add(lp.Pattern)) continue;
                rules.Add(new DisplayRule
                {
                    Enabled = lp.Enabled, Name = string.IsNullOrWhiteSpace(lp.Label) ? lp.Pattern : lp.Label,
                    Categories = new() { "Tile" }, Match = new() { lp.Pattern },
                    Shape = "Diamond", Color = "#F259F2", Opacity = 1f, Size = 5f, Navigable = true,
                    Label = string.IsNullOrWhiteSpace(lp.Label) ? null : lp.Label,
                });
                added++;
            }
            if (added > 0) _displayRules.Replace(rules);
            foreach (var lp in _landmarkPatterns.All.ToList()) _landmarkPatterns.Remove(lp.Pattern);
            Console.WriteLine($"Migrated {added} landmark-tile pattern(s) into Tile display rules.");
        }
        // One-time: fold the old AutoNavPatterns list onto matching rules' Auto-path flag (a rule auto-
        // paths when one of its match terms overlaps a pattern), then retire the list. Preserves the
        // "auto-path to the expedition encounter on zone entry" default.
        if (_settings.AutoNavPatterns.Count > 0)
        {
            var rules = _displayRules.All.ToList();
            var pats = _settings.AutoNavPatterns;
            var changed = false;
            foreach (var r in rules)
            {
                if (r.Navigable) continue;
                if (r.Match.Any(m => pats.Any(p =>
                        m.Contains(p, StringComparison.OrdinalIgnoreCase) || p.Contains(m, StringComparison.OrdinalIgnoreCase))))
                { r.Navigable = true; changed = true; }
            }
            if (changed) _displayRules.Replace(rules);
            _settings.AutoNavPatterns = new(); _settings.Save();
            Console.WriteLine("Migrated auto-path patterns onto display rules' Auto-path flag.");
        }
        _displayRulesGen = _displayRules.Generation;
        // User-editable overlay on the baked curated landmark table (the "Landmarks" tab). Inject its
        // lookup so the landmark scan honors user edits on top of the shipped community data.
        _landmarkStore = new LandmarkStore(Path.Combine(ConfigDir, "landmarks.json"));
        _live.CuratedLookup = _landmarkStore.Lookup;
        _landmarkStoreGen = _landmarkStore.Generation;
        Console.WriteLine($"Hidden entities: {_hidden.Count} pattern(s); display rules: {_displayRules.Count}");
        _api = new ApiServer(() => _state, _settings, GetNavSelection, ToggleNavTarget, ClearNavSelection,
                             _hidden, _displayRules, _landmarkStore, CurrentTilePaths, AtlasJson, SetAtlasSelection,
                             SetAtlasHighlight, VersionJson, _settings.ApiPort);
        try { _api.Start(); Console.WriteLine($"API on http://localhost:{_settings.ApiPort} (dashboard at /)"); }
        catch (Exception ex) { Console.Error.WriteLine($"API server disabled: {ex.Message}"); }
        Console.WriteLine("Hotkeys: F6=add nearest path target  F7=clear path targets  "
                          + "F8=auto-flask  F9=quit  F12=open dashboard");
        // Best-effort version check against GitHub (non-blocking; never fails startup).
        _ = Task.Run(async () =>
        {
            var u = await UpdateChecker.CheckAsync();
            _update = u;
            if (u.UpdateAvailable)
                Console.WriteLine($"\n*** UPDATE AVAILABLE: {u.Latest} — you have v{u.Current}. Download: {u.Url} ***\n");
            else
                Console.WriteLine($"POE2Radar v{u.Current}" + (u.Latest != null ? " (up to date)." : " (update check unavailable)."));
        });
    }

    /// <summary>API (/api/version): this build's version + the latest known on GitHub + a download URL.
    /// Lets the dashboard show an "update available" banner. Null-ish until the async check completes.</summary>
    private object VersionJson()
    {
        var u = _update;
        return new
        {
            current = u?.Current ?? UpdateChecker.Current,
            latest = u?.Latest,
            updateAvailable = u?.UpdateAvailable ?? false,
            url = u?.Url ?? UpdateChecker.ReleasesPage,
        };
    }

    public void Run()
    {
        _gameHwnd = OverlayNative.FindWindowForProcess(_process.ProcessId);
        while (!_shutdown)
        {
            if (_gameHwnd == 0) _gameHwnd = OverlayNative.FindWindowForProcess(_process.ProcessId);
            if (_gameHwnd != 0) _window.TrackGameWindow(_gameHwnd);
            if (!_window.PumpMessages()) break;
            Tick();
            // Configurable frame budget (read live so dashboard edits apply immediately). The world
            // walk is independently throttled to WorldHz inside Tick().
            var hz = Math.Clamp(_settings.FpsCap, 15, 360);
            Thread.Sleep(Math.Max(1, 1000 / hz));
        }
    }

    private void Tick()
    {
        HandleHotkeys();

        var inGame = _live.TryResolve(out var inGameState, out var areaInstance, out var localPlayer);
        var player = NumVec2.Zero;
        var map = default(Poe2Live.MapUi);
        var areaLevel = 0;

        if (inGame)
        {
            // AreaInstance is a fresh object per area — use its address to invalidate per-area caches.
            if (areaInstance != _lastAreaInstance) { _terrain = null; _lastAreaInstance = areaInstance; }
            _areaInstanceForApi = areaInstance; // for /api/tiles
            _inGameStateForApi = inGameState;   // for /api/atlas node read
            _areaHash = _live.AreaHash(areaInstance);
            areaLevel = _live.AreaLevel(areaInstance);

            player = _live.PlayerGrid(localPlayer) ?? NumVec2.Zero;
            map = _live.ReadMap(inGameState, areaInstance);
            _areaCode = _live.AreaCode(areaInstance);
            _charName = _live.PlayerName(localPlayer);
            _charLevel = _live.PlayerLevel(localPlayer);
            _cameraMatrix = _live.CameraMatrix(inGameState);
            TickAutoFlask(localPlayer);

            var now = DateTime.UtcNow;
            if ((now - _worldAt).TotalMilliseconds >= 1000.0 / WorldHz)
            {
                _worldAt = now;
                _terrain ??= _live.Terrain(areaInstance);
                _entities = _live.Entities(areaInstance);
                // Drop the local player's own entity — it lives in the AwakeEntities map like any
                // other Player, but the dedicated center blip already represents "you" (gated by
                // ShowPlayerBlip). Without this, a Player-category dot renders at map-center even with
                // the blip off. Filtering here (not the renderer) keeps the nav builder and HTTP API
                // consistent, and still leaves party members visible as Player dots.
                if (localPlayer != 0)
                    _entities = _entities.Where(e => e.Address != localPlayer).ToList();
                // Drop user-hidden entities once, here — so the renderer, nav-target builder, and the
                // published RadarState (HTTP API) all see the same filtered list. Cull by metadata.
                if (_hidden.Count > 0)
                    _entities = _entities.Where(e => !_hidden.IsHidden(e.Metadata)).ToList();
                // If the user edited the custom landmark patterns, drop the cached per-area scan so it
                // rebuilds with the new patterns this tick (otherwise it only refreshes on zone change).
                if (_landmarkPatterns.Generation != _landmarkGen)
                {
                    _landmarkGen = _landmarkPatterns.Generation;
                    _live.InvalidateLandmarks();
                }
                // A changed display ruleset can add/remove "Tile" rules that surface tiles — rebuild.
                if (_displayRules.Generation != _displayRulesGen)
                {
                    _displayRulesGen = _displayRules.Generation;
                    _live.InvalidateLandmarks();
                }
                // Curated-landmark edits (Landmarks tab) change what surfaces + the labels — rebuild.
                if (_landmarkStore.Generation != _landmarkStoreGen)
                {
                    _landmarkStoreGen = _landmarkStore.Generation;
                    _live.InvalidateLandmarks();
                }
                // Live-apply a changed cluster radius (dashboard/config edit) the same way.
                if (_settings.LandmarkClusterGap != _appliedClusterGap)
                {
                    _appliedClusterGap = _settings.LandmarkClusterGap;
                    _live.LandmarkClusterGap = _appliedClusterGap;
                    _live.InvalidateLandmarks();
                }
                _landmarks = _live.Landmarks(areaInstance); // cached per area in Poe2Live

                // Atlas node highlights — ReadNodes is cheap when the atlas is closed (it gates on the
                // atlas panel's visible bit before any whole-tree scan), so this is safe each world tick.
                // When open, build marks for on-screen + selected nodes (the renderer culls the rest).
                // Selection comes from the dashboard via the API.
                BuildAtlasMarks(inGameState);

                // Rebuild the unified navigation-target list (tiles + entity POIs) for this tick.
                _navTargets = BuildNavTargets(player);

                // On a zone change: drop the (now-stale) selection, then apply the persistent
                // auto-nav patterns against the new zone's targets. Keyed off the AreaInstance
                // address (a fresh object per area), same signal the per-area caches use.
                if (areaInstance != _navTargetsArea)
                {
                    _navTargetsArea = areaInstance;
                    OnAreaChanged();
                }

                // Auto-deselect entity targets the game has marked complete (e.g. a looted expedition):
                // they're already gone from the map + nav-target list, but the still-present (faded)
                // entity would otherwise keep resolving, so the route would keep pathing to it.
                PruneCompletedTargets();

                // Per-tick route maintenance (draw-only, NO A* on this thread). For each selected
                // target: cheaply advance its cursor; fire a BACKGROUND replan only on a real trigger.
                // Then drain finished routes and rebuild _selectedPaths from the trackers' cursors.
                MaintainRoutes(player);

                // Selection snapshot + legend are render inputs that change only with the selection /
                // nav-target list — rebuild them here (30 Hz) rather than every render frame.
                _selectedSnapshot = SnapshotSelection();
                _legend = BuildLegend(_selectedSnapshot);
            }
        }
        else
        {
            _selectedPaths = new List<SelectedPath>();
            _atlasOpen = false;
        }

        _state = new RadarState(inGame, _areaHash, areaLevel, map.IsVisible, map.Zoom, player, _entities, _landmarks,
            _hpPct, _manaPct, _autoFlask, _flaskNote, _areaCode, _charName, _charLevel);

        var realActive = _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd;
        // "Always show" draws the overlay even when PoE2 isn't focused (for dashboard calibration).
        var drawActive = realActive || _settings.AlwaysShowOverlay;
        var atlasProj = AtlasProjection(); // resolution-correct (auto from window height) or manual calib
        var ctx = new RenderContext(
            InGame: inGame,
            Active: drawActive,
            WindowWidth: _window.Width,
            WindowHeight: _window.Height,
            PlayerGrid: player,
            Map: map,
            Entities: _entities,
            Landmarks: _landmarks,
            AreaHash: _areaHash,
            Terrain: _terrain,
            ScaleMul: _settings.ScaleMul,
            OffsetX: _settings.OffX,
            OffsetY: _settings.OffY,
            HpPct: _hpPct,
            ManaPct: _manaPct,
            FlaskNote: _flaskNote,
            AreaCode: _areaCode,
            CharLevel: _charLevel,
            CameraMatrix: _cameraMatrix,
            HideJunk: _settings.HideJunk,
            ShowPath: _settings.ShowPath,
            UseCuratedLandmarks: _settings.UseCuratedLandmarks,
            ShowMonsters: _settings.ShowMonsters,
            ShowTerrain: _settings.ShowTerrain,
            ShowPlayerBlip: _settings.ShowPlayerBlip,
            HpBarNormal: _settings.HpBarNormal,
            HpBarMagic: _settings.HpBarMagic,
            HpBarRare: _settings.HpBarRare,
            HpBarUnique: _settings.HpBarUnique,
            SelectedPaths: _selectedPaths,
            IsSelected: _selectedSnapshot.Contains,
            Legend: _legend,
            NavMenuExpanded: _navMenuExpanded,
            NavMenuCorner: _settings.NavMenuCorner,
            Styles: _settings.Styles,
            HpBars: _settings.HpBars,
            TerrainStyle: _settings.Terrain,
            Resolve: _displayRules.Resolve,
            ResolveTile: p => _displayRules.ResolveTile(p, requireMatch: false),
            AtlasOpen: _atlasOpen,
            AtlasNodes: _atlasMarks,
            // Projection: derived live from the window height (UIscale = winH/1600) × live zoom unless the
            // user manually calibrated (then their zoom-rescaled homography is used). relPos is read live so
            // pan is already handled; the zoom term is folded into the scale either way. _atlasProj is the
            // 8-coeff homography layout {h0..h7}. This is what makes non-1080p resolutions line up.
            AtlasScale: (float)atlasProj[0],
            AtlasScaleY: (float)atlasProj[4],
            AtlasOffX: (float)atlasProj[2],
            AtlasOffY: (float)atlasProj[5],
            AtlasShearX: (float)atlasProj[1],
            AtlasShearY: (float)atlasProj[3],
            AtlasPersX: (float)atlasProj[6],
            AtlasPersY: (float)atlasProj[7]);
        // The overlay is only visible while PoE2 is foreground (Render draws nothing otherwise). Skip
        // the whole draw + UpdateLayeredWindow blit when unfocused — but render once on the focus-loss
        // transition so the last visible frame is cleared rather than left frozen on screen.
        if (ctx.Active || _overlayHadContent)
        {
            _renderer.Render(ctx);
            _overlayHadContent = ctx.Active;
        }

        // Make the overlay grab clicks only while the cursor is over a clickable legend row;
        // otherwise stay click-through so the game receives the clicks. Runs after Render so
        // LegendRowRects reflects the frame just drawn. Gate on REAL focus (never grab clicks when
        // PoE2 isn't foreground, even if "always show overlay" is keeping it drawn).
        UpdateClickThrough(realActive);
    }

    /// <summary>
    /// Per-frame click-through toggle. The overlay captures clicks (click-through OFF) only while the
    /// overlay is active (PoE2 foreground) AND the cursor is currently over a legend row. In every
    /// other case — overlay hidden, PoE2 not foreground, or the map closed (legend empty) — it stays
    /// click-through so we never eat the user's game clicks. Reads only the cursor; sends nothing.
    /// </summary>
    private void UpdateClickThrough(bool active)
    {
        var overWidget = active
                         && _renderer.LegendRowRects.Count > 0
                         && OverlayNative.GetCursorPos(out var pt)
                         && HitTestWidget(ScreenToClientPoint(pt)) is not null;
        _window.SetClickThrough(!overWidget);
    }

    /// <summary>Convert a screen-space cursor point to the overlay window's client coords.</summary>
    private (int X, int Y) ScreenToClientPoint(OverlayNative.POINT screen)
    {
        var p = screen;
        OverlayNative.ScreenToClient(_window.Handle, ref p);
        return (p.X, p.Y);
    }

    /// <summary>
    /// Hit-test a client-space point against the renderer's navigation-menu rects. Returns the
    /// matched Action string (e.g. "menu-toggle", "corner:TopRight", "target:e:123") or null if the
    /// point is over no widget rect. LegendRowRects are in overlay client pixels (D2D renders at
    /// 96 DPI into a DIB sized to the game window's physical client rect, so 1 DIP == 1 device
    /// pixel == 1 client pixel), the same space ScreenToClient yields.
    /// </summary>
    private string? HitTestWidget((int X, int Y) p)
    {
        foreach (var (rect, action) in _renderer.LegendRowRects)
            if (p.X >= rect.Left && p.X < rect.Right && p.Y >= rect.Top && p.Y < rect.Bottom)
                return action;
        return null;
    }

    /// <summary>
    /// WM_LBUTTONDOWN handler (wired to <see cref="OverlayWindow.OnClientClick"/>): dispatch the
    /// click on the navigation-menu widget. "menu-toggle" flips the dropdown; "corner:X" pins the
    /// widget to that screen corner (persisted); "target:&lt;id&gt;" toggles that nav target's selection.
    /// Client coords arrive directly from the window, in the same space as LegendRowRects. Purely
    /// local UI — nothing is ever sent to the game.
    /// </summary>
    private void OnOverlayClick(int clientX, int clientY)
    {
        var action = HitTestWidget((clientX, clientY));
        if (action is null) return;

        if (action == "menu-toggle")
        {
            _navMenuExpanded = !_navMenuExpanded;
        }
        else if (action.StartsWith("corner:", StringComparison.Ordinal))
        {
            _settings.NavMenuCorner = action.Substring("corner:".Length);
            _settings.Save();
        }
        else if (action.StartsWith("target:", StringComparison.Ordinal))
        {
            TogglePathTarget(action.Substring("target:".Length));
        }
    }

    /// <summary>
    /// Auto-flask: press the life/mana flask key when the corresponding pool drops below its
    /// threshold. Hard-gated: enabled + PoE2 is the foreground window + per-flask cooldown.
    /// </summary>
    private void TickAutoFlask(nint localPlayer)
    {
        // No plausible vitals read (Life component missing, or vital offsets drifted past the auto-
        // relocation's reach): DON'T fire — firing on unknown HP would either spam or never trigger.
        // Surface it so a post-patch break is visible instead of silently "armed but never fires".
        if (_live.PlayerVitals(localPlayer) is not { } v)
        {
            _flaskNote = "paused (vitals unreadable — offsets may have drifted)";
            return;
        }
        _hpPct = v.HpPct; _manaPct = v.ManaPct;

        if (!_autoFlask) { _flaskNote = "OFF (F8)"; return; }
        if (GetForegroundWindow() != _gameHwnd) { _flaskNote = "paused (PoE2 not focused)"; return; }
        _flaskNote = "armed";

        var now = DateTime.UtcNow;
        if (v.HpPct < _settings.LifeThresholdPct &&
            now - _lifeFiredAt >= TimeSpan.FromMilliseconds(_settings.LifeCooldownMs))
        {
            SendInputNative.Tap((ushort)_settings.LifeKey); _lifeFiredAt = now; _flaskNote = $"life@{v.HpPct:F0}%";
        }
        if (v.ManaPct < _settings.ManaThresholdPct &&
            now - _manaFiredAt >= TimeSpan.FromMilliseconds(_settings.ManaCooldownMs))
        {
            SendInputNative.Tap((ushort)_settings.ManaKey); _manaFiredAt = now; _flaskNote = $"mana@{v.ManaPct:F0}%";
        }
    }

    /// <summary>Poll overlay hotkeys: F8 auto-flask toggle, F9 quit, F12 dashboard, F6/F7 path targets.
    /// Map calibration is web-config-only (no in-game keys, to avoid accidental presses).</summary>
    private void HandleHotkeys()
    {
        // F8 master kill-switch for auto-flask (debounced).
        if (Down(0x77) && DateTime.UtcNow >= _nextToggleAt)
        {
            _autoFlask = !_autoFlask;
            _nextToggleAt = DateTime.UtcNow.AddMilliseconds(300);
            Console.WriteLine($"\nAuto-flask: {(_autoFlask ? "ON" : "OFF")}");
        }
        // F9 quits the overlay (besides the tray-icon Exit).
        if (Down(0x78)) { Console.WriteLine("\nF9 — exiting."); RequestShutdown(); }

        // F12 opens the web dashboard in the default browser — only while PoE2 is the foreground
        // window (debounced). Purely launches a browser; sends nothing to the game.
        if (Down(0x7B) && DateTime.UtcNow >= _nextBrowserAt
            && _gameHwnd != 0 && GetForegroundWindow() == _gameHwnd)
        {
            _nextBrowserAt = DateTime.UtcNow.AddMilliseconds(800);
            OpenDashboard();
        }

        // F6 adds the nearest not-yet-selected landmark to the path selection; F7 clears it.
        // Both debounced.
        if (DateTime.UtcNow >= _nextPathKeyAt)
        {
            if (Down(AddNearestVk))
            {
                AddNearestPathTarget();
                _nextPathKeyAt = DateTime.UtcNow.AddMilliseconds(300);
            }
            else if (Down(ClearPathsVk))
            {
                ClearPathTargets();
                _nextPathKeyAt = DateTime.UtcNow.AddMilliseconds(300);
            }
        }

        // Atlas calibration: F10 = capture a tile↔cursor point (Shift+F10 = reset); F11 = solve + apply.
        if (DateTime.UtcNow >= _nextCalibKeyAt)
        {
            if (Down(0x79)) // F10
            {
                _nextCalibKeyAt = DateTime.UtcNow.AddMilliseconds(250);
                if ((GetAsyncKeyState(0x10) & 0x8000) != 0) { _atlasCalibPts.Clear(); Console.WriteLine("\nAtlas calib: points reset."); }
                else AtlasCalibCapture();
            }
            else if (Down(0x7A)) // F11
            {
                _nextCalibKeyAt = DateTime.UtcNow.AddMilliseconds(250);
                AtlasCalibSolve();
            }
        }
    }

    /// <summary>F10: capture one calibration point — the tile (IconType==0 node) under the cursor and the
    /// cursor's screen position. Uses the FIXED rough projection to find the nearest tile (stable across
    /// captures); the actual fit is solved later from all points.</summary>
    private void AtlasCalibCapture()
    {
        if (_inGameStateForApi == 0 || !GetCursorPos(out var pt)) { Console.WriteLine("\nAtlas calib: not in game."); return; }
        double cx = pt.X, cy = pt.Y; nint best = 0; double bd = 1e18, brx = 0, bry = 0;
        foreach (var n in _atlas.ReadNodes(_inGameStateForApi))
        {
            if (n.IconType != 0 || !n.Visible) continue; // only clickable tiles (type>0 = offset tags)
            var (sx, sy) = ProjectPick(n.X, n.Y);
            var d = (sx - cx) * (sx - cx) + (sy - cy) * (sy - cy);
            if (d < bd) { bd = d; best = n.Element; brx = n.X; bry = n.Y; }
        }
        if (best == 0) { Console.WriteLine("\nAtlas calib: no tile near cursor (in the Atlas?)."); return; }
        _atlasCalibPts.Add(new[] { brx, bry, cx, cy });
        Console.WriteLine($"\nAtlas calib: point {_atlasCalibPts.Count} (relPos {brx:F0},{bry:F0} -> screen {cx:F0},{cy:F0}, pickDist {Math.Sqrt(bd):F0}px). F11 to solve.");
    }

    /// <summary>F11: solve the canvas→screen homography (least-squares, outlier-rejecting) from the
    /// captured points and apply it live + persist. Needs 4+ points; 6-8 spread points give the best fit.</summary>
    private void AtlasCalibSolve()
    {
        if (_atlasCalibPts.Count < 4) { Console.WriteLine($"\nAtlas calib: need 4+ points (have {_atlasCalibPts.Count})."); return; }
        var pts = _atlasCalibPts.Select(p => (double[])p.Clone()).ToList();

        // Robust affine RANSAC: the dominant error is MIS-PICKS (a capture grabbed the wrong tile near the
        // cursor) — not perspective. Keep the largest self-consistent subset, reject the rest. The atlas
        // mapping is ~uniform scale + offset, so affine is the right model; a homography just over-fits a
        // handful of hand-captured points (and the perspective it "finds" is the fit bending to outliers).
        var rob = AtlasHomography.RobustFit(pts, inlierPx: 12);
        if (rob == null)
        {
            Console.WriteLine($"\nAtlas calib: no consistent set among {pts.Count} pts. Likely all mis-picks — " +
                              "recapture clicking tile CENTERS, well spread (corners + middle). Shift+F10 resets.");
            return;
        }
        var (sol, inlierIdx) = rob.Value;
        var inliers = inlierIdx.Select(i => pts[i]).ToList();
        var rejected = pts.Count - inliers.Count;

        // Perspective upgrade — a safety valve only. The model is scale+translation (validated); a
        // homography needs a STRONG, well-supported signal to win (≥6 clean inliers, clearly lower error,
        // tiny persp terms) or it just overfits a handful of hand-captured points.
        var affMax = inliers.Max(p => AtlasHomography.Resid(sol, p));
        var hom = AtlasHomography.Fit(inliers);
        var usedPersp = false;
        if (hom != null && inliers.Count >= 6)
        {
            var homMax = inliers.Max(p => AtlasHomography.Resid(hom, p));
            if (homMax < affMax * 0.5 && Math.Abs(hom[6]) < 1e-3 && Math.Abs(hom[7]) < 1e-3) { sol = hom; usedPersp = true; }
        }

        _settings.AtlasScale = (float)sol[0]; _settings.AtlasShearX = (float)sol[1]; _settings.AtlasOffX = (float)sol[2];
        _settings.AtlasShearY = (float)sol[3]; _settings.AtlasScaleY = (float)sol[4]; _settings.AtlasOffY = (float)sol[5];
        _settings.AtlasPersX = (float)sol[6]; _settings.AtlasPersY = (float)sol[7];
        _settings.AtlasCalibZoom = _atlasZoom > 0.01f ? _atlasZoom : 0.85f; // anchor zoom for live rescale
        _settings.AtlasCalibrated = true; // this manual solve now OVERRIDES the auto window-height projection
        _settings.Save();

        var maxErr = inliers.Max(p => AtlasHomography.Resid(sol, p));
        var rmsErr = Math.Sqrt(inliers.Average(p => { var r = AtlasHomography.Resid(sol, p); return r * r; }));
        Console.WriteLine($"\nAtlas calib: SOLVED + applied — {inliers.Count} inliers, {rejected} rejected (mis-picks). " +
                          $"model={(usedPersp ? "homography" : "scale+translate")}  max {maxErr:F1}px  rms {rmsErr:F1}px. Rings updated live.");
        if (!usedPersp) Console.WriteLine($"  uniform scale≈{sol[0]:F3}/{sol[4]:F3}  offset=({sol[2]:F0},{sol[5]:F0})  (UIscale×zoom; will need rescaling on zoom)");
        for (var i = 0; i < pts.Count; i++)
            Console.WriteLine($"    pt{i}: relPos=({pts[i][0]:F0},{pts[i][1]:F0}) -> err {AtlasHomography.Resid(sol, pts[i]):F1}px {(inlierIdx.Contains(i) ? "" : "[REJECTED mis-pick]")}");
    }

    /// <summary>Project a node's canvas relPos to screen for tile PICKING. Uses the live applied transform
    /// (settings) when it's been calibrated — accurate picks → fewer mis-grabs — falling back to the rough
    /// reference before the first solve. Safe against a slightly-off transform now that the solve is
    /// RANSAC-robust (a few bad picks are rejected, so picking can't poison the fit the way it used to).</summary>
    private (double sx, double sy) ProjectPick(float x, float y)
    {
        // Same projection the renderer uses: the live window-height-derived auto transform until the user
        // has a manual F10/F11 solve, then their calibrated homography. Resolution-correct either way, so
        // picks land on the right tile at any resolution (was previously a 1080p-only rough fallback).
        var s = AtlasProjection();
        double w = s[6] * x + s[7] * y + 1; if (Math.Abs(w) < 1e-6) w = 1;
        return ((s[0] * x + s[1] * y + s[2]) / w, (s[3] * x + s[4] * y + s[5]) / w);
    }

    /// <summary>Live zoom rescale factor for the calibrated transform's linear part: liveZoom / calibZoom.
    /// 1.0 when at the calibrated zoom. Guards against a zero/garbage calib zoom.</summary>
    private float AtlasZoomK
    {
        get { var cz = _settings.AtlasCalibZoom; return cz > 0.01f && _atlasZoom > 0.01f ? _atlasZoom / cz : 1f; }
    }

    /// <summary>The resolution-correct atlas projection (uniform scale + canvas-origin offset) derived LIVE
    /// from the game window height and the live atlas zoom: screen = relPos × (UIscale×zoom) + offset, with
    /// UIscale = winH/1600. Both scale and offset scale together off the 1080p reference, so this lines up at
    /// any resolution with no hand-calibration. Used unless the user has an F10/F11 manual solve
    /// (<see cref="RadarSettings.AtlasCalibrated"/>), which then overrides it. Returned as the 8-coeff
    /// homography layout (shear + perspective = 0).</summary>
    private double[] AutoAtlasProjection()
    {
        float uiScale = _window.Height > 0 ? _window.Height / 1600f : 1080f / 1600f;
        float scale = uiScale * (_atlasZoom > 0.01f ? _atlasZoom : 0.85f);
        float k = scale / AtlasRefScale; // offset scales with the same factor as the scale
        return new double[] { scale, 0, AtlasRefOffset.X * k, 0, scale, AtlasRefOffset.Y * k, 0, 0 };
    }

    /// <summary>The atlas projection coefficients to render/pick with this frame: the user's manual F10/F11
    /// solve (zoom-rescaled) when calibrated, otherwise the live window-height-derived auto projection.</summary>
    private double[] AtlasProjection()
    {
        if (!_settings.AtlasCalibrated) return AutoAtlasProjection();
        double k = AtlasZoomK;
        return new double[] { _settings.AtlasScale * k, _settings.AtlasShearX * k, _settings.AtlasOffX,
                              _settings.AtlasShearY * k, _settings.AtlasScaleY * k, _settings.AtlasOffY,
                              _settings.AtlasPersX, _settings.AtlasPersY };
    }

    // ── Unified navigation-target selection (draw-only guidance, multi-select). ──────────────
    // Model: _navTargets is one list built each world tick from BOTH tile landmarks AND entity POIs,
    // each addressed by a STABLE STRING id ("t:<path>" / "e:<entityId>"). _selectedIds is the ordered
    // set of selected ids; an id's position in that list is its color SLOT (0..7), so each selected
    // target draws its own A* route + legend swatch in its own color. F6 adds the nearest not-yet-
    // selected target; F7 clears all; clicking a legend row toggles that target. The selection is
    // capped at MaxSelectedTargets (palette size) so colors stay distinct and per-tick planning is
    // bounded. On a zone change the selection is cleared and the persistent auto-nav patterns re-
    // select matching targets.

    /// <summary>
    /// Build the unified navigation-target list for this world tick: every tile landmark first, then
    /// qualifying entity POIs nearest-first. An entity qualifies (is selectable) when it's alive AND
    /// (game-flagged POI, OR a unique monster, OR its display rule has the Auto-path flag). Each target
    /// carries <see cref="NavTarget.AutoPath"/> — true when its display rule opts into auto-pathing —
    /// which drives the zone-entry auto-selection (replacing the old AutoNavPatterns list). Deduped by id.
    /// </summary>
    private List<NavTarget> BuildNavTargets(NumVec2 player)
    {
        var targets = new List<NavTarget>(_landmarks.Count + 16);
        var seen = new HashSet<string>();

        // (a) Tile landmarks — id "t:<key>" (per-cluster). Auto-path when a Tile rule opts in.
        foreach (var lm in _landmarks)
        {
            var id = "t:" + lm.Key;
            if (!seen.Add(id)) continue;
            var autoPath = _displayRules.ResolveTile(lm.Path, requireMatch: false)?.Navigable ?? false;
            targets.Add(new NavTarget(id, LandmarkLabel(lm), lm.Center, lm.Path, IsEntity: false, AutoPath: autoPath));
        }

        // (b) Entity POIs — id "e:<entityId>", nearest-first. Selectable if POI/unique/Auto-path rule;
        // AutoPath true only when the matched rule's Auto-path flag is set.
        var pois = _entities
            .Where(e => e.IsAlive && !e.IconComplete)
            .Select(e => (e, nav: _displayRules.Resolve(e)?.Navigable ?? false))
            .Where(x => x.e.Poi
                        || (x.e.Category == Poe2Live.EntityCategory.Monster && x.e.Rarity == Poe2Live.Rarity.Unique)
                        || x.nav)
            .OrderBy(x => NumVec2.DistanceSquared(x.e.Grid, player));
        foreach (var (e, nav) in pois)
        {
            var id = "e:" + e.Id;
            if (!seen.Add(id)) continue;
            targets.Add(new NavTarget(id, EntityLabel(e.Metadata), e.Grid, e.Metadata, IsEntity: true, AutoPath: nav));
        }

        return targets;
    }

    /// <summary>Zone change: remember the leaving zone's selection (by its instance hash), then either
    /// RESTORE the selection we previously had for the zone we're entering (so a town round-trip keeps
    /// your pathing) or — on a first visit — seed it from the persistent auto-nav patterns. Trackers are
    /// NOT touched here — the per-tick reconciliation (ReconcileTrackers) syncs them to _selectedIds.</summary>
    private void OnAreaChanged()
    {
        int count; bool restored;
        lock (_navLock)
        {
            // Save what was selected in the zone we're leaving, keyed by ITS instance hash.
            if (_selectionAreaHash != 0) RememberZoneSelection(_selectionAreaHash, _selectedIds);

            _selectedIds.Clear();
            _selectionCapWarned = false;
            _selectionAreaHash = _areaHash;

            // Returning to a remembered instance → restore its selection verbatim (the user's explicit
            // choices win, including an intentionally-empty one, so a zone they cleared stays cleared).
            List<string>? remembered = null;
            restored = _areaHash != 0 && _zoneSelections.TryGetValue(_areaHash, out remembered);
            if (restored)
            {
                foreach (var id in remembered!)
                {
                    if (_selectedIds.Count >= MaxSelectedTargets) break;
                    if (!_selectedIds.Contains(id)) _selectedIds.Add(id);
                }
            }
            else
            {
                // First visit to this instance: auto-select every target whose display rule opted into
                // auto-pathing (the per-rule "Auto-path" flag), capped so colors/planning stay bounded.
                foreach (var t in _navTargets)
                {
                    if (_selectedIds.Count >= MaxSelectedTargets) break;
                    if (t.AutoPath && !_selectedIds.Contains(t.Id))
                        _selectedIds.Add(t.Id);
                }
            }
            count = _selectedIds.Count;
        }
        _selectedPaths = new List<SelectedPath>();

        if (count > 0)
            Console.WriteLine($"\nNav: {(restored ? "restored" : "auto-selected")} {count} target(s) on zone change.");
    }

    /// <summary>
    /// Drop selected ENTITY targets the game has marked complete (IconComplete — e.g. a claimed
    /// expedition / used incursion device). Such an entity is hidden from the map and excluded from
    /// the nav-target list, but it lingers (faded) in the live entity set, so <see cref="TryResolveTargetGrid"/>
    /// would still resolve it and the route would keep pathing there. Pruning the id stops the route
    /// (its tracker is removed by the next ReconcileTrackers) and "sticks" via the per-zone memory.
    /// <para>Only prunes targets whose entity is PRESENT-and-complete — an entity merely out of network
    /// range (temporarily absent) is left selected so it resumes when you return to it.</para>
    /// </summary>
    private void PruneCompletedTargets()
    {
        lock (_navLock)
        {
            if (_selectedIds.Count == 0) return;
            _selectedIds.RemoveAll(id =>
            {
                if (!id.StartsWith("e:", StringComparison.Ordinal) || !uint.TryParse(id.AsSpan(2), out var eid))
                    return false;
                foreach (var e in _entities)
                    if (e.Id == eid) return e.IconComplete; // present → prune iff completed; else keep
                return false; // absent (out of range) → keep; it may return
            });
        }
    }

    /// <summary>Store a copy of <paramref name="ids"/> under <paramref name="hash"/>, evicting the
    /// oldest remembered zone when the table is full. Call under <see cref="_navLock"/>.</summary>
    private void RememberZoneSelection(uint hash, List<string> ids)
    {
        if (!_zoneSelections.ContainsKey(hash))
        {
            if (_zoneOrder.Count >= MaxRememberedZones)
            {
                _zoneSelections.Remove(_zoneOrder[0]);
                _zoneOrder.RemoveAt(0);
            }
            _zoneOrder.Add(hash);
        }
        _zoneSelections[hash] = new List<string>(ids);
    }

    /// <summary>Surfacing matcher fed to Poe2Live: a terrain tile surfaces as a landmark when a user
    /// landmark pattern matches OR a (non-hide) "Tile" display rule with explicit match terms matches.
    /// Returns the label to show (empty string = use the tile's derived name), or null to not surface.</summary>
    private string? TileLandmarkMatch(string tilePath)
    {
        var tr = _displayRules.ResolveTile(tilePath, requireMatch: true);
        return tr is { Hide: false } ? (tr.Label ?? "") : null;
    }

    /// <summary>Distinct terrain-tile paths for the current area (served by /api/tiles for the add-rule
    /// picker). Empty when not in game. Cached per area inside Poe2Live.</summary>
    private IReadOnlyList<string> CurrentTilePaths()
        => _areaInstanceForApi != 0 ? _live.TilePaths(_areaInstanceForApi) : Array.Empty<string>();


    /// <summary>F6: add the nearest navigation target not already selected into the selection.</summary>
    private void AddNearestPathTarget()
    {
        if (_navTargets.Count == 0) return;
        var player = _state.Player;

        // _navTargets isn't fully distance-sorted (tiles come first), so scan for the nearest
        // unselected target by grid distance. Snapshot the selection to test membership.
        var selected = SnapshotSelection();
        var bestId = (string?)null;
        var bestD = float.MaxValue;
        foreach (var t in _navTargets)
        {
            if (selected.Contains(t.Id)) continue;
            var d = NumVec2.DistanceSquared(t.Grid, player);
            if (d < bestD) { bestD = d; bestId = t.Id; }
        }
        if (bestId is not null) ToggleSelectionCore(bestId); // shares the cap check + locked mutate + log
    }

    /// <summary>F7: clear the entire path selection. Only edits _selectedIds (under the lock); the
    /// per-tick reconciliation removes the now-orphaned trackers.</summary>
    private void ClearPathTargets()
    {
        bool wasEmpty;
        lock (_navLock)
        {
            wasEmpty = _selectedIds.Count == 0;
            _selectedIds.Clear();
            _selectionCapWarned = false;
        }
        if (!wasEmpty) Console.WriteLine("\nPath targets: cleared");
    }

    /// <summary>
    /// Toggle a navigation target by its stable id (legend-row click / F6 / API). Delegates to the
    /// single locked toggle core so in-game and API mutations share identical semantics.
    /// </summary>
    private void TogglePathTarget(string id) => ToggleSelectionCore(id);

    /// <summary>
    /// THE one place the selection set is mutated. Adds the id if absent (unless at the cap), removes
    /// it if present — all under <see cref="_navLock"/>. Does NOT touch trackers (those are created/
    /// removed by the tick-thread reconciliation from _selectedIds), so it is safe to call from the
    /// HTTP thread. Returns the new selection labels for logging.
    /// </summary>
    private void ToggleSelectionCore(string id)
    {
        if (string.IsNullOrEmpty(id)) return;

        bool changed;
        string labels;
        lock (_navLock)
        {
            if (_selectedIds.Remove(id))
            {
                _selectionCapWarned = false;
                changed = true;
            }
            else if (_selectedIds.Count >= MaxSelectedTargets)
            {
                if (!_selectionCapWarned)
                {
                    Console.WriteLine($"\nPath targets: selection full ({MaxSelectedTargets}); ignoring add.");
                    _selectionCapWarned = true;
                }
                return; // over cap — ignore the add
            }
            else
            {
                _selectedIds.Add(id);
                changed = true;
            }

            labels = _selectedIds.Count == 0 ? "none" : string.Join(", ", _selectedIds.Select(TargetLabel));
        }

        if (changed) Console.WriteLine($"\nPath targets: {labels}");
    }

    /// <summary>Snapshot the current selection ids (under the lock) into a fresh list — the standard
    /// way every reader observes the selection without holding the lock during its work.</summary>
    private List<string> SnapshotSelection()
    {
        lock (_navLock) return new List<string>(_selectedIds);
    }

    /// <summary>
    /// Tick-thread tracker reconciliation: bring the (tick-thread-owned) <see cref="_trackers"/> map in
    /// line with the selection. Creates a <see cref="RouteTracker"/> (and enqueues its initial replan)
    /// for any selected id lacking one, and removes trackers whose id is no longer selected (their
    /// in-flight results are ignored on drain). This is the ONLY code that adds/removes trackers, so
    /// API-thread selection edits never race the tracker map. Takes a selection snapshot.
    /// </summary>
    private void ReconcileTrackers(List<string> selected)
    {
        // Remove trackers no longer selected.
        if (_trackers.Count > 0)
        {
            var live = new HashSet<string>(selected);
            var stale = _trackers.Keys.Where(k => !live.Contains(k)).ToList();
            foreach (var id in stale) _trackers.Remove(id);
        }

        // Create trackers for newly-selected ids and kick off their first plan.
        foreach (var id in selected)
        {
            if (_trackers.ContainsKey(id)) continue;
            var tracker = new RouteTracker();
            _trackers[id] = tracker;
            if (TryResolveTargetGrid(id, out var grid))
                EnqueueReplan(id, tracker, grid);
        }
    }

    /// <summary>
    /// Resolve ANY selected id to its current goal grid against the live world (not just the curated
    /// <see cref="_navTargets"/> menu), so the dashboard can navigate to any entity/landmark:
    /// <list type="bullet">
    /// <item>"t:&lt;path&gt;" → the landmark in <see cref="_landmarks"/> whose Path matches; grid = Center.</item>
    /// <item>"e:&lt;id&gt;" → the entity in <see cref="_entities"/> whose Id matches; grid = Grid.</item>
    /// </list>
    /// Returns false if the id is malformed or the target isn't present this tick (despawned / other
    /// zone) — callers keep the id selected and simply skip planning until it resolves.
    /// </summary>
    private bool TryResolveTargetGrid(string id, out NumVec2 grid)
    {
        grid = default;
        if (string.IsNullOrEmpty(id) || id.Length < 2) return false;

        if (id.StartsWith("t:", StringComparison.Ordinal))
        {
            var key = id[2..];
            foreach (var lm in _landmarks)
                if (lm.Key == key) { grid = lm.Center; return true; }
            return false;
        }

        if (id.StartsWith("e:", StringComparison.Ordinal))
        {
            if (!uint.TryParse(id[2..], out var entityId)) return false;
            foreach (var e in _entities)
                if (e.Id == entityId) { grid = e.Grid; return true; }
            return false;
        }

        return false;
    }

    /// <summary>
    /// Per-tick route maintenance — runs on the tick thread, NEVER calls A*. Snapshots the selection
    /// (once, under the lock), reconciles the tracker map to it, then for each selected target:
    /// advance its cursor (cheap), and if a trigger fires and no replan is in flight, enqueue a
    /// BACKGROUND replan toward the target's resolved grid. Then drain finished routes into the
    /// trackers and rebuild <see cref="_selectedPaths"/> from the trackers' cursors.
    /// </summary>
    private void MaintainRoutes(NumVec2 player)
    {
        // Snapshot the selection ONCE; everything below works off this local list (tick-thread only).
        var selected = SnapshotSelection();

        // (a) Bring the tick-thread-owned tracker map in line with the selection (create/remove).
        ReconcileTrackers(selected);

        // (b) Maintain + trigger replans. Resolve each id to its live grid; if it doesn't resolve this
        //     tick (despawned / not yet present) keep it selected but skip planning.
        foreach (var id in selected)
        {
            if (!_trackers.TryGetValue(id, out var tracker)) continue;
            tracker.Maintain(player);
            if (!TryResolveTargetGrid(id, out var goal)) continue;
            if (!tracker.ReplanInFlight && tracker.ShouldReplan(player, goal))
                EnqueueReplan(id, tracker, goal);
        }

        // (c) Drain completed background routes; apply only those still tracked.
        if (_replanner.TryDrainResults(out var results))
        {
            foreach (var r in results)
            {
                if (!_trackers.TryGetValue(r.TargetId, out var tracker)) continue; // deselected → ignore
                tracker.ApplyResult(r.Waypoints, new NumVec2(r.Goal.x, r.Goal.y));
                Console.WriteLine($"replan: {TargetLabel(r.TargetId)} = {r.Waypoints.Count} waypoints");
            }
        }

        // (d) Cheap rebuild of the draw list from each tracker's current (cursor-advanced) points.
        RebuildSelectedPaths(selected);
    }

    /// <summary>Snapshot the immutable terrain + player/goal and hand a replan request to the worker
    /// (marks the tracker in-flight). No A* on this thread.</summary>
    private void EnqueueReplan(string id, RouteTracker tracker, NumVec2 goal)
    {
        if (_terrain is not { } terrain) return; // can't plan without terrain yet
        var player = _state.Player;
        tracker.MarkReplanRequested();
        _replanner.Enqueue(new BackgroundReplanner.Request(
            id, terrain, ((int)player.X, (int)player.Y), ((int)goal.X, (int)goal.Y)));
    }

    /// <summary>Rebuild <see cref="_selectedPaths"/> from the trackers' CurrentPoints, each colored by
    /// its id's selection-order slot (capped at the palette size). CHEAP — no A*. Takes a selection
    /// snapshot so it never touches _selectedIds directly.</summary>
    private void RebuildSelectedPaths(List<string> selected)
    {
        var paths = new List<SelectedPath>(selected.Count);
        for (var i = 0; i < selected.Count; i++)
        {
            if (!_trackers.TryGetValue(selected[i], out var tracker)) continue;
            var pts = tracker.CurrentPoints;
            if (pts.Count > 0) paths.Add(new SelectedPath(Math.Min(i, MaxSelectedTargets - 1), pts));
        }
        _selectedPaths = paths;
    }

    /// <summary>Display label for a selected id (its NavTarget name if still present, else the raw id).</summary>
    private string TargetLabel(string id)
    {
        foreach (var t in _navTargets) if (t.Id == id) return t.Name;
        return id;
    }

    /// <summary>Friendly display label for a tile landmark (curated if enabled + present, else derived).</summary>
    private string LandmarkLabel(Poe2Live.Landmark lm)
        => _settings.UseCuratedLandmarks && lm.CuratedName is { } c ? c : lm.Name;

    /// <summary>
    /// Turn an entity metadata path into a readable label: take the last '/'-segment, strip a trailing
    /// "_NN"/digit run, and insert spaces before interior capitals
    /// (e.g. ".../Expedition2/Expedition2Encounter" → "Expedition Encounter";
    /// "Waypoint_LongActivationRadius" → "Waypoint Long Activation Radius").
    /// </summary>
    private static string EntityLabel(string metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return "(entity)";

        // Prefer a curated friendly name from the entity-name table when one exists
        // (e.g. "Lightning Wraith"); fall back to the path-derived prettifier below.
        if (EntityNameResolver.Shared.Resolve(metadata) is { Length: > 0 } resolved)
            return resolved;

        var slash = metadata.LastIndexOf('/');
        var seg = slash >= 0 ? metadata[(slash + 1)..] : metadata;

        // Strip a trailing "_NN" or trailing digit run (e.g. "Expedition2Encounter" keeps the
        // interior "2"; "Encounter_03" → "Encounter").
        var end = seg.Length;
        while (end > 0 && char.IsDigit(seg[end - 1])) end--;
        if (end > 0 && seg[end - 1] == '_') end--;
        if (end > 0) seg = seg[..end];

        // Insert spaces before interior capitals / before a digit-to-letter or letter-to-digit edge.
        var sb = new System.Text.StringBuilder(seg.Length + 8);
        for (var i = 0; i < seg.Length; i++)
        {
            var ch = seg[i];
            if (i > 0)
            {
                var prev = seg[i - 1];
                var boundary = (char.IsUpper(ch) && (char.IsLower(prev) || char.IsDigit(prev)))
                               || (char.IsDigit(ch) && char.IsLetter(prev) && !char.IsDigit(prev));
                if (boundary && sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            }
            sb.Append(ch);
        }
        var label = sb.ToString().Trim();
        return label.Length == 0 ? "(entity)" : label;
    }

    /// <summary>Build the legend rows (one per unified navigation target), marking the selected targets
    /// and their selection-order color slot (-1 when unselected). Takes a selection snapshot so it
    /// doesn't touch _selectedIds while the API thread may be mutating it.</summary>
    private List<LegendEntry> BuildLegend(List<string> selected)
    {
        var legend = new List<LegendEntry>(_navTargets.Count);
        foreach (var t in _navTargets)
        {
            var slot = selected.IndexOf(t.Id);
            legend.Add(new LegendEntry(t, slot, slot >= 0));
        }
        return legend;
    }

    // ── Public navigation accessors (callable from the API/HTTP thread; all _navLock-guarded). ──

    /// <summary>API: a snapshot of the selected ids with their slot (index in selection order).
    /// Safe to call concurrently with the tick loop.</summary>
    public IReadOnlyList<(string Id, int Slot)> GetNavSelection()
    {
        lock (_navLock)
        {
            var list = new List<(string, int)>(_selectedIds.Count);
            for (var i = 0; i < _selectedIds.Count; i++) list.Add((_selectedIds[i], i));
            return list;
        }
    }

    /// <summary>API: toggle a nav target by id — add if absent (respecting the cap), remove if present.
    /// Shares the exact locked core the in-game toggle uses; only edits _selectedIds (trackers are
    /// reconciled on the tick thread). Safe to call concurrently with the tick loop.</summary>
    public void ToggleNavTarget(string id) => ToggleSelectionCore(id);

    /// <summary>API: clear the whole nav selection. Safe to call concurrently with the tick loop.</summary>
    public void ClearNavSelection() => ClearPathTargets();

    /// <summary>API (/api/atlas): a JSON-ready snapshot of the atlas map-data we can read — the full
    /// map-archetype catalog and the set of map types present in the current atlas region. Inspection /
    /// validation only (no spatial graph yet — see resources/atlas-research-notes.md). The reader scans
    /// + caches, so the first call after entering the atlas may take a moment; called on the API thread.</summary>
    private object AtlasJson()
    {
        // Anchor the scan to the live game-heap slab (the catalog shares the arena with AreaInstance).
        var d = _atlas.Read(_lastAreaInstance);
        // Live node graph (atlas nodes are UiElements) — summary + the locally-visible highlight set.
        var nodes = _inGameStateForApi != 0 ? _atlas.ReadNodes(_inGameStateForApi) : new List<Poe2Atlas.AtlasNodeLive>();
        var vis = nodes.Where(n => n.Visible).ToList();
        return new
        {
            located = d.Located,
            note = d.Note,
            catalogAddr = $"0x{d.CatalogAddr:X}",
            catalogCount = d.CatalogCount,
            regionCount = d.Region.Count,
            catalog = d.Catalog.Select(m => new { id = m.Id, code = m.Code, name = m.Name, kind = m.Kind, parsedObj = $"0x{m.ParsedObj:X}" }),
            region = d.Region.Select(r => new { code = r.Code, name = r.Name, kind = r.Kind }),
            nodes = new
            {
                total = nodes.Count,
                visible = vis.Count,
                hasContent = nodes.Count(n => n.HasContent),
                unvisited = nodes.Count(n => !n.Visited),
                unlocked = nodes.Count(n => n.Unlocked),
                biomes = nodes.GroupBy(n => (int)n.Biome).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => g.Count()),
            },
            // Every distinct content tag currently on the atlas (+ count), for the dashboard's filter /
            // highlight-rule pickers. These are the readable content/mechanic names (Powerful Map Boss,
            // Breach, Delirium, …) resolved from each node's EndgameMapAtlas row.
            allTags = nodes.SelectMany(n => n.Tags).GroupBy(t => t).OrderByDescending(g => g.Count())
                .Select(g => new { tag = g.Key, count = g.Count() }),
            // Distinct MAP NAMES (Sun Temple, Precursor Tower, Vaal City, …) — the separate "Map" filter
            // group, so towers/temples/specific maps are highlightable independently of rolled content.
            allMaps = nodes.Where(n => !string.IsNullOrEmpty(n.MapName)).GroupBy(n => n.MapName)
                .OrderBy(g => g.Key).Select(g => new { tag = g.Key, count = g.Count() }),
            // The currently active rules (persisted): tracked tags (rings) + arrow tags (off-screen
            // direction). Match against BOTH content tags and map names.
            highlightTags = _settings.AtlasHighlightTags,
            arrowTags = _settings.AtlasArrowTags,
            // The individual live nodes for the dashboard's grid. On-screen first, then content/unvisited.
            nodeList = nodes
                .OrderByDescending(n => n.Visible).ThenByDescending(n => n.HasContent).ThenByDescending(n => !n.Visited)
                .Take(2000)
                .Select(n => new
                {
                    el = ((long)n.Element).ToString(), // unique stable key (element address) for selection
                    id = n.Id, biome = (int)n.Biome, type = n.IconType, hasContent = n.HasContent,
                    unlocked = n.Unlocked, visited = n.Visited, visible = n.Visible,
                    x = (int)n.X, y = (int)n.Y, map = n.MapName, tags = n.Tags,
                }),
        };
    }

    /// <summary>Read the live atlas nodes and build the highlight-mark list for the renderer. Cheap when
    /// the atlas is closed (ReadNodes returns empty via its visibility gate). When open, marks the
    /// on-screen nodes + any dashboard-selected nodes; the renderer projects + culls them.</summary>
    private void BuildAtlasMarks(nint inGameState)
    {
        var nodes = _atlas.ReadNodes(inGameState);
        if (nodes.Count == 0) { _atlasOpen = false; if (_atlasMarks.Count > 0) _atlasMarks = new(); return; }
        _atlasOpen = true;
        // Live zoom = the nodes' shared canvas scale (+0x130). Use the median (robust to a stray 0/odd node).
        var scales = nodes.Where(n => n.Scale > 0.01f).Select(n => n.Scale).OrderBy(s => s).ToList();
        if (scales.Count > 0) _atlasZoom = scales[scales.Count / 2];
        HashSet<nint> sel; lock (_atlasLock) sel = new HashSet<nint>(_atlasSel);

        // One-time default: track + arrow every Citadel (high-value, usually off-screen) until the user
        // edits the rules from the dashboard. Boss is intentionally NOT defaulted (too common). Wait until
        // tag resolution has caught up (it's budget-limited per tick) so we seed ALL citadels, not just the
        // first batch resolved.
        if (!_settings.AtlasRulesInitialized && _atlas.AllTagsResolved)
        {
            var cit = nodes.Where(n => !string.IsNullOrEmpty(n.MapName) && n.MapName.Contains("Citadel", StringComparison.OrdinalIgnoreCase))
                           .Select(n => n.MapName).Distinct().ToList();
            if (cit.Count > 0)
            {
                _settings.AtlasHighlightTags = new List<string>(cit);
                _settings.AtlasArrowTags = new List<string>(cit);
                foreach (var c in cit) _settings.AtlasHighlightColors[c] = "#e0b341"; // Citadel gold
                _settings.AtlasRulesInitialized = true;
                _settings.Save();
            }
        }

        // A node matches a rule set if its map name or one of its content tags is in the set; returns the
        // matched tag (drives label + colour). Track set ⇒ draw a ring; Arrow set ⇒ off-screen edge arrow.
        var hlTrack = new HashSet<string>(_settings.AtlasHighlightTags ?? new(), StringComparer.OrdinalIgnoreCase);
        var hlArrow = new HashSet<string>(_settings.AtlasArrowTags ?? new(), StringComparer.OrdinalIgnoreCase);
        static string? Match(HashSet<string> set, in Poe2Atlas.AtlasNodeLive nd)
        {
            if (set.Count == 0) return null;
            if (!string.IsNullOrEmpty(nd.MapName) && set.Contains(nd.MapName)) return nd.MapName;
            if (nd.Tags is { Count: > 0 }) foreach (var t in nd.Tags) if (set.Contains(t)) return t;
            return null;
        }
        var marks = new List<AtlasMark>(128);
        foreach (var n in nodes)
        {
            var selected = sel.Contains(n.Element);
            var mTrack = Match(hlTrack, n);
            var mArrow = Match(hlArrow, n);
            var isTracked = selected || mTrack != null;
            var isArrow = mArrow != null;
            // ONLY tracked/arrow maps are drawn (the point: surface content the game hides). AtlasDrawAll
            // debug overrides this to draw every node.
            if (!_settings.AtlasDrawAll && !isTracked && !isArrow) continue;
            var matched = mTrack ?? mArrow;
            var label = matched ?? (n.Tags is { Count: > 0 } ? n.Tags[0] : (string.IsNullOrEmpty(n.MapName) ? null : n.MapName));
            string? color = matched != null && _settings.AtlasHighlightColors.TryGetValue(matched, out var c) ? c : null;
            marks.Add(new AtlasMark(n.X, n.Y, isTracked, n.HasContent, n.Visited, n.Unlocked, n.Biome, n.IconType, label, color, isArrow));
        }
        _atlasMarks = marks;
    }

    /// <summary>API: set the dashboard-selected atlas nodes (by element address) to highlight in-game.
    /// Draw-only — never sends input to the game. Safe to call from the API thread.</summary>
    public void SetAtlasSelection(IReadOnlyList<long> els)
    {
        lock (_atlasLock) { _atlasSel.Clear(); foreach (var e in els) _atlasSel.Add((nint)e); }
    }

    /// <summary>API: set the active atlas highlight rules (tag + ring colour). Only nodes whose content
    /// tags or map name match one of these are drawn in-game, in the rule's colour. Persisted; applied on
    /// the next world tick. Draw-only.</summary>
    public void SetAtlasHighlight(IReadOnlyList<(string tag, string color, bool track, bool arrow)> rules)
    {
        var tags = new List<string>(); var arrows = new List<string>();
        var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tag, color, track, arrow) in rules)
        {
            if (string.IsNullOrWhiteSpace(tag) || !seen.Add(tag)) continue;
            if (track) tags.Add(tag);
            if (arrow) arrows.Add(tag);
            if (!string.IsNullOrWhiteSpace(color)) colors[tag] = color;
        }
        _settings.AtlasHighlightTags = tags;
        _settings.AtlasArrowTags = arrows;
        _settings.AtlasHighlightColors = colors;
        _settings.AtlasRulesInitialized = true;   // any explicit edit locks out the Citadel default-seed
        _settings.Save();
    }

    /// <summary>Open the web dashboard in the user's default browser (F12). Launches a browser only —
    /// nothing is sent to the game.</summary>
    private void OpenDashboard()
    {
        var url = $"http://localhost:{_settings.ApiPort}/";
        try
        {
            Console.WriteLine($"F12 — opening {url}");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Console.Error.WriteLine($"Open dashboard failed: {ex.Message}"); }
    }

    private static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [StructLayout(LayoutKind.Sequential)] private struct CursorPoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out CursorPoint p);

    public void Dispose()
    {
        _replanner.Dispose();
        _api.Dispose();
        _renderer.Dispose();
        _window.Dispose();
    }
}

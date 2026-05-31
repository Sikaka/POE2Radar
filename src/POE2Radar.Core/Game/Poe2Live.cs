namespace POE2Radar.Core.Game;

/// <summary>
/// Live PoE2 game-state reader for the radar overlay. Resolves the top-level chain each tick
/// (GameState → InGameState → AreaInstance) and exposes the player position, the entity list,
/// the walkable terrain grid, and the large-map UI state — all via offsets validated live and
/// recorded in <see cref="Poe2"/> / resources/community-offsets.md.
///
/// <para>Construct once with the AOB-resolved GameState pointer slot (see Bootstrap). Call
/// <see cref="TryResolve"/> at the start of each tick; everything else takes the resolved
/// AreaInstance / InGameState.</para>
/// </summary>
public sealed class Poe2Live
{
    private readonly MemoryReader _reader;
    private readonly nint _gameStateSlot;

    // Per-entity frozen data, keyed by entity object address (stable within an area).
    private readonly Dictionary<nint, nint> _renderAddr = new();   // entity → Render component
    private readonly Dictionary<nint, nint> _lifeAddr = new();     // entity → Life component (0 = none)
    private readonly Dictionary<nint, nint> _posAddr = new();      // entity → Positioned component (0 = none)
    private readonly Dictionary<nint, nint> _ompAddr = new();      // entity → ObjectMagicProperties (0 = none)
    private readonly Dictionary<nint, nint> _chestAddr = new();    // entity → Chest component (0 = none)
    private readonly Dictionary<nint, EntityCategory> _category = new();
    private readonly Dictionary<nint, string> _meta = new();
    private readonly Dictionary<nint, bool> _hasIcon = new();      // entity has a MinimapIcon component (game POI)
    private nint _entCacheKey;   // AreaInstance address the entity caches were built for

    public Poe2Live(MemoryReader reader, nint gameStateSlot)
    {
        _reader = reader;
        _gameStateSlot = gameStateSlot;
    }

    public enum EntityCategory { Player, Monster, Npc, Chest, Transition, Object, Other }

    /// <summary>Monster rarity from ObjectMagicProperties.Rarity. NonMonster = not applicable.</summary>
    public enum Rarity { Normal = 0, Magic = 1, Rare = 2, Unique = 3, NonMonster = -1 }

    public readonly record struct EntityDot(
        uint Id, nint Address, System.Numerics.Vector2 Grid, Vector3 World, EntityCategory Category, string Metadata,
        int HpCur, int HpMax, bool Poi, byte Reaction, Rarity Rarity, bool Opened)
    {
        /// <summary>Monsters are "alive" only with positive HP; non-life entities are always shown.</summary>
        public bool IsAlive => HpMax <= 0 || HpCur > 0;
        public bool HasLife => HpMax > 0;
        /// <summary>GameHelper2 rule: friendly when (Reaction &amp; 0x7F) == 1.</summary>
        public bool IsFriendly => (Reaction & 0x7F) == 1;
        public float HpFraction => HpMax > 0 ? Math.Clamp((float)HpCur / HpMax, 0f, 1f) : 1f;
    }

    public readonly record struct MapUi(bool IsVisible, float ShiftX, float ShiftY, float Zoom);

    /// <summary>A static tile-based landmark: a notable terrain feature and its grid centroid.
    /// <paramref name="CuratedName"/> is an optional curated friendly label (null when none matches);
    /// <paramref name="Name"/> is the derived-from-path fallback.</summary>
    public readonly record struct Landmark(string Name, string Path, System.Numerics.Vector2 Center, int TileCount, string? CuratedName = null);

    public sealed record TerrainData(byte[] Walkable, int Width, int Height);

    /// <summary>Resolve the in-game chain. Returns false during loading / character select.</summary>
    public bool TryResolve(out nint inGameState, out nint areaInstance, out nint localPlayer)
    {
        inGameState = areaInstance = localPlayer = 0;
        var gameState = Ptr(_gameStateSlot);
        if (gameState == 0) return false;

        // InGameState = first element of the CurrentStatePtr StdVector; fall back to States[].
        var candidates = new List<nint>(13);
        var vecFirst = Ptr(gameState + Poe2.GameState.CurrentStatePtr);
        if (vecFirst != 0) candidates.Add(Ptr(vecFirst));
        for (var i = 0; i < Poe2.GameState.StateSlotCount; i++)
            candidates.Add(Ptr(gameState + Poe2.GameState.States + (nint)(i * Poe2.GameState.StateSlotStride)));

        foreach (var igs in candidates)
        {
            if (igs == 0) continue;
            var ai = Ptr(igs + Poe2.InGameState.AreaInstanceData);
            if (ai == 0) continue;
            var lp = Ptr(ai + Poe2.AreaInstance.LocalPlayer);
            if (lp == 0) continue;
            if (!ReadMetadata(lp).StartsWith("Metadata/", StringComparison.Ordinal)) continue;
            inGameState = igs; areaInstance = ai; localPlayer = lp;
            return true;
        }
        return false;
    }

    /// <summary>Per-area instance hash. (Caches key on the AreaInstance address; this is for display/ID.)</summary>
    public uint AreaHash(nint areaInstance)
    {
        _reader.TryReadStruct<uint>(areaInstance + Poe2.AreaInstance.CurrentAreaHash, out var h);
        return h;
    }

    /// <summary>Monster/area level (validated live: 27, 32).</summary>
    public int AreaLevel(nint areaInstance)
    {
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.CurrentAreaLevel, out var l);
        return l;
    }

    private string _areaCode = ""; private nint _areaCodeFor = -1;

    /// <summary>Area code identifier (e.g. "G1_town"). Cached per area.</summary>
    public string AreaCode(nint areaInstance)
    {
        if (areaInstance == _areaCodeFor) return _areaCode;
        _areaCodeFor = areaInstance;
        var info = Ptr(areaInstance + Poe2.AreaInstance.AreaInfoPtr);
        var s = Ptr(info);
        _areaCode = s == 0 ? "" : _reader.ReadStringUtf16(s, 64);
        return _areaCode;
    }

    private nint _plPlayer, _plPlayerFor;
    private nint PlayerComp(nint localPlayer)
    {
        if (localPlayer != _plPlayerFor) { _plPlayerFor = localPlayer; _plPlayer = ResolveComponent(localPlayer, "Player"); }
        return _plPlayer;
    }

    /// <summary>Local character name (validated via StdWString @ Player+0x1B0).</summary>
    public string PlayerName(nint localPlayer)
    {
        var c = PlayerComp(localPlayer);
        return c == 0 ? "" : ReadStdWString(c + Poe2.PlayerComponent.Name);
    }

    /// <summary>Local character level (byte @ Player+0x204).</summary>
    public int PlayerLevel(nint localPlayer)
    {
        var c = PlayerComp(localPlayer);
        return c != 0 && _reader.TryReadStruct<byte>(c + Poe2.PlayerComponent.Level, out var b) ? b : 0;
    }

    /// <summary>Player grid position (from the Render component's world position ÷ grid ratio).</summary>
    public System.Numerics.Vector2? PlayerGrid(nint localPlayer) => EntityGrid(localPlayer);

    public readonly record struct Vitals(int HpCur, int HpUnreserved, int ManaCur, int ManaUnreserved)
    {
        public float HpPct   => HpUnreserved   > 0 ? 100f * HpCur   / HpUnreserved   : 100f;
        public float ManaPct => ManaUnreserved > 0 ? 100f * ManaCur / ManaUnreserved : 100f;
    }

    private nint _plLife, _plLifeFor;

    /// <summary>
    /// Local player HP/mana as current vs. *unreserved* max (auras reserve part of the pool, so
    /// raw Max would understate the real % full). Drives the auto-flask thresholds.
    /// </summary>
    public Vitals? PlayerVitals(nint localPlayer)
    {
        if (localPlayer != _plLifeFor) { _plLifeFor = localPlayer; _plLife = ResolveComponent(localPlayer, "Life"); }
        if (_plLife == 0) return null;
        if (!_reader.TryReadStruct<VitalStruct>(_plLife + Poe2.Life.Health, out var hp)) return null;
        _reader.TryReadStruct<VitalStruct>(_plLife + Poe2.Life.Mana, out var mana);
        return new Vitals(hp.Current, Unreserved(hp), mana.Current, Unreserved(mana));
    }

    private static int Unreserved(VitalStruct v)
    {
        var reserved = (int)Math.Ceiling(v.ReservedFraction / 10000f * v.Max) + v.ReservedFlat;
        return Math.Max(0, v.Max - reserved);
    }

    /// <summary>
    /// Walk the awake-entity std::map and project each to a grid dot with a category. Visuals /
    /// decorations (id ≥ 0x40000000) are skipped. Render addresses + categories are cached per
    /// entity for the area's lifetime; the per-tick cost is then ~1 pointer read per entity.
    /// </summary>
    public List<EntityDot> Entities(nint areaInstance)
    {
        if (areaInstance != _entCacheKey)
        {
            _renderAddr.Clear(); _lifeAddr.Clear(); _posAddr.Clear(); _ompAddr.Clear(); _chestAddr.Clear();
            _category.Clear(); _meta.Clear(); _hasIcon.Clear();
            _entCacheKey = areaInstance;
        }

        var dots = new List<EntityDot>(256);
        var head = Ptr(areaInstance + Poe2.AreaInstance.AwakeEntities);
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.AwakeEntities + 8, out var size);
        if (head == 0 || size <= 0 || size > 100000) return dots;

        var root = Ptr(head + Poe2.StdMapNode.Parent);
        var queue = new Queue<nint>(); queue.Enqueue(root);
        var visited = new HashSet<nint>();
        while (queue.Count > 0 && visited.Count < 200000)
        {
            var node = queue.Dequeue();
            if (node == 0 || node == head || !visited.Add(node)) continue;
            if (!_reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;

            _reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
            var entity = Ptr(node + Poe2.StdMapNode.ValueEntityPtr);
            queue.Enqueue(Ptr(node + Poe2.StdMapNode.Left));
            queue.Enqueue(Ptr(node + Poe2.StdMapNode.Right));

            if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
            var world = EntityWorld(entity);
            if (world is not { } wv) continue;
            var g = new System.Numerics.Vector2(wv.X / Poe2.WorldToGridRatio, wv.Y / Poe2.WorldToGridRatio);

            var cat = Categorize(entity);
            int hpCur = 0, hpMax = 0;
            var rarity = Rarity.NonMonster;
            var opened = false;
            if (cat is EntityCategory.Monster or EntityCategory.Player) (hpCur, hpMax) = ReadHp(entity);
            if (cat is EntityCategory.Monster or EntityCategory.Chest) rarity = ReadRarity(entity);
            if (cat == EntityCategory.Chest) opened = ReadChestOpened(entity);

            dots.Add(new EntityDot(id, entity, g, wv, cat, _meta.GetValueOrDefault(entity, ""), hpCur, hpMax,
                HasIcon(entity), ReadReaction(entity), rarity, opened));
        }
        return dots;
    }

    /// <summary>Whether the entity has a MinimapIcon component — i.e. the game marks it as a POI.</summary>
    private bool HasIcon(nint entity)
    {
        if (_hasIcon.TryGetValue(entity, out var has)) return has;
        has = ResolveComponent(entity, "MinimapIcon") != 0;
        _hasIcon[entity] = has;
        return has;
    }

    private Rarity ReadRarity(nint entity)
    {
        if (!_ompAddr.TryGetValue(entity, out var omp))
        {
            omp = ResolveComponent(entity, "ObjectMagicProperties");
            _ompAddr[entity] = omp;
        }
        if (omp == 0) return Rarity.Normal;
        if (!_reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out var r) || r is < 0 or > 3)
            return Rarity.Normal;
        return (Rarity)r;
    }

    private byte ReadReaction(nint entity)
    {
        if (!_posAddr.TryGetValue(entity, out var pos))
        {
            pos = ResolveComponent(entity, "Positioned");
            _posAddr[entity] = pos;
        }
        if (pos == 0) return 0;
        return _reader.TryReadStruct<byte>(pos + Poe2.Positioned.Reaction, out var b) ? b : (byte)0;
    }

    private (int cur, int max) ReadHp(nint entity)
    {
        if (!_lifeAddr.TryGetValue(entity, out var life))
        {
            life = ResolveComponent(entity, "Life");
            _lifeAddr[entity] = life;
        }
        if (life == 0) return (0, 0);
        if (!_reader.TryReadStruct<VitalStruct>(life + Poe2.Life.Health, out var v)) return (0, 0);
        return (v.Current, v.Max);
    }

    private List<Landmark>? _landmarks;
    private nint _landmarksKey = -1;

    /// <summary>
    /// Static tile-based landmarks for the area (boss arenas, treasure, waypoints, mechanics…).
    /// Scans the terrain tile grid once per area (cached): each tile's TgtPath, grouped by path
    /// for "interesting" features, with the grid centroid of each group. This is the pre-explored
    /// "X is over here" layer — terrain-feature granularity, not a per-monster spawn table.
    /// </summary>
    public IReadOnlyList<Landmark> Landmarks(nint areaInstance)
    {
        if (areaInstance == _landmarksKey && _landmarks is not null) return _landmarks;
        _landmarksKey = areaInstance;
        _landmarks = ScanLandmarks(areaInstance);
        return _landmarks;
    }

    private List<Landmark> ScanLandmarks(nint areaInstance)
    {
        var result = new List<Landmark>();
        var areaCode = AreaCode(areaInstance);
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        if (!_reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX) || tilesX <= 0) return result;
        var first = Ptr(terrain + Poe2.Terrain.TileDetailsPtr);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last) || first == 0) return result;
        var count = ((long)last - (long)first) / Poe2.TileStructureSize;
        if (count is <= 0 or > 1_000_000) return result;

        // Accumulate sum-of-positions + count per interesting path. Cache path by TgtFilePtr so
        // we read each distinct tile type's StdWString once (dozens), not once per tile (thousands).
        var pathCache = new Dictionary<nint, string?>();
        var sumX = new Dictionary<string, double>();
        var sumY = new Dictionary<string, double>();
        var num = new Dictionary<string, int>();

        for (long i = 0; i < count; i++)
        {
            var tile = first + (nint)(i * Poe2.TileStructureSize);
            var tgtFile = Ptr(tile + Poe2.TileStructure.TgtFilePtr);
            if (tgtFile == 0) continue;
            if (!pathCache.TryGetValue(tgtFile, out var path))
            {
                var p = ReadStdWString(tgtFile + Poe2.TgtFileStruct.TgtPath);
                // Surface a tile as a navigable landmark if the curated community list names it for
                // this area, OR it matches the generic keyword filter (fallback for uncurated areas).
                // The curated list is the primary whitelist: it covers area transitions, vendors, NPCs,
                // etc. whose tile paths carry none of the keywords.
                var keep = CustomLandmarkData.TryMatch(areaCode, p) != null || IsInterestingLandmark(p);
                path = keep ? p : null;
                pathCache[tgtFile] = path;
            }
            if (path is null) continue;

            var gx = (i % tilesX) * Poe2.Terrain.TileGridCells;
            var gy = (i / tilesX) * Poe2.Terrain.TileGridCells;
            sumX[path] = sumX.GetValueOrDefault(path) + gx;
            sumY[path] = sumY.GetValueOrDefault(path) + gy;
            num[path] = num.GetValueOrDefault(path) + 1;
        }

        foreach (var (path, n) in num)
            result.Add(new Landmark(LandmarkName(path), path,
                new System.Numerics.Vector2((float)(sumX[path] / n), (float)(sumY[path] / n)), n,
                CustomLandmarkData.TryMatch(areaCode, path)));
        return result;
    }

    private static bool IsInterestingLandmark(string p)
    {
        if (string.IsNullOrEmpty(p)) return false;
        foreach (var kw in new[] { "arena", "boss", "treasure", "waypoint", "encounter", "ritual",
                                   "vault", "reward", "unique", "checkpoint", "altar", "shrine" })
            if (p.Contains(kw, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string LandmarkName(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = slash >= 0 ? path[(slash + 1)..] : path;
        return name.EndsWith(".tdt", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Read the packed walkable grid (one nibble per cell, 2 cells/byte) into a flat 0/1 array.</summary>
    public TerrainData? Terrain(nint areaInstance)
    {
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        var first = Ptr(terrain + Poe2.Terrain.GridWalkableData);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.GridWalkableData + 8, out var last) || last == 0) return null;
        if (!_reader.TryReadStruct<int>(terrain + Poe2.Terrain.BytesPerRow, out var bytesPerRow) || bytesPerRow <= 0 || bytesPerRow > 65536) return null;
        var totalBytes = (long)last - (long)first;
        if (first == 0 || totalBytes <= 0 || totalBytes > 64 * 1024 * 1024) return null;

        var rows = (int)(totalBytes / bytesPerRow);
        var width = bytesPerRow * 2;
        if (rows <= 0 || rows > 65536) return null;

        var raw = new byte[totalBytes];
        if (_reader.TryReadBytes(first, raw) != raw.Length) return null;

        var walk = new byte[width * rows];
        for (var y = 0; y < rows; y++)
        {
            var rowBase = (long)y * bytesPerRow;
            for (var x = 0; x < width; x++)
            {
                var b = raw[rowBase + (x >> 1)];
                var nibble = (x & 1) == 0 ? (b & 0x0F) : (b >> 4);
                walk[y * width + x] = (byte)(nibble != 0 ? 1 : 0);
            }
        }
        return new TerrainData(walk, width, rows);
    }

    private readonly List<nint> _mapEls = new();
    private readonly HashSet<nint> _everHidden = new(); // elements observed with visible-bit clear
    private nint _mapCacheKey = -1;

    /// <summary>
    /// Large-map UI state. The two MapUiElements (DefaultShift=(0,-20)) are discovered once per
    /// area and cached — per frame we only read their flags/shift/zoom (cheap). The element whose
    /// visible bit actually toggles is the "open the map" signal we gate on; the always-on minimap
    /// element stays visible. Until a toggle is observed, "2 of 2 visible" is treated as open.
    /// </summary>
    public MapUi ReadMap(nint inGameState, nint areaInstance)
    {
        if (areaInstance != _mapCacheKey || _mapEls.Count == 0)
        {
            _mapCacheKey = areaInstance;
            _mapEls.Clear();
            _everHidden.Clear();
            DiscoverMapElements(inGameState);
        }

        var visibleCount = 0;
        nint toggleable = 0;
        var any = false; MapUi anyUi = default;
        foreach (var el in _mapEls)
        {
            if (!TryReadMapElement(el, out var vis, out var sx, out var sy, out var zoom)) continue;
            if (!vis) _everHidden.Add(el);
            else visibleCount++;
            if (_everHidden.Contains(el)) toggleable = el;
            if (!any) { any = true; anyUi = new MapUi(vis, sx, sy, zoom); }
        }
        if (!any) return default;

        // Projection params come from the toggleable (full) map once known, else the first found.
        if (toggleable != 0 && TryReadMapElement(toggleable, out var tv, out var tsx, out var tsy, out var tz))
            return new MapUi(tv, tsx, tsy, tz);

        // Not yet observed a toggle: treat "both visible" as open.
        return new MapUi(visibleCount >= 2, anyUi.ShiftX, anyUi.ShiftY, anyUi.Zoom);
    }

    private void DiscoverMapElements(nint inGameState)
    {
        var uiRoot = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) return;
        var queue = new Queue<nint>(); queue.Enqueue(uiRoot);
        var visited = new HashSet<nint>();
        var body = new byte[Poe2.MapUiElement.Zoom + 8];
        while (queue.Count > 0 && visited.Count < 30000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !visited.Add(el)) continue;

            var first = Ptr(el + Poe2.UiElement.Children);
            if (first != 0 && _reader.TryReadStruct<nint>(el + Poe2.UiElement.Children + 8, out var lastC))
            {
                var n = ((long)lastC - (long)first) / 8;
                if (n is > 0 and <= 8192)
                    for (long k = 0; k < n; k++) queue.Enqueue(Ptr(first + (nint)(k * 8)));
            }

            if (_reader.TryReadBytes(el, body) < body.Length) continue;
            if (BitConverter.ToSingle(body, Poe2.MapUiElement.DefaultShift) != 0f) continue;
            if (BitConverter.ToSingle(body, Poe2.MapUiElement.DefaultShift + 4) != -20f) continue;
            var zoom = BitConverter.ToSingle(body, Poe2.MapUiElement.Zoom);
            if (zoom is <= 0.05f or >= 8f) continue;
            _mapEls.Add(el);
        }
    }

    private bool TryReadMapElement(nint el, out bool visible, out float shiftX, out float shiftY, out float zoom)
    {
        visible = false; shiftX = shiftY = zoom = 0;
        if (!_reader.TryReadStruct<float>(el + Poe2.MapUiElement.DefaultShift + 4, out var dsy) || dsy != -20f) return false;
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Shift, out shiftX);
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Shift + 4, out shiftY);
        _reader.TryReadStruct<float>(el + Poe2.MapUiElement.Zoom, out zoom);
        visible = IsVisible(el);
        return true;
    }

    /// <summary>Element's own visibility bit (0x0B of Flags). Note: full visibility is hierarchical.</summary>
    public bool IsVisible(nint element)
    {
        if (!_reader.TryReadStruct<uint>(element + Poe2.UiElement.Flags, out var flags)) return false;
        return (flags & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;
    }

    // ── internals ───────────────────────────────────────────────────────────

    private Vector3? EntityWorld(nint entity)
    {
        if (!_renderAddr.TryGetValue(entity, out var render))
        {
            render = ResolveComponent(entity, "Render");
            _renderAddr[entity] = render; // cache even if 0, to avoid re-walking
        }
        if (render == 0) return null;
        if (!_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out var w)) return null;
        return w;
    }

    private System.Numerics.Vector2? EntityGrid(nint entity)
        => EntityWorld(entity) is { } w ? new System.Numerics.Vector2(w.X / Poe2.WorldToGridRatio, w.Y / Poe2.WorldToGridRatio) : null;

    /// <summary>Chest opened state: Chest component +0x168 is 1 while closed/openable, 0 once opened.</summary>
    private bool ReadChestOpened(nint entity)
    {
        if (!_chestAddr.TryGetValue(entity, out var c)) { c = ResolveComponent(entity, "Chest"); _chestAddr[entity] = c; }
        if (c == 0) return false;
        return _reader.TryReadStruct<byte>(c + Poe2.ChestComponent.OpenState, out var b) && b == 0;
    }

    /// <summary>WorldToScreen matrix (16 floats, row-major) from Camera@InGameState+0x368. Null if unavailable.</summary>
    public float[]? CameraMatrix(nint inGameState)
    {
        var cam = Ptr(inGameState + Poe2.InGameState.Camera);
        if (cam == 0) return null;
        var b = new byte[64];
        if (_reader.TryReadBytes(cam + Poe2.Camera.WorldToScreenMatrix, b) != 64) return null;
        var m = new float[16];
        System.Buffer.BlockCopy(b, 0, m, 0, 64);
        return m;
    }

    private EntityCategory Categorize(nint entity)
    {
        if (_category.TryGetValue(entity, out var c)) return c;
        var meta = ReadMetadata(entity);
        _meta[entity] = meta;
        c = meta switch
        {
            // Real combat monsters only — exclude on-death/aura effect carriers (MonsterMods),
            // player/ally summons, and invisible effect daemons. Those clutter the map and aren't
            // fight targets. (Hostility via Positioned.Reaction is a future refinement.)
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal) && IsNonCombat(meta) => EntityCategory.Other,
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal)   => EntityCategory.Monster,
            _ when meta.Contains("/Characters/", StringComparison.Ordinal)  => EntityCategory.Player,
            _ when meta.Contains("/NPC/", StringComparison.Ordinal)         => EntityCategory.Npc,
            // Real chests only — exclude breakable props (urns/vases/pots/etc.) under /Chests/.
            _ when meta.Contains("/Chests", StringComparison.Ordinal) && IsBreakableProp(meta) => EntityCategory.Other,
            _ when meta.Contains("/Chests", StringComparison.Ordinal)       => EntityCategory.Chest,
            _ when meta.Contains("Transition", StringComparison.Ordinal)    => EntityCategory.Transition,
            _ when meta.Contains("/Terrain/", StringComparison.Ordinal)     => EntityCategory.Object,
            _                                                              => EntityCategory.Other,
        };
        _category[entity] = c;
        return c;
    }

    /// <summary>True for "/Chests/" entities that are destructible scenery (urns, vases, pots…) not loot chests.</summary>
    private static bool IsBreakableProp(string meta) =>
        meta.Contains("Urn", StringComparison.Ordinal) ||
        meta.Contains("Vase", StringComparison.Ordinal) ||
        meta.Contains("Pot", StringComparison.Ordinal) ||
        meta.Contains("Jar", StringComparison.Ordinal) ||
        meta.Contains("Sack", StringComparison.Ordinal) ||
        meta.Contains("Barrel", StringComparison.Ordinal) ||
        meta.Contains("Crate", StringComparison.Ordinal) ||
        meta.Contains("Debris", StringComparison.Ordinal) ||
        meta.Contains("Rubble", StringComparison.Ordinal) ||
        meta.Contains("Basket", StringComparison.Ordinal) ||
        meta.Contains("Coffin", StringComparison.Ordinal);

    /// <summary>True for "/Monsters/" entities that aren't real fight targets (effects / summons).</summary>
    private static bool IsNonCombat(string meta) =>
        meta.Contains("MonsterMods", StringComparison.Ordinal) ||
        meta.Contains("Summoned", StringComparison.Ordinal) ||
        meta.Contains("/Daemon/", StringComparison.Ordinal) ||
        meta.Contains("Invisible", StringComparison.Ordinal);

    /// <summary>Resolve a component address by name via EntityDetails → ComponentLookUp (StdBucket) → ComponentList.</summary>
    private nint ResolveComponent(nint entity, string name)
    {
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return 0;
        var lookup = Ptr(details + Poe2.EntityDetails.ComponentLookUpPtr);
        if (lookup == 0) return 0;
        if (!_reader.TryReadStruct<StdVector>(entity + Poe2.Entity.ComponentList, out var compList)) return 0;
        var compCount = ((long)compList.Last - (long)compList.First) / 8;
        if (compCount is <= 0 or > 256) return 0;

        var bFirst = Ptr(lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
        if (!_reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return 0;
        var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
        if (bFirst == 0 || entries is <= 0 or > 256) return 0;

        for (long i = 0; i < entries; i++)
        {
            var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
            var namePtr = Ptr(e);
            if (!_reader.TryReadStruct<int>(e + 8, out var index)) continue;
            if (index < 0 || index >= compCount) continue;
            if (_reader.ReadStringUtf8(namePtr, 32) != name) continue;
            return Ptr(compList.First + (nint)(index * 8));
        }
        return 0;
    }

    /// <summary>Read an entity's metadata path: EntityDetails(+0x08) → name StdWString(+0x08).</summary>
    private string ReadMetadata(nint entity)
    {
        var details = Ptr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return string.Empty;
        return ReadStdWString(details + Poe2.EntityDetails.Name);
    }

    private string ReadStdWString(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(addr);
        return ptr == 0 ? string.Empty : _reader.ReadStringUtf16(ptr, len);
    }

    /// <summary>Safe pointer read: 0 on failure or implausible (non-user-mode) value.</summary>
    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : p;
    }
}

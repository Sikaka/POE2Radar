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
    private readonly Dictionary<nint, nint> _iconAddr = new();     // entity → MinimapIcon component (0 = none); game POI
    private readonly Dictionary<nint, Rarity> _rarity = new();     // entity → rarity (static per spawn; cached)
    private readonly Dictionary<nint, string[]> _mods = new();     // entity → affix mod ids (static per spawn; cached; empty = no mods)
    private readonly Dictionary<nint, (Rarity rarity, string? art, bool identified)> _itemIdent = new(); // WorldItem entity → dropped-item identity (static; cached)
    private readonly Dictionary<nint, uint> _idAt = new();         // entity address → last-seen std::map key id (recycle guard)
    // Bounds the number of NEW (uncached) monster mod reads per Entities() pass so walking into a large
    // pack can't stall the world tick. Cached monsters cost nothing; new ones fill over a few ticks.
    private int _modReadBudget;
    private const int ModReadBudgetPerPass = 16;
    private readonly byte[] _modVecBuf = new byte[24]; // one StdVector (First/Last/End)
    // Same budgeting for the (cheap, read-once) dropped-item identity reads.
    private int _itemReadBudget;
    private const int ItemReadBudgetPerPass = 12;
    private nint _entCacheKey;   // AreaInstance address the entity caches were built for

    // Reused across Entities() calls (tick thread only) to avoid per-tick allocations. The std::map
    // walk reads each 48-byte node in ONE ReadProcessMemory (fields are contiguous), not 5 syscalls.
    private readonly Queue<nint> _entQueue = new();
    private readonly HashSet<nint> _entVisited = new();
    private readonly byte[] _nodeBuf = new byte[0x30];
    // Reused camera-matrix buffers (read every render frame).
    private readonly byte[] _camBytes = new byte[64];
    private readonly float[] _camMatrix = new float[16];

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
        int HpCur, int HpMax, bool Poi, byte Reaction, Rarity Rarity, bool Opened, bool IconComplete = false,
        IReadOnlyList<string>? Mods = null, string? ItemArt = null, bool ItemIdentified = true)
    {
        // ItemIdentified (positional): for a dropped unique, whether the game has identified it (Mods+0x90).
        // Drives the loot overlay's unique rule — unID → reveal the resolved name; ID → value only. Defaults
        // true so non-item / non-unique entities are never treated as "unidentified".
        /// <summary>The monster's affix mod ids (auras/buffs), never null. Empty for non-monsters,
        /// unrolled monsters, or before the budgeted mod read has filled this entity in.</summary>
        public IReadOnlyList<string> ModList => Mods ?? Array.Empty<string>();
        // ItemArt (positional): for a dropped-item (WorldItem) entity, the basename of its 2D art (.dds),
        // e.g. "Earthbound" — the price-lookup key (matches poe2scout IconUrl basename). Null for non-items
        // / not-yet-read. When set, Rarity carries the dropped item's rarity (Unique=3) for gating.

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
    public readonly record struct Landmark(string Name, string Path, System.Numerics.Vector2 Center, int TileCount, string? CuratedName = null)
    {
        /// <summary>Stable per-CLUSTER identity for nav selection. A tile path can now yield several
        /// landmarks (one per spatial cluster — e.g. each stair-up section of a multi-level dungeon),
        /// so the path alone is ambiguous; qualify it with the integer centroid, which is stable per
        /// area (tiles are static terrain).</summary>
        public string Key => $"{Path}@{(int)Center.X},{(int)Center.Y}";
    }

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

    /// <summary>Player world position (the Render component's CurrentWorldPosition, incl. Z). RENDER-RATE
    /// safe — resolves + caches the player's Render component on demand (same path as <see cref="PlayerGrid"/>),
    /// so the render thread can anchor the guidance line at the player's live feet without the local player
    /// needing to be in the (world-rate) entity list.</summary>
    public Vector3? PlayerWorld(nint localPlayer) => EntityWorld(localPlayer);

    public readonly record struct Vitals(int HpCur, int HpUnreserved, int ManaCur, int ManaUnreserved,
        int EsCur, int EsUnreserved)
    {
        public float HpPct   => HpUnreserved   > 0 ? 100f * HpCur   / HpUnreserved   : 100f;
        public float ManaPct => ManaUnreserved > 0 ? 100f * ManaCur / ManaUnreserved : 100f;
        // ES% is 100 when there is no ES pool (Max 0, or the offset couldn't be confirmed) so an
        // "ES" / "Either" flask trigger never fires on a build that has no shield to restore.
        public float EsPct   => EsUnreserved   > 0 ? 100f * EsCur   / EsUnreserved   : 100f;
        public bool  HasEs   => EsUnreserved > 0;
    }

    private nint _plLife, _plLifeFor;

    // Self-healing vital offsets. Components are resolved by NAME (robust across patches), but the
    // VitalStruct offsets WITHIN the Life component slide between patches (e.g. 2026-06-04: Health
    // 0x1A8→0x1B0, Mana 0x1F8→0x208, ES 0x230→0x248 — each by a different small amount). We validate
    // each configured offset against a live Life component once; if it doesn't read a valid pool we
    // re-anchor it (see ResolveVitalOffset) so a minor layout shift degrades gracefully (auto-flask +
    // HP bars keep working) instead of silently reading 0. The same offsets back the monster HP reads
    // (identical component layout). Logged loudly so the table still gets updated.
    //
    // Health and ES BOTH self-heal; Mana is best-effort (kept for the mana flask but never gated on).
    // _esOffKnown gates the ES read: if ES can't be confirmed near its offset we suppress the read
    // entirely (→ ES% reads 100 → the ES/Either trigger never fires) rather than risk reading a decoy
    // and misfiring the flask.
    private int _healthOff = Poe2.Life.Health, _manaOff = Poe2.Life.Mana, _esOff = Poe2.Life.EnergyShield;
    private bool _esOffKnown = true;
    private bool _vitalOffsetsResolved;

    // Stricter than VitalStruct.LooksValid: ReservedFraction is reservation in basis-points, so a real
    // pool keeps it in [0, 10000]. The Life component is littered with decoy structs that pass the
    // loose check but carry out-of-range/garbage ReservedFraction — this filters most of them out.
    private static bool LooksLikeRealPool(in VitalStruct v)
        => v.LooksValid() && v.ReservedFraction >= 0 && v.ReservedFraction <= 10000;

    // Resolve one pool's offset within the Life component, healing small drift. Returns the configured
    // offset if it still reads a valid pool (the normal case); otherwise searches a TIGHT window
    // anchored on the configured offset and returns the valid pool nearest to it, or -1 if none. The
    // window is deliberately narrow so the distant decoy VitalStructs (verified live to sit well away
    // from each real pool) stay out of reach — we heal a slide, we don't hunt blindly.
    private int ResolveVitalOffset(nint lifeComp, int configured)
    {
        if (_reader.TryReadStruct<VitalStruct>(lifeComp + configured, out var v) && v.LooksValid())
            return configured;
        int best = -1, bestDist = int.MaxValue;
        for (var off = Math.Max(0x80, configured - 0x18); off <= configured + 0x30; off += 4)
        {
            if (_reader.TryReadStruct<VitalStruct>(lifeComp + off, out var c) && LooksLikeRealPool(c))
            {
                var d = Math.Abs(off - configured);
                if (d < bestDist) { bestDist = d; best = off; }
            }
        }
        return best;
    }

    private void EnsureVitalOffsets(nint lifeComp)
    {
        if (_vitalOffsetsResolved || lifeComp == 0) return;

        // Health is safety-critical and reliably the FIRST valid pool, so it gets an extra fallback:
        // if it won't anchor near its configured offset, take the first valid pool in the component.
        var health = ResolveVitalOffset(lifeComp, Poe2.Life.Health);
        if (health < 0)
        {
            for (var off = 0x80; off <= 0x400; off += 4)
                if (_reader.TryReadStruct<VitalStruct>(lifeComp + off, out var v) && LooksLikeRealPool(v)) { health = off; break; }
            if (health < 0) return; // not in-game yet / unreadable — retry next call (don't latch)
        }

        _vitalOffsetsResolved = true;
        _healthOff = health;
        if (_healthOff != Poe2.Life.Health)
            Console.WriteLine($"Poe2Live: Life Health offset appears to have drifted — auto-relocated " +
                $"0x{Poe2.Life.Health:X}->0x{_healthOff:X} (life flask + HP bars keep working). Update " +
                $"Poe2.Life + re-validate (Research --vitals).");

        // ES self-heals the same way; if it can't be confirmed we suppress the read (safe: ES% → 100).
        var es = ResolveVitalOffset(lifeComp, Poe2.Life.EnergyShield);
        _esOffKnown = es >= 0;
        if (es >= 0)
        {
            _esOff = es;
            if (_esOff != Poe2.Life.EnergyShield)
                Console.WriteLine($"Poe2Live: Life EnergyShield offset appears to have drifted — auto-relocated " +
                    $"0x{Poe2.Life.EnergyShield:X}->0x{_esOff:X} (ES flask keeps working). Update Poe2.Life + re-validate (Research --vitals).");
        }
        else
        {
            Console.WriteLine($"Poe2Live: Life EnergyShield offset (0x{Poe2.Life.EnergyShield:X}) couldn't be confirmed — " +
                "ES flask trigger suppressed (reads as full) until the table is updated (Research --vitals).");
        }

        // Mana: best-effort relocation only. The mana flask is never gated on a confident read — if it
        // drifts past the window it keeps the configured offset (reads 0 → mana% 100 → no misfire).
        var mana = ResolveVitalOffset(lifeComp, Poe2.Life.Mana);
        if (mana >= 0) _manaOff = mana;
    }

    /// <summary>
    /// Local player HP/mana as current vs. *unreserved* max (auras reserve part of the pool, so
    /// raw Max would understate the real % full). Drives the auto-flask thresholds. Returns null
    /// when the Life component / vitals can't be read plausibly (Max &lt;= 0) — the caller MUST treat
    /// that as "unknown" and NOT fire flasks, rather than assuming full/empty.
    /// </summary>
    public Vitals? PlayerVitals(nint localPlayer)
    {
        if (localPlayer != _plLifeFor) { _plLifeFor = localPlayer; _plLife = ResolveComponent(localPlayer, "Life"); }
        if (_plLife == 0) return null;
        EnsureVitalOffsets(_plLife);
        if (!_reader.TryReadStruct<VitalStruct>(_plLife + _healthOff, out var hp) || hp.Max <= 0) return null;
        _reader.TryReadStruct<VitalStruct>(_plLife + _manaOff, out var mana);
        VitalStruct es = default; // suppressed (stays 0 → ES% 100) when the offset isn't confirmed
        if (_esOffKnown) _reader.TryReadStruct<VitalStruct>(_plLife + _esOff, out es);
        return new Vitals(hp.Current, Unreserved(hp), mana.Current, Unreserved(mana), es.Current, Unreserved(es));
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
            _category.Clear(); _meta.Clear(); _iconAddr.Clear(); _rarity.Clear(); _mods.Clear(); _itemIdent.Clear(); _idAt.Clear();
            _entCacheKey = areaInstance;
        }

        var dots = new List<EntityDot>(256);
        var head = Ptr(areaInstance + Poe2.AreaInstance.AwakeEntities);
        _reader.TryReadStruct<int>(areaInstance + Poe2.AreaInstance.AwakeEntities + 8, out var size);
        if (head == 0 || size <= 0 || size > 100000) return dots;

        var root = Ptr(head + Poe2.StdMapNode.Parent);
        _entQueue.Clear(); _entQueue.Enqueue(root);
        _entVisited.Clear();
        _modReadBudget = ModReadBudgetPerPass;
        _itemReadBudget = ItemReadBudgetPerPass;
        while (_entQueue.Count > 0 && _entVisited.Count < 200000)
        {
            var node = _entQueue.Dequeue();
            if (node == 0 || node == head || !_entVisited.Add(node)) continue;

            // One read for the whole node — Left/Right/IsNil/KeyId/ValueEntityPtr are contiguous in
            // 48 bytes, so this replaces 5 separate ReadProcessMemory syscalls per node with one.
            if (_reader.TryReadBytes(node, _nodeBuf) < _nodeBuf.Length) continue;
            if (_nodeBuf[Poe2.StdMapNode.IsNil] != 0) continue; // sentinel/nil — don't traverse its children

            var id = BitConverter.ToUInt32(_nodeBuf, Poe2.StdMapNode.KeyId);
            var entity = (nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.ValueEntityPtr);
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Left));
            _entQueue.Enqueue((nint)BitConverter.ToInt64(_nodeBuf, Poe2.StdMapNode.Right));

            if (entity == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;

            // Recycle guard: entity object addresses are reused within an area as things die/spawn.
            // The std::map key id is the stable per-entity identity (monotonic, never reused in an
            // area), so if THIS address now carries a different id than we cached it under, the prior
            // occupant is gone — evict its frozen component addresses/category/rarity/icon so we don't
            // read a freed/reused Life or Render (stale HP bars over corpses, POIs flickering at stale
            // positions). Re-resolves fresh below.
            if (_idAt.TryGetValue(entity, out var prevId) && prevId != id) EvictEntity(entity);
            _idAt[entity] = id;

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
            var mods = cat == EntityCategory.Monster ? ReadMods(entity) : null;
            var meta = _meta.GetValueOrDefault(entity, "");
            // Dropped items (WorldItem containers, categorized Other) carry a price-lookup identity: art
            // basename + rarity, read once off the inner item entity. Rarity then reflects the item.
            string? itemArt = null;
            var itemIdentified = true;
            if (cat == EntityCategory.Other && meta.Contains("WorldItem", StringComparison.Ordinal))
                (rarity, itemArt, itemIdentified) = ReadItemIdentity(entity);

            var (poi, iconComplete) = ReadIcon(entity);
            dots.Add(new EntityDot(id, entity, g, wv, cat, meta, hpCur, hpMax,
                poi, ReadReaction(entity), rarity, opened, iconComplete, mods, itemArt, itemIdentified));
        }
        return dots;
    }

    /// <summary>Drop every frozen per-entity cache entry for an address whose occupant has changed
    /// (the std::map key id no longer matches). Forces a fresh component re-resolve next read.</summary>
    private void EvictEntity(nint entity)
    {
        _renderAddr.Remove(entity); _lifeAddr.Remove(entity); _posAddr.Remove(entity);
        _ompAddr.Remove(entity); _chestAddr.Remove(entity); _category.Remove(entity);
        _meta.Remove(entity); _iconAddr.Remove(entity); _rarity.Remove(entity); _mods.Remove(entity); _itemIdent.Remove(entity);
    }

    /// <summary>
    /// The entity's POI state from its MinimapIcon component:
    /// <list type="bullet">
    /// <item><c>poi</c> — the game marks it as a map POI (component present).</item>
    /// <item><c>complete</c> — the game has FADED the icon because its encounter is finished
    ///   (CompletedState != 0). The component stays put once resolved, so we cache only its ADDRESS
    ///   and read the flag live every tick (it flips, e.g. on claiming an expedition reward).</item>
    /// </list>
    /// </summary>
    private (bool poi, bool complete) ReadIcon(nint entity)
    {
        if (!_iconAddr.TryGetValue(entity, out var icon))
        {
            icon = ResolveComponent(entity, "MinimapIcon");
            _iconAddr[entity] = icon; // cache even if 0, to avoid re-walking non-POI entities
        }
        if (icon == 0) return (false, false);
        var complete = _reader.TryReadStruct<int>(icon + Poe2.MinimapIcon.CompletedState, out var s) && s != 0;
        return (true, complete);
    }

    private Rarity ReadRarity(nint entity)
    {
        // Rarity is fixed at spawn — read it once per entity and cache the value (not just the addr).
        if (_rarity.TryGetValue(entity, out var cached)) return cached;
        if (!_ompAddr.TryGetValue(entity, out var omp))
        {
            omp = ResolveComponent(entity, "ObjectMagicProperties");
            _ompAddr[entity] = omp;
        }
        if (omp == 0) { _rarity[entity] = Rarity.Normal; return Rarity.Normal; }
        if (!_reader.TryReadStruct<int>(omp + Poe2.ObjectMagicProperties.Rarity, out var r))
            return Rarity.Normal; // transient read failure — don't poison the cache
        var rarity = r is >= 0 and <= 3 ? (Rarity)r : Rarity.Normal;
        _rarity[entity] = rarity;
        return rarity;
    }

    /// <summary>
    /// The monster's affix mod ids (auras/buffs) from ObjectMagicProperties+Mods. Like rarity, mods are
    /// fixed at spawn, so the result is cached per entity (even when empty) and read at most once. New
    /// (uncached) reads are bounded by <see cref="_modReadBudget"/> per pass so a fresh pack fills over a
    /// few world ticks rather than stalling one. Reads ONLY the rolled-affix vector (+0x168); the +0x150
    /// rarity-placeholder filler (MonsterRare/Magic/Unique{N}) is intentionally excluded.
    /// </summary>
    private string[]? ReadMods(nint entity)
    {
        if (_mods.TryGetValue(entity, out var cached)) return cached.Length == 0 ? null : cached;
        if (_modReadBudget <= 0) return null;                  // out of budget this pass — retry next tick (don't cache)

        if (!_ompAddr.TryGetValue(entity, out var omp))
        {
            omp = ResolveComponent(entity, "ObjectMagicProperties");
            _ompAddr[entity] = omp;
        }
        if (omp == 0) { _mods[entity] = Array.Empty<string>(); return null; }
        _modReadBudget--;

        // StdVector at omp+Mods: [First, Last, End]. Element stride ModElemStride; each element holds a
        // record pointer at +ModRecordPtr; the record's +ModIdString is a UTF-16 mod-id string.
        if (_reader.TryReadBytes(omp + Poe2.ObjectMagicProperties.Mods, _modVecBuf) < _modVecBuf.Length)
            return null; // transient read failure — leave uncached, retry next tick
        var first = (nint)BitConverter.ToInt64(_modVecBuf, 0);
        var last = (nint)BitConverter.ToInt64(_modVecBuf, 8);
        var len = (long)last - first;
        const int stride = Poe2.ObjectMagicProperties.ModElemStride;
        if (first == 0 || len <= 0 || len > 0x4000 || len % stride != 0)
        {
            _mods[entity] = Array.Empty<string>(); return null; // no/garbage affix vector — cache as empty
        }
        var n = (int)(len / stride);
        if (n > 100) { _mods[entity] = Array.Empty<string>(); return null; }

        var list = new List<string>(n);
        for (var i = 0; i < n; i++)
        {
            var rec = Ptr(first + (nint)(i * stride + Poe2.ObjectMagicProperties.ModRecordPtr));
            if (rec == 0) continue;
            var idPtr = Poe2.ObjectMagicProperties.ModIdString == 0 ? rec : Ptr(rec + Poe2.ObjectMagicProperties.ModIdString);
            if (idPtr == 0) continue;
            var s = _reader.ReadStringUtf16(idPtr, 64);
            if (LooksLikeModId(s) && !list.Contains(s)) list.Add(s);
        }
        var arr = list.Count == 0 ? Array.Empty<string>() : list.ToArray();
        _mods[entity] = arr;
        return arr.Length == 0 ? null : arr;
    }

    /// <summary>
    /// Resolve a dropped item's identity for price lookup: unwrap the WorldItem container → inner item
    /// entity, read its rarity (Mods+0x94) and 2D-art basename (RenderItem+0x28 → UTF-16 .dds path). Like
    /// other item facts these are fixed once dropped, so the result is cached per entity and read at most
    /// once; new reads are bounded per pass by <see cref="_itemReadBudget"/>. Returns (rarity, artBasename);
    /// artBasename is null when the item can't be resolved.
    /// </summary>
    private (Rarity, string?, bool) ReadItemIdentity(nint entity)
    {
        if (_itemIdent.TryGetValue(entity, out var cached)) return cached;
        if (_itemReadBudget <= 0) return (Rarity.NonMonster, null, true);   // out of budget — retry next tick (don't cache)

        var wi = ResolveComponent(entity, "WorldItem");
        var item = wi == 0 ? 0 : Ptr(wi + Poe2.WorldItemComponent.ItemEntity);
        if (item == 0) { var v = (Rarity.NonMonster, (string?)null, true); _itemIdent[entity] = v; return v; }
        _itemReadBudget--;

        // Rarity (+0x94) + Identified (+0x90) from the item's Mods component (distinct from monster
        // ObjectMagicProperties+0x144). Identified defaults true (non-uniques / no Mods comp aren't "unID").
        var rarity = Rarity.NonMonster;
        var identified = true;
        var modsComp = ResolveComponent(item, "Mods");
        if (modsComp != 0)
        {
            if (_reader.TryReadStruct<int>(modsComp + Poe2.ModsComponent.Rarity, out var r) && r is >= 0 and <= 3)
                rarity = (Rarity)r;
            if (_reader.TryReadStruct<int>(modsComp + Poe2.ModsComponent.Identified, out var idf))
                identified = idf != 0;
        }

        // 2D-art .dds path → basename (the price key). RenderItem+0x28 is a pointer to the UTF-16 path.
        string? art = null;
        var renderItem = ResolveComponent(item, "RenderItem");
        if (renderItem != 0)
        {
            var pathPtr = Ptr(renderItem + Poe2.RenderItemComponent.ResourcePath);
            if (pathPtr != 0)
            {
                var full = _reader.ReadStringUtf16(pathPtr, 128);
                art = ArtBasename(full);
            }
        }

        var result = (rarity, art, identified);
        _itemIdent[entity] = result;
        return result;
    }

    /// <summary>"Art/2DItems/Weapons/.../Uniques/Earthbound.dds" → "Earthbound" (last path segment, no
    /// extension). Returns null for empty/garbage so callers can ignore it.</summary>
    private static string? ArtBasename(string path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var slash = path.LastIndexOf('/');
        var start = slash >= 0 ? slash + 1 : 0;
        var dot = path.LastIndexOf('.');
        var end = dot > start ? dot : path.Length;
        if (end <= start) return null;
        var name = path[start..end];
        return name.Length >= 2 ? name : null;
    }

    /// <summary>A GGG mod id is a non-trivial identifier: letters/digits/underscore only, has a letter.</summary>
    private static bool LooksLikeModId(string s)
    {
        if (s.Length is < 3 or > 64) return false;
        var hasLetter = false;
        foreach (var c in s)
        {
            if (c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z')) { hasLetter = true; continue; }
            if (c is (>= '0' and <= '9') or '_') continue;
            return false;
        }
        return hasLetter;
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
        // Use the (possibly auto-relocated) Health offset so monster HP bars survive the same vital-
        // block drift the player vitals do. _healthOff == Poe2.Life.Health unless drift was detected.
        if (!_reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) return (0, 0);
        return (v.Current, v.Max);
    }

    private List<Landmark>? _landmarks;
    private nint _landmarksKey = -1;

    /// <summary>Optional Overlay-supplied matcher: given a tile path, returns a friendly label (possibly
    /// empty) when the user wants that tile surfaced as a landmark, or null to ignore it. Lets users add
    /// their own landmark/tile patterns at runtime on top of the built-in keyword filter + curated list.
    /// Set by RadarApp; call <see cref="InvalidateLandmarks"/> after the pattern set changes so the
    /// per-area scan cache rebuilds.</summary>
    public Func<string, string?>? CustomLandmarkMatch { get; set; }

    /// <summary>Optional Overlay-supplied curated-label lookup: (areaCode, tilePath) → friendly label,
    /// or null. Lets a user-editable overlay sit on top of the baked-in <see cref="CustomLandmarkData"/>
    /// (the "Landmarks" tab). When unset, the baked data is used directly. Call <see cref="InvalidateLandmarks"/>
    /// after edits so the per-area scan rebuilds.</summary>
    public Func<string, string, string?>? CuratedLookup { get; set; }

    /// <summary>Resolve a tile's curated label: the injected user overlay if wired, else the baked list.</summary>
    private string? Curated(string areaCode, string tilePath)
        => CuratedLookup is { } f ? f(areaCode, tilePath) : CustomLandmarkData.TryMatch(areaCode, tilePath);

    /// <summary>Max gap (in TILES, Chebyshev) between cells still treated as one landmark cluster.
    /// Larger merges nearby copies of a reusable tile into fewer markers; smaller splits them. Set by
    /// the Overlay from <c>RadarSettings.LandmarkClusterGap</c>; call <see cref="InvalidateLandmarks"/>
    /// after changing it so the per-area scan rebuilds. Clamped to a sane range when used.</summary>
    public int LandmarkClusterGap { get; set; } = 2;

    /// <summary>Drop the cached per-area landmark scan so the next <see cref="Landmarks"/> call rebuilds
    /// it (e.g. after the user edits the custom landmark patterns from the dashboard).</summary>
    public void InvalidateLandmarks() => _landmarksKey = -1;

    private List<string>? _tilePaths;
    private nint _tilePathsKey = -1;

    /// <summary>
    /// All DISTINCT terrain-tile paths in the area (sorted), scanned once per area and cached. This is
    /// the full vocabulary of tile names — what the dashboard's add-rule picker browses so a tile rule
    /// can target any tile, not just the ones already surfaced as landmarks.
    /// </summary>
    public IReadOnlyList<string> TilePaths(nint areaInstance)
    {
        if (areaInstance == _tilePathsKey && _tilePaths is not null) return _tilePaths;
        _tilePathsKey = areaInstance;
        _tilePaths = ScanTilePaths(areaInstance);
        return _tilePaths;
    }

    private List<string> ScanTilePaths(nint areaInstance)
    {
        var result = new List<string>();
        var terrain = areaInstance + Poe2.AreaInstance.TerrainMetadata;
        if (!_reader.TryReadStruct<long>(terrain + Poe2.Terrain.TotalTiles, out var tilesX) || tilesX <= 0) return result;
        var first = Ptr(terrain + Poe2.Terrain.TileDetailsPtr);
        if (!_reader.TryReadStruct<nint>(terrain + Poe2.Terrain.TileDetailsPtr + 8, out var last) || first == 0) return result;
        var count = ((long)last - (long)first) / Poe2.TileStructureSize;
        if (count is <= 0 or > 1_000_000) return result;

        // Distinct by TgtFilePtr (one read per tile type — dozens, not per tile), collect the paths.
        var seenPtr = new HashSet<nint>();
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (long i = 0; i < count; i++)
        {
            var tgt = Ptr(first + (nint)(i * Poe2.TileStructureSize) + Poe2.TileStructure.TgtFilePtr);
            if (tgt == 0 || !seenPtr.Add(tgt)) continue;
            var p = ReadStdWString(tgt + Poe2.TgtFileStruct.TgtPath);
            if (!string.IsNullOrEmpty(p)) paths.Add(p);
        }
        result.AddRange(paths);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

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

        // Collect each kept path's tile cells (in tile-index space) so we can CLUSTER them spatially
        // rather than average every instance into one centroid. A reusable tile (e.g. a "stairs up"
        // wall piece) recurs in several disjoint spots — multi-level dungeons have multiple stair-up /
        // stair-down sections connecting layers — and averaging them lands a marker in the dead space
        // between, pointing at nothing. Clustering yields one landmark per actual spot. Cache path by
        // TgtFilePtr so we read each distinct tile type's StdWString once (dozens), not per tile.
        var pathCache = new Dictionary<nint, string?>();
        var cellsByPath = new Dictionary<string, List<(int tx, int ty)>>();

        for (long i = 0; i < count; i++)
        {
            var tile = first + (nint)(i * Poe2.TileStructureSize);
            var tgtFile = Ptr(tile + Poe2.TileStructure.TgtFilePtr);
            if (tgtFile == 0) continue;
            if (!pathCache.TryGetValue(tgtFile, out var path))
            {
                var p = ReadStdWString(tgtFile + Poe2.TgtFileStruct.TgtPath);
                // Surface a tile as a landmark ONLY if the curated community list names it for this area
                // OR a user "Tile" display rule matches it (CustomLandmarkMatch). The old generic keyword
                // sweep was removed — it surfaced decorative terrain (e.g. every "...Vault_Door..." tile)
                // as noise; users now opt into any tile via Tile rules + the dashboard picker.
                var keep = Curated(areaCode, p) != null
                           || CustomLandmarkMatch?.Invoke(p) != null;
                path = keep ? p : null;
                pathCache[tgtFile] = path;
            }
            if (path is null) continue;
            (cellsByPath.TryGetValue(path, out var cells) ? cells : cellsByPath[path] = new())
                .Add(((int)(i % tilesX), (int)(i / tilesX)));
        }

        var cell = Poe2.Terrain.TileGridCells;
        foreach (var (path, cells) in cellsByPath)
        {
            var name = LandmarkName(path);
            // Curated label wins; else a non-empty user label; else null (derived name shows). Same
            // for every cluster of this path (they're the same feature type in different spots).
            var curated = Curated(areaCode, path) ?? NonEmpty(CustomLandmarkMatch?.Invoke(path));
            foreach (var cluster in ClusterTiles(cells, Math.Clamp(LandmarkClusterGap, 0, 64)))
            {
                double sx = 0, sy = 0;
                foreach (var (tx, ty) in cluster) { sx += tx; sy += ty; }
                var center = new System.Numerics.Vector2(
                    (float)(sx / cluster.Count * cell), (float)(sy / cluster.Count * cell));
                result.Add(new Landmark(name, path, center, cluster.Count, curated));
            }
        }
        return result;
    }

    /// <summary>
    /// Group same-path tile cells into spatially-disjoint clusters: two cells join when within a
    /// Chebyshev gap of <c>≤ gap</c> tiles (gap=2 bridges a one-tile hole inside a feature while
    /// keeping well-separated copies apart; larger merges more, 0 = only directly-touching cells).
    /// Plain BFS over a cell set — O(tiles) for the small kept-path counts, so a tile type that recurs
    /// across the map yields one cluster per location instead of a single meaningless average.
    /// </summary>
    private static List<List<(int tx, int ty)>> ClusterTiles(List<(int tx, int ty)> cells, int gap)
    {
        var set = new HashSet<(int, int)>(cells);
        var visited = new HashSet<(int, int)>();
        var clusters = new List<List<(int tx, int ty)>>();
        var queue = new Queue<(int, int)>();
        foreach (var start in cells)
        {
            if (!visited.Add(start)) continue;
            var cluster = new List<(int tx, int ty)>();
            queue.Enqueue(start);
            while (queue.Count > 0)
            {
                var (cx, cy) = queue.Dequeue();
                cluster.Add((cx, cy));
                for (var dx = -gap; dx <= gap; dx++)
                    for (var dy = -gap; dy <= gap; dy++)
                    {
                        var nb = (cx + dx, cy + dy);
                        if (set.Contains(nb) && visited.Add(nb)) queue.Enqueue(nb);
                    }
            }
            clusters.Add(cluster);
        }
        return clusters;
    }

    /// <summary>Null for null/empty, else the string — so an empty user label means "surface but use the
    /// path-derived name" rather than showing a blank curated label.</summary>
    private static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

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
    private readonly HashSet<nint> _everHidden = new();  // elements observed with visible-bit clear
    private readonly HashSet<nint> _everVisible = new(); // elements observed with visible-bit set
    private nint _mapCacheKey = -1;

    /// <summary>
    /// Map UI state. The MapUiElements (DefaultShift=(0,-20), Zoom=0.5) are discovered once per area
    /// and cached — per frame we only read their flags/shift/zoom (cheap). The game exposes several:
    /// some are always-on, some always-off, and one is the minimap viewport whose visible bit Tab
    /// toggles. We gate "map open" on a *genuine toggler* — an element observed BOTH visible and
    /// hidden — so a permanently-hidden element can't masquerade as the toggle signal (the bug that
    /// pinned this to "closed" once the UI began exposing 4 elements instead of 2). Projection
    /// params (shift/zoom) come from a currently-visible toggler. Until the first toggle is observed
    /// this area, fall back to "more than the always-on baseline visible" (&gt;=2).
    /// </summary>
    public MapUi ReadMap(nint inGameState, nint areaInstance)
    {
        if (areaInstance != _mapCacheKey || _mapEls.Count == 0)
        {
            _mapCacheKey = areaInstance;
            _mapEls.Clear();
            _everHidden.Clear();
            _everVisible.Clear();
            DiscoverMapElements(inGameState);
        }

        var visibleCount = 0;
        var any = false; MapUi anyUi = default;
        var sawToggler = false; var togglerVisible = false; var haveTogglerUi = false; MapUi togglerUi = default;
        foreach (var el in _mapEls)
        {
            if (!TryReadMapElement(el, out var vis, out var sx, out var sy, out var zoom)) continue;
            if (vis) { _everVisible.Add(el); visibleCount++; } else _everHidden.Add(el);
            if (!any) { any = true; anyUi = new MapUi(vis, sx, sy, zoom); }

            // A genuine toggler has been seen in BOTH states; permanently-on/off elements never qualify.
            if (_everVisible.Contains(el) && _everHidden.Contains(el))
            {
                sawToggler = true;
                if (vis) togglerVisible = true;
                if (vis || !haveTogglerUi) { togglerUi = new MapUi(vis, sx, sy, zoom); haveTogglerUi = true; }
            }
        }
        if (!any) return default;

        if (sawToggler)
            return new MapUi(togglerVisible, togglerUi.ShiftX, togglerUi.ShiftY, togglerUi.Zoom);

        // No toggle observed yet this area: the open minimap lights up one element beyond the
        // always-on baseline, so >=2 visible ≈ open. Superseded as soon as a real toggle is seen.
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

    /// <summary>RENDER-RATE live read of one already-known monster's world position + HP, reusing the
    /// component addresses cached by the last <see cref="Entities"/> walk (no component re-resolve, no map
    /// re-enumeration). This is what lets HP bars track a moving monster smoothly at the full frame rate
    /// while the expensive entity enumeration stays at world rate. Two tiny reads (12-byte position, 8-byte
    /// vital). Returns false if the entity isn't in the current area's cache or the position read fails.</summary>
    public bool TryLiveBar(nint entity, out Vector3 world, out int hpCur, out int hpMax)
    {
        world = default; hpCur = 0; hpMax = 0;
        if (!_renderAddr.TryGetValue(entity, out var render) || render == 0) return false;
        if (!_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out world)) return false;
        if (_lifeAddr.TryGetValue(entity, out var life) && life != 0
            && _reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) { hpCur = v.Current; hpMax = v.Max; }
        return true;
    }

    /// <summary>The Render + Life component addresses cached for <paramref name="entity"/> by the most
    /// recent <see cref="Entities"/> walk (0 when not resolved). Lets the world thread CAPTURE these into
    /// an HP-bar spec so the RENDER thread can read the bar's live pos/HP via <see cref="TryLiveBarAt"/>
    /// on its OWN reader stack — no shared per-entity cache between threads. Returns false if no Render.</summary>
    public bool TryBarComponents(nint entity, out nint render, out nint life)
    {
        render = _renderAddr.GetValueOrDefault(entity);
        life = _lifeAddr.GetValueOrDefault(entity);
        return render != 0;
    }

    /// <summary>RENDER-RATE bar read from EXPLICIT component addresses (captured off the world thread's
    /// <see cref="Entities"/> walk via <see cref="TryBarComponents"/>), using THIS instance's reader +
    /// resolved Health offset. Touches no per-entity cache, so a render-thread <see cref="Poe2Live"/> can
    /// drive HP bars without sharing state with the world-thread instance. <paramref name="render"/> must
    /// be non-zero. Returns false on a failed position read.</summary>
    public bool TryLiveBarAt(nint render, nint life, out Vector3 world, out int hpCur, out int hpMax)
    {
        world = default; hpCur = 0; hpMax = 0;
        if (render == 0 || !_reader.TryReadStruct<Vector3>(render + Poe2.Render.CurrentWorldPosition, out world)) return false;
        if (life != 0 && _reader.TryReadStruct<VitalStruct>(life + _healthOff, out var v)) { hpCur = v.Current; hpMax = v.Max; }
        return true;
    }

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

    /// <summary>Chest opened state. The 2026-06-06 patch INVERTED this flag: Chest +0x168 is now 0
    /// while closed/openable and non-zero once opened/used (was the reverse). Validated live by diffing
    /// one rare chest closed-vs-opened — only +0x168 flipped (0→1; loot/interaction pointers nulled).
    /// A read failure returns not-opened (i.e. shows the chest): for chests, over-showing is far safer
    /// than silently hiding a real one — which is exactly the bug this flip caused.</summary>
    private bool ReadChestOpened(nint entity)
    {
        if (!_chestAddr.TryGetValue(entity, out var c)) { c = ResolveComponent(entity, "Chest"); _chestAddr[entity] = c; }
        if (c == 0) return false;
        return _reader.TryReadStruct<byte>(c + Poe2.ChestComponent.OpenState, out var b) && b != 0;
    }

    /// <summary>WorldToScreen matrix (16 floats, row-major) from Camera@InGameState+0x368. Null if unavailable.</summary>
    public float[]? CameraMatrix(nint inGameState)
    {
        var cam = Ptr(inGameState + Poe2.InGameState.Camera);
        if (cam == 0) return null;
        // Reuse the buffers — this runs every render frame; the result is consumed synchronously.
        if (_reader.TryReadBytes(cam + Poe2.Camera.WorldToScreenMatrix, _camBytes) != 64) return null;
        System.Buffer.BlockCopy(_camBytes, 0, _camMatrix, 0, 64);
        return _camMatrix;
    }

    private EntityCategory Categorize(nint entity)
    {
        if (_category.TryGetValue(entity, out var c)) return c;
        var meta = ReadMetadata(entity);
        _meta[entity] = meta;
        c = meta switch
        {
            // NPCs FIRST: friendly NPCs (Alva, vendors…) live under "Metadata/Monsters/NPC/…", so the
            // "/NPC/" check must precede "/Monsters/" or they'd be miscategorized as combat monsters
            // (and a Unique-rarity NPC would draw the enemy unique star). "/NPC/" is the NPC marker.
            _ when meta.Contains("/NPC/", StringComparison.Ordinal)         => EntityCategory.Npc,
            // Real combat monsters only — exclude on-death/aura effect carriers (MonsterMods),
            // player/ally summons, and invisible effect daemons. Those clutter the map and aren't
            // fight targets. (Friendly/hostile is applied at draw time via Positioned.Reaction.)
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal) && IsNonCombat(meta) => EntityCategory.Other,
            _ when meta.Contains("/Monsters/", StringComparison.Ordinal)   => EntityCategory.Monster,
            _ when meta.Contains("/Characters/", StringComparison.Ordinal)  => EntityCategory.Player,
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

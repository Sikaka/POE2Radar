using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// Re-discovers the AtlasNode data fields (biome / grid position / map id) after a patch.
///
/// <para>Two things make this hard, and both are handled here:</para>
/// <list type="number">
///   <item>The shipped node-class detector scores candidates USING the biome offset and gates on a
///   hardcoded ~40px icon size. Once either drifts it can no longer find the class it needs in order
///   to find the drift. This anchors purely on geometry — atlas nodes are the large population of
///   uniformly-square, uniformly-scaled elements — so it works with every data offset unknown.</item>
///   <item>The per-node data may live either directly on the element or behind an indirection
///   (GameHelper2 models it as <c>*(*(node+0x10)+0x20)</c>). Scanning only the element body cannot
///   find it in the indirect case, so this profiles BOTH the element and every per-node struct it
///   points at.</item>
/// </list>
///
/// <para>Field shapes used as the discriminator — deliberately strict, because a loose test (e.g.
/// "a byte that is mostly 0 with a few small values") matches hundreds of padding offsets and buries
/// the real field in noise:</para>
/// <list type="bullet">
///   <item><b>Biome</b>: a byte in 1..12 for a MAJORITY of nodes, with >= 4 distinct values.</item>
///   <item><b>GridPos</b>: an int pair, near-unique per node, coordinates within a small range.</item>
///   <item><b>MapId</b>: a value shared by groups of nodes (map types repeat) — neither unique nor constant.</item>
/// </list>
/// </summary>
internal static class AtlasNodeScan
{
    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    public static int Run(ProcessHandle process, MemoryReader reader, int lo, int hi, bool indirect)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }

        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var uiRoot = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        var root = Ptr(reader, uiRoot + Poe2.UiElement.Parent) is var tr && tr != 0 ? tr : uiRoot;
        if (root == 0) { Console.WriteLine("UI root null."); return 1; }

        var byVtable = new Dictionary<nint, List<nint>>();
        var queue = new Queue<nint>(); queue.Enqueue(root);
        var seen = new HashSet<nint>();
        while (queue.Count > 0 && seen.Count < 300000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !seen.Add(el)) continue;
            if (Ptr(reader, el + Poe2.UiElement.Self) != el) continue;
            var vt = Ptr(reader, el);
            if (vt != 0)
            {
                if (!byVtable.TryGetValue(vt, out var l)) byVtable[vt] = l = new List<nint>();
                l.Add(el);
            }
            var begin = Ptr(reader, el + Poe2.UiElement.Children);
            var end = Ptr(reader, el + Poe2.UiElement.ChildrenEnd);
            if (begin == 0 || end <= begin) continue;
            var n = (int)((end - begin) / 8);
            if (n is < 0 or > 16384) continue;
            for (var i = 0; i < n; i++) queue.Enqueue(Ptr(reader, begin + i * 8));
        }

        var cands = new List<(nint Vt, List<nint> Els, float Size)>();
        foreach (var (vt, els) in byVtable)
        {
            if (els.Count < 100) continue;
            var sizes = new Dictionary<float, int>();
            foreach (var e in els)
            {
                if (!reader.TryReadStruct<float>(e + Poe2.UiElement.SizeW, out var w)) continue;
                if (!reader.TryReadStruct<float>(e + Poe2.UiElement.SizeH, out var h)) continue;
                if (w <= 8f || Math.Abs(w - h) > 0.5f) continue;
                sizes[w] = sizes.GetValueOrDefault(w) + 1;
            }
            if (sizes.Count == 0) continue;
            var (modeSize, modeCount) = sizes.OrderByDescending(kv => kv.Value).First();
            if (modeCount < 100) continue;
            cands.Add((vt, els.Where(e => reader.TryReadStruct<float>(e + Poe2.UiElement.SizeW, out var w)
                                          && Math.Abs(w - modeSize) < 0.5f).ToList(), modeSize));
        }
        if (cands.Count == 0)
        {
            Console.WriteLine("No square multi-instance class found — is the Atlas MAP view open?");
            return 1;
        }

        foreach (var cand in cands.OrderByDescending(c => c.Els.Count).Take(3))
        {
            var nodes = cand.Els.Take(600).ToList();
            Console.WriteLine("\n=================================================================");
            Console.WriteLine($"Node class vtable 0x{cand.Vt:X}  ({cand.Els.Count} square instances, size {cand.Size}x{cand.Size})");

            Console.WriteLine($"\n--- DIRECT: fields on the element itself (0x{lo:X}..0x{hi:X}) ---");
            ProfileStruct(reader, nodes, e => e, lo, hi);

            if (!indirect) continue;

            // INDIRECT: any offset holding a per-node-distinct pointer is a candidate data struct.
            Console.WriteLine("\n--- INDIRECT: per-node data structs reached from the element ---");
            for (var off = 0; off <= 0x60; off += 8)
            {
                var targets = new HashSet<nint>();
                var live2 = 0;
                foreach (var e in nodes)
                {
                    var p = Ptr(reader, e + off);
                    if (p == 0) continue;
                    live2++; targets.Add(p);
                }
                // Want: nearly every node has one, and they are mostly distinct (per-node data).
                if (live2 < nodes.Count * 0.9 || targets.Count < nodes.Count * 0.5) continue;
                Console.WriteLine($"\n  via element+0x{off:X2}  ({targets.Count} distinct targets)");
                ProfileStruct(reader, nodes, e => Ptr(reader, e + off), 0x00, 0x400);

                // and one more hop, which is where GameHelper2 puts it (*(*(node+0x10)+0x20)).
                for (var inner = 0; inner <= 0x40; inner += 8)
                {
                    var t2 = new HashSet<nint>();
                    var live3 = 0;
                    foreach (var e in nodes)
                    {
                        var p = Ptr(reader, e + off);
                        if (p == 0) continue;
                        var q = Ptr(reader, p + inner);
                        if (q == 0) continue;
                        live3++; t2.Add(q);
                    }
                    if (live3 < nodes.Count * 0.9 || t2.Count < nodes.Count * 0.5) continue;
                    Console.WriteLine($"\n  via element+0x{off:X2} -> +0x{inner:X2}  ({t2.Count} distinct)");
                    ProfileStruct(reader, nodes, e =>
                    {
                        var p = Ptr(reader, e + off);
                        return p == 0 ? 0 : Ptr(reader, p + inner);
                    }, 0x00, 0x400);
                }
            }
        }
        return 0;
    }

    /// <summary>Profile one struct family (resolved per node by <paramref name="resolve"/>) for biome/grid/map fields.</summary>
    private static void ProfileStruct(MemoryReader reader, List<nint> nodes, Func<nint, nint> resolve, int lo, int hi)
    {
        var bases = nodes.Select(resolve).Where(b => b != 0).ToList();
        if (bases.Count < 50) { Console.WriteLine("    (too few resolvable bases)"); return; }

        var biomeHits = new List<(int Off, int Distinct, double Frac, string Sample)>();
        for (var off = lo; off <= hi; off++)
        {
            var vals = new Dictionary<byte, int>();
            var inRange = 0;
            foreach (var b in bases)
            {
                if (!reader.TryReadStruct<byte>(b + off, out var v)) continue;
                vals[v] = vals.GetValueOrDefault(v) + 1;
                if (v is >= 1 and <= 12) inRange++;
            }
            var frac = inRange / (double)bases.Count;
            var distinct = vals.Count(kv => kv.Key is >= 1 and <= 12);
            // STRICT: a real biome is set on most nodes, not a mostly-zero padding byte.
            if (frac < 0.5 || distinct < 4) continue;
            var sample = string.Join(",", vals.Where(kv => kv.Key <= 12).OrderBy(kv => kv.Key)
                                              .Select(kv => $"{kv.Key}x{kv.Value}"));
            biomeHits.Add((off, distinct, frac, sample));
        }
        Console.WriteLine($"    biome-shaped (byte 1..12 on >=50% of nodes, >=4 distinct): {biomeHits.Count}");
        foreach (var h in biomeHits.OrderByDescending(h => h.Distinct).Take(6))
            Console.WriteLine($"      +0x{h.Off:X3}  {h.Distinct} distinct, {h.Frac:P0} in range: {h.Sample}" +
                              (h.Off == Poe2.AtlasNode.Biome ? "   <= committed Biome" : ""));

        var gridHits = new List<(int Off, int Unique, string Sample)>();
        for (var off = lo; off + 8 <= hi; off += 4)
        {
            var pairs = new HashSet<(int, int)>();
            var ok = 0;
            foreach (var b in bases)
            {
                if (!reader.TryReadStruct<int>(b + off, out var x) || !reader.TryReadStruct<int>(b + off + 4, out var y)) continue;
                if (Math.Abs(x) > 300 || Math.Abs(y) > 300) { ok = -1; break; }
                ok++; pairs.Add((x, y));
            }
            if (ok <= 0 || pairs.Count < bases.Count * 0.8) continue;
            // Reject all-zero / degenerate pairs.
            if (pairs.Count(p => p is { Item1: 0, Item2: 0 }) > 0 && pairs.Count < 10) continue;
            gridHits.Add((off, pairs.Count, string.Join(" ", pairs.Take(6).Select(p => $"({p.Item1},{p.Item2})"))));
        }
        Console.WriteLine($"    grid-pos-shaped (int pair, |v|<=300, >=80% unique): {gridHits.Count}");
        foreach (var h in gridHits.Take(6))
            Console.WriteLine($"      +0x{h.Off:X3}  {h.Unique}/{bases.Count} unique  e.g. {h.Sample}" +
                              (h.Off == Poe2.AtlasNode.GridPos ? "   <= committed GridPos" : ""));

        // Map-type id: repeats in groups — many nodes, far fewer distinct values, but not constant.
        var idHits = new List<(int Off, int Distinct)>();
        for (var off = lo; off + 4 <= hi; off += 4)
        {
            var vals = new Dictionary<uint, int>();
            foreach (var b in bases)
                if (reader.TryReadStruct<uint>(b + off, out var v)) vals[v] = vals.GetValueOrDefault(v) + 1;
            if (vals.Count is < 5 or > 200) continue;
            if (vals.Values.Max() > bases.Count * 0.9) continue;   // near-constant → not an id
            if (vals.Keys.Any(k => k > 0x10000)) continue;         // real row ids are small
            idHits.Add((off, vals.Count));
        }
        Console.WriteLine($"    map-id-shaped (5..200 distinct small values, grouped): {idHits.Count}");
        foreach (var h in idHits.Take(6))
            Console.WriteLine($"      +0x{h.Off:X3}  {h.Distinct} distinct" +
                              (h.Off == Poe2.AtlasNode.MapNodeId ? "   <= committed MapNodeId" : ""));
    }
}

internal static class AtlasStringScan
{
    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    private static bool Ascii(string s)
        => s.Length is >= 3 and <= 64 && s.All(c => c is >= ' ' and <= '~');

    /// <summary>
    /// Finds the atlas node data by STRING SIGNATURE rather than by statistical shape.
    /// A node must be able to name its map, so somewhere behind it is a UTF-16 string like "MapCrypt".
    /// This walks up to three pointer hops out of every candidate node element and reports each
    /// (hop-path, offset) that lands on readable text, with samples — an unambiguous anchor that
    /// does not depend on knowing biome/grid offsets first.
    /// </summary>
    public static int Run(ProcessHandle process, MemoryReader reader, string needle)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }

        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var uiRoot = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        var root = Ptr(reader, uiRoot + Poe2.UiElement.Parent) is var t && t != 0 ? t : uiRoot;

        // Collect VISIBLE square elements grouped by vtable — the atlas nodes are on screen right now,
        // so gating on visibility removes the hidden inventory/skill icon classes that polluted earlier runs.
        var byVtable = new Dictionary<nint, List<nint>>();
        var queue = new Queue<nint>(); queue.Enqueue(root);
        var seen = new HashSet<nint>();
        while (queue.Count > 0 && seen.Count < 300000)
        {
            var el = queue.Dequeue();
            if (el == 0 || !seen.Add(el)) continue;
            if (Ptr(reader, el + Poe2.UiElement.Self) != el) continue;
            if (reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var fl) &&
                (fl & (1u << Poe2.UiElement.FlagVisibleBit)) != 0)
            {
                var vt = Ptr(reader, el);
                if (vt != 0)
                {
                    if (!byVtable.TryGetValue(vt, out var l)) byVtable[vt] = l = new List<nint>();
                    l.Add(el);
                }
            }
            var begin = Ptr(reader, el + Poe2.UiElement.Children);
            var end = Ptr(reader, el + Poe2.UiElement.ChildrenEnd);
            if (begin == 0 || end <= begin) continue;
            var n = (int)((end - begin) / 8);
            if (n is < 0 or > 16384) continue;
            for (var i = 0; i < n; i++) queue.Enqueue(Ptr(reader, begin + i * 8));
        }

        Console.WriteLine($"\nVisible element classes with >=40 instances (needle=\"{needle}\"):");
        foreach (var (vt, els) in byVtable.Where(k => k.Value.Count >= 40).OrderByDescending(k => k.Value.Count).Take(8))
        {
            reader.TryReadStruct<float>(els[0] + Poe2.UiElement.SizeW, out var w);
            reader.TryReadStruct<float>(els[0] + Poe2.UiElement.SizeH, out var h);
            Console.WriteLine($"\n=== vtable 0x{vt:X}  {els.Count} visible instances  size {w}x{h} ===");
            var sample = els.Take(120).ToList();
            var hits = new Dictionary<string, List<string>>();
            foreach (var el in sample) Probe(reader, el, el, "", 0, hits, needle);
            if (hits.Count == 0) { Console.WriteLine("    (no readable strings within 3 hops)"); continue; }
            foreach (var (path, vals) in hits.OrderByDescending(k => k.Value.Count).Take(12))
                Console.WriteLine($"    {path}  x{vals.Count}  e.g. {string.Join(" | ", vals.Distinct().Take(4))}");
        }
        return 0;
    }

    private static void Probe(MemoryReader reader, nint cur, nint _, string path, int depth,
                              Dictionary<string, List<string>> hits, string needle)
    {
        if (depth > 2) return;
        for (var off = 0; off <= 0x400; off += 8)
        {
            var p = Ptr(reader, cur + off);
            if (p == 0) continue;
            var s = reader.ReadStringUtf16(p, 64);
            if (Ascii(s) && (needle.Length == 0 || s.Contains(needle, StringComparison.OrdinalIgnoreCase)))
            {
                var key = $"{path}+0x{off:X3}";
                if (!hits.TryGetValue(key, out var l)) hits[key] = l = new List<string>();
                l.Add(s);
                continue;
            }
            if (depth < 2) Probe(reader, p, p, $"{path}+0x{off:X3}", depth + 1, hits, needle);
        }
    }
}

internal static class AtlasGridScan
{
    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    /// <summary>
    /// Identifies the atlas node class by GRID POSITION rather than by biome.
    /// Biome is dead in the 2026-09-05 build, and every shipped detector keys off it, so they all
    /// fall back to a wrong class. GridPos is live, and its value range is a strong signature:
    /// a real atlas node holds an (int,int) pair in roughly X[-64..64] Y[0..192], distinct per node.
    /// Scores every vtable on that and prints the winner plus its coordinate span.
    /// </summary>
    public static int Run(ProcessHandle process, MemoryReader reader, int gridOff)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }
        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var uiRoot = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        var root = Ptr(reader, uiRoot + Poe2.UiElement.Parent) is var t && t != 0 ? t : uiRoot;

        var byVtable = new Dictionary<nint, List<nint>>();
        var q = new Queue<nint>(); q.Enqueue(root);
        var seen = new HashSet<nint>();
        while (q.Count > 0 && seen.Count < 300000)
        {
            var el = q.Dequeue();
            if (el == 0 || !seen.Add(el)) continue;
            if (Ptr(reader, el + Poe2.UiElement.Self) != el) continue;
            var vt = Ptr(reader, el);
            if (vt != 0) { if (!byVtable.TryGetValue(vt, out var l)) byVtable[vt] = l = new List<nint>(); l.Add(el); }
            var b = Ptr(reader, el + Poe2.UiElement.Children);
            var e2 = Ptr(reader, el + Poe2.UiElement.ChildrenEnd);
            if (b == 0 || e2 <= b) continue;
            var n = (int)((e2 - b) / 8);
            if (n is < 0 or > 16384) continue;
            for (var i = 0; i < n; i++) q.Enqueue(Ptr(reader, b + i * 8));
        }

        Console.WriteLine($"\nScoring {byVtable.Count} vtables on grid-pos signature at +0x{gridOff:X3}\n");
        var results = new List<(nint Vt, int Count, int InRange, int Distinct, int MinX, int MaxX, int MinY, int MaxY)>();
        foreach (var (vt, els) in byVtable)
        {
            if (els.Count < 50) continue;
            var pairs = new HashSet<(int, int)>();
            int inRange = 0, minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            foreach (var el in els)
            {
                if (!reader.TryReadStruct<int>(el + gridOff, out var x)) continue;
                if (!reader.TryReadStruct<int>(el + gridOff + 4, out var y)) continue;
                if (x is < -64 or > 64 || y is < 0 or > 192) continue;
                inRange++; pairs.Add((x, y));
                minX = Math.Min(minX, x); maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y); maxY = Math.Max(maxY, y);
            }
            if (inRange < els.Count * 0.5 || pairs.Count < 20) continue;
            results.Add((vt, els.Count, inRange, pairs.Count, minX, maxX, minY, maxY));
        }
        foreach (var r in results.OrderByDescending(r => r.Distinct).Take(8))
            Console.WriteLine($"  vtable 0x{r.Vt:X}  {r.Count} instances, {r.InRange} in-range, {r.Distinct} distinct coords" +
                              $"  X[{r.MinX}..{r.MaxX}] Y[{r.MinY}..{r.MaxY}]  (module +0x{(long)r.Vt - 0x7FF682040000:X})");
        if (results.Count == 0)
        {
            Console.WriteLine("  no class matched — GridPos offset likely wrong for this build, or the Atlas is not loaded.");
            return 1;
        }

        // Full verification against the WINNING class (highest distinct-coordinate count).
        var win = results.OrderByDescending(r => r.Distinct).First();
        var nodes = byVtable[win.Vt];
        var grid = new HashSet<(int, int)>();
        foreach (var el in nodes)
            if (reader.TryReadStruct<int>(el + gridOff, out var x) && reader.TryReadStruct<int>(el + gridOff + 4, out var y)
                && x is >= -64 and <= 64 && y is >= 0 and <= 192) grid.Add((x, y));

        Console.WriteLine($"\n=== VERIFYING remaining fields on vtable 0x{win.Vt:X} ===");

        // Rolled map id: *(*(node+DataStorage)+DataModel) + DataMapId -> ptr chain -> UTF-16 "MapXxx".
        var mapNames = new List<string>();
        foreach (var el in nodes.Take(400))
        {
            var st = Ptr(reader, el + Poe2.AtlasNode.DataStorage);
            var nd = st == 0 ? 0 : Ptr(reader, st + Poe2.AtlasNode.DataModel);
            if (nd == 0) continue;
            var cur = nd + Poe2.AtlasNode.DataMapId;
            for (var hop = 0; hop < 4; hop++)
            {
                var txt = reader.ReadStringUtf16(cur, 40);
                if (txt.Length >= 3 && txt.All(c => c is >= ' ' and <= '~')) { mapNames.Add(txt); break; }
                cur = Ptr(reader, cur);
                if (cur == 0) break;
            }
        }
        Console.WriteLine($"  DataMapId +0x{Poe2.AtlasNode.DataMapId:X3}: {mapNames.Count} readable, " +
                          $"{mapNames.Distinct().Count()} distinct  e.g. {string.Join(" | ", mapNames.Distinct().Take(5))}");

        // Localized name: sweep the +0x300 row for ANY offset yielding readable text, rather than
        // trusting a single committed offset — the row layout moved and 0x32 reads nothing here.
        Console.WriteLine("  row-name sweep (element+0x300 row, offsets 0x00..0x80):");
        var nameHits = new List<(int Off, int Count, int Distinct, string Sample)>();
        for (var off = 0; off <= 0x80; off++)
        {
            var vals = new List<string>();
            foreach (var el in nodes.Take(300))
            {
                var row = Ptr(reader, el + Poe2.AtlasNode.MapNodeId);
                if (row == 0) continue;
                var np = Ptr(reader, row + off);
                if (np == 0) continue;
                var txt = reader.ReadStringUtf16(np, 48);
                if (txt.Length >= 3 && txt.Length <= 47 && txt.All(c => c is >= ' ' and <= '~')) vals.Add(txt);
            }
            if (vals.Count < 50) continue;
            nameHits.Add((off, vals.Count, vals.Distinct().Count(), string.Join(" | ", vals.Distinct().Take(4))));
        }
        foreach (var h in nameHits.OrderByDescending(h => h.Distinct).Take(6))
            Console.WriteLine($"    +0x{h.Off:X2}  {h.Count} readable, {h.Distinct} distinct  e.g. {h.Sample}" +
                              (h.Off == Poe2.AtlasMapRow.WorldAreaName ? "   <= committed" : ""));
        if (nameHits.Count == 0) Console.WriteLine("    (none — the name may not hang off this row)");

        // Rolled CONTENT sweep: treat each offset on the +0x300 row as a StdVector of row pointers,
        // and look for readable names behind them. Single-deref found nothing, so content is a
        // vector-of-rows, not an inline string.
        Console.WriteLine("  content-vector sweep (element+0x300 row, offsets 0x00..0x80 as StdVector):");
        var contentHits = new List<(int Off, int Nodes, int Distinct, string Sample)>();
        for (var off = 0; off <= 0x80; off += 8)
        {
            var names = new List<string>();
            var withAny = 0;
            foreach (var el in nodes.Take(300))
            {
                var row = Ptr(reader, el + Poe2.AtlasNode.MapNodeId);
                if (row == 0) continue;
                var vb = Ptr(reader, row + off);
                var ve = Ptr(reader, row + off + 8);
                if (vb == 0 || ve <= vb) continue;
                var cnt = (int)((ve - vb) / 8);
                if (cnt is < 1 or > 32) continue;
                var got = false;
                for (var i = 0; i < cnt; i++)
                {
                    var cr = Ptr(reader, vb + i * 8);
                    if (cr == 0) continue;
                    for (var inner = 0; inner <= 0x20; inner += 8)
                    {
                        var sp = Ptr(reader, cr + inner);
                        if (sp == 0) continue;
                        var txt = reader.ReadStringUtf16(sp, 40);
                        if (txt.Length >= 3 && txt.All(c => c is >= ' ' and <= '~')) { names.Add(txt); got = true; break; }
                    }
                }
                if (got) withAny++;
            }
            if (names.Count < 10) continue;
            contentHits.Add((off, withAny, names.Distinct().Count(), string.Join(" | ", names.Distinct().Take(6))));
        }
        foreach (var h in contentHits.OrderByDescending(h => h.Distinct).Take(6))
            Console.WriteLine($"    +0x{h.Off:X2}  {h.Nodes} nodes, {h.Distinct} distinct  e.g. {h.Sample}");
        if (contentHits.Count == 0) Console.WriteLine("    (none)");

        // Content sweep on the nodeData struct — mapId lives there (+0x290), so the rolled content
        // tags almost certainly do too. Try both a direct string pointer and a vector-of-rows.
        Console.WriteLine("  nodeData sweep (*(*(node+0x10)+0x20), offsets 0x00..0x400):");
        var ndHits = new List<(int Off, string Kind, int Nodes, int Distinct, string Sample)>();
        for (var off = 0; off <= 0x400; off += 8)
        {
            var direct = new List<string>(); var vecNames = new List<string>();
            int dN = 0, vN = 0;
            foreach (var el in nodes.Take(300))
            {
                var st = Ptr(reader, el + Poe2.AtlasNode.DataStorage);
                var nd = st == 0 ? 0 : Ptr(reader, st + Poe2.AtlasNode.DataModel);
                if (nd == 0) continue;

                var sp = Ptr(reader, nd + off);
                if (sp != 0)
                {
                    var txt = reader.ReadStringUtf16(sp, 40);
                    if (txt.Length >= 3 && txt.All(c => c is >= ' ' and <= '~')) { direct.Add(txt); dN++; }
                }

                var vb = Ptr(reader, nd + off); var ve = Ptr(reader, nd + off + 8);
                if (vb != 0 && ve > vb)
                {
                    var cnt = (int)((ve - vb) / 8);
                    if (cnt is >= 1 and <= 32)
                    {
                        var got = false;
                        for (var i = 0; i < cnt; i++)
                        {
                            var cr = Ptr(reader, vb + i * 8);
                            if (cr == 0) continue;
                            for (var inner = 0; inner <= 0x20; inner += 8)
                            {
                                var s2 = Ptr(reader, cr + inner);
                                if (s2 == 0) continue;
                                var t2 = reader.ReadStringUtf16(s2, 40);
                                if (t2.Length >= 3 && t2.All(c => c is >= ' ' and <= '~')) { vecNames.Add(t2); got = true; break; }
                            }
                        }
                        if (got) vN++;
                    }
                }
            }
            if (direct.Count >= 20)
                ndHits.Add((off, "str", dN, direct.Distinct().Count(), string.Join(" | ", direct.Distinct().Take(5))));
            if (vecNames.Count >= 10)
                ndHits.Add((off, "vec", vN, vecNames.Distinct().Count(), string.Join(" | ", vecNames.Distinct().Take(5))));
        }
        foreach (var h in ndHits.OrderByDescending(h => h.Distinct).Take(10))
            Console.WriteLine($"    +0x{h.Off:X3} [{h.Kind}]  {h.Nodes} nodes, {h.Distinct} distinct  e.g. {h.Sample}");
        if (ndHits.Count == 0) Console.WriteLine("    (none)");

        // Status byte sweep on nodeData: accessible/completed bits. Every node currently reports
        // BOTH set, which with atlasHideCompleted=true hides the whole atlas — the signature of a
        // drifted offset reading a constant, not of a genuinely 100%-completed atlas.
        Console.WriteLine("  nodeData status-byte sweep (0x270..0x300):");
        for (var off = 0x270; off <= 0x300; off++)
        {
            var vals = new Dictionary<byte, int>();
            foreach (var el in nodes.Take(400))
            {
                var st = Ptr(reader, el + Poe2.AtlasNode.DataStorage);
                var nd = st == 0 ? 0 : Ptr(reader, st + Poe2.AtlasNode.DataModel);
                if (nd == 0) continue;
                if (reader.TryReadStruct<byte>(nd + off, out var b)) vals[b] = vals.GetValueOrDefault(b) + 1;
            }
            if (vals.Count is < 2 or > 6) continue;                 // a flag byte: a few distinct values
            if (vals.Keys.Any(k => k > 15)) continue;               // bit flags stay small
            var total = vals.Values.Sum();
            if (vals.Values.Max() > total * 0.95) continue;         // not near-constant
            Console.WriteLine($"    +0x{off:X3}  {string.Join(", ", vals.OrderBy(k => k.Key).Select(k => $"{k.Key}x{k.Value}"))}" +
                              (off == Poe2.AtlasNode.DataStatus ? "   <= committed DataStatus" : ""));
        }

        // Connection graph: on the canvas (the parent holding the most of these nodes).
        var parents = new Dictionary<nint, int>();
        foreach (var el in nodes)
        {
            var par = Ptr(reader, el + Poe2.UiElement.Parent);
            if (par != 0) parents[par] = parents.GetValueOrDefault(par) + 1;
        }
        if (parents.Count > 0)
        {
            var canvas = parents.OrderByDescending(k => k.Value).First().Key;
            Console.WriteLine($"  canvas 0x{canvas:X} (holds {parents.OrderByDescending(k => k.Value).First().Value} nodes)");
            var b = Ptr(reader, canvas + Poe2.AtlasGraph.ConnectionsVec);
            var e3 = Ptr(reader, canvas + Poe2.AtlasGraph.ConnectionsVec + 8);
            if (b != 0 && e3 > b)
            {
                var count = (int)((e3 - b) / Poe2.AtlasGraph.EdgeStride);
                var onGrid = 0;
                for (var i = 0; i < Math.Min(count, 2000); i++)
                {
                    var ep = b + i * Poe2.AtlasGraph.EdgeStride;
                    reader.TryReadStruct<int>(ep + Poe2.AtlasGraph.EdgeSourceOff, out var sx);
                    reader.TryReadStruct<int>(ep + Poe2.AtlasGraph.EdgeSourceOff + 4, out var sy);
                    reader.TryReadStruct<int>(ep + Poe2.AtlasGraph.EdgeTargetOff, out var tx);
                    reader.TryReadStruct<int>(ep + Poe2.AtlasGraph.EdgeTargetOff + 4, out var ty);
                    if (grid.Contains((sx, sy)) && grid.Contains((tx, ty))) onGrid++;
                }
                Console.WriteLine($"  ConnectionsVec +0x{Poe2.AtlasGraph.ConnectionsVec:X3}: {count} edges, " +
                                  $"{onGrid} with BOTH endpoints on real grid positions");
            }
            else Console.WriteLine($"  ConnectionsVec +0x{Poe2.AtlasGraph.ConnectionsVec:X3}: empty/invalid vector");
        }
        Console.WriteLine($"\n  => node class = module +0x{(long)win.Vt - 0x7FF682040000:X}");
        return 0;
    }
}

internal static class AtlasGates
{
    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    private static bool Visible(MemoryReader r, nint el)
        => r.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var f)
           && (f & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;

    /// <summary>
    /// Reports each gate ReadNodes must pass, in order, so a failure is attributable instead of guessed:
    /// the AtlasPanel open-gate (a hardcoded UiRoot child index — prime drift candidate), the node-class
    /// detection, and the canvas's HIERARCHICAL visibility (every ancestor's visible bit).
    /// Also calls the real Poe2Atlas.ReadNodes so the probe and the overlay cannot disagree.
    /// </summary>
    public static int Run(ProcessHandle process, MemoryReader reader)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }
        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var uiRoot = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        Console.WriteLine($"\nUiRoot 0x{uiRoot:X}");

        // GATE 1 — AtlasPanel open-gate (hardcoded child index).
        var first = Ptr(reader, uiRoot + Poe2.UiElement.Children);
        var last = Ptr(reader, uiRoot + Poe2.UiElement.ChildrenEnd);
        var n = first != 0 && last > first ? (int)((last - first) / 8) : 0;
        Console.WriteLine($"GATE1 AtlasPanelOpen: UiRoot has {n} children; committed index {Poe2.AtlasPanel.UiRootChildIndex}");
        if (n > Poe2.AtlasPanel.UiRootChildIndex)
        {
            var panel = Ptr(reader, first + Poe2.AtlasPanel.UiRootChildIndex * 8);
            var pf = Ptr(reader, panel + Poe2.UiElement.Children);
            var pl = Ptr(reader, panel + Poe2.UiElement.ChildrenEnd);
            var pc = pf != 0 && pl > pf ? (int)((pl - pf) / 8) : 0;
            Console.WriteLine($"      child[{Poe2.AtlasPanel.UiRootChildIndex}] = 0x{panel:X}  visible={Visible(reader, panel)}  children={pc} (expected {Poe2.AtlasPanel.ExpectedChildCount})");
        }
        // Which UiRoot children ARE visible with a plausible panel shape?
        Console.WriteLine("      visible UiRoot children (index: children count):");
        for (var i = 0; i < n; i++)
        {
            var c = Ptr(reader, first + i * 8);
            if (c == 0 || !Visible(reader, c)) continue;
            var cf = Ptr(reader, c + Poe2.UiElement.Children);
            var cl = Ptr(reader, c + Poe2.UiElement.ChildrenEnd);
            var cc = cf != 0 && cl > cf ? (int)((cl - cf) / 8) : 0;
            if (cc > 0) Console.Write($"  {i}:{cc}");
        }
        Console.WriteLine();

        // GATE 2/3 — detection + hierarchical visibility, via the REAL reader.
        var atlas = new Poe2Atlas(reader);
        var nodes = atlas.ReadNodes(igs);
        Console.WriteLine($"\nGATE2/3 Poe2Atlas.ReadNodes -> {nodes.Count} nodes");
        var vis = nodes.Where(x => x.Visible).ToList();
        var named = nodes.Count(x => !string.IsNullOrEmpty(x.MapCode));
        Console.WriteLine($"      visible={vis.Count}/{nodes.Count}   with MapCode={named}/{nodes.Count}");
        Console.WriteLine($"      relPos span: X[{nodes.Min(x => x.X):0}..{nodes.Max(x => x.X):0}] Y[{nodes.Min(x => x.Y):0}..{nodes.Max(x => x.Y):0}]");
        foreach (var v in vis.Take(6))
            Console.WriteLine($"      VIS grid=({v.GridX},{v.GridY}) rel=({v.X:0.#},{v.Y:0.#}) scale={v.Scale} size={v.W}x{v.H} '{v.MapName}' [{v.MapCode}]");
        if (vis.Count == 0)
            Console.WriteLine("      NO node has its own visible bit set — the renderer will draw none.");
        if (nodes.Count == 0)
            Console.WriteLine("      (0 nodes — if GATE1 shows the committed index is not a visible panel, that is the blocker)");
        return 0;
    }
}

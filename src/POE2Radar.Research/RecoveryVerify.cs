using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// Stage 3 of post-patch recovery: once <c>--recover</c> has the chain walking again, re-locate the
/// remaining scalar/pointer fields that do NOT move with the AreaInstance player block — the
/// AreaInfo pointer, the area level/hash scalars, and the InGameState UiRoot/Camera pointers.
/// Each is found by shape (an ASCII area code, a plausible level, a self-referencing UiElement,
/// a camera with a 1.0 zoom float) rather than by assuming a uniform shift.
/// </summary>
internal static class RecoveryVerify
{
    // NOTE: no alignment requirement. PoE2 stores raw pointers into string blobs and into the
    // middle of buffers (WorldAreas row strings, terrain StdVector end pointers), so demanding
    // 8-byte alignment silently hides exactly the fields a post-patch hunt is looking for.
    private static bool Plausible(nint p) => p >= 0x10000 && p < 0x7FFFFFFFFFFF;

    private static nint Ptr(MemoryReader r, nint addr)
        => r.TryReadStruct<nint>(addr, out var p) && Plausible(p) ? p : 0;

    private static bool AsciiCode(string s)
        => s.Length is >= 3 and <= 40 && s.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_' or '-');

    private static bool Printable(string s)
        => s.Length is >= 4 and <= 48 && s.All(c => c is >= ' ' and <= '~');

    public static int Run(ProcessHandle process, MemoryReader reader)
    {
        var slot = ResolveSlot(process, reader);
        if (slot == 0) { Console.WriteLine("Could not resolve the GameState slot — run --recover first."); return 1; }

        var live = new Poe2Live(reader, slot);
        if (!live.TryResolve(out var igs, out var ai, out var lp))
        { Console.WriteLine("Chain does not resolve — run --recover first."); return 1; }

        Console.WriteLine("\n=== STAGE 3: field re-validation ===\n");
        Console.WriteLine($"InGameState 0x{igs:X}  AreaInstance 0x{ai:X}  LocalPlayer 0x{lp:X}\n");

        // ── AreaInfo: AreaInstance+off -> AreaInfo, whose +0x00 -> UTF-16 "Code\0Name\0" ──
        Console.WriteLine("--- AreaInfoPtr candidates (ASCII area code only) ---");
        var areaHits = new List<int>();
        for (var off = 0; off <= 0x400; off += 8)
        {
            var info = Ptr(reader, ai + off);
            if (info == 0) continue;
            var str = Ptr(reader, info);
            if (str == 0) continue;
            var code = reader.ReadStringUtf16(str, 40);
            if (!AsciiCode(code)) continue;
            // Name follows the code's NUL terminator.
            var name = reader.ReadStringUtf16(str + (code.Length + 1) * 2, 60);
            areaHits.Add(off);
            var mark = off == Poe2.AreaInstance.AreaInfoPtr ? "   <= committed" : "";
            Console.WriteLine($"  +0x{off:X3} -> Code='{code}'  Name='{name}'{mark}");
        }
        if (areaHits.Count == 0) Console.WriteLine("  (none at AreaInfo+0x00 — widening the search)");

        // Wider hunt: AreaInstance+off -> AreaInfo; the code string pointer may no longer sit at
        // AreaInfo+0x00, so sweep the first 0x80 bytes of each candidate for it.
        Console.WriteLine("\n--- AreaInfoPtr deep hunt (any printable UTF-16 in AreaInfo+0x00..0x100) ---");
        for (var off = 0; off <= 0x400; off += 8)
        {
            var info = Ptr(reader, ai + off);
            if (info == 0) continue;
            for (var inner = 0; inner <= 0x100; inner += 8)
            {
                var str = Ptr(reader, info + inner);
                if (str == 0) continue;
                var code = reader.ReadStringUtf16(str, 48);
                if (!Printable(code)) continue;
                // A WorldAreas row stores Code then Name back to back, NUL-separated.
                var name = reader.ReadStringUtf16(str + (code.Length + 1) * 2, 60);
                var nameTxt = Printable(name) ? name : "";
                Console.WriteLine($"  AreaInstance+0x{off:X3} -> +0x{inner:X2} -> '{code}'{(nameTxt.Length > 0 ? $"  /  '{nameTxt}'" : "")}");
            }
        }

        // Raw header dump — the level/hash scalars live here; eyeball the shift.
        Console.WriteLine("\n--- AreaInstance header dump (+0x90..0x140, int32) ---");
        for (var off = 0x90; off <= 0x140; off += 16)
        {
            var line = $"  +0x{off:X3}:";
            for (var k = 0; k < 16; k += 4)
            {
                reader.TryReadStruct<int>(ai + off + k, out var v);
                line += $"  {v,12}";
            }
            Console.WriteLine(line);
        }

        // ── Area level / hash scalars near the header ──
        Console.WriteLine("\n--- AreaLevel candidates (int in 1..100, offsets 0x80..0x180) ---");
        for (var off = 0x80; off <= 0x180; off += 4)
        {
            if (!reader.TryReadStruct<int>(ai + off, out var v)) continue;
            if (v is < 1 or > 100) continue;
            var mark = off == Poe2.AreaInstance.CurrentAreaLevel ? "   <= committed" : "";
            Console.WriteLine($"  +0x{off:X3} = {v}{mark}");
        }
        Console.WriteLine("\n--- AreaHash (committed offset readback; must be stable in-zone, change on zone) ---");
        reader.TryReadStruct<uint>(ai + Poe2.AreaInstance.CurrentAreaHash, out var hash);
        Console.WriteLine($"  +0x{Poe2.AreaInstance.CurrentAreaHash:X3} = 0x{hash:X8}  (plausible: {(hash != 0 && hash != 0xFFFFFFFF ? "yes" : "NO")})");

        // ── Entity StdMaps: {Head ptr, int Size}. A real map's head node reaches entities whose
        //    Details+Name resolves to a Metadata/ path, which no unrelated {ptr,int} pair will. ──
        Console.WriteLine("\n--- Entity StdMap candidates (0x600..0x800) ---");
        for (var off = 0x600; off <= 0x800; off += 8)
        {
            var head = Ptr(reader, ai + off);
            if (head == 0) continue;
            if (!reader.TryReadStruct<int>(ai + off + 8, out var size)) continue;
            if (size is < 1 or > 100000) continue;
            var sample = SampleEntityMetadata(reader, head);
            if (sample.Length == 0) continue;
            var mark = off == Poe2.AreaInstance.AwakeEntities ? "   <= committed Awake"
                     : off == Poe2.AreaInstance.SleepingEntities ? "   <= committed Sleeping" : "";
            Console.WriteLine($"  +0x{off:X3}  size={size,-6} sample='{sample}'{mark}");
        }

        // ── TerrainStruct is INLINE in AreaInstance (not a pointer). Anchor on the shape:
        //    TotalTiles {long,long} at +0x18, the walkable StdVector at +0xD0, BytesPerRow at +0x130,
        //    and the self-consistency check bytesPerRow*2 == tilesX*23. ──
        // Raw StdVector sweep — the four terrain grid layers sit at a 0x18 stride, so a run of four
        // large byte-vectors 0x18 apart pins the terrain base regardless of where the struct moved.
        Console.WriteLine("\n--- large StdVector {start,end} pairs in AreaInstance 0x700..0xB80 ---");
        var vecOffs = new List<int>();
        for (var off = 0x700; off <= 0xB80; off += 8)
        {
            var st = Ptr(reader, ai + off);
            var en = Ptr(reader, ai + off + 8);
            if (st == 0 || en <= st) continue;
            var n = (long)(en - st);
            if (n is < 0x400 or > 0x4000000) continue;
            vecOffs.Add(off);
            Console.WriteLine($"  +0x{off:X3}  bytes={n}");
        }
        foreach (var v in vecOffs)
            if (vecOffs.Contains(v + 0x18) && vecOffs.Contains(v + 0x30) && vecOffs.Contains(v + 0x48))
            {
                var terrainBase = v - Poe2.Terrain.GridWalkableData;
                reader.TryReadStruct<int>(ai + terrainBase + Poe2.Terrain.BytesPerRow, out var bpr);
                reader.TryReadStruct<long>(ai + terrainBase + Poe2.Terrain.TotalTiles, out var tx);
                reader.TryReadStruct<long>(ai + terrainBase + Poe2.Terrain.TotalTiles + 8, out var ty);
                Console.WriteLine($"  => 4-layer run at +0x{v:X3} implies TerrainMetadata = 0x{terrainBase:X3} " +
                                  $"(committed 0x{Poe2.AreaInstance.TerrainMetadata:X3});  tiles={tx}x{ty}  bytesPerRow={bpr}");
            }

        Console.WriteLine("\n--- TerrainStruct base candidates (inline, 0x780..0xA80) ---");
        for (var off = 0x780; off <= 0xA80; off += 8)
        {
            var b = ai + off;
            if (!reader.TryReadStruct<long>(b + Poe2.Terrain.TotalTiles, out var tilesX)) continue;
            if (!reader.TryReadStruct<long>(b + Poe2.Terrain.TotalTiles + 8, out var tilesY)) continue;
            if (tilesX is < 1 or > 4096 || tilesY is < 1 or > 4096) continue;
            var vecStart = Ptr(reader, b + Poe2.Terrain.GridWalkableData);
            var vecEnd = Ptr(reader, b + Poe2.Terrain.GridWalkableData + 8);
            if (vecStart == 0 || vecEnd <= vecStart) continue;
            var bytes = (long)(vecEnd - vecStart);
            if (bytes is < 0x400 or > 0x4000000) continue;
            if (!reader.TryReadStruct<int>(b + Poe2.Terrain.BytesPerRow, out var bpr)) continue;
            if (bpr is < 4 or > 8192) continue;
            var cellsPerRow = bpr * 2L;
            var expected = tilesX * Poe2.Terrain.TileGridCells;
            // cellsPerRow rounds UP to a whole byte, so an odd tilesX*23 lands one cell high.
            var consistent = Math.Abs(cellsPerRow - expected) <= 1 && bytes % bpr == 0
                             && bytes / bpr == tilesY * Poe2.Terrain.TileGridCells;
            var mark = off == Poe2.AreaInstance.TerrainMetadata ? "   <= committed" : "";
            Console.WriteLine($"  +0x{off:X3}  tiles={tilesX}x{tilesY}  walkableBytes={bytes}  bytesPerRow={bpr}  " +
                              $"grid={cellsPerRow}x{(bpr == 0 ? 0 : bytes / bpr)}  {(consistent ? "CONSISTENT" : "inconsistent")}{mark}");
        }

        // ── InGameState.UiRoot: a UiElement whose Self ptr (+0x08) points back at itself ──
        Console.WriteLine("\n--- UiRoot candidates (UiElement with self-referencing +0x08) ---");
        for (var off = 0x200; off <= 0x500; off += 8)
        {
            var ui = Ptr(reader, igs + off);
            if (ui == 0) continue;
            if (Ptr(reader, ui + Poe2.UiElement.Self) != ui) continue;
            var mark = off == Poe2.InGameState.UiRoot ? "   <= committed" : "";
            Console.WriteLine($"  +0x{off:X3} -> 0x{ui:X}  (self-ref){mark}");
        }

        // ── InGameState.Camera: object carrying a 1.0 zoom float at +0x528 ──
        Console.WriteLine("\n--- Camera candidates (float 1.0 at +0x528) ---");
        for (var off = 0x200; off <= 0x500; off += 8)
        {
            var cam = Ptr(reader, igs + off);
            if (cam == 0) continue;
            if (!reader.TryReadStruct<float>(cam + Poe2.Camera.Zoom, out var z)) continue;
            if (Math.Abs(z - 1.0f) > 0.001f) continue;
            var mark = off == Poe2.InGameState.Camera ? "   <= committed" : "";
            Console.WriteLine($"  +0x{off:X3} -> 0x{cam:X}  Zoom={z}{mark}");
        }

        Console.WriteLine();
        return 0;
    }

    /// <summary>
    /// Walk a few std::map nodes from the head and return the first entity metadata path found.
    /// Node layout: Left/Parent/Right, then Data{Key,Value} at +0x20 — Value is the Entity ptr.
    /// </summary>
    private static string SampleEntityMetadata(MemoryReader reader, nint head)
    {
        var node = Ptr(reader, head + Poe2.StdMapNode.Parent);
        for (var i = 0; i < 24 && node != 0; i++)
        {
            var ent = Ptr(reader, node + 0x28);
            if (ent != 0)
            {
                var details = Ptr(reader, ent + Poe2.Entity.EntityDetailsPtr);
                if (details != 0)
                {
                    var meta = ReadStdWString(reader, details + Poe2.EntityDetails.Name);
                    if (meta.StartsWith("Metadata/", StringComparison.Ordinal)) return meta;
                }
            }
            node = Ptr(reader, node + Poe2.StdMapNode.Left);
        }
        return string.Empty;
    }

    private static string ReadStdWString(MemoryReader reader, nint addr)
    {
        if (!reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(reader, addr);
        return ptr == 0 ? string.Empty : reader.ReadStringUtf16(ptr, len);
    }

    private static nint ResolveSlot(ProcessHandle process, MemoryReader reader)
    {
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _))
                    return s;
        return 0;
    }
}

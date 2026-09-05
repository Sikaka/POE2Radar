using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// Pins the map UI element's VISIBILITY field by observation rather than assumption.
/// Locating the elements is easy (they are the only ones whose DefaultShift is exactly (0,-20)),
/// but after a class-layout shift the visibility bit can land anywhere — so this diffs a whole
/// window of each element while the user toggles the map, and reports every dword that tracked it.
/// </summary>
internal static class MapWatch
{
    private const int WinStart = 0x80;
    private const int WinLen = 0x200;

    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    public static int Run(ProcessHandle process, MemoryReader reader, int seconds)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }

        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var uiRoot = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        if (uiRoot == 0) { Console.WriteLine("UiRoot null."); return 1; }

        var maps = FindMapElements(reader, uiRoot);
        Console.WriteLine($"\nMap elements (DefaultShift@+0x{Poe2.MapUiElement.DefaultShift:X} == (0,-20)): {maps.Count}");
        foreach (var m in maps) Console.WriteLine($"  0x{m:X}");
        if (maps.Count == 0) return 1;

        Console.WriteLine($"\nToggle the in-game map (Tab) a few times over the next {seconds}s...\n");

        var baseline = new Dictionary<nint, byte[]>();
        foreach (var m in maps)
        {
            var buf = new byte[WinLen];
            reader.TryReadBytes(m + WinStart, buf);
            baseline[m] = buf;
        }

        var changedOffs = new Dictionary<nint, SortedSet<int>>();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            foreach (var m in maps)
            {
                var buf = new byte[WinLen];
                if (reader.TryReadBytes(m + WinStart, buf) <= 0) continue;
                var prev = baseline[m];
                for (var i = 0; i + 4 <= WinLen; i += 4)
                {
                    var a = BitConverter.ToUInt32(prev, i);
                    var b = BitConverter.ToUInt32(buf, i);
                    if (a == b) continue;
                    if (!changedOffs.TryGetValue(m, out var set)) changedOffs[m] = set = new SortedSet<int>();
                    if (set.Add(WinStart + i))
                        Console.WriteLine($"  0x{m:X}  +0x{WinStart + i:X3}  0x{a:X8} -> 0x{b:X8}   bits 0x{a ^ b:X8}");
                }
                baseline[m] = buf;
            }
            Thread.Sleep(60);
        }

        Console.WriteLine("\n=== summary: offsets that changed on toggle ===");
        foreach (var (m, set) in changedOffs)
            Console.WriteLine($"  0x{m:X}: {string.Join(", ", set.Select(o => $"+0x{o:X3}"))}");
        if (changedOffs.Count == 0)
            Console.WriteLine("  nothing in the window changed — the elements may be recreated on toggle " +
                              "(re-run and compare the element ADDRESSES between runs).");
        return 0;
    }

    /// <summary>Breadth-first walk of the UI tree collecting elements whose DefaultShift is (0,-20).</summary>
    private static List<nint> FindMapElements(MemoryReader reader, nint root)
    {
        var found = new List<nint>();
        var seen = new HashSet<nint>();
        var queue = new Queue<nint>();
        queue.Enqueue(root);
        while (queue.Count > 0 && seen.Count < 200000)
        {
            var el = queue.Dequeue();
            if (!seen.Add(el)) continue;

            if (reader.TryReadStruct<float>(el + Poe2.MapUiElement.DefaultShift, out var dx) &&
                reader.TryReadStruct<float>(el + Poe2.MapUiElement.DefaultShift + 4, out var dy) &&
                dx == 0f && dy == -20f)
                found.Add(el);

            var begin = Ptr(reader, el + Poe2.UiElement.Children);
            var end = Ptr(reader, el + Poe2.UiElement.ChildrenEnd);
            if (begin == 0 || end <= begin) continue;
            var count = (int)((end - begin) / 8);
            if (count is < 0 or > 4096) continue;
            for (var i = 0; i < count; i++)
            {
                var child = Ptr(reader, begin + i * 8);
                if (child != 0) queue.Enqueue(child);
            }
        }
        return found;
    }
}

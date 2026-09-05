using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// Walks the live UI tree from GameUi and prints every VISIBLE element that carries text.
/// This is the generic re-fingerprinting tool: panel resolution elsewhere keys off Flags
/// "role" values that drift per patch, but the on-screen text does not — so dumping the
/// visible subtree with each element's path, flags and text shows exactly which node to
/// fingerprint next, and simultaneously re-validates <see cref="Poe2.UiElement.Text"/>.
/// </summary>
internal static class UiTextDump
{
    private static nint Ptr(MemoryReader r, nint a)
        => r.TryReadStruct<nint>(a, out var p) && p >= 0x10000 && p < 0x7FFFFFFFFFFF ? p : 0;

    public static int Run(ProcessHandle process, MemoryReader reader, string? filter, int maxDepth)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }

        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var gameUi = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        if (gameUi == 0) { Console.WriteLine("GameUi null."); return 1; }

        Console.WriteLine($"\nGameUi 0x{gameUi:X}  (Text@+0x{Poe2.UiElement.Text:X}, Flags@+0x{Poe2.UiElement.Flags:X})");
        Console.WriteLine(filter is null ? "Dumping ALL visible text elements.\n"
                                         : $"Dumping visible text elements matching \"{filter}\".\n");

        var hits = 0;
        Walk(reader, gameUi, "", 0, maxDepth, filter, ref hits);
        Console.WriteLine($"\n{hits} visible text element(s).");
        if (hits == 0)
            Console.WriteLine("None — if the panel is definitely open, Poe2.UiElement.Text or .Flags is still wrong.");
        return 0;
    }

    /// <summary>
    /// Locate the Text field by histogram instead of by assumption: sweep every visible element's body
    /// for a std::wstring holding printable ASCII, and tally which offset yields them. The real Text
    /// offset is the one that produces many readable strings across unrelated elements; a coincidence
    /// produces one or two. Prints samples per offset so the right one is obvious by eye.
    /// </summary>
    public static int Scan(ProcessHandle process, MemoryReader reader, int lo, int hi)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }

        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var gameUi = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        if (gameUi == 0) { Console.WriteLine("GameUi null."); return 1; }

        var visible = new List<nint>();
        Collect(reader, gameUi, 0, 16, visible);
        Console.WriteLine($"\nScanning {visible.Count} visible elements, offsets 0x{lo:X}..0x{hi:X}\n");

        var tally = new Dictionary<int, List<string>>();
        foreach (var el in visible)
            for (var off = lo; off <= hi; off += 8)
            {
                var t = ReadStdWString(reader, el + off);
                if (t.Length < 4 || t.Length > 120) continue;
                if (!t.All(c => c is >= ' ' and <= '~')) continue;
                if (!tally.TryGetValue(off, out var l)) tally[off] = l = new List<string>();
                if (l.Count < 6) l.Add(t);
                else l.Add(null!);
            }

        foreach (var (off, l) in tally.OrderByDescending(kv => kv.Value.Count).Take(20))
        {
            var samples = l.Where(x => x != null).Take(5);
            var mark = off == Poe2.UiElement.Text ? "   <= committed Text" : "";
            Console.WriteLine($"  +0x{off:X3}  {l.Count,5} string(s){mark}");
            foreach (var sm in samples) Console.WriteLine($"           \"{sm}\"");
        }
        if (tally.Count == 0) Console.WriteLine("  no printable std::wstring found in that range — widen --lo/--hi.");
        return 0;
    }

    /// <summary>
    /// Pins UiElement.Parent by CONSISTENCY, not by assumption: for many visible child elements, find
    /// every offset holding a pointer whose own Children vector actually contains that child. The true
    /// Parent offset is the one that round-trips for essentially all of them.
    /// </summary>
    public static int ParentScan(ProcessHandle process, MemoryReader reader, int lo, int hi)
    {
        nint gsSlot = 0;
        foreach (var pat in AobPatterns.GameStateRefs)
            foreach (var s in AobScanner.ScanForResolvedAddresses(process, reader, pat).Distinct())
                if (new Poe2Live(reader, s).TryResolve(out _, out _, out _)) { gsSlot = s; break; }
        if (gsSlot == 0) { Console.WriteLine("Chain not resolved."); return 1; }

        var live = new Poe2Live(reader, gsSlot);
        live.TryResolve(out var igs, out _, out _);
        var gameUi = Ptr(reader, igs + Poe2.InGameState.UiRoot);
        if (gameUi == 0) { Console.WriteLine("GameUi null."); return 1; }

        var visible = new List<nint>();
        Collect(reader, gameUi, 0, 16, visible);
        var sample = visible.Skip(1).Take(400).ToList();
        Console.WriteLine($"\nParent scan over {sample.Count} elements, offsets 0x{lo:X}..0x{hi:X}\n");

        var score = new Dictionary<int, int>();
        foreach (var el in sample)
            for (var off = lo; off <= hi; off += 8)
            {
                var cand = Ptr(reader, el + off);
                if (cand == 0 || cand == el) continue;
                if (!ChildrenContain(reader, cand, el)) continue;
                score[off] = score.GetValueOrDefault(off) + 1;
            }

        foreach (var (off, n) in score.OrderByDescending(kv => kv.Value).Take(10))
        {
            var mark = off == Poe2.UiElement.Parent ? "   <= committed Parent" : "";
            Console.WriteLine($"  +0x{off:X3}  {n}/{sample.Count} elements round-trip{mark}");
        }
        if (score.Count == 0) Console.WriteLine("  no offset round-tripped — widen --lo/--hi.");
        return 0;
    }

    private static bool ChildrenContain(MemoryReader reader, nint parent, nint child)
    {
        var begin = Ptr(reader, parent + Poe2.UiElement.Children);
        var end = Ptr(reader, parent + Poe2.UiElement.ChildrenEnd);
        if (begin == 0 || end <= begin) return false;
        var count = (int)((end - begin) / 8);
        if (count is < 0 or > 4096) return false;
        for (var i = 0; i < count; i++)
            if (Ptr(reader, begin + i * 8) == child) return true;
        return false;
    }

    private static void Collect(MemoryReader reader, nint el, int depth, int maxDepth, List<nint> outList)
    {
        if (depth > maxDepth || outList.Count > 6000) return;
        if (!reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags)) return;
        if ((flags & (1u << Poe2.UiElement.FlagVisibleBit)) == 0) return;
        outList.Add(el);
        var begin = Ptr(reader, el + Poe2.UiElement.Children);
        var end = Ptr(reader, el + Poe2.UiElement.ChildrenEnd);
        if (begin == 0 || end <= begin) return;
        var count = (int)((end - begin) / 8);
        if (count is < 0 or > 4096) return;
        for (var i = 0; i < count; i++)
        {
            var child = Ptr(reader, begin + i * 8);
            if (child != 0 && child != el) Collect(reader, child, depth + 1, maxDepth, outList);
        }
    }

    private static void Walk(MemoryReader reader, nint el, string path, int depth, int maxDepth,
                             string? filter, ref int hits)
    {
        if (depth > maxDepth) return;
        if (!reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var flags)) return;
        // Only descend through VISIBLE branches — hidden panels keep stale text from previous opens.
        if ((flags & (1u << Poe2.UiElement.FlagVisibleBit)) == 0) return;

        var text = ReadStdWString(reader, el + Poe2.UiElement.Text);
        if (!string.IsNullOrWhiteSpace(text) && text.Length <= 200)
        {
            if (filter is null || text.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                hits++;
                Console.WriteLine($"  [{path}] 0x{el:X} flags=0x{flags:X8}  \"{text.Replace("\n", "\\n")}\"");
            }
        }

        var begin = Ptr(reader, el + Poe2.UiElement.Children);
        var end = Ptr(reader, el + Poe2.UiElement.ChildrenEnd);
        if (begin == 0 || end <= begin) return;
        var count = (int)((end - begin) / 8);
        if (count is < 0 or > 4096) return;
        for (var i = 0; i < count; i++)
        {
            var child = Ptr(reader, begin + i * 8);
            if (child != 0 && child != el)
                Walk(reader, child, path.Length == 0 ? i.ToString() : $"{path}.{i}", depth + 1, maxDepth, filter, ref hits);
        }
    }

    private static string ReadStdWString(MemoryReader reader, nint addr)
    {
        if (!reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return string.Empty;
        if (len < 8) return reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(reader, addr);
        return ptr == 0 ? string.Empty : reader.ReadStringUtf16(ptr, len);
    }
}

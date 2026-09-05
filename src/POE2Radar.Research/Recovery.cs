using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// Post-patch chain recovery. The committed "Game States" AOB pins a jnz displacement
/// (<c>0F 85 16 01 00 00</c>) that shifts whenever nearby code changes, so a league patch can
/// kill the scan outright. This probe re-scans with only the <c>cmp [rip+rel32], rbp</c> shape,
/// structurally validates each resolved slot as a GameState, then either confirms the committed
/// offsets still work or brute-forces the InGameState-&gt;AreaInstance-&gt;LocalPlayer hops.
/// </summary>
internal static class Recovery
{
    // cmp [rip+rel32], rbp — the shape only; no trailing jump displacement.
    private static readonly byte?[] CmpRipRbp = [0x48, 0x39, 0x2D, null, null, null, null];

    // NOTE: no alignment requirement. PoE2 stores raw pointers into string blobs and into the
    // middle of buffers (WorldAreas row strings, terrain StdVector end pointers), so demanding
    // 8-byte alignment silently hides exactly the fields a post-patch hunt is looking for.
    private static bool Plausible(nint p) => p >= 0x10000 && p < 0x7FFFFFFFFFFF;

    /// <summary>Non-throwing pointer read; 0 when the page is unmapped or the value is junk.</summary>
    private static nint Ptr(MemoryReader reader, nint addr)
        => reader.TryReadStruct<nint>(addr, out var p) && Plausible(p) ? p : 0;

    public static int Run(ProcessHandle process, MemoryReader reader, bool deep)
    {
        Console.WriteLine("\n=== CHAIN RECOVERY ===\n");

        var sections = AobScanner.ReadExecutableSections(process, reader);
        var slots = new List<nint>();
        foreach (var (secBase, bytes) in sections)
            foreach (var off in AobScanner.FindPattern(bytes, CmpRipRbp))
            {
                var slot = AobScanner.ResolveRipRelative(secBase, off, 3, 7, bytes);
                if (Plausible(slot)) slots.Add(slot);
            }
        slots = slots.Distinct().ToList();
        Console.WriteLine($"Relaxed 'cmp [rip+rel32], rbp' matches: {slots.Count} distinct slots");

        // Structural GameState filter: States[] at +0x48 should hold ~12 distinct heap pointers.
        var roots = new List<(nint Slot, nint Gs, int Valid)>();
        foreach (var slot in slots)
        {
            var gs = Ptr(reader, slot);
            if (!Plausible(gs)) continue;
            var seen = new HashSet<nint>();
            for (var i = 0; i < Poe2.GameState.StateSlotCount; i++)
            {
                var p = Ptr(reader, gs + Poe2.GameState.States + i * Poe2.GameState.StateSlotStride);
                if (Plausible(p)) seen.Add(p);
            }
            if (seen.Count >= 6) roots.Add((slot, gs, seen.Count));
        }
        Console.WriteLine($"Slots passing GameState structural check: {roots.Count}");
        foreach (var r in roots)
            Console.WriteLine($"  slot 0x{r.Slot:X16} -> GameState 0x{r.Gs:X16}  ({r.Valid} live states)");

        if (roots.Count == 0)
        {
            Console.WriteLine("\nNo GameState-shaped root found. Are you loaded into a zone?");
            return 1;
        }

        // Stage 1 — do the COMMITTED offsets still work from any of these slots?
        Console.WriteLine("\n--- Stage 1: committed offsets ---");
        foreach (var r in roots)
        {
            var live = new Poe2Live(reader, r.Slot);
            if (live.TryResolve(out var igs, out var ai, out var lp))
            {
                Console.WriteLine("  CHAIN OK with committed offsets.");
                Console.WriteLine($"    slot          0x{r.Slot:X16}");
                Console.WriteLine($"    InGameState   0x{igs:X16}");
                Console.WriteLine($"    AreaInstance  0x{ai:X16}");
                Console.WriteLine($"    LocalPlayer   0x{lp:X16}");
                Console.WriteLine($"    AreaCode      {live.AreaCode(ai)}   Level {live.AreaLevel(ai)}  Hash 0x{live.AreaHash(ai):X8}");
                Console.WriteLine("\n  => Only the AOB pattern drifted. Relax AobPatterns.GameStateRefs; offsets are intact.");
                if (!deep) return 0;
            }
        }
        Console.WriteLine("  committed offsets did NOT resolve.");

        // Stage 2 — brute-force the three hops.
        Console.WriteLine("\n--- Stage 2: brute-force InGameState -> AreaInstance -> LocalPlayer ---");
        foreach (var r in roots)
        {
            var igsList = new List<(string Src, nint Ptr)>();
            var vec = Ptr(reader, r.Gs + Poe2.GameState.CurrentStatePtr);
            if (Plausible(vec))
            {
                var f = Ptr(reader, vec);
                if (Plausible(f)) igsList.Add(("CurrentStateVec[0]", f));
            }
            for (var i = 0; i < Poe2.GameState.StateSlotCount; i++)
            {
                var p = Ptr(reader, r.Gs + Poe2.GameState.States + i * Poe2.GameState.StateSlotStride);
                if (Plausible(p)) igsList.Add(($"States[{i}]", p));
            }

            foreach (var (src, igs) in igsList.DistinctBy(x => x.Ptr))
            {
                var igsBlock = new byte[0x800];
                if (reader.TryReadBytes(igs, igsBlock) <= 0) continue;

                for (var aiOff = 0; aiOff + 8 <= igsBlock.Length; aiOff += 8)
                {
                    var ai = (nint)BitConverter.ToInt64(igsBlock, aiOff);
                    if (!Plausible(ai)) continue;

                    var aiBlock = new byte[0xA00];
                    if (reader.TryReadBytes(ai, aiBlock) <= 0) continue;

                    for (var lpOff = 0; lpOff + 8 <= aiBlock.Length; lpOff += 8)
                    {
                        var lp = (nint)BitConverter.ToInt64(aiBlock, lpOff);
                        if (!Plausible(lp)) continue;
                        // Entity shape: Details @+0x08 and ComponentList @+0x10 both heap pointers.
                        var details = Ptr(reader, lp + Poe2.Entity.EntityDetailsPtr);
                        if (!Plausible(details)) continue;
                        if (!Plausible(Ptr(reader, lp + Poe2.Entity.ComponentList))) continue;

                        var meta = ReadStdWString(reader, details + Poe2.EntityDetails.Name);
                        if (!meta.StartsWith("Metadata/Characters/", StringComparison.Ordinal)) continue;

                        Console.WriteLine($"  HIT  slot 0x{r.Slot:X16}  via {src}");
                        Console.WriteLine($"      InGameState.AreaInstanceData = 0x{aiOff:X3}   (committed 0x{Poe2.InGameState.AreaInstanceData:X3})");
                        Console.WriteLine($"      AreaInstance.LocalPlayer     = 0x{lpOff:X3}   (committed 0x{Poe2.AreaInstance.LocalPlayer:X3})");
                        Console.WriteLine($"      InGameState 0x{igs:X16}  AreaInstance 0x{ai:X16}  LocalPlayer 0x{lp:X16}");
                        Console.WriteLine($"      metadata: {meta}");
                        DumpAreaInstanceCandidates(reader, aiBlock, lpOff);
                        return 0;
                    }
                }
            }
        }

        Console.WriteLine("  no LocalPlayer-shaped entity found under any candidate.");
        return 1;
    }

    /// <summary>
    /// Once LocalPlayer is located, the rest of the AreaInstance block moves with it (it has
    /// shifted as one unit in every prior patch). Print the shift plus the re-based neighbours.
    /// </summary>
    private static void DumpAreaInstanceCandidates(MemoryReader reader, byte[] aiBlock, int lpOff)
    {
        var shift = lpOff - Poe2.AreaInstance.LocalPlayer;
        Console.WriteLine($"\n      block shift vs committed: {(shift >= 0 ? "+" : "-")}0x{Math.Abs(shift):X}");
        Console.WriteLine("      re-based block offsets (apply shift, then validate):");
        Console.WriteLine($"        ServerDataPtr    = 0x{Poe2.AreaInstance.ServerDataPtr + shift:X3}");
        Console.WriteLine($"        AwakeEntities    = 0x{Poe2.AreaInstance.AwakeEntities + shift:X3}");
        Console.WriteLine($"        SleepingEntities = 0x{Poe2.AreaInstance.SleepingEntities + shift:X3}");
        Console.WriteLine($"        TerrainMetadata  = 0x{Poe2.AreaInstance.TerrainMetadata + shift:X3}");

        // AreaInfoPtr: find the offset whose target's first qword is a UTF-16 area code.
        Console.WriteLine("      AreaInfoPtr candidates (target -> area code string):");
        for (var off = 0; off + 8 <= Math.Min(aiBlock.Length, 0x200); off += 8)
        {
            var p = (nint)BitConverter.ToInt64(aiBlock, off);
            if (!Plausible(p)) continue;
            var s = Ptr(reader, p);
            if (!Plausible(s)) continue;
            var code = reader.ReadStringUtf16(s, 32);
            if (code.Length is >= 3 and <= 31 && code.All(c => char.IsLetterOrDigit(c) || c == '_'))
                Console.WriteLine($"        +0x{off:X3} -> \"{code}\"{(off == Poe2.AreaInstance.AreaInfoPtr ? "   (committed)" : "")}");
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

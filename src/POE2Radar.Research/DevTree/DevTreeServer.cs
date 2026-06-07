using System.Net;
using System.Text;
using System.Text.Json;
using POE2Radar.Core;
using POE2Radar.Core.Game;

namespace POE2Radar.Research;

/// <summary>
/// Interactive, browser-based memory/structure explorer for PoE2 reverse-engineering — the project's
/// answer to ExileApi's DevTree (PoE1). DevTree could reflect over ExileApi's pre-built C# object
/// model; we have no such model, so this instead exposes RAW memory navigation, which is what you
/// actually want when DISCOVERING offsets:
///
///   • Named roots         — the live resolved chain (GameState → InGameState → AreaInstance →
///                           LocalPlayer → UiRoot, + the awake-entity map head) as jump-off points.
///   • Pointer-walk tree   — any address rendered as 8-byte slots, each interpreted simultaneously as
///                           hex / i64 / 2×i32 / 2×f32 / pointer / std::wstring / StdVector shape;
///                           pointer slots are expandable, so you descend a struct by clicking.
///   • UI-element tree      — lazily walk the live UiElement tree from UiRoot (addr, child count,
///                           visible bit, flags); click any node to open it in the slot view.
///   • Entity/component     — list awake entities (id, metadata) → their components → byte inspect.
///   • Value search         — scan process memory for an int / float / utf-16 string / pointer.
///
/// Lives in Research (dev-time tooling, never linked into the overlay). Read-only: it only ever
/// OpenProcess+ReadProcessMemory through the shared <see cref="MemoryReader"/>. Requests are handled
/// one-at-a-time on a single listener thread, so the reader is never touched concurrently.
/// </summary>
public sealed class DevTreeServer : IDisposable
{
    private readonly MemoryReader _reader;
    private readonly Poe2Live _live;          // chain resolver (resolved live per request)
    private readonly HttpListener _listener = new();
    private volatile bool _running;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DevTreeServer(MemoryReader reader, nint gameStateSlot, int port)
    {
        _reader = reader;
        _live = new Poe2Live(reader, gameStateSlot);
        _listener.Prefixes.Add($"http://localhost:{port}/");
    }

    public void Start()
    {
        _listener.Start();
        _running = true;
        var t = new Thread(Loop) { IsBackground = true, Name = "POE2Radar.DevTree" };
        t.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            HttpListenerContext ctx;
            try { ctx = _listener.GetContext(); }
            catch { return; }
            try { Handle(ctx); }
            catch (Exception ex) { TryWrite(ctx, 500, JsonSerializer.Serialize(new { error = ex.Message }, Json)); }
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        var path = ctx.Request.Url?.AbsolutePath ?? "/";
        var q = ctx.Request.QueryString;
        switch (path)
        {
            case "/":
                WriteHtml(ctx, DevTreeHtml.Page);
                break;

            case "/api/roots":
                Write(ctx, 200, JsonSerializer.Serialize(Roots(), Json));
                break;

            case "/api/mem":
            {
                if (TryAddr(q["addr"], out var addr))
                {
                    var len = ParseInt(q["len"], 0x200, 0x10, 0x4000);
                    Write(ctx, 200, JsonSerializer.Serialize(Slots(addr, len), Json));
                }
                else Write(ctx, 400, JsonSerializer.Serialize(new { error = "bad addr" }, Json));
                break;
            }

            case "/api/ui":
            {
                nint el;
                if (TryAddr(q["addr"], out var a)) el = a;
                else el = _live.TryResolve(out var igs, out _, out _) ? SafePtr(igs + Poe2.InGameState.UiRoot) : 0;
                if (el == 0) Write(ctx, 404, JsonSerializer.Serialize(new { error = "no UiRoot (in game?)" }, Json));
                else Write(ctx, 200, JsonSerializer.Serialize(UiNode(el), Json));
                break;
            }

            case "/api/ui-flat":
                Write(ctx, 200, JsonSerializer.Serialize(UiFlat(ParseInt(q["max"], 90000, 1, 300000)), Json));
                break;

            case "/api/entities":
                Write(ctx, 200, JsonSerializer.Serialize(Entities(ParseInt(q["limit"], 2000, 1, 50000)), Json));
                break;

            case "/api/components":
            {
                if (TryAddr(q["addr"], out var addr))
                    Write(ctx, 200, JsonSerializer.Serialize(Components(addr), Json));
                else Write(ctx, 400, JsonSerializer.Serialize(new { error = "bad addr" }, Json));
                break;
            }

            case "/api/search":
                Write(ctx, 200, JsonSerializer.Serialize(Search(q["type"], q["value"], ParseInt(q["max"], 200, 1, 5000)), Json));
                break;

            default:
                Write(ctx, 404, JsonSerializer.Serialize(new { error = "not found" }, Json));
                break;
        }
    }

    // ── Roots: the live chain, re-resolved each call so it tracks zoning. ──
    // Hex string only — System.Text.Json refuses to serialize IntPtr, and the client navigates by
    // the hex string anyway (treats "0x0" as absent).
    private record RootDto(string Name, string Hex, string Note);
    private List<RootDto> Roots()
    {
        var list = new List<RootDto>();
        if (!_live.TryResolve(out var igs, out var ai, out var lp))
        {
            list.Add(new RootDto("(not in game)", "0x0", "chain unresolved — load into an area"));
            return list;
        }
        var uiRoot = SafePtr(igs + Poe2.InGameState.UiRoot);
        var awake = SafePtr(ai + Poe2.AreaInstance.AwakeEntities);
        var cam = SafePtr(igs + Poe2.InGameState.Camera);
        list.Add(new RootDto("InGameState", Hex(igs), "active game state"));
        list.Add(new RootDto("AreaInstance", Hex(ai), "current area container"));
        list.Add(new RootDto("LocalPlayer", Hex(lp), MetaOf(lp)));
        list.Add(new RootDto("UiRoot", Hex(uiRoot), "root UiElement (use UI tab)"));
        list.Add(new RootDto("AwakeEntities", Hex(awake), "std::map head"));
        list.Add(new RootDto("Camera", Hex(cam), "Zoom @ +0x528"));
        return list;
    }

    // ── Slot view: a window of memory, every 8-byte slot interpreted as many types at once. ──
    private record SlotDto(int Off, string Hex, long I64, int I32Lo, int I32Hi, float? F0, float? F1,
        string? Ptr, bool PtrReadable, string? Str, long VecCount, string? Ascii);
    // Raw bytes reinterpreted as float are often Infinity/NaN — which System.Text.Json refuses to
    // emit. Map non-finite to null so the row still serializes (the client skips null floats).
    private static float? Finite(float f) => float.IsFinite(f) ? f : null;
    private object Slots(nint baseAddr, int len)
    {
        var buf = new byte[len];
        var n = _reader.TryReadBytes(baseAddr, buf);
        var rows = new List<SlotDto>();
        for (var o = 0; o + 8 <= n; o += 8)
        {
            var i64 = BitConverter.ToInt64(buf, o);
            var f0 = BitConverter.ToSingle(buf, o);
            var f1 = BitConverter.ToSingle(buf, o + 4);
            var ptr = (nint)i64;
            var isPtr = (ulong)ptr is >= 0x10000 and <= 0x7FFFFFFFFFFF;
            var readable = isPtr && _reader.TryReadStruct<long>(ptr, out _);

            // StdVector hint: this slot = First, next two = Last/Cap, with a plausible element span.
            long vecCount = 0;
            if (isPtr && o + 24 <= n)
            {
                var last = BitConverter.ToInt64(buf, o + 8);
                var cap = BitConverter.ToInt64(buf, o + 16);
                var span = last - i64;
                if (last >= i64 && cap >= last && span is > 0 and <= 0x400000 && (ulong)last <= 0x7FFFFFFFFFFF)
                    vecCount = span / 8;
            }

            // String hints: a std::wstring interpreted AT this offset, and (for readable pointers) a
            // short utf-8/utf-16 preview of the target (catches name pointers, vtable-adjacent strings).
            var str = PrintableWString(baseAddr + o);
            if (str is null && readable)
            {
                var u8 = _reader.ReadStringUtf8(ptr, 32);
                if (IsPrintable(u8)) str = "→ \"" + u8 + "\"";
                else { var u16 = _reader.ReadStringUtf16(ptr, 32); if (IsPrintable(u16)) str = "→ L\"" + u16 + "\""; }
            }

            var ascii = new char[8];
            for (var k = 0; k < 8; k++) { var c = buf[o + k]; ascii[k] = c is >= 0x20 and < 0x7f ? (char)c : '.'; }

            rows.Add(new SlotDto(o, HexBytes(buf, o), i64,
                BitConverter.ToInt32(buf, o), BitConverter.ToInt32(buf, o + 4), Finite(f0), Finite(f1),
                isPtr ? Hex(ptr) : null, readable, str, vecCount, new string(ascii)));
        }
        return new { addr = Hex(baseAddr), len = n, rows };
    }

    // ── UI tree: one element + its direct children (lazy expand via addr). ──
    private record UiChild(string Addr, int ChildCount, bool Visible, string Flags);
    private object UiNode(nint el)
    {
        var visible = Visible(el);
        var flags = _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var f) ? f : 0;
        var (childFirst, childCount) = VecOf(el + Poe2.UiElement.Children);
        var kids = new List<UiChild>();
        for (long i = 0; i < Math.Min(childCount, 4096); i++)
        {
            var c = SafePtr(childFirst + (nint)(i * 8));
            if (!IsUiElement(c)) continue;
            var (_, cc) = VecOf(c + Poe2.UiElement.Children);
            kids.Add(new UiChild(Hex(c), (int)cc, Visible(c), Hex32(_reader.TryReadStruct<uint>(c + Poe2.UiElement.Flags, out var cf) ? cf : 0)));
        }
        return new { addr = Hex(el), visible, flags = Hex32(flags), childCount, children = kids };
    }

    private bool Visible(nint el)
        => _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var f) && ((f >> Poe2.UiElement.FlagVisibleBit) & 1) != 0;

    // A real UiElement is self-referential at +Self (same trick --find-map uses). Validating this
    // before walking/recording a child prunes garbage pointers that would otherwise let the BFS
    // wander out of the UI tree and explode the node count (and destabilize diffs).
    private bool IsUiElement(nint el) => el != 0 && SafePtr(el + Poe2.UiElement.Self) == el;

    // Full BFS of the UI tree from UiRoot → flat list, for the client's snapshot/diff. Each node
    // carries a child-index PATH ("0/3/12") so a flipped element can be located in the live tree.
    private record UiFlatDto(string Addr, string Parent, int Depth, string Path, bool Visible, string Flags, int ChildCount);
    private object UiFlat(int max)
    {
        var list = new List<UiFlatDto>();
        if (!_live.TryResolve(out var igs, out _, out _)) return new { error = "not in game", nodes = list };
        var root = SafePtr(igs + Poe2.InGameState.UiRoot);
        if (root == 0) return new { error = "no UiRoot", nodes = list };
        var queue = new Queue<(nint el, nint parent, int depth, string path)>();
        queue.Enqueue((root, 0, 0, "root"));
        var seen = new HashSet<nint>();
        // The tree is large (~60k elements). Read each element's header in ONE syscall (covers
        // Self@0x08, Children vec@0x10/0x18, Flags@0x180), and bulk-read each child-pointer array in
        // one more — instead of 4+ separate reads per node. Self-validate at dequeue (== el) to prune
        // any non-UI pointer that slipped into a children vector.
        var hdr = new byte[Poe2.UiElement.Flags + 4];
        while (queue.Count > 0 && list.Count < max)
        {
            var (el, parent, depth, path) = queue.Dequeue();
            if (el == 0 || !seen.Add(el)) continue;
            if (_reader.TryReadBytes(el, hdr) < hdr.Length) continue;
            if ((nint)BitConverter.ToInt64(hdr, Poe2.UiElement.Self) != el) continue;   // not a UiElement
            var flags = BitConverter.ToUInt32(hdr, Poe2.UiElement.Flags);
            var first = (nint)BitConverter.ToInt64(hdr, Poe2.UiElement.Children);
            var last = (nint)BitConverter.ToInt64(hdr, Poe2.UiElement.Children + 8);
            var span = (long)last - (long)first;
            var count = first != 0 && span is > 0 and <= 0x400000 ? span / 8 : 0;
            list.Add(new UiFlatDto(Hex(el), Hex(parent), depth, path, ((flags >> Poe2.UiElement.FlagVisibleBit) & 1) != 0, Hex32(flags), (int)count));

            var take = (int)Math.Min(count, 4096);
            if (take > 0)
            {
                var cb = new byte[take * 8];
                if (_reader.TryReadBytes(first, cb) == cb.Length)
                    for (var i = 0; i < take; i++)
                    {
                        var c = (nint)BitConverter.ToInt64(cb, i * 8);
                        if ((ulong)c is >= 0x10000 and <= 0x7FFFFFFFFFFF) queue.Enqueue((c, el, depth + 1, $"{path}/{i}"));
                    }
            }
        }
        return new { count = list.Count, capped = list.Count >= max, nodes = list };
    }

    // ── Entities: walk the awake std::map (id, addr, metadata). ──
    private record EntityDto(uint Id, string Addr, string Metadata);
    private List<EntityDto> Entities(int limit)
    {
        var list = new List<EntityDto>();
        if (!_live.TryResolve(out _, out var ai, out _)) return list;
        var head = SafePtr(ai + Poe2.AreaInstance.AwakeEntities);
        if (head == 0) return list;
        var queue = new Queue<nint>(); queue.Enqueue(SafePtr(head + Poe2.StdMapNode.Parent));
        var visited = new HashSet<nint>();
        while (queue.Count > 0 && visited.Count < 200000 && list.Count < limit)
        {
            var node = queue.Dequeue();
            if (node == 0 || node == head || !visited.Add(node)) continue;
            if (!_reader.TryReadStruct<byte>(node + Poe2.StdMapNode.IsNil, out var nil) || nil != 0) continue;
            _reader.TryReadStruct<uint>(node + Poe2.StdMapNode.KeyId, out var id);
            var ent = SafePtr(node + Poe2.StdMapNode.ValueEntityPtr);
            queue.Enqueue(SafePtr(node + Poe2.StdMapNode.Left));
            queue.Enqueue(SafePtr(node + Poe2.StdMapNode.Right));
            if (ent == 0 || id >= Poe2.EntityList.VisualIdThreshold) continue;
            list.Add(new EntityDto(id, Hex(ent), MetaOf(ent)));
        }
        return list.OrderBy(e => e.Id).ToList();
    }

    // ── Components: an entity's component lookup → (name, addr). ──
    private record ComponentDto(string Name, string Addr);
    private List<ComponentDto> Components(nint entity)
    {
        var list = new List<ComponentDto>();
        var details = SafePtr(entity + Poe2.Entity.EntityDetailsPtr);
        if (details == 0) return list;
        var lookup = SafePtr(details + Poe2.EntityDetails.ComponentLookUpPtr);
        if (lookup == 0) return list;
        if (!_reader.TryReadStruct<StdVector>(entity + Poe2.Entity.ComponentList, out var cl)) return list;
        var compCount = ((long)cl.Last - (long)cl.First) / 8;
        if (compCount is <= 0 or > 256) return list;
        var bFirst = SafePtr(lookup + Poe2.ComponentLookUp.NameAndIndexBucket);
        if (!_reader.TryReadStruct<nint>(lookup + Poe2.ComponentLookUp.NameAndIndexBucket + 8, out var bLast)) return list;
        var entries = ((long)bLast - (long)bFirst) / Poe2.ComponentLookUp.EntryStride;
        if (bFirst == 0 || entries is <= 0 or > 256) return list;
        for (long i = 0; i < entries; i++)
        {
            var e = bFirst + (nint)(i * Poe2.ComponentLookUp.EntryStride);
            if (!_reader.TryReadStruct<int>(e + 8, out var index) || index < 0 || index >= compCount) continue;
            var name = _reader.ReadStringUtf8(SafePtr(e), 40);
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new ComponentDto(name, Hex(SafePtr(cl.First + (nint)(index * 8)))));
        }
        return list.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
    }

    // ── Value search across private regions. type ∈ {int,float,str,ptr}. ──
    private object Search(string? type, string? value, int max)
    {
        if (string.IsNullOrEmpty(value)) return new { error = "empty value", hits = Array.Empty<string>() };
        byte[]? needle = null; int align = 4;
        switch ((type ?? "int").ToLowerInvariant())
        {
            case "int":  if (!long.TryParse(value, out var iv)) return Err("not an int"); needle = BitConverter.GetBytes((int)iv); align = 4; break;
            case "float":if (!float.TryParse(value, out var fv)) return Err("not a float"); needle = BitConverter.GetBytes(fv); align = 4; break;
            case "ptr":  if (!TryAddr(value, out var pv)) return Err("not a pointer"); needle = BitConverter.GetBytes((long)pv); align = 8; break;
            case "str":  needle = Encoding.Unicode.GetBytes(value); align = 2; break;   // utf-16, as the game stores text
            default: return Err("type must be int|float|str|ptr");
        }

        var hits = new List<string>();
        var chunk = new byte[1 << 20];
        long scanned = 0;
        foreach (var (regionBase, regionSize) in _reader.Process.EnumerateReadableRegions(privateOnly: true))
        {
            if (hits.Count >= max) break;
            long off = 0;
            while (off < regionSize && hits.Count < max)
            {
                var toRead = (int)Math.Min(chunk.Length, regionSize - off);
                var read = _reader.TryReadBytes(regionBase + (nint)off, chunk.AsSpan(0, toRead));
                if (read <= 0) break;
                scanned += read;
                for (var i = 0; i + needle.Length <= read; i += align)
                {
                    var match = true;
                    for (var k = 0; k < needle.Length; k++) if (chunk[i + k] != needle[k]) { match = false; break; }
                    if (match) { hits.Add(Hex(regionBase + (nint)(off + i))); if (hits.Count >= max) break; }
                }
                if (read != toRead) break;
                off += toRead;
            }
        }
        return new { type, value, scannedMB = scanned / 1024 / 1024, capped = hits.Count >= max, hits };

        static object Err(string m) => new { error = m, hits = Array.Empty<string>() };
    }

    // ── helpers ──
    private nint SafePtr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return u is >= 0x10000 and <= 0x7FFFFFFFFFFF ? p : 0;
    }

    private (nint first, long count) VecOf(nint vecAddr)
    {
        var first = SafePtr(vecAddr);
        if (first == 0 || !_reader.TryReadStruct<nint>(vecAddr + 8, out var last)) return (0, 0);
        var span = (long)last - (long)first;
        return span is > 0 and <= 0x400000 ? (first, span / 8) : (first, 0);
    }

    private string MetaOf(nint entity)
    {
        var d = SafePtr(entity + Poe2.Entity.EntityDetailsPtr);
        return d == 0 ? "" : ReadStdWString(d + Poe2.EntityDetails.Name);
    }

    // MSVC std::wstring (SSO): len @ +0x10; inline UTF-16 at base if len < 8, else buffer ptr @ +0x00.
    private string ReadStdWString(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 1024) return "";
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        var ptr = SafePtr(addr);
        return ptr == 0 ? "" : _reader.ReadStringUtf16(ptr, len);
    }

    private string? PrintableWString(nint addr)
    {
        var s = ReadStdWString(addr);
        return IsPrintable(s) ? "\"" + s + "\"" : null;
    }

    private static bool IsPrintable(string? s) => !string.IsNullOrEmpty(s) && s.Length >= 2 && s.All(c => c is >= ' ' and < (char)0x7f);

    private static string Hex(nint a) => $"0x{(long)a:X}";
    private static string Hex32(uint v) => $"0x{v:X8}";
    private static string HexBytes(byte[] b, int o) => string.Join(' ', Enumerable.Range(0, 8).Select(j => b[o + j].ToString("X2")));

    private static bool TryAddr(string? s, out nint addr)
    {
        addr = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!long.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        addr = (nint)v;
        return true;
    }

    private static int ParseInt(string? s, int dflt, int min, int max)
        => int.TryParse(s, out var v) ? Math.Clamp(v, min, max) : dflt;

    private static void Write(HttpListenerContext ctx, int status, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void WriteHtml(HttpListenerContext ctx, string html)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.StatusCode = 200;
        ctx.Response.ContentType = "text/html; charset=utf-8";
        ctx.Response.Headers["Cache-Control"] = "no-store";
        ctx.Response.ContentLength64 = bytes.Length;
        ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
        ctx.Response.OutputStream.Close();
    }

    private static void TryWrite(HttpListenerContext ctx, int status, string body)
    {
        try { Write(ctx, status, body); } catch { }
    }

    public void Dispose()
    {
        _running = false;
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}

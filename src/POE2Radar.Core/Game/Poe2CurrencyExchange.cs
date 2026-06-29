using System.Collections.Generic;

namespace POE2Radar.Core.Game;

/// <summary>
/// Reads the in-game Currency Exchange (Kalguur market) order book so the overlay can show depth + recommend
/// a sell ratio. The panel is a UI element — a direct child of GameUi — holding two inline <c>std::vector</c>
/// headers: <see cref="Poe2Offsets.CurrencyExchange.WantedStockVec"/> ("I Want" side) and
/// <see cref="Poe2Offsets.CurrencyExchange.OfferedStockVec"/> ("I Have" side). Each points to a heap array of
/// stride-0x10 <see cref="StockEntry"/> records. The vector HEADERS are stable inline in the panel; only their
/// begin/end churn as orders fill, so we read them LIVE (no stale-pointer race).
///
/// <para>Layout cross-referenced 1:1 from the PoE1 ExileApi <c>CurrencyExchangePanel</c> (via the user's POEMCP
/// live-eval) and validated live in PoE2 2026-06-29. Ratio = Get/Give (derived, not stored). The final entry
/// of each side is the <c>{Get=0,Give=0,ListedCount=n}</c> "everything-below" rest bucket.</para>
///
/// <para>The panel is resolved STRUCTURALLY — scan GameUi's visible children for one carrying valid stock
/// vectors at both offsets — rather than by a (drift-prone) child index or a non-unique flag fingerprint.
/// Self-healing across patches as long as the two entry offsets hold; re-discover with Research
/// <c>--exchange-panel3</c>.</para>
/// </summary>
public sealed class Poe2CurrencyExchange
{
    private readonly MemoryReader _reader;
    private nint _panel; // cached panel element (0 = closed / unresolved)

    public Poe2CurrencyExchange(MemoryReader reader) => _reader = reader;

    /// <summary>One order-book row: amounts the order receives/gives + listed stock. Ratio is Get/Give.</summary>
    public readonly record struct StockEntry(int Get, int Give, int ListedCount)
    {
        public double Ratio => Give != 0 ? Get / (double)Give : 0.0;
        public bool IsRest => Get == 0 && Give == 0; // the "< rest" everything-below bucket
    }

    /// <summary>The two sides of the live book. <see cref="Open"/> is false when the exchange isn't on screen.
    /// <see cref="HaveQty"/> is the "I Have" quantity the user is selling (0 if not readable).</summary>
    public sealed class Book
    {
        public static readonly Book Closed = new(System.Array.Empty<StockEntry>(), System.Array.Empty<StockEntry>(), false, 0, 0);
        public Book(IReadOnlyList<StockEntry> offered, IReadOnlyList<StockEntry> wanted, bool open, int haveQty, nint panel)
        { Offered = offered; Wanted = wanted; Open = open; HaveQty = haveQty; PanelAddr = panel; }
        public IReadOnlyList<StockEntry> Offered { get; }
        public IReadOnlyList<StockEntry> Wanted { get; }
        public bool Open { get; }
        public int HaveQty { get; }
        public nint PanelAddr { get; }   // the panel UI element (for pinning the overlay to its screen rect)
    }

    /// <summary>Read the live book (both sides + the "I Have" quantity) for the currently-open exchange pair.
    /// <see cref="Book.Closed"/> when the panel isn't open. Cheap: resolve the panel (cached) then two small
    /// vector reads + decode + one child-text read.</summary>
    public Book Read(nint inGameState)
    {
        var panel = ResolvePanel(inGameState);
        if (panel == 0) return Book.Closed;
        var offered = ReadStock(panel + Poe2.CurrencyExchange.OfferedStockVec);
        var wanted = ReadStock(panel + Poe2.CurrencyExchange.WantedStockVec);
        if (offered.Count == 0 && wanted.Count == 0) return Book.Closed;
        return new Book(offered, wanted, true, ReadHaveQty(panel), panel);
    }

    /// <summary>The "I Have" quantity = the integer text of the panel's <see cref="Poe2Offsets.CurrencyExchange.HaveQtyChildIndex"/>
    /// direct child (the count input). 0 when blank / not readable.</summary>
    private int ReadHaveQty(nint panel)
    {
        if (!Children(panel, out var first, out var n)) return 0;
        var idx = Poe2.CurrencyExchange.HaveQtyChildIndex;
        if (idx < 0 || idx >= n) return 0;
        var child = Ptr(first + (nint)(idx * 8));
        if (child == 0) return 0;
        var text = ReadStdWString(child + Poe2.UiElement.Text).Trim();
        return int.TryParse(text, out var q) && q > 0 ? q : 0;
    }

    private string ReadStdWString(nint addr)
    {
        if (!_reader.TryReadStruct<int>(addr + 0x10, out var len) || len <= 0 || len > 256) return "";
        if (len < 8) return _reader.ReadStringUtf16(addr, len);
        var ptr = Ptr(addr);
        return ptr == 0 ? "" : _reader.ReadStringUtf16(ptr, len);
    }

    // ── panel resolution (structural; self-healing) ────────────────────────────────────────────────

    private nint ResolvePanel(nint inGameState)
    {
        // Fast path: revalidate the cached panel (still visible + still carries an offered stock vector).
        if (_panel != 0 && Visible(_panel) && IsStockVec(_panel + Poe2.CurrencyExchange.OfferedStockVec))
            return _panel;
        _panel = 0;
        var gameUi = Ptr(inGameState + Poe2.InGameState.UiRoot);
        if (gameUi == 0 || !Children(gameUi, out var first, out var n)) return 0;
        for (long i = 0; i < n; i++)
        {
            var child = Ptr(first + (nint)(i * 8));
            if (child == 0 || !Visible(child)) continue;          // panel-open gate (hidden children skipped)
            if (IsStockVec(child + Poe2.CurrencyExchange.OfferedStockVec)
                && IsStockVec(child + Poe2.CurrencyExchange.WantedStockVec))
                return _panel = child;
        }
        return 0;
    }

    /// <summary>True when the std::vector at <paramref name="vecAddr"/> looks like a real stock list: a clean
    /// stride-0x10 array with ≥1 priced order (Get&gt;0 &amp;&amp; Give&gt;0 &amp;&amp; ListedCount&gt;0) among its first entries.</summary>
    private bool IsStockVec(nint vecAddr)
    {
        if (!_reader.TryReadStruct<StdVector>(vecAddr, out var v) || v.First == 0) return false;
        var bytes = (long)v.Last - (long)v.First;
        if (bytes <= 0 || bytes > 0x80000 || bytes % Poe2.CurrencyExchange.EntryStride != 0) return false;
        var count = bytes / Poe2.CurrencyExchange.EntryStride;
        if (count is < 1 or > 5000) return false;
        var probe = (int)System.Math.Min(count, 4);
        for (var k = 0; k < probe; k++)
        {
            var e = ReadEntry(v.First + (nint)(k * Poe2.CurrencyExchange.EntryStride));
            if (e.Get > 0 && e.Give > 0 && e.ListedCount > 0) return true;
        }
        return false;
    }

    private List<StockEntry> ReadStock(nint vecAddr)
    {
        var result = new List<StockEntry>();
        if (!_reader.TryReadStruct<StdVector>(vecAddr, out var v) || v.First == 0) return result;
        var bytes = (long)v.Last - (long)v.First;
        if (bytes <= 0 || bytes > 0x80000 || bytes % Poe2.CurrencyExchange.EntryStride != 0) return result;
        var count = (int)(bytes / Poe2.CurrencyExchange.EntryStride);
        if (count is < 1 or > 5000) return result;
        for (var k = 0; k < count; k++)
            result.Add(ReadEntry(v.First + (nint)(k * Poe2.CurrencyExchange.EntryStride)));
        return result;
    }

    private StockEntry ReadEntry(nint addr)
    {
        _reader.TryReadStruct<ushort>(addr + Poe2.CurrencyExchange.EntryGet, out var get);
        _reader.TryReadStruct<ushort>(addr + Poe2.CurrencyExchange.EntryGive, out var give);
        _reader.TryReadStruct<int>(addr + Poe2.CurrencyExchange.EntryListedCount, out var listed);
        return new StockEntry(get, give, listed);
    }

    private bool Visible(nint el)
        => _reader.TryReadStruct<uint>(el + Poe2.UiElement.Flags, out var f) && (f & (1u << Poe2.UiElement.FlagVisibleBit)) != 0;

    private bool Children(nint el, out nint first, out long n)
    {
        first = Ptr(el + Poe2.UiElement.Children); n = 0;
        if (first == 0 || !_reader.TryReadStruct<nint>(el + Poe2.UiElement.ChildrenEnd, out var last)) return false;
        n = ((long)last - (long)first) / 8;
        return n is > 0 and <= 4000;
    }

    private nint Ptr(nint addr)
    {
        if (!_reader.TryReadStruct<nint>(addr, out var p)) return 0;
        var u = (ulong)p;
        return (u < 0x10000 || u > 0x7FFFFFFFFFFF) ? 0 : p;
    }
}

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Overlay.Pricing;

/// <summary>
/// Result of a price lookup. <see cref="Exalted"/> is the item's value in Exalted Orbs (PoE2's base
/// economy unit). <see cref="Quantity"/> is the listing count / trade volume — a confidence signal:
/// low-volume rows are often mislisted (a 2-listing leveling unique priced at 5000 div).
/// </summary>
public readonly record struct PriceResult(string Name, double Exalted, int Quantity, string Category)
{
    public bool LowConfidence(int minQty) => Quantity < minQty;
}

/// <summary>
/// Centralized price source, ported in spirit from the user's PoE1 NinjaPriceService and built around
/// the live <b>poe.ninja</b> PoE2 economy API (the gold-standard source, estimated off the official trade
/// API). Fetches the current league's currency-like + unique prices, converts everything to Exalted, and
/// indexes them by normalized name AND by 2D-art basename (the bridge to in-game item reads: a dropped
/// item's RenderItem .dds basename equals poe.ninja's icon basename), caches to disk, and refreshes on a
/// TTL. Reads are lock-free off volatile snapshots; the whole index is swapped atomically.
///
/// <para>Two poe.ninja endpoints, both returning all rows in one (un-paginated) response:</para>
/// <list type="bullet">
///   <item><b>exchange</b> (<c>.../exchange/current/overview?type=…</c>) — fungible/currency-like rows.
///   <c>items[]</c> maps id→name/image, <c>lines[]</c> carries the price; joined by id.</item>
///   <item><b>stash item</b> (<c>.../stash/current/item/overview?type=Unique…</c>) — uniques, with
///   name/icon/price/listingCount all inline on each <c>lines[]</c> row.</item>
/// </list>
///
/// <para>Price unit: every line's <c>primaryValue</c> is in <b>Divine Orbs</b> (validated against known
/// anchors — Divine=1, Exalted≈0.0066, and the dirt-cheap unique floor ≈0.0017 div), and
/// <c>core.rates.exalted</c> is Exalted-per-Divine, so <c>ex = primaryValue × rates.exalted</c> uniformly.</para>
///
/// <para>poe.ninja has no clean league-list endpoint, so the current league NAME is still discovered via
/// poe2scout's lightweight <c>/Leagues</c> (its <c>Value</c> strings match what poe.ninja expects, e.g.
/// "Runes of Aldur"); the divine/chaos RATES come from poe.ninja itself so prices stay self-consistent.</para>
///
/// <para>This is the recyclable data source: ground-loot valuation, runeforge rewards, expedition choices,
/// ritual/vendor overlays all consume <see cref="TryByArt"/> / <see cref="TryByName"/>.</para>
/// </summary>
public sealed class PriceBook
{
    // poe.ninja "stash item" unique types (icon → art basename + price inline per line).
    private static readonly string[] UniqueTypes =
        { "UniqueWeapons", "UniqueArmours", "UniqueAccessories", "UniqueFlasks", "UniqueJewels", "UniqueTablets" };

    // poe.ninja "exchange" currency-like types. Superset of the old poe2scout set — includes
    // SoulCores (poe2scout's "ultimatum"), UncutGems (Uncut Skill/Spirit/Support Gem by level) and
    // LineageSupportGems (named, tradeable). NOTE: individual CUT active skill gems (e.g. "Rain of
    // Blades") are not a traded market on poe.ninja or anywhere — only UncutGems carry a value.
    private static readonly string[] ExchangeTypes =
        { "Currency", "Runes", "Fragments", "Essences", "Expedition", "Breach", "Ritual", "Delirium",
          "UncutGems", "Abyss", "SoulCores", "LineageSupportGems", "Idols" };

    private const string NinjaExchange = "https://poe.ninja/poe2/api/economy/exchange/current/overview";
    private const string NinjaStashItem = "https://poe.ninja/poe2/api/economy/stash/current/item/overview";

    private static readonly HttpClient Http = CreateHttp();
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _cachePath;
    private readonly object _gate = new();

    // Atomically-swapped snapshots (volatile; lock-free reads on the tick/render thread).
    private volatile Dictionary<string, PricedItem> _byArt = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, PricedItem> _byName = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _fetching;
    private DateTime _lastFetchUtc = DateTime.MinValue;
    private string _league = "";
    private string? _leagueOverride;

    public double ExPerDivine { get; private set; } = 1;
    public double ExPerChaos { get; private set; } = 1;
    public bool IsLoaded => _byName.Count > 0 || _byArt.Count > 0;
    public int ItemCount => _byArt.Count + _byName.Count;
    public string League => _league;
    public string Status { get; private set; } = "not started";
    public DateTime LastFetchUtc => _lastFetchUtc;
    public int RefreshIntervalMinutes { get; set; } = 30;

    private sealed record PricedItem(string Name, double Exalted, int Quantity, string Category);

    public PriceBook(string cachePath, string? leagueOverride = null)
    {
        _cachePath = cachePath;
        _leagueOverride = string.IsNullOrWhiteSpace(leagueOverride) ? null : leagueOverride.Trim();
        TryLoadCache();
    }

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("POE2Radar-PriceBook");
        return c;
    }

    /// <summary>Set/clear a manual league (e.g. from settings); triggers a refresh if it changed.</summary>
    public void SetLeagueOverride(string? league)
    {
        var v = string.IsNullOrWhiteSpace(league) ? null : league.Trim();
        if (v == _leagueOverride) return;
        _leagueOverride = v;
        _lastFetchUtc = DateTime.MinValue; // force the next RefreshIfDue to re-fetch
    }

    /// <summary>Call periodically (cheap when not due). Kicks a background fetch when stale and not already running.</summary>
    public void RefreshIfDue()
    {
        if (_fetching) return;
        if (DateTime.UtcNow - _lastFetchUtc < TimeSpan.FromMinutes(RefreshIntervalMinutes)) return;
        _fetching = true;
        _ = Task.Run(FetchAsync);
    }

    public void ForceRefresh()
    {
        if (_fetching) return;
        _fetching = true;
        _ = Task.Run(FetchAsync);
    }

    /// <summary>Look up a unique by its 2D-art basename (e.g. "Earthbound") — the in-game item read key.</summary>
    public PriceResult? TryByArt(string? artBasename)
    {
        if (string.IsNullOrWhiteSpace(artBasename)) return null;
        return _byArt.TryGetValue(artBasename.Trim(), out var p)
            ? new PriceResult(p.Name, p.Exalted, p.Quantity, p.Category) : null;
    }

    /// <summary>Look up any priced item (unique or currency) by display name.</summary>
    public PriceResult? TryByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return _byName.TryGetValue(Normalize(name), out var p)
            ? new PriceResult(p.Name, p.Exalted, p.Quantity, p.Category) : null;
    }

    /// <summary>Convert an Exalted value to a short display string in the largest sensible unit.</summary>
    public string Format(double ex)
    {
        if (ExPerDivine > 1 && ex >= ExPerDivine) return $"{ex / ExPerDivine:0.##} div";
        return $"{ex:0.##} ex";
    }

    // ── fetch ────────────────────────────────────────────────────────────────

    private async Task FetchAsync()
    {
        try
        {
            Status = "fetching…";
            var league = await ResolveLeagueAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(league)) { Status = "no league"; return; }
            var lg = Uri.EscapeDataString(league);

            var byArt = new Dictionary<string, PricedItem>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, PricedItem>(StringComparer.OrdinalIgnoreCase);

            // Rates default to "not yet known"; the first overview response that carries core.rates sets
            // them, and ApplyRates fixes ExPerDivine/ExPerChaos for the rest of the fetch + conversions.
            double exPerDivine = 0;

            foreach (var type in ExchangeTypes)
                await FetchExchangeAsync(lg, type, byArt, byName, () => exPerDivine, r => exPerDivine = r).ConfigureAwait(false);
            foreach (var type in UniqueTypes)
                await FetchUniquesAsync(lg, type, byArt, byName, () => exPerDivine, r => exPerDivine = r).ConfigureAwait(false);

            if (byArt.Count == 0 && byName.Count == 0) { Status = "fetch returned no rows"; return; }

            _byArt = byArt;
            _byName = byName;
            _league = league;
            _lastFetchUtc = DateTime.UtcNow;
            Status = $"loaded {byName.Count} by name + {byArt.Count} by art for '{league}'";
            SaveCache();
        }
        catch (Exception ex) { Status = $"fetch failed: {ex.Message}"; }
        finally { _fetching = false; }
    }

    /// <summary>Discover the current league name via poe2scout's /Leagues (its Value strings are exactly what
    /// poe.ninja expects). A configured override wins; otherwise pick the current softcore league.</summary>
    private async Task<string> ResolveLeagueAsync()
    {
        if (_leagueOverride != null) return _leagueOverride;
        try
        {
            var json = await Http.GetStringAsync("https://poe2scout.com/api/poe2/Leagues").ConfigureAwait(false);
            var leagues = JsonSerializer.Deserialize<List<ScoutLeague>>(json, Json) ?? new();
            var pick = leagues.FirstOrDefault(l => l.IsCurrent && !l.Value.StartsWith("HC", StringComparison.OrdinalIgnoreCase))
                       ?? leagues.FirstOrDefault(l => l.IsCurrent)
                       ?? leagues.FirstOrDefault();
            return pick?.Value ?? "";
        }
        catch { return ""; }
    }

    // Convert poe.ninja core.rates → our Exalted-per-Divine / Exalted-per-Chaos. rates are "units per
    // Divine": exalted = ex/div directly; chaos = chaos/div, so ex/chaos = (ex/div)/(chaos/div).
    private void ApplyRates(NinjaCore? core)
    {
        if (core?.Rates == null) return;
        if (core.Rates.TryGetValue("exalted", out var exPerDiv) && exPerDiv > 0)
        {
            ExPerDivine = exPerDiv;
            if (core.Rates.TryGetValue("chaos", out var chaosPerDiv) && chaosPerDiv > 0)
                ExPerChaos = exPerDiv / chaosPerDiv;
        }
    }

    private async Task FetchExchangeAsync(string leagueEscaped, string type,
        Dictionary<string, PricedItem> byArt, Dictionary<string, PricedItem> byName,
        Func<double> getRate, Action<double> setRate)
    {
        try
        {
            var url = $"{NinjaExchange}?league={leagueEscaped}&type={type}";
            var data = JsonSerializer.Deserialize<NinjaOverview>(await Http.GetStringAsync(url).ConfigureAwait(false), Json);
            if (data?.Lines == null) return;
            if (getRate() <= 0) { ApplyRates(data.Core); setRate(ExPerDivine); }
            var rate = getRate();
            if (rate <= 0) return; // can't price without a rate

            // items[] maps id → name/image; join the priced lines[] to it by id.
            var meta = new Dictionary<string, NinjaItem>(StringComparer.Ordinal);
            if (data.Items != null)
                foreach (var it in data.Items)
                    if (!string.IsNullOrEmpty(it.Id)) meta[it.Id] = it;

            foreach (var ln in data.Lines)
            {
                if (ln.Id.ValueKind != JsonValueKind.String) continue; // exchange ids are strings
                var id = ln.Id.GetString();
                if (string.IsNullOrEmpty(id) || !meta.TryGetValue(id, out var m)) continue;
                if (string.IsNullOrWhiteSpace(m.Name) || ln.PrimaryValue <= 0) continue;
                var ex = ln.PrimaryValue * rate;
                var qty = (int)Math.Clamp(ln.VolumePrimaryValue ?? 0, 0, int.MaxValue);
                var item = new PricedItem(m.Name.Trim(), ex, qty, type);
                Upsert(byName, Normalize(m.Name), item);
                var art = ArtBasenameFromIcon(m.Image);
                if (art != null) Upsert(byArt, art, item);
            }
        }
        catch { /* a missing/empty category is fine — skip it */ }
    }

    private async Task FetchUniquesAsync(string leagueEscaped, string type,
        Dictionary<string, PricedItem> byArt, Dictionary<string, PricedItem> byName,
        Func<double> getRate, Action<double> setRate)
    {
        try
        {
            var url = $"{NinjaStashItem}?league={leagueEscaped}&type={type}";
            var data = JsonSerializer.Deserialize<NinjaOverview>(await Http.GetStringAsync(url).ConfigureAwait(false), Json);
            if (data?.Lines == null) return;
            if (getRate() <= 0) { ApplyRates(data.Core); setRate(ExPerDivine); }
            var rate = getRate();
            if (rate <= 0) return;

            foreach (var ln in data.Lines)
            {
                if (string.IsNullOrWhiteSpace(ln.Name) || ln.PrimaryValue <= 0) continue;
                var ex = ln.PrimaryValue * rate;
                var item = new PricedItem(ln.Name.Trim(), ex, ln.ListingCount ?? 0, type);
                Upsert(byName, Normalize(ln.Name), item);
                var art = ArtBasenameFromIcon(ln.Icon);
                if (art != null) Upsert(byArt, art, item);
            }
        }
        catch { }
    }

    // Keep the higher-value listing on a key collision (shared art across variants → show the best).
    private static void Upsert(Dictionary<string, PricedItem> map, string key, PricedItem item)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!map.TryGetValue(key, out var cur) || item.Exalted > cur.Exalted) map[key] = item;
    }

    /// <summary>poe.ninja icon → basename. ".../<hash>/Earthbound.png" → "Earthbound" (handles both the
    /// absolute web.poecdn.com unique icons and the relative "/gen/image/.../X.png" currency images).</summary>
    private static string? ArtBasenameFromIcon(string? iconUrl)
    {
        if (string.IsNullOrWhiteSpace(iconUrl)) return null;
        var noQuery = iconUrl.Split('?')[0];
        var seg = noQuery.Split('/').LastOrDefault();
        if (string.IsNullOrWhiteSpace(seg)) return null;
        var dot = seg.LastIndexOf('.');
        var name = dot > 0 ? seg[..dot] : seg;
        return name.Length >= 2 ? name : null;
    }

    private static string Normalize(string s) => s.Trim();

    // ── disk cache ─────────────────────────────────────────────────────────────

    private sealed class CacheDto
    {
        public string League { get; set; } = "";
        public DateTime FetchedUtc { get; set; }
        public double ExPerDivine { get; set; }
        public double ExPerChaos { get; set; }
        public Dictionary<string, PricedItem> ByArt { get; set; } = new();
        public Dictionary<string, PricedItem> ByName { get; set; } = new();
    }

    private void TryLoadCache()
    {
        try
        {
            if (!File.Exists(_cachePath)) { Status = "no cache; will fetch"; return; }
            var dto = JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(_cachePath), Json);
            if (dto == null) return;
            // Honor a configured league: a cache for a different league shouldn't be used.
            if (_leagueOverride != null && !string.Equals(dto.League, _leagueOverride, StringComparison.OrdinalIgnoreCase)) return;
            _byArt = new Dictionary<string, PricedItem>(dto.ByArt, StringComparer.OrdinalIgnoreCase);
            _byName = new Dictionary<string, PricedItem>(dto.ByName, StringComparer.OrdinalIgnoreCase);
            _league = dto.League;
            _lastFetchUtc = dto.FetchedUtc;
            if (dto.ExPerDivine > 0) ExPerDivine = dto.ExPerDivine;
            if (dto.ExPerChaos > 0) ExPerChaos = dto.ExPerChaos;
            Status = $"cache: {ItemCount} entries for '{_league}'";
        }
        catch (Exception ex) { Status = $"cache load failed: {ex.Message}"; }
    }

    private void SaveCache()
    {
        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var dto = new CacheDto
            {
                League = _league, FetchedUtc = _lastFetchUtc, ExPerDivine = ExPerDivine, ExPerChaos = ExPerChaos,
                ByArt = new(_byArt), ByName = new(_byName),
            };
            File.WriteAllText(_cachePath, JsonSerializer.Serialize(dto, Json));
        }
        catch { }
    }

    // ── DTOs ─────────────────────────────────────────────────────────────────────

    // poe2scout /Leagues — used only to discover the current league name.
    private sealed class ScoutLeague
    {
        public string Value { get; set; } = "";
        public bool IsCurrent { get; set; }
    }

    // poe.ninja overview (both exchange + stash item share this shape).
    private sealed class NinjaOverview
    {
        public NinjaCore? Core { get; set; }
        public List<NinjaLine>? Lines { get; set; }
        public List<NinjaItem>? Items { get; set; }   // exchange only (id→name/image); empty for uniques
    }
    private sealed class NinjaCore
    {
        public Dictionary<string, double>? Rates { get; set; }   // "units per Divine": exalted, chaos
        public string? Primary { get; set; }
        public string? Secondary { get; set; }
    }
    private sealed class NinjaItem
    {
        public string Id { get; set; } = "";       // string id ("exalted", "divine", …) on exchange
        public string Name { get; set; } = "";
        public string? Image { get; set; }
    }
    private sealed class NinjaLine
    {
        // id is a string on the exchange endpoint and a number on the uniques endpoint — read raw and
        // branch on ValueKind (uniques carry name/icon inline so their id is unused).
        public JsonElement Id { get; set; }
        public string? Name { get; set; }            // uniques: display name
        public string? Icon { get; set; }            // uniques: absolute art url
        public double PrimaryValue { get; set; }     // value in Divine Orbs (× rates.exalted → Exalted)
        public double? VolumePrimaryValue { get; set; } // exchange: trade volume (confidence)
        public int? ListingCount { get; set; }       // uniques: listing count (confidence)
    }
}

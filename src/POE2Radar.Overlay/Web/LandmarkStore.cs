using System.Text.Json;
using System.Text.Json.Serialization;
using POE2Radar.Core.Game;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// User-editable overlay on top of the baked-in curated landmark table (<see cref="CustomLandmarkData"/>:
/// area code → tile-path pattern → friendly label). Backs the dashboard "Landmarks" tab so users can
/// view the known lookups, fix/rename/add entries, suppress wrong ones, and import/export the list to
/// share or submit back for baking into a release.
///
/// <para>Model: a per-area pattern→label map persisted to <c>config/landmarks.json</c>. A string value
/// overrides/adds a label; a <c>null</c> value SUPPRESSES (the curated label is treated as absent). The
/// effective lookup is the user overlay first (area, then the global <c>*</c> bucket), then the baked
/// data — mirroring how the baked table itself resolves.</para>
/// </summary>
public sealed class LandmarkStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, Dictionary<string, string?>> _user = new(StringComparer.OrdinalIgnoreCase);
    private volatile Dictionary<string, Dictionary<string, string?>> _snap = new(StringComparer.OrdinalIgnoreCase);
    private volatile int _generation;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = null, // area codes / tile paths are case-sensitive keys — keep verbatim
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public LandmarkStore(string filePath)
    {
        _filePath = filePath;
        Load();
        Rebuild();
    }

    /// <summary>Bumped on every edit so the tick loop can rebuild the landmark scan (same pattern as
    /// the other live-editable stores).</summary>
    public int Generation => _generation;

    /// <summary>One merged row for the dashboard: where it came from and its current label/state.</summary>
    public readonly record struct Entry(string Area, string Pattern, string? Label, string Source, bool Suppressed);

    /// <summary>Effective curated label for (area, tile path): the user overlay wins (a string label, or
    /// <c>null</c> when the user suppressed it), else the baked table. Lock-free hot path (called from the
    /// per-area landmark scan).</summary>
    public string? Lookup(string areaCode, string tilePath)
    {
        var snap = _snap;
        if (TryUser(snap, areaCode, tilePath, out var label)) return label;
        if (!string.Equals(areaCode, "*", StringComparison.Ordinal) && TryUser(snap, "*", tilePath, out var glob)) return glob;
        return CustomLandmarkData.TryMatch(areaCode, tilePath);
    }

    private static bool TryUser(Dictionary<string, Dictionary<string, string?>> data, string area, string path, out string? label)
    {
        label = null;
        if (data.TryGetValue(area, out var m))
            foreach (var (pat, val) in m)
                if (!string.IsNullOrEmpty(pat) && path.Contains(pat, StringComparison.OrdinalIgnoreCase)) { label = val; return true; }
        return false;
    }

    /// <summary>The merged baked+user table as a flat, ordered list for the dashboard (each row tagged
    /// baked/user + suppressed).</summary>
    public List<Entry> All()
    {
        var res = new Dictionary<(string area, string pat), Entry>();
        foreach (var (area, map) in CustomLandmarkData.Load())
            foreach (var (pat, label) in map)
                res[(area, pat)] = new Entry(area, pat, label, "baked", false);
        lock (_gate)
            foreach (var (area, map) in _user)
                foreach (var (pat, val) in map)
                    res[(area, pat)] = new Entry(area, pat, val, "user", val is null);
        return res.Values.OrderBy(e => e.Area, StringComparer.OrdinalIgnoreCase)
                         .ThenBy(e => e.Pattern, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Add / override / rename (label) or suppress (label == null) an (area, pattern) entry.</summary>
    public void Set(string area, string pattern, string? label)
    {
        if (string.IsNullOrWhiteSpace(area) || string.IsNullOrWhiteSpace(pattern)) return;
        lock (_gate)
        {
            if (!_user.TryGetValue(area, out var m)) { m = new(StringComparer.Ordinal); _user[area] = m; }
            m[pattern] = label;
            Rebuild(); Save();
        }
    }

    /// <summary>Delete a USER entry (reverts to whatever the baked table says for that pattern).</summary>
    public void Remove(string area, string pattern)
    {
        lock (_gate)
        {
            if (_user.TryGetValue(area, out var m) && m.Remove(pattern))
            {
                if (m.Count == 0) _user.Remove(area);
                Rebuild(); Save();
            }
        }
    }

    /// <summary>Replace the whole user overlay (the "Import" action). Posted map is area → pattern → label
    /// (null = suppress). It then drives the effective lookup on top of the baked baseline.</summary>
    public void Import(Dictionary<string, Dictionary<string, string?>>? data)
    {
        lock (_gate)
        {
            _user = data ?? new(StringComparer.OrdinalIgnoreCase);
            Rebuild(); Save();
        }
    }

    /// <summary>Export the EFFECTIVE merged table (baked overlaid by user edits, suppressed entries
    /// dropped) as a clean <c>area → pattern → label</c> JSON — ready to share or submit for baking.</summary>
    public string ExportJson()
    {
        var merged = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> Area(string a) => merged.TryGetValue(a, out var m) ? m : merged[a] = new(StringComparer.Ordinal);
        foreach (var (area, map) in CustomLandmarkData.Load())
            foreach (var (pat, label) in map) Area(area)[pat] = label;
        lock (_gate)
            foreach (var (area, map) in _user)
                foreach (var (pat, val) in map)
                {
                    if (val is null) { if (merged.TryGetValue(area, out var mm)) mm.Remove(pat); }
                    else Area(area)[pat] = val;
                }
        return JsonSerializer.Serialize(merged, Json);
    }

    private void Rebuild()
    {
        // Immutable snapshot copy for lock-free Lookup.
        var copy = new Dictionary<string, Dictionary<string, string?>>(_user.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (area, map) in _user) copy[area] = new Dictionary<string, string?>(map, StringComparer.Ordinal);
        _snap = copy;
        _generation++;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var data = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string?>>>(File.ReadAllText(_filePath), Json);
            if (data != null) _user = new Dictionary<string, Dictionary<string, string?>>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) { Console.Error.WriteLine($"Landmark store load failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_user, Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Landmark store save failed: {ex.Message}"); }
    }
}

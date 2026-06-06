using System.Reflection;
using System.Text.Json;

namespace POE2Radar.Overlay.Web;

/// <summary>
/// One user "watch" rule: highlight entities whose metadata contains <see cref="Pattern"/>
/// (case-insensitive) with a custom <see cref="Color"/> / <see cref="Shape"/> / <see cref="Size"/>
/// and draw <see cref="Label"/> next to them. <see cref="Enabled"/> toggles a rule without deleting
/// it. <see cref="Shape"/> is a named icon from <c>IconLibrary</c>; <see cref="Color"/> is
/// <c>#RRGGBB</c>.
/// </summary>
public sealed record WatchedEntry(
    string Pattern, string Label, string Color, bool Enabled = true, float Size = 7f, string Shape = "Diamond");

/// <summary>
/// User-managed highlight list. Entities matching an enabled rule are force-drawn (even if their
/// category would normally be filtered) with the rule's color/shape/size and a text label — the
/// quick "show me where the X is, and name it" layer, distinct from the built-in style/mechanic
/// table in <see cref="Config.RadarStyles"/>. Persisted to <c>config/watched_entities.json</c>;
/// seeded from the embedded <c>default_watched.json</c> on first run.
///
/// <para>Thread-safety: <see cref="Match"/> runs on the render thread per entity while the HTTP API
/// thread mutates. We keep an immutable snapshot in a volatile field (lock-free reads) and rebuild
/// it under a lock on mutation — same pattern as <see cref="HiddenEntities"/>.</para>
/// </summary>
public sealed class WatchedEntities
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private readonly Dictionary<string, WatchedEntry> _entries = new(StringComparer.OrdinalIgnoreCase); // under _gate
    private volatile WatchedEntry[] _enabledSnapshot = Array.Empty<WatchedEntry>();                     // immutable
    private static readonly JsonSerializerOptions Json =
        new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public WatchedEntities(string filePath)
    {
        _filePath = filePath;
        Load();
        if (_entries.Count == 0) LoadDefaults();
        if (Migrate()) Save();
        Rebuild();
    }

    /// <summary>One-time, idempotent repair of seeded watch patterns from older builds (loaded
    /// verbatim). Each fix rewrites an over-broad default key to a precise directory/path term that
    /// matches the real feature but not unrelated entities, preserving the user's label/color/shape:
    /// <list type="bullet">
    /// <item>"Expedition2Encounter" → "Expedition2/Expedition2Encounter" (the bare key is a substring
    ///   of "Expedition2EncounterCrack", so it tagged the detonation-effect objects).</item>
    /// <item>"Strongbox" → "StrongBoxes" (the bare key matched the box's …Strongbox monsters and
    ///   ordinary area chests named "…Strongbox", e.g. KedgeBayChestStrongbox).</item>
    /// </list>
    /// Only the EXACT known-bad keys are touched, so any pattern the user customized is left alone.
    /// Returns true if it changed anything.</summary>
    private bool Migrate()
    {
        (string stale, string precise)[] fixes =
        {
            ("Expedition2Encounter", "Expedition2/Expedition2Encounter"),
            ("Strongbox",            "StrongBoxes"),
        };
        var changed = false;
        foreach (var (stale, precise) in fixes)
        {
            if (!_entries.TryGetValue(stale, out var old)) continue;
            _entries.Remove(stale);
            if (!_entries.ContainsKey(precise)) _entries[precise] = old with { Pattern = precise };
            Console.WriteLine($"Watched entities: migrated stale \"{stale}\" pattern to \"{precise}\".");
            changed = true;
        }
        return changed;
    }

    /// <summary>All rules (snapshot copy; safe to enumerate off-thread).</summary>
    public IReadOnlyList<WatchedEntry> All { get { lock (_gate) return _entries.Values.ToArray(); } }

    /// <summary>Number of enabled rules. Lock-free (reads the compiled snapshot).</summary>
    public int EnabledCount => _enabledSnapshot.Length;

    /// <summary>First ENABLED rule whose pattern is contained in <paramref name="metadata"/>, else null.
    /// Lock-free hot path.</summary>
    public WatchedEntry? Match(string metadata)
    {
        if (string.IsNullOrEmpty(metadata)) return null;
        var snap = _enabledSnapshot; // volatile read of an immutable array
        foreach (var e in snap)
            if (metadata.Contains(e.Pattern, StringComparison.OrdinalIgnoreCase)) return e;
        return null;
    }

    public void Add(string pattern, string label, string color, float size = 7f, string shape = "Diamond")
    {
        if (string.IsNullOrWhiteSpace(pattern)) return;
        lock (_gate)
        {
            _entries[pattern] = new WatchedEntry(pattern, label, color, true, size, shape);
            Rebuild(); Save();
        }
    }

    public void Update(string pattern, string? label = null, string? color = null,
                       bool? enabled = null, float? size = null, string? shape = null)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(pattern, out var e)) return;
            _entries[pattern] = e with
            {
                Label = label ?? e.Label,
                Color = color ?? e.Color,
                Enabled = enabled ?? e.Enabled,
                Size = size ?? e.Size,
                Shape = shape ?? e.Shape,
            };
            Rebuild(); Save();
        }
    }

    public void Remove(string pattern)
    {
        lock (_gate)
        {
            if (!_entries.Remove(pattern)) return;
            Rebuild(); Save();
        }
    }

    /// <summary>Rebuild the immutable enabled-only snapshot. Call under <see cref="_gate"/>.</summary>
    private void Rebuild()
        => _enabledSnapshot = _entries.Values.Where(e => e.Enabled).ToArray();

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return;
            var list = JsonSerializer.Deserialize<List<WatchedEntry>>(File.ReadAllText(_filePath), Json);
            if (list == null) return;
            foreach (var e in list)
                if (!string.IsNullOrWhiteSpace(e.Pattern)) _entries[e.Pattern] = e;
        }
        catch (Exception ex) { Console.Error.WriteLine($"Watched entities load failed: {ex.Message}"); }
    }

    private void LoadDefaults()
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var resName = asm.GetManifestResourceNames().FirstOrDefault(n => n.Contains("default_watched"));
            if (resName == null) return;
            using var stream = asm.GetManifestResourceStream(resName)!;
            var list = JsonSerializer.Deserialize<List<WatchedEntry>>(stream, Json);
            if (list == null) return;
            foreach (var e in list)
                if (!string.IsNullOrWhiteSpace(e.Pattern)) _entries[e.Pattern] = e;
            Save();
        }
        catch (Exception ex) { Console.Error.WriteLine($"Watched defaults load failed: {ex.Message}"); }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_entries.Values.ToList(), Json));
        }
        catch (Exception ex) { Console.Error.WriteLine($"Watched entities save failed: {ex.Message}"); }
    }
}

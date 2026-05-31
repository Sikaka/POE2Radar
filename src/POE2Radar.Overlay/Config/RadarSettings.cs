using System.Text.Json;
using System.Text.Json.Serialization;

namespace POE2Radar.Overlay.Config;

/// <summary>
/// User-tweakable overlay settings, persisted as JSON next to the executable
/// (<c>config/radar_settings.json</c>). Defaults reproduce the original hardcoded behavior exactly,
/// so a missing/partial file changes nothing. Calibration is saved live as hotkeys adjust it.
/// </summary>
public sealed class RadarSettings
{
    // ── Feature flags (reserved for later phases; no behavior wired yet). ──
    public bool HideJunk { get; set; } = false;
    public bool ShowPath { get; set; } = false;
    public bool UseCuratedLandmarks { get; set; } = true;
    public bool DrawAllLandmarkPaths { get; set; } = false;

    // ── Radar display toggles. ──
    public bool ShowMonsters { get; set; } = true;
    public bool ShowTerrain { get; set; } = true;

    // ── Navigation-menu widget: which screen corner it is pinned to.
    //    One of "TopLeft", "TopRight", "BottomLeft", "BottomRight". ──
    public string NavMenuCorner { get; set; } = "TopLeft";

    // ── Persistent auto-nav: substrings matched (case-insensitive Contains) against a navigation
    //    target's MatchKey (tile path / entity metadata). On every zone change, every target whose
    //    MatchKey matches ANY pattern is auto-selected (up to the 8-color cap), so entering a new
    //    zone auto-draws a path to e.g. the expedition encounter. Seeded with one example so the
    //    feature is visible out of the box; clear the list to disable. ──
    public List<string> AutoNavPatterns { get; set; } = new() { "ExpeditionEncounter" };

    // ── Monster HP bars (world-space nameplates) by rarity.
    //    Defaults preserve prior behavior: Magic/Rare/Unique shown, Normal hidden. ──
    public bool HpBarNormal { get; set; } = false;
    public bool HpBarMagic { get; set; } = true;
    public bool HpBarRare { get; set; } = true;
    public bool HpBarUnique { get; set; } = true;

    // ── Projection calibration (PageUp/Down = scale, arrows = offset, Home = reset). ──
    public float ScaleMul { get; set; } = 1.0f;
    public float OffX { get; set; } = 0f;
    public float OffY { get; set; } = 0f;

    // ── Auto-flask thresholds + per-flask cooldowns (milliseconds). ──
    public float LifeThresholdPct { get; set; } = 65f;
    public float ManaThresholdPct { get; set; } = 30f;
    public int LifeCooldownMs { get; set; } = 2500;
    public int ManaCooldownMs { get; set; } = 2000;

    // ── Flask key codes (Win32 virtual-key). Defaults: '1' = life, '2' = mana. ──
    public int LifeKey { get; set; } = 0x31;
    public int ManaKey { get; set; } = 0x32;

    // ── HTTP API. ──
    public int ApiPort { get; set; } = 7777;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Config file path: a "config" directory next to the executable.</summary>
    public static string FilePath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "radar_settings.json");

    /// <summary>
    /// Load settings from disk. Returns defaults if the file is missing (and writes a default file),
    /// and is tolerant of partial/missing keys. Never throws on IO/parse errors — logs and falls back.
    /// </summary>
    public static RadarSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                var fresh = new RadarSettings();
                fresh.Save();
                return fresh;
            }

            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<RadarSettings>(json, Json);
            return loaded ?? new RadarSettings();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings load failed ({ex.Message}); using defaults.");
            return new RadarSettings();
        }
    }

    /// <summary>Persist current settings to disk. Never throws on IO error — logs and continues.</summary>
    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Settings save failed: {ex.Message}");
        }
    }
}

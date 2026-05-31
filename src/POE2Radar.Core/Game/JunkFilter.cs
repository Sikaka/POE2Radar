namespace POE2Radar.Core.Game;

/// <summary>
/// Suppresses "junk" entities from the radar by case-insensitive substring match on the
/// entity's metadata path. Junk = nodes with no gameplay value: invisible attachment points,
/// cosmetic MTX, FX/audio/material asset nodes, invisible modifier daemons, pets, clones, etc.
///
/// Pattern set reconciled (2026-05-31) from two diverging sources in the upstream fork:
///   - the shipped <c>JunkPatterns</c> array in POE2Radar-main/.../Game/JunkFilter.cs
///   - the proposed regex + per-pattern table in docs/entity_database_analysis.md
/// Counts below are from that analysis (6,692 entity paths from the GGPK, 2026-05-30).
///
/// Categories (why each is junk):
///   Visual / cosmetic asset nodes — purely render-side, never interactable:
///     "/attachments"  (438)  visual attachment points on models
///     "microtransactions" (53)  MTX cosmetics
///     "/timelines/"   (191)  player cosmetic animation timelines
///     "stashskins"    (60)   stash visual variants
///     "hairstyles"    (—)    character cosmetic hair
///     "/outfits/"     (—)    character cosmetic outfits
///   Engine asset / effect definition nodes — not world entities:
///     "/fx/"   (19+)  particle effects
///     "/mat/"  (15+)  material definitions
///     "/ao/"   (13+)  ambient-occlusion data
///     "/epk/"  (14+)  effect packages
///     "/graph/"(15)   skill graph nodes
///     "/audio/"(17)   sound definitions
///     "/environment/" (48)  environment settings
///   Invisible daemon / modifier entities — logic carriers with no model:
///     "monstermods"       (194) invisible monster-modifier daemons
///     "essencemoddaemons" (44)  invisible essence modifiers
///     "tormentedspirits"  (48)  tormented-spirit daemons
///     "/daemon/"          (38+) generic invisible helper entities
///   Pets / clones / summon base classes — clutter, not real targets:
///     "/pet/"          (14)  pet cosmetics
///     "/clone/"        (36)  player clone effects
///     "playersummoned" (41)  summoned-entity base classes
///   Already-handled-elsewhere markers:
///     "bossroomminimapicon" (41) minimap-icon entities (surfaced via POI, not raw dots)
///     "/runemarked"        (—)  rune-marker decorator nodes
///
/// Deliberately DROPPED from the fork's proposed regex (err toward NOT hiding real entities):
///   "weapons/" — the analysis regex includes it for cosmetic weapon skins, but the same
///     substring would match dropped weapon ITEMS / weapon-wielding entities. Too broad; excluded.
///
/// Anchoring choices: where the fork's regex used a bare token (e.g. "clone", "environment",
/// "outfits", "runemarked") we keep the shipped array's slash-delimited form ("/clone/",
/// "/environment/", "/outfits/", "/runemarked") to reduce the chance of matching a legitimate
/// path segment by accident.
/// </summary>
public static class JunkFilter
{
    private static readonly string[] JunkPatterns =
    [
        // Visual / cosmetic asset nodes
        "/attachments",
        "microtransactions",
        "/timelines/",
        "stashskins",
        "hairstyles",
        "/outfits/",
        // Engine asset / effect definition nodes
        "/fx/",
        "/mat/",
        "/ao/",
        "/epk/",
        "/graph/",
        "/audio/",
        "/environment/",
        // Invisible daemon / modifier entities
        "monstermods",
        "essencemoddaemons",
        "tormentedspirits",
        "/daemon/",
        // Pets / clones / summon base classes
        "/pet/",
        "/clone/",
        "playersummoned",
        // Already handled elsewhere / decorator markers
        "bossroomminimapicon",
        "/runemarked",
    ];

    /// <summary>
    /// True if the entity's metadata path matches any junk pattern (case-insensitive substring).
    /// Integration is a one-liner on the render side:
    /// <c>if (hideJunk &amp;&amp; JunkFilter.IsJunk(e.Metadata)) continue;</c>
    /// </summary>
    public static bool IsJunk(string metadata)
    {
        if (string.IsNullOrEmpty(metadata))
            return false;
        foreach (var p in JunkPatterns)
            if (metadata.Contains(p, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}

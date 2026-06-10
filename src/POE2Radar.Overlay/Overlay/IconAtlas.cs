using System.Numerics;
using POE2Radar.Overlay.Config;
using SixLabors.ImageSharp;

namespace POE2Radar.Overlay;

/// <summary>
/// GPU texture atlas for radar icons. Loads the GameHelper2 icons.png sprite sheet and uploads
/// it to the GPU. Maps our icon names to specific (column, row) positions in the sprite sheet.
/// Renderer draws icons with AddImage() + UV coordinates instead of per-frame primitive shapes.
/// <para>Thread model: <see cref="EnsureInitialized"/> must be called from the DX11/ImGui render thread.
/// <see cref="TryGet"/> is lock-free and safe from any thread once initialized.</para>
/// </summary>
public static class IconAtlas
{
    public const int IconSize = 64;
    private const string IconsFileName = "icons.png";

    private static volatile bool _initialized;
    private static nint _atlasTextureId;
    private static int _atlasWidth, _atlasHeight;
    private static readonly Dictionary<string, IconTexture> _map = new(StringComparer.OrdinalIgnoreCase);

    public readonly record struct IconTexture(nint TextureId, Vector2 UV0, Vector2 UV1);

    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Mapping from our icon names to GameHelper2 sprite sheet positions (column, row).
    /// Each icon in the sprite sheet is 64x64 pixels.
    /// </summary>
    private static readonly Dictionary<string, (int col, int row)> IconPositions = new(StringComparer.OrdinalIgnoreCase)
    {
        // Basic shapes mapped to GameHelper2 icons
        ["Circle"] = (0, 14),        // Normal Monster - round icon
        ["Diamond"] = (4, 57),       // Rare Monster - diamond shape
        ["Star"] = (6, 57),          // Unique Monster - star shape
        ["Square"] = (8, 38),        // Strongbox - square shape
        ["Plus"] = (7, 0),           // Shrine - plus/cross shape
        ["Triangle"] = (4, 57),      // Use Rare Monster as triangle-ish
        ["Hexagon"] = (6, 57),       // Use Unique Monster as hexagon-ish
        ["Pentagon"] = (0, 14),      // Use Normal Monster as pentagon-ish
        ["Cross"] = (7, 0),          // Shrine - cross shape
        ["Ring"] = (0, 0),           // Self - ring/circle shape
        ["Heart"] = (6, 9),          // Chest icon - heart-ish
        ["Shield"] = (8, 38),        // Strongbox - shield-ish
        ["Gem"] = (4, 48),           // Rare Chest - gem-ish
        ["ArrowUp"] = (5, 0),        // Delirium Bomb - arrow-ish
        ["TriangleDown"] = (6, 0),   // Delirium Spawner - downward triangle
        ["Exclamation"] = (12, 44),  // POI Monster default - exclamation
        ["Droplet"] = (1, 13),       // Magic Chest - droplet-ish
    };

    /// <summary>Upload sprite sheet to GPU. Must be called from DX11/ImGui thread. Idempotent.</summary>
    public static void EnsureInitialized(ClickableTransparentOverlay.Overlay overlay)
    {
        if (_initialized) return;

        var iconsPath = Path.Combine(AppContext.BaseDirectory, "Overlay", IconsFileName);
        if (!File.Exists(iconsPath))
        {
            Console.Error.WriteLine($"IconAtlas: {IconsFileName} not found at {iconsPath}");
            return;
        }

        using var image = Image.Load(iconsPath);
        _atlasWidth = image.Width;
        _atlasHeight = image.Height;

        overlay.AddOrGetImagePointer(iconsPath, false, out _atlasTextureId, out var texW, out var texH);

        foreach (var (name, (col, row)) in IconPositions)
        {
            var uv0 = new Vector2(
                (float)(col * IconSize) / texW,
                (float)(row * IconSize) / texH);
            var uv1 = new Vector2(
                (float)((col + 1) * IconSize) / texW,
                (float)((row + 1) * IconSize) / texH);

            _map[name] = new IconTexture(_atlasTextureId, uv0, uv1);
        }

        _initialized = true;
        Console.WriteLine($"IconAtlas: Loaded {_map.Count} icons from {IconsFileName} ({texW}x{texH})");
    }

    /// <summary>Look up icon's GPU texture + UV rect. Returns false if unknown or not initialized.</summary>
    public static bool TryGet(string? iconName, out IconTexture tex)
    {
        tex = default;
        if (!_initialized || string.IsNullOrEmpty(iconName)) return false;
        return _map.TryGetValue(iconName, out tex);
    }

    /// <summary>Compute a UV rect for any sprite-sheet cell in icons.png.</summary>
    public static bool TryGet(SpriteIconRef? icon, out IconTexture tex)
    {
        tex = default;
        if (!_initialized || icon is null) return false;
        if (!string.Equals(icon.Sheet, IconsFileName, StringComparison.OrdinalIgnoreCase)) return false;

        var cell = icon.CellSize > 0 ? icon.CellSize : IconSize;
        var x0 = icon.Col * cell;
        var y0 = icon.Row * cell;
        var x1 = x0 + cell;
        var y1 = y0 + cell;
        if (x0 < 0 || y0 < 0 || x1 > _atlasWidth || y1 > _atlasHeight) return false;

        tex = new IconTexture(
            _atlasTextureId,
            new Vector2((float)x0 / _atlasWidth, (float)y0 / _atlasHeight),
            new Vector2((float)x1 / _atlasWidth, (float)y1 / _atlasHeight));
        return true;
    }

    public static IReadOnlyCollection<string> Names => _map.Keys;
}

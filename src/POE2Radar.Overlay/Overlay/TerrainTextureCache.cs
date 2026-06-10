using POE2Radar.Core.Game;
using POE2Radar.Overlay.Config;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace POE2Radar.Overlay;

/// <summary>
/// Bakes the walkable grid into an area/style-keyed transparent PNG and uploads it once. Rendering then
/// draws the whole terrain with one image quad instead of thousands of per-frame line segments.
/// </summary>
public sealed class TerrainTextureCache
{
    private readonly Dictionary<string, string> _paths = new(StringComparer.Ordinal);

    public bool TryGet(
        ClickableTransparentOverlay.Overlay overlay,
        TextureRegistry registry,
        Poe2Live.TerrainData terrain,
        uint areaHash,
        TerrainSettings style,
        out TextureRegistry.TextureHandle handle)
    {
        handle = default;
        if (terrain.Width <= 1 || terrain.Height <= 1 || terrain.Walkable.Length < terrain.Width * terrain.Height)
            return false;

        var key = BuildKey(terrain, areaHash, style);
        if (!_paths.TryGetValue(key, out var path) || !File.Exists(path))
        {
            path = Path.Combine(AppContext.BaseDirectory, "cache", "terrain", key + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            BuildTextureFile(path, terrain, style);
            _paths[key] = path;
            registry.Forget(path);
        }

        return registry.TryGet(overlay, path, out handle);
    }

    private static string BuildKey(Poe2Live.TerrainData terrain, uint areaHash, TerrainSettings style)
        => $"terrain_{areaHash:X8}_{terrain.Width}x{terrain.Height}_{StyleHash(style):X8}";

    private static uint StyleHash(TerrainSettings style)
    {
        unchecked
        {
            var h = 2166136261u;
            AddString(style.InteriorColor);
            AddString(style.EdgeColor);
            AddInt(BitConverter.SingleToInt32Bits(style.InteriorOpacity));
            AddInt(BitConverter.SingleToInt32Bits(style.EdgeOpacity));
            AddInt(style.ImGuiEdgeDetail);
            AddInt(BitConverter.SingleToInt32Bits(style.ImGuiEdgeThickness));
            return h;

            void AddString(string? s)
            {
                foreach (var ch in s ?? "")
                {
                    h ^= ch;
                    h *= 16777619u;
                }
            }

            void AddInt(int v)
            {
                h ^= (byte)v; h *= 16777619u;
                h ^= (byte)(v >> 8); h *= 16777619u;
                h ^= (byte)(v >> 16); h *= 16777619u;
                h ^= (byte)(v >> 24); h *= 16777619u;
            }
        }
    }

    private static void BuildTextureFile(string path, Poe2Live.TerrainData terrain, TerrainSettings style)
    {
        var interior = Parse(style.InteriorColor, style.InteriorOpacity);
        var edge = Parse(style.EdgeColor, style.EdgeOpacity);
        var drawInterior = interior.A != 0;
        var w = terrain.Width;
        var h = terrain.Height;
        var cells = terrain.Walkable;

        using var image = new Image<Rgba32>(w, h, Color.Transparent);
        for (var y = 1; y < h - 1; y++)
        {
            var row = y * w;
            for (var x = 1; x < w - 1; x++)
            {
                var idx = row + x;
                if (cells[idx] == 0) continue;

                var isEdge =
                    cells[idx - 1] == 0 ||
                    cells[idx + 1] == 0 ||
                    cells[idx - w] == 0 ||
                    cells[idx + w] == 0;

                if (isEdge) image[x, y] = edge;
                else if (drawInterior) image[x, y] = interior;
            }
        }

        image.Save(path);
    }

    private static Rgba32 Parse(string? hex, float opacity)
    {
        opacity = Math.Clamp(opacity, 0f, 1f);
        if (string.IsNullOrWhiteSpace(hex)) return new Rgba32(255, 255, 255, (byte)MathF.Round(opacity * 255f));
        hex = hex.Trim();
        if (hex.StartsWith("#", StringComparison.Ordinal)) hex = hex[1..];
        if (hex.Length != 6 || !uint.TryParse(hex, System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            rgb = 0xFFFFFF;

        return new Rgba32(
            (byte)((rgb >> 16) & 0xFF),
            (byte)((rgb >> 8) & 0xFF),
            (byte)(rgb & 0xFF),
            (byte)MathF.Round(opacity * 255f));
    }
}

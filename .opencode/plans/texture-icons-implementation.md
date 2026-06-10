# Texture-Based Icon Rendering — Implementation Plan

## Overview
Replace per-entity `AddCircleFilled()` calls with GPU-texture `AddImage()` calls using a sprite sheet atlas, matching GameHelper2's approach.

## Files to Create/Modify

### 1. NEW: `src/POE2Radar.Overlay/Overlay/IconAtlas.cs` (~200 lines)

```csharp
using System.Numerics;
using ClickableTransparentOverlay;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace POE2Radar.Overlay;

/// <summary>
/// GPU texture atlas for radar icons. Rasterizes SVG icon definitions from <see cref="IconLibrary"/>
/// into a single sprite sheet and uploads to GPU via CTO library. Renderer draws icons with
/// AddImage() + UV coordinates instead of per-frame primitive shapes.
/// </summary>
public static class IconAtlas
{
    public const int IconSize = 64;
    private const int AtlasColumns = 8;
    private const int Padding = 2;

    private static volatile bool _initialized;
    private static nint _atlasPtr;
    private static int _atlasWidth, _atlasHeight;
    private static int _cols, _rows;
    private static readonly Dictionary<string, IconTexture> _map = new(StringComparer.OrdinalIgnoreCase);

    public readonly record struct IconTexture(nint Ptr, Vector2 UV0, Vector2 UV1);

    public static bool IsInitialized => _initialized;

    /// <summary>Upload sprite sheet to GPU. Must be called from DX11/ImGui thread. Idempotent.</summary>
    public static void EnsureInitialized(Overlay overlay)
    {
        if (_initialized) return;

        var icons = IconLibrary.Ordered;
        if (icons.Count == 0) return;

        _cols = Math.Min(AtlasColumns, icons.Count);
        _rows = (icons.Count + _cols - 1) / _cols;
        var cellSize = IconSize + Padding * 2;
        _atlasWidth = _cols * cellSize;
        _atlasHeight = _rows * cellSize;

        using var atlas = new Image<Rgba32>(_atlasWidth, _atlasHeight);

        for (var i = 0; i < icons.Count; i++)
        {
            var col = i % _cols;
            var row = i / _cols;
            var iconImage = RasterizeIcon(icons[i], IconSize);
            var ox = col * cellSize + Padding;
            var oy = row * cellSize + Padding;
            Blit(atlas, iconImage, ox, oy);

            var uv0 = new Vector2(
                (float)(col * cellSize + Padding) / _atlasWidth,
                (float)(row * cellSize + Padding) / _atlasHeight);
            var uv1 = new Vector2(
                (float)(col * cellSize + Padding + IconSize) / _atlasWidth,
                (float)(row * cellSize + Padding + IconSize) / _atlasHeight);
            _map[icons[i].Name] = new IconTexture(0, uv0, uv1);
        }

        overlay.AddOrGetImagePointer("poe2radar_icon_atlas", atlas, true, out _atlasPtr);

        var keys = _map.Keys.ToList();
        foreach (var k in keys)
            _map[k] = new IconTexture(_atlasPtr, _map[k].UV0, _map[k].UV1);

        _initialized = true;
        Console.WriteLine($"IconAtlas: {_map.Count} icons -> {_atlasWidth}x{_atlasHeight} sprite sheet");
    }

    /// <summary>Look up icon's GPU texture + UV rect. Returns false if unknown or not initialized.</summary>
    public static bool TryGet(string? iconName, out IconTexture tex)
    {
        tex = default;
        if (!_initialized || string.IsNullOrEmpty(iconName)) return false;
        return _map.TryGetValue(iconName, out tex);
    }

    public static IReadOnlyCollection<string> Names => _map.Keys;

    // ── SVG -> RGBA rasterizer ─────────────────────────────────────────────────

    private static Image<Rgba32> RasterizeIcon(IconDef icon, int size)
    {
        var img = new Image<Rgba32>(size, size);
        if (icon.VbW <= 0 || icon.VbH <= 0) return img;

        var scaleX = size / icon.VbW;
        var scaleY = size / icon.VbH;
        var offX = -icon.VbX * scaleX;
        var offY = -icon.VbY * scaleY;

        Vector2 Transform(Vector2 p) => new(p.X * scaleX + offX, p.Y * scaleY + offY);

        foreach (var pathD in icon.Paths)
        {
            var figures = SvgPath.Parse(pathD);
            foreach (var fig in figures)
            {
                var edges = new List<(float y0, float y1, float xAtY0, float dxPerDy)>();
                CollectEdges(fig, Transform, edges);
                if (edges.Count > 0) ScanlineFill(img, edges);
            }
        }
        return img;
    }

    private static void CollectEdges(SvgPath.SvgFigure fig, Func<Vector2, Vector2> transform,
        List<(float y0, float y1, float xAtY0, float dxPerDy)> edges)
    {
        var points = new List<Vector2> { transform(fig.Start) };

        foreach (var seg in fig.Segs)
        {
            switch (seg.Kind)
            {
                case SvgPath.SegKind.Line:
                    points.Add(transform(seg.End));
                    break;
                case SvgPath.SegKind.Quad:
                {
                    var prev = points[^1];
                    var c = transform(seg.C1);
                    var end = transform(seg.End);
                    for (var t = 0.25f; t <= 1.001f; t += 0.25f)
                    {
                        var tt = Math.Min(t, 1f);
                        var mt = 1f - tt;
                        points.Add(new Vector2(
                            mt * mt * prev.X + 2 * mt * tt * c.X + tt * tt * end.X,
                            mt * mt * prev.Y + 2 * mt * tt * c.Y + tt * tt * end.Y));
                    }
                    break;
                }
                case SvgPath.SegKind.Cubic:
                {
                    var prev = points[^1];
                    var c1 = transform(seg.C1);
                    var c2 = transform(seg.C2);
                    var end = transform(seg.End);
                    for (var s = 1; s <= 8; s++)
                    {
                        var t = s / 8f;
                        var mt = 1f - t;
                        points.Add(new Vector2(
                            mt * mt * mt * prev.X + 3 * mt * mt * t * c1.X + 3 * mt * t * t * c2.X + t * t * t * end.X,
                            mt * mt * mt * prev.Y + 3 * mt * mt * t * c1.Y + 3 * mt * t * t * c2.Y + t * t * t * end.Y));
                    }
                    break;
                }
            }
        }

        for (var i = 0; i < points.Count - 1; i++)
            AddEdge(points[i], points[i + 1], edges);
        if (fig.Closed && points.Count >= 2)
            AddEdge(points[^1], points[0], edges);
    }

    private static void AddEdge(Vector2 a, Vector2 b,
        List<(float y0, float y1, float xAtY0, float dxPerDy)> edges)
    {
        var dy = b.Y - a.Y;
        if (MathF.Abs(dy) < 1e-4f) return;
        edges.Add((Math.Min(a.Y, b.Y), Math.Max(a.Y, b.Y),
            a.Y < b.Y ? a.X : b.X, (b.X - a.X) / dy));
    }

    private static void ScanlineFill(Image<Rgba32> img,
        List<(float y0, float y1, float xAtY0, float dxPerDy)> edges)
    {
        var minY = Math.Max(0, (int)MathF.Floor(edges.Min(e => e.y0)));
        var maxY = Math.Min(img.Height - 1, (int)MathF.Ceiling(edges.Max(e => e.y1)));
        var white = new Rgba32(255, 255, 255, 255);

        for (var y = minY; y <= maxY; y++)
        {
            var scanY = y + 0.5f;
            var xs = new List<float>();
            foreach (var (y0, y1, xAtY0, dxPerDy) in edges)
                if (scanY >= y0 && scanY < y1)
                    xs.Add(xAtY0 + (scanY - y0) * dxPerDy);

            if (xs.Count < 2) continue;
            xs.Sort();
            for (var i = 0; i + 1 < xs.Count; i += 2)
            {
                var x0 = Math.Max(0, (int)MathF.Floor(xs[i]));
                var x1 = Math.Min(img.Width - 1, (int)MathF.Ceiling(xs[i + 1]));
                for (var x = x0; x <= x1; x++)
                    img[x, y] = white;
            }
        }
    }

    private static void Blit(Image<Rgba32> atlas, Image<Rgba32> src, int ox, int oy)
    {
        for (var y = 0; y < src.Height && oy + y < atlas.Height; y++)
        {
            var srcRow = src.GetPixelRowSpan(y);
            for (var x = 0; x < src.Width && ox + x < atlas.Width; x++)
                if (srcRow[x].A > 0)
                    atlas[ox + x, oy + y] = srcRow[x];
        }
    }
}
```

---

### 2. MODIFY: `src/POE2Radar.Overlay/Web/DisplayRules.cs`

**Add to `DisplayRule` class (after line 38):**
```csharp
    public string? IconName { get; set; }  // icon from IconLibrary; null = use Shape primitive
```

**In `BuildDefault()` method, propagate IconName from MechanicStyle (around line 200):**
```csharp
    foreach (var m in st.Mechanics ?? new())
        rules.Add(new DisplayRule
        {
            Name = m.Name, Enabled = m.Enabled,
            Categories = new(m.Categories ?? new()), Match = new(m.Match ?? new()),
            Shape = m.Shape, Color = m.Color, Opacity = m.Opacity, Size = m.Size,
            IconName = m.IconName,  // <-- ADD THIS LINE
        });
```

---

### 3. MODIFY: `src/POE2Radar.Overlay/Config/RadarSettings.cs`

**Add to `IconStyle` class (after line 254):**
```csharp
    public string? IconName { get; set; }  // texture icon name; null = use Shape primitive
```

**Update default `Mechanics` list (around line 357) to include IconName:**
```csharp
    public List<MechanicStyle> Mechanics { get; set; } = new()
    {
        new() { Name = "Expedition", Match = new() { "Expedition2/Expedition2Encounter" }, Categories = new() { "Other" }, Shape = "Plus", IconName = "Plus", Color = "#26E6D9", Opacity = 1f, Size = 7f },
        new() { Name = "Ritual",     Match = new() { "Ritual" },                            Shape = "Star", IconName = "Star", Color = "#FF3355", Opacity = 1f, Size = 7f },
        new() { Name = "Breach",     Match = new() { "Breach" },                            Shape = "Diamond", IconName = "Diamond", Color = "#A64DFF", Opacity = 1f, Size = 7f },
        new() { Name = "Strongbox",  Match = new() { "StrongBoxes" }, Categories = new() { "Chest" }, Shape = "Square", IconName = "Square", Color = "#FFB300", Opacity = 1f, Size = 6f },
        new() { Name = "Essence",    Match = new() { "Essence" },                           Shape = "Triangle", IconName = "Triangle", Color = "#33E0FF", Opacity = 1f, Size = 7f },
        new() { Name = "Shrine",     Match = new() { "Shrine" },                            Shape = "Star", IconName = "Star", Color = "#7DFF7D", Opacity = 1f, Size = 6f },
    };
```

---

### 4. MODIFY: `src/POE2Radar.Overlay/Overlay/ImGuiRadarOverlay.cs`

**Add field to class (around line 30):**
```csharp
    private bool _atlasInitialized;
```

**In `Render()` method, after the `_closeRequested` check (around line 100):**
```csharp
    if (!_atlasInitialized && inGame)
    {
        IconAtlas.EnsureInitialized(this);
        _atlasInitialized = true;
    }
```

**In `DrawMap()` method, replace entity drawing (around line 291):**

BEFORE:
```csharp
    dl.AddCircleFilled(p, radius, ColorU32(color, rule?.Opacity ?? 0.95f), 16);
```

AFTER:
```csharp
    var halfSize = new NumVec2(radius, radius);
    if (rule?.IconName is { } iconName && IconAtlas.TryGet(iconName, out var tex))
    {
        dl.AddImage(tex.Ptr, p - halfSize, p + halfSize, tex.UV0, tex.UV1,
            ColorU32(rule.Color ?? "#FFFFFF", rule.Opacity));
    }
    else
    {
        dl.AddCircleFilled(p, radius, ColorU32(color, rule?.Opacity ?? 0.95f), 16);
    }
```

**Same pattern for landmark drawing (around line 305):**

BEFORE:
```csharp
    dl.AddCircleFilled(p, lmSize, ColorU32(lmColor, tr?.Opacity ?? 0.95f), 12);
```

AFTER:
```csharp
    var lmHalf = new NumVec2(lmSize, lmSize);
    if (tr?.IconName is { } lmIcon && IconAtlas.TryGet(lmIcon, out var lmTex))
    {
        dl.AddImage(lmTex.Ptr, p - lmHalf, p + lmHalf, lmTex.UV0, lmTex.UV1,
            ColorU32(lmColor, tr.Opacity));
    }
    else
    {
        dl.AddCircleFilled(p, lmSize, ColorU32(lmColor, tr?.Opacity ?? 0.95f), 12);
    }
```

---

## Summary

| File | Action | Lines |
|------|--------|-------|
| `Overlay/IconAtlas.cs` | CREATE | ~200 |
| `Web/DisplayRules.cs` | MODIFY | +2 |
| `Config/RadarSettings.cs` | MODIFY | +7 |
| `Overlay/ImGuiRadarOverlay.cs` | MODIFY | +20 |
| **Total** | | **~230** |

## Backward Compatibility
- Existing `display_rules.json` files work unchanged (IconName is null → fallback to primitives)
- Both rendering paths coexist during migration
- Users can gradually add IconName to their rules

## Performance Impact
- Before: ~16 line segments per circle × N entities per frame (CPU)
- After: 1 textured quad per entity (GPU blit)
- Expected: 5-10x reduction in draw call CPU overhead for 500+ entities

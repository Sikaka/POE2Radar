using System.Numerics;
using ImGuiNET;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Diagnostics;
using NumVec2 = System.Numerics.Vector2;
using GameVec2 = POE2Radar.Core.Game.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// Experimental GameHelper-style overlay backend: GPU/ImGui draw lists instead of a DIB +
/// UpdateLayeredWindow present. It consumes the same RenderContext as the legacy renderer so the
/// memory/pathfinding pipeline stays external and unchanged.
/// </summary>
public sealed class ImGuiRadarOverlay : ClickableTransparentOverlay.Overlay
{
    private volatile RenderContext? _ctx;
    private volatile bool _closeRequested;
    private int _renderCrashLogged;
    private int _width = 800;
    private int _height = 600;
    private readonly object _boundsLock = new();
    private System.Drawing.Point _position;
    private System.Drawing.Size _size = new(800, 600);
    private static readonly Vector4[] PathPalette =
    [
        new(0.20f, 0.90f, 0.40f, 1f),
        new(1.00f, 0.55f, 0.10f, 1f),
        new(0.30f, 0.70f, 1.00f, 1f),
        new(1.00f, 0.30f, 0.70f, 1f),
        new(0.95f, 0.90f, 0.20f, 1f),
        new(0.60f, 0.40f, 1.00f, 1f),
        new(0.20f, 1.00f, 0.85f, 1f),
        new(1.00f, 0.40f, 0.40f, 1f),
    ];

    public ImGuiRadarOverlay()
        : base("POE2Radar ImGuiDx", true, 3840, 2160)
    {
        VSync = false;
    }

    public int OverlayWidth => _width;
    public int OverlayHeight => _height;

    public void UpdateContext(RenderContext ctx) => _ctx = ctx;

    public void SetGameBounds(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        lock (_boundsLock)
        {
            _position = new System.Drawing.Point(x, y);
            _size = new System.Drawing.Size(width, height);
            _width = width;
            _height = height;
        }
    }

    public void RequestClose() => _closeRequested = true;

    protected override void Render()
    {
        try
        {
            if (_closeRequested)
            {
                Close();
                return;
            }

            lock (_boundsLock)
            {
                Position = _position;
                Size = _size;
            }

            var ctx = _ctx;
            if (ctx is null || !ctx.Active || !ctx.InGame) return;

            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;

            var dl = ImGui.GetBackgroundDrawList();
            if (ctx.Map.IsVisible)
                DrawMap(dl, ctx);
            else
                DrawPathsWorld(dl, ctx);

            DrawNavChip(ctx);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _renderCrashLogged, 1) == 0)
                CrashLog.Write("ImGuiDx render crashed", ex);
            Close();
        }
    }

    private static void DrawMap(ImDrawListPtr dl, RenderContext ctx)
    {
        var W = ctx.WindowWidth;
        var H = ctx.WindowHeight;
        var center = new NumVec2(W * 0.5f + ctx.Map.ShiftX + ctx.OffsetX, H * 0.5f + ctx.Map.ShiftY - 20f + ctx.OffsetY);
        var scale = MathF.Max(0.01f, ctx.Map.Zoom * (H / 677f) * ctx.ScaleMul);

        if (ctx.ShowTerrain && ctx.Terrain is { } terrain)
            DrawTerrainEdges(dl, ctx, terrain, center, scale);

        if (ctx.ShowPath)
            DrawPathsMap(dl, ctx, center, scale);

        if (ctx.ShowMonsters)
        {
            foreach (var e in ctx.Entities)
            {
                var rule = ctx.Resolve?.Invoke(e);
                if (rule is { Hide: true }) continue;
                var p = Project(e.Grid, ctx.PlayerGrid, center, scale);
                if (p.X < -20 || p.Y < -20 || p.X > W + 20 || p.Y > H + 20) continue;
                var color = rule?.Color ?? EntityColor(e);
                var radius = rule?.Size ?? EntityRadius(e);
                dl.AddCircleFilled(p, radius, ColorU32(color, rule?.Opacity ?? 0.95f), 16);
            }
        }

        foreach (var lm in ctx.Landmarks)
        {
            var p = Project(lm.Center, ctx.PlayerGrid, center, scale);
            if (p.X < -40 || p.Y < -40 || p.X > W + 40 || p.Y > H + 40) continue;
            dl.AddCircleFilled(p, 4.5f, ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.35f, 0.95f, 0.95f)), 12);
        }

        if (ctx.ShowPlayerBlip)
            dl.AddCircleFilled(center, 5.5f, ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.95f, 1.00f, 1.00f)), 18);
    }

    private static void DrawTerrainEdges(ImDrawListPtr dl, RenderContext ctx, Poe2Live.TerrainData terrain, NumVec2 center, float scale)
    {
        var data = terrain.Walkable;
        var bytesPerRow = terrain.Width;
        if (data.Length == 0 || bytesPerRow <= 0) return;

        var rows = data.Length / bytesPerRow;
        var stride = Math.Max(1, Math.Min(6, (int)MathF.Ceiling(1.5f / MathF.Max(scale, 0.05f))));
        var col = ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.82f, 0.95f, 0.55f));
        for (var y = 1; y < rows - 1; y += stride)
        {
            var row = y * bytesPerRow;
            for (var x = 1; x < bytesPerRow - 1; x += stride)
            {
                var idx = row + x;
                if (idx < 0 || idx >= data.Length || data[idx] == 0) continue;
                if (data[idx - 1] != 0 && data[idx + 1] != 0 && data[idx - bytesPerRow] != 0 && data[idx + bytesPerRow] != 0)
                    continue;

                var p = Project(new NumVec2(x, y), ctx.PlayerGrid, center, scale);
                if (p.X < -4 || p.Y < -4 || p.X > ctx.WindowWidth + 4 || p.Y > ctx.WindowHeight + 4) continue;
                dl.AddCircleFilled(p, 1.25f, col, 6);
            }
        }
    }

    private static void DrawPathsMap(ImDrawListPtr dl, RenderContext ctx, NumVec2 center, float scale)
    {
        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count < 2) continue;
            var col = PathColor(path.ColorSlot);
            NumVec2? prev = null;
            foreach (var (x, y) in path.Points)
            {
                var p = Project(new NumVec2(x, y), ctx.PlayerGrid, center, scale);
                if (prev is { } a)
                    dl.AddLine(a, p, col, 2.2f);
                prev = p;
            }
        }
    }

    private static void DrawPathsWorld(ImDrawListPtr dl, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { Length: >= 16 } m) return;
        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count < 2) continue;
            var col = PathColor(path.ColorSlot);
            NumVec2? prev = null;
            foreach (var (x, y) in path.Points)
            {
                var world = new System.Numerics.Vector3(x * Poe2.WorldToGridRatio, y * Poe2.WorldToGridRatio, 0);
                if (!TryProject(world, m, ctx.WindowWidth, ctx.WindowHeight, out var p)) continue;
                if (prev is { } a)
                    dl.AddLine(a, p, col, 2.4f);
                prev = p;
            }
        }
    }

    private static void DrawNavChip(RenderContext ctx)
    {
        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav;

        ImGui.SetNextWindowBgAlpha(0.72f);
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(8, 8), ImGuiCond.Always);
        if (!ImGui.Begin("POE2RadarNav", flags))
        {
            ImGui.End();
            return;
        }

        var selected = 0;
        foreach (var row in ctx.Legend)
            if (row.IsSelected) selected++;
        ImGui.TextUnformatted(selected > 0 ? $"POE2Radar {selected}/8" : "POE2Radar");

        if (ctx.NavMenuExpanded)
        {
            foreach (var row in ctx.Legend)
            {
                var color = row.IsSelected ? PathColorVec(row.ColorSlot) : new Vector4(0.70f, 0.70f, 0.70f, 0.65f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                ImGui.TextUnformatted(LegendRowText(row));
                ImGui.PopStyleColor();
            }
        }

        if (ctx.ShowPerfStats)
        {
            ImGui.Separator();
            var p = ctx.Perf;
            ImGui.TextUnformatted($"fps {p.Fps:F0} tick {p.TickMs:F1} draw {p.DrawMs:F1} present {p.PresentMs:F1}");
            ImGui.TextUnformatted($"map {p.MapMs:F1} paths {p.PathsMs:F1} menu {p.NavMenuMs:F1}");
        }

        ImGui.End();
    }

    private static string LegendRowText(LegendEntry row)
    {
        var prefix = row.IsSelected ? $"{row.ColorSlot + 1}. " : "   ";
        var type = row.Target.IsEntity ? "E" : "L";
        var dist = row.Distance >= 0 ? $" {row.Distance:F0}c" : "";
        var status = row.Status switch
        {
            NavTargetStatus.Cached when row.Target.IsEntity => " (last seen)",
            NavTargetStatus.NoPath => " (no path)",
            _ => "",
        };
        return $"{prefix}[{type}] {row.Target.Name}{dist}{status}";
    }

    private static NumVec2 Project(NumVec2 cell, NumVec2 player, NumVec2 center, float scale)
    {
        var d = cell - player;
        var md = MapProjection.GridDeltaToMapDelta(new GameVec2 { X = d.X, Y = d.Y }, scale);
        return new NumVec2(center.X + md.X, center.Y + md.Y);
    }

    private static bool TryProject(System.Numerics.Vector3 world, float[] m, float W, float H, out NumVec2 screen)
    {
        screen = default;
        var x = world.X; var y = world.Y; var z = world.Z;
        var clipX = x * m[0] + y * m[4] + z * m[8] + m[12];
        var clipY = x * m[1] + y * m[5] + z * m[9] + m[13];
        var clipW = x * m[3] + y * m[7] + z * m[11] + m[15];
        if (clipW <= 0.001f) return false;
        var ndcX = clipX / clipW;
        var ndcY = clipY / clipW;
        var sx = (ndcX + 1f) * 0.5f * W;
        var sy = (1f - ndcY) * 0.5f * H;
        if (!float.IsFinite(sx) || !float.IsFinite(sy)) return false;
        screen = new NumVec2(sx, sy);
        return true;
    }

    private static uint PathColor(int slot)
    {
        var v = PathColorVec(slot);
        return ImGui.ColorConvertFloat4ToU32(v);
    }

    private static Vector4 PathColorVec(int slot)
    {
        return PathPalette[((slot % PathPalette.Length) + PathPalette.Length) % PathPalette.Length];
    }

    private static string EntityColor(Poe2Live.EntityDot e) => e.Category switch
    {
        Poe2Live.EntityCategory.Monster => e.Rarity switch
        {
            Poe2Live.Rarity.Unique => "#AF6025",
            Poe2Live.Rarity.Rare => "#FFFF77",
            Poe2Live.Rarity.Magic => "#8888FF",
            _ => "#FF4040",
        },
        Poe2Live.EntityCategory.Player => "#4CF2FF",
        Poe2Live.EntityCategory.Npc => "#FFFFFF",
        Poe2Live.EntityCategory.Chest => "#FFCC55",
        Poe2Live.EntityCategory.Transition => "#66FF99",
        _ => "#B0B0B0",
    };

    private static float EntityRadius(Poe2Live.EntityDot e) => e.Category switch
    {
        Poe2Live.EntityCategory.Monster => e.Rarity is Poe2Live.Rarity.Rare or Poe2Live.Rarity.Unique ? 4.4f : 3.2f,
        Poe2Live.EntityCategory.Player => 4.2f,
        Poe2Live.EntityCategory.Npc => 3.8f,
        Poe2Live.EntityCategory.Chest => 3.5f,
        Poe2Live.EntityCategory.Transition => 4.8f,
        _ => 3f,
    };

    private static uint ColorU32(string hex, float opacity)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return ImGui.ColorConvertFloat4ToU32(new Vector4(r / 255f, g / 255f, b / 255f, Math.Clamp(opacity, 0f, 1f)));
        return ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, Math.Clamp(opacity, 0f, 1f)));
    }
}

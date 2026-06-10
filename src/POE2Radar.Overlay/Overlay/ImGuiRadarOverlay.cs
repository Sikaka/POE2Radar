using System.Numerics;
using ImGuiNET;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Diagnostics;
using NumVec2 = System.Numerics.Vector2;
using GameVec2 = POE2Radar.Core.Game.Vector2;

namespace POE2Radar.Overlay;

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
    private readonly Action<Action> _enqueue;
    private readonly Action<string> _toggleTarget;
    private readonly Action<string> _setCorner;

    private bool _navMenuExpanded;
    private string _navMenuCorner = "TopLeft";

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

    public ImGuiRadarOverlay(Action<Action> enqueue, Action<string> toggleTarget, Action<string> setCorner)
        : base("POE2Radar ImGuiDx", true, 3840, 2160)
    {
        _enqueue = enqueue;
        _toggleTarget = toggleTarget;
        _setCorner = setCorner;
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

            if (ctx.AtlasOpen)
                DrawAtlas(dl, ctx);
            else if (ctx.Map.IsVisible)
                DrawMap(dl, ctx);
            else
                DrawPathsWorld(dl, ctx);

            DrawNameplates(dl, ctx);
            DrawPathLabels(dl, ctx);
            DrawNavMenu(ctx);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _renderCrashLogged, 1) == 0)
                CrashLog.Write("ImGuiDx render crashed", ex);
            Close();
        }
    }

    // ── Atlas overlay ──

    private static void DrawAtlas(ImDrawListPtr dl, RenderContext ctx)
    {
        var W = ctx.WindowWidth;
        var H = ctx.WindowHeight;
        float h0 = ctx.AtlasScale, h1 = ctx.AtlasShearX, h2 = ctx.AtlasOffX,
              h3 = ctx.AtlasShearY, h4 = ctx.AtlasScaleY, h5 = ctx.AtlasOffY,
              h6 = ctx.AtlasPersX, h7 = ctx.AtlasPersY;
        float ccx = W * 0.5f, ccy = H * 0.5f;

        // F10 route
        var route = ctx.AtlasRoute;
        if (route is { Count: >= 2 })
        {
            var dark = ColorU32(0, 0, 0, 0.6f);
            var bright = ColorU32(59, 219, 255, 0.95f);
            var pts = new NumVec2[route.Count];
            for (var i = 0; i < route.Count; i++) pts[i] = ProjAtlas(route[i]);
            for (var i = 1; i < pts.Length; i++) dl.AddLine(pts[i - 1], pts[i], dark, 7f);
            for (var i = 1; i < pts.Length; i++) dl.AddLine(pts[i - 1], pts[i], bright, 3.5f);
            for (var i = 1; i < pts.Length - 1; i++) dl.AddCircle(pts[i], 4f, bright, 0, 2f);
        }
        else if (ctx.AtlasStart is { } sa && ctx.AtlasEnd is { } eb)
        {
            var a = ProjAtlas(sa); var b = ProjAtlas(eb);
            dl.AddLine(a, b, ColorU32(0, 0, 0, 0.6f), 6f);
            dl.AddLine(a, b, ColorU32(224, 179, 65, 1f), 2.5f);
        }

        if (ctx.AtlasStart is { } s) { var p = ProjAtlas(s); dl.AddCircleFilled(p, 8f, ColorU32(110, 232, 135, 1f), 12); dl.AddCircleFilled(p, 3f, ColorU32(110, 232, 135, 1f), 8); }
        if (ctx.AtlasEnd is { } e) { var p = ProjAtlas(e); dl.AddCircle(p, 11f, ColorU32(224, 179, 65, 1f), 0, 3f); dl.AddCircle(p, 4f, ColorU32(224, 179, 65, 1f), 0, 2f); }

        // Node rings
        if (ctx.AtlasNodes is { Count: > 0 } marks)
        {
            foreach (var n in marks)
            {
                var w = h6 * n.X + h7 * n.Y + 1f;
                if (MathF.Abs(w) < 1e-6f) continue;
                var sx = (h0 * n.X + h1 * n.Y + h2) / w;
                var sy = (h3 * n.X + h4 * n.Y + h5) / w;
                var onScreen = sx >= 0 && sx <= W && sy >= 0 && sy <= H;
                var col = string.IsNullOrEmpty(n.Color) ? ColorU32(59, 219, 255, 1f) : ColorU32(n.Color, 0.95f);

                if (!onScreen)
                {
                    if (n.Arrow) DrawAtlasArrow(dl, sx, sy, ccx, ccy, W, H, col, n.Label);
                    continue;
                }

                var c = new NumVec2(sx, sy);
                if (n.Selected || n.Arrow)
                {
                    dl.AddCircle(c, 18f, col, 0, 3f);
                    dl.AddCircle(c, 9f, col, 0, 2f);
                }
                else if (n.IconType > 0)
                {
                    dl.AddCircle(c, 7f, ColorU32(255, 230, 51, 0.9f), 0, 2f);
                }
                else if (n.Visited)
                {
                    dl.AddCircle(c, 16f, ColorU32(255, 51, 255, 1f), 0, 3f);
                    dl.AddCircle(c, 8f, ColorU32(255, 51, 255, 1f), 0, 2f);
                }
                else
                {
                    dl.AddCircle(c, 11f, n.HasContent ? ColorU32(255, 158, 66, 0.95f) : ColorU32(110, 232, 135, 0.85f), 0, 2f);
                }

                if (n.Label != null)
                    dl.AddText(new NumVec2(sx + 11f, sy - 9f), ColorU32(255, 255, 255, 0.9f), n.Label);
            }
        }

        NumVec2 ProjAtlas(NumVec2 p) { var pw = h6 * p.X + h7 * p.Y + 1f; if (MathF.Abs(pw) < 1e-6f) pw = 1f; return new NumVec2((h0 * p.X + h1 * p.Y + h2) / pw, (h3 * p.X + h4 * p.Y + h5) / pw); }
    }

    private static void DrawAtlasArrow(ImDrawListPtr dl, float sx, float sy, float cx, float cy, float W, float H, uint col, string? label)
    {
        float dx = sx - cx, dy = sy - cy;
        float len = MathF.Sqrt(dx * dx + dy * dy); if (len < 1f) return;
        float ux = dx / len, uy = dy / len;
        const float margin = 46f;
        float tX = MathF.Abs(ux) > 1e-4f ? (W * 0.5f - margin) / MathF.Abs(ux) : 1e9f;
        float tY = MathF.Abs(uy) > 1e-4f ? (H * 0.5f - margin) / MathF.Abs(uy) : 1e9f;
        float t = MathF.Min(tX, tY);
        float ex = cx + ux * t, ey = cy + uy * t;
        float px = -uy, py = ux;
        var tip = new NumVec2(ex + ux * 11f, ey + uy * 11f);
        var bl = new NumVec2(ex - ux * 9f + px * 10f, ey - uy * 9f + py * 10f);
        var br = new NumVec2(ex - ux * 9f - px * 10f, ey - uy * 9f - py * 10f);
        dl.AddTriangleFilled(tip, bl, br, col);
        if (label != null)
            dl.AddText(new NumVec2(ex - ux * 56f - 95f, ey - uy * 18f - 8f), ColorU32(255, 255, 255, 0.9f), label);
    }

    // ── Map overlay ──

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
                if (p.X < -40 || p.Y < -40 || p.X > W + 40 || p.Y > H + 40) continue;
                var color = rule?.Color ?? EntityColor(e);
                var radius = rule?.Size ?? EntityRadius(e);
                dl.AddCircleFilled(p, radius, ColorU32(color, rule?.Opacity ?? 0.95f), 16);
                if (rule is { Label: { Length: > 0 } lbl })
                    dl.AddText(new NumVec2(p.X + 7, p.Y - 7), ColorU32(color, 0.9f), lbl);
            }
        }

        foreach (var lm in ctx.Landmarks)
        {
            var tr = ctx.ResolveTile?.Invoke(lm.Path);
            if (tr is { Hide: true }) continue;
            var p = Project(lm.Center, ctx.PlayerGrid, center, scale);
            if (p.X < -40 || p.Y < -40 || p.X > W + 40 || p.Y > H + 40) continue;
            var lmColor = tr?.Color ?? "#F259F2";
            var lmSize = tr?.Size ?? 4.5f;
            dl.AddCircleFilled(p, lmSize, ColorU32(lmColor, tr?.Opacity ?? 0.95f), 12);
            var label = tr?.Label is { Length: > 0 } rl ? rl
                      : (ctx.UseCuratedLandmarks && lm.CuratedName is { } c ? c : lm.Name);
            dl.AddText(new NumVec2(p.X + 7, p.Y - 7), ColorU32(lmColor, 0.9f), label);
        }

        if (ctx.ShowPlayerBlip)
            dl.AddCircleFilled(center, 5.5f, ColorU32(77, 242, 255, 1f), 18);
    }

    private static void DrawTerrainEdges(ImDrawListPtr dl, RenderContext ctx, Poe2Live.TerrainData terrain, NumVec2 center, float scale)
    {
        var data = terrain.Walkable;
        var bytesPerRow = terrain.Width;
        if (data.Length == 0 || bytesPerRow <= 0) return;

        var rows = data.Length / bytesPerRow;
        var stride = Math.Max(1, Math.Min(6, (int)MathF.Ceiling(1.5f / MathF.Max(scale, 0.05f))));
        var col = ColorU32(46, 209, 242, 0.55f);
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

    // ── World-space paths (map closed) ──

    private static void DrawPathsWorld(ImDrawListPtr dl, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { Length: >= 16 } m) return;

        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        var z = 0f;
        foreach (var e in ctx.Entities)
            if (e.Category == Poe2Live.EntityCategory.Player) { z = e.World.Z; break; }

        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count == 0) continue;
            var col = PathColor(path.ColorSlot);

            NumVec2? prev = null;
            foreach (var (gx, gy) in path.Points)
            {
                var wx = gx * GridConstants.GridToWorld;
                var wy = gy * GridConstants.GridToWorld;
                var cw = wx * m[3] + wy * m[7] + z * m[11] + m[15];
                if (cw <= 0.0001f) { prev = null; continue; }
                var cx = wx * m[0] + wy * m[4] + z * m[8] + m[12];
                var cy = wx * m[1] + wy * m[5] + z * m[9] + m[13];
                var sx = (cx / cw / 2f + 0.5f) * W;
                var sy = (0.5f - cy / cw / 2f) * H;
                if (!float.IsFinite(sx) || !float.IsFinite(sy)) continue;
                var p = new NumVec2(sx, sy);
                if (prev is { } pr) dl.AddLine(pr, p, col, 2.4f);
                dl.AddCircleFilled(p, 3.5f, col, 8);
                prev = p;
            }
        }
    }

    // ── HP bars (world-space nameplates) ──

    private static void DrawNameplates(ImDrawListPtr dl, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { Length: >= 16 } m) return;
        if (ctx.HpBarTargets is not { Count: > 0 } bars) return;

        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        var bh = ctx.HpBars.Height;

        foreach (var t in bars)
        {
            var w = t.World;
            var cw = w.X * m[3] + w.Y * m[7] + w.Z * m[11] + m[15];
            if (cw <= 0.0001f) continue;
            var cx = w.X * m[0] + w.Y * m[4] + w.Z * m[8] + m[12];
            var cy = w.X * m[1] + w.Y * m[5] + w.Z * m[9] + m[13];
            var sx = (cx / cw / 2f + 0.5f) * W;
            var sy = (0.5f - cy / cw / 2f) * H;
            if (sx < 0 || sx > W || sy < 0 || sy > H) continue;

            var bw = t.Width;
            var bx = sx - bw / 2f + ctx.HpBars.OffsetX;
            var by = sy + ctx.HpBars.OffsetY;

            // Background
            dl.AddRectFilled(new NumVec2(bx, by), new NumVec2(bx + bw, by + bh), ColorU32(13, 13, 13, 0.78f));

            // Fill — red below 30%
            uint fillCol;
            if (t.Frac < 0.3f)
                fillCol = ColorU32(255, 51, 51, 0.95f);
            else
                fillCol = ColorU32(
                    (byte)((t.Fill >> 16) & 0xFF),
                    (byte)((t.Fill >> 8) & 0xFF),
                    (byte)(t.Fill & 0xFF),
                    ((t.Fill >> 24) & 0xFF) / 255f);
            dl.AddRectFilled(new NumVec2(bx, by), new NumVec2(bx + bw * Math.Clamp(t.Frac, 0f, 1f), by + bh), fillCol);

            // Border
            if (t.BorderWidth > 0f)
            {
                uint borderCol = ColorU32(
                    (byte)((t.Border >> 16) & 0xFF),
                    (byte)((t.Border >> 8) & 0xFF),
                    (byte)(t.Border & 0xFF),
                    ((t.Border >> 24) & 0xFF) / 255f);
                dl.AddRect(new NumVec2(bx, by), new NumVec2(bx + bw, by + bh), borderCol, 0, 0, t.BorderWidth);
            }
        }
    }

    // ── Path endpoint labels ──

    private static void DrawPathLabels(ImDrawListPtr dl, RenderContext ctx)
    {
        if (ctx.SelectedPaths.Count == 0 || ctx.Map.IsVisible) return;

        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        if (ctx.CameraMatrix is not { Length: >= 16 } m) return;

        var z = 0f;
        foreach (var e in ctx.Entities)
            if (e.Category == Poe2Live.EntityCategory.Player) { z = e.World.Z; break; }

        var labelAnchors = new List<(NumVec2 anchor, int slot, string text)>();
        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count == 0) continue;
            var end = path.Points[path.Points.Count - 1];
            var wx = end.x * GridConstants.GridToWorld;
            var wy = end.y * GridConstants.GridToWorld;
            var cw = wx * m[3] + wy * m[7] + z * m[11] + m[15];
            if (cw <= 0.0001f) continue;
            var cx = wx * m[0] + wy * m[4] + z * m[8] + m[12];
            var cy = wx * m[1] + wy * m[5] + z * m[9] + m[13];
            var sx = (cx / cw / 2f + 0.5f) * W;
            var sy = (0.5f - cy / cw / 2f) * H;
            if (!float.IsFinite(sx) || !float.IsFinite(sy)) continue;

            var label = string.IsNullOrWhiteSpace(path.Label) ? path.TargetId : path.Label;
            label += path.Status switch
            {
                NavTargetStatus.Cached when path.IsEntity => " (last seen)",
                NavTargetStatus.NoPath => " (no path)",
                _ => "",
            };
            var text = $"{path.ColorSlot + 1}. {label}";
            labelAnchors.Add((new NumVec2(sx, sy), path.ColorSlot, text));
        }

        foreach (var (anchor, slot, text) in labelAnchors)
        {
            var textW = Math.Min(text.Length * 7.2f + 12f, 220f);
            var textH = 18f;
            var left = Math.Clamp(anchor.X + 10f, 4f, W - textW - 4f);
            var top = Math.Clamp(anchor.Y - 9f, 4f, H - textH - 4f);

            dl.AddRectFilled(new NumVec2(left, top), new NumVec2(left + textW, top + textH), ColorU32(13, 13, 13, 0.78f));
            dl.AddRect(new NumVec2(left, top), new NumVec2(left + textW, top + textH), ColorU32(56, 56, 56, 0.22f), 0, 0, 1f);

            // Swatch
            var swatchY = top + (textH - 7f) * 0.5f;
            dl.AddRectFilled(new NumVec2(left + 4f, swatchY), new NumVec2(left + 11f, swatchY + 7f), PathColor(slot));

            dl.AddText(new NumVec2(left + 15f, top + 2f), PathColor(slot), text);
        }
    }

    // ── Nav menu ──

    private void DrawNavMenu(RenderContext ctx)
    {
        _navMenuCorner = ctx.NavMenuCorner;
        var isRight = _navMenuCorner is "TopRight" or "BottomRight";
        var isBottom = _navMenuCorner is "BottomLeft" or "BottomRight";

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoFocusOnAppearing |
            ImGuiWindowFlags.NoNav;

        var cornerPos = isRight
            ? new System.Numerics.Vector2(ctx.WindowWidth - 6, isBottom ? ctx.WindowHeight - 6 : 6)
            : new System.Numerics.Vector2(6, isBottom ? ctx.WindowHeight - 6 : 6);
        var pivot = new System.Numerics.Vector2(isRight ? 1f : 0f, isBottom ? 1f : 0f);

        ImGui.SetNextWindowBgAlpha(0.72f);
        ImGui.SetNextWindowPos(cornerPos, ImGuiCond.Always, pivot);

        if (!ImGui.Begin("POE2RadarNav", flags))
        {
            ImGui.End();
            return;
        }

        var selected = 0;
        foreach (var row in ctx.Legend) if (row.IsSelected) selected++;

        var headerText = selected > 0 ? $"POE2Radar {selected}/8" : "POE2Radar";
        if (ImGui.Button(_navMenuExpanded ? "v " + headerText : "> " + headerText))
            _navMenuExpanded = !_navMenuExpanded;
        ImGui.SameLine();

        foreach (var (label, corner) in new[] { ("[TL]", "TopLeft"), ("[TR]", "TopRight"), ("[BL]", "BottomLeft"), ("[BR]", "BottomRight") })
        {
            var isActive = corner == _navMenuCorner;
            if (isActive) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.ColorConvertFloat4ToU32(new Vector4(0.30f, 0.95f, 1.00f, 1.00f)));
            if (ImGui.SmallButton(label))
            {
                _navMenuCorner = corner;
                _enqueue(() => _setCorner(corner));
            }
            if (isActive) ImGui.PopStyleColor();
            ImGui.SameLine();
        }
        ImGui.Spacing();

        if (_navMenuExpanded)
        {
            foreach (var row in ctx.Legend)
            {
                var color = row.IsSelected ? PathColorVec(row.ColorSlot) : new Vector4(0.70f, 0.70f, 0.70f, 0.65f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);

                var text = LegendRowText(row);
                if (ImGui.Selectable(text, row.IsSelected))
                {
                    var targetId = row.Target.Id;
                    _enqueue(() => _toggleTarget(targetId));
                }

                ImGui.PopStyleColor();
            }
        }

        if (ctx.ShowPerfStats)
        {
            ImGui.Separator();
            var p = ctx.Perf;
            ImGui.TextUnformatted($"fps {p.Fps:F0}  tick {p.TickMs:F1}  world {p.WorldMs:F1}  ent {p.EntitiesMs:F1}");
            ImGui.TextUnformatted($"draw {p.DrawMs:F1}  hp {p.HpBarsMs:F1}  map {p.MapMs:F1}  paths {p.PathsMs:F1}");
            ImGui.TextUnformatted($"names {p.NameplatesMs:F1}  atlas {p.AtlasMs:F1}  menu {p.NavMenuMs:F1}");
            ImGui.TextUnformatted($"reads {p.ReadsPerSec / 1000f:F1}k/s  {p.MibPerSec:F2} MiB/s  fail {p.FailedReadsPerSec:F0}/s");
            ImGui.TextUnformatted($"{p.EntityCount} ent  {p.HpBarCount} bars  {p.SelectedPathCount} sel");
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

    // ── Projection helpers ──

    private static NumVec2 Project(NumVec2 cell, NumVec2 player, NumVec2 center, float scale)
    {
        var d = cell - player;
        var md = MapProjection.GridDeltaToMapDelta(new GameVec2 { X = d.X, Y = d.Y }, scale);
        return new NumVec2(center.X + md.X, center.Y + md.Y);
    }

    // ── Color helpers ──

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

    private static uint ColorU32(byte r, byte g, byte b, float a)
    {
        return ImGui.ColorConvertFloat4ToU32(new Vector4(r / 255f, g / 255f, b / 255f, Math.Clamp(a, 0f, 1f)));
    }
}

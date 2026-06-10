using System.Numerics;
using ImGuiNET;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using POE2Radar.Overlay.Config;
using POE2Radar.Overlay.Diagnostics;
using POE2Radar.Overlay.Web;
using NumVec2 = System.Numerics.Vector2;
using GameVec2 = POE2Radar.Core.Game.Vector2;

namespace POE2Radar.Overlay;

public sealed class ImGuiRadarOverlay : ClickableTransparentOverlay.Overlay
{
    private readonly object _settingsLock;
    private volatile RadarSettings _settings;
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
    private readonly Action _addNearest;
    private readonly Action _clearPaths;
    private readonly TextureRegistry _textures = new();
    private readonly TerrainTextureCache _terrainTextures = new();

    private bool _navMenuExpanded;
    private bool _settingsOpen;
    private string _navMenuCorner = "TopLeft";
    private DisplayRules? _displayRules;
    private HiddenEntities? _hidden;
    private int _rulesUiGeneration = -1;
    private List<DisplayRule> _rulesUiCache = new();
    private string _hidePatternInput = "";

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

    private static readonly string[] ShapeNames = ["Circle", "Diamond", "Triangle", "Square", "Star", "Hexagon", "Pentagon", "Cross", "Plus", "Ring", "Heart", "Shield", "Gem"];

    public ImGuiRadarOverlay(Action<Action> enqueue, Action<string> toggleTarget, Action<string> setCorner,
        Action addNearest, Action clearPaths, RadarSettings settings)
        : base("POE2Radar", true, 3840, 2160)
    {
        _enqueue = enqueue;
        _toggleTarget = toggleTarget;
        _setCorner = setCorner;
        _addNearest = addNearest;
        _clearPaths = clearPaths;
        _settings = settings;
        _settingsLock = new object();
        _navMenuCorner = settings.NavMenuCorner;
        VSync = false;
    }

    public int OverlayWidth => _width;
    public int OverlayHeight => _height;

    public void UpdateContext(RenderContext ctx) => _ctx = ctx;

    public void AttachEntityStores(DisplayRules displayRules, HiddenEntities hidden)
    {
        _displayRules = displayRules;
        _hidden = hidden;
        _rulesUiGeneration = -1;
        _rulesUiCache.Clear();
    }

    public void UpdateSettings(RadarSettings settings)
    {
        lock (_settingsLock) _settings = settings;
    }

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

    public void ToggleSettings() => _settingsOpen = !_settingsOpen;

    protected override void Render()
    {
        try
        {
            if (_closeRequested) { Close(); return; }

            lock (_boundsLock) { Position = _position; Size = _size; }

            var io = ImGui.GetIO();
            io.ConfigFlags |= ImGuiConfigFlags.NoMouseCursorChange;

            var ctx = _ctx;
            var inGame = ctx is not null && ctx.InGame;

            var dl = ImGui.GetBackgroundDrawList();

            if (inGame && ctx!.Active)
            {
                IconAtlas.EnsureInitialized(this);

                if (ctx.AtlasOpen)
                    DrawAtlas(dl, ctx);
                else if (ctx.Map.IsVisible)
                    DrawMap(dl, ctx, ctx.MapFrame);
                else
                {
                    if (ctx.MiniMap.IsVisible)
                        DrawMap(dl, ctx, ctx.MiniMapFrame);
                    DrawPathsWorld(dl, ctx);
                }

                DrawNameplates(dl, ctx);
                DrawPathLabels(dl, ctx);
            }

            if (inGame)
                DrawNavMenu(ctx!);

            if (_settingsOpen)
                DrawSettingsPanel(ctx);
        }
        catch (Exception ex)
        {
            if (Interlocked.Exchange(ref _renderCrashLogged, 1) == 0)
                CrashLog.Write("ImGui render crashed", ex);
            Close();
        }
    }

    // ── Settings HUD (HP/ES/Mana bars inside settings panel) ──

    private static void DrawSettingsHud(RenderContext ctx)
    {
        var dl = ImGui.GetWindowDrawList();
        var cursor = ImGui.GetCursorScreenPos();
        var avail = ImGui.GetContentRegionAvail().X;
        var barH = 16f;
        var gap = 6f;
        var numBars = 3;
        var labelW = 20f;
        var textW = 36f;
        var barW = (avail - gap * (numBars + 1) - numBars * (labelW + textW)) / numBars;

        float x = cursor.X + gap;
        float y = cursor.Y + 2f;

        DrawColoredBar(dl, x, y, barW, barH, labelW, textW, "HP", ctx.HpPct,
            ctx.HpPct > 60f ? ColorU32(46, 204, 113, 0.9f) :
            ctx.HpPct > 30f ? ColorU32(241, 196, 15, 0.9f) : ColorU32(231, 76, 60, 0.9f));

        x += barW + labelW + textW + gap;
        DrawColoredBar(dl, x, y, barW, barH, labelW, textW, "ES", ctx.EsPct,
            ColorU32(52, 152, 219, 0.85f));

        x += barW + labelW + textW + gap;
        DrawColoredBar(dl, x, y, barW, barH, labelW, textW, "MP", ctx.ManaPct,
            ColorU32(52, 152, 219, 0.85f));

        // Reserve space
        ImGui.Dummy(new System.Numerics.Vector2(avail, barH + 4f));

        // Status line
        var flaskColor = ctx.FlaskNote == "armed"
            ? ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.80f, 0.44f, 1f))
            : ImGui.ColorConvertFloat4ToU32(new Vector4(0.95f, 0.77f, 0.06f, 1f));
        ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f), "Flask: ");
        ImGui.SameLine(0, 0);
        ImGui.TextColored(new Vector4(
            flaskColor >> 16 != 0 ? ((flaskColor >> 16) & 0xFF) / 255f : 0,
            ((flaskColor >> 8) & 0xFF) / 255f,
            (flaskColor & 0xFF) / 255f, 1f), ctx.FlaskNote);
        ImGui.SameLine(ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(
            $"Lv {ctx.CharLevel}  {ctx.AreaCode}").X);
        ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), $"Lv {ctx.CharLevel}  {ctx.AreaCode}");
    }

    private static void DrawColoredBar(ImDrawListPtr dl, float x, float y, float w, float h,
        float labelW, float textW, string label, float pct, uint fillColor)
    {
        dl.AddText(new NumVec2(x, y - 1f), ColorU32(180, 180, 180, 0.75f), label);
        float bx = x + labelW + 2f;
        float frac = Math.Clamp(pct / 100f, 0f, 1f);
        uint bgBar = ColorU32(30, 30, 35, 0.75f);
        dl.AddRectFilled(new NumVec2(bx, y + 2f), new NumVec2(bx + w, y + h - 2f), bgBar, 3f);
        if (frac > 0.005f)
            dl.AddRectFilled(new NumVec2(bx, y + 2f), new NumVec2(bx + w * frac, y + h - 2f), fillColor, 3f);
        dl.AddText(new NumVec2(bx + w + 3f, y - 1f), ColorU32(220, 220, 220, 0.8f), pct.ToString("F0") + "%");
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
                if (!onScreen) { if (n.Arrow) DrawAtlasArrow(dl, sx, sy, ccx, ccy, W, H, col, n.Label); continue; }
                var c = new NumVec2(sx, sy);
                if (n.Selected || n.Arrow) { dl.AddCircle(c, 18f, col, 0, 3f); dl.AddCircle(c, 9f, col, 0, 2f); }
                else if (n.IconType > 0) dl.AddCircle(c, 7f, ColorU32(255, 230, 51, 0.9f), 0, 2f);
                else if (n.Visited) { dl.AddCircle(c, 16f, ColorU32(255, 51, 255, 1f), 0, 3f); dl.AddCircle(c, 8f, ColorU32(255, 51, 255, 1f), 0, 2f); }
                else dl.AddCircle(c, 11f, n.HasContent ? ColorU32(255, 158, 66, 0.95f) : ColorU32(110, 232, 135, 0.85f), 0, 2f);
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

    private void DrawMap(ImDrawListPtr dl, RenderContext ctx, MapFrame frame)
    {
        var W = ctx.WindowWidth;
        var H = ctx.WindowHeight;
        var center = frame.Center;
        var scale = MathF.Max(0.01f, frame.Scale);

        var clipped = frame.IsMinimap && frame.Width > 1f && frame.Height > 1f;
        if (clipped)
            dl.PushClipRect(
                frame.Position,
                new NumVec2(frame.Position.X + frame.Width, frame.Position.Y + frame.Height),
                true);

        try
        {
            if (ctx.ShowTerrain && ctx.Terrain is { } terrain)
            {
                if (!DrawTerrainTexture(dl, ctx, terrain, frame, center, scale))
                    DrawTerrainEdges(dl, ctx, terrain, frame, center, scale);
            }

            if (ctx.ShowPath)
                DrawPathsMap(dl, ctx, frame, center, scale);

            if (ctx.ShowMonsters)
            {
                foreach (var e in ctx.Entities)
                {
                    var rule = ctx.Resolve?.Invoke(e);
                    if (rule is { Hide: true }) continue;
                    var p = Project(e.Grid, ctx.PlayerGrid, center, scale, e.TerrainHeight - frame.PlayerTerrainHeight);
                    if (p.X < -40 || p.Y < -40 || p.X > W + 40 || p.Y > H + 40) continue;
                    var color = rule?.Color ?? EntityColor(e);
                    var radius = rule?.Size ?? EntityRadius(e);
                    DrawIconOrShape(dl, p, radius, color, rule?.Opacity ?? 0.95f, rule?.Sprite, rule?.Shape);
                    if (rule is { Label: { Length: > 0 } lbl })
                        dl.AddText(new NumVec2(p.X + 7, p.Y - 7), ColorU32(color, 0.9f), lbl);
                }
            }

            foreach (var lm in ctx.Landmarks)
            {
                var tr = ctx.ResolveTile?.Invoke(lm.Path);
                if (tr is { Hide: true }) continue;
                var p = Project(lm.Center, ctx.PlayerGrid, center, scale, -frame.PlayerTerrainHeight);
                if (p.X < -40 || p.Y < -40 || p.X > W + 40 || p.Y > H + 40) continue;
                var lmColor = tr?.Color ?? "#F259F2";
                var lmSize = tr?.Size ?? 4.5f;
                DrawIconOrShape(dl, p, lmSize, lmColor, tr?.Opacity ?? 0.95f, tr?.Sprite ?? ctx.Styles.Landmark.Sprite, tr?.Shape ?? ctx.Styles.Landmark.Shape);
                var label = tr?.Label is { Length: > 0 } rl ? rl
                          : (ctx.UseCuratedLandmarks && lm.CuratedName is { } c ? c : lm.Name);
                dl.AddText(new NumVec2(p.X + 7, p.Y - 7), ColorU32(lmColor, 0.9f), label);
            }

            if (ctx.ShowPlayerBlip)
                DrawIconOrShape(dl, center, ctx.Styles.Player.Size, ctx.Styles.Player.Color, ctx.Styles.Player.Opacity, ctx.Styles.Player.Sprite, ctx.Styles.Player.Shape);
        }
        finally
        {
            if (clipped)
                dl.PopClipRect();
        }
    }

    private bool DrawTerrainTexture(ImDrawListPtr dl, RenderContext ctx, Poe2Live.TerrainData terrain, MapFrame frame, NumVec2 center, float scale)
    {
        if (!_terrainTextures.TryGet(this, _textures, terrain, ctx.AreaHash, ctx.TerrainStyle, out var tex))
            return false;

        var terrainDeltaZ = -frame.PlayerTerrainHeight;
        var p0 = Project(new NumVec2(0, 0), ctx.PlayerGrid, center, scale, terrainDeltaZ);
        var p1 = Project(new NumVec2(terrain.Width, 0), ctx.PlayerGrid, center, scale, terrainDeltaZ);
        var p2 = Project(new NumVec2(terrain.Width, terrain.Height), ctx.PlayerGrid, center, scale, terrainDeltaZ);
        var p3 = Project(new NumVec2(0, terrain.Height), ctx.PlayerGrid, center, scale, terrainDeltaZ);

        dl.AddImageQuad(
            tex.Id,
            p0, p1, p2, p3,
            new NumVec2(0, 0),
            new NumVec2(1, 0),
            new NumVec2(1, 1),
            new NumVec2(0, 1),
            0xFFFFFFFF);
        return true;
    }

    private static void DrawIconOrShape(
        ImDrawListPtr dl,
        NumVec2 center,
        float size,
        string color,
        float opacity,
        SpriteIconRef? sprite,
        string? shape)
    {
        if (IconAtlas.TryGet(sprite, out var icon) || IconAtlas.TryGet(shape, out icon))
        {
            var half = MathF.Max(1f, size * Math.Clamp(sprite?.Scale ?? 1f, 0.2f, 4f));
            var tint = ColorU32(color, opacity);
            dl.AddImage(
                icon.TextureId,
                new NumVec2(center.X - half, center.Y - half),
                new NumVec2(center.X + half, center.Y + half),
                icon.UV0,
                icon.UV1,
                tint);
            return;
        }

        dl.AddCircleFilled(center, MathF.Max(1f, size), ColorU32(color, opacity), 16);
    }

    private static void DrawTerrainEdges(ImDrawListPtr dl, RenderContext ctx, Poe2Live.TerrainData terrain, MapFrame frame, NumVec2 center, float scale)
    {
        var data = terrain.Walkable;
        var bytesPerRow = terrain.Width;
        if (data.Length == 0 || bytesPerRow <= 0) return;

        var edgeCol = ColorU32(ctx.TerrainStyle.EdgeColor, ctx.TerrainStyle.EdgeOpacity);
        var interiorCol = ColorU32(ctx.TerrainStyle.InteriorColor, ctx.TerrainStyle.InteriorOpacity);
        var rows = data.Length / bytesPerRow;
        var W = ctx.WindowWidth;
        var H = ctx.WindowHeight;

        var edgeStride = Math.Max(1, (int)MathF.Ceiling(0.8f / MathF.Max(scale, 0.15f)));
        if (edgeStride > 3) edgeStride = 3;
        var thickness = Math.Clamp(1.2f * scale, 0.8f, 4f);

        var interiorStride = Math.Max(2, edgeStride * 3);

        for (var y = 1; y < rows - 2; y += edgeStride)
        {
            var row = y * bytesPerRow;
            for (var x = 1; x < bytesPerRow - 2; x += edgeStride)
            {
                var idx = row + x;
                if (idx < 0 || idx >= data.Length || data[idx] == 0) continue;

                var isEdge = data[idx - 1] == 0 || data[idx + 1] == 0
                          || data[idx - bytesPerRow] == 0 || data[idx + bytesPerRow] == 0;

                var p = Project(new NumVec2(x, y), ctx.PlayerGrid, center, scale, -frame.PlayerTerrainHeight);
                if (p.X < -8 || p.Y < -8 || p.X > W + 8 || p.Y > H + 8) continue;

                if (isEdge)
                {
                    if (x + edgeStride < bytesPerRow - 1)
                    {
                        var rightIdx = row + x + edgeStride;
                        if (rightIdx < data.Length && data[rightIdx] != 0)
                        {
                            var pr = Project(new NumVec2(x + edgeStride, y), ctx.PlayerGrid, center, scale, -frame.PlayerTerrainHeight);
                            if (MathF.Abs(pr.X - p.X) < 80f && MathF.Abs(pr.Y - p.Y) < 80f)
                                dl.AddLine(p, pr, edgeCol, thickness);
                        }
                    }

                    if (y + edgeStride < rows - 1)
                    {
                        var bottomIdx = (y + edgeStride) * bytesPerRow + x;
                        if (bottomIdx < data.Length && data[bottomIdx] != 0)
                        {
                            var pb = Project(new NumVec2(x, y + edgeStride), ctx.PlayerGrid, center, scale, -frame.PlayerTerrainHeight);
                            if (MathF.Abs(pb.X - p.X) < 80f && MathF.Abs(pb.Y - p.Y) < 80f)
                                dl.AddLine(p, pb, edgeCol, thickness);
                        }
                    }
                }
                else if (y % interiorStride == 0 && x % interiorStride == 0 && scale > 0.15f
                         && ctx.TerrainStyle.InteriorOpacity > 0.01f)
                {
                    dl.AddCircleFilled(p, Math.Clamp(1.2f * scale, 0.6f, 2.5f), interiorCol, 4);
                }
            }
        }
    }

    private static void DrawPathsMap(ImDrawListPtr dl, RenderContext ctx, MapFrame frame, NumVec2 center, float scale)
    {
        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count < 2) continue;
            var col = PathColor(path.ColorSlot);
            NumVec2? prev = null;
            foreach (var (x, y) in path.Points)
            {
                var p = Project(new NumVec2(x, y), ctx.PlayerGrid, center, scale, -frame.PlayerTerrainHeight);
                if (prev is { } a) dl.AddLine(a, p, col, 2.2f);
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

    private void DrawNameplates(ImDrawListPtr dl, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { Length: >= 16 } m) return;
        if (ctx.HpBarTargets is not { Count: > 0 } bars) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        var bh = ctx.HpBars.Height;
        TextureRegistry.TextureHandle fullTex = default;
        TextureRegistry.TextureHandle hollowTex = default;
        var useFullTex = ctx.HpBars.UseTextures && _textures.TryGetOutputTexture(this, "full_bar.png", out fullTex);
        var useHollowTex = ctx.HpBars.UseTextures && _textures.TryGetOutputTexture(this, "hollow_bar.png", out hollowTex);

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

            dl.AddRectFilled(new NumVec2(bx, by), new NumVec2(bx + bw, by + bh), ColorU32(13, 13, 13, 0.78f));

            uint fillCol;
            if (t.Frac < 0.3f)
                fillCol = ColorU32(255, 51, 51, 0.95f);
            else
                fillCol = ColorU32((byte)((t.Fill >> 16) & 0xFF), (byte)((t.Fill >> 8) & 0xFF), (byte)(t.Fill & 0xFF), ((t.Fill >> 24) & 0xFF) / 255f);

            var hpFrac = Math.Clamp(t.Frac, 0f, 1f);
            if (useFullTex)
                DrawPartialImage(dl, fullTex, bx, by, bw, bh, hpFrac, fillCol);
            else
                dl.AddRectFilled(new NumVec2(bx, by), new NumVec2(bx + bw * hpFrac, by + bh), fillCol);

            var esFrac = Math.Clamp(t.EsFrac, 0f, 1f);
            if (esFrac > 0.005f)
            {
                var esCol = ColorU32(ctx.HpBars.EnergyShieldColor, 0.86f);
                if (useHollowTex)
                    DrawPartialImage(dl, hollowTex, bx, by, bw, bh, esFrac, esCol);
                else
                    dl.AddRect(new NumVec2(bx, by), new NumVec2(bx + bw * esFrac, by + bh), esCol, 0, 0, 1.5f);
            }

            if (t.BorderWidth > 0f)
            {
                uint borderCol = ColorU32((byte)((t.Border >> 16) & 0xFF), (byte)((t.Border >> 8) & 0xFF), (byte)(t.Border & 0xFF), ((t.Border >> 24) & 0xFF) / 255f);
                dl.AddRect(new NumVec2(bx, by), new NumVec2(bx + bw, by + bh), borderCol, 0, 0, t.BorderWidth);
            }
        }
    }

    private static void DrawPartialImage(
        ImDrawListPtr dl,
        TextureRegistry.TextureHandle texture,
        float x,
        float y,
        float width,
        float height,
        float fraction,
        uint tint)
    {
        fraction = Math.Clamp(fraction, 0f, 1f);
        if (fraction <= 0f) return;
        dl.AddImage(
            texture.Id,
            new NumVec2(x, y),
            new NumVec2(x + width * fraction, y + height),
            new NumVec2(0, 0),
            new NumVec2(fraction, 1),
            tint);
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
            labelAnchors.Add((new NumVec2(sx, sy), path.ColorSlot, $"{path.ColorSlot + 1}. {label}"));
        }

        foreach (var (anchor, slot, text) in labelAnchors)
        {
            var textW = Math.Min(text.Length * 7.2f + 12f, 220f);
            var textH = 18f;
            var left = Math.Clamp(anchor.X + 10f, 4f, W - textW - 4f);
            var top = Math.Clamp(anchor.Y - 9f, 4f, H - textH - 4f);
            dl.AddRectFilled(new NumVec2(left, top), new NumVec2(left + textW, top + textH), ColorU32(13, 13, 13, 0.78f));
            dl.AddRect(new NumVec2(left, top), new NumVec2(left + textW, top + textH), ColorU32(56, 56, 56, 0.22f), 0, 0, 1f);
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

        var cornerPos = isRight
            ? new System.Numerics.Vector2(ctx.WindowWidth - 6, isBottom ? ctx.WindowHeight - 6 : 6)
            : new System.Numerics.Vector2(6, isBottom ? ctx.WindowHeight - 6 : 6);
        var pivot = new System.Numerics.Vector2(isRight ? 1f : 0f, isBottom ? 1f : 0f);

        ImGui.SetNextWindowBgAlpha(0.80f);
        ImGui.SetNextWindowPos(cornerPos, ImGuiCond.Always, pivot);

        const ImGuiWindowFlags flags =
            ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoFocusOnAppearing | ImGuiWindowFlags.NoNav;

        if (!ImGui.Begin("Nav", flags)) { ImGui.End(); return; }

        var selected = 0;
        foreach (var row in ctx.Legend) if (row.IsSelected) selected++;

        var headerText = selected > 0 ? $"POE2Radar {selected}/8" : "POE2Radar";
        if (ImGui.Button(_navMenuExpanded ? "v " + headerText : "> " + headerText))
            _navMenuExpanded = !_navMenuExpanded;
        ImGui.SameLine();

        if (ImGui.SmallButton("+"))
            _enqueue(() => _addNearest());
        ImGui.SameLine();

        if (ImGui.SmallButton("-"))
            _enqueue(() => _clearPaths());
        ImGui.SameLine();
        if (ImGui.SmallButton("\u2699"))
            _settingsOpen = !_settingsOpen;

        ImGui.SameLine();
        foreach (var (label, corner) in new[] { ("TL", "TopLeft"), ("TR", "TopRight"), ("BL", "BottomLeft"), ("BR", "BottomRight") })
        {
            var active = corner == _navMenuCorner;
            if (active) ImGui.PushStyleColor(ImGuiCol.Text, ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.95f, 1f, 1f)));
            if (ImGui.SmallButton(label))
            {
                _navMenuCorner = corner;
                _enqueue(() => _setCorner(corner));
            }
            if (active) ImGui.PopStyleColor();
            if (corner != "BottomRight") ImGui.SameLine();
        }

        if (_navMenuExpanded)
        {
            ImGui.Spacing();
            foreach (var row in ctx.Legend)
            {
                var color = row.IsSelected ? PathColorVec(row.ColorSlot) : new Vector4(0.7f, 0.7f, 0.7f, 0.65f);
                ImGui.PushStyleColor(ImGuiCol.Text, color);
                if (ImGui.Selectable(LegendRowText(row), row.IsSelected))
                {
                    var id = row.Target.Id;
                    _enqueue(() => _toggleTarget(id));
                }
                ImGui.PopStyleColor();
            }
        }

        if (ctx.ShowPerfStats)
        {
            ImGui.Separator();
            var p = ctx.Perf;
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), $"fps {p.Fps:F0}  tick {p.TickMs:F1}  ent {p.EntitiesMs:F1}");
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), $"map {p.MapMs:F1}  paths {p.PathsMs:F1}  hp {p.HpBarsMs:F1}");
            ImGui.TextColored(new Vector4(0.55f, 0.55f, 0.55f, 1f), $"reads {p.ReadsPerSec / 1000f:F1}k/s  {p.MibPerSec:F2} MiB/s");
        }

        ImGui.End();
    }

    // ── Settings panel ──

    private void DrawSettingsPanel(RenderContext? ctx)
    {
        if (!_settingsOpen) return;

        var s = _settings;
        float wW = ctx?.WindowWidth ?? _width;
        float wH = ctx?.WindowHeight ?? _height;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 440), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(
            (wW - 560) * 0.5f,
            (wH - 440) * 0.5f), ImGuiCond.FirstUseEver);

        const ImGuiWindowFlags sflags = ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;

        if (!ImGui.Begin("POE2Radar Settings", ref _settingsOpen, sflags)) { ImGui.End(); return; }

        if (ctx is not null)
            DrawSettingsHud(ctx);

        ImGui.Separator();

        if (ImGui.BeginTabBar("SettingsTabs"))
        {
            if (ImGui.BeginTabItem("Radar")) { DrawRadarTab(s); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Entities")) { DrawEntitiesTab(s); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("HP Bars")) { DrawHpBarsTab(s); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Flask")) { DrawFlaskTab(s); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem("Atlas")) { DrawAtlasTab(s); ImGui.EndTabItem(); }
            ImGui.EndTabBar();
        }

        ImGui.End();
    }

    private void DrawEntitiesTab(RadarSettings s)
    {
        if (ImGui.CollapsingHeader("Detection & Auto-path", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int radius = s.EntityDrawRadiusGrid;
            ImGui.SliderInt("Detection radius (grid)", ref radius, 0, 500, radius == 0 ? "Unlimited" : "%d");
            s.EntityDrawRadiusGrid = radius;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip("Max grid distance from player for entity dots, nav targets, and API list. 0 = no limit.");

            bool ap = s.AutoPathNavigable;
            if (ImGui.Checkbox("Auto-path to flagged entities", ref ap))
            {
                s.AutoPathNavigable = ap;
                if (ap) s.ShowPath = true;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip("Continuously path to nearest targets whose display rule has Auto-path enabled (web dashboard or display_rules.json). Manual F6/legend picks are preserved.");
        }

        if (_displayRules is null)
        {
            ImGui.TextDisabled("Display rules not wired yet.");
            return;
        }

        if (ImGui.CollapsingHeader("Display rules", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var gen = _displayRules.Generation;
            if (gen != _rulesUiGeneration)
            {
                _rulesUiGeneration = gen;
                _rulesUiCache = _displayRules.All.ToList();
            }

            ImGui.TextDisabled("Advanced matchers / reorder: web dashboard (F12) or display_rules.json");
            ImGui.BeginChild("EntityRulesList", new NumVec2(0, 180));

            for (var i = 0; i < _rulesUiCache.Count; i++)
            {
                var rule = _rulesUiCache[i];
                ImGui.PushID(i);

                bool en = rule.Enabled;
                if (ImGui.Checkbox("##en", ref en) && en != rule.Enabled)
                {
                    var c = CloneDisplayRule(rule);
                    c.Enabled = en;
                    _displayRules.Update(i, c);
                }
                ImGui.SameLine();
                ImGui.TextUnformatted(rule.Name.Length > 0 ? rule.Name : $"Rule {i}");

                bool hide = rule.Hide;
                ImGui.SameLine();
                if (ImGui.Checkbox("Hide", ref hide) && hide != rule.Hide)
                {
                    var c = CloneDisplayRule(rule);
                    c.Hide = hide;
                    _displayRules.Update(i, c);
                }

                var col = ParseHexColor(rule.Color);
                ImGui.SameLine();
                if (ImGui.ColorEdit3("##col", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoLabel))
                {
                    var c = CloneDisplayRule(rule);
                    c.Color = FormatHexColor3(col);
                    _displayRules.Update(i, c);
                }

                float op = rule.Opacity;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(72f);
                if (ImGui.DragFloat("##op", ref op, 0.01f, 0f, 1f, "α%.2f") && MathF.Abs(op - rule.Opacity) > 0.0001f)
                {
                    var c = CloneDisplayRule(rule);
                    c.Opacity = op;
                    _displayRules.Update(i, c);
                }

                float sz = rule.Size;
                ImGui.SameLine();
                ImGui.SetNextItemWidth(72f);
                if (ImGui.DragFloat("##sz", ref sz, 0.1f, 1f, 24f, "sz%.1f") && MathF.Abs(sz - rule.Size) > 0.0001f)
                {
                    var c = CloneDisplayRule(rule);
                    c.Size = sz;
                    _displayRules.Update(i, c);
                }

                bool nav = rule.Navigable;
                ImGui.SameLine();
                if (ImGui.Checkbox("Auto-path", ref nav) && nav != rule.Navigable)
                {
                    var c = CloneDisplayRule(rule);
                    c.Navigable = nav;
                    _displayRules.Update(i, c);
                }

                ImGui.PopID();
            }

            ImGui.EndChild();
        }

        if (_hidden is null) return;

        if (ImGui.CollapsingHeader("Hidden by metadata", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.SetNextItemWidth(-80f);
            ImGui.InputText("##hidepat", ref _hidePatternInput, 256);
            ImGui.SameLine();
            if (ImGui.Button("Add") && _hidePatternInput.Trim().Length > 0)
            {
                _hidden.Add(_hidePatternInput.Trim());
                _hidePatternInput = "";
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort))
                ImGui.SetTooltip("Substring or glob (* ?) — matched entities never appear on radar or nav list");

            foreach (var p in _hidden.All)
            {
                ImGui.TextUnformatted(p);
                ImGui.SameLine();
                if (ImGui.SmallButton($"Remove##{p}"))
                    _hidden.Remove(p);
            }
        }
    }

    private void DrawRadarTab(RadarSettings s)
    {
        if (ImGui.CollapsingHeader("Display", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool sm = s.ShowMonsters; ImGui.Checkbox("Show Monsters", ref sm); s.ShowMonsters = sm;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Draw entity dots (monsters, NPCs, chests) on the map overlay");

            bool st = s.ShowTerrain; ImGui.Checkbox("Show Terrain", ref st); s.ShowTerrain = st;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Draw walkable terrain boundary edges on the map overlay");

            bool sb = s.ShowPlayerBlip; ImGui.Checkbox("Player Blip", ref sb); s.ShowPlayerBlip = sb;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Show a cyan dot at your position on the map overlay");

            bool sp = s.ShowPath; ImGui.Checkbox("Show Paths", ref sp); s.ShowPath = sp;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Draw guidance route polylines between you and selected targets");

            bool hj = s.HideJunk; ImGui.Checkbox("Hide Junk", ref hj); s.HideJunk = hj;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Filter out common clutter entities (rocks, debris, etc.)");

            bool cl = s.UseCuratedLandmarks; ImGui.Checkbox("Curated Landmarks", ref cl); s.UseCuratedLandmarks = cl;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Use community-curated friendly names for landmarks instead of raw tile paths");

            bool pf = s.ShowPerfStats; ImGui.Checkbox("Perf Stats", ref pf); s.ShowPerfStats = pf;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Show FPS, timing, and memory read counters in the navigation menu");

            bool ao = s.AlwaysShowOverlay; ImGui.Checkbox("Always Show Overlay", ref ao); s.AlwaysShowOverlay = ao;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Keep the overlay visible even when PoE2 is not the foreground window");

            int fps = s.FpsCap; ImGui.SliderInt("FPS Cap", ref fps, 15, 360); s.FpsCap = fps;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Maximum render rate in Hz — higher is smoother but more GPU load");

            int lcg = s.LandmarkClusterGap; ImGui.SliderInt("Cluster Gap", ref lcg, 0, 64); s.LandmarkClusterGap = lcg;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Max tile distance for merging nearby landmarks into one marker (0 = disable clustering)");
        }

        if (ImGui.CollapsingHeader("Map Calibration"))
        {
            float smul = s.ScaleMul; ImGui.SliderFloat("Scale", ref smul, 0.1f, 3f, "%.2f"); s.ScaleMul = smul;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Adjust the map overlay zoom multiplier relative to the game's minimap");

            float ox = s.OffX; ImGui.SliderFloat("Offset X", ref ox, -200f, 200f, "%.0f"); s.OffX = ox;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Shift the entire map overlay horizontally in pixels");

            float oy = s.OffY; ImGui.SliderFloat("Offset Y", ref oy, -200f, 200f, "%.0f"); s.OffY = oy;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Shift the entire map overlay vertically in pixels");
        }

        if (ImGui.CollapsingHeader("Terrain"))
        {
            var ti = s.Terrain.InteriorColor;
            var te = s.Terrain.EdgeColor;
            float tia = s.Terrain.InteriorOpacity;
            float tea = s.Terrain.EdgeOpacity;
            var iv = new Vector4(
                int.TryParse(ti.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var ir) ? ir / 255f : 1f,
                int.TryParse(ti.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var ig) ? ig / 255f : 1f,
                int.TryParse(ti.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var ib) ? ib / 255f : 1f,
                tia);
            if (ImGui.ColorEdit4("Interior", ref iv, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
            {
                s.Terrain.InteriorColor = $"#{(int)(iv.X * 255):X2}{(int)(iv.Y * 255):X2}{(int)(iv.Z * 255):X2}";
                s.Terrain.InteriorOpacity = iv.W;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Color and opacity for the interior of walkable terrain cells");

            var ev = new Vector4(
                int.TryParse(te.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var er) ? er / 255f : 1f,
                int.TryParse(te.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var eg) ? eg / 255f : 1f,
                int.TryParse(te.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var eb) ? eb / 255f : 1f,
                tea);
            if (ImGui.ColorEdit4("Edge", ref ev, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar))
            {
                s.Terrain.EdgeColor = $"#{(int)(ev.X * 255):X2}{(int)(ev.Y * 255):X2}{(int)(ev.Z * 255):X2}";
                s.Terrain.EdgeOpacity = ev.W;
            }
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Color and opacity for walkable terrain boundary edges");
        }

        if (ImGui.CollapsingHeader("Display Rules"))
        {
            ImGui.TextDisabled("Display rules are managed via the web dashboard or display_rules.json");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Open the web dashboard (F12) to edit per-entity display rules — shape, color, size, and match conditions");
        }

        if (ImGui.Button("Save Settings"))
            _enqueue(() => SaveSettings());
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Write all current settings to config/radar_settings.json");
    }

    private void DrawHpBarsTab(RadarSettings s)
    {
        if (ImGui.CollapsingHeader("Rarity Toggles", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool bn = s.HpBarNormal; ImGui.Checkbox("Normal", ref bn); s.HpBarNormal = bn;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Show HP bars on normal (white name) monsters");

            ImGui.SameLine(); bool bm = s.HpBarMagic; ImGui.Checkbox("Magic", ref bm); s.HpBarMagic = bm;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Show HP bars on magic (blue name) monsters");

            ImGui.SameLine(); bool br = s.HpBarRare; ImGui.Checkbox("Rare", ref br); s.HpBarRare = br;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Show HP bars on rare (yellow name) monsters");

            ImGui.SameLine(); bool bu = s.HpBarUnique; ImGui.Checkbox("Unique", ref bu); s.HpBarUnique = bu;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Show HP bars on unique (orange name) bosses and monsters");
        }

        if (ImGui.CollapsingHeader("Bar Geometry", ImGuiTreeNodeFlags.DefaultOpen))
        {
            var hb = s.HpBars;
            float w = hb.WidthNormal; ImGui.SliderFloat("Width Normal", ref w, 30f, 250f); hb.WidthNormal = w;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("HP bar width in pixels for normal monsters");

            w = hb.WidthMagic; ImGui.SliderFloat("Width Magic", ref w, 30f, 250f); hb.WidthMagic = w;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("HP bar width in pixels for magic monsters");

            w = hb.WidthRare; ImGui.SliderFloat("Width Rare", ref w, 30f, 250f); hb.WidthRare = w;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("HP bar width in pixels for rare monsters");

            w = hb.WidthUnique; ImGui.SliderFloat("Width Unique", ref w, 30f, 250f); hb.WidthUnique = w;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("HP bar width in pixels for unique monsters and bosses");

            ImGui.Separator();
            float h = hb.Height; ImGui.SliderFloat("Bar Height", ref h, 2f, 12f); hb.Height = h;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("HP bar height in pixels — applies to all rarities");

            float ox = hb.OffsetX; ImGui.SliderFloat("Offset X", ref ox, -50f, 50f); hb.OffsetX = ox;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Horizontal offset from the monster's world position in pixels");

            float oy = hb.OffsetY; ImGui.SliderFloat("Offset Y", ref oy, -100f, 50f); hb.OffsetY = oy;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Vertical offset from the monster's world position — negative = above, positive = below");
        }
    }

    private void DrawFlaskTab(RadarSettings s)
    {
        if (ImGui.CollapsingHeader("Life Flask", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int mode = s.LifeFlaskMode switch { "EnergyShield" => 1, "Either" => 2, _ => 0 };
            ImGui.Combo("Trigger Pool", ref mode, "Health\0Energy Shield\0Either\0");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Which resource pool triggers the life flask key — Health only, Energy Shield only, or Either");
            s.LifeFlaskMode = mode switch { 1 => "EnergyShield", 2 => "Either", _ => "Health" };

            float lt = s.LifeThresholdPct; ImGui.SliderFloat("Life Threshold %", ref lt, 0f, 100f, "%.0f"); s.LifeThresholdPct = lt;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Use life flask when HP falls below this percentage");

            float et = s.EsThresholdPct; ImGui.SliderFloat("ES Threshold %", ref et, 0f, 100f, "%.0f"); s.EsThresholdPct = et;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Use life flask when energy shield falls below this percentage (only in ES or Either mode)");

            int lc = s.LifeCooldownMs; ImGui.SliderInt("Cooldown ms", ref lc, 200, 10000); s.LifeCooldownMs = lc;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Minimum delay between life flask activations in milliseconds");

            int lk = s.LifeKey; ImGui.InputInt("Key code (hex)", ref lk, 1, 16); if (lk > 0) s.LifeKey = lk;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Win32 virtual-key code for the life flask key — 0x31 = '1', 0x32 = '2', 0x33 = '3'");
        }

        if (ImGui.CollapsingHeader("Mana Flask", ImGuiTreeNodeFlags.DefaultOpen))
        {
            float mt = s.ManaThresholdPct; ImGui.SliderFloat("Mana Threshold %", ref mt, 0f, 100f, "%.0f"); s.ManaThresholdPct = mt;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Use mana flask when mana falls below this percentage");

            int mc = s.ManaCooldownMs; ImGui.SliderInt("Cooldown ms", ref mc, 200, 10000); s.ManaCooldownMs = mc;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Minimum delay between mana flask activations in milliseconds");

            int mk = s.ManaKey; ImGui.InputInt("Key code (hex)", ref mk, 1, 16); if (mk > 0) s.ManaKey = mk;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Win32 virtual-key code for the mana flask key — 0x31 = '1', 0x32 = '2', 0x33 = '3'");
        }

        if (ImGui.CollapsingHeader("Status"))
        {
            ImGui.BulletText("F8 toggles auto-flask on/off. Settings apply immediately.");
            ImGui.BulletText("Keys are Win32 virtual-key codes (0x31 = '1', 0x32 = '2').");
        }
    }

    private void DrawAtlasTab(RadarSettings s)
    {
        if (ImGui.CollapsingHeader("Display", ImGuiTreeNodeFlags.DefaultOpen))
        {
            bool sr = s.AtlasShowRoute; ImGui.Checkbox("Show F10 Route", ref sr); s.AtlasShowRoute = sr;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Draw the F10 point-to-point route through the atlas node connection graph");

            bool da = s.AtlasDrawAll; ImGui.Checkbox("Draw All Nodes (debug)", ref da); s.AtlasDrawAll = da;
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Draw every atlas node regardless of highlight or arrow rules — useful for testing");
        }

        if (ImGui.CollapsingHeader("Highlight Tags"))
        {
            ImGui.BulletText("Configure highlight and arrow tags via the web dashboard Atlas tab.");
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.DelayShort)) ImGui.SetTooltip("Open the web dashboard (F12) and go to the Atlas tab to set content tags to highlight with rings or off-screen arrows");
            if (s.AtlasHighlightTags is { Count: > 0 } tags)
            {
                ImGui.Text("Tracked tags:");
                foreach (var t in tags)
                {
                    var color = s.AtlasHighlightColors.TryGetValue(t, out var c) ? c : "#58A6FF";
                    ImGui.BulletText($"{t}  [{color}]");
                }
            }
        }
    }

    private void SaveSettings()
    {
        lock (_settingsLock) _settings.Save();
    }

    // ── Helpers ──

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

    private static NumVec2 Project(NumVec2 cell, NumVec2 player, NumVec2 center, float scale, float deltaWorldZ = 0f)
    {
        var d = cell - player;
        var md = MapProjection.GridDeltaToMapDelta(new GameVec2 { X = d.X, Y = d.Y }, scale, deltaWorldZ);
        return new NumVec2(center.X + md.X, center.Y + md.Y);
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

    private static uint ColorU32(byte r, byte g, byte b, float a)
    {
        return ImGui.ColorConvertFloat4ToU32(new Vector4(r / 255f, g / 255f, b / 255f, Math.Clamp(a, 0f, 1f)));
    }

    private static DisplayRule CloneDisplayRule(DisplayRule r) => new()
    {
        Enabled = r.Enabled,
        Name = r.Name,
        Categories = new List<string>(r.Categories),
        Match = new List<string>(r.Match),
        Rarity = r.Rarity,
        Reaction = r.Reaction,
        Life = r.Life,
        Chest = r.Chest,
        Poi = r.Poi,
        Encounter = r.Encounter,
        Hide = r.Hide,
        Shape = r.Shape,
        Color = r.Color,
        Opacity = r.Opacity,
        Size = r.Size,
        Sprite = r.Sprite?.Clone(),
        Label = r.Label,
        Navigable = r.Navigable,
    };

    private static System.Numerics.Vector3 ParseHexColor(string hex)
    {
        if (hex.Length == 7 && hex[0] == '#'
            && byte.TryParse(hex.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            && byte.TryParse(hex.AsSpan(3, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            && byte.TryParse(hex.AsSpan(5, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
            return new System.Numerics.Vector3(r / 255f, g / 255f, b / 255f);
        return new System.Numerics.Vector3(1f, 1f, 1f);
    }

    private static string FormatHexColor3(System.Numerics.Vector3 v)
        => $"#{(int)(v.X * 255):X2}{(int)(v.Y * 255):X2}{(int)(v.Z * 255):X2}";
}

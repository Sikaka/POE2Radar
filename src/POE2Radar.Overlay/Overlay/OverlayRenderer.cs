using System.Numerics;
using POE2Radar.Core.Game;
using POE2Radar.Core.Pathfinding;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using NumVec2 = System.Numerics.Vector2;
using GameVec2 = POE2Radar.Core.Game.Vector2;

namespace POE2Radar.Overlay;

/// <summary>
/// PoE2 radar overlay. When the large map is open, draws the walkable-terrain bitmap, entity
/// dots (enemies, NPCs, etc.), and the player blip, projected player-centered onto the map with
/// the same isometric math the PoE Radar plugin uses. Projection scale/offset are calibratable
/// at runtime (see <see cref="RadarApp"/>).
/// </summary>
public sealed class OverlayRenderer : IDisposable
{
    private static readonly Color4 ColPlayer  = new(0.30f, 0.95f, 1.00f, 1.00f);
    private static readonly Color4 ColMonster = new(1.00f, 0.20f, 0.20f, 0.95f); // Normal
    private static readonly Color4 ColMagic   = new(0.45f, 0.65f, 1.00f, 0.97f); // Magic (blue)
    private static readonly Color4 ColRare    = new(1.00f, 0.85f, 0.15f, 1.00f); // Rare (gold)
    private static readonly Color4 ColUnique  = new(1.00f, 0.45f, 0.00f, 1.00f); // Unique (orange)
    private static readonly Color4 ColNpc     = new(1.00f, 0.85f, 0.20f, 0.95f);
    private static readonly Color4 ColChest   = new(0.95f, 0.55f, 0.10f, 0.95f);
    private static readonly Color4 ColTrans   = new(0.40f, 1.00f, 0.60f, 0.95f);
    private static readonly Color4 ColObject  = new(0.55f, 0.75f, 1.00f, 0.70f);
    private static readonly Color4 ColOther   = new(0.70f, 0.70f, 0.70f, 0.60f);
    private static readonly Color4 ColText    = new(1f, 1f, 1f, 1f);
    private static readonly Color4 ColPanel   = new(0.05f, 0.05f, 0.05f, 0.78f);
    private static readonly Color4 ColLandmark = new(0.95f, 0.35f, 0.95f, 1f); // magenta — static tile landmarks
    private static readonly Color4 ColTargetMark = new(1f, 1f, 1f, 1f);          // active-target highlight in the legend

    // Distinct, evenly-spread hues for per-landmark guidance paths / legend swatches.
    private static readonly Color4[] PathPalette =
    {
        new(0.20f, 0.90f, 0.40f, 1f), // green
        new(1.00f, 0.55f, 0.10f, 1f), // orange
        new(0.30f, 0.70f, 1.00f, 1f), // sky blue
        new(1.00f, 0.30f, 0.70f, 1f), // pink
        new(0.95f, 0.90f, 0.20f, 1f), // yellow
        new(0.60f, 0.40f, 1.00f, 1f), // violet
        new(0.20f, 1.00f, 0.85f, 1f), // teal
        new(1.00f, 0.40f, 0.40f, 1f), // salmon
    };

    /// <summary>The color a guidance path (and its legend swatch) draws in, by selection-order color slot.</summary>
    private static Color4 PathColor(int slot) => PathPalette[((slot % PathPalette.Length) + PathPalette.Length) % PathPalette.Length];

    /// <summary>
    /// Screen rectangles of the interactive navigation-menu widget, rebuilt every frame the widget
    /// draws (and cleared when the overlay isn't Active/InGame). RadarApp hit-tests pointer clicks
    /// against these to toggle the menu, pin a corner, or toggle a path target. Each entry pairs a
    /// rect with an Action string: <c>"menu-toggle"</c>, <c>"corner:TopLeft|TopRight|BottomLeft|
    /// BottomRight"</c>, or <c>"target:&lt;navTargetId&gt;"</c> (dropdown rows, only when expanded).
    /// </summary>
    public IReadOnlyList<(Vortice.RawRectF Rect, string Action)> LegendRowRects => _legendRowRects;
    private readonly List<(Vortice.RawRectF Rect, string Action)> _legendRowRects = new();

    private readonly OverlayWindow _window;
    private TerrainBitmap? _terrain;

    private enum Icon { Circle, Triangle, Star, Diamond, Plus, Square }
    private ID2D1PathGeometry? _geoTriangle, _geoStar, _geoDiamond, _geoPlus;

    private ID2D1SolidColorBrush? _bPlayer, _bMonster, _bNpc, _bChest, _bTrans, _bObject, _bOther, _bText, _bPanel, _bLandmark;
    private ID2D1SolidColorBrush? _bMagic, _bRare, _bUnique;
    private ID2D1SolidColorBrush? _bPath; // recolored per landmark via SetColor
    private IDWriteTextFormat? _tf;
    private bool _ready;

    public OverlayRenderer(OverlayWindow window) { _window = window; }

    private void EnsureResources()
    {
        if (_ready) return;
        var rt = _window.RenderTarget;
        _bPlayer  = rt.CreateSolidColorBrush(ColPlayer);
        _bMonster = rt.CreateSolidColorBrush(ColMonster);
        _bNpc     = rt.CreateSolidColorBrush(ColNpc);
        _bChest   = rt.CreateSolidColorBrush(ColChest);
        _bTrans   = rt.CreateSolidColorBrush(ColTrans);
        _bObject  = rt.CreateSolidColorBrush(ColObject);
        _bOther   = rt.CreateSolidColorBrush(ColOther);
        _bText    = rt.CreateSolidColorBrush(ColText);
        _bPanel   = rt.CreateSolidColorBrush(ColPanel);
        _bLandmark = rt.CreateSolidColorBrush(ColLandmark);
        _bMagic   = rt.CreateSolidColorBrush(ColMagic);
        _bRare    = rt.CreateSolidColorBrush(ColRare);
        _bUnique  = rt.CreateSolidColorBrush(ColUnique);
        _bPath    = rt.CreateSolidColorBrush(PathPalette[0]);
        _tf = _window.DWriteFactory.CreateTextFormat("Consolas", null, FontWeight.Normal, FontStyle.Normal, FontStretch.Normal, 12f, "en-us");
        _ready = true;
    }

    public void Render(RenderContext ctx)
    {
        if (!_window.IsValid) return;
        EnsureResources();
        var rt = _window.RenderTarget;
        rt.BeginDraw();
        rt.Clear(new Color4(0f, 0f, 0f, 0f));
        rt.TextAntialiasMode = Vortice.Direct2D1.TextAntialiasMode.Grayscale;
        try
        {
            // Draw nothing unless PoE2 is the foreground window — so the overlay never shows
            // over other apps when you alt-tab. (The cleared frame above hides prior content.)
            if (ctx.Active && ctx.InGame)
            {
                DrawNameplates(rt, ctx);                   // world-space HP bars over hostile mobs
                if (ctx.Map.IsVisible)
                    DrawMap(rt, ctx);                      // terrain + dots + on-map path polylines
                else
                    DrawPathsWorld(rt, ctx);               // ground waypoints + lines when the map is closed

                // The navigation-menu widget is ALWAYS interactive in-game (map open or not). It
                // (re)builds _legendRowRects, so it must run last; nothing else touches those rects now.
                DrawNavMenu(rt, ctx);
            }
            else
            {
                _legendRowRects.Clear(); // not active/in-game: no stale click rects
            }
        }
        finally { rt.EndDraw(); }
        _window.Present();
    }

    /// <summary>
    /// World-space HP bars over Magic/Rare/Unique monsters, projected via the camera WorldToScreen
    /// matrix. Drawn whether or not the big map is open (it's a heads-up combat overlay).
    /// </summary>
    private void DrawNameplates(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;
        foreach (var e in ctx.Entities)
        {
            if (e.Category != Poe2Live.EntityCategory.Monster || !e.IsAlive || e.HpMax <= 0) continue;
            if (e.IsFriendly) continue; // hostile monsters only — no bars over allied/minion mobs
            // Per-rarity HP-bar toggle (NonMonster never shows one).
            var showBar = e.Rarity switch
            {
                Poe2Live.Rarity.Normal => ctx.HpBarNormal,
                Poe2Live.Rarity.Magic  => ctx.HpBarMagic,
                Poe2Live.Rarity.Rare   => ctx.HpBarRare,
                Poe2Live.Rarity.Unique => ctx.HpBarUnique,
                _                      => false,
            };
            if (!showBar) continue;

            var w = e.World;
            var cw = w.X*m[3] + w.Y*m[7] + w.Z*m[11] + m[15];
            if (cw <= 0.0001f) continue;
            var cx = w.X*m[0] + w.Y*m[4] + w.Z*m[8] + m[12];
            var cy = w.X*m[1] + w.Y*m[5] + w.Z*m[9] + m[13];
            var sx = (cx/cw/2f + 0.5f) * W;
            var sy = (0.5f - cy/cw/2f) * H;
            if (sx < 0 || sx > W || sy < 0 || sy > H) continue;

            var (col, bw) = e.Rarity switch
            {
                Poe2Live.Rarity.Unique => (_bUnique!, 64f),
                Poe2Live.Rarity.Rare   => (_bRare!, 50f),
                Poe2Live.Rarity.Magic  => (_bMagic!, 38f),
                _                      => (_bMonster!, 30f), // Normal
            };
            const float bh = 5f;
            var bx = sx - bw / 2f;
            var by = sy - 30f; // sit above the mob
            var frac = e.HpFraction;
            rt.FillRectangle(new Vortice.RawRectF(bx, by, bx + bw, by + bh), _bPanel!);
            var fill = frac < 0.3f ? _bMonster! : col;
            rt.FillRectangle(new Vortice.RawRectF(bx, by, bx + bw * frac, by + bh), fill);
            rt.DrawRectangle(new Vortice.RawRectF(bx, by, bx + bw, by + bh), col, 1f);
        }
    }

    /// <summary>
    /// Draw-only guidance routes rendered on the WORLD GROUND, shown when the big map is CLOSED.
    /// Each selected target's smoothed grid waypoints are converted to world space
    /// (grid × <see cref="GridConstants.GridToWorld"/>, at the player's world-Z plane) and projected
    /// via the camera WorldToScreen matrix — the same projection used for nameplates. Lines connect
    /// consecutive waypoints; a marker dot sits on each. Z is approximated by the player's height, so
    /// the line sits at the player's feet plane (it can float/sink on steep slopes — height TBD).
    /// </summary>
    private void DrawPathsWorld(ID2D1RenderTarget rt, RenderContext ctx)
    {
        if (ctx.CameraMatrix is not { } m || ctx.SelectedPaths.Count == 0) return;
        float W = ctx.WindowWidth, H = ctx.WindowHeight;

        // Ground plane height = the player entity's world Z (paths sit at the player's feet).
        var z = 0f;
        foreach (var e in ctx.Entities)
            if (e.Category == Poe2Live.EntityCategory.Player) { z = e.World.Z; break; }

        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count == 0) continue;
            _bPath!.Color = PathColor(path.ColorSlot);

            NumVec2? prev = null;
            foreach (var (gx, gy) in path.Points)
            {
                float wx = gx * GridConstants.GridToWorld, wy = gy * GridConstants.GridToWorld;
                var cw = wx * m[3] + wy * m[7] + z * m[11] + m[15];
                if (cw <= 0.0001f) { prev = null; continue; } // waypoint behind camera — break the line
                var cxp = wx * m[0] + wy * m[4] + z * m[8] + m[12];
                var cyp = wx * m[1] + wy * m[5] + z * m[9] + m[13];
                var p = new NumVec2((cxp / cw / 2f + 0.5f) * W, (0.5f - cyp / cw / 2f) * H);
                if (prev is { } pr) rt.DrawLine(pr, p, _bPath, 3f);
                rt.FillEllipse(new Ellipse(p, 4f, 4f), _bPath);
                prev = p;
            }
        }
    }

    private void EnsureShapeGeometries()
    {
        if (_geoTriangle is not null) return;
        var factory = (ID2D1Factory)_window.RenderTarget.Factory;

        _geoTriangle = factory.CreatePathGeometry();
        using (var s = _geoTriangle.Open())
        {
            s.BeginFigure(new NumVec2(0f, -1f), FigureBegin.Filled);
            s.AddLine(new NumVec2(0.866f, 0.5f)); s.AddLine(new NumVec2(-0.866f, 0.5f));
            s.EndFigure(FigureEnd.Closed); s.Close();
        }
        _geoDiamond = factory.CreatePathGeometry();
        using (var s = _geoDiamond.Open())
        {
            s.BeginFigure(new NumVec2(0f, -1f), FigureBegin.Filled);
            s.AddLine(new NumVec2(1f, 0f)); s.AddLine(new NumVec2(0f, 1f)); s.AddLine(new NumVec2(-1f, 0f));
            s.EndFigure(FigureEnd.Closed); s.Close();
        }
        _geoStar = factory.CreatePathGeometry();
        using (var s = _geoStar.Open())
        {
            const float inner = 0.42f; var first = true;
            for (var i = 0; i < 10; i++)
            {
                var a = -MathF.PI / 2f + i * MathF.PI / 5f;
                var rr = (i & 1) == 0 ? 1f : inner;
                var pt = new NumVec2(MathF.Cos(a) * rr, MathF.Sin(a) * rr);
                if (first) { s.BeginFigure(pt, FigureBegin.Filled); first = false; } else s.AddLine(pt);
            }
            s.EndFigure(FigureEnd.Closed); s.Close();
        }
        _geoPlus = factory.CreatePathGeometry();
        using (var s = _geoPlus.Open())
        {
            const float a = 0.36f;
            var pts = new[] {
                new NumVec2(-a,-1f), new NumVec2(a,-1f), new NumVec2(a,-a), new NumVec2(1f,-a),
                new NumVec2(1f,a), new NumVec2(a,a), new NumVec2(a,1f), new NumVec2(-a,1f),
                new NumVec2(-a,a), new NumVec2(-1f,a), new NumVec2(-1f,-a), new NumVec2(-a,-a) };
            s.BeginFigure(pts[0], FigureBegin.Filled);
            for (var i = 1; i < pts.Length; i++) s.AddLine(pts[i]);
            s.EndFigure(FigureEnd.Closed); s.Close();
        }
    }

    /// <summary>Draw a categorical icon at screen point p with radius r. Circle/Square use D2D
    /// primitives; the rest stamp a cached unit geometry via a per-call scale+translate transform.</summary>
    private void DrawIcon(ID2D1RenderTarget rt, Icon icon, NumVec2 p, float r, ID2D1SolidColorBrush brush, bool filled)
    {
        if (icon == Icon.Circle) { if (filled) rt.FillEllipse(new Ellipse(p, r, r), brush); else rt.DrawEllipse(new Ellipse(p, r, r), brush, 1.2f); return; }
        if (icon == Icon.Square)
        {
            var h = r * 0.9f; var rect = new Vortice.RawRectF(p.X - h, p.Y - h, p.X + h, p.Y + h);
            if (filled) rt.FillRectangle(rect, brush); else rt.DrawRectangle(rect, brush, 1.5f); return;
        }
        EnsureShapeGeometries();
        var geo = icon switch { Icon.Triangle => _geoTriangle, Icon.Star => _geoStar, Icon.Diamond => _geoDiamond, Icon.Plus => _geoPlus, _ => null };
        if (geo is null) return;
        var prev = rt.Transform;
        rt.Transform = new Matrix3x2(r, 0f, 0f, r, p.X, p.Y);
        if (filled) rt.FillGeometry(geo, brush); else rt.DrawGeometry(geo, brush, 1.5f / r);
        rt.Transform = prev;
    }

    private void DrawMap(ID2D1RenderTarget rt, RenderContext ctx)
    {
        // MapCenter = window center + DefaultShift(0,-20) + Shift + manual offset.
        var center = new NumVec2(
            ctx.WindowWidth  * 0.5f + ctx.Map.ShiftX + ctx.OffsetX,
            ctx.WindowHeight * 0.5f + ctx.Map.ShiftY - 20f + ctx.OffsetY);
        var scale = ctx.Map.Zoom * (ctx.WindowHeight / 677f) * ctx.ScaleMul;
        var player = ctx.PlayerGrid;

        // Terrain bitmap, projected via the same affine grid→screen transform.
        if (ctx.ShowTerrain && ctx.Terrain is { } t)
        {
            _terrain ??= new TerrainBitmap(rt);
            _terrain.EnsureBuiltRaw(t.Walkable, t.Width, t.Height, ctx.AreaHash, inTransition: false);
            if (_terrain.Bitmap is { } bmp)
            {
                var p00 = Project(new NumVec2(0, 0), player, center, scale);
                var p10 = Project(new NumVec2(t.Width, 0), player, center, scale);
                var p01 = Project(new NumVec2(0, t.Height), player, center, scale);
                var ex = (p10 - p00) / t.Width;
                var ey = (p01 - p00) / t.Height;
                var prev = rt.Transform;
                rt.Transform = new Matrix3x2(ex.X, ex.Y, ey.X, ey.Y, p00.X, p00.Y);
                rt.DrawBitmap(bmp, 1f, BitmapInterpolationMode.Linear, new Rect(0, 0, t.Width, t.Height));
                rt.Transform = prev;
            }
        }

        // Entity dots. Props (Object/Other) and dead monsters are filtered out — they're the
        // clutter; the API still serves them for troubleshooting. Game-flagged POIs (entities
        // with a MinimapIcon component) always draw with a white ring, even if their category
        // would otherwise be filtered (waypoints, checkpoints, shrines, …).
        foreach (var e in ctx.Entities)
        {
            if (ctx.HideJunk && JunkFilter.IsJunk(e.Metadata)) continue;
            ID2D1SolidColorBrush brush; float r; Icon icon;
            switch (e.Category)
            {
                case Poe2Live.EntityCategory.Monster:
                    if (!ctx.ShowMonsters) continue;     // monster dots toggle
                    if (!e.IsAlive) continue;            // skip corpses
                    (brush, r, icon) = e.Rarity switch    // distinct shape + color by rarity
                    {
                        Poe2Live.Rarity.Unique => (_bUnique!, 6.5f, Icon.Star),
                        Poe2Live.Rarity.Rare   => (_bRare!, 5.5f, Icon.Triangle),
                        Poe2Live.Rarity.Magic  => (_bMagic!, 3.4f, Icon.Diamond),
                        _                      => (_bMonster!, 2.6f, Icon.Circle),
                    };
                    break;
                case Poe2Live.EntityCategory.Player:     (brush, r, icon) = (_bPlayer!, 3.0f, Icon.Circle); break;
                case Poe2Live.EntityCategory.Npc:        (brush, r, icon) = (_bNpc!, 4.0f, Icon.Plus); break;
                case Poe2Live.EntityCategory.Chest:
                    if (e.Opened) continue;                                   // skip used chests
                    if (e.Rarity is not (Poe2Live.Rarity.Rare or Poe2Live.Rarity.Unique)) continue; // rare+ only
                    (brush, r, icon) = (e.Rarity == Poe2Live.Rarity.Unique ? _bUnique! : _bRare!, 5.0f, Icon.Square);
                    break;
                case Poe2Live.EntityCategory.Transition: (brush, r, icon) = (_bTrans!, 4.5f, Icon.Diamond); break;
                default:
                    if (!e.Poi) continue;                // Object/Other → skip unless a POI
                    (brush, r, icon) = (_bObject!, 3.0f, Icon.Circle); break;
            }
            var p = Project(new NumVec2(e.Grid.X, e.Grid.Y), player, center, scale);
            DrawIcon(rt, icon, p, r, brush, filled: true);
        }

        // Static tile landmarks (boss arena, treasure, …) — diamond + label at the group centroid.
        foreach (var lm in ctx.Landmarks)
        {
            var p = Project(new NumVec2(lm.Center.X, lm.Center.Y), player, center, scale);
            var d = 5f;
            var diamond = new[] { new NumVec2(p.X, p.Y - d), new NumVec2(p.X + d, p.Y), new NumVec2(p.X, p.Y + d), new NumVec2(p.X - d, p.Y) };
            for (var i = 0; i < 4; i++) rt.DrawLine(diamond[i], diamond[(i + 1) % 4], _bLandmark!, 1.6f);
            // Prefer the curated friendly label when enabled and present; else the derived name.
            var label = ctx.UseCuratedLandmarks && lm.CuratedName is { } c ? c : lm.Name;
            rt.DrawText(label, _tf!, new Rect(p.X + 7, p.Y - 7, p.X + 240, p.Y + 9), _bLandmark!, DrawTextOptions.Clip);
        }

        // Draw-only guidance routes: one full smoothed A* polyline per selected landmark, each in its
        // own legend color (precomputed by RadarApp). Drawn whenever a target is selected (selecting =
        // intent to navigate); not gated on ShowPath.
        DrawPaths(rt, ctx, player, center, scale);

        // Player blip on top.
        rt.FillEllipse(new Ellipse(center, 5f, 5f), _bPlayer!);
    }

    /// <summary>
    /// Draw-only guidance routes. Each selected landmark gets its own smoothed walkable A* polyline
    /// (grid → screen), drawn in that landmark's legend color (<see cref="PathColor"/>). Routes are
    /// precomputed per-target by RadarApp; here we just project and stroke them.
    /// </summary>
    private void DrawPaths(ID2D1RenderTarget rt, RenderContext ctx, NumVec2 player, NumVec2 center, float scale)
    {
        foreach (var path in ctx.SelectedPaths)
        {
            if (path.Points.Count < 2) continue;
            _bPath!.Color = PathColor(path.ColorSlot);
            NumVec2? prev = null;
            foreach (var (gx, gy) in path.Points)
            {
                var p = Project(new NumVec2(gx, gy), player, center, scale);
                if (prev is { } pr) rt.DrawLine(pr, p, _bPath, 2.4f);
                prev = p;
            }
        }
    }

    // ── Navigation-menu widget geometry (all in client/device pixels at 96 DPI). ──
    private const float NavPad = 6f, NavRowH = 18f, NavHeaderH = 22f, NavSwatch = 10f, NavPanelW = 230f;
    private const float NavMargin = 6f;                 // gap from the screen edge when pinned
    // ASCII corner buttons (Consolas lacks reliable ↖↗↙↘ glyphs, so we use these per spec).
    private static readonly (string Label, string Corner)[] NavCorners =
    {
        ("[TL]", "TopLeft"), ("[TR]", "TopRight"), ("[BL]", "BottomLeft"), ("[BR]", "BottomRight"),
    };

    /// <summary>
    /// The collapsible, corner-pinnable "POE2Radar" navigation menu. Always drawn (map open or not)
    /// while the overlay is Active + InGame; replaces the old status line AND the bottom-left legend.
    ///
    /// <para>COLLAPSED: a "POE2Radar" chip plus four corner buttons (<c>[TL] [TR] [BL] [BR]</c>).
    /// EXPANDED: the navigation targets (ctx.Legend) under the chip — a colored swatch + curated name
    /// per row; selected rows show a filled swatch in their route color + a highlight, unselected rows
    /// a dim outline swatch. No hotkey-hint line.</para>
    ///
    /// <para>Pinned via <see cref="RenderContext.NavMenuCorner"/>. The panel is anchored at the chosen
    /// corner and CLAMPED so the whole expanded dropdown stays on-screen: <c>*Right</c> right-aligns,
    /// <c>Bottom*</c> grows upward (chip pinned to the bottom edge, rows stacked above it). Every
    /// clickable rect is recorded into <see cref="LegendRowRects"/> with its Action string.</para>
    /// </summary>
    private void DrawNavMenu(ID2D1RenderTarget rt, RenderContext ctx)
    {
        _legendRowRects.Clear();

        var expanded = ctx.NavMenuExpanded;
        var rowCount = expanded ? ctx.Legend.Count : 0;
        var panelW   = NavPanelW;
        var panelH   = NavHeaderH + rowCount * NavRowH + NavPad * 2;

        // Anchor at the chosen corner, then clamp so the whole panel (incl. dropdown) is on-screen.
        var corner   = ctx.NavMenuCorner;
        var isRight  = corner is "TopRight" or "BottomRight";
        var isBottom = corner is "BottomLeft" or "BottomRight";

        var left = isRight ? ctx.WindowWidth - NavMargin - panelW : NavMargin;
        var top  = isBottom ? ctx.WindowHeight - NavMargin - panelH : NavMargin;
        // Clamp into the window in case it's narrower/shorter than the panel.
        left = Math.Clamp(left, NavMargin, Math.Max(NavMargin, ctx.WindowWidth  - NavMargin - panelW));
        top  = Math.Clamp(top,  NavMargin, Math.Max(NavMargin, ctx.WindowHeight - NavMargin - panelH));

        rt.FillRectangle(new Vortice.RawRectF(left, top, left + panelW, top + panelH), _bPanel!);

        // Header row: when pinned to a Bottom corner the dropdown grows UPWARD, so the chip sits at
        // the BOTTOM of the panel and rows stack above it; otherwise the chip is at the top.
        var headerY = isBottom && expanded ? top + panelH - NavPad - NavHeaderH : top + NavPad;

        // "POE2Radar" chip (click → toggle dropdown). Sized to its text so the corner buttons sit after it.
        const string chip = "POE2Radar";
        var chipW = chip.Length * 7.3f + 8f;
        var chipRect = new Vortice.RawRectF(left + NavPad, headerY, left + NavPad + chipW, headerY + NavHeaderH - 2f);
        rt.FillRectangle(chipRect, _bPanel!);
        rt.DrawRectangle(chipRect, _bPlayer!, 1f);
        rt.DrawText((expanded ? "v " : "> ") + chip, _tf!,
            new Rect(chipRect.Left + 4f, headerY + 2f, chipRect.Right, headerY + NavHeaderH), _bText!, DrawTextOptions.Clip);
        _legendRowRects.Add((chipRect, "menu-toggle"));

        // Four corner buttons after the chip. The one matching the current corner is highlighted.
        var bx = chipRect.Right + 6f;
        foreach (var (label, c) in NavCorners)
        {
            var bw = label.Length * 7.3f + 4f;
            var bRect = new Vortice.RawRectF(bx, headerY, bx + bw, headerY + NavHeaderH - 2f);
            var sel = c == corner;
            rt.DrawText(label, _tf!, new Rect(bRect.Left, headerY + 2f, bRect.Right + 6f, headerY + NavHeaderH),
                sel ? _bPlayer! : _bOther!, DrawTextOptions.Clip);
            _legendRowRects.Add((bRect, "corner:" + c));
            bx += bw + 4f;
        }

        if (!expanded) return;

        // Dropdown rows. For Bottom corners the chip is at the bottom, so rows fill from the panel top.
        var rowTop = isBottom ? top + NavPad : headerY + NavHeaderH;
        var y = rowTop;
        foreach (var row in ctx.Legend)
        {
            var rowRect = new Vortice.RawRectF(left, y, left + panelW, y + NavRowH);
            _legendRowRects.Add((rowRect, "target:" + row.Target.Id)); // click → TogglePathTarget(id)

            // Swatch: selected rows fill with their selection-order route color (matches DrawPaths);
            // unselected rows get just a dim outline so the click target is still visible.
            var swatchRect = new Vortice.RawRectF(left + NavPad, y + 3f, left + NavPad + NavSwatch, y + 3f + NavSwatch);
            if (row.IsSelected)
            {
                _bPath!.Color = PathColor(row.ColorSlot);
                rt.FillRectangle(swatchRect, _bPath);
            }
            else
            {
                _bPath!.Color = WithAlpha(_bOther!.Color, 0.45f);
                rt.DrawRectangle(swatchRect, _bPath, 1f);
            }

            // Selected rows get a "> " marker + the highlight color. Entity POIs get a "*" prefix so
            // they're distinguishable from tile landmarks at a glance. Name is already prettified/curated.
            var prefix = row.IsSelected ? "> " : (row.Target.IsEntity ? "* " : "  ");
            var text = prefix + row.Target.Name;
            var textBrush = row.IsSelected ? _bPlayer! : (row.Target.IsEntity ? _bLandmark! : _bText!);
            rt.DrawText(text, _tf!, new Rect(left + NavPad + NavSwatch + 5f, y, left + panelW - 4f, y + NavRowH), textBrush, DrawTextOptions.Clip);
            y += NavRowH;
        }
    }

    private static Color4 WithAlpha(Color4 c, float a) => new(c.R, c.G, c.B, a);

    private static NumVec2 Project(NumVec2 cell, NumVec2 player, NumVec2 center, float scale)
    {
        var d = cell - player;
        var md = MapProjection.GridDeltaToMapDelta(new GameVec2 { X = d.X, Y = d.Y }, scale);
        return new NumVec2(center.X + md.X, center.Y + md.Y);
    }

    public void Dispose()
    {
        _bPlayer?.Dispose(); _bMonster?.Dispose(); _bNpc?.Dispose(); _bChest?.Dispose();
        _bTrans?.Dispose(); _bObject?.Dispose(); _bOther?.Dispose(); _bText?.Dispose(); _bPanel?.Dispose(); _bLandmark?.Dispose();
        _bMagic?.Dispose(); _bRare?.Dispose(); _bUnique?.Dispose();
        _bPath?.Dispose();
        _geoTriangle?.Dispose(); _geoStar?.Dispose(); _geoDiamond?.Dispose(); _geoPlus?.Dispose();
        _tf?.Dispose();
        _terrain?.Dispose();
    }
}

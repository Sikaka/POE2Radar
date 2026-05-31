using System.Runtime.InteropServices;
using Vortice.DCommon;
using Vortice.Direct2D1;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace POE2Radar.Overlay;

/// <summary>
/// Direct2D "fog of war" overlay: one pixel per grid cell, dimming the UNexplored portion of the
/// walkable map. Walkable cells the player has not yet visited get a subtle translucent-dark pixel;
/// explored cells and walls stay fully transparent so the terrain/PoE map shows through.
///
/// <para>Pure visual bookkeeping (draw-only). Rebuilt only when the cheap (areaHash, exploredCount)
/// key changes — i.e. on a zone swap or when the player reveals new ground on a world tick — so the
/// per-frame cost is a single textured-quad draw, not a per-cell loop or allocation.</para>
/// </summary>
public sealed class FogBitmap : IDisposable
{
    private readonly ID2D1RenderTarget _renderTarget;
    private ID2D1Bitmap? _bitmap;
    private int _builtForWidth;
    private int _builtForHeight;
    private uint _builtForAreaHash;
    private int _builtForExplored = -1;

    public FogBitmap(ID2D1RenderTarget renderTarget) { _renderTarget = renderTarget; }

    public ID2D1Bitmap? Bitmap => _bitmap;

    /// <summary>Rebuild only when dimensions, area, or the explored count have changed.</summary>
    public void EnsureBuilt(byte[] walkable, int width, int height, uint areaHash, int exploredCount, Func<int, int, bool> isExplored)
    {
        if (width <= 0 || height <= 0) return;
        if (_bitmap is not null
            && width == _builtForWidth && height == _builtForHeight
            && areaHash == _builtForAreaHash && exploredCount == _builtForExplored) return;
        BuildFrom(walkable, width, height, areaHash, exploredCount, isExplored);
    }

    private void BuildFrom(byte[] walkable, int w, int h, uint areaHash, int exploredCount, Func<int, int, bool> isExplored)
    {
        // Subtle dark wash over unexplored walkable cells (premultiplied BGRA). Kept low-alpha so it
        // dims rather than hides; explored cells / walls remain alpha 0.
        const byte fogAlpha = 110; // ~0.43 — matches RenderContext fog tone, applied per visible cell
        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (walkable[y * w + x] == 0) continue;   // walls: let the real map show through
                if (isExplored(x, y)) continue;            // explored: clear
                var idx = (y * w + x) * 4;
                var af = fogAlpha / 255f;
                pixels[idx + 0] = (byte)(0 * af);          // B
                pixels[idx + 1] = (byte)(0 * af);          // G
                pixels[idx + 2] = (byte)(0 * af);          // R
                pixels[idx + 3] = fogAlpha;                // A
            }
        }

        _bitmap?.Dispose();
        var props = new BitmapProperties(new PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
        var size = new SizeI(w, h);
        var pinned = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try { _bitmap = _renderTarget.CreateBitmap(size, pinned.AddrOfPinnedObject(), (uint)(w * 4), props); }
        finally { pinned.Free(); }

        _builtForWidth = w;
        _builtForHeight = h;
        _builtForAreaHash = areaHash;
        _builtForExplored = exploredCount;
    }

    public void Dispose() { _bitmap?.Dispose(); _bitmap = null; }
}

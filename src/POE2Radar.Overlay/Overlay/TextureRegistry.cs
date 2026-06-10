namespace POE2Radar.Overlay;

/// <summary>
/// DX/ImGui-thread texture registry for PNG files used by the overlay. Callers pass absolute paths;
/// the registry uploads each path once through ClickableTransparentOverlay and returns the texture id.
/// </summary>
public sealed class TextureRegistry
{
    private readonly Dictionary<string, TextureHandle> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public readonly record struct TextureHandle(nint Id, int Width, int Height, string Path);

    public bool TryGet(ClickableTransparentOverlay.Overlay overlay, string absolutePath, out TextureHandle handle)
    {
        handle = default;
        if (string.IsNullOrWhiteSpace(absolutePath)) return false;

        absolutePath = Path.GetFullPath(absolutePath);
        if (_byPath.TryGetValue(absolutePath, out handle)) return handle.Id != 0;
        if (!File.Exists(absolutePath)) return false;

        overlay.AddOrGetImagePointer(absolutePath, false, out var id, out var width, out var height);
        if (id == 0) return false;

        handle = new TextureHandle(id, Convert.ToInt32(width), Convert.ToInt32(height), absolutePath);
        _byPath[absolutePath] = handle;
        return true;
    }

    public bool TryGetOutputTexture(
        ClickableTransparentOverlay.Overlay overlay,
        string fileName,
        out TextureHandle handle)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Overlay", "Textures", fileName);
        if (TryGet(overlay, path, out handle)) return true;

        path = Path.Combine(AppContext.BaseDirectory, "Textures", fileName);
        return TryGet(overlay, path, out handle);
    }

    public void Forget(string absolutePath)
    {
        if (string.IsNullOrWhiteSpace(absolutePath)) return;
        _byPath.Remove(Path.GetFullPath(absolutePath));
    }
}

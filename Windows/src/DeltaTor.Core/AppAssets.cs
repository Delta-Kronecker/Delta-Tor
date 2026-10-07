namespace DeltaTor.Core;

/// <summary>
/// Files shipped with the executable (the counterpart of Android's
/// context.assets). Looked up next to the exe, where the build copies
/// Windows/assets/.
/// </summary>
public static class AppAssets
{
    /// <summary>A bundled file path (relative, '/' or '\' separators), or null when not shipped.</summary>
    public static string? Existing(string relative)
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? path : null;
    }
}

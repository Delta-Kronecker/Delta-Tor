namespace DeltaTor.Core;

/// <summary>
/// Where the official Tor Expert Bundle lives at runtime.
///
/// The bundle is fetched (never committed) by Windows/fetch-binaries.ps1;
/// resolution order:
///   1. the DELTATOR_BUNDLE environment variable (explicit override)
///   2. next to the executable (packaged layout: ./tor/tor.exe, or
///      ./vendor/&lt;bundle&gt;/tor/tor.exe)
///   3. walking up from the executable (development layout: repo-root
///      tor-expert-bundle-* / Windows/vendor/*)
///
/// Layout inside the bundle (official, verified in CI):
///   &lt;root&gt;/tor/tor.exe
///   &lt;root&gt;/tor/pluggable_transports/lyrebird.exe
///   &lt;root&gt;/data/geoip, data/geoip6, data/torrc-defaults
/// </summary>
public static class TorBinaries
{
    private static readonly Lazy<string?> Resolved = new(Resolve);

    public static string? BundleRoot => Resolved.Value;

    public static string TorExe => Path.Combine(BundleRoot ?? "", "tor", "tor.exe");
    public static string LyrebirdExe => Path.Combine(BundleRoot ?? "", "tor", "pluggable_transports", "lyrebird.exe");
    public static string GeoIp => Path.Combine(BundleRoot ?? "", "data", "geoip");
    public static string GeoIp6 => Path.Combine(BundleRoot ?? "", "data", "geoip6");

    public static bool TorAvailable => File.Exists(TorExe);
    public static bool LyrebirdAvailable => File.Exists(LyrebirdExe);

    private static bool LooksLikeBundle(string dir) =>
        File.Exists(Path.Combine(dir, "tor", "tor.exe"));

    private static string? Resolve()
    {
        var env = Environment.GetEnvironmentVariable("DELTATOR_BUNDLE");
        if (!string.IsNullOrEmpty(env) && LooksLikeBundle(env)) return Path.GetFullPath(env);

        var baseDir = AppContext.BaseDirectory;

        // Packaged layout: the bundle sits next to the exe, in any of these forms.
        if (LooksLikeBundle(baseDir)) return baseDir;
        var vendor = Path.Combine(baseDir, "vendor");
        if (Directory.Exists(vendor))
        {
            foreach (var d in Directory.GetDirectories(vendor))
                if (LooksLikeBundle(d)) return d;
        }

        // Development layout: walk up looking for a fetched bundle directory.
        var dir = new DirectoryInfo(baseDir);
        for (var depth = 0; depth < 8 && dir != null; depth++, dir = dir.Parent)
        {
            if (LooksLikeBundle(dir.FullName)) return dir.FullName;
            if (!Directory.Exists(dir.FullName)) continue;
            foreach (var d in Directory.GetDirectories(dir.FullName, "tor-expert-bundle-*"))
                if (LooksLikeBundle(d)) return d;
        }
        return null;
    }
}

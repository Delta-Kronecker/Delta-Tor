using System.Text.Json;
using System.Text.Json.Serialization;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// Counts installs by downloading a one byte asset from a dedicated release.
///
/// GitHub increments the download counter of a release asset on every request,
/// so a single successful fetch per installation is enough to make that number
/// the install count. The asset is never stored and never parsed, only the
/// counter matters.
///
/// The done flag is written only after a request actually reached the asset, so
/// the number is exactly one per installation: every launch before that
/// success retries (with backoff, see <see cref="StatBeacon"/>), every launch
/// after it does nothing at all.
/// </summary>
public static class InstallCounter
{
    private const string TAG = "InstallCounter";

    /// <summary>
    /// The Windows counter asset, separate from Android's so the two platforms
    /// stay distinguishable in the same release (see PLAN 2.2).
    /// </summary>
    private const string AssetUrl =
        "https://github.com/Delta-Kronecker/ForInstallationStatistics/releases/download/" +
        "ForInstallationStatistics/DeltaTorwindows213";

    public static void CountInstall()
    {
        var state = LoadState();
        if (state.Counted) return;

        if (StatBeacon.Hit(TAG, AssetUrl, "DeltaTor-Windows"))
        {
            state.Counted = true;
            SaveState(state);
            AppLog.I(TAG, "Install counted");
        }
    }

    /// <summary>The one-shot state, the counterpart of Android prefs file "deltator_install".</summary>
    private sealed class InstallState
    {
        [JsonPropertyName("counted_v1")]
        public bool Counted { get; set; }
    }

    private static InstallState LoadState()
    {
        try
        {
            if (File.Exists(AppPaths.InstallStateFile))
            {
                return JsonSerializer.Deserialize<InstallState>(
                    File.ReadAllText(AppPaths.InstallStateFile)) ?? new InstallState();
            }
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"state read failed: {e.Message}");
        }
        return new InstallState();
    }

    private static void SaveState(InstallState state)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(AppPaths.InstallStateFile, JsonSerializer.Serialize(state));
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"state write failed: {e.Message}");
        }
    }
}
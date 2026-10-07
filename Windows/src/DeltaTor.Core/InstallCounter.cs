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
/// The flag is written only after a successful response, so a launch without
/// connectivity does not consume the one attempt: the next launch tries again
/// and a failed request never inflates the counter either.
/// </summary>
public static class InstallCounter
{
    private const string TAG = "InstallCounter";
    private const int MaxAttempts = 5;
    private const int TimeoutMs = 8_000;

    /// <summary>
    /// The Windows counter asset, separate from Android's so the two platforms
    /// stay distinguishable in the same release (see PLAN 2.2).
    /// </summary>
    private const string AssetUrl =
        "https://github.com/Delta-Kronecker/ForInstallationStatistics/releases/download/" +
        "ForInstallationStatistics/DeltaTorWindows";

    public static void CountInstall()
    {
        var state = LoadState();
        if (state.Counted) return;
        if (state.Attempts >= MaxAttempts) return;
        state.Attempts++;
        SaveState(state);

        try
        {
            using var resp = Http.GetAsync(AssetUrl).GetAwaiter().GetResult();
            var code = (int)resp.StatusCode;
            resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            if (code is >= 200 and <= 399)
            {
                state.Counted = true;
                SaveState(state);
                AppLog.I(TAG, $"Install counted (HTTP {code})");
            }
            else
            {
                AppLog.W(TAG, $"Install count HTTP {code}");
            }
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"Install count failed: {e.Message}");
        }
    }

    /// <summary>The one-shot state, the counterpart of Android prefs file "deltator_install".</summary>
    private sealed class InstallState
    {
        [JsonPropertyName("counted_v1")]
        public bool Counted { get; set; }

        [JsonPropertyName("attempts_v1")]
        public int Attempts { get; set; }
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

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(TimeoutMs) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeltaTor-Windows");
        return c;
    }
}

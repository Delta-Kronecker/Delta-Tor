using System.Net.Http;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// One GET for one statistic, retried only when the previous attempt really
/// failed.
///
/// The install and connect numbers are GitHub release-asset download counters,
/// so the request *is* the event: every request that reaches the asset bumps
/// the number by one. That makes duplicates expensive and retries safe only
/// when the last request genuinely did not complete, which is exactly what this
/// helper enforces:
///
///  - attempts run in sequence, never in parallel, and one asset is never
///    requested twice at the same time (a second caller waits for the first);
///  - the next attempt happens only after a timeout, a network error, or a
///    non-success HTTP status -- a 200 ends the loop immediately;
///  - the body is copied to Stream.Null and discarded. Nothing is written to
///    disk, so there is no file to save, clean up, or leak;
///  - the total number of attempts is bounded, so a machine that is offline
///    does not spin.
/// </summary>
internal static class StatBeacon
{
    private const string TAG = "StatBeacon";

    /// <summary>One try plus three retries. Failures do not count as downloads.</summary>
    private const int MaxAttempts = 4;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    private static readonly HttpClient Http = new() { Timeout = RequestTimeout };

    private static readonly object GateLock = new();
    private static readonly Dictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    /// <summary>
    /// Requests <paramref name="url"/> until it succeeds or the attempts run
    /// out. Returns true only when the asset was actually served.
    /// </summary>
    public static bool Hit(string name, string url, string userAgent)
    {
        var gate = Gate(name);
        gate.Wait();
        try
        {
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                if (attempt > 1) Thread.Sleep(BackoffMs(attempt));
                if (TryOnce(name, url, userAgent)) return true;
            }
            AppLog.W(TAG, $"{name} still not counted after {MaxAttempts} attempts");
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private static SemaphoreSlim Gate(string name)
    {
        lock (GateLock)
        {
            if (Gates.TryGetValue(name, out var existing)) return existing;
            var created = new SemaphoreSlim(1, 1);
            Gates[name] = created;
            return created;
        }
    }

    /// <summary>1s, 3s, 7s: long enough to clear a dropped link, short enough to finish.</summary>
    private static int BackoffMs(int attempt) => attempt switch
    {
        2 => 1_000,
        3 => 3_000,
        _ => 7_000,
    };

    private static bool TryOnce(string name, string url, string userAgent)
    {
        try
        {
            using var cts = new CancellationTokenSource(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            // GitHub rejects a request without a UA on some paths; this is not
            // the app's real browsing identity, just a counter ping.
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            request.Headers.TryAddWithoutValidation("Accept", "application/octet-stream");

            using var resp = Http.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var code = (int)resp.StatusCode;
            // Read the response out and drop it. The read is what guarantees the
            // transfer finished, and no byte of it ever reaches a file.
            using (var body = resp.Content.ReadAsStream(cts.Token))
            {
                body.CopyTo(Stream.Null);
            }

            if (code is >= 200 and <= 399)
            {
                AppLog.I(TAG, $"{name} counted (HTTP {code})");
                return true;
            }
            AppLog.W(TAG, $"{name} attempt got HTTP {code}");
            return false;
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"{name} attempt failed: {e.Message}");
            return false;
        }
    }
}
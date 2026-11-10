using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeltaTor.Core.Sstp;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// The VPN Gate server pool behind the Proxy Chain: fetches the host list once
/// a day (direct first, from behind Tor if the direct route is censored away),
/// probes each server with a *real* SSTP session, ranks the pool by measured
/// delay, and measures the live chain with a URL request once it exists.
///
/// Every probe is the full negotiation the tunnel itself uses — TCP to Tor's
/// SOCKS bridge, TLS, the SSTP HTTP request, LCP, auth, IPCP — because a TCP
/// connect alone says nothing about a server that answers the port but refuses
/// the hub. The probe session never attaches a TUN (<see cref="SstpMode.Probe"/>).
/// </summary>
public static class ProxyChainManager
{
    private const string Tag = "ChainList";

    /// <summary>The daily-updated VPN Gate host list. Format: one `host[:port]` per line.</summary>
    private const string HostListUrl =
        "https://github.com/Delta-Kronecker/Vpn-Gate/raw/refs/heads/main/sstp_hosts.txt";

    /// <summary>What an untested/failed measurement is stored as.</summary>
    private const long NoDelay = -1L;

    /// <summary>One day, with a margin so an update is not skipped over midnight drift.</summary>
    private const long RefreshMillis = 20L * 60 * 60 * 1000;

    private const int BatchSize = 10;
    private const long ProbeTimeoutMs = 45_000L;

    /// <summary>The request the live chain is measured with.</summary>
    public const string DefaultTestUrl = "http://connectivitycheck.gstatic.com/generate_204";

    private const int UrlTimeoutMs = 10_000;

    /// <summary>A section header found in the host list: one country, any count.</summary>
    public sealed record Country(string Code, string Name);

    public sealed record Server(
        string Host,
        int Port,
        long DelayMs = NoDelay,
        string CountryCode = "",
        string CountryName = "")
    {
        public string Key => $"{Host}:{Port}";
        public bool Tested => DelayMs >= 0;
    }

    private static readonly object Gate = new();
    private static readonly object ListGate = new();

    private static bool _initialized;
    private static long _updatedAtMillis;
    private static List<Country> _countries = new();
    private static List<Server> _servers = new();
    private static int _fetching;
    private static volatile bool _testing;

    private static string ListFile => Path.Combine(AppPaths.Root, "proxy_chain.json");

    /// <summary>Snapshots for the config sheet, grouped by country and already ranked.</summary>
    public static IReadOnlyList<Server> Servers
    {
        get { lock (Gate) return _servers.ToArray(); }
    }

    public static IReadOnlyList<Country> Countries
    {
        get { lock (Gate) return _countries.ToArray(); }
    }

    public static long UpdatedAtMillis
    {
        get { lock (Gate) return _updatedAtMillis; }
    }

    public static bool Testing => _testing;

    public static bool Fetching => Volatile.Read(ref _fetching) != 0;

    public static void Init()
    {
        if (_initialized) return;
        lock (Gate)
        {
            if (_initialized) return;
            _initialized = true;
        }

        Load();
        ClearProbes();
    }

    /// <summary>
    /// One update of the VPN Gate pool per day, the Windows stand-in for the
    /// Android daily worker: the app has no background scheduler once it is
    /// closed, so this keeps the rhythm while it is open and the freshness
    /// check in <see cref="UpdateIfNeededAsync"/> covers every launch.
    /// </summary>
    public static async Task DailyLoopAsync(CancellationToken token = default)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromHours(24), token).ConfigureAwait(false);
                await UpdateIfNeededAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e)
            {
                AppLog.W(Tag, $"daily chain update failed: {e.Message}");
            }
        }
    }

    // -------------------------------------------------------------- freshness

    /// <summary>
    /// Drop every stored probe delay. Runs at app start (a ping from a
    /// previous run is not a ping from this one) and whenever the user
    /// disconnects, so the sheet in front of them only ever carries numbers
    /// from this session.
    /// </summary>
    public static void ClearProbes()
    {
        bool changed;
        lock (ListGate)
        {
            lock (Gate)
            {
                changed = _servers.Any(s => s.DelayMs >= 0);
                if (!changed) return;
                _servers = _servers.Select(s => s with { DelayMs = NoDelay }).ToList();
            }
        }

        if (!changed) return;
        Publish();
        _ = Task.Run(Save);
        AppLog.I(Tag, "probe delays cleared");
    }

    // ------------------------------------------------------------------ store

    private sealed class FileRoot
    {
        public long updated { get; set; }
        public List<FileCountry> countries { get; set; } = new();
        public List<FileServer> servers { get; set; } = new();
    }

    private sealed class FileCountry
    {
        public string c { get; set; } = "";
        public string n { get; set; } = "";
    }

    private sealed class FileServer
    {
        public string h { get; set; } = "";
        public int p { get; set; }
        public long? ms { get; set; }
        public string cc { get; set; } = "";
        public string cn { get; set; } = "";
    }

    private static void Load()
    {
        try
        {
            var path = ListFile;
            if (!File.Exists(path)) return;
            var root = JsonSerializer.Deserialize<FileRoot>(File.ReadAllText(path));
            if (root is null) return;

            lock (Gate)
            {
                _updatedAtMillis = root.updated;
                _countries = root.countries
                    .Select(c => new Country(c.c, c.n))
                    .ToList();
                _servers = root.servers
                    .Select(s => new Server(s.h, s.p, s.ms ?? NoDelay, s.cc, s.cn))
                    .OrderBy(CountryIndex)
                    .ThenBy(DelayOrder)
                    .ToList();
            }

            Publish();
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"chain list restore failed: {e.Message}");
        }
    }

    private static void Save()
    {
        try
        {
            FileRoot root;
            lock (Gate)
            {
                root = new FileRoot
                {
                    updated = _updatedAtMillis,
                    countries = _countries
                        .Select(c => new FileCountry { c = c.Code, n = c.Name })
                        .ToList(),
                    servers = _servers
                        .Select(s => new FileServer
                        {
                            h = s.Host,
                            p = s.Port,
                            ms = s.DelayMs,
                            cc = s.CountryCode,
                            cn = s.CountryName
                        })
                        .ToList()
                };
            }

            var json = JsonSerializer.Serialize(root);
            var tmp = ListFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, ListFile, overwrite: true);
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"chain list save failed: {e.Message}");
        }
    }

    /// <summary>
    /// Repaint the sheet: the list itself is read straight from the properties
    /// above, so raising the shared change event is what makes a fresh
    /// measurement visible.
    /// </summary>
    private static void Publish() => AppState.UpdateChain(state => state);

    private static int CountryIndex(Server server)
    {
        var code = server.CountryCode;
        if (string.IsNullOrWhiteSpace(code)) return int.MaxValue;
        lock (Gate)
        {
            var index = _countries.FindIndex(c => c.Code == code);
            return index < 0 ? int.MaxValue - 1 : index;
        }
    }

    private static long DelayOrder(Server server) =>
        server.DelayMs < 0 ? long.MaxValue : server.DelayMs;

    /// <summary>
    /// Sorted list view: countries first, in the order the host file declared
    /// them, then measured delay inside each country, then whatever the file
    /// itself put last. Unknown countries (an older list without headers) sink
    /// below every declared one, so a fetch with sections always wins.
    /// </summary>
    private static List<Server> Sorted(IEnumerable<Server> servers)
    {
        var list = servers.ToList();
        list.Sort((a, b) =>
        {
            var byCountry = CountryIndex(a).CompareTo(CountryIndex(b));
            return byCountry != 0 ? byCountry : DelayOrder(a).CompareTo(DelayOrder(b));
        });
        return list;
    }

    // ------------------------------------------------------------------ daily

    public static async Task UpdateIfNeededAsync()
    {
        Init();

        bool freshEnough;
        lock (Gate)
        {
            freshEnough = _servers.Count > 0 &&
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - _updatedAtMillis < RefreshMillis;
        }

        if (freshEnough) return;

        await UpdateNowAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Fetch, parse, dedupe, keep previous measurements where the key
    /// survives.
    ///
    /// Returns false when both routes failed or the list came back empty — and
    /// when a fetch is already running — so the sheet knows to offer a retry
    /// instead of assuming the empty list is final.
    /// </summary>
    public static async Task<bool> UpdateNowAsync()
    {
        if (Interlocked.CompareExchange(ref _fetching, 1, 0) != 0) return false;
        try
        {
            var text = await FetchAsync(viaTor: false).ConfigureAwait(false);
            if (text is null)
            {
                AppLog.I(Tag, "direct fetch failed, retrying behind Tor");
                text = await FetchAsync(viaTor: true).ConfigureAwait(false);
            }

            if (text is null)
            {
                AppLog.W(Tag, "host list fetch failed on both routes");
                return false;
            }

            var parsed = Parse(text);
            if (parsed.Count == 0)
            {
                AppLog.W(Tag, "host list came back empty");
                return false;
            }

            // Country sections: however many, in whatever order this fetch
            // brought them. The sheet mirrors this order exactly, so a new
            // country tomorrow (or a missing one) is picked up with no code
            // change.
            var orderedCountries = new List<Country>();
            var seenCountries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var server in parsed)
            {
                if (server.CountryCode.Length == 0) continue;
                if (seenCountries.Add(server.CountryCode))
                {
                    orderedCountries.Add(new Country(server.CountryCode, server.CountryName));
                }
            }

            List<Server> merged;
            lock (Gate)
            {
                var previous = _servers.ToDictionary(s => s.Key, s => s, StringComparer.Ordinal);
                // Fresh country facts always win; only the delay measurement
                // survives from the previous list.
                merged = Sorted(parsed.Select(fresh =>
                {
                    if (previous.TryGetValue(fresh.Key, out var prior))
                    {
                        return fresh with { DelayMs = prior.DelayMs };
                    }

                    return fresh;
                }));

                _countries = orderedCountries;
                _updatedAtMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                _servers = merged;
            }

            Save();
            Publish();
            AppLog.I(Tag,
                $"host list updated: {merged.Count} servers, {orderedCountries.Count} countries");
            return true;
        }
        finally
        {
            Interlocked.Exchange(ref _fetching, 0);
        }
    }

    private static async Task<string?> FetchAsync(bool viaTor)
    {
        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(15),
                UseProxy = viaTor
            };
            if (viaTor)
            {
                handler.Proxy = new WebProxy($"socks5://127.0.0.1:{Config.ProxyPort}");
            }

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
            return await client.GetStringAsync(HostListUrl).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            AppLog.D(Tag, $"fetch ({(viaTor ? "tor" : "direct")}) failed: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Parses the host list. Lines under a <c>=== Country (CC) ===</c> section
    /// header inherit that country; every header is discovered at fetch time,
    /// so the country set can freely change day to day. Hosts before any header
    /// get no country and render in an "unknown" group.
    /// </summary>
    internal static List<Server> Parse(string text)
    {
        var defaultPort = Config.SstpPort;
        var section = new Regex(@"^===\s*(.+?)\s*\(([A-Za-z]{2})\)");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<Server>();
        var currentName = "";
        var currentCode = "";

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var header = section.Match(line);
            if (header.Success)
            {
                currentName = header.Groups[1].Value.Trim();
                currentCode = header.Groups[2].Value.ToUpperInvariant();
                continue;
            }

            var colon = line.LastIndexOf(':');
            var suffix = colon > 0 ? line[(colon + 1)..] : "";
            string host;
            int port;
            if (colon > 0 && suffix.Length > 0 && suffix.All(char.IsDigit))
            {
                host = line[..colon];
                port = int.TryParse(suffix, out var p) ? p : defaultPort;
            }
            else
            {
                host = line;
                port = defaultPort;
            }

            if (host.Trim().Length == 0) continue;

            var normalized = host.ToLowerInvariant();
            if (seen.Add($"{normalized}:{port}"))
            {
                output.Add(new Server(normalized, port, NoDelay, currentCode, currentName));
            }
        }

        return output;
    }

    // ---------------------------------------------------------------- ranking

    /// <summary>
    /// The <paramref name="limit"/> best servers apart from
    /// <paramref name="exclude"/>: ranked by the last real probe, untested ones
    /// last. This is what the engine falls back to when a server dies.
    /// </summary>
    public static IReadOnlyList<Server> BestServers(string? exclude, int limit)
    {
        lock (Gate)
        {
            return _servers
                .Where(s => s.Key != exclude)
                .OrderBy(DelayOrder)
                .Take(limit)
                .ToArray();
        }
    }

    // ------------------------------------------------------------ probing

    /// <summary>
    /// Probe every server with a real SSTP session, <see cref="BatchSize"/> at
    /// a time.
    ///
    /// Progress is published through <see cref="AppState.UpdateChain"/> so the
    /// sheet can watch it without polling. When all servers have been probed
    /// the pool is re-ranked and persisted.
    /// </summary>
    public static async Task<bool> TestAllAsync()
    {
        if (_testing) return false;
        List<Server> pool;
        lock (Gate) pool = _servers.ToList();
        if (pool.Count == 0) return false;
        if (!TorSocksBridge.IsRunning())
        {
            AppLog.W(Tag, "probe skipped: the Tor bridge is not running");
            return false;
        }

        _testing = true;
        AppState.UpdateChain(s => s with { Tested = 0, TestTotal = pool.Count });

        try
        {
            using var semaphore = new SemaphoreSlim(BatchSize, BatchSize);
            var done = 0;
            var results = new System.Collections.Concurrent.ConcurrentDictionary<string, long>(
                StringComparer.Ordinal);

            var tasks = pool.Select(async server =>
            {
                await semaphore.WaitAsync().ConfigureAwait(false);
                try
                {
                    var delay = await ProbeServerAsync(server).ConfigureAwait(false);
                    if (delay is not null) results[server.Key] = delay.Value;
                    ApplyDelay(server.Key, delay ?? NoDelay);
                    var finished = Interlocked.Increment(ref done);
                    AppState.UpdateChain(s => s with { Tested = finished });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks).ConfigureAwait(false);

            // Persist the final full ranking; the live view was already kept
            // up to date one measurement at a time.
            lock (Gate)
            {
                _servers = Sorted(_servers.Select(s =>
                    s with { DelayMs = results.TryGetValue(s.Key, out var ms) ? ms : NoDelay }));
            }

            Save();
            Publish();
            AppLog.I(Tag, $"probe finished: {results.Count}/{pool.Count} reachable");
            return results.Count > 0;
        }
        finally
        {
            _testing = false;
            AppState.UpdateChain(s => s with { Tested = -1, TestTotal = -1 });
        }
    }

    /// <summary>Writes one measurement and re-publishes, so the sheet re-sorts live.</summary>
    private static void ApplyDelay(string key, long delayMs)
    {
        lock (ListGate)
        {
            lock (Gate)
            {
                _servers = Sorted(_servers.Select(s => s.Key == key ? s with { DelayMs = delayMs } : s));
            }
        }

        Publish();
    }

    /// <summary>
    /// One full SSTP negotiation against a single server. Returns the measured
    /// handshake time in milliseconds, or null when the server refused, timed
    /// out or failed anywhere between TCP and IPCP.
    /// </summary>
    private static async Task<long?> ProbeServerAsync(Server server)
    {
        var negotiated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var parameters = new SstpParams
        {
            Mode = SstpMode.Probe,
            Hostname = server.Host,
            Port = server.Port,
            ProxyHostname = "127.0.0.1",
            ProxyPort = Config.ProxyPort,
            Username = "vpn",
            Password = "vpn",
            DoVerifyCert = Config.ChainVerifyTlsCert
        };

        var bridge = new SharedBridge(
            parameters,
            CancellationToken.None,
            builder: null,
            protect: _ => true,
            log: line => AppLog.D(Tag, $"probe {server.Key}: {line}"),
            onSessionEnded: () => ended.TrySetResult(),
            onNegotiated: () => negotiated.TrySetResult());

        // The clock starts before the first byte: the TCP+SOCKS dial is part of
        // what the user would feel picking this server.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var controller = new Controller(bridge);
        controller.LaunchJobMain();

        try
        {
            Task first;
            try
            {
                first = await Task.WhenAny(negotiated.Task, ended.Task)
                    .WaitAsync(TimeSpan.FromMilliseconds(ProbeTimeoutMs))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // No negotiation inside the probe window: whatever is still
                // running must stop. Failures normally killed themselves; a
                // raw timeout did not.
                controller.Kill(null);
                await WaitQuietlyAsync(ended.Task, 5_000).ConfigureAwait(false);
                return null;
            }

            if (first != negotiated.Task)
            {
                return null;
            }

            // Close the probe politely so VPN Gate frees the call slot, and
            // wait - bounded - until the teardown confirms it ended.
            controller.Disconnect();
            await WaitQuietlyAsync(ended.Task, 10_000).ConfigureAwait(false);
            return stopwatch.ElapsedMilliseconds;
        }
        finally
        {
            controller.Kill(null);
        }
    }

    /// <summary>Wait for a task, capped at <paramref name="timeoutMs"/>; false on timeout.</summary>
    private static async Task<bool> WaitQuietlyAsync(Task task, int timeoutMs)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // -------------------------------------------------------------- URL test

    /// <summary>
    /// The delay the *live* chain adds, measured with an actual HTTP fetch.
    ///
    /// The chain owns the machine's default route, so this process — which is
    /// not excluded from the chain adapter (only tor and lyrebird are) — sends
    /// the request through the tunnel exactly like any other app. Returns -1
    /// when the fetch failed.
    /// </summary>
    public static async Task<int> MeasureChainUrlAsync()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromMilliseconds(UrlTimeoutMs),
                UseProxy = false
            };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromMilliseconds(UrlTimeoutMs)
            };

            using var response = await client.GetAsync(Config.ChainTestUrl).ConfigureAwait(false);
            await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            return (int)stopwatch.ElapsedMilliseconds;
        }
        catch (Exception e)
        {
            AppLog.W(Tag, $"chain URL test failed: {e.Message}");
            return -1;
        }
    }
}
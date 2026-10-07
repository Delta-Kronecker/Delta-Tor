using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// A single Tor client instance bound to one transport.
///
/// Used by ParallelTorManager to race vanilla / obfs4 / webtunnel against each
/// other. Each runner owns its own Tor data dir, lyrebird PT process and SOCKS5
/// port, so multiple transports can bootstrap in parallel.
///
/// Transport handling:
/// - "vanilla": bridge lines are `IP:PORT FP` (no pluggable transport plugin).
/// - "obfs4" / "webtunnel": a managed lyrebird process is started first; Tor is
///   configured via `ClientTransportPlugin socks5 &lt;addr&gt;` pointing at the
///   PT's SOCKS5 listener.
/// </summary>
public sealed partial class TorRunner
{
    private readonly string _tag;
    private readonly string _name;
    private readonly string _bridgeLines;
    private readonly string _listenHost;
    private readonly string _dataDir;
    private readonly string _ptStateDir;

    private readonly object _stopLock = new();
    private readonly object _logLock = new();
    private readonly object _healthyLock = new();
    private readonly List<string> _recentLog = new();
    private readonly List<string> _healthyBridges = new();

    private ConcurrentDictionary<string, string> _lyrebirdCmethods = new(StringComparer.Ordinal);

    private volatile Process? _torProcess;
    private volatile Process? _lyrebirdProcess;

    /// <summary>The loopback control port Tor binds when exit countries are selected.</summary>
    private volatile int _controlPort;

    private int _exitApplied;
    private readonly ManualResetEventSlim _exitAppliedDone = new(initialState: true);

    private int _bootstrapPercent;
    private volatile bool _ready;
    private volatile string? _failed;
    private volatile bool _started;

    /// <summary>
    /// True once <see cref="Stop"/> has run and every process it owned was seen
    /// to exit, so a caller can report a real "the core is gone" instead of
    /// "we asked it to go". False before the first stop, and false if a process
    /// outlived the kill.
    /// </summary>
    private volatile bool _confirmedStopped;

    public TorRunner(string name, int torSocksPort, string bridgeLines, string listenHost = "127.0.0.1")
    {
        _name = name;
        _tag = $"TorRunner[{name}]";
        TorSocksPort = torSocksPort;
        _bridgeLines = bridgeLines;
        _listenHost = listenHost;
        _dataDir = AppPaths.TorDataDir(name);
        _ptStateDir = Path.Combine(_dataDir, "pt_state");
    }

    public string Name => _name;
    public int TorSocksPort { get; }
    public bool Ready => _ready;
    public bool Started => _started;
    public bool ConfirmedStopped => _confirmedStopped;

    public string? Failed
    {
        get => _failed;
        set => _failed = value;
    }

    public bool IsRunning()
    {
        var torOk = _torProcess is { HasExited: false };
        var lyrebirdOk = _lyrebirdProcess == null || _lyrebirdProcess is { HasExited: false };
        return torOk && lyrebirdOk;
    }

    public bool IsReady() => _ready && _torProcess is { HasExited: false };

    public int Progress() => Volatile.Read(ref _bootstrapPercent);

    /// <summary>Blocks until the post-bootstrap exit steering finished (or timed out).</summary>
    public bool AwaitExitApplied(int timeoutMs) => _exitAppliedDone.Wait(timeoutMs);

    /// <summary>
    /// Once this runner is bootstrapped, push the selected exit countries onto
    /// the running Tor over its control port. Runs once per session on a
    /// background thread so it can never block the log reader. Every step is
    /// traced under its own "ExitNode" log section so a device-side failure is
    /// visible in the app log.
    /// </summary>
    private void ApplyExitNodesLater()
    {
        var ccs = ExitNodes.CurrentCodes()
            .Select(c => c.Trim().ToUpperInvariant())
            .Where(c => c.Length == 2 && c.All(ch => ch is >= 'A' and <= 'Z'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ccs.Count == 0) return;
        if (Interlocked.CompareExchange(ref _exitApplied, 1, 0) != 0) return;
        var port = _controlPort;
        if (port <= 0) return;
        var thread = new Thread(() =>
        {
            var ok = false;
            try
            {
                ok = ApplyExitNodesViaControl(port, ccs);
            }
            catch (Exception e)
            {
                AppLog.W(TagExit, $"[{_name}] could not apply via control port {port}: {e.Message}");
            }
            _exitAppliedDone.Set();
            AppLog.I(TagExit,
                $"[{_name}] exit nodes {(ok ? "ACTIVE" : "NOT applied")} ({string.Join(",", ccs)}; StrictNodes 0)");
        });
        thread.IsBackground = true;
        thread.Name = $"{_name}-exit-apply";
        thread.Start();
    }

    /// <summary>
    /// The control exchange itself. Returns true when the exit rules took
    /// effect. The template this app ships with keeps values that fight a live
    /// switch, so the present values are retired (10 min turnover, Conflux off)
    /// and a fresh identity is requested so the next streams leave through the
    /// selected countries. Any line the binary rejects is left alone, traced,
    /// and the session continues with the exit list still in force.
    /// </summary>
    private bool ApplyExitNodesViaControl(int port, IReadOnlyList<string> ccs)
    {
        var exitValue = string.Join(",", ccs.Select(c => "{" + c + "}"));
        AppLog.I(TagExit, $"=== EXIT NODE |> {_name} |> {string.Join(",", ccs)} ===");
        AppLog.I(TagExit, $"[{_name}] official geoip in use for {string.Join(",", ccs)}");
        AppLog.I(TagExit, $"[{_name}] control port: {_listenHost}:{port}");
        return SendControl(port, new[]
        {
            new ControlCommand("AUTHENTICATE", Optional: false),
            new ControlCommand($"SETCONF ExitNodes=\"{exitValue}\"", Optional: false),
            new ControlCommand("SETCONF StrictNodes=0", Optional: false),
            new ControlCommand("SETCONF MaxCircuitDirtiness=600", Optional: true),
            new ControlCommand("SETCONF ConfluxEnabled=0", Optional: true),
            new ControlCommand("SETCONF CircuitBuildTimeout=180", Optional: true),
            new ControlCommand("SETCONF LearnCircuitBuildTimeout=1", Optional: true),
            new ControlCommand("SIGNAL NEWNYM", Optional: true),
            new ControlCommand("GETCONF ExitNodes", Optional: false),
            new ControlCommand("GETCONF StrictNodes", Optional: false)
        });
    }

    private sealed record ControlCommand(string Line, bool Optional);

    /// <summary>
    /// Send a short control exchange to a bootstrapped Tor. Logs one line per
    /// command (command + final status) under the ExitNode section; optional
    /// commands that fail are waived with a warning, mandatory ones throw.
    /// </summary>
    private bool SendControl(int port, IReadOnlyList<ControlCommand> commands)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            if (!socket.ConnectAsync(IPAddress.Loopback, port).Wait(4_000))
                throw new TimeoutException("connect timed out");
            socket.ReceiveTimeout = 4_000;
            socket.SendTimeout = 4_000;
            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.Latin1, detectEncodingFromByteOrderMarks: false);
            foreach (var command in commands)
            {
                var bytes = Encoding.Latin1.GetBytes(command.Line + "\r\n");
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
                var reply = ReadReply(reader);
                var ok = reply.Count > 0 && reply[^1].StartsWith("250", StringComparison.Ordinal);
                if (!ok)
                {
                    if (command.Optional)
                    {
                        AppLog.W(TagExit,
                            $"[{_name}] {command.Line} -> {string.Join(" / ", reply)} (ignored)");
                        continue;
                    }
                    throw new Exception($"{command.Line} -> {string.Join(" / ", reply)}");
                }
                AppLog.I(TagExit, $"[{_name}] {command.Line} -> {reply[^1]}");
            }
            return true;
        }
        finally
        {
            try { socket.Close(); } catch { /* already gone */ }
        }
    }

    /// <summary>
    /// Read one complete control reply. Single-line replies come back as one
    /// line; multi-line "250-..." blocks end at their "250 ..." terminator and
    /// "250+..." (data) blocks consume the raw data lines up to the "." line and
    /// the closing "250 ..." line.
    /// </summary>
    private static List<string> ReadReply(StreamReader reader)
    {
        var outLines = new List<string>();
        var line = reader.ReadLine();
        if (line == null) return outLines;
        outLines.Add(line);
        if (line.StartsWith("250+", StringComparison.Ordinal))
        {
            // Raw data follows: read until the standalone "." terminator.
            while (true)
            {
                var data = reader.ReadLine();
                if (data == null) break;
                if (data == ".") break;
                outLines.Add(data);
            }
            var closing = reader.ReadLine();
            if (closing != null) outLines.Add(closing);
        }
        else
        {
            while (line.StartsWith("250-", StringComparison.Ordinal))
            {
                line = reader.ReadLine();
                if (line == null) break;
                outLines.Add(line);
            }
        }
        return outLines;
    }

    /// <summary>
    /// A free loopback port for the control listener. Port 0 picks one from the
    /// ephemeral range; the small re-bind race is acceptable here and a runner
    /// that wins only ever needs it after its own bootstrap.
    /// </summary>
    private static int FreeEphemeralPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // Ring buffer of this runner's own log lines, so a dead process can be
    // explained after the fact (Tor's own words, not a generic "process exited").
    private void NoteLog(string message)
    {
        lock (_logLock)
        {
            _recentLog.Add(message);
            while (_recentLog.Count > 60) _recentLog.RemoveAt(0);
        }
    }

    /// <summary>
    /// One-line reason this runner is not going to win: the most informative
    /// line Tor or lyrebird printed, preferring errors/warnings over the rest.
    /// </summary>
    public string FailureSummary()
    {
        List<string> lines;
        lock (_logLock) lines = _recentLog.ToList();
        if (lines.Count == 0) return "process exited (no output captured)";
        var meaningful = lines.Where(line =>
        {
            var l = line.ToLowerInvariant();
            return l.Contains("error") || l.Contains("failed") || l.Contains("fatal") ||
                   l.Contains("warn") || l.Contains("could not") || l.Contains("unable") ||
                   l.Contains("refused") || l.Contains("no such") || l.Contains("not found") ||
                   l.Contains("unrecognized") || l.Contains("invalid") || l.Contains("timeout");
        }).ToList();
        var detail = (meaningful.Count > 0 ? meaningful : lines).Last().Trim();
        var exit = "";
        var tor = _torProcess;
        if (tor is { HasExited: true })
        {
            var code = -1;
            try { code = tor.ExitCode; } catch { /* raced */ }
            exit = $" (exit {code})";
        }
        return $"process exited{exit} · {Truncate(detail, 220)}";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

    /// <summary>Everything this runner has logged during the current session (for the log UI).</summary>
    public IReadOnlyList<string> LogLines()
    {
        lock (_logLock) return _recentLog.ToArray();
    }

    /// <summary>
    /// Fingerprints of the bridges that actually worked for this run: Tor only
    /// logs a bridge descriptor once the pluggable-transport handshake and the
    /// descriptor download both succeeded, which makes it the one reliable
    /// "this bridge is alive" signal in the log.
    /// </summary>
    public IReadOnlyList<string> HealthyBridges()
    {
        lock (_healthyLock) return _healthyBridges.ToArray();
    }

    // Matches: new bridge descriptor 'NAME' (cached): $FINGERPRINT~NAME [...]
    // and the "(fresh): $" variant. Mirrors the Windows client's log scan.
    private void NoteBridgeDescriptor(string line)
    {
        if (!line.Contains("bridge descriptor", StringComparison.OrdinalIgnoreCase)) return;
        var i = line.IndexOf(CachedMarker, StringComparison.Ordinal);
        if (i < 0)
        {
            i = line.IndexOf(FreshMarker, StringComparison.Ordinal);
            if (i < 0) return;
            i += FreshMarker.Length;
        }
        else
        {
            i += CachedMarker.Length;
        }
        // Hex only, and all of it. A partial or non-hex fingerprint is worse
        // than none: it counts towards the pool and matches no line in any
        // list. The scan stops at the first character that is not hex.
        var hex = new StringBuilder(FingerprintHexLength);
        while (i < line.Length && line[i] != '~' && hex.Length < FingerprintHexLength)
        {
            var c = line[i];
            if (!IsHexChar(c)) break;
            hex.Append(c);
            i++;
        }
        // Lower-cased on the way in, because the lists are lower-cased and a
        // fingerprint is the same fingerprint in either case.
        var fp = hex.ToString().ToLowerInvariant();
        if (fp.Length == FingerprintHexLength)
        {
            lock (_healthyLock)
            {
                if (!_healthyBridges.Contains(fp)) _healthyBridges.Add(fp);
            }
        }
    }

    private static bool IsHexChar(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    /// <summary>
    /// Start the PT (if needed) and the Tor process for this transport. Bridge
    /// lines are capped to <see cref="MaxBridgeLines"/>, taken from the end of
    /// a list that has already been shuffled, so which lines are used differs
    /// per attempt. Returns null on success, or the reason it failed.
    /// </summary>
    public Exception? Start()
    {
        Stop();
        Volatile.Write(ref _bootstrapPercent, 0);
        _controlPort = 0;
        Interlocked.Exchange(ref _exitApplied, 0);
        _exitAppliedDone.Set();
        _ready = false;
        _failed = null;
        _started = true;
        lock (_logLock) _recentLog.Clear();
        lock (_healthyLock) _healthyBridges.Clear();

        try
        {
            var rawLines = ParallelTorManager.SplitLines(_bridgeLines)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
                .Select(l => l.StartsWith("bridge ", StringComparison.OrdinalIgnoreCase) ? l[7..].Trim() : l)
                .ToList();
            // Tor aborts on the FIRST bad Bridge line, so a single malformed
            // entry from a collector would take the whole transport down. Drop
            // them here and say so, instead of losing the runner.
            var wellFormed = new List<string>();
            var malformed = new List<string>();
            foreach (var l in rawLines) (IsValidBridgeLine(l) ? wellFormed : malformed).Add(l);
            if (malformed.Count > 0)
            {
                AppLog.W(_tag,
                    $"dropped {malformed.Count} malformed bridge line(s), first: {Truncate(malformed[0], 70)}");
            }
            var cleanLines = wellFormed.Take(MaxBridgeLines).ToList();

            var isDirect = _name == "direct";
            if (cleanLines.Count == 0 && !isDirect)
            {
                return new Exception($"{_tag}: no bridge lines available");
            }

            // `vanilla-memory` is the vanilla transport too, hence StartsWith.
            var isVanilla = _name.StartsWith(ParallelTorManager.TransportVanilla, StringComparison.Ordinal) || isDirect;
            // Only real pluggable-transport names count. The memory runner mixes
            // plain `ip:port fp` lines with prefixed ones, and a bare address as
            // the first token must not be mistaken for a CMETHOD.
            var transports = isVanilla
                ? new List<string>()
                : cleanLines
                    .Select(FirstToken)
                    .Where(t => t != null && PluggableTransports.Contains(t))
                    .Select(t => t!)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

            Directory.CreateDirectory(_dataDir);
            Directory.CreateDirectory(_ptStateDir);
            foreach (var f in new[] { "state", "lock" })
            {
                var file = Path.Combine(_dataDir, f);
                if (File.Exists(file)) File.Delete(file);
            }

            // The lines Tor is finally given. Narrowed below when lyrebird turns
            // out not to speak every transport the list asked for.
            var usableLines = cleanLines;

            if (transports.Count > 0)
            {
                if (!TorBinaries.LyrebirdAvailable)
                    return new Exception($"{_tag}: lyrebird (obfs4proxy) binary not found");
                var lyrebirdFailure = StartLyrebird(TorBinaries.LyrebirdExe, transports);
                if (lyrebirdFailure != null) return lyrebirdFailure;
                var missing = transports.Where(t => !_lyrebirdCmethods.ContainsKey(t)).ToList();
                if (missing.Count > 0)
                {
                    // lyrebird serves obfs4, webtunnel and meek-lite (and
                    // snowflake on the official bundle). A list that mixes a
                    // transport it cannot serve with ones it can is still worth
                    // running on the ones it can, so those lines are dropped and
                    // the runner goes on. Only a list that needs nothing else is
                    // a real failure.
                    AppLog.W(_tag, $"Lyrebird cannot serve [{string.Join(", ", missing)}]; dropping those bridge line(s)");
                    foreach (var m in missing) transports.Remove(m);
                    usableLines = cleanLines
                        .Where(line => !missing.Contains(FirstToken(line) ?? ""))
                        .ToList();
                    if (usableLines.Count == 0 && !isDirect)
                    {
                        Stop();
                        return new Exception($"{_tag}: every bridge line needs [{string.Join(", ", missing)}]");
                    }
                }
            }

            PrepareGeoIp();

            var torrcPath = WriteTorrc(usableLines, isVanilla, isDirect);
            var torBinary = TorBinaries.TorExe;
            if (!File.Exists(torBinary))
            {
                return new Exception($"{_tag}: Tor binary not found at {torBinary}");
            }

            var psi = new ProcessStartInfo
            {
                FileName = torBinary,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = _dataDir
            };
            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(torrcPath);
            psi.Environment["HOME"] = _dataDir;

            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
            _torProcess = process;

            StartReader(process.StandardOutput, "Tor: ", $"{_name}-tor-output");
            StartReader(process.StandardError, "Tor: ", $"{_name}-tor-error");

            AppLog.I(_tag,
                $"Started Tor on {_listenHost}:{TorSocksPort} ({_name}, {string.Join(", ", transports)} transport)");
            return null;
        }
        catch (Exception e)
        {
            AppLog.E(_tag, "Start failed", e);
            Stop();
            return e;
        }
    }

    private void StartReader(StreamReader reader, string prefix, string threadName)
    {
        var thread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    HandleTorLine(prefix + line);
                }
            }
            catch (Exception e)
            {
                if (_torProcess != null)
                {
                    AppLog.W(_tag, $"Tor output reader error: {e.Message}");
                }
            }
        });
        thread.IsBackground = true;
        thread.Name = threadName;
        thread.Start();
    }

    private void HandleTorLine(string line)
    {
        NoteLog(line);
        AppLog.D(_tag, line);
        NoteBridgeDescriptor(line);
        var match = BootstrapRegex().Match(line);
        if (match.Success)
        {
            var pct = int.Parse(match.Groups[1].Value);
            Volatile.Write(ref _bootstrapPercent, pct);
            AppLog.I(_tag, $"Bootstrap: {pct}%");
            if (pct >= 100)
            {
                _ready = true;
                ApplyExitNodesLater();
            }
        }
    }

    [GeneratedRegex(@"Bootstrapped (\d+)%")]
    private static partial Regex BootstrapRegex();

    // --- Lyrebird managed transport ---

    /// <summary>
    /// Launch lyrebird as a managed transport process and wait for CMETHOD
    /// registration (Tor's PT spec); it can provide obfs4, webtunnel, meek_lite
    /// and snowflake transports on the official bundle.
    /// </summary>
    private Exception? StartLyrebird(string ptBinaryPath, IReadOnlyList<string> transports)
    {
        _lyrebirdCmethods = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        AppLog.I(_tag, $"Launching lyrebird for transports: {string.Join(", ", transports)}");

        var psi = new ProcessStartInfo
        {
            FileName = ptBinaryPath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        psi.Environment["TOR_PT_MANAGED_TRANSPORT_VER"] = "1";
        psi.Environment["TOR_PT_CLIENT_TRANSPORTS"] = string.Join(",", transports);
        psi.Environment["TOR_PT_STATE_LOCATION"] = _ptStateDir + Path.DirectorySeparatorChar;
        psi.Environment["TOR_PT_EXIT_ON_STDIN_CLOSE"] = "1";

        Process process;
        try
        {
            process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (Exception e)
        {
            AppLog.E(_tag, $"Failed to launch lyrebird: {e.Message}");
            return new Exception($"{_tag}: failed to launch lyrebird: {e.Message}");
        }
        _lyrebirdProcess = process;

        var stderrThread = new Thread(() =>
        {
            try
            {
                string? line;
                while ((line = process.StandardError.ReadLine()) != null)
                {
                    var l = $"lyrebird stderr: {line}";
                    NoteLog(l);
                    AppLog.D(_tag, l);
                }
            }
            catch { /* process ended */ }
        });
        stderrThread.IsBackground = true;
        stderrThread.Name = $"{_name}-lyrebird-stderr";
        stderrThread.Start();

        // Deliberately not disposed: the stdout reader thread may still call
        // Set() long after this method returns (when the process finally closes
        // its pipe), and a disposed event would crash that thread.
        var cmethodsDone = new ManualResetEventSlim(initialState: false);
        var protocolError = new string?[1];

        var stdoutThread = new Thread(() =>
        {
            try
            {
                string? raw;
                while ((raw = process.StandardOutput.ReadLine()) != null)
                {
                    var l = raw.Trim();
                    NoteLog($"lyrebird PT: {l}");
                    AppLog.D(_tag, $"Lyrebird PT: {l}");
                    if (l.StartsWith("VERSION ", StringComparison.Ordinal))
                    {
                        AppLog.I(_tag, $"Lyrebird protocol: {l}");
                    }
                    else if (l.StartsWith("CMETHOD ", StringComparison.Ordinal))
                    {
                        var parts = WhitespaceRegex().Split(l);
                        if (parts.Length >= 4)
                        {
                            var m = parts[1];
                            var addr = parts[3];
                            _lyrebirdCmethods[m] = addr;
                            AppLog.I(_tag, $"Lyrebird registered: {m} at {addr}");
                        }
                    }
                    else if (l.StartsWith("CMETHOD-ERROR ", StringComparison.Ordinal))
                    {
                        AppLog.E(_tag, $"Lyrebird transport error: {l}");
                    }
                    else if (l == "CMETHODS DONE")
                    {
                        AppLog.I(_tag, "Lyrebird: all transports registered");
                        cmethodsDone.Set();
                    }
                    else if (l.StartsWith("ENV-ERROR ", StringComparison.Ordinal))
                    {
                        protocolError[0] = l;
                        cmethodsDone.Set();
                    }
                    else if (l.StartsWith("VERSION-ERROR ", StringComparison.Ordinal))
                    {
                        protocolError[0] = l;
                        cmethodsDone.Set();
                    }
                }
            }
            catch (Exception e)
            {
                if (_lyrebirdProcess != null)
                {
                    AppLog.W(_tag, $"Lyrebird stdout reader error: {e.Message}");
                }
            }
            cmethodsDone.Set();
        });
        stdoutThread.IsBackground = true;
        stdoutThread.Name = $"{_name}-lyrebird-stdout";
        stdoutThread.Start();

        var success = cmethodsDone.Wait(TimeSpan.FromSeconds(10));
        if (!success)
        {
            AppLog.E(_tag, "Lyrebird timed out waiting for CMETHODS DONE");
            Stop();
            return new Exception($"{_tag}: lyrebird timed out during PT protocol setup");
        }
        if (protocolError[0] != null)
        {
            Stop();
            return new Exception($"{_tag}: lyrebird protocol error: {protocolError[0]}");
        }
        if (process.HasExited)
        {
            var exitCode = process.ExitCode;
            Stop();
            return new Exception($"{_tag}: lyrebird exited with code {exitCode}");
        }
        return null;
    }

    // --- torrc ---

    private string WriteTorrc(IReadOnlyList<string> cleanLines, bool isVanilla, bool isDirect = false)
    {
        // Filtered to real transport names for the same reason Start() does: a
        // plain `ip:port fp` line has an address as its first token, and
        // treating that as a transport name logged one bogus "no CMETHOD"
        // warning per bridge.
        var transports = cleanLines
            .Select(l => WhitespaceRegex().Split(l).FirstOrDefault()?.ToLowerInvariant() ?? "")
            .Where(t => PluggableTransports.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var torrcFile = Path.Combine(_dataDir, "torrc");
        var common = new StringBuilder();
        common.Append($"SocksPort {_listenHost}:{TorSocksPort}\n");
        common.Append($"DataDirectory {Quote(_dataDir)}\n");
        common.Append($"UseBridges {(isDirect ? 0 : 1)}\n");
        if (File.Exists(TorBinaries.GeoIp)) common.Append($"GeoIPFile {Quote(TorBinaries.GeoIp)}\n");
        if (File.Exists(TorBinaries.GeoIp6)) common.Append($"GeoIPv6File {Quote(TorBinaries.GeoIp6)}\n");
        common.Append("Log info stdout\n");
        common.Append("KeepalivePeriod 30\n");
        common.Append("ClientUseIPv4 1\n");
        common.Append("ClientUseIPv6 1\n");
        common.Append("ClientPreferIPv6ORPort auto\n");
        common.Append("DormantClientTimeout 2419200\n");
        common.Append("ClientBootstrapConsensusAuthorityDownloadInitialDelay 0\n");
        common.Append("ReducedConnectionPadding 0");
        var commonStr = common.ToString();

        var pluginDirectives = new StringBuilder();
        if (!isVanilla)
        {
            foreach (var transport in transports)
            {
                if (_lyrebirdCmethods.TryGetValue(transport, out var addr))
                {
                    pluginDirectives.Append($"ClientTransportPlugin {transport} socks5 {addr}\n");
                }
                else
                {
                    AppLog.W(_tag, $"No lyrebird CMETHOD for transport: {transport}");
                }
            }
        }

        var bridgeDirectives = new StringBuilder();
        foreach (var line in cleanLines)
        {
            bridgeDirectives.Append($"Bridge {line}\n");
        }

        // Exit-country steering is applied AFTER bootstrap via the control port
        // (see ApplyExitNodesLater). Bootstrapping with ExitNodes in the torrc
        // makes the initial microdescriptor phase much slower, because Tor only
        // counts descriptors of the chosen countries towards having enough
        // directory info; on a censored net that extra delay is where "never
        // connects" comes from. So the first boot is unrestricted (fast), and
        // the country rules are pushed in once the runner is already at 100%.
        var exitCodes = ExitNodes.CurrentCodes()
            .Select(c => c.Trim().ToUpperInvariant())
            .Where(c => c.Length == 2 && c.All(ch => ch is >= 'A' and <= 'Z'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (exitCodes.Count > 0)
        {
            _controlPort = FreeEphemeralPort();
        }

        var templateLines = TorrcSettings.TemplateLines();
        var torrcContent =
            $"{commonStr}\n{string.Join("\n", templateLines)}\n{pluginDirectives.ToString().Trim()}\n{bridgeDirectives.ToString().Trim()}\n" +
            (exitCodes.Count > 0 ? $"ControlPort {_listenHost}:{_controlPort}\n" : "");
        File.WriteAllText(torrcFile, torrcContent);
        try
        {
            File.WriteAllText(Path.Combine(AppPaths.Root, "tor_last.torrc"), torrcContent);
        }
        catch { /* not fatal */ }

        AppLog.D(_tag, $"--- Generated torrc ({_name}) ---");
        foreach (var line in ParallelTorManager.SplitLines(torrcContent))
        {
            if (!string.IsNullOrWhiteSpace(line)) AppLog.D(_tag, $"torrc: {line}");
        }
        AppLog.D(_tag, $"--- End torrc ({_name}) ---");
        return torrcFile;
    }

    /// <summary>Quote a path for torrc when it contains spaces (common on Windows).</summary>
    private static string Quote(string path) =>
        path.Contains(' ') ? $"\"{path}\"" : path;

    // --- Helpers ---

    private static string? FirstToken(string line) =>
        WhitespaceRegex().Split(line).FirstOrDefault(t => t.Length > 0)?.ToLowerInvariant();

    /// <summary>
    /// Windows ships the official geoip databases inside the bundle; there is
    /// nothing to generate per-run (that was an Android-only need of the
    /// geoip-less libtor build).
    /// </summary>
    private void PrepareGeoIp()
    {
        var codes = ExitNodes.CurrentCodes()
            .Select(c => c.Trim().ToUpperInvariant())
            .Where(c => c.Length == 2 && c.All(ch => ch is >= 'A' and <= 'Z'))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (codes.Count == 0) return;
        if (File.Exists(TorBinaries.GeoIp))
        {
            AppLog.I(TagExit,
                $"[{_name}] official geoip ready for {string.Join(",", codes)} (Tor can resolve the exit countries)");
        }
        else
        {
            AppLog.W(TagExit,
                $"[{_name}] geoip NOT found: Tor cannot resolve {string.Join(",", codes)} and will pick any exit");
            AppLog.W(_tag,
                "no geoip database: the exit country cannot be enforced, Tor will pick any exit");
        }
    }

    /// <summary>
    /// Stop the Tor and lyrebird processes for this instance. This blocks until
    /// the processes are really gone: a listening socket is only released when
    /// its process exits. Callers that immediately rebind the same port depend
    /// on that guarantee, so a stubborn process is reported instead of being
    /// left behind.
    /// </summary>
    public void Stop()
    {
        lock (_stopLock)
        {
            var tor = _torProcess;
            var bird = _lyrebirdProcess;
            _torProcess = null;
            _lyrebirdProcess = null;

            var torDead = Terminate(tor, "Tor");
            var birdDead = Terminate(bird, "lyrebird");
            _confirmedStopped = (tor == null || torDead) && (bird == null || birdDead);

            _lyrebirdCmethods = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
            _ready = false;
        }
    }

    /// <summary>Returns true only when the process is confirmed gone.</summary>
    private bool Terminate(Process? p, string what)
    {
        if (p == null) return true;
        try
        {
            if (!p.HasExited)
            {
                if (what == "lyrebird")
                {
                    // The PT spec's polite stop: TOR_PT_EXIT_ON_STDIN_CLOSE=1.
                    try { p.StandardInput.Close(); } catch { /* already closed */ }
                    if (p.WaitForExit((int)TerminateTimeoutMs)) return true;
                    AppLog.W(_tag, $"{what} ignored the polite stop, forcing kill");
                }
                p.Kill(entireProcessTree: true);
                if (!p.WaitForExit((int)TerminateTimeoutMs))
                {
                    AppLog.E(_tag, $"{what} still alive after kill: {Describe(p)}");
                    return false;
                }
            }
            return true;
        }
        catch (Exception e)
        {
            AppLog.E(_tag, $"Error stopping {what}", e);
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return p.HasExited;
        }
    }

    private static string Describe(Process p)
    {
        try { return $"pid {p.Id}"; } catch { return "pid ?"; }
    }

    /// <summary>
    /// True when Tor will accept this as a `Bridge` line. Tor refuses the whole
    /// configuration on the first bridge it cannot parse, and the usual
    /// offender is a fingerprint that is not exactly 40 hex characters, so that
    /// is checked here before anything reaches torrc. The shape is either
    /// `addr:port FINGERPRINT ...` (vanilla) or
    /// `obfs4|webtunnel|snowflake|meek addr:port FINGERPRINT ...`.
    /// </summary>
    private static bool IsValidBridgeLine(string line)
    {
        var parts = WhitespaceRegex().Split(line).Where(p => !string.IsNullOrWhiteSpace(p)).ToArray();
        if (parts.Length < 2) return false;
        var fpIndex = PluggableTransports.Contains(parts[0].ToLowerInvariant()) ? 2 : 1;
        if (parts.Length <= fpIndex) return false;
        var addr = parts[fpIndex - 1];
        if (!addr.Contains(':') || addr.StartsWith(":")) return false;
        return IsFingerprint(parts[fpIndex]);
    }

    /// <summary>A bridge identity digest is always 40 hex characters.</summary>
    private static bool IsFingerprint(string token) =>
        token.Length == FingerprintHexLength && token.All(IsHexChar);

    private const int MaxBridgeLines = 150;
    private const int FingerprintHexLength = 40;
    private const long TerminateTimeoutMs = 2_000L;
    private const string TagExit = "ExitNode";
    private const string CachedMarker = "(cached): $";
    private const string FreshMarker = "(fresh): $";

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private static readonly HashSet<string> PluggableTransports = new(StringComparer.Ordinal)
        { "obfs4", "webtunnel", "snowflake", "meek_lite", "meek" };
}

using System.Net.Http.Headers;
using DeltaTor.Core.Util;

namespace DeltaTor.Core;

/// <summary>
/// The race engine of <see cref="ParallelTorManager"/> (Windows counterpart of
/// the Android race()/restartTransport() coroutine code). Runs synchronously —
/// the service state machine calls it on a worker thread, which replaces
/// withContext(Dispatchers.IO).
///
/// Fetch bridge lists, launch every runner and wait for the first to reach
/// 100% bootstrap. The winner carries the traffic; the losers are stopped.
/// </summary>
public static partial class ParallelTorManager
{
    private static readonly object RunnersLock = new();

    private static IReadOnlyDictionary<string, TorRunner> _runners =
        new Dictionary<string, TorRunner>(StringComparer.Ordinal);

    /// <summary>
    /// Bridge lines and ports of the last race, kept so a recovery can rebuild
    /// the winning transport without going back to the network for its list.
    /// </summary>
    private static IReadOnlyDictionary<string, string> _lastPlans =
        new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, int> _lastPorts =
        new Dictionary<string, int>(StringComparer.Ordinal);

    /// <summary>
    /// Bumped whenever the runner map is emptied. A start captures it and
    /// checks it every poll, so a stop or a second connect that empties the map
    /// under a running race is reported as the supersession it is, instead of
    /// the race waking up to an empty snapshot and blaming the transports it
    /// was watching.
    /// </summary>
    private static long _generation;

    /// <summary>Current bootstrap % per transport (-1 = failed). Safe for UI reads.</summary>
    public static IReadOnlyDictionary<string, int> ProgressSnapshot()
    {
        lock (RunnersLock)
        {
            return _runners.ToDictionary(
                kv => kv.Key,
                kv => kv.Value.Failed != null ? -1 : kv.Value.Progress(),
                StringComparer.Ordinal);
        }
    }

    public static bool IsBusy()
    {
        lock (RunnersLock)
        {
            return _runners.Values.Any(r => r.Started && (r.Failed == null || !r.Ready) && r.IsRunning());
        }
    }

    /// <summary>
    /// Fetch bridge lists, launch every runner and wait for the first to reach
    /// 100% bootstrap. Returns the winning <see cref="TorRunner"/>; the losers
    /// are stopped and their processes torn down.
    ///
    /// <paramref name="basePort"/> is the app's own bridge port. It is reserved
    /// and NOT handed to any runner: the runners take basePort+1 ..
    /// basePort+MaxPortOffset, so a runner that wins can never hold the port
    /// the app needs to bind.
    ///
    /// <paramref name="autoTransports"/> is the transports the user allows in
    /// auto mode; ignored by every other mode, which always runs one transport.
    /// <paramref name="onProgress"/> is called every <see cref="PollIntervalMs"/>
    /// with a live snapshot, including runners that have not finished starting.
    /// <paramref name="runMemory"/> false starts the chosen mode's runner alone
    /// (the log is still read, so what proves itself is still recorded).
    /// </summary>
    public static TorRunner Race(
        int basePort,
        int sessionId,
        string transportMode,
        string customBridges,
        IReadOnlySet<string> autoTransports,
        bool runMemory = true,
        Action<IReadOnlyDictionary<string, TorRunner>>? onProgress = null)
    {
        StopAll();

        // Unknown or missing values fall back to auto, so a stale preference
        // can never leave the user with no way to connect.
        var modeRaw = transportMode.ToLowerInvariant();
        var mode = Modes.Contains(modeRaw) ? modeRaw : TransportAuto;
        var customLines = SplitLines(customBridges)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#", StringComparison.Ordinal))
            .ToList();

        if (mode == TransportCustom && customLines.Count == 0)
        {
            StopAll();
            throw new InvalidOperationException("No custom bridges provided");
        }

        // Auto races exactly what the user ticked. A blank or fully stale set
        // falls back to the full list so auto can never end up with nothing.
        var autoNames = autoTransports.Where(t => AutoSources.Contains(t)).ToList();
        if (autoNames.Count == 0) autoNames = AutoSources.ToList();

        // Only the lists this mode actually needs are resolved, which keeps a
        // direct connect from waiting on any download at all.
        HashSet<string> needed = mode switch
        {
            TransportAuto => autoNames.ToHashSet(StringComparer.Ordinal),
            TransportDirect or TransportCustom => new HashSet<string>(StringComparer.Ordinal),
            TransportCombined => CombinedSources.ToHashSet(StringComparer.Ordinal),
            _ => new HashSet<string>(StringComparer.Ordinal) { mode }
        };
        var fetched = mode == TransportCustom
            ? new Dictionary<string, string>(StringComparer.Ordinal)
                { [TransportCustom] = string.Join("\n", customLines) }
            : FetchBridgeLines(needed);

        // Combined has no list of its own, so it is assembled here out of the
        // lists it covers. Merged rather than concatenated: the merge alternates
        // between the sources and drops repeats by fingerprint, so the per-runner
        // line cap in TorRunner takes an even slice of vanilla, obfs4 and
        // webtunnel instead of the first hundred vanilla bridges.
        string? combinedLines = null;
        if (mode == TransportCombined)
        {
            combinedLines = MergeBridgeLists(CombinedSources
                .Where(fetched.ContainsKey)
                .Select(s => fetched[s])
                .ToList());
        }
        if (combinedLines != null)
        {
            var n = SplitLines(combinedLines).Count(l => !string.IsNullOrWhiteSpace(l));
            AppLog.I(TAG, $"Combined-Bridge: {n} bridge(s) from {string.Join("/", CombinedSources)}");
            AppLog.Transport(sessionId, TransportCombined, 'I', TAG, $"combined list: {n} bridge(s)");
        }
        var lines = combinedLines != null
            ? new Dictionary<string, string>(fetched, StringComparer.Ordinal)
                { [TransportCombined] = combinedLines }
            : fetched;

        // The memory runner, in the two shapes it can take:
        //  - auto with several transports: one mixed runner over every pool
        //  - a single selected transport: a twin of that very transport
        // Direct and custom mode have no memory twin. With runMemory off there
        // is no memory runner in any shape.
        string? singleTransport =
            mode == TransportAuto && autoNames.Count == 1 ? autoNames[0] :
            TwinnedModes.Contains(mode) ? mode : null;
        var mixedMemoryLines =
            mode == TransportAuto && autoNames.Count > 1 && runMemory
                ? BridgeMemory.BridgeLinesFor(lines)
                : null;
        var twinMemoryLines =
            runMemory && singleTransport != null
                ? BridgeMemory.BridgeLinesFor(lines, singleTransport)
                : null;
        var twinName = singleTransport != null ? MemoryNameFor(singleTransport) : null;
        if (mixedMemoryLines != null)
        {
            var n = SplitLines(mixedMemoryLines).Count(l => !string.IsNullOrWhiteSpace(l));
            AppLog.I(TAG, $"Memory runner: {n} proven bridge(s) available");
            AppLog.Transport(sessionId, TransportMemory, 'I', TAG, $"reusing {n} previously proven bridge(s)");
        }
        if (twinMemoryLines != null && twinName != null)
        {
            var n = SplitLines(twinMemoryLines).Count(l => !string.IsNullOrWhiteSpace(l));
            AppLog.I(TAG, $"Memory twin: {n} proven {singleTransport} bridge(s) for {twinName}");
            AppLog.Transport(sessionId, twinName, 'I', TAG,
                $"reusing {n} previously proven {singleTransport} bridge(s)");
        }
        if (!runMemory)
        {
            // Only the runners are off. The runner that does start still reads
            // its own log and still records what worked, so the pools keep
            // filling from this connect; only the chance to connect through a
            // remembered bridge is gone.
            AppLog.I(TAG, "Memory runner: off by choice, racing the selected mode only");
            AppLog.I(TAG, "Memory runner: proven bridges from this connect are still recorded");
        }
        else if (mixedMemoryLines == null && twinMemoryLines == null)
        {
            AppLog.I(TAG, "Memory runner: nothing proven yet for this mode, racing without it");
        }

        // basePort belongs to the app's bridge, so the runners start above it.
        var allPorts = RunnerPorts(basePort);
        var plans = new List<KeyValuePair<string, string>>();
        void Plan(string name) =>
            plans.Add(new KeyValuePair<string, string>(name, lines.TryGetValue(name, out var l) ? l : ""));
        switch (mode)
        {
            case TransportAuto:
                foreach (var name in autoNames) Plan(name);
                break;
            case TransportFresh:
            case TransportCombined:
            case TransportVanilla:
            case TransportObfs4:
            case TransportWebtunnel:
            case TransportSnowflake:
            case TransportCustom:
                Plan(mode);
                break;
            case TransportDirect:
                plans.Add(new KeyValuePair<string, string>(TransportDirect, ""));
                break;
        }
        if (mixedMemoryLines != null)
        {
            plans.Add(new KeyValuePair<string, string>(TransportMemory, mixedMemoryLines));
        }
        if (twinMemoryLines != null && twinName != null)
        {
            plans.Add(new KeyValuePair<string, string>(twinName, twinMemoryLines));
        }

        if (plans.Count == 0)
        {
            StopAll();
            throw new InvalidOperationException($"No bridges available for {mode}");
        }

        // Every attempt gets its own order, per runner -- except the memory
        // runners, which keep the order the pool already has. The order means
        // nothing to Tor, which tries them in turn until one works, but it means
        // something to the network: the first bridge in the list is the one every
        // attempt reaches first, so a list whose order never changes means every
        // attempt starts in the same place. A new order each time gives the whole
        // list its turn over a few attempts.
        var preparedPlans = plans.Select(p => new KeyValuePair<string, string>(
            p.Key,
            IsMemoryRunner(p.Key) ? MemoryRunnerLinesOf(p.Value) : Shuffled(p.Value))).ToList();

        var planNames = plans.Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
        _lastPlans = plans.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        _lastPorts = allPorts.Where(kv => planNames.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        var gen = BeginRunners();

        foreach (var (name, bridgeLines) in preparedPlans)
        {
            var runner = new TorRunner(name, allPorts[name], bridgeLines);
            lock (RunnersLock)
            {
                // Copy-on-write: a reader iterating a published snapshot never
                // sees the dictionary mutate under it.
                var copy = new Dictionary<string, TorRunner>(_runners, StringComparer.Ordinal);
                copy[name] = runner;
                _runners = copy;
            }
            var bridgeCount = SplitLines(bridgeLines).Count(l => !string.IsNullOrWhiteSpace(l));
            AppLog.Transport(sessionId, name, 'I', TAG, $"starting ({bridgeCount} bridges)");
            var error = runner.Start();
            if (error != null)
            {
                var reason = error.Message.Length > 0 ? error.Message : "failed to start";
                runner.Failed = reason;
                AppLog.Transport(sessionId, name, 'E', TAG, $"failed to start: {reason}");
                AppLog.E(TAG, $"{name} failed to start: {reason}");
            }
            else
            {
                AppLog.Transport(sessionId, name, 'I', TAG, "tor + transport started");
            }
        }

        // Runners already proven in an earlier poll, so two runners finishing
        // in the same tick both contribute their bridges to the memory.
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, TorRunner> lastSeen =
            new Dictionary<string, TorRunner>(StringComparer.Ordinal);

        var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + RaceTimeoutMs;
        try
        {
            while (true)
            {
                CheckGeneration(gen);
                lock (RunnersLock)
                {
                    lastSeen = _runners;
                }
                onProgress?.Invoke(lastSeen);

                // Mark runners whose Tor process died before completing. Read
                // the real reason from their own per-transport log tail first.
                foreach (var r in lastSeen.Values)
                {
                    if (r.Failed == null && r.Started && !r.IsReady() && !r.IsRunning())
                    {
                        var reason = r.FailureSummary();
                        r.Failed = reason;
                        AppLog.W(TAG, $"{r.Name} exited early: {reason}");
                        AppLog.Transport(sessionId, r.Name, 'E', TAG, $"tor died: {reason}");
                    }
                }

                // Every runner that reached 100% teaches the memory its working
                // bridges there and then, so the win is banked before the losers
                // are stopped rather than at the end of the race.
                foreach (var r in lastSeen.Values.Where(r => r.IsReady() && recorded.Add(r.Name)))
                {
                    RecordMemory(sessionId, r);
                }

                var winner = lastSeen.Values.FirstOrDefault(r => r.IsReady());
                if (winner != null)
                {
                    AppLog.I(TAG, $"Winner: {winner.Name} at {winner.Progress()}%");
                    AppLog.Transport(sessionId, winner.Name, 'I', TAG, "*** WINNER *** bootstrapped 100%");
                    foreach (var loser in lastSeen.Values.Where(r => !ReferenceEquals(r, winner)))
                    {
                        AppLog.I(TAG, $"Stopping losing transport: {loser.Name}");
                        if (loser.Failed != null)
                        {
                            AppLog.Transport(sessionId, loser.Name, 'E', TAG, $"lost the race: {loser.Failed}");
                        }
                        loser.Stop();
                    }
                    return winner;
                }

                var live = lastSeen.Values.Count(r => r.Failed == null);
                if (live == 0)
                {
                    var details = string.Join(", ", lastSeen.Values.Select(r => $"{r.Name}={r.Failed}"));
                    foreach (var r in lastSeen.Values)
                    {
                        AppLog.Transport(sessionId, r.Name, 'E', TAG, $"final: {r.Failed}");
                        foreach (var line in r.LogLines().TakeLast(12))
                        {
                            AppLog.Transport(sessionId, r.Name, 'D', TAG, line);
                        }
                    }
                    StopAll();
                    throw new InvalidOperationException($"All transports failed ({details})");
                }

                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
                {
                    StopAll();
                    var minutes = RaceTimeoutMs / 60_000;
                    throw new InvalidOperationException($"No transport reached 100% within {minutes} min");
                }

                Thread.Sleep((int)PollIntervalMs);
            }
        }
        finally
        {
            // Whatever any runner proved is kept, on every way out of here: a
            // winner, a race where every runner failed, a timeout, a
            // cancellation. A fetched bridge descriptor is the proof; the
            // percentage is not. A runner that got two bridges and stalled at
            // 60% knows those two bridges work, and without this they are thrown
            // away with the runner that was stopped for losing.
            foreach (var r in lastSeen.Values.Where(r => recorded.Add(r.Name)))
            {
                RecordMemory(sessionId, r);
            }
        }
    }

    /// <summary>
    /// Store the bridges a runner proved. The proof is always filed under the
    /// real transport, never under the runner name: a `webtunnel-memory` twin
    /// teaches the webtunnel pool, so both the twin and the plain webtunnel
    /// runner benefit next time. The pool is only ever added to; a bridge that
    /// stops working costs a wasted attempt now and then, against a pool capped
    /// at sixty per transport where each connect puts its own proven bridges at
    /// the front.
    /// </summary>
    private static void RecordMemory(int sessionId, TorRunner runner)
    {
        var proven = runner.HealthyBridges();
        if (proven.Count == 0)
        {
            AppLog.Transport(sessionId, runner.Name, 'I', TAG,
                "100% but no bridge descriptor seen, memory unchanged");
            return;
        }
        var transport = BaseTransportOf(runner.Name);
        var added = BridgeMemory.Remember(transport, proven);
        AppLog.Transport(sessionId, runner.Name, 'I', TAG,
            $"memory[{transport}] updated: {proven.Count} working bridge(s), +{added} new " +
            $"(pool {BridgeMemory.Count(transport)})");
        // The stats on screen carry a mem figure, and it was just written, so
        // it moves now rather than at the next app start.
        BridgeStore.RefreshMemory();
    }

    /// <summary>
    /// Bring one transport back after the network dropped under it. A full
    /// <see cref="Race"/> is the wrong tool for a link that just blipped: it
    /// would go back to the bridge lists, race every transport again and hand
    /// the caller a new port. This re-runs just the transport that was carrying
    /// traffic, on the port it already had, with the same lines it had, plus its
    /// memory twin when the pool has something proven. The caller's listener
    /// survives and only needs a repoint.
    ///
    /// Returns the new winner, or throws if it cannot bootstrap in
    /// <see cref="RecoveryTimeoutMs"/>; the caller then falls back to a full
    /// reconnect.
    /// </summary>
    public static TorRunner RestartTransport(
        int basePort,
        int sessionId,
        string name,
        Action<IReadOnlyDictionary<string, TorRunner>>? onProgress = null)
    {
        if (!_lastPlans.TryGetValue(name, out var cached))
        {
            throw new InvalidOperationException($"No cached bridges for {name}");
        }
        if (!_lastPorts.ContainsKey(name))
        {
            throw new InvalidOperationException($"No cached port for {name}");
        }

        // The twin is looked up fresh: the pool may have grown since the race,
        // and a transport that had no twin then can have one now. The mixed
        // auto-mode memory runner is skipped, it already carries every pool.
        var baseName = BaseTransportOf(name);
        var twin = MemoryNameFor(baseName);
        string? twinLines;
        if (twin == name || name == TransportMemory)
        {
            twinLines = null;
        }
        else
        {
            twinLines = BridgeMemory.BridgeLinesFor(
                new Dictionary<string, string>(StringComparer.Ordinal) { [baseName] = cached },
                baseName)
                ?? (_lastPlans.TryGetValue(twin, out var t) ? t : null);
        }

        // A twin that has no port of its own cannot be started. Its port is
        // reserved by RunnerPorts, not by the last race: on the first connect
        // the memory pool is empty, so no twin races and no port is recorded
        // for it, while the race itself fills the pool. Recovery now takes the
        // reserved port instead of throwing.
        var fixedPorts = RunnerPorts(basePort);
        var plans = new List<KeyValuePair<string, string>>
        {
            // Re-shuffled, like any other attempt: the lines that just failed
            // are the ones this restart is least likely to get through first. A
            // memory runner keeps the pool's own order and takes its newest fifty.
            new(name, IsMemoryRunner(name) ? MemoryRunnerLinesOf(cached) : Shuffled(cached))
        };
        if (twinLines != null && fixedPorts.ContainsKey(twin))
        {
            plans.Add(new KeyValuePair<string, string>(twin, MemoryRunnerLinesOf(twinLines)));
        }
        var ports = plans.ToDictionary(
            p => p.Key,
            p => _lastPorts.TryGetValue(p.Key, out var lp) ? lp : fixedPorts[p.Key],
            StringComparer.Ordinal);

        // What this recovery builds becomes the last plan, the same way a race
        // records its own. Without it the twin it just started is unreachable:
        // in auto mode the race never plans a twin at all, yet the twin is the
        // one holding the proven bridges and the one that wins. Merged, not
        // replaced: a transport proven by an earlier race is still a transport
        // this recovery can rebuild.
        _lastPlans = new Dictionary<string, string>(_lastPlans, StringComparer.Ordinal)
            .Concat(plans)
            .GroupBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
        _lastPorts = new Dictionary<string, int>(_lastPorts, StringComparer.Ordinal)
            .Concat(ports)
            .GroupBy(kv => kv.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);

        AppLog.I(TAG,
            $"Recovery: restarting {name} on port {ports[name]}" +
            (plans.Count > 1 ? $" with {twin}" : ""));

        // The dying processes still hold their listeners, and a replacement
        // that binds too early just fails to start, so wait the ports out.
        StopAll();
        foreach (var port in ports.Values)
        {
            AwaitPortFree("127.0.0.1", port);
        }

        var gen = BeginRunners();
        var recorded = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyDictionary<string, TorRunner> lastSeen =
            new Dictionary<string, TorRunner>(StringComparer.Ordinal);
        foreach (var (planName, bridgeLines) in plans)
        {
            var runner = new TorRunner(planName, ports[planName], bridgeLines);
            lock (RunnersLock)
            {
                var copy = new Dictionary<string, TorRunner>(_runners, StringComparer.Ordinal);
                copy[planName] = runner;
                _runners = copy;
            }
            var bridgeCount = SplitLines(bridgeLines).Count(l => !string.IsNullOrWhiteSpace(l));
            AppLog.Transport(sessionId, planName, 'I', TAG, $"recovery restart ({bridgeCount} bridges)");
            var error = runner.Start();
            if (error != null)
            {
                var reason = error.Message.Length > 0 ? error.Message : "failed to start";
                runner.Failed = reason;
                AppLog.Transport(sessionId, planName, 'E', TAG, $"recovery start failed: {reason}");
            }
        }

        var deadline = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + RecoveryTimeoutMs;
        try
        {
            while (true)
            {
                CheckGeneration(gen);
                lock (RunnersLock)
                {
                    lastSeen = _runners;
                }
                onProgress?.Invoke(lastSeen);
                foreach (var r in lastSeen.Values)
                {
                    if (r.Failed == null && r.Started && !r.IsReady() && !r.IsRunning())
                    {
                        r.Failed = r.FailureSummary();
                        AppLog.Transport(sessionId, r.Name, 'E', TAG, $"recovery: tor died: {r.Failed}");
                    }
                }
                foreach (var r in lastSeen.Values.Where(r => r.IsReady() && recorded.Add(r.Name)))
                {
                    RecordMemory(sessionId, r);
                }

                var winner = lastSeen.Values.FirstOrDefault(r => r.IsReady());
                if (winner != null)
                {
                    foreach (var loser in lastSeen.Values.Where(r => !ReferenceEquals(r, winner)))
                    {
                        AppLog.I(TAG, $"Recovery: stopping {loser.Name}");
                        loser.Stop();
                    }
                    AppLog.Transport(sessionId, winner.Name, 'I', TAG,
                        $"*** RECOVERED *** 100% on port {winner.TorSocksPort}");
                    return winner;
                }

                if (lastSeen.Values.All(r => r.Failed != null))
                {
                    var details = string.Join(", ", lastSeen.Values.Select(r => $"{r.Name}={r.Failed}"));
                    StopAll();
                    throw new InvalidOperationException($"Recovery failed ({details})");
                }
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= deadline)
                {
                    StopAll();
                    throw new InvalidOperationException(
                        $"Recovery of {name} did not bootstrap in {RecoveryTimeoutMs / 1000}s");
                }
                Thread.Sleep((int)PollIntervalMs);
            }
        }
        finally
        {
            // As in Race(): the descriptor lines this restart produced are proof
            // whatever became of the restart itself.
            foreach (var r in lastSeen.Values.Where(r => recorded.Add(r.Name)))
            {
                RecordMemory(sessionId, r);
            }
        }
    }

    /// <summary>
    /// Stop and discard every runner. <see cref="TorRunner.Stop"/> blocks until
    /// the processes are really gone, so this returns with no Tor/lyrebird
    /// process of ours left holding a socket.
    /// </summary>
    public static void StopAll()
    {
        foreach (var runner in DetachRunners())
        {
            runner.Stop();
        }
    }

    /// <summary>Take the runner list out of the map so a stop cannot race a new start.</summary>
    private static IReadOnlyList<TorRunner> DetachRunners()
    {
        lock (RunnersLock)
        {
            var copy = _runners.Values.ToList();
            _runners = new Dictionary<string, TorRunner>(StringComparer.Ordinal);
            Interlocked.Increment(ref _generation);
            return copy;
        }
    }

    /// <summary>
    /// Empty the map for a fresh start and take the generation that start owns.
    /// Any poll loop still running on an older generation is watching runners
    /// that no longer exist and is told so instead of failing on its own.
    /// </summary>
    private static long BeginRunners()
    {
        lock (RunnersLock)
        {
            _runners = new Dictionary<string, TorRunner>(StringComparer.Ordinal);
            return Interlocked.Increment(ref _generation);
        }
    }

    private static void CheckGeneration(long gen)
    {
        if (Interlocked.Read(ref _generation) != gen)
        {
            throw new InvalidOperationException("Start was superseded by a stop or a newer connect");
        }
    }

    /// <summary>
    /// Stop everything and wait for every runner port to be released. Used on
    /// disconnect and on a user stop so a following connect never races the
    /// previous teardown. The return value is not just "we sent the signal":
    /// every runner reports whether its processes were seen to exit, and a port
    /// only frees once the kernel has reaped the listener, so a true here means
    /// the cores are really down and the ports are really back.
    /// </summary>
    public static bool StopAllAndWait(int basePort, string host = "127.0.0.1")
    {
        // basePort itself is the app's own bridge port; runners take
        // +1 .. +MaxPortOffset.
        var ports = Enumerable.Range(basePort, MaxPortOffset + 1).ToList();
        var list = DetachRunners();
        foreach (var runner in list)
        {
            runner.Stop();
        }
        var survivors = list.Where(r => !r.ConfirmedStopped).ToList();
        foreach (var s in survivors)
        {
            AppLog.E(TAG, $"runner {s.Name} still has a live process after kill");
        }
        AppLog.I(TAG,
            $"stopped {list.Count} runner(s), {list.Count - survivors.Count} confirmed dead, " +
            $"{survivors.Count} unconfirmed");
        var free = ports.All(p => AwaitPortFree(host, p));
        AppLog.I(TAG, free ? "every port free" : "some ports are still held");
        return free;
    }

    /// <summary>
    /// Read the bridge list for every transport. Preference order: disk cache →
    /// bundled assets → network. A transport backed by several files is merged
    /// by <see cref="MergeBridgeLists"/> into one list.
    /// </summary>
    private static IReadOnlyDictionary<string, string> FetchBridgeLines(HashSet<string>? only)
    {
        var lines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, urls) in BRIDGE_SOURCES)
        {
            if (only != null && !only.Contains(name)) continue;
            var cached = BridgeStore.Lines(name);
            string content;
            if (!string.IsNullOrWhiteSpace(cached))
            {
                content = cached;
            }
            else
            {
                var bundled = ReadBundled(urls);
                if (bundled != null)
                {
                    content = bundled;
                    BridgeStore.SaveLines(name, content);
                }
                else
                {
                    content = DownloadLists(urls);
                    if (!string.IsNullOrWhiteSpace(content)) BridgeStore.SaveLines(name, content);
                }
            }
            lines[name] = content;
        }
        // Let the UI (exit-node ranking, bridge counts) see the cache we wrote.
        BridgeStore.RefreshState();
        return lines;
    }

    /// <summary>
    /// Where the bundled copies ship with the app (Android: assets/bridges/).
    /// Next to the executable; the build copies Windows/assets/bridges there.
    /// </summary>
    private static string BundledDir() =>
        Path.Combine(AppContext.BaseDirectory, BundledAssetDir);

    /// <summary>The bundled copies of every file this transport is built from.</summary>
    private static string? ReadBundled(IReadOnlyList<string> urls)
    {
        var bodies = new List<string>();
        foreach (var url in urls)
        {
            var body = ReadBundledFile(Path.Combine(BundledDir(), BundledAssetName(url)));
            if (body != null) bodies.Add(body);
        }
        var merged = MergeBridgeLists(bodies);
        return string.IsNullOrWhiteSpace(merged) ? null : merged;
    }

    /// <summary>One bundled file, or null when it is missing or empty.</summary>
    private static string? ReadBundledFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            AppLog.I(TAG, $"bundled {Path.GetFileName(path)} ({text.Length} chars)");
            return text;
        }
        catch (Exception e)
        {
            AppLog.W(TAG, $"no bundled {Path.GetFileName(path)}: {e.Message}");
            return null;
        }
    }

    private static string DownloadLists(IReadOnlyList<string> urls)
    {
        var bodies = new List<string>();
        foreach (var url in urls)
        {
            string text;
            try
            {
                text = DownloadText(url);
            }
            catch (Exception e)
            {
                AppLog.E(TAG, $"download failed for {url}: {e.Message}");
                text = "";
            }
            if (!string.IsNullOrWhiteSpace(text)) bodies.Add(text);
        }
        return MergeBridgeLists(bodies);
    }

    private static readonly HttpClient DownloadHttp = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMilliseconds(20_000) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("DeltaTor/1.0");
        return c;
    }

    private static string DownloadText(string url)
    {
        AppLog.I(TAG, $"Downloading {url}");
        using var cts = new CancellationTokenSource(20_000);
        var resp = DownloadHttp.GetAsync(url, cts.Token).GetAwaiter().GetResult();
        if (resp.StatusCode == System.Net.HttpStatusCode.OK)
        {
            return resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        }
        AppLog.E(TAG, $"HTTP {(int)resp.StatusCode} for {url}");
        return "";
    }
}

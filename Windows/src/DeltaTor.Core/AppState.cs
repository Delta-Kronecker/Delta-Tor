namespace DeltaTor.Core;

/// <summary>
/// Shared connection state, observed by the UI and updated by the service
/// engine (Windows counterpart of Android AppState; StateFlow -> plain
/// properties + a <see cref="Changed"/> event that the WinForms UI marshals
/// onto its own thread).
/// </summary>
public static class AppState
{
    /// <summary>What a modal notice is about, so the UI can word itself for the reason.</summary>
    public enum NoticeKind
    {
        /// <summary>
        /// A connect in a mode other than auto stalled, and the app stopped it,
        /// switched to auto and started again by itself. The only automatic
        /// change there is; deliberately not offered to auto itself.
        /// </summary>
        AutoRecovery,

        /// <summary>One-time explainer shown the first time the app is ever opened.</summary>
        FirstRun
    }

    /// <summary>
    /// One block of a notice body, with the direction it has to be laid out in.
    /// A notice can carry more than one script (the first-run explainer is
    /// English, Persian and Russian in the same dialog); each block is laid out
    /// in its own direction, which is the only way the Persian one reads RTL.
    /// </summary>
    /// <param name="Lead">Optional opening line, drawn bolder than the rest.</param>
    public sealed record NoticeBlock(string Text, bool Rtl = false, string Lead = "");

    /// <summary>
    /// A notice the user never asked for and cannot miss. Separate from
    /// <see cref="VpnState.Error"/>: an error belongs to a failed connection and
    /// is cleared by the next one, while these describe an action the app took
    /// on the user's behalf and survive the restart they caused. The id is
    /// monotonic, not a boolean, so the same notice can happen twice.
    /// </summary>
    public sealed record Notice(long Id, NoticeKind Kind, string Title, IReadOnlyList<NoticeBlock> Blocks);

    public sealed record VpnState
    {
        public bool Connecting { get; init; }
        public bool TorRunning { get; init; }
        public bool Connected { get; init; }

        /// <summary>Up but carrying nothing: the link or the circuits are being restored.</summary>
        public bool Reconnecting { get; init; }

        /// <summary>
        /// A stop is in progress: the tunnel is down and every Tor/lyrebird
        /// process is being killed. Nothing may be started while this is set,
        /// because a start would race the teardown for the ports.
        /// </summary>
        public bool Stopping { get; init; }

        public IReadOnlyDictionary<string, int> Transports { get; init; } = EmptyMap;
        public string Transport { get; init; } = "";
        public string? Error { get; init; }

        /// <summary>
        /// A modal notice awaiting acknowledgement. Not cleared by a connect or
        /// a teardown: see <see cref="Notice"/>.
        /// </summary>
        public Notice? Notice { get; init; }

        public long TxBytes { get; init; }
        public long RxBytes { get; init; }
        public float TxSpeed { get; init; }
        public float RxSpeed { get; init; }
        public long ConnectedAtMillis { get; init; }
        public string ExitCode { get; init; } = "";
        public string ExitName { get; init; } = "";
        public string ExitIp { get; init; } = "";

        /// <summary>
        /// Set while proxy mode is on and Tor is up, as the address to configure
        /// a client with: "socks5://127.0.0.1:9050". There is no tunnel to imply
        /// it, so it is carried explicitly.
        /// </summary>
        public string SocksEndpoint { get; init; } = "";
    }

    public sealed record BridgeState
    {
        public bool Updating { get; init; }
        public long LastUpdateMillis { get; init; }
        public int Vanilla { get; init; }
        public int Obfs4 { get; init; }
        public int Webtunnel { get; init; }
        public int Snowflake { get; init; }
        public int Fresh { get; init; }
        public int Combined { get; init; }

        /// <summary>Bridges remembered per transport by BridgeMemory.</summary>
        public IReadOnlyDictionary<string, int> Memory { get; init; } = EmptyMap;

        public string? Error { get; init; }
    }

    public sealed record ReleaseState
    {
        public bool Checking { get; init; }
        public string LatestVersion { get; init; } = "";
        public string LatestUrl { get; init; } = "";
        public bool Newer { get; init; }
    }

    private static readonly IReadOnlyDictionary<string, int> EmptyMap = new Dictionary<string, int>();

    private static readonly object Lock = new();

    private static VpnState _state = new();
    private static BridgeState _bridgeState = new();
    private static ReleaseState _releaseState = new(checking: true);
    private static string _mode = "";
    private static volatile bool _vpnStarted;

    /// <summary>Raised after every state change (already outside the lock).</summary>
    public static event Action? Changed;

    public static VpnState State
    {
        get { lock (Lock) return _state; }
    }

    /// <summary>The connection mode the app is really using (not always the drawer's).</summary>
    public static string Mode
    {
        get { lock (Lock) return _mode; }
    }

    public static void SetMode(string mode)
    {
        bool changed;
        lock (Lock)
        {
            changed = _mode != mode;
            if (changed) _mode = mode;
        }
        if (changed) Changed?.Invoke();
    }

    public static BridgeState BridgeState
    {
        get { lock (Lock) return _bridgeState; }
    }

    public static ReleaseState ReleaseState
    {
        get { lock (Lock) return _releaseState; }
    }

    /// <summary>Whether the tunnel service is currently started.</summary>
    public static bool VpnStarted
    {
        get => _vpnStarted;
        private set => _vpnStarted = value;
    }

    private static long _noticeSeq;

    /// <summary>
    /// Raise a notice and return the id the UI has to acknowledge. Replacing an
    /// unacknowledged notice rather than queueing is deliberate: the old one
    /// described a connect attempt that no longer exists.
    /// </summary>
    public static long PostNotice(NoticeKind kind, string title, string body) =>
        PostNoticeBlocks(kind, title, new[] { new NoticeBlock(body) });

    /// <summary>As <see cref="PostNotice"/>, for a body made of blocks laid out in different directions.</summary>
    public static long PostNoticeBlocks(NoticeKind kind, string title, IReadOnlyList<NoticeBlock> blocks)
    {
        var id = Interlocked.Increment(ref _noticeSeq);
        lock (Lock)
        {
            _state = _state with { Notice = new Notice(id, kind, title, blocks) };
        }
        Changed?.Invoke();
        return id;
    }

    /// <summary>
    /// Acknowledge a notice. The id is checked so a dismissal that arrives
    /// after a newer notice replaced this one cannot take that newer one down.
    /// </summary>
    public static void ClearNotice(long id)
    {
        bool changed = false;
        lock (Lock)
        {
            if (_state.Notice?.Id == id)
            {
                _state = _state with { Notice = null };
                changed = true;
            }
        }
        if (changed) Changed?.Invoke();
    }

    public static void MarkStarted()
    {
        VpnStarted = true;
    }

    public static void MarkStopped()
    {
        VpnStarted = false;
        // Teardown resets the whole state, and three things have to survive it:
        // the stop flag (keeps the UI locked until the cores are confirmed
        // gone), the error (the only explanation for a connection that failed on
        // its own), and the notice (a recovery notice is posted *before* its
        // retry runs and that retry is allowed to fail).
        lock (Lock)
        {
            _state = new VpnState
            {
                Stopping = _state.Stopping,
                Error = _state.Error,
                Notice = _state.Notice
            };
        }
        Changed?.Invoke();
    }

    public static void Update(Func<VpnState, VpnState> block)
    {
        lock (Lock)
        {
            _state = block(_state);
        }
        Changed?.Invoke();
    }

    public static void UpdateBridge(Func<BridgeState, BridgeState> block)
    {
        lock (Lock)
        {
            _bridgeState = block(_bridgeState);
        }
        Changed?.Invoke();
    }

    public static void UpdateRelease(Func<ReleaseState, ReleaseState> block)
    {
        lock (Lock)
        {
            _releaseState = block(_releaseState);
        }
        Changed?.Invoke();
    }
}

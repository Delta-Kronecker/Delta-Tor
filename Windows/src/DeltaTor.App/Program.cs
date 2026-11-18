using DeltaTor.Core;
using DeltaTor.Core.Util;
using DeltaTor.Native;

namespace DeltaTor.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Portable first: any data an earlier (AppData-based) build left
        // behind is copied next to the exe before anything reads a path.
        AppPaths.MigrateLegacy();

        // Startup sequence, 1:1 with Android DeltaTorApp.onCreate. Config.Init
        // also syncs AppLog.Enabled = Config.LoggingEnabled, which Android does
        // on the line right after Config.init.
        Config.Init();
        ExitNodes.Init();
        // Android ExitNodes.loadDirectory (MainActivity 157): the capacity
        // table and the picker list both arrive in the background.
        ExitCapacityIndex.Load();
        Task.Run(BridgeCountries.TopSync);
        // Notification channels (Android createNotificationChannels) have no
        // Windows equivalent; the tray/toast surface initializes with the UI.
        BridgeStore.RefreshState();
        Task.Run(BridgeStore.AutoUpdateIfStale);
        Task.Run(ReleaseChecker.Check);
        Task.Run(InstallCounter.CountInstall);
        // Proxy chain: restore the persisted VPN Gate pool, then refresh it if
        // the day has turned and keep the daily rhythm while the app is open
        // (Android's ProxyChainWorker + scheduleProxyChainDailyUpdate).
        ProxyChainManager.Init();
        _ = Task.Run(ProxyChainManager.UpdateIfNeededAsync);
        _ = Task.Run(() => ProxyChainManager.DailyLoopAsync());

        // The tun2socks engine behind TunnelEngine: Se7enTun owns the Wintun
        // adapter, its catch-all routes, adapter DNS and split rules, and tears
        // them down on a clean exit. A hard kill is recovered by Se7enTun's own
        // reconcile on the next start (it caches and restarts its data plane).
        TunnelEngine.Impl = new Se7enTunnel();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TunnelEngine.Stop();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => TunnelEngine.Stop();
        // The DNS relay holds every adapter's resolver host, so it must come
        // down with the process too: leaving the machine pointed at a loopback
        // port nobody listens on would take name resolution down with it.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => TorDns.Stop();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => TorDns.Stop();
        // Close the recorded-log files on a clean exit. AutoFlush already put
        // every line on disk; this just releases the handles.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => LogFiles.Shutdown();

        // The chain's data plane: a dedicated Wintun adapter driven over the
        // raw Wintun API (the counterpart of Android's VpnService.Builder).
        // Null here would make every chain connect report "adapter unavailable".
        SstpTun.Factory = () => new WintunTun();

        // The language picker runs before the first-run notice and after the
        // engine is wired up: a fresh install stores no language, and neither
        // does an update of an install that predates the key (its first-run
        // flag is long set). Neither notice can be worded until that choice
        // exists, and the picker must not sit in the middle of engine startup.
        if (!Config.LanguageChosen)
        {
            using var picker = new LanguageDialog();
            picker.ShowDialog();
        }
        ShowStartupNotices();

        Application.Run(new MainForm());
    }

    /// <summary>
    /// Show whichever onboarding notice is due after the language is known.
    ///
    /// A fresh install gets the first-run explainer; an install that already
    /// saw it (including an update from before the route-speed tip existed)
    /// gets the tip directly. When the first-run notice is the one posted, the
    /// tip follows when that dialog is dismissed, so the two are never on
    /// screen together and neither replaces the other.
    /// </summary>
    private static void ShowStartupNotices()
    {
        if (!Config.FirstRunNoticeShown)
        {
            MaybeShowFirstRunNotice();
            return;
        }
        MaybeShowRouteSpeedNotice();
    }

    /// <summary>MainActivity.maybeShowFirstRunNotice (line 195).</summary>
    private static void MaybeShowFirstRunNotice()
    {
        if (Config.FirstRunNoticeShown) return;
        // The flag is written before the notice is posted rather than when it
        // is dismissed, so a crash between the two cannot nag again next time.
        Config.FirstRunNoticeShown = true;
        AppState.PostNoticeBlocks(NoticeKind.FirstRun, "", FirstRunNoticeBlocks());
    }

    /// <summary>First-run instruction, in the language picked at startup (MainActivity 223).</summary>
    private static IReadOnlyList<NoticeBlock> FirstRunNoticeBlocks() => NoticeLocalization.Pick(
        english: new NoticeBlock(
            Text: "After every successful connection, DeltaTor remembers the connection " +
                "paths it used on your network, and after about 2 to 3 connections the time " +
                "a connect takes comes down.",
            Lead: "Please be patient on your first connection"),
        russian: new NoticeBlock(
            Text: "После каждого успешного подключения DeltaTor запоминает пути подключения, " +
                "которые он использовал в вашей сети, и после примерно 2-3 подключений время, " +
                "необходимое для подключения, сокращается.",
            Lead: "Пожалуйста, будьте терпеливы при первом подключении"),
        persian: new NoticeBlock(
            Text: "دلتاتور پس از هر اتصال موفق مسیر های اتصال روی شبکه اینترنت شما را به خاطر می سپارد و پس از حدود 2 یا 3 اتصال، زمان لازم برای اتصال کاهش می یابد.",
            Rtl: true,
            Lead: "لطفاً در اولین اتصال صبور باشید")
    );

    /// <summary>
    /// MainActivity.maybeShowRouteSpeedNotice: the tip that follows the
    /// first-run notice. Internal because the main window posts it when the
    /// first-run dialog closes, so the two are sequential rather than one
    /// replacing the other.
    /// </summary>
    internal static void MaybeShowRouteSpeedNotice()
    {
        if (Config.RouteSpeedNoticeShown) return;
        Config.RouteSpeedNoticeShown = true;
        AppState.PostNoticeBlocks(NoticeKind.RouteSpeed, "", RouteSpeedNoticeBlocks());
    }

    private static IReadOnlyList<NoticeBlock> RouteSpeedNoticeBlocks() => NoticeLocalization.Pick(
        english: new NoticeBlock(
            Lead: "The best route is chosen every 30 seconds",
            Text: "While connected, DeltaTor checks the connection paths every 30 seconds and " +
                "uses the best ones, so download and upload speeds go up."),
        russian: new NoticeBlock(
            Lead: "Лучший маршрут выбирается каждые 30 секунд",
            Text: "Во время подключения DeltaTor каждые 30 секунд проверяет маршруты " +
                "подключения и использует самые быстрые из них, поэтому скорость загрузки и " +
                "выгрузки возрастает."),
        persian: new NoticeBlock(
            Lead: "هر 30 ثانیه بهترین مسیر انتخاب می‌شود",
            Text: "پس از اتصال، دلتاتور هر 30 ثانیه مسیرهای اتصال را بررسی می‌کند و از " +
                "بهترین آن‌ها استفاده می‌کند. به همین دلیل سرعت دانلود و آپلود افزایش می‌یابد.",
            Rtl: true)
    );
}

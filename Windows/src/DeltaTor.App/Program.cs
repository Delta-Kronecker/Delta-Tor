using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using DeltaTor.Core;
using DeltaTor.Core.Util;
using DeltaTor.Native;

namespace DeltaTor.App;

internal static class Program
{
    /// <summary>
    /// The folder next to the exe that holds everything except the program
    /// itself: the other managed assemblies, the Tor bundle, the bundled
    /// assets, the Wintun data plane and the app's own state.
    /// </summary>
    private const string DataFolderName = "data";

    /// <summary>Absolute path of <see cref="DataFolderName"/>; set once at startup.</summary>
    private static string _dataDir = "";

    [STAThread]
    private static void Main(string[] args)
    {
        // Deliberately System-only: the JIT resolves what a method body
        // references *before* its first line runs, and every DeltaTor
        // assembly lives in data/ — reachable only after the resolver below
        // is installed. Splitting the startup is what keeps that ordering
        // true, so this method may not touch any type from the app.
        PrepareAppDirectory();
        Run(args);
    }

    /// <summary>
    /// Point the app at &lt;exe&gt;\data and make the runtime find the assemblies
    /// and the one native library that live there. System-only by design (see
    /// <see cref="Main"/>).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PrepareAppDirectory()
    {
        var exeDir = AppContext.BaseDirectory;
        var dataDir = Path.Combine(exeDir, DataFolderName);
        var usable = true;
        try
        {
            Directory.CreateDirectory(dataDir);
        }
        catch
        {
            // A read-only install (an exe dropped somewhere the user cannot
            // write) keeps the old layout rather than failing to start.
            usable = false;
        }
        if (!usable) dataDir = exeDir;
        // A new exe dropped into an old (pre-data) install, with no data/ next
        // to it: keep the old layout instead of pointing the app at a folder
        // that does not have its assemblies.
        if (usable && !File.Exists(Path.Combine(dataDir, "DeltaTor.Core.dll")))
            dataDir = exeDir;
        _dataDir = dataDir;

        // AppContext.BaseDirectory is the one lever every path helper in the
        // app reads (AppPaths.Root, TorBinaries, AppAssets, LogFiles, and
        // Se7enTun's Resources lookup), so moving it moves all of them.
        // Keep the trailing separator the runtime normally reports.
        AppContext.SetData(
            "APP_CONTEXT_BASE_DIRECTORY", dataDir + Path.DirectorySeparatorChar);

        // The host probes assemblies relative to the exe; they ship in data.
        // The default probe failing is what raises this event.
        AssemblyLoadContext.Default.Resolving += ResolveFromData;

        // wintun.dll is the only native file loaded by name. Preloading it from
        // data makes the later LoadLibrary("wintun.dll") resolve to the module
        // already in memory; SetDllDirectory covers anything a dependency
        // pulls in by name.
        try
        {
            SetDllDirectory(dataDir);
        }
        catch
        {
            // Best effort; the preload below is the primary path.
        }
        TryPreload(Path.Combine(dataDir, "wintun.dll"));

        // Updating by extracting over an older portable install leaves its
        // DLLs next to the exe, and the host's probe there runs before the
        // resolver above: a stale DeltaTor.Core.dll would win over the one in
        // data and fail with a version mismatch. The copies in data are the
        // ones that belong to this build, so remove the duplicates.
        if (!string.Equals(
                Path.TrimEndingDirectorySeparator(dataDir),
                Path.TrimEndingDirectorySeparator(exeDir),
                StringComparison.OrdinalIgnoreCase))
        {
            RemoveStaleRootAssemblies(exeDir, dataDir);
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Assembly? ResolveFromData(AssemblyLoadContext context, AssemblyName name)
    {
        if (string.IsNullOrEmpty(name.Name)) return null;
        var path = Path.Combine(_dataDir, name.Name + ".dll");
        return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
    }

    private static void TryPreload(string path)
    {
        try
        {
            if (File.Exists(path)) NativeLibrary.Load(path);
        }
        catch
        {
            // The P/Invoke still gets a chance through the DLL search path.
        }
    }

    private static void RemoveStaleRootAssemblies(string exeDir, string dataDir)
    {
        try
        {
            foreach (var dll in Directory.EnumerateFiles(exeDir, "*.dll"))
            {
                var shipped = Path.Combine(dataDir, Path.GetFileName(dll));
                if (!File.Exists(shipped)) continue;
                try { File.Delete(dll); }
                catch { /* locked by something else; the resolver is the fallback */ }
            }
        }
        catch
        {
            // Best effort: a failure here only affects an in-place upgrade.
        }
    }

    // SetDllDirectoryW adds one directory to the native search path used by
    // LoadLibrary for the rest of the process.
    [DllImport("kernel32.dll", EntryPoint = "SetDllDirectoryW",
        CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectory(string path);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Run(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Crash reporting, installed before any window exists so nothing escapes
        // it. A fatal exception is persisted first and turned into a "send the
        // log" prompt on the next launch; an exception on the UI thread is
        // reported in place, while the app is still alive to show the dialog.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            LogReporter.ReportCrash(e.Exception, "UI thread");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            LogReporter.WriteCrashFile(e.ExceptionObject as Exception, "AppDomain");

        // One-time move of the state an older build kept next to the exe: data/
        // is the root now, and a plain upgrade must not look like a fresh
        // install. Before MigrateLegacy, so the AppData copy cannot shadow it.
        AppPaths.MigratePortableLayout();
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
    /// saw it gets the next unseen tip directly. When the first-run notice is
    /// the one posted, the rest follow as each dialog is dismissed (see
    /// <see cref="MaybeShowNextStartupNotice"/>), so they never replace each
    /// other and none is skipped.
    /// </summary>
    private static void ShowStartupNotices()
    {
        if (!Config.FirstRunNoticeShown)
        {
            MaybeShowFirstRunNotice();
            return;
        }
        MaybeShowNextStartupNotice();
    }

    /// <summary>
    /// Post the next onboarding notice in order, if any is still unseen.
    /// Internal because the main window calls it when a notice dialog closes:
    /// that keeps the tips sequential instead of stacking them.
    /// </summary>
    internal static void MaybeShowNextStartupNotice()
    {
        MaybeShowTunNotice();
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
    /// The last of the startup tips: the default mode is now the full Tun
    /// tunnel, which carries every app; System Proxy is the lighter opt-in for
    /// users who only want the apps that use the Windows system proxy routed.
    /// Internal for the same reason as the other tips.
    /// </summary>
    internal static void MaybeShowTunNotice()
    {
        if (Config.TunNoticeShown) return;
        Config.TunNoticeShown = true;
        AppState.PostNoticeBlocks(NoticeKind.TunMode, "", TunNoticeBlocks());
    }

    private static IReadOnlyList<NoticeBlock> TunNoticeBlocks() => NoticeLocalization.Pick(
        english: new NoticeBlock(
            Lead: "DeltaTor now connects as a full Tun tunnel",
            Text: "By default DeltaTor connects in Tun mode: it opens a virtual network " +
                "adapter and routes every app on your PC through Tor, the way a VPN does. " +
                "If you would rather route only the apps that use the Windows system " +
                "proxy, such as your browser and Telegram, switch to System Proxy mode " +
                "in Settings. The new mode takes effect on the next connect."),
        russian: new NoticeBlock(
            Lead: "DeltaTor теперь подключается как полный туннель Tun",
            Text: "По умолчанию DeltaTor подключается в режиме Tun: он создаёт виртуальный " +
                "сетевой адаптер и направляет через Tor трафик всех приложений на " +
                "компьютере — как обычный VPN. Если вы хотите направлять через DeltaTor " +
                "только приложения, использующие системный прокси, — браузер и Telegram, — " +
                "переключитесь в настройках на режим System Proxy. Новый режим вступит в " +
                "силу при следующем подключении."),
        persian: new NoticeBlock(
            Lead: "دلتاتور اکنون به صورت تانل کامل Tun وصل م‌شود",
            Text: "دلتاتور به صورت پیش‌فرض روی حالت Tun وصل م‌شود: یک آداپتور شبکه مجازی " +
                "می‌سازد و ترافیک همه برنامه‌های کامپیوتر را مانند یک VPN از تور عبور " +
                "می‌دهد. اگر می‌خواهید فقط برنامه‌هایی که از پروکسی سیستم استفاده می‌کنند " +
                "(مرورگر و تلگرام) از دلتاتور عبور کنند، از تنظیمات حالت System Proxy را " +
                "انتخاب کنید. حالت جدید از اتصال بعدی اعمال می‌شود.",
            Rtl: true)
    );
}

using DeltaTor.Core;

namespace DeltaTor.App;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

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
        MaybeShowFirstRunNotice();

        Application.Run(new MainForm());
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

    /// <summary>First-run instruction, shown in all three languages at once (MainActivity 208).</summary>
    private static IReadOnlyList<NoticeBlock> FirstRunNoticeBlocks() => new[]
    {
        // Persian first. It is the app's own language and the one this notice
        // was written in; the other two are there because the people who need
        // it are not all reading the same script.
        new NoticeBlock(
            Text: "دلتاتور پس از هر اتصال موفق مسیر های اتصال روی شبکه اینترنت شما را به خاطر می سپارد و پس از حدود 2 یا 3 اتصال، زمان لازم برای اتصال کاهش می یابد.",
            Rtl: true,
            Lead: "لطفاً در اولین اتصال صبور باشید"),
        new NoticeBlock(
            Text: "After every successful connection, DeltaTor remembers the connection " +
                "paths it used on your network, and after about 2 to 3 connections the time " +
                "a connect takes comes down.",
            Lead: "Please be patient on your first connection"),
        new NoticeBlock(
            Text: "После каждого успешного подключения DeltaTor запоминает пути подключения, " +
                "которые он использовал в вашей сети, и после примерно 2-3 подключений время, " +
                "необходимое для подключения, сокращается.",
            Lead: "Пожалуйста, будьте терпеливы при первом подключении")
    };
}

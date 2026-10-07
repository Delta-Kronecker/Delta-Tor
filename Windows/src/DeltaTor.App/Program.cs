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
        // Notification channels (Android createNotificationChannels) have no
        // Windows equivalent; the tray/toast surface initializes with the UI.
        BridgeStore.RefreshState();
        Task.Run(BridgeStore.AutoUpdateIfStale);
        Task.Run(ReleaseChecker.Check);
        Task.Run(InstallCounter.CountInstall);

        Application.Run(new MainForm());
    }
}

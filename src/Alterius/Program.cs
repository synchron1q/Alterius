using Alterius.Core;
using Alterius.UI;
using Alterius.Upload;

namespace Alterius;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\Alterius_SingleInstance_Mutex";

    [STAThread]
    private static void Main()
    {
        using var instanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "Alterius уже запущен в системном трее.",
                "Alterius",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

        var logger = new FileLogger();
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            logger.Error("Program", "Unhandled AppDomain exception.", e.ExceptionObject as Exception);
        };
        Application.ThreadException += (_, e) =>
        {
            logger.Error("Program", "Unhandled UI thread exception.", e.Exception);
        };

        logger.Info("Program", "Alterius starting up.");

        var configManager = new ConfigManager(logger);
        logger.SetRetentionDays(configManager.Current.LogRetentionDays);

        var localQueue = new LocalQueue(logger);
        using var connectionManager = new SmbConnectionManager(logger);
        var uploader = new SmbUploader(logger, connectionManager);
        using var uploadWorker = new SmbUploadWorker(configManager, localQueue, connectionManager, uploader, logger);
        using var coordinator = new RecordingCoordinator(configManager, localQueue, uploadWorker, logger);
        using var trayApp = new TrayApp(configManager, coordinator, uploadWorker, connectionManager, localQueue, logger);

        Application.Run(trayApp);
        logger.Info("Program", "Alterius exited cleanly.");

        GC.KeepAlive(instanceMutex);
    }
}

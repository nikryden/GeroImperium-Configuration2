using GeroImperium.Core.Protocol;

namespace GeroImperium.Service;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var watcher = new DeviceWatcher();
        using var deviceManager = new DeviceConnectionManager(watcher);
        var pipeServer = new PipeServer(deviceManager);
        using var pipeServerCts = new CancellationTokenSource();

        using var trayIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application,
            Visible = true,
            Text = "GeroImperium Sync Service -- not connected",
        };

        deviceManager.StatusChanged += (_, _) =>
        {
            trayIcon.Text = deviceManager.IsConnected
                ? $"GeroImperium Sync Service -- connected on {deviceManager.PortName}"
                : "GeroImperium Sync Service -- not connected";
        };

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Exit", null, (_, _) => Application.Exit());
        trayIcon.ContextMenuStrip = trayMenu;

        watcher.Start();
        _ = pipeServer.RunAsync(pipeServerCts.Token);

        Application.Run();

        pipeServerCts.Cancel();
        trayIcon.Visible = false;
    }
}

namespace GeroImperium.Service;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var deviceManager = new DeviceConnectionManager();
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
            trayIcon.Text = deviceManager switch
            {
                { IsBleConnected: true, IsAppLaunchSubscribed: true } => "GeroImperium Sync Service -- connected (App-Launch active)",
                { IsBleConnected: true } => "GeroImperium Sync Service -- connected (App-Launch unavailable)",
                _ => "GeroImperium Sync Service -- not connected",
            };
        };

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("Exit", null, (_, _) => Application.Exit());
        trayIcon.ContextMenuStrip = trayMenu;

        _ = pipeServer.RunAsync(pipeServerCts.Token);

        Application.Run();

        pipeServerCts.Cancel();
        trayIcon.Visible = false;
        deviceManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

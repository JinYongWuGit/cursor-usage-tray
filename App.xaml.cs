using System.Windows;
using System.Windows.Threading;

namespace UsageTray;

public partial class App : System.Windows.Application
{
    private readonly CursorUsageClient usageClient = new();

    private MainWindow? mainWindow;
    private TrayIconService? trayIconService;
    private DispatcherTimer? refreshTimer;
    private UserSettings userSettings = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 1. Load user settings (refresh interval, window opacity, start with windows)
        userSettings = UserSettings.Load();

        // Sync registry with saved user settings if necessary
        if (userSettings.StartWithWindows != AutoStartService.IsAutoStartEnabled())
        {
            AutoStartService.SetAutoStart(userSettings.StartWithWindows);
        }

        mainWindow = new MainWindow
        {
            CurrentOpacity = userSettings.WindowOpacity,
        };
        var viewModel = mainWindow.ViewModel;

        // 2. Try to load cached usage so startup displays last-known numbers instantly (< 1ms).
        var cachedSnapshot = UsageCache.TryLoad();
        if (cachedSnapshot is not null)
        {
            viewModel.ApplyDetailedUsage(cachedSnapshot);
        }

        // 3. Initialize tray icon service.
        var initialInterval = TimeSpan.FromMinutes(userSettings.RefreshIntervalMinutes);
        trayIconService = new TrayIconService(
            iconText: viewModel.IconText,
            tooltipText: viewModel.SummaryText,
            initialRefreshInterval: initialInterval,
            initialOpacity: userSettings.WindowOpacity,
            initialStartWithWindows: userSettings.StartWithWindows,
            showWindow: mainWindow.ShowUsage,
            exitApplication: Shutdown,
            onRefreshIntervalSelected: interval =>
            {
                if (refreshTimer is not null)
                {
                    refreshTimer.Interval = interval;
                }
                userSettings.RefreshIntervalMinutes = Math.Max(1, (int)Math.Round(interval.TotalMinutes));
                userSettings.Save();
            },
            onOpacitySelected: opacity =>
            {
                if (mainWindow is not null)
                {
                    mainWindow.CurrentOpacity = opacity;
                }
                userSettings.WindowOpacity = opacity;
                userSettings.Save();
            },
            onStartWithWindowsToggled: enabled =>
            {
                userSettings.StartWithWindows = enabled;
                userSettings.Save();
                AutoStartService.SetAutoStart(enabled);
            });

        refreshTimer = new DispatcherTimer { Interval = initialInterval };
        refreshTimer.Tick += async (_, _) => await RefreshUsageAsync(viewModel);
        refreshTimer.Start();

        _ = RefreshUsageAsync(viewModel);
    }

    private async Task RefreshUsageAsync(UsageDisplayViewModel viewModel)
    {
        var snapshot = await usageClient.GetUsageAsync();
        if (snapshot is null)
        {
            // Cursor not installed/logged in, or the dashboard API failed; keep showing the last known values.
            return;
        }

        viewModel.ApplyDetailedUsage(snapshot);
        UsageCache.Save(snapshot);
        trayIconService?.UpdateAmount(viewModel.IconText, viewModel.SummaryText);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        refreshTimer?.Stop();
        trayIconService?.Dispose();
        mainWindow?.CloseFromApplication();
        base.OnExit(e);
    }
}

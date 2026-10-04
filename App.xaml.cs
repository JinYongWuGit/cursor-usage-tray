using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;

namespace CursorUsageTray;

public partial class App : System.Windows.Application
{
    private readonly CursorUsageClient usageClient = new();

    private MainWindow? mainWindow;
    private TrayIconService? trayIconService;
    private DispatcherTimer? refreshTimer;
    private DispatcherTimer? processWatcherTimer;
    private bool? lastCursorRunningState;
    private bool isRefreshingUsage;
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
            showWindow: () =>
            {
                var isRunning = CursorProcessService.IsCursorOrAgentRunning();
                viewModel.IsMonitoringPaused = !isRunning;
                mainWindow.ShowUsage();
            },
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

        // 4. Periodic usage pull timer (e.g. every 1 or 5 minutes while active)
        refreshTimer = new DispatcherTimer { Interval = initialInterval };
        refreshTimer.Tick += async (_, _) => await RefreshUsageAsync(viewModel);
        refreshTimer.Start();

        // 5. Fast 2-second process watcher to instantly detect when Cursor/Agent starts or stops
        processWatcherTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        processWatcherTimer.Tick += async (_, _) => await CheckProcessStatusAsync(viewModel);
        processWatcherTimer.Start();

        // Always pull once on startup after cache is loaded to ensure data is up-to-date,
        // even if no Cursor process is currently running.
        _ = RefreshUsageAsync(viewModel, force: true);
    }

    private async Task CheckProcessStatusAsync(UsageDisplayViewModel viewModel)
    {
        var isRunning = CursorProcessService.IsCursorOrAgentRunning();

        if (lastCursorRunningState != isRunning)
        {
            var previouslyRunning = lastCursorRunningState == true;
            lastCursorRunningState = isRunning;
            viewModel.IsMonitoringPaused = !isRunning;
            trayIconService?.UpdateAmount(viewModel.IconText, viewModel.SummaryText, isPaused: !isRunning);

            if (isRunning && !previouslyRunning)
            {
                Debug.WriteLine("[App] Cursor IDE, CLI, or Agent launched; triggering immediate usage refresh.");
                refreshTimer?.Stop();
                refreshTimer?.Start();
                await RefreshUsageAsync(viewModel, force: true);
            }
            else
            {
                Debug.WriteLine("[App] Cursor IDE, CLI, or Agent exited; monitoring paused.");
            }
        }
    }

    private async Task RefreshUsageAsync(UsageDisplayViewModel viewModel, bool force = false)
    {
        if (isRefreshingUsage) return;
        isRefreshingUsage = true;

        try
        {
            var isCursorRunning = CursorProcessService.IsCursorOrAgentRunning();
            lastCursorRunningState = isCursorRunning;
            viewModel.IsMonitoringPaused = !isCursorRunning;
            trayIconService?.UpdateAmount(viewModel.IconText, viewModel.SummaryText, isPaused: !isCursorRunning);

            if (!force && !isCursorRunning)
            {
                Debug.WriteLine("[App] Cursor IDE, CLI, or Agent process is not active; skipping usage refresh.");
                return;
            }

            var snapshot = await usageClient.GetUsageAsync();
            if (snapshot is null)
            {
                // Cursor not installed/logged in, or the dashboard API failed; keep showing the last known values.
                return;
            }

            viewModel.ApplyDetailedUsage(snapshot);
            UsageCache.Save(snapshot);
            trayIconService?.UpdateAmount(viewModel.IconText, viewModel.SummaryText, isPaused: !isCursorRunning);
        }
        finally
        {
            isRefreshingUsage = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        processWatcherTimer?.Stop();
        refreshTimer?.Stop();
        trayIconService?.Dispose();
        mainWindow?.CloseFromApplication();
        base.OnExit(e);
    }
}

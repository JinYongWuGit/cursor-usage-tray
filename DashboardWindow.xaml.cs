using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace CursorUsageTray;

public partial class DashboardWindow : Window
{
    private static DashboardWindow? currentInstance;
    private readonly CursorUsageClient usageClient = new();
    private DashboardState? currentState;
    private CursorBenchSnapshot? currentBench;
    private bool isRefreshing;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private string? pendingInitialTab;

    public static void ShowDashboard(string? initialTab = null)
    {
        if (currentInstance is not null)
        {
            if (currentInstance.WindowState == WindowState.Minimized)
            {
                currentInstance.WindowState = WindowState.Normal;
            }
            currentInstance.Activate();
            if (!string.IsNullOrEmpty(initialTab))
            {
                currentInstance.SelectTab(initialTab);
            }
            return;
        }

        currentInstance = new DashboardWindow(initialTab);
        currentInstance.Closed += (s, e) => currentInstance = null;
        currentInstance.Show();
    }

    public DashboardWindow(string? initialTab = null)
    {
        pendingInitialTab = initialTab;
        InitializeComponent();
        SetWindowIconSafely();
        Loaded += OnLoaded;
    }

    private void SetWindowIconSafely()
    {
        try
        {
            var exePath = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
            {
                using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
                if (sysIcon != null)
                {
                    Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                        sysIcon.Handle,
                        Int32Rect.Empty,
                        System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load window icon: {ex}");
        }
    }

    public void SelectTab(string? tab)
    {
        if (string.IsNullOrEmpty(tab))
        {
            return;
        }

        pendingInitialTab = tab;
        PostToWebview(new { type = "selectTab", tab });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var userDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorUsageTray", "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await WebView.EnsureCoreWebView2Async(env);

            WebView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            WebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            var htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dashboard", "dashboard.html");
            if (!File.Exists(htmlPath))
            {
                htmlPath = Path.Combine(Directory.GetCurrentDirectory(), "dashboard", "dashboard.html");
            }

            WebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to initialize WebView2: {ex.Message}\n\nPlease ensure Microsoft Edge WebView2 Runtime is installed.",
                "Cursor Usage Dashboard",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            var rawJson = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var tEl) ? tEl.GetString() : null;

            switch (type)
            {
                case "ready":
                    await SendInitialStateAsync();
                    break;

                case "refresh":
                    await RefreshDataAsync();
                    break;

                case "refreshCursorBench":
                    await RefreshCursorBenchAsync();
                    break;

                case "openExternal":
                    if (root.TryGetProperty("url", out var urlEl))
                    {
                        var url = urlEl.GetString();
                        if (!string.IsNullOrEmpty(url))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[DashboardWindow] OpenExternal failed: {ex.Message}");
                            }
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DashboardWindow] WebMessage error: {ex}");
        }
    }

    private async Task SendInitialStateAsync()
    {
        currentBench = await CursorBenchService.GetSnapshotAsync(forceRefresh: false);
        PostToWebview(new { type = "cursorbench", snapshot = currentBench });

        if (!string.IsNullOrEmpty(pendingInitialTab))
        {
            PostToWebview(new { type = "selectTab", tab = pendingInitialTab });
        }

        await RefreshDataAsync();
    }

    private async Task RefreshDataAsync()
    {
        if (isRefreshing) return;
        isRefreshing = true;
        PostToWebview(new { type = "loading", on = true });

        try
        {
            currentState = await usageClient.GetDashboardStateAsync();
            PostToWebview(new { type = "state", state = currentState });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DashboardWindow] Refresh failed: {ex.Message}");
        }
        finally
        {
            PostToWebview(new { type = "loading", on = false });
            isRefreshing = false;
        }
    }

    private async Task RefreshCursorBenchAsync()
    {
        PostToWebview(new { type = "cursorbenchRefreshing", on = true });
        try
        {
            currentBench = await CursorBenchService.GetSnapshotAsync(forceRefresh: true);
            PostToWebview(new { type = "cursorbench", snapshot = currentBench });
        }
        finally
        {
            PostToWebview(new { type = "cursorbenchRefreshing", on = false });
        }
    }

    private void PostToWebview(object message)
    {
        if (WebView.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(message, JsonOpts);
        WebView.CoreWebView2.PostWebMessageAsJson(json);
    }
}

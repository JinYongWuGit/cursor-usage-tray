using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;

namespace UsageTray;

public partial class MainWindow : Window
{
    private bool allowClose;

    public MainWindow()
    {
        InitializeComponent();
        ViewModel = new UsageDisplayViewModel();
        DataContext = ViewModel;
    }

    public UsageDisplayViewModel ViewModel { get; }

    private double currentOpacity = 0.85;

    public double CurrentOpacity
    {
        get => currentOpacity;
        set
        {
            currentOpacity = Math.Max(0.2, Math.Min(1.0, value));
            if (Visibility == Visibility.Visible && Opacity > 0)
            {
                Opacity = currentOpacity;
            }
        }
    }

    public void ShowUsage()
    {
        // Width/Height are only known after layout, so size invisibly first, then reposition before revealing.
        Opacity = 0;
        Show();
        UpdateLayout();
        Left = SystemParameters.WorkArea.Right - ActualWidth - 12;
        Top = SystemParameters.WorkArea.Bottom - ActualHeight - 12;
        Opacity = CurrentOpacity;
        Activate();
    }

    public void CloseFromApplication()
    {
        allowClose = true;
        Close();
    }

    private void OnToggleExpand(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var oldRight = Left + ActualWidth;
        var oldBottom = Top + ActualHeight;

        ViewModel.ToggleExpanded();
        UpdateLayout();

        // Anchor to bottom-right corner so expansion flows smoothly upward and leftward
        Left = oldRight - ActualWidth;
        Top = oldBottom - ActualHeight;
    }

    private void OnDashboardLinkClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        try
        {
            Process.Start(new ProcessStartInfo("https://cursor.com/dashboard/usage")
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to launch dashboard url: {ex}");
        }
    }

    private void OnOpenDashboardWindowClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        DashboardWindow.ShowDashboard();
    }

    private void OnDragHandleMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        DragMove();
    }

    private void OnCloseMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        Hide();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!allowClose)
        {
            e.Cancel = true;
            Hide();
        }
    }
}

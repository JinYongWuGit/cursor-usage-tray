using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CursorUsageTray;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly Action showWindow;
    private readonly Action exitApplication;
    private readonly Action<TimeSpan> onRefreshIntervalSelected;
    private readonly Action<double> onOpacitySelected;
    private readonly Action<TrayIconColor> onColorSelected;
    private readonly ToolStripMenuItem summaryMenuItem;
    private readonly ToolStripMenuItem oneMinuteMenuItem;
    private readonly ToolStripMenuItem fiveMinuteMenuItem;
    private readonly ToolStripMenuItem customIntervalMenuItem;
    private readonly ToolStripMenuItem opacity100MenuItem;
    private readonly ToolStripMenuItem opacity90MenuItem;
    private readonly ToolStripMenuItem opacity85MenuItem;
    private readonly ToolStripMenuItem opacity75MenuItem;
    private readonly ToolStripMenuItem opacity50MenuItem;
    private readonly ToolStripMenuItem customOpacityMenuItem;
    private readonly ToolStripMenuItem colorAutoMenuItem;
    private readonly ToolStripMenuItem colorWhiteMenuItem;
    private readonly ToolStripMenuItem colorBlackMenuItem;
    private readonly ToolStripMenuItem startWithWindowsMenuItem;
    private readonly Dispatcher dispatcher;
    private readonly System.Threading.Timer promotionRetryTimer;
    private int promotionAttempts;
    private int currentIntervalMinutes;
    private double currentOpacity;
    private TrayIconColor currentColor;
    private string lastIconText = "";
    private string lastTooltipText = "";
    private bool lastIsPaused;
    private Icon? renderedIcon;
    private bool disposed;

    public TrayIconService(
        string iconText,
        string tooltipText,
        TimeSpan initialRefreshInterval,
        double initialOpacity,
        bool initialStartWithWindows,
        TrayIconColor initialColor,
        Action showWindow,
        Action exitApplication,
        Action<TimeSpan> onRefreshIntervalSelected,
        Action<double> onOpacitySelected,
        Action<bool> onStartWithWindowsToggled,
        Action<TrayIconColor> onColorSelected)
    {
        this.showWindow = showWindow;
        this.exitApplication = exitApplication;
        this.onRefreshIntervalSelected = onRefreshIntervalSelected;
        this.onOpacitySelected = onOpacitySelected;
        this.onColorSelected = onColorSelected;
        dispatcher = System.Windows.Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        lastIconText = iconText;
        lastTooltipText = tooltipText;

        summaryMenuItem = new ToolStripMenuItem(tooltipText, null, (_, _) => showWindow());

        oneMinuteMenuItem = new ToolStripMenuItem("1 minute", null, (_, _) => SelectInterval(TimeSpan.FromMinutes(1)));
        fiveMinuteMenuItem = new ToolStripMenuItem("5 minutes", null, (_, _) => SelectInterval(TimeSpan.FromMinutes(5)));
        customIntervalMenuItem = new ToolStripMenuItem("Custom...", null, (_, _) => SelectCustomInterval());
        var refreshIntervalMenuItem = new ToolStripMenuItem("Refresh interval");
        refreshIntervalMenuItem.DropDownItems.Add(oneMinuteMenuItem);
        refreshIntervalMenuItem.DropDownItems.Add(fiveMinuteMenuItem);
        refreshIntervalMenuItem.DropDownItems.Add(customIntervalMenuItem);

        opacity100MenuItem = new ToolStripMenuItem("100% (Solid)", null, (_, _) => SelectOpacity(1.0));
        opacity90MenuItem = new ToolStripMenuItem("90%", null, (_, _) => SelectOpacity(0.90));
        opacity85MenuItem = new ToolStripMenuItem("85%", null, (_, _) => SelectOpacity(0.85));
        opacity75MenuItem = new ToolStripMenuItem("75%", null, (_, _) => SelectOpacity(0.75));
        opacity50MenuItem = new ToolStripMenuItem("50%", null, (_, _) => SelectOpacity(0.50));
        customOpacityMenuItem = new ToolStripMenuItem("Custom...", null, (_, _) => SelectCustomOpacity());
        var opacityMenuItem = new ToolStripMenuItem("Transparency / Opacity");
        opacityMenuItem.DropDownItems.Add(opacity100MenuItem);
        opacityMenuItem.DropDownItems.Add(opacity90MenuItem);
        opacityMenuItem.DropDownItems.Add(opacity85MenuItem);
        opacityMenuItem.DropDownItems.Add(opacity75MenuItem);
        opacityMenuItem.DropDownItems.Add(opacity50MenuItem);
        opacityMenuItem.DropDownItems.Add(customOpacityMenuItem);

        colorAutoMenuItem = new ToolStripMenuItem("Auto (detect taskbar)", null, (_, _) => SelectColor(TrayIconColor.Auto));
        colorWhiteMenuItem = new ToolStripMenuItem("White (for dark taskbars)", null, (_, _) => SelectColor(TrayIconColor.White));
        colorBlackMenuItem = new ToolStripMenuItem("Black (for light taskbars)", null, (_, _) => SelectColor(TrayIconColor.Black));
        var colorMenuItem = new ToolStripMenuItem("Tray icon color");
        colorMenuItem.DropDownItems.Add(colorAutoMenuItem);
        colorMenuItem.DropDownItems.Add(colorWhiteMenuItem);
        colorMenuItem.DropDownItems.Add(colorBlackMenuItem);

        startWithWindowsMenuItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = initialStartWithWindows,
        };
        startWithWindowsMenuItem.Click += (sender, _) =>
        {
            if (sender is ToolStripMenuItem item)
            {
                onStartWithWindowsToggled(item.Checked);
            }
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(summaryMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Dashboard (Usage & Bench)", null, (_, _) => DashboardWindow.ShowDashboard());
        menu.Items.Add(refreshIntervalMenuItem);
        menu.Items.Add(opacityMenuItem);
        menu.Items.Add(colorMenuItem);
        menu.Items.Add(startWithWindowsMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Card", null, (_, _) => showWindow());
        menu.Items.Add("Exit", null, (_, _) => exitApplication());

        notifyIcon = new NotifyIcon
        {
            ContextMenuStrip = menu,
            Visible = true
        };
        notifyIcon.MouseClick += OnMouseClick;

        UpdateCheckedColor(initialColor);
        UpdateAmount(iconText, tooltipText);
        UpdateCheckedInterval(initialRefreshInterval);
        UpdateCheckedOpacity(initialOpacity);

        try
        {
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }
        catch
        {
            // Best effort registration
        }

        // Windows only registers the icon's NotifyIconSettings entry a moment after it first appears,
        // so retry a few times to flip it to "always show" (pinned) instead of the hidden overflow.
        promotionRetryTimer = new System.Threading.Timer(_ => TryPromoteTrayIcon(), null, 1000, 1500);
    }

    private void SelectInterval(TimeSpan interval)
    {
        UpdateCheckedInterval(interval);
        onRefreshIntervalSelected(interval);
    }

    private void SelectCustomInterval()
    {
        var minutes = PromptForCustomMinutes(currentIntervalMinutes);
        if (minutes is int selectedMinutes)
        {
            SelectInterval(TimeSpan.FromMinutes(selectedMinutes));
        }
    }

    private void UpdateCheckedInterval(TimeSpan interval)
    {
        currentIntervalMinutes = Math.Max(1, (int)Math.Round(interval.TotalMinutes));
        oneMinuteMenuItem.Checked = currentIntervalMinutes == 1;
        fiveMinuteMenuItem.Checked = currentIntervalMinutes == 5;
        var isCustom = currentIntervalMinutes != 1 && currentIntervalMinutes != 5;
        customIntervalMenuItem.Checked = isCustom;
        customIntervalMenuItem.Text = isCustom ? $"Custom ({currentIntervalMinutes} min)..." : "Custom...";
    }

    private static int? PromptForCustomMinutes(int currentMinutes)
    {
        using var form = new Form
        {
            Text = "Custom refresh interval",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            ClientSize = new Size(260, 110),
        };

        var label = new Label { Text = "Refresh every (minutes):", AutoSize = true, Location = new Point(12, 15) };
        var minutesInput = new NumericUpDown
        {
            Minimum = 1,
            Maximum = 1440,
            Value = Math.Min(1440, Math.Max(1, currentMinutes)),
            Location = new Point(12, 40),
            Width = 100,
        };
        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(88, 75) };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(168, 75) };

        form.Controls.Add(label);
        form.Controls.Add(minutesInput);
        form.Controls.Add(okButton);
        form.Controls.Add(cancelButton);
        form.AcceptButton = okButton;
        form.CancelButton = cancelButton;

        return form.ShowDialog() == DialogResult.OK ? (int)minutesInput.Value : null;
    }

    private void SelectOpacity(double opacity)
    {
        UpdateCheckedOpacity(opacity);
        onOpacitySelected(opacity);
    }

    private void SelectCustomOpacity()
    {
        var opacity = PromptForCustomOpacity(currentOpacity);
        if (opacity is double selectedOpacity)
        {
            SelectOpacity(selectedOpacity);
        }
    }

    private void UpdateCheckedOpacity(double opacity)
    {
        currentOpacity = Math.Max(0.2, Math.Min(1.0, opacity));
        var percent = (int)Math.Round(currentOpacity * 100);

        opacity100MenuItem.Checked = percent == 100;
        opacity90MenuItem.Checked = percent == 90;
        opacity85MenuItem.Checked = percent == 85;
        opacity75MenuItem.Checked = percent == 75;
        opacity50MenuItem.Checked = percent == 50;

        var isCustom = percent != 100 && percent != 90 && percent != 85 && percent != 75 && percent != 50;
        customOpacityMenuItem.Checked = isCustom;
        customOpacityMenuItem.Text = isCustom ? $"Custom ({percent}%)..." : "Custom...";
    }

    private static double? PromptForCustomOpacity(double current)
    {
        var currentPercent = (int)Math.Round(current * 100);
        using var form = new Form
        {
            Text = "Custom window opacity",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterScreen,
            MinimizeBox = false,
            MaximizeBox = false,
            ShowInTaskbar = false,
            TopMost = true,
            ClientSize = new Size(260, 110),
        };

        var label = new Label { Text = "Opacity percentage (20% - 100%):", AutoSize = true, Location = new Point(12, 15) };
        var percentInput = new NumericUpDown
        {
            Minimum = 20,
            Maximum = 100,
            Value = Math.Min(100, Math.Max(20, currentPercent)),
            Location = new Point(12, 40),
            Width = 100,
        };
        var okButton = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(88, 75) };
        var cancelButton = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(168, 75) };

        form.Controls.Add(label);
        form.Controls.Add(percentInput);
        form.Controls.Add(okButton);
        form.Controls.Add(cancelButton);
        form.AcceptButton = okButton;
        form.CancelButton = cancelButton;

        return form.ShowDialog() == DialogResult.OK ? (double)percentInput.Value / 100.0 : null;
    }

    public void UpdateCheckedAutoStart(bool enabled)
    {
        startWithWindowsMenuItem.Checked = enabled;
    }

    private void SelectColor(TrayIconColor color)
    {
        UpdateCheckedColor(color);
        onColorSelected(color);
        UpdateAmount(lastIconText, lastTooltipText, lastIsPaused);
    }

    private void UpdateCheckedColor(TrayIconColor color)
    {
        currentColor = color;
        colorAutoMenuItem.Checked = color == TrayIconColor.Auto;
        colorWhiteMenuItem.Checked = color == TrayIconColor.White;
        colorBlackMenuItem.Checked = color == TrayIconColor.Black;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color)
        {
            if (currentColor == TrayIconColor.Auto)
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!disposed)
                    {
                        UpdateAmount(lastIconText, lastTooltipText, lastIsPaused);
                    }
                }));
            }
        }
    }

    /// <summary>Re-renders the tray icon glyph and tooltip/menu text. Call again whenever usage data refreshes.</summary>
    public void UpdateAmount(string iconText, string tooltipText, bool isPaused = false)
    {
        lastIconText = iconText;
        lastTooltipText = tooltipText;
        lastIsPaused = isPaused;

        var previousIcon = renderedIcon;
        renderedIcon = RenderIcon(iconText, isPaused, currentColor);
        notifyIcon.Icon = renderedIcon;
        var fullTooltip = isPaused ? $"{tooltipText} (Paused - No Cursor active)" : tooltipText;
        notifyIcon.Text = fullTooltip.Length < 128 ? fullTooltip : fullTooltip.Substring(0, 127);
        summaryMenuItem.Text = isPaused ? $"{tooltipText} (Polling paused - no Cursor running)" : tooltipText;
        previousIcon?.Dispose();
    }

    public static Color ResolveTextColor(TrayIconColor colorPreference)
    {
        return colorPreference switch
        {
            TrayIconColor.White => Color.White,
            TrayIconColor.Black => Color.Black,
            _ => IsDarkTaskbar() ? Color.White : Color.Black
        };
    }

    public static bool IsDarkTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("SystemUsesLightTheme");
            if (value is int intValue)
            {
                return intValue == 0;
            }
        }
        catch
        {
            // Ignore registry read errors
        }

        // On Windows 10/11, default taskbar appearance is dark
        return true;
    }

    public static Icon RenderIcon(string text, bool isPaused = false, TrayIconColor colorPreference = TrayIconColor.Auto)
    {
        // Windows reserves a fixed square slot per tray icon (there's no public API for a wide,
        // clock-style text item), so render at a larger canvas and fill it edge-to-edge for legibility.
        const int size = 64;
        using var bitmap = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.Clear(Color.Transparent);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            var textColor = ResolveTextColor(colorPreference);
            using var brush = new SolidBrush(textColor);
            using var path = CreateOptimizedTextPath(text, size);
            graphics.FillPath(brush, path);

            if (isPaused)
            {
                // Draw a sleek pause indicator: amber dot in top-right corner with two pause bars
                const float dotSize = 14f;
                float dotX = size - dotSize - 1f;
                const float dotY = 1f;
                using var dotBrush = new SolidBrush(Color.FromArgb(245, 158, 11)); // Amber 500
                var borderColor = textColor == Color.White ? Color.FromArgb(30, 30, 30) : Color.White;
                using var borderPen = new Pen(borderColor, 2f);
                graphics.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
                graphics.DrawEllipse(borderPen, dotX, dotY, dotSize, dotSize);

                using var pausePen = new Pen(Color.White, 1.5f);
                graphics.DrawLine(pausePen, dotX + 4.5f, dotY + 4f, dotX + 4.5f, dotY + 10f);
                graphics.DrawLine(pausePen, dotX + 8.5f, dotY + 4f, dotX + 8.5f, dotY + 10f);
            }
        }

        var hIcon = bitmap.GetHicon();
        try
        {
            using var handleIcon = Icon.FromHandle(hIcon);
            return (Icon)handleIcon.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    private static GraphicsPath CreateOptimizedTextPath(string text, int size)
    {
        var path = new GraphicsPath();
        if (string.IsNullOrWhiteSpace(text))
        {
            return path;
        }

        var dotIndex = text.IndexOf('.');
        float maxW = size - 2f;
        float maxH = size - 4f;

        for (float f = size * 0.85f; f >= 12f; f -= 1f)
        {
            var candidate = new GraphicsPath();
            using var font = new Font("Segoe UI", f, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat(StringFormat.GenericTypographic);

            if (dotIndex > 0)
            {
                var intPart = text.Substring(0, dotIndex);
                var fracPart = text.Substring(dotIndex);

                candidate.AddString(intPart, font.FontFamily, (int)font.Style, f, PointF.Empty, format);
                var intBounds = candidate.GetBounds();

                using var fracPath = new GraphicsPath();
                fracPath.AddString(fracPart, font.FontFamily, (int)font.Style, f, PointF.Empty, format);
                var fracBounds = fracPath.GetBounds();

                // Tighten spacing: tuck the dot closer to the preceding digit to eliminate wide blank gaps
                float shiftX = intBounds.Right - fracBounds.Left - (f * 0.08f);
                using var shiftMatrix = new Matrix();
                shiftMatrix.Translate(shiftX, 0);
                fracPath.Transform(shiftMatrix);

                candidate.AddPath(fracPath, false);
            }
            else
            {
                candidate.AddString(text, font.FontFamily, (int)font.Style, f, PointF.Empty, format);
            }

            var b = candidate.GetBounds();
            if ((b.Width <= maxW && b.Height <= maxH) || f <= 12f)
            {
                // Optical and geometric centering based on actual ink bounds
                using var centerMatrix = new Matrix();
                centerMatrix.Translate((size - b.Width) / 2f - b.X, (size - b.Height) / 2f - b.Y);
                candidate.Transform(centerMatrix);
                return candidate;
            }

            candidate.Dispose();
        }

        path.AddString(text, FontFamily.GenericSansSerif, (int)FontStyle.Bold, 12f, PointF.Empty, StringFormat.GenericTypographic);
        return path;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        try
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
        catch
        {
        }

        promotionRetryTimer.Dispose();
        notifyIcon.Visible = false;
        notifyIcon.Dispose();
        renderedIcon?.Dispose();
    }

    private void TryPromoteTrayIcon()
    {
        if (disposed || ++promotionAttempts > 5)
        {
            promotionRetryTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            return;
        }

        var executablePath = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrEmpty(executablePath))
        {
            return;
        }

        try
        {
            using var settingsKey = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", writable: true);
            if (settingsKey is null)
            {
                return;
            }

            var promoted = false;
            foreach (var subKeyName in settingsKey.GetSubKeyNames())
            {
                using var subKey = settingsKey.OpenSubKey(subKeyName, writable: true);
                var entryPath = subKey?.GetValue("ExecutablePath") as string;
                if (subKey is null || !string.Equals(entryPath, executablePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                subKey.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                promoted = true;
            }

            if (promoted)
            {
                promotionRetryTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
        }
        catch (Exception)
        {
            // Best-effort: registry layout for this is undocumented and may change between Windows builds.
            promotionRetryTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }
    }

    private void OnMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            showWindow();
        }
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll")]
    internal static extern bool DestroyIcon(IntPtr handle);
}

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace UsageTray;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon notifyIcon;
    private readonly Action showWindow;
    private readonly Action exitApplication;
    private readonly Action<TimeSpan> onRefreshIntervalSelected;
    private readonly Action<double> onOpacitySelected;
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
    private readonly ToolStripMenuItem startWithWindowsMenuItem;
    private readonly System.Threading.Timer promotionRetryTimer;
    private int promotionAttempts;
    private int currentIntervalMinutes;
    private double currentOpacity;
    private Icon? renderedIcon;
    private bool disposed;

    public TrayIconService(
        string iconText,
        string tooltipText,
        TimeSpan initialRefreshInterval,
        double initialOpacity,
        bool initialStartWithWindows,
        Action showWindow,
        Action exitApplication,
        Action<TimeSpan> onRefreshIntervalSelected,
        Action<double> onOpacitySelected,
        Action<bool> onStartWithWindowsToggled)
    {
        this.showWindow = showWindow;
        this.exitApplication = exitApplication;
        this.onRefreshIntervalSelected = onRefreshIntervalSelected;
        this.onOpacitySelected = onOpacitySelected;

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

        UpdateAmount(iconText, tooltipText);
        UpdateCheckedInterval(initialRefreshInterval);
        UpdateCheckedOpacity(initialOpacity);

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

    /// <summary>Re-renders the tray icon glyph and tooltip/menu text. Call again whenever usage data refreshes.</summary>
    public void UpdateAmount(string iconText, string tooltipText)
    {
        var previousIcon = renderedIcon;
        renderedIcon = RenderIcon(iconText);
        notifyIcon.Icon = renderedIcon;
        notifyIcon.Text = tooltipText.Length < 128 ? tooltipText : tooltipText.Substring(0, 127);
        summaryMenuItem.Text = tooltipText;
        previousIcon?.Dispose();
    }

    private static Icon RenderIcon(string text)
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

            // GenericTypographic trims the large default margins System.Drawing normally reserves
            // around text, so the glyphs can actually use the full canvas instead of looking tiny.
            using var format = new StringFormat(StringFormat.GenericTypographic)
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center
            };

            var fontSize = size * 0.95f;
            const float padding = 2f;
            SizeF measured;
            Font font;
            while (true)
            {
                font = new Font("Segoe UI", fontSize, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
                measured = graphics.MeasureString(text, font, PointF.Empty, format);
                if ((measured.Width <= size - padding && measured.Height <= size - padding) || fontSize <= size * 0.3f)
                {
                    break;
                }

                font.Dispose();
                fontSize -= 1f;
            }

            using (font)
            using (var brush = new SolidBrush(Color.Black))
            {
                var bounds = new RectangleF(0, 0, size, size);
                graphics.DrawString(text, font, brush, bounds, format);
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

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
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

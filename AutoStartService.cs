using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace UsageTray;

public static class AutoStartService
{
    public const string DefaultRegistryKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultAppName = "CursorUsageTray";

    public static bool IsAutoStartEnabled(string subKeyPath = DefaultRegistryKey, string appName = DefaultAppName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKeyPath, writable: false);
            var value = key?.GetValue(appName) as string;
            return !string.IsNullOrEmpty(value);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStartService] IsAutoStartEnabled failed: {ex.Message}");
            return false;
        }
    }

    public static void SetAutoStart(bool enable, string? customExecutablePath = null, string subKeyPath = DefaultRegistryKey, string appName = DefaultAppName)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKeyPath);
            if (key is null) return;

            if (enable)
            {
                var exePath = customExecutablePath ?? GetExecutablePath();
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue(appName, $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue(appName, throwOnMissingValue: false);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AutoStartService] SetAutoStart({enable}) failed: {ex.Message}");
        }
    }

    public static string GetExecutablePath()
    {
        try
        {
            var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrEmpty(mainModule) && File.Exists(mainModule))
            {
                return mainModule!;
            }
        }
        catch
        {
            // fallback
        }

        var appDomainBase = AppDomain.CurrentDomain.BaseDirectory;
        var exeInBase = Path.Combine(appDomainBase, "UsageTray.exe");
        if (File.Exists(exeInBase))
        {
            return exeInBase;
        }

        return System.Windows.Forms.Application.ExecutablePath ?? AppDomain.CurrentDomain.BaseDirectory;
    }
}

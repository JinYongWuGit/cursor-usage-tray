using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CursorUsageTray;

/// <summary>
/// Detects whether the Cursor IDE, Cursor CLI, or Cursor Agent processes are currently running.
/// </summary>
public static class CursorProcessService
{
    private static readonly string[] CursorProcessNames =
    [
        "cursor",
        "cursor-agent",
        "cursoragent",
        "cursor_agent",
        "cursor-cli",
        "cursorcli",
        "cursorsandbox",
        "cursor-tunnel"
    ];

    private const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int processAccess, bool bInheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>
    /// Checks if any Cursor IDE, CLI, or Cursor Agent process is currently running.
    /// </summary>
    public static bool IsCursorOrAgentRunning()
    {
        try
        {
            var currentProcessId = Process.GetCurrentProcess().Id;
            return IsProcessActive(Process.GetProcessesByName, currentProcessId);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CursorProcessService] Error checking process status: {ex.Message}");
            // Fail-safe: if process enumeration throws unexpectedly, return true to avoid blocking usage refresh.
            return true;
        }
    }

    public static bool IsProcessActive(Func<string, Process[]> getProcessesByName, int currentProcessId = 0)
    {
        // 1. Direct process name check (fastest, case-insensitive on Windows)
        foreach (var name in CursorProcessNames)
        {
            Process[] procs;
            try
            {
                procs = getProcessesByName(name);
            }
            catch
            {
                continue;
            }

            try
            {
                foreach (var p in procs)
                {
                    if (currentProcessId > 0 && p.Id == currentProcessId)
                    {
                        continue;
                    }
                    return true;
                }
            }
            finally
            {
                foreach (var p in procs)
                {
                    p.Dispose();
                }
            }
        }

        // 2. Check node processes (Cursor agent CLI or Cursor background workers)
        Process[] nodeProcs;
        try
        {
            nodeProcs = getProcessesByName("node");
        }
        catch
        {
            return false;
        }

        try
        {
            foreach (var p in nodeProcs)
            {
                try
                {
                    if (currentProcessId > 0 && p.Id == currentProcessId)
                    {
                        continue;
                    }

                    var processPath = TryGetProcessPath(p);
                    if (IsCursorRelatedPath(processPath))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Ignore transient process exit or access issues
                }
            }
        }
        finally
        {
            foreach (var p in nodeProcs)
            {
                p.Dispose();
            }
        }

        return false;
    }

    public static string? TryGetProcessPath(Process process)
    {
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            try
            {
                var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process.Id);
                if (handle != IntPtr.Zero)
                {
                    try
                    {
                        var buffer = new StringBuilder(1024);
                        var size = buffer.Capacity;
                        if (QueryFullProcessImageName(handle, 0, buffer, ref size))
                        {
                            return buffer.ToString();
                        }
                    }
                    finally
                    {
                        CloseHandle(handle);
                    }
                }
            }
            catch
            {
                // Fall back to MainModule
            }
        }

        try
        {
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public static bool IsCursorRelatedPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var normalized = path!;
        return normalized.IndexOf("cursor-agent", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf("cursor_agent", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf("cursorsandbox", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf(@"\Programs\cursor\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf(@"/Programs/cursor/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf(@"\Cursor\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf(@"/Cursor/", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf(@"\.cursor\", StringComparison.OrdinalIgnoreCase) >= 0 ||
               normalized.IndexOf(@"/.cursor/", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

using System.Diagnostics;
using Xunit;

namespace CursorUsageTray.Tests;

public sealed class CursorProcessServiceTests
{
    [Theory]
    [InlineData(@"C:\Users\user\AppData\Local\cursor-agent\versions\2026.06.12-19-59-36-f6aba9a\node.exe", true)]
    [InlineData(@"C:\Users\user\AppData\Local\cursor-agent\versions\2026.06.12-19-59-36-f6aba9a\cursorsandbox.exe", true)]
    [InlineData(@"C:\Users\user\AppData\Local\cursor-agent\cursor-agent.cmd", true)]
    [InlineData(@"C:\Users\user\AppData\Local\Programs\cursor\Cursor.exe", true)]
    [InlineData(@"C:\Users\user\AppData\Local\Programs\cursor\resources\app\resources\helpers\node.exe", true)]
    [InlineData(@"/home/user/.cursor-agent/versions/latest/node", true)]
    [InlineData(@"/home/user/.cursor/cursor", true)]
    [InlineData(@"C:\Program Files\nodejs\node.exe", false)]
    [InlineData(@"C:\Windows\System32\cmd.exe", false)]
    [InlineData(@"", false)]
    [InlineData(null, false)]
    public void IsCursorRelatedPath_CorrectlyIdentifiesCursorPaths(string? path, bool expected)
    {
        var result = CursorProcessService.IsCursorRelatedPath(path);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void IsProcessActive_ReturnsTrue_WhenCursorProcessFound()
    {
        var dummyProcess = Process.GetCurrentProcess();

        bool result = CursorProcessService.IsProcessActive(
            name => name == "cursor" ? [dummyProcess] : [],
            currentProcessId: dummyProcess.Id + 9999);

        Assert.True(result);
    }

    [Fact]
    public void IsProcessActive_IgnoresSelf_WhenOnlyMatchingProcessIsCurrentProcess()
    {
        var currentProcess = Process.GetCurrentProcess();

        bool result = CursorProcessService.IsProcessActive(
            name => name == "cursor" ? [currentProcess] : [],
            currentProcessId: currentProcess.Id);

        Assert.False(result);
    }

    [Fact]
    public void IsProcessActive_ReturnsFalse_WhenNoMatchingProcesses()
    {
        bool result = CursorProcessService.IsProcessActive(
            _ => [],
            currentProcessId: 1234);

        Assert.False(result);
    }

    [Fact]
    public void IsProcessActive_ReturnsTrue_WhenCursorAgentProcessFound()
    {
        var dummyProcess = Process.GetCurrentProcess();

        bool result = CursorProcessService.IsProcessActive(
            name => name == "cursor-agent" ? [dummyProcess] : [],
            currentProcessId: dummyProcess.Id + 9999);

        Assert.True(result);
    }

    [Fact]
    public void IsCursorOrAgentRunning_ExecutesWithoutExceptions()
    {
        // Must never throw an exception regardless of running environment
        var isRunning = CursorProcessService.IsCursorOrAgentRunning();
        // Returns a valid boolean
        Assert.True(isRunning || !isRunning);
    }

    [Fact]
    public void IsProcessActive_ReturnsTrue_WhenCursorCliProcessFound()
    {
        var dummyProcess = Process.GetCurrentProcess();

        bool result = CursorProcessService.IsProcessActive(
            name => name == "cursor-cli" ? [dummyProcess] : [],
            currentProcessId: dummyProcess.Id + 9999);

        Assert.True(result);
    }
}

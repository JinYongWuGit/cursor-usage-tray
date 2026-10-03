using Xunit;

namespace UsageTray.Tests;

public sealed class UsageDisplayViewModelTests
{
    [Fact]
    public void ExposesTheAppliedUsageReadout()
    {
        var viewModel = new UsageDisplayViewModel();

        viewModel.ApplyUsage(23.00m, 550m);

        Assert.Equal("$23.00", viewModel.AmountText);
        Assert.Equal("/ $550", viewModel.LimitText);
        Assert.Equal("4.2% used", viewModel.PercentText);
    }

    [Fact]
    public void CalculatesUsagePercentage()
    {
        var viewModel = new UsageDisplayViewModel();

        viewModel.ApplyUsage(23.00m, 550m);

        Assert.Equal(23m / 550m * 100m, (decimal)viewModel.PercentUsed, precision: 10);
    }

    [Fact]
    public void TreatsZeroLimitAsUnknownRatherThanDividingByZero()
    {
        var viewModel = new UsageDisplayViewModel();

        Assert.Equal(0, viewModel.PercentUsed);
        Assert.Equal("-- used", viewModel.PercentText);
    }

    [Fact]
    public void TogglesExpandedStateAndArrowText()
    {
        var viewModel = new UsageDisplayViewModel();

        Assert.False(viewModel.IsExpanded);
        Assert.Equal("▼", viewModel.ExpandArrowText);
        Assert.Equal("Expand details", viewModel.ExpandToolTip);

        viewModel.ToggleExpanded();

        Assert.True(viewModel.IsExpanded);
        Assert.Equal("▲", viewModel.ExpandArrowText);
        Assert.Equal("Collapse", viewModel.ExpandToolTip);

        viewModel.ToggleExpanded();

        Assert.False(viewModel.IsExpanded);
    }

    [Fact]
    public void AppliesDetailedUsageSnapshotCorrectly()
    {
        var viewModel = new UsageDisplayViewModel();
        var models = new[]
        {
            new ModelUsageRow("default", 120, 91_000_000, 18.64m),
        };

        var snapshot = new CursorUsageSnapshot(
            used: 18.66m,
            limit: 550.00m,
            onDemandSpend: 0.02m,
            onDemandRatio: 0.02 / 18.66,
            monthlyUsageSubtitle: "Included total usage (dashboard)",
            modelBreakdown: models,
            resetInfoText: "Resets in 29 days on Nov 1, 2026");

        viewModel.ApplyDetailedUsage(snapshot);

        Assert.Equal("$18.66", viewModel.AmountText);
        Assert.Equal("$18.66 / $550.00", viewModel.MonthlyUsageValue);
        Assert.Equal("$0.02", viewModel.OnDemandSpendText);
        Assert.Equal("Included total usage (dashboard)", viewModel.MonthlyUsageSubtitle);
        Assert.Equal("Resets in 29 days on Nov 1, 2026", viewModel.ResetInfoText);
        Assert.True(viewModel.HasResetInfo);
        Assert.True(viewModel.HasModels);
        Assert.Single(viewModel.Models);
        Assert.Equal("default", viewModel.Models[0].Model);
        Assert.Equal(120, viewModel.Models[0].Requests);
        Assert.Equal("91.0M", viewModel.Models[0].TokensFormatted);
        Assert.Equal("$18.64", viewModel.Models[0].SpendFormatted);
    }

    [Theory]
    [InlineData(500, "500")]
    [InlineData(1_500, "1.5K")]
    [InlineData(91_000_000, "91.0M")]
    [InlineData(2_500_000_000, "2.5B")]
    public void FormatsTokensCorrectly(long tokens, string expected)
    {
        var formatted = ModelUsageRow.FormatTokens(tokens);
        Assert.Equal(expected, formatted);
    }

    [Fact]
    public void InitialUninitializedStateShowsNeutralPlaceholders()
    {
        var viewModel = new UsageDisplayViewModel();

        Assert.False(viewModel.IsInitialized);
        Assert.Equal("--", viewModel.IconText);
        Assert.Equal("--", viewModel.AmountText);
        Assert.Equal("Loading...", viewModel.SummaryText);
        Assert.Equal("--", viewModel.MonthlyUsageValue);
        Assert.Equal("--", viewModel.OnDemandSpendText);
        Assert.Equal("-- used", viewModel.PercentText);
        Assert.Equal(0, viewModel.PercentUsed);

        viewModel.ApplyUsage(15.00m, 550m);

        Assert.True(viewModel.IsInitialized);
        Assert.Equal("$15", viewModel.IconText);
        Assert.Equal("$15.00", viewModel.AmountText);
        Assert.Equal("$15.00 / $550", viewModel.SummaryText);
    }

    [Fact]
    public void UsageCacheSavesAndLoadsSnapshot()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"usage_cache_test_{System.Guid.NewGuid()}.json");
        try
        {
            var models = new[]
            {
                new ModelUsageRow("default", 50, 40_000_000, 10.50m),
            };
            var snapshot = new CursorUsageSnapshot(
                used: 25.00m,
                limit: 550.00m,
                onDemandSpend: 1.25m,
                onDemandRatio: 0.05,
                monthlyUsageSubtitle: "Test Subtitle",
                modelBreakdown: models,
                resetInfoText: "Resets in 15 days on Nov 1, 2026");

            UsageCache.Save(snapshot, tempFile);
            var loaded = UsageCache.TryLoad(tempFile);

            Assert.NotNull(loaded);
            Assert.Equal(25.00m, loaded.Used);
            Assert.Equal(550.00m, loaded.Limit);
            Assert.Equal(1.25m, loaded.OnDemandSpend);
            Assert.Equal("Test Subtitle", loaded.MonthlyUsageSubtitle);
            Assert.Equal("Resets in 15 days on Nov 1, 2026", loaded.ResetInfoText);
            Assert.Single(loaded.ModelBreakdown);
            Assert.Equal("default", loaded.ModelBreakdown[0].Model);
            Assert.Equal(50, loaded.ModelBreakdown[0].Requests);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void FormatsResetInfoString()
    {
        var resetIso = DateTimeOffset.UtcNow.AddDays(10).ToString("o");
        var text = CursorUsageClient.FormatResetInfo(resetIso);

        Assert.NotNull(text);
        Assert.StartsWith("Resets in 10 days on", text);
    }

    [Fact]
    public void UserSettingsSavesAndLoadsRoundtrip()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"user_settings_test_{System.Guid.NewGuid()}.json");
        try
        {
            var settings = new UserSettings
            {
                RefreshIntervalMinutes = 15,
                WindowOpacity = 0.75,
            };

            settings.Save(tempFile);
            var loaded = UserSettings.Load(tempFile);

            Assert.NotNull(loaded);
            Assert.Equal(15, loaded.RefreshIntervalMinutes);
            Assert.Equal(0.75, loaded.WindowOpacity);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void ParsesCursorBenchHtmlTableCorrectly()
    {
        var html = @"
<!DOCTYPE html>
<html>
<body>
  <h1>CursorBench 3.2</h1>
  <table class=""w-full table-fixed border-collapse"">
    <thead>
      <tr><th>#</th><th>Model</th><th>Score</th><th>Cost / task</th><th>Tokens / task</th><th>Steps / task</th></tr>
    </thead>
    <tbody>
      <tr>
        <td>1</td>
        <td>Claude 3.7 Sonnet</td>
        <td>72.5%</td>
        <td>$5.40</td>
        <td>45,000</td>
        <td>50</td>
      </tr>
      <tr>
        <td>2</td>
        <td>GPT-4o</td>
        <td>68.1%</td>
        <td>$2.10</td>
        <td>32,500</td>
        <td>38</td>
      </tr>
    </tbody>
  </table>
</body>
</html>";

        var snap = CursorBenchService.ParseCursorBenchHtml(html);
        Assert.Equal("3.2", snap.Version);
        Assert.Equal(2, snap.Rows.Count);
        Assert.Equal("Claude 3.7 Sonnet", snap.Rows[0].Model);
        Assert.Equal(0.725, snap.Rows[0].Score);
        Assert.Equal(5.40, snap.Rows[0].CostPerTask);
        Assert.Equal(45000, snap.Rows[0].TokensPerTask);
        Assert.Equal(50, snap.Rows[0].StepsPerTask);
    }

    [Fact]
    public void LoadsBundledCursorBenchSnapshot()
    {
        var snap = CursorBenchService.LoadBundledSnapshot();
        Assert.NotNull(snap);
        Assert.True(snap.Rows.Count > 0);
        Assert.Contains(snap.Rows, r => r.Model.Contains("Fable") || r.Model.Contains("Grok") || r.Model.Contains("Sonnet") || r.Model.Contains("Claude") || r.Model.Contains("GPT"));
    }

    [Fact]
    public void SerializesDashboardStateToCamelCaseJson()
    {
        var state = new DashboardState
        {
            GeneratedAt = 1700000000000,
            IsTeamMember = true,
            QuotaAwareEventDisplay = true,
            Data = new UsagePayload
            {
                IncludedRequests = new IncludedRequestsDto { Used = 100, Limit = 500 },
                OnDemand = new OnDemandDto { State = "limited", SpendDollars = 5.25m, LimitDollars = 50m }
            },
            Events = new System.Collections.Generic.List<UsageEventDto>
            {
                new() { Timestamp = 1700000000000, Model = "gpt-4", Kind = "Included", TotalTokens = 1200, Requests = 1, SpendCents = 0 }
            }
        };

        var json = System.Text.Json.JsonSerializer.Serialize(state, new System.Text.Json.JsonSerializerOptions
        {
            PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
        });

        Assert.Contains("\"generatedAt\":1700000000000", json);
        Assert.Contains("\"isTeamMember\":true", json);
        Assert.Contains("\"includedRequests\":{\"used\":100,\"limit\":500}", json);
        Assert.Contains("\"spendDollars\":5.25", json);
        Assert.Contains("\"totalTokens\":1200", json);
    }

    [Fact]
    public void UserSettingsSavesAndLoadsStartWithWindows()
    {
        var tempFile = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"user_settings_autostart_{System.Guid.NewGuid()}.json");
        try
        {
            var settings = new UserSettings
            {
                RefreshIntervalMinutes = 5,
                WindowOpacity = 0.90,
                StartWithWindows = true,
            };

            settings.Save(tempFile);
            var loaded = UserSettings.Load(tempFile);

            Assert.NotNull(loaded);
            Assert.True(loaded.StartWithWindows);
        }
        finally
        {
            if (System.IO.File.Exists(tempFile))
            {
                System.IO.File.Delete(tempFile);
            }
        }
    }

    [Fact]
    public void AutoStartServiceSetsAndQueriesCustomRegistryKey()
    {
        var testKey = @"Software\UsageTrayUnitTest_" + System.Guid.NewGuid().ToString("N");
        var testApp = "TestApp";
        var fakeExe = @"C:\Program Files\UsageTray\UsageTray.exe";

        try
        {
            Assert.False(AutoStartService.IsAutoStartEnabled(testKey, testApp));

            AutoStartService.SetAutoStart(true, fakeExe, testKey, testApp);
            Assert.True(AutoStartService.IsAutoStartEnabled(testKey, testApp));

            AutoStartService.SetAutoStart(false, fakeExe, testKey, testApp);
            Assert.False(AutoStartService.IsAutoStartEnabled(testKey, testApp));
        }
        finally
        {
            try
            {
                Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false);
            }
            catch { }
        }
    }
}

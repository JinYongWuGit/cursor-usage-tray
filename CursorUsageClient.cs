using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CursorUsageTray;

/// <summary>Detailed model usage entry.</summary>
public sealed class ModelUsageRow
{
    public ModelUsageRow(string model, int requests, long totalTokens, decimal spendDollars)
    {
        Model = model;
        Requests = requests;
        TotalTokens = totalTokens;
        SpendDollars = spendDollars;
        TokensFormatted = FormatTokens(totalTokens);
        SpendFormatted = spendDollars.ToString("$0.00", CultureInfo.InvariantCulture);
    }

    public string Model { get; }

    public int Requests { get; }

    public long TotalTokens { get; }

    public decimal SpendDollars { get; }

    public string TokensFormatted { get; }

    public string SpendFormatted { get; }

    public static string FormatTokens(long n)
    {
        if (n >= 1_000_000_000) return $"{(n / 1_000_000_000.0):0.0}B";
        if (n >= 1_000_000) return $"{(n / 1_000_000.0):0.0}M";
        if (n >= 1_000) return $"{(n / 1_000.0):0.0}K";
        return n.ToString(CultureInfo.InvariantCulture);
    }
}

/// <summary>Usage, limit, and detailed metrics read from the Cursor dashboard API.</summary>
public sealed class CursorUsageSnapshot
{
    public CursorUsageSnapshot(
        decimal used,
        decimal limit,
        decimal onDemandSpend = 0m,
        double onDemandRatio = 0,
        string? monthlyUsageSubtitle = null,
        IReadOnlyList<ModelUsageRow>? modelBreakdown = null,
        string? resetInfoText = null)
    {
        Used = used;
        Limit = limit;
        OnDemandSpend = onDemandSpend;
        OnDemandRatio = Math.Max(0, Math.Min(1, onDemandRatio));
        MonthlyUsageSubtitle = monthlyUsageSubtitle ?? "Included total usage (dashboard)";
        ModelBreakdown = modelBreakdown ?? Array.Empty<ModelUsageRow>();
        ResetInfoText = resetInfoText;
    }

    public decimal Used { get; }

    public decimal Limit { get; }

    public decimal OnDemandSpend { get; }

    public double OnDemandRatio { get; }

    public string MonthlyUsageSubtitle { get; }

    public IReadOnlyList<ModelUsageRow> ModelBreakdown { get; }

    public string? ResetInfoText { get; }
}

/// <summary>
/// Reads the locally cached Cursor auth token from the Cursor desktop app's SQLite state database
/// and calls the same (undocumented) dashboard endpoints the Cursor web dashboard uses, mirroring
/// https://github.com (cursor-metrics VS Code extension)'s cursor-api.ts.
/// </summary>
public sealed class CursorUsageClient
{
    private static readonly Uri BaseUri = new("https://cursor.com");

    // UseCookies defaults to true, which makes HttpClientHandler manage cookies via its own
    // CookieContainer and silently drop our manually-set "Cookie" request header on .NET Framework
    // (the request still succeeds, just without auth, so the API returns 401). Disabling it ensures
    // the header we set is actually sent, on both net48 and net10.
    private readonly HttpClient httpClient = new(new HttpClientHandler { UseCookies = false })
    {
        Timeout = TimeSpan.FromSeconds(15),
    };

    public async Task<CursorUsageSnapshot?> GetUsageAsync(bool onlyIfCursorRunning = false, CancellationToken cancellationToken = default)
    {
        if (onlyIfCursorRunning && !CursorProcessService.IsCursorOrAgentRunning())
        {
            return null;
        }

        try
        {
            var auth = TryReadAuth();
            if (auth is null)
            {
                return null;
            }

            var stripe = await GetJsonAsync("/api/auth/stripe", auth, cancellationToken).ConfigureAwait(false);
            var (isTeamMember, teamId) = ParseStripeTeamInfo(stripe);

            if (isTeamMember && teamId is not null)
            {
                var summaryTask = GetJsonAsync($"/api/usage-summary?teamId={teamId}", auth, cancellationToken);
                var eventsTask = GetUsageEventsAsync(auth, teamId.Value, cancellationToken);

                await Task.WhenAll(summaryTask, eventsTask).ConfigureAwait(false);
                var summary = await summaryTask.ConfigureAwait(false);
                var events = await eventsTask.ConfigureAwait(false);

                var monthly = ParseMonthlyUsage(summary);
                if (monthly is { Enabled: true } usage)
                {
                    var resetsAt = summary?.RootElement.TryGetProperty("billingCycleEnd", out var endEl) == true
                        ? endEl.GetString()
                        : null;
                    var resetInfo = FormatResetInfo(resetsAt);

                    var (models, includedSpendDollars) = AggregateEvents(events, resetsAt);
                    var onDemandSpend = Math.Max(0, usage.UsedDollars - includedSpendDollars);
                    var ratio = usage.UsedDollars > 0 ? (double)(onDemandSpend / usage.UsedDollars) : 0;

                    return new CursorUsageSnapshot(
                        used: usage.UsedDollars,
                        limit: usage.LimitDollars,
                        onDemandSpend: onDemandSpend,
                        onDemandRatio: ratio,
                        monthlyUsageSubtitle: "Included total usage (dashboard)",
                        modelBreakdown: models,
                        resetInfoText: resetInfo);
                }
            }

            var usageDoc = await GetJsonAsync($"/api/usage?user={auth.UserId}", auth, cancellationToken).ConfigureAwait(false);
            var totals = ExtractUsageTotals(usageDoc);
            return totals is null ? null : new CursorUsageSnapshot(totals.Value.Used, totals.Value.Limit);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CursorUsageClient] Failed to fetch usage: {ex}");
            return null;
        }
    }

    public Task<JsonDocument?> GetRawJsonAsync(string pathAndQuery, CursorAuth auth) =>
        GetJsonAsync(pathAndQuery, auth, CancellationToken.None);

    private async Task<JsonDocument?> GetJsonAsync(string pathAndQuery, CursorAuth auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(BaseUri, pathAndQuery));
        request.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={auth.SessionToken}");
        request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://cursor.com/dashboard");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Debug.WriteLine($"[CursorUsageClient] {pathAndQuery} failed: {(int)response.StatusCode}");
            return null;
        }

        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonDocument.Parse(json);
    }

    private async Task<JsonDocument?> GetUsageEventsAsync(CursorAuth auth, long teamId, CancellationToken cancellationToken)
    {
        var endDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var startDate = endDate - (31L * 86400000L);
        var payload = new
        {
            teamId,
            startDate = startDate.ToString(CultureInfo.InvariantCulture),
            endDate = endDate.ToString(CultureInfo.InvariantCulture),
            page = 1,
            pageSize = 500,
        };
        return await PostJsonAsync("/api/dashboard/get-filtered-usage-events", payload, auth, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DashboardState> GetDashboardStateAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        try
        {
            var auth = TryReadAuth();
            if (auth is null)
            {
                return new DashboardState
                {
                    GeneratedAt = now,
                    Error = "Could not find Cursor auth token. Please sign in to Cursor.",
                };
            }

            var stripe = await GetJsonAsync("/api/auth/stripe", auth, cancellationToken).ConfigureAwait(false);
            var (isTeamMember, teamId) = ParseStripeTeamInfo(stripe);

            if (isTeamMember && teamId is not null)
            {
                var summaryTask = GetJsonAsync($"/api/usage-summary?teamId={teamId}", auth, cancellationToken);
                var usageTask = GetJsonAsync($"/api/usage?user={auth.UserId}", auth, cancellationToken);
                var teamSpendTask = PostJsonAsync("/api/dashboard/get-team-spend", new { teamId = teamId.Value }, auth, cancellationToken);
                var eventsTask = FetchAllUsageEventsAsync(auth, teamId.Value, cancellationToken);

                await Task.WhenAll(summaryTask, usageTask, teamSpendTask, eventsTask).ConfigureAwait(false);

                var summary = await summaryTask.ConfigureAwait(false);
                var usageDoc = await usageTask.ConfigureAwait(false);
                var teamSpendDoc = await teamSpendTask.ConfigureAwait(false);
                var events = await eventsTask.ConfigureAwait(false);

                var monthly = ParseMonthlyUsage(summary);
                var usageTotals = ExtractUsageTotals(usageDoc);

                string? resetsAt = summary?.RootElement.TryGetProperty("billingCycleEnd", out var endEl) == true
                    ? endEl.GetString()
                    : null;

                decimal memberUsedReqs = usageTotals?.Used ?? 0m;
                decimal memberLimitReqs = usageTotals?.Limit ?? 0m;
                decimal memberSpendDollars = 0m;
                decimal? hardLimitOverride = null;
                string? dashboardUserId = null;

                if (teamSpendDoc is not null && teamSpendDoc.RootElement.TryGetProperty("teamMemberSpend", out var membersEl) && membersEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var m in membersEl.EnumerateArray())
                    {
                        var email = m.TryGetProperty("email", out var em) ? em.GetString() : null;
                        var userId = m.TryGetProperty("userId", out var uid) ? uid.ToString() : null;
                        var authId = m.TryGetProperty("authId", out var aid) ? aid.GetString() : null;

                        if ((auth.Email != null && string.Equals(email, auth.Email, StringComparison.OrdinalIgnoreCase)) ||
                            string.Equals(userId, auth.UserId, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(authId, auth.UserId, StringComparison.OrdinalIgnoreCase))
                        {
                            dashboardUserId = userId;
                            var spendCents = TryGetDecimal(m, "spendCents") ?? 0m;
                            memberSpendDollars = spendCents / 100m;
                            hardLimitOverride = TryGetDecimal(m, "hardLimitOverrideDollars");
                            break;
                        }
                    }

                    if (string.IsNullOrEmpty(resetsAt) && teamSpendDoc.RootElement.TryGetProperty("nextCycleStart", out var ncsEl))
                    {
                        if (ncsEl.TryGetInt64(out var ncsMs))
                        {
                            resetsAt = DateTimeOffset.FromUnixTimeMilliseconds(ncsMs).ToString("o", CultureInfo.InvariantCulture);
                        }
                    }
                }

                string onDemandState;
                var (models, includedSpendDollars) = AggregateEventsFromDtos(events, resetsAt);
                decimal onDemandSpend;
                decimal usedDollars;
                decimal limitDollars;

                if (monthly is { Enabled: true } mUsage)
                {
                    onDemandState = "limited";
                    usedDollars = mUsage.UsedDollars;
                    limitDollars = mUsage.LimitDollars;
                    onDemandSpend = Math.Max(0, mUsage.UsedDollars - includedSpendDollars);
                }
                else
                {
                    usedDollars = memberUsedReqs;
                    limitDollars = memberLimitReqs;
                    onDemandSpend = memberSpendDollars;
                    if (hardLimitOverride is not null && hardLimitOverride > 0)
                    {
                        onDemandState = "limited";
                    }
                    else
                    {
                        onDemandState = "unlimited";
                    }
                }

                var data = new UsagePayload
                {
                    IncludedRequests = new IncludedRequestsDto
                    {
                        Used = memberUsedReqs,
                        Limit = memberLimitReqs,
                    },
                    IncludedSpend = monthly is { Enabled: true } mSpend
                        ? new IncludedSpendDto { IncludedDollars = includedSpendDollars, TotalDollars = mSpend.UsedDollars }
                        : null,
                    OnDemand = new OnDemandDto
                    {
                        State = onDemandState,
                        SpendDollars = monthly is { Enabled: true } mDol ? mDol.UsedDollars : memberSpendDollars,
                        LimitDollars = monthly is { Enabled: true } mLim ? mLim.LimitDollars : hardLimitOverride,
                        Label = monthly is { Enabled: true } ? "Monthly usage" : "On-Demand Usage",
                        Footer = monthly is { Enabled: true } ? "Included total usage (dashboard)" : "Pay for extra usage beyond your plan limits",
                    },
                    ResetsAt = resetsAt,
                };

                // Also update local cache so tray popup stays fresh
                var snapshot = new CursorUsageSnapshot(
                    used: usedDollars,
                    limit: limitDollars,
                    onDemandSpend: onDemandSpend,
                    onDemandRatio: limitDollars > 0 ? (double)(onDemandSpend / limitDollars) : 0,
                    monthlyUsageSubtitle: monthly is { Enabled: true } ? "Included total usage (dashboard)" : null,
                    modelBreakdown: models,
                    resetInfoText: FormatResetInfo(resetsAt));
                UsageCache.Save(snapshot);

                var dailySpend = await FetchDailySpendAsync(auth, teamId.Value, dashboardUserId, cancellationToken).ConfigureAwait(false);

                return new DashboardState
                {
                    GeneratedAt = now,
                    Data = data,
                    Events = events,
                    DailySpend = dailySpend,
                    ResetsAt = resetsAt,
                    IsTeamMember = true,
                    QuotaAwareEventDisplay = true,
                    Error = null,
                };
            }
            else
            {
                var usageDoc = await GetJsonAsync($"/api/usage?user={auth.UserId}", auth, cancellationToken).ConfigureAwait(false);
                var totals = ExtractUsageTotals(usageDoc);
                var data = new UsagePayload
                {
                    IncludedRequests = new IncludedRequestsDto
                    {
                        Used = totals?.Used ?? 0,
                        Limit = totals?.Limit ?? 0,
                    },
                    OnDemand = new OnDemandDto { State = "disabled" },
                };

                return new DashboardState
                {
                    GeneratedAt = now,
                    Data = data,
                    Events = new List<UsageEventDto>(),
                    DailySpend = new List<DailySpendRowDto>(),
                    ResetsAt = null,
                    IsTeamMember = false,
                    QuotaAwareEventDisplay = true,
                    Error = null,
                };
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CursorUsageClient] GetDashboardStateAsync failed: {ex}");
            return new DashboardState
            {
                GeneratedAt = now,
                Error = ex.Message,
            };
        }
    }

    public async Task<List<UsageEventDto>> FetchAllUsageEventsAsync(CursorAuth auth, long teamId, CancellationToken cancellationToken = default)
    {
        var allEvents = new List<UsageEventDto>();
        var endDate = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var startDate = endDate - (31L * 86400000L);
        const int maxPages = 10;
        const int pageSize = 500;

        for (int page = 1; page <= maxPages; page++)
        {
            var payload = new
            {
                teamId,
                startDate = startDate.ToString(CultureInfo.InvariantCulture),
                endDate = endDate.ToString(CultureInfo.InvariantCulture),
                page,
                pageSize,
            };

            var doc = await PostJsonAsync("/api/dashboard/get-filtered-usage-events", payload, auth, cancellationToken).ConfigureAwait(false);
            if (doc is null) break;

            var root = doc.RootElement;
            if (!root.TryGetProperty("usageEventsDisplay", out var eventsArray) || eventsArray.ValueKind != JsonValueKind.Array)
            {
                break;
            }

            int count = 0;
            foreach (var item in eventsArray.EnumerateArray())
            {
                count++;
                var dto = ParseUsageEventDto(item);
                if (dto is not null)
                {
                    allEvents.Add(dto);
                }
            }

            if (count < pageSize) break;
        }

        return allEvents;
    }

    private static UsageEventDto? ParseUsageEventDto(JsonElement item)
    {
        long timestamp = 0;
        if (item.TryGetProperty("timestamp", out var tsEl))
        {
            if (tsEl.ValueKind == JsonValueKind.String && long.TryParse(tsEl.GetString(), out var tsParsed))
                timestamp = tsParsed;
            else if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var tsNum))
                timestamp = tsNum;
        }

        string model = "default";
        if (item.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
        {
            var raw = modelEl.GetString();
            if (!string.IsNullOrEmpty(raw))
            {
                model = raw == "auto" ? "default" : raw!;
            }
        }

        var kindStr = item.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : "";
        string kind = kindStr switch
        {
            "USAGE_EVENT_KIND_USAGE_BASED" => "On-Demand",
            "USAGE_EVENT_KIND_ERRORED_NOT_CHARGED" => "Errored",
            "USAGE_EVENT_KIND_ABORTED_NOT_CHARGED" => "Aborted",
            _ => "Included",
        };

        long inTok = 0, outTok = 0, cacheRead = 0, cacheWrite = 0;
        decimal? totalCents = null;

        if (item.TryGetProperty("tokenUsage", out var tokenUsage) && tokenUsage.ValueKind == JsonValueKind.Object)
        {
            inTok = TryGetInt64(tokenUsage, "inputTokens");
            outTok = TryGetInt64(tokenUsage, "outputTokens");
            cacheRead = TryGetInt64(tokenUsage, "cacheReadTokens");
            cacheWrite = TryGetInt64(tokenUsage, "cacheWriteTokens");
            totalCents = TryGetDecimal(tokenUsage, "totalCents");
        }

        var totalTokens = inTok + outTok + cacheRead + cacheWrite;
        var requests = (double)(TryGetDecimal(item, "numRequests") ?? 1m);
        var chargedCents = TryGetDecimal(item, "chargedCents");
        var requestsCosts = TryGetDecimal(item, "requestsCosts");
        var spendCents = Math.Round(chargedCents ?? totalCents ?? requestsCosts ?? 0m);
        var maxMode = item.TryGetProperty("maxMode", out var mmEl) && mmEl.ValueKind == JsonValueKind.True;

        return new UsageEventDto
        {
            Timestamp = timestamp,
            Model = model,
            Kind = kind,
            TotalTokens = totalTokens,
            Requests = requests,
            SpendCents = spendCents,
            MaxMode = maxMode,
        };
    }

    private static (IReadOnlyList<ModelUsageRow> Models, decimal IncludedSpendDollars) AggregateEventsFromDtos(
        IReadOnlyList<UsageEventDto> events,
        string? resetsAtIso)
    {
        long cutoffMs;
        if (!string.IsNullOrEmpty(resetsAtIso) && DateTimeOffset.TryParse(resetsAtIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var resetDto))
        {
            var cycleStart = resetDto.AddMonths(-1);
            cutoffMs = cycleStart.ToUnixTimeMilliseconds();
        }
        else
        {
            cutoffMs = DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds();
        }

        var modelTotals = new Dictionary<string, (int Requests, long Tokens, decimal SpendCents)>(StringComparer.OrdinalIgnoreCase);
        decimal includedSpendCents = 0m;

        foreach (var e in events)
        {
            if (e.Timestamp > 0 && e.Timestamp < cutoffMs) continue;

            if (e.Kind == "Included")
            {
                includedSpendCents += e.SpendCents;
            }

            if (!modelTotals.TryGetValue(e.Model, out var cur))
            {
                cur = (0, 0, 0m);
            }
            modelTotals[e.Model] = (cur.Requests + (int)Math.Max(1, Math.Round(e.Requests)), cur.Tokens + e.TotalTokens, cur.SpendCents + e.SpendCents);
        }

        var list = modelTotals
            .Select(kvp => new ModelUsageRow(kvp.Key, kvp.Value.Requests, kvp.Value.Tokens, kvp.Value.SpendCents / 100m))
            .OrderByDescending(m => m.TotalTokens)
            .ToList();

        return (list, includedSpendCents / 100m);
    }

    private async Task<List<DailySpendRowDto>> FetchDailySpendAsync(CursorAuth auth, long teamId, string? dashboardUserId, CancellationToken cancellationToken)
    {
        var list = new List<DailySpendRowDto>();
        try
        {
            long dUserId = 0;
            if (!long.TryParse(dashboardUserId, out dUserId))
            {
                if (!long.TryParse(auth.UserId, out dUserId)) return list;
            }

            var periodEndMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var periodStartMs = periodEndMs - (31L * 86400000L);
            var payload = new
            {
                teamId,
                userId = dUserId,
                periodStartMs,
                periodEndMs,
                groupBy = 1,
                spendType = 3,
            };

            var doc = await PostJsonAsync("/api/dashboard/get-daily-spend-by-category", payload, auth, cancellationToken).ConfigureAwait(false);
            if (doc is null) return list;

            if (doc.RootElement.TryGetProperty("dailySpend", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in array.EnumerateArray())
                {
                    var day = TryGetInt64(row, "day");
                    var catRaw = row.TryGetProperty("category", out var cEl) ? cEl.GetString() : null;
                    var cat = catRaw == "auto" ? "default" : (catRaw ?? "");
                    var spendCents = TryGetDecimal(row, "spendCents") ?? 0m;
                    var tokens = TryGetInt64(row, "totalTokens");

                    if (day > 0 && !string.IsNullOrEmpty(cat))
                    {
                        list.Add(new DailySpendRowDto
                        {
                            Day = day,
                            Category = cat,
                            SpendCents = spendCents,
                            TotalTokens = tokens,
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CursorUsageClient] FetchDailySpendAsync failed: {ex.Message}");
        }

        return list;
    }

    private async Task<JsonDocument?> PostJsonAsync<T>(string pathAndQuery, T payload, CursorAuth auth, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, pathAndQuery));
        request.Headers.TryAddWithoutValidation("Cookie", $"WorkosCursorSessionToken={auth.SessionToken}");
        request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://cursor.com/dashboard");

        var json = JsonSerializer.Serialize(payload);
        request.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Debug.WriteLine($"[CursorUsageClient] POST {pathAndQuery} failed: {(int)response.StatusCode}");
            return null;
        }

        var responseJson = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return JsonDocument.Parse(responseJson);
    }

    public static string? FormatResetInfo(string? iso)
    {
        if (string.IsNullOrEmpty(iso) || !DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var resetDate))
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        var diff = resetDate - now;
        var daysLeft = Math.Max(0, (int)Math.Ceiling(diff.TotalDays));
        var formattedDate = resetDate.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
        var daySuffix = daysLeft == 1 ? "day" : "days";
        return $"Resets in {daysLeft} {daySuffix} on {formattedDate}";
    }

    private static (IReadOnlyList<ModelUsageRow> Models, decimal IncludedSpendDollars) AggregateEvents(JsonDocument? eventsDoc, string? resetsAtIso)
    {
        if (eventsDoc is null)
        {
            return (Array.Empty<ModelUsageRow>(), 0m);
        }

        var root = eventsDoc.RootElement;
        if (!root.TryGetProperty("usageEventsDisplay", out var eventsArray) || eventsArray.ValueKind != JsonValueKind.Array)
        {
            return (Array.Empty<ModelUsageRow>(), 0m);
        }

        long cutoffMs;
        if (!string.IsNullOrEmpty(resetsAtIso) && DateTimeOffset.TryParse(resetsAtIso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var resetDto))
        {
            var cycleStart = resetDto.AddMonths(-1);
            cutoffMs = cycleStart.ToUnixTimeMilliseconds();
        }
        else
        {
            cutoffMs = DateTimeOffset.UtcNow.AddDays(-31).ToUnixTimeMilliseconds();
        }

        var modelTotals = new Dictionary<string, (int Requests, long Tokens, decimal SpendCents)>(StringComparer.OrdinalIgnoreCase);
        decimal includedSpendCents = 0m;

        foreach (var item in eventsArray.EnumerateArray())
        {
            long timestamp = 0;
            if (item.TryGetProperty("timestamp", out var tsEl))
            {
                if (tsEl.ValueKind == JsonValueKind.String && long.TryParse(tsEl.GetString(), out var tsParsed))
                {
                    timestamp = tsParsed;
                }
                else if (tsEl.ValueKind == JsonValueKind.Number && tsEl.TryGetInt64(out var tsNum))
                {
                    timestamp = tsNum;
                }
            }

            if (timestamp > 0 && timestamp < cutoffMs)
            {
                continue;
            }

            string model = "default";
            if (item.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
            {
                var rawModel = modelEl.GetString();
                if (!string.IsNullOrEmpty(rawModel))
                {
                    model = rawModel == "auto" ? "default" : rawModel!;
                }
            }

            long tokens = 0;
            decimal spendCents = 0;

            if (item.TryGetProperty("tokenUsage", out var tokenUsage) && tokenUsage.ValueKind == JsonValueKind.Object)
            {
                var inTok = TryGetInt64(tokenUsage, "inputTokens");
                var outTok = TryGetInt64(tokenUsage, "outputTokens");
                var cacheRead = TryGetInt64(tokenUsage, "cacheReadTokens");
                var cacheWrite = TryGetInt64(tokenUsage, "cacheWriteTokens");
                tokens = inTok + outTok + cacheRead + cacheWrite;

                spendCents = TryGetDecimal(tokenUsage, "totalCents") ?? 0m;
            }

            if (spendCents == 0 && item.TryGetProperty("chargedCents", out _))
            {
                spendCents = TryGetDecimal(item, "chargedCents") ?? 0m;
            }

            var kind = item.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : null;
            var isIncluded = kind is null || !kind.Contains("USAGE_BASED");
            if (isIncluded)
            {
                includedSpendCents += spendCents;
            }

            if (!modelTotals.TryGetValue(model, out var current))
            {
                current = (0, 0, 0m);
            }
            modelTotals[model] = (current.Requests + 1, current.Tokens + tokens, current.SpendCents + spendCents);
        }

        var list = modelTotals
            .Select(kvp => new ModelUsageRow(kvp.Key, kvp.Value.Requests, kvp.Value.Tokens, kvp.Value.SpendCents / 100m))
            .OrderByDescending(m => m.TotalTokens)
            .ToList();

        return (list, includedSpendCents / 100m);
    }

    private static long TryGetInt64(JsonElement element, string prop)
    {
        if (element.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetInt64(out var num)) return num;
            if (val.ValueKind == JsonValueKind.String && long.TryParse(val.GetString(), out var parsed)) return parsed;
        }
        return 0;
    }

    private static decimal? TryGetDecimal(JsonElement element, string prop)
    {
        if (element.TryGetProperty(prop, out var val))
        {
            if (val.ValueKind == JsonValueKind.Number && val.TryGetDecimal(out var num)) return num;
            if (val.ValueKind == JsonValueKind.String && decimal.TryParse(val.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        }
        return null;
    }

    public sealed class CursorAuth
    {
        public CursorAuth(string userId, string sessionToken, string? email = null)
        {
            UserId = userId;
            SessionToken = sessionToken;
            Email = email;
        }

        public string UserId { get; }

        public string SessionToken { get; }

        public string? Email { get; }
    }

    /// <summary>Reads the Cursor desktop app's cached access token straight out of its local SQLite state database.</summary>
    public static CursorAuth? TryReadAuth()
    {
        var dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "User", "globalStorage", "state.vscdb");

        if (!File.Exists(dbPath))
        {
            Debug.WriteLine($"[CursorUsageClient] Cursor state database not found at {dbPath}");
            return null;
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM ItemTable WHERE key IN ('cursorAuth/accessToken', 'cursorAuth/cachedEmail')";
        using var reader = command.ExecuteReader();
        string? jwt = null;
        string? email = null;
        while (reader.Read())
        {
            var key = reader.GetString(0);
            var val = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (key == "cursorAuth/accessToken") jwt = val;
            else if (key == "cursorAuth/cachedEmail") email = val;
        }

        if (jwt is null || jwt.Length == 0)
        {
            Debug.WriteLine("[CursorUsageClient] No cached accessToken found in Cursor state database");
            return null;
        }

        var userId = ExtractUserIdFromJwt(jwt);
        if (userId is null)
        {
            return null;
        }

        return new CursorAuth(userId, $"{userId}%3A%3A{jwt}", email);
    }

    private static string? ExtractUserIdFromJwt(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var payloadJson = DecodeBase64Url(parts[1]);
        using var payload = JsonDocument.Parse(payloadJson);
        var subject = payload.RootElement.TryGetProperty("sub", out var sub) ? sub.GetString() : null;
        if (subject is null || subject.Length == 0)
        {
            return null;
        }

        var segments = subject.Split('|');
        return segments.Length > 1 ? segments[1] : subject;
    }

    private static string DecodeBase64Url(string input)
    {
        var base64 = input.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }

        return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    public static (bool IsTeamMember, long? TeamId) ParseStripeTeamInfo(JsonDocument? stripe)
    {
        if (stripe is null)
        {
            return (false, null);
        }

        var root = stripe.RootElement;
        var isTeamMember = root.TryGetProperty("isTeamMember", out var teamMemberEl) && teamMemberEl.ValueKind == JsonValueKind.True;
        var teamId = root.TryGetProperty("teamId", out var teamIdEl) && teamIdEl.TryGetInt64(out var id) ? id : (long?)null;
        return (isTeamMember, teamId);
    }

    private readonly struct MonthlyUsage
    {
        public MonthlyUsage(decimal usedDollars, decimal limitDollars, bool enabled)
        {
            UsedDollars = usedDollars;
            LimitDollars = limitDollars;
            Enabled = enabled;
        }

        public decimal UsedDollars { get; }

        public decimal LimitDollars { get; }

        public bool Enabled { get; }
    }

    /// <summary>Dollar-based "included usage" summary; only populated for team accounts.</summary>
    private static MonthlyUsage? ParseMonthlyUsage(JsonDocument? summary)
    {
        if (summary is null)
        {
            return null;
        }

        var root = summary.RootElement;
        if (!root.TryGetProperty("individualUsage", out var individualUsage) ||
            !individualUsage.TryGetProperty("overall", out var overall))
        {
            return null;
        }

        var usedCents = TryGetNumber(overall, "used");
        var limitCents = TryGetNumber(overall, "limit");
        if (usedCents is null || limitCents is null)
        {
            return null;
        }

        var enabled = overall.TryGetProperty("enabled", out var enabledEl) && enabledEl.ValueKind == JsonValueKind.True;
        return new MonthlyUsage(usedCents.Value / 100m, limitCents.Value / 100m, enabled);
    }

    private readonly struct RequestTotals
    {
        public RequestTotals(decimal used, decimal limit, string source)
        {
            Used = used;
            Limit = limit;
            Source = source;
        }

        public decimal Used { get; }

        public decimal Limit { get; }

        public string Source { get; }
    }

    /// <summary>Request-count usage/limit, used as a fallback when the dollar-based summary isn't available.</summary>
    private static RequestTotals? ExtractUsageTotals(JsonDocument? usage)
    {
        if (usage is null || usage.RootElement.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        RequestTotals? gpt4Totals = null;
        var candidates = new List<RequestTotals>();

        foreach (var property in usage.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var totals = ExtractBucketTotals(property.Value, property.Name);
            if (totals is null)
            {
                continue;
            }

            if (property.Name == "gpt-4")
            {
                gpt4Totals = totals;
            }
            else
            {
                candidates.Add(totals.Value);
            }
        }

        var best = PickBestTotals(candidates);
        if (gpt4Totals is null)
        {
            return best;
        }

        if (best is null)
        {
            return gpt4Totals;
        }

        var preferBest = best.Value.Limit > gpt4Totals.Value.Limit ||
            (best.Value.Limit == gpt4Totals.Value.Limit && best.Value.Used > gpt4Totals.Value.Used);
        return preferBest ? best : gpt4Totals;
    }

    private static RequestTotals? ExtractBucketTotals(JsonElement bucket, string source)
    {
        var used = TryGetNumber(bucket, "numRequests")
            ?? TryGetNumber(bucket, "usedRequests")
            ?? TryGetNumber(bucket, "requestsUsed")
            ?? TryGetNumber(bucket, "includedRequestsUsed");

        var limit = TryGetNumber(bucket, "maxRequestUsage")
            ?? TryGetNumber(bucket, "maxRequests")
            ?? TryGetNumber(bucket, "requestLimit")
            ?? TryGetNumber(bucket, "includedRequestLimit");

        if (used is null && limit is null)
        {
            return null;
        }

        return new RequestTotals(used ?? 0, limit ?? 0, source);
    }

    private static RequestTotals? PickBestTotals(List<RequestTotals> candidates)
    {
        RequestTotals? best = null;
        foreach (var candidate in candidates)
        {
            if (best is null || Score(candidate) > Score(best.Value) ||
                (Score(candidate) == Score(best.Value) && IsBetter(candidate, best.Value)))
            {
                best = candidate;
            }
        }

        return best;

        static int Score(RequestTotals totals) => (totals.Limit > 0 ? 1 : 0) + (totals.Used > 0 ? 1 : 0);
        static bool IsBetter(RequestTotals a, RequestTotals b) =>
            a.Limit != b.Limit ? a.Limit > b.Limit : a.Used > b.Used;
    }

    private static decimal? TryGetNumber(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number when property.TryGetDecimal(out var number) => number,
            JsonValueKind.String when decimal.TryParse(property.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null,
        };
    }
}

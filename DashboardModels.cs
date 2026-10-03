using System.Text.Json.Serialization;

namespace UsageTray;

public sealed class DashboardState
{
    public long GeneratedAt { get; set; }
    public UsagePayload? Data { get; set; }
    public List<UsageEventDto> Events { get; set; } = new();
    public List<DailySpendRowDto> DailySpend { get; set; } = new();
    public string? ResetsAt { get; set; }
    public bool IsTeamMember { get; set; }
    public bool QuotaAwareEventDisplay { get; set; } = true;
    public string? Error { get; set; }
}

public sealed class UsagePayload
{
    public IncludedRequestsDto IncludedRequests { get; set; } = new();
    public IncludedSpendDto? IncludedSpend { get; set; }
    public OnDemandDto OnDemand { get; set; } = new();
    public string? ResetsAt { get; set; }
}

public sealed class IncludedRequestsDto
{
    public decimal Used { get; set; }
    public decimal Limit { get; set; }
}

public sealed class IncludedSpendDto
{
    public decimal IncludedDollars { get; set; }
    public decimal TotalDollars { get; set; }
}

public sealed class OnDemandDto
{
    public string State { get; set; } = "limited"; // "disabled" | "limited" | "unlimited"
    public decimal SpendDollars { get; set; }
    public decimal? LimitDollars { get; set; }
    public string? Label { get; set; }
    public string? Footer { get; set; }
}

public sealed class UsageEventDto
{
    public long Timestamp { get; set; }
    public string Model { get; set; } = "";
    public string Kind { get; set; } = "Included";
    public long TotalTokens { get; set; }
    public double Requests { get; set; }
    public decimal SpendCents { get; set; }
    public bool MaxMode { get; set; }
}

public sealed class DailySpendRowDto
{
    public long Day { get; set; }
    public string Category { get; set; } = "";
    public decimal SpendCents { get; set; }
    public long TotalTokens { get; set; }
}

public sealed class CursorBenchSnapshot
{
    public string Version { get; set; } = "unknown";
    public string CapturedAt { get; set; } = "";
    public string SourceUrl { get; set; } = "https://cursor.com/cursorbench";
    public List<CursorBenchRowDto> Rows { get; set; } = new();
    public string Provenance { get; set; } = "bundled";
    public bool? Stale { get; set; }
}

public sealed class CursorBenchRowDto
{
    public string Model { get; set; } = "";
    public double Score { get; set; }
    public double CostPerTask { get; set; }
    public double TokensPerTask { get; set; }
    public double StepsPerTask { get; set; }
}

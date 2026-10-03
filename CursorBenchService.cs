using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CursorUsageTray;

public static class CursorBenchService
{
    private const string SourceUrl = "https://cursor.com/cursorbench";
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(24);
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    public static string CacheFilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CursorUsageTray", "cursorbench-cache.json");

    public static async Task<CursorBenchSnapshot> GetSnapshotAsync(bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var cached = ReadCache();
        if (!forceRefresh && cached is not null && IsFresh(cached.CapturedAt))
        {
            cached.Provenance = "cache";
            cached.Stale = false;
            return cached;
        }

        try
        {
            var live = await FetchLiveSnapshotAsync(cancellationToken).ConfigureAwait(false);
            WriteCache(live);
            return live;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CursorBenchService] Live fetch failed: {ex.Message}");
            if (cached is not null)
            {
                cached.Provenance = "cache";
                cached.Stale = true;
                return cached;
            }

            return LoadBundledSnapshot();
        }
    }

    private static bool IsFresh(string? capturedAt)
    {
        if (string.IsNullOrEmpty(capturedAt)) return false;
        if (!DateTimeOffset.TryParse(capturedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return false;
        return DateTimeOffset.UtcNow - date < CacheTtl;
    }

    private static CursorBenchSnapshot? ReadCache()
    {
        try
        {
            var path = CacheFilePath;
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            var snap = JsonSerializer.Deserialize<CursorBenchSnapshot>(json, JsonOptions);
            return snap?.Rows != null && snap.Rows.Count > 0 ? snap : null;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteCache(CursorBenchSnapshot snapshot)
    {
        try
        {
            var path = CacheFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CursorBenchService] Write cache failed: {ex.Message}");
        }
    }

    public static CursorBenchSnapshot LoadBundledSnapshot()
    {
        try
        {
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var path = Path.Combine(appDir, "dashboard", "cursorbench-latest.json");
            if (!File.Exists(path))
            {
                // Fallback to source directory if running in dev environment
                path = Path.Combine(Directory.GetCurrentDirectory(), "dashboard", "cursorbench-latest.json");
            }

            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var snap = JsonSerializer.Deserialize<CursorBenchSnapshot>(json, JsonOptions);
                if (snap != null)
                {
                    snap.Provenance = "bundled";
                    return snap;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CursorBenchService] Load bundled snapshot failed: {ex.Message}");
        }

        return new CursorBenchSnapshot
        {
            Version = "bundled",
            CapturedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            SourceUrl = SourceUrl,
            Provenance = "bundled",
            Rows = new List<CursorBenchRowDto>(),
        };
    }

    private static async Task<CursorBenchSnapshot> FetchLiveSnapshotAsync(CancellationToken cancellationToken)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, SourceUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) CursorTray/1.0");
        req.Headers.TryAddWithoutValidation("Accept", "text/html");

        using var resp = await HttpClient.SendAsync(req, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ParseCursorBenchHtml(html);
    }

    public static CursorBenchSnapshot ParseCursorBenchHtml(string html)
    {
        var versionMatch = Regex.Match(html, @"CursorBench\s+(\d+\.\d+)", RegexOptions.IgnoreCase);
        var version = versionMatch.Success ? versionMatch.Groups[1].Value : "unknown";

        var tableMatch = Regex.Match(html, @"<table[^>]*class=""[^""]*w-full[^""]*table-fixed[^""]*border-collapse[^""]*""[^>]*>[\s\S]*?</table>", RegexOptions.IgnoreCase);
        string tableHtml = "";
        if (tableMatch.Success)
        {
            tableHtml = tableMatch.Value;
        }
        else
        {
            var tables = Regex.Matches(html, @"<table[\s\S]*?</table>", RegexOptions.IgnoreCase);
            foreach (Match tm in tables)
            {
                var h = StripHtml(tm.Value.Length > 1200 ? tm.Value.Substring(0, 1200) : tm.Value).ToLowerInvariant();
                if (h.Contains("model") && h.Contains("score") && h.Contains("cost"))
                {
                    tableHtml = tm.Value;
                    break;
                }
            }
        }

        if (string.IsNullOrEmpty(tableHtml))
        {
            throw new InvalidOperationException("CursorBench leaderboard table not found in HTML");
        }

        var rows = new List<CursorBenchRowDto>();
        var rowMatches = Regex.Matches(tableHtml, @"<tr[^>]*>([\s\S]*?)</tr>", RegexOptions.IgnoreCase);

        foreach (Match rm in rowMatches)
        {
            var cellMatches = Regex.Matches(rm.Groups[1].Value, @"<t[dh][^>]*>([\s\S]*?)</t[dh]>", RegexOptions.IgnoreCase);
            if (cellMatches.Count < 6) continue;

            var model = StripHtml(cellMatches[1].Groups[1].Value);
            if (string.Equals(model, "model", StringComparison.OrdinalIgnoreCase)) continue;

            var score = ParsePercent(cellMatches[2].Groups[1].Value);
            var cost = ParseMoney(cellMatches[3].Groups[1].Value);
            var tokens = ParseDouble(cellMatches[4].Groups[1].Value);
            var steps = ParseDouble(cellMatches[5].Groups[1].Value);

            if (score is null || cost is null || tokens is null || steps is null) continue;

            rows.Add(new CursorBenchRowDto
            {
                Model = model,
                Score = Math.Round(score.Value, 4),
                CostPerTask = cost.Value,
                TokensPerTask = tokens.Value,
                StepsPerTask = steps.Value,
            });
        }

        if (rows.Count == 0)
        {
            throw new InvalidOperationException("No parseable rows in CursorBench leaderboard");
        }

        return new CursorBenchSnapshot
        {
            Version = version,
            CapturedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            SourceUrl = SourceUrl,
            Provenance = "live",
            Rows = rows,
        };
    }

    private static string StripHtml(string text)
    {
        var noComments = Regex.Replace(text, @"<!--[\s\S]*?-->", "");
        var noTags = Regex.Replace(noComments, @"<[^>]+>", " ");
        return Regex.Replace(noTags, @"\s+", " ").Trim();
    }

    private static double? ParsePercent(string text)
    {
        var cleaned = StripHtml(text).Replace("%", "").Trim();
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v / 100.0 : null;
    }

    private static double? ParseMoney(string text)
    {
        var cleaned = Regex.Replace(StripHtml(text), @"[^0-9.-]", "");
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static double? ParseDouble(string text)
    {
        var cleaned = StripHtml(text).Replace(",", "").Trim();
        return double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
    }
}

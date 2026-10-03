using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace CursorUsageTray;

public static class UsageCache
{
    private static readonly string CacheDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CursorUsageTray");

    private static readonly string CacheFilePath = Path.Combine(CacheDirectory, "cache.json");

    public static CursorUsageSnapshot? TryLoad(string? customPath = null)
    {
        try
        {
            var filePath = customPath ?? CacheFilePath;
            if (!File.Exists(filePath))
            {
                return null;
            }

            var json = File.ReadAllText(filePath);
            var data = JsonSerializer.Deserialize<CachedUsageData>(json);
            if (data is null)
            {
                return null;
            }

            var models = data.Models?
                .Select(m => new ModelUsageRow(m.Model, m.Requests, m.TotalTokens, m.SpendDollars))
                .ToList();

            return new CursorUsageSnapshot(
                used: data.Used,
                limit: data.Limit,
                onDemandSpend: data.OnDemandSpend,
                onDemandRatio: data.OnDemandRatio,
                monthlyUsageSubtitle: data.MonthlyUsageSubtitle,
                modelBreakdown: models,
                resetInfoText: data.ResetInfoText);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UsageCache] Failed to load cache: {ex}");
            return null;
        }
    }

    public static void Save(CursorUsageSnapshot snapshot, string? customPath = null)
    {
        try
        {
            var filePath = customPath ?? CacheFilePath;
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var data = new CachedUsageData
            {
                Used = snapshot.Used,
                Limit = snapshot.Limit,
                OnDemandSpend = snapshot.OnDemandSpend,
                OnDemandRatio = snapshot.OnDemandRatio,
                MonthlyUsageSubtitle = snapshot.MonthlyUsageSubtitle,
                ResetInfoText = snapshot.ResetInfoText,
                Models = snapshot.ModelBreakdown.Select(m => new CachedModelRow
                {
                    Model = m.Model,
                    Requests = m.Requests,
                    TotalTokens = m.TotalTokens,
                    SpendDollars = m.SpendDollars,
                }).ToList(),
            };

            var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[UsageCache] Failed to save cache: {ex}");
        }
    }

    private sealed class CachedUsageData
    {
        public decimal Used { get; set; }
        public decimal Limit { get; set; }
        public decimal OnDemandSpend { get; set; }
        public double OnDemandRatio { get; set; }
        public string? MonthlyUsageSubtitle { get; set; }
        public string? ResetInfoText { get; set; }
        public List<CachedModelRow>? Models { get; set; }
    }

    private sealed class CachedModelRow
    {
        public string Model { get; set; } = "";
        public int Requests { get; set; }
        public long TotalTokens { get; set; }
        public decimal SpendDollars { get; set; }
    }
}

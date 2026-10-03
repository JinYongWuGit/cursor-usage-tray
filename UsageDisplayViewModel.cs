using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CursorUsageTray;

public sealed class UsageDisplayViewModel : INotifyPropertyChanged
{
    private decimal used;
    private decimal limit;
    private decimal onDemandSpend;
    private double onDemandRatio;
    private string monthlyUsageSubtitle = "Included total usage (dashboard)";
    private string? resetInfoText;
    private IReadOnlyList<ModelUsageRow> models = Array.Empty<ModelUsageRow>();
    private bool isExpanded;
    private bool isInitialized;

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsInitialized
    {
        get => isInitialized;
        private set
        {
            if (isInitialized == value) return;
            isInitialized = value;
            OnPropertyChanged();
        }
    }

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value)
            {
                return;
            }

            isExpanded = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ExpandArrowText));
            OnPropertyChanged(nameof(ExpandToolTip));
        }
    }

    public string ExpandArrowText => IsExpanded ? "▲" : "▼";

    public string ExpandToolTip => IsExpanded ? "Collapse" : "Expand details";

    public decimal Used
    {
        get => used;
        set => ApplyUsage(value, limit);
    }

    public decimal Limit
    {
        get => limit;
        set => ApplyUsage(used, value);
    }

    public decimal OnDemandSpend
    {
        get => onDemandSpend;
        set
        {
            if (onDemandSpend == value) return;
            onDemandSpend = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OnDemandSpendText));
        }
    }

    public double OnDemandRatio
    {
        get => onDemandRatio;
        set
        {
            var clamped = Math.Max(0, Math.Min(1, value));
            if (Math.Abs(onDemandRatio - clamped) < 0.0001) return;
            onDemandRatio = clamped;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OnDemandBarWidth));
        }
    }

    public double OnDemandBarWidth => Math.Max(4, Math.Min(120, Math.Round(OnDemandRatio * 120)));

    public string OnDemandSpendText => IsInitialized ? OnDemandSpend.ToString("$0.00") : "--";

    public string MonthlyUsageLabel => "Monthly usage";

    public string MonthlyUsageValue => IsInitialized
        ? (Limit > 0 ? $"{AmountText} / {Limit.ToString("$0.00")}" : AmountText)
        : "--";

    public string MonthlyUsageSubtitle
    {
        get => monthlyUsageSubtitle;
        set
        {
            if (monthlyUsageSubtitle == value) return;
            monthlyUsageSubtitle = value;
            OnPropertyChanged();
        }
    }

    public string? ResetInfoText
    {
        get => resetInfoText;
        set
        {
            if (resetInfoText == value) return;
            resetInfoText = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasResetInfo));
        }
    }

    public bool HasResetInfo => !string.IsNullOrEmpty(ResetInfoText);

    public IReadOnlyList<ModelUsageRow> Models
    {
        get => models;
        set
        {
            models = value ?? Array.Empty<ModelUsageRow>();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasModels));
        }
    }

    public bool HasModels => Models.Count > 0;

    public void ToggleExpanded()
    {
        IsExpanded = !IsExpanded;
    }

    /// <summary>Sets used/limit together so dependent properties only raise change notifications once.</summary>
    public void ApplyUsage(decimal newUsed, decimal newLimit)
    {
        if (isInitialized && used == newUsed && limit == newLimit)
        {
            return;
        }

        isInitialized = true;
        used = newUsed;
        limit = newLimit;
        OnPropertyChanged(nameof(IsInitialized));
        OnPropertyChanged(nameof(Used));
        OnPropertyChanged(nameof(Limit));
        OnPropertyChanged(nameof(AmountText));
        OnPropertyChanged(nameof(LimitText));
        OnPropertyChanged(nameof(IconText));
        OnPropertyChanged(nameof(PercentUsed));
        OnPropertyChanged(nameof(PercentText));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(MonthlyUsageValue));
        OnPropertyChanged(nameof(OnDemandSpendText));
    }

    /// <summary>Applies complete snapshot containing detailed usage breakdown and reset date.</summary>
    public void ApplyDetailedUsage(CursorUsageSnapshot snapshot)
    {
        ApplyUsage(snapshot.Used, snapshot.Limit);
        OnDemandSpend = snapshot.OnDemandSpend;
        OnDemandRatio = snapshot.OnDemandRatio;
        MonthlyUsageSubtitle = snapshot.MonthlyUsageSubtitle;
        ResetInfoText = snapshot.ResetInfoText;
        Models = snapshot.ModelBreakdown;
    }

    public string AmountText => IsInitialized ? Used.ToString("$0.00") : "--";

    public string LimitText => IsInitialized ? $"/ {Limit:$0}" : "";

    public string IconText => IsInitialized ? Used.ToString("$0") : "--";

    public string SummaryText => IsInitialized ? $"{AmountText} {LimitText}" : "Loading...";

    public double PercentUsed => (!IsInitialized || Limit == 0) ? 0 : (double)(Used / Limit * 100m);

    public string PercentText => (!IsInitialized || Limit == 0) ? "-- used" : $"{Used / Limit:P1} used";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using System.ComponentModel;
using System.Windows.Data;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceWorkspaceViewModel
{
    public ICollectionView ProblemOrders { get; private set; } = null!;
    private string _problemSearch = "", _problemFilter = "Усі";
    private string _problemPeriod = "Останні 14 днів";
    private DateTimeOffset _problemNow = DateTimeOffset.UtcNow;
    public event EventHandler? ProblemScopeChanged;
    public IReadOnlyList<string> ProblemPeriods { get; } = ["Останні 14 днів", "За весь час"];
    public string ProblemPeriod
    {
        get => _problemPeriod;
        set
        {
            if (!ProblemPeriods.Contains(value) || !SetProperty(ref _problemPeriod, value)) return;
            RefreshProblemView();
            ProblemScopeChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    public IEnumerable<MarketplaceOrderRowViewModel> ProblemScopeRows => _orderRows.Where(InProblemPeriod);
    public IReadOnlyList<string> ProblemFilters { get; } = ["Усі", "Зберігання", "Понад 24 год без чека / ТТН"];
    public string ProblemSearch { get => _problemSearch; set { if (SetProperty(ref _problemSearch, value ?? "")) RefreshProblemView(); } }
    public string ProblemFilter { get => _problemFilter; set { if (SetProperty(ref _problemFilter, value)) RefreshProblemView(); } }
    public string ProblemSummary => $"Проблемних замовлень: {ProblemOrders.Cast<object>().Count()}. " +
        (ProblemPeriod == "За весь час" ? "За весь час у збереженому архіві. " :
            $"Період: {DateRangeBuilder.KyivDate(_problemNow).AddDays(-13):dd.MM.yyyy} — {DateRangeBuilder.KyivDate(_problemNow):dd.MM.yyyy}. ") +
        $"Без актуального статусу доставки: {ProblemScopeRows.Count(row => row.ProblemTrackingPending)}. " +
        "Автооновлення — раз на 30 хв. Старі замовлення зберігаються в кеші; календар угорі не змінює цей список.";

    private bool InProblemPeriod(MarketplaceOrderRowViewModel row) => ProblemPeriod == "За весь час" ||
        row.Model.CreatedAt is { } created && DateRangeBuilder.KyivDate(created) >= DateRangeBuilder.KyivDate(_problemNow).AddDays(-13) &&
        DateRangeBuilder.KyivDate(created) <= DateRangeBuilder.KyivDate(_problemNow);

    private void InitializeProblems()
    {
        ProblemOrders = new ListCollectionView(_orderRows) { Filter = value => value is MarketplaceOrderRowViewModel row &&
            InProblemPeriod(row) && row.ProblemReason.Length > 0 && (ProblemFilter == "Усі" ||
                ProblemFilter == "Зберігання" && row.ProblemPriority < 3 ||
                ProblemFilter == "Понад 24 год без чека / ТТН" && row.ProblemReason.Contains("Понад 24 год")) &&
            (string.IsNullOrWhiteSpace(ProblemSearch) || new[] { row.Number, row.TrackingDisplay, row.BuyerDisplay,
                row.StoreName, row.Marketplace, row.ProblemReason }.Any(s => s.Contains(ProblemSearch.Trim(), StringComparison.CurrentCultureIgnoreCase))) };
        ProblemOrders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.ProblemPriority), ListSortDirection.Ascending));
        ProblemOrders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.CreatedAt), ListSortDirection.Ascending));
    }

    public void RefreshProblems(DateTimeOffset? now = null)
    {
        if (_updatingOrderRows) return;
        _problemNow = now ?? DateTimeOffset.UtcNow;
        // Existing accepted receipt IDs outside the visible receipt range still count as issued.
        var saved = _automaticCache.AccountContext == _account
            ? _automaticCache.ExactLinks.Values.Select(link => _deduplication.NormalizeKey(link.Order)).ToHashSet() : [];
        foreach (var row in _orderRows) row.UpdateProblems(_problemNow, saved.Contains(row.Key));
        RefreshProblemView();
    }

    private void RefreshProblemView()
    {
        ProblemOrders.Refresh();
        OnPropertyChanged(nameof(ProblemSummary));
    }
}

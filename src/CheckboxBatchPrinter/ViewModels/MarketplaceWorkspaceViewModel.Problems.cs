using System.ComponentModel;
using System.Windows.Data;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceWorkspaceViewModel
{
    public ICollectionView ProblemOrders { get; private set; } = null!;
    private string _problemSearch = "", _problemFilter = "Усі";
    public IReadOnlyList<string> ProblemFilters { get; } = ["Усі", "Зберігання", "Понад 24 год без чека / ТТН"];
    public string ProblemSearch { get => _problemSearch; set { if (SetProperty(ref _problemSearch, value ?? "")) RefreshProblemView(); } }
    public string ProblemFilter { get => _problemFilter; set { if (SetProperty(ref _problemFilter, value)) RefreshProblemView(); } }
    public string ProblemSummary => $"Проблемних замовлень: {ProblemOrders.Cast<object>().Count()}. Перевірено {_orderRows.Count} завантажених замовлень. " +
        $"Без актуального статусу доставки: {_orderRows.Count(row => row.ProblemTrackingPending)}. " +
        "Зберігання: попередження за 1 день до платного; довго не забирають: від 5 днів. Фільтр дат угорі не приховує цей список.";

    private void InitializeProblems()
    {
        ProblemOrders = new ListCollectionView(_orderRows) { Filter = value => value is MarketplaceOrderRowViewModel row &&
            row.ProblemReason.Length > 0 && (ProblemFilter == "Усі" ||
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
        // Existing accepted receipt IDs outside the visible receipt range still count as issued.
        var saved = _automaticCache.AccountContext == _account
            ? _automaticCache.ExactLinks.Values.Select(link => _deduplication.NormalizeKey(link.Order)).ToHashSet() : [];
        foreach (var row in _orderRows) row.UpdateProblems(now ?? DateTimeOffset.UtcNow, saved.Contains(row.Key));
        RefreshProblemView();
    }

    private void RefreshProblemView()
    {
        ProblemOrders.Refresh();
        OnPropertyChanged(nameof(ProblemSummary));
    }
}

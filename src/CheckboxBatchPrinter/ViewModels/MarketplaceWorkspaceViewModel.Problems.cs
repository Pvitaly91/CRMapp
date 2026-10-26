using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceWorkspaceViewModel
{
    public ICollectionView ProblemOrders { get; private set; } = null!;
    public ICommand CopyShipmentTrackingCommand { get; private set; } = null!;
    private string _problemSearch = "", _problemFilter = "Усі";
    private string _problemPeriod = "Останні 14 днів";
    private DateTime? _problemDateFrom = DateRangeBuilder.TodayKyiv.AddDays(-13), _problemDateTo = DateRangeBuilder.TodayKyiv;
    private DateTimeOffset _problemNow = DateTimeOffset.UtcNow;
    public event EventHandler? ProblemScopeChanged;
    public IReadOnlyList<string> ProblemPeriods { get; } = ["Останні 14 днів", "За весь час", "За календарем"];
    public string ProblemPeriod
    {
        get => _problemPeriod;
        set
        {
            if (!ProblemPeriods.Contains(value) || !SetProperty(ref _problemPeriod, value)) return;
            RefreshProblemScope();
        }
    }
    public bool IsProblemCustomPeriod => ProblemPeriod == "За календарем";
    public DateTime? ProblemDateFrom
    {
        get => _problemDateFrom;
        set { if (SetProperty(ref _problemDateFrom, value?.Date)) RefreshProblemScope(); }
    }
    public DateTime? ProblemDateTo
    {
        get => _problemDateTo;
        set { if (SetProperty(ref _problemDateTo, value?.Date)) RefreshProblemScope(); }
    }
    public bool HasValidProblemDateRange => !IsProblemCustomPeriod ||
        ProblemDateFrom is { } from && ProblemDateTo is { } to && from <= to;
    public string ProblemDateRangeHint => HasValidProblemDateRange ? "" :
        ProblemDateFrom is null || ProblemDateTo is null ? "Оберіть обидві дати періоду." : "Дата «до» не може бути раніше дати «від».";
    public (DateOnly From, DateOnly To) ProblemRefreshRange
    {
        get
        {
            if (!HasValidProblemDateRange) throw new InvalidOperationException(ProblemDateRangeHint);
            if (IsProblemCustomPeriod) return (DateOnly.FromDateTime(ProblemDateFrom!.Value), DateOnly.FromDateTime(ProblemDateTo!.Value));
            var to = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
            return (to.AddDays(-13), to);
        }
    }
    public IEnumerable<MarketplaceOrderRowViewModel> ProblemScopeRows => _orderRows.Where(InProblemPeriod);
    public IReadOnlyList<string> ProblemFilters { get; } = ["Усі", "Зберігання", "Понад 24 год без чека / ТТН"];
    public string ProblemSearch { get => _problemSearch; set { if (SetProperty(ref _problemSearch, value ?? "")) RefreshProblemView(); } }
    public string ProblemFilter { get => _problemFilter; set { if (SetProperty(ref _problemFilter, value)) RefreshProblemView(); } }
    public string ProblemSummary => $"Проблемних замовлень: {ProblemOrders.Cast<object>().Count()}. " +
        (ProblemPeriod == "За весь час" ? "За весь час у збереженому архіві. " :
            IsProblemCustomPeriod ? HasValidProblemDateRange ? $"Період: {ProblemDateFrom:dd.MM.yyyy} — {ProblemDateTo:dd.MM.yyyy}. " : ProblemDateRangeHint + " " :
            $"Період: {DateRangeBuilder.KyivDate(_problemNow).AddDays(-13):dd.MM.yyyy} — {DateRangeBuilder.KyivDate(_problemNow):dd.MM.yyyy}. ") +
        $"Без актуального статусу доставки: {ProblemScopeRows.Count(row => row.ProblemTrackingPending)}. " +
        "Автооновлення ТТН — раз на 30 хв. Старі замовлення зберігаються в кеші; власний інтервал — «За календарем».";

    private bool InProblemPeriod(MarketplaceOrderRowViewModel row)
    {
        if (ProblemPeriod == "За весь час") return true;
        if (!HasValidProblemDateRange || row.Model.CreatedAt is not { } created) return false;
        var from = IsProblemCustomPeriod ? DateOnly.FromDateTime(ProblemDateFrom!.Value) : DateRangeBuilder.KyivDate(_problemNow).AddDays(-13);
        var to = IsProblemCustomPeriod ? DateOnly.FromDateTime(ProblemDateTo!.Value) : DateRangeBuilder.KyivDate(_problemNow);
        var date = DateRangeBuilder.KyivDate(created);
        return date >= from && date <= to;
    }

    private void RefreshProblemScope()
    {
        OnPropertyChanged(nameof(IsProblemCustomPeriod));
        OnPropertyChanged(nameof(HasValidProblemDateRange));
        OnPropertyChanged(nameof(ProblemDateRangeHint));
        RefreshProblemView();
        ProblemScopeChanged?.Invoke(this, EventArgs.Empty);
    }

    private void InitializeProblems()
    {
        CopyShipmentTrackingCommand = new RelayCommand(parameter =>
        {
            if (CanCopyShipmentTracking(parameter)) _dialogs.CopyText(((OrderShipmentLink)parameter!).TrackingNumber);
        }, CanCopyShipmentTracking);
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
        ((RelayCommand)CopyShipmentTrackingCommand).RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(ProblemSummary));
    }

    private bool CanCopyShipmentTracking(object? parameter) => parameter is OrderShipmentLink link &&
        link.TrackingNumber.Length > 0 && _orderRows.Contains(link.Row) && link.Row.Model.Shipments.Contains(link.Shipment);
}

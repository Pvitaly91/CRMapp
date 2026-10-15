using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

/// <summary>Manual selection over already loaded receipts; no requests or link changes.</summary>
public sealed class ReceiptPickerViewModel : ObservableObject
{
    private readonly ObservableCollection<ReceiptRowViewModel> _rows;
    private readonly Func<ReceiptRowViewModel, Task>? _preview;
    private string _searchText = "";
    private decimal? _searchAmount;
    private DateTime? _dateFrom, _dateTo;
    private ReceiptRowViewModel? _selectedReceipt;
    private bool _isPreviewBusy;
    private string _previewError = "";

    public ReceiptPickerViewModel(MarketplaceOrder order, IReadOnlyList<ReceiptRowViewModel> receipts,
        Func<ReceiptRowViewModel, Task>? preview = null)
    {
        Order = order;
        _preview = preview;
        _rows = new ObservableCollection<ReceiptRowViewModel>(receipts
            .Where(row => !string.IsNullOrWhiteSpace(row.Id))
            .DistinctBy(row => row.Id, StringComparer.OrdinalIgnoreCase));
        Receipts = new ListCollectionView(_rows) { Filter = MatchesFilter };
        Receipts.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.LocalDateTime), ListSortDirection.Descending));
        Receipts.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.Id), ListSortDirection.Ascending));
        LinkCommand = new RelayCommand(_ => Choose(), _ => CanLink);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, parameter =>
            _preview is not null && !IsPreviewBusy && parameter is ReceiptRowViewModel row && Receipts.Contains(row));
        ClearDateFromCommand = new RelayCommand(_ => DateFrom = null, _ => CanEditFilters && DateFrom.HasValue);
        ClearDateToCommand = new RelayCommand(_ => DateTo = null, _ => CanEditFilters && DateTo.HasValue);
    }

    public event Action<string>? ChoiceRequested;
    public MarketplaceOrder Order { get; }
    public ICollectionView Receipts { get; }
    public ICommand LinkCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand ClearDateFromCommand { get; }
    public ICommand ClearDateToCommand { get; }
    public string OrderHeading => "Замовлення №" + (string.IsNullOrWhiteSpace(Order.Number) ? Order.Key.OrderId : Order.Number);
    public string OrderDate => Order.CreatedAt is { } date
        ? TimeZoneInfo.ConvertTime(date, DateRangeBuilder.KyivZone).ToString("dd.MM.yyyy HH:mm", CultureInfo.CurrentCulture)
        : "Дата невідома";
    public string OrderAmount => Order.Total is { } total
        ? total.ToString("N2", CultureInfo.CurrentCulture) + (string.IsNullOrWhiteSpace(Order.Currency) ? " (валюта невідома)" : " " + Order.Currency)
        : "Сума невідома";
    public string ResultCount => $"Доступних чеків: {Receipts.Cast<ReceiptRowViewModel>().Count()} з {_rows.Count}";
    public bool HasInvalidDateRange => DateFrom is { } from && DateTo is { } to && from.Date > to.Date;
    public string FilterMessage => HasInvalidDateRange
        ? "Дата «до» не може бути раніше дати «від»."
        : _rows.Count == 0 ? "Немає доступних чеків серед уже завантажених."
        : Receipts.IsEmpty ? "За цими датами та пошуком чеків не знайдено. Змініть або очистьте фільтри."
        : "";
    public bool CanEditFilters => !IsPreviewBusy;
    public bool CanLink => !IsPreviewBusy && SelectedReceipt is { } row && Receipts.Contains(row);
    public bool IsPreviewBusy => _isPreviewBusy;
    public string PreviewError
    {
        get => _previewError;
        private set => SetProperty(ref _previewError, value);
    }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!CanEditFilters || !SetProperty(ref _searchText, value ?? "")) return;
            _searchAmount = ReceiptTabViewModel.ParseSearchAmount(_searchText);
            Refresh();
        }
    }
    public DateTime? DateFrom
    {
        get => _dateFrom;
        set { if (CanEditFilters && SetProperty(ref _dateFrom, value?.Date)) Refresh(); }
    }
    public DateTime? DateTo
    {
        get => _dateTo;
        set { if (CanEditFilters && SetProperty(ref _dateTo, value?.Date)) Refresh(); }
    }
    public ReceiptRowViewModel? SelectedReceipt
    {
        get => _selectedReceipt;
        set
        {
            if (IsPreviewBusy || !SetProperty(ref _selectedReceipt, value)) return;
            OnPropertyChanged(nameof(CanLink));
            ((RelayCommand)LinkCommand).RaiseCanExecuteChanged();
        }
    }

    private bool MatchesFilter(object value)
    {
        if (value is not ReceiptRowViewModel row || HasInvalidDateRange) return false;
        if (DateFrom.HasValue || DateTo.HasValue)
        {
            if (row.Model.DisplayDate is not { } timestamp) return false;
            var date = DateRangeBuilder.KyivDate(timestamp);
            if (DateFrom is { } from && date < DateOnly.FromDateTime(from) ||
                DateTo is { } to && date > DateOnly.FromDateTime(to)) return false;
        }
        var query = SearchText.Trim();
        return query.Length == 0 || row.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.FiscalCode.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.Serial.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            _searchAmount is { } amount && row.Model.TotalKnown && row.Total == amount;
    }
    private void Refresh()
    {
        Receipts.Refresh();
        if (SelectedReceipt is { } selected && !Receipts.Contains(selected)) SelectedReceipt = null;
        foreach (var property in new[] { nameof(ResultCount), nameof(HasInvalidDateRange), nameof(FilterMessage), nameof(CanLink) })
            OnPropertyChanged(property);
        RaiseCommands();
    }
    private void Choose()
    {
        if (CanLink && SelectedReceipt is { } row) ChoiceRequested?.Invoke(row.Id);
    }
    private async Task PreviewAsync(object? parameter)
    {
        if (_preview is null || IsPreviewBusy || parameter is not ReceiptRowViewModel row || !Receipts.Contains(row)) return;
        SetPreviewBusy(true);
        PreviewError = "";
        try { await _preview(row); }
        catch (OperationCanceledException) { PreviewError = "Перегляд чека скасовано."; }
        catch (Exception) { PreviewError = "Не вдалося відкрити чек. Повторіть перегляд."; }
        finally { SetPreviewBusy(false); }
    }
    private void SetPreviewBusy(bool value)
    {
        if (!SetProperty(ref _isPreviewBusy, value, nameof(IsPreviewBusy))) return;
        OnPropertyChanged(nameof(CanEditFilters));
        OnPropertyChanged(nameof(CanLink));
        RaiseCommands();
    }
    private void RaiseCommands()
    {
        ((RelayCommand)LinkCommand).RaiseCanExecuteChanged();
        ((AsyncRelayCommand)PreviewCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearDateFromCommand).RaiseCanExecuteChanged();
        ((RelayCommand)ClearDateToCommand).RaiseCanExecuteChanged();
    }
}

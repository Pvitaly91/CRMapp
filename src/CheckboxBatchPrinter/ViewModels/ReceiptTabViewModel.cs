using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

/// <summary>Independent UI state over shared Checkbox rows; no requests or storage.</summary>
public sealed class ReceiptTabViewModel : ObservableObject, IDisposable
{
    private readonly ObservableCollection<ReceiptRowViewModel> _rows;
    private readonly HashSet<ReceiptRowViewModel> _observedRows = [];
    private bool _headerSelectionBusy, _changingHeaderSelection, _disposed;
    private string _searchText = "";
    private decimal? _searchAmount;
    private static readonly Regex PlainAmount = new(
        @"\A[+-]?(?:[0-9]+|[0-9]{1,3}(?:[ \u00a0\u202f][0-9]{3})+)(?:[.,][0-9]{1,2})?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex EnglishAmount = new(
        @"\A[+-]?[0-9]{1,3}(?:,[0-9]{3})+(?:\.[0-9]{1,2})?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex EuropeanAmount = new(
        @"\A[+-]?[0-9]{1,3}(?:\.[0-9]{3})+(?:,[0-9]{1,2})?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private ReceiptTypeOption? _selectedType;
    private ReceiptRowViewModel? _selectedReceipt;
    private readonly MarketplaceWorkspaceViewModel? _marketplace;
    private DateOnly? _dateFrom, _dateTo;

    public ReceiptTabViewModel(ObservableCollection<ReceiptRowViewModel> rows, ReceiptTypeOption initialType,
        bool usesOrders = false, MarketplaceWorkspaceViewModel? marketplace = null)
    {
        UsesOrders = usesOrders;
        _rows = rows;
        _marketplace = marketplace;
        _selectedType = initialType;
        ToggleHeaderSelectionCommand = new RelayCommand(_ => ToggleHeaderSelection(), _ => CanToggleHeaderSelection);
        SelectPrintCandidatesCommand = new RelayCommand(_ => SelectPrintCandidates(), _ => CanSelectPrintCandidates);
        // Never use GetDefaultView here: WPF would share filters/current-item/sorting.
        View = new ListCollectionView(rows) { Filter = MatchesFilter };
        View.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.LocalDate), ListSortDirection.Descending));
        View.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.LocalTime), ListSortDirection.Descending));
        ((INotifyCollectionChanged)View).CollectionChanged += ViewChanged;
        _rows.CollectionChanged += RowsChanged;
        ObserveRows();
    }

    public bool UsesOrders { get; }
    public ICollectionView View { get; }
    public ICommand ToggleHeaderSelectionCommand { get; }
    public ICommand SelectPrintCandidatesCommand { get; }
    public bool IsHeaderSelectionBusy
    {
        get => _headerSelectionBusy;
        set { if (SetProperty(ref _headerSelectionBusy, value)) UpdateHeaderSelection(); }
    }
    public bool CanToggleHeaderSelection => !_disposed && !IsHeaderSelectionBusy &&
        (View.Cast<ReceiptRowViewModel>().Any() || _rows.Any(IsMarked));
    public bool CanSelectPrintCandidates => !_disposed && !IsHeaderSelectionBusy &&
        View.Cast<ReceiptRowViewModel>().Any();
    public bool? HeaderSelectionState
    {
        get
        {
            var visible = View.Cast<ReceiptRowViewModel>().ToArray();
            if (visible.Length > 0 && visible.All(IsMarked)) return true;
            return _rows.Any(IsMarked) ? null : false;
        }
    }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value ?? "")) return;
            _searchAmount = ParseSearchAmount(_searchText);
            View.Refresh();
        }
    }
    public ReceiptTypeOption? SelectedType
    {
        get => _selectedType;
        set { if (SetProperty(ref _selectedType, value)) View.Refresh(); }
    }
    public ReceiptRowViewModel? SelectedReceipt
    {
        get => _selectedReceipt;
        set => SetProperty(ref _selectedReceipt, value);
    }

    public bool IsMarked(ReceiptRowViewModel row) => UsesOrders ? row.IsSelectedForOrders : row.IsSelected;
    public void SetDisplayDates(DateOnly? from, DateOnly? to)
    {
        if (_dateFrom == from && _dateTo == to) return;
        _dateFrom = from;
        _dateTo = to;
        View.Refresh();
        if (SelectedReceipt is not null && !View.Contains(SelectedReceipt)) SelectedReceipt = null;
    }

    public void SetMarked(ReceiptRowViewModel row, bool value)
    {
        if (UsesOrders) row.IsSelectedForOrders = value;
        else row.IsSelected = value;
    }

    private void ToggleHeaderSelection()
    {
        if (!CanToggleHeaderSelection) return;
        var visible = View.Cast<ReceiptRowViewModel>().ToArray();
        var clear = visible.Length == 0 || visible.All(IsMarked);
        _changingHeaderSelection = true;
        try
        {
            foreach (var row in clear ? _rows.ToArray() : visible) SetMarked(row, !clear);
        }
        finally { _changingHeaderSelection = false; UpdateHeaderSelection(); }
    }
    private void SelectPrintCandidates()
    {
        if (!CanSelectPrintCandidates) return;
        var visible = View.Cast<ReceiptRowViewModel>().ToArray();
        _changingHeaderSelection = true;
        try
        {
            foreach (var row in visible)
                SetMarked(row, row.OrderMatch?.Order is { IsCanceled: false } &&
                    row.PrintStatus is PrintItemStatus.Waiting or PrintItemStatus.Error &&
                    string.Equals(row.Model.Status, "DONE", StringComparison.OrdinalIgnoreCase));
        }
        finally { _changingHeaderSelection = false; UpdateHeaderSelection(); }
    }
    private void RowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        ObserveRows();
        UpdateHeaderSelection();
    }
    private void ObserveRows()
    {
        var current = _rows.ToHashSet();
        foreach (var row in _observedRows.Except(current).ToArray())
        {
            row.SelectionChanged -= RowSelectionChanged;
            row.PropertyChanged -= RowPropertyChanged;
            _observedRows.Remove(row);
        }
        foreach (var row in current.Except(_observedRows).ToArray())
        {
            row.SelectionChanged += RowSelectionChanged;
            row.PropertyChanged += RowPropertyChanged;
            _observedRows.Add(row);
        }
    }
    private void RowSelectionChanged(object? sender, EventArgs e) => UpdateHeaderSelection();
    private void RowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(ReceiptRowViewModel.PrintStatus)
            or nameof(ReceiptRowViewModel.OrderMatch) or nameof(ReceiptRowViewModel.Model)
            or nameof(ReceiptRowViewModel.Status))
            UpdateHeaderSelection();
    }
    private void ViewChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateHeaderSelection();
    private void UpdateHeaderSelection()
    {
        if (_changingHeaderSelection) return;
        OnPropertyChanged(nameof(HeaderSelectionState));
        OnPropertyChanged(nameof(CanToggleHeaderSelection));
        OnPropertyChanged(nameof(CanSelectPrintCandidates));
        ((RelayCommand)ToggleHeaderSelectionCommand).RaiseCanExecuteChanged();
        ((RelayCommand)SelectPrintCandidatesCommand).RaiseCanExecuteChanged();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _rows.CollectionChanged -= RowsChanged;
        ((INotifyCollectionChanged)View).CollectionChanged -= ViewChanged;
        foreach (var row in _observedRows)
        {
            row.SelectionChanged -= RowSelectionChanged;
            row.PropertyChanged -= RowPropertyChanged;
        }
        _observedRows.Clear();
        UpdateHeaderSelection();
    }

    private bool MatchesFilter(object item)
    {
        if (item is not ReceiptRowViewModel row) return false;
        if (_dateFrom.HasValue || _dateTo.HasValue)
        {
            if (row.Model.DisplayDate is not { } timestamp) return false;
            var date = DateRangeBuilder.KyivDate(timestamp);
            if (_dateFrom is { } from && date < from || _dateTo is { } to && date > to) return false;
        }
        // The basic Checkbox screen never consults a marketplace filter or OrderMatch.
        if (UsesOrders && _marketplace?.MatchesFilter(row) == false) return false;
        if (!string.IsNullOrEmpty(SelectedType?.Value) && !string.Equals(row.RawType, SelectedType.Value, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return row.Id.Contains(query, StringComparison.OrdinalIgnoreCase) ||
               (_searchAmount is { } amount && row.Model.TotalKnown && row.Total == amount) ||
               row.FiscalCode.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Serial.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Payment.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.CashRegister.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Type.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               (UsesOrders && row.MatchesOrderSearch(query));
    }

    internal static decimal? ParseSearchAmount(string query)
    {
        if (query.Length > 128) return null;
        var text = query.Trim();
        string normalized;
        if (PlainAmount.IsMatch(text))
            normalized = text.Replace(" ", "").Replace("\u00a0", "").Replace("\u202f", "").Replace(',', '.');
        else if (EnglishAmount.IsMatch(text)) normalized = text.Replace(",", "");
        else if (EuropeanAmount.IsMatch(text)) normalized = text.Replace(".", "").Replace(',', '.');
        else return null;
        return decimal.TryParse(normalized, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var amount) ? amount : null;
    }
}

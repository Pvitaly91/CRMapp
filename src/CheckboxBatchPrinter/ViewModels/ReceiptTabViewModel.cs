using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

/// <summary>Independent UI state over shared Checkbox rows; no requests or storage.</summary>
public sealed class ReceiptTabViewModel : ObservableObject
{
    private string _searchText = "";
    private ReceiptTypeOption? _selectedType;
    private ReceiptRowViewModel? _selectedReceipt;
    private readonly MarketplaceWorkspaceViewModel? _marketplace;
    private DateOnly? _dateFrom, _dateTo;

    public ReceiptTabViewModel(ObservableCollection<ReceiptRowViewModel> rows, ReceiptTypeOption initialType,
        bool usesOrders = false, MarketplaceWorkspaceViewModel? marketplace = null)
    {
        UsesOrders = usesOrders;
        _marketplace = marketplace;
        _selectedType = initialType;
        // Never use GetDefaultView here: WPF would share filters/current-item/sorting.
        View = new ListCollectionView(rows) { Filter = MatchesFilter };
        View.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.LocalDate), ListSortDirection.Descending));
        View.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.LocalTime), ListSortDirection.Descending));
    }

    public bool UsesOrders { get; }
    public ICollectionView View { get; }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) View.Refresh(); }
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
        return row.FiscalCode.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Serial.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Payment.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.CashRegister.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Type.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               (UsesOrders && row.MatchesOrderSearch(query));
    }
}

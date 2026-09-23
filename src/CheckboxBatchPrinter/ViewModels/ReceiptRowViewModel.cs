using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

public sealed class ReceiptRowViewModel : ObservableObject
{
    private bool _isSelected;
    private bool _isSelectedForOrders;
    private PrintItemStatus _printStatus = PrintItemStatus.Waiting;
    private string _printError = string.Empty;
    private ReceiptOrderMatch? _orderMatch;

    public ReceiptRowViewModel(ReceiptRecord model) => Model = model;

    public event EventHandler? SelectionChanged;
    public ReceiptRecord Model { get; }
    public string Id => Model.Id;
    public string RawType => Model.Type;
    public string Type => ReceiptTypes.ToUkrainian(Model.Type);
    public string Status => ReceiptStatuses.ToUkrainian(Model.Status);
    public DateTime? LocalDate => Model.DisplayDate is { } date ? TimeZoneInfo.ConvertTime(date, DateRangeBuilder.KyivZone).Date : null;
    public string LocalTime => Model.DisplayDate is { } date ? TimeZoneInfo.ConvertTime(date, DateRangeBuilder.KyivZone).ToString("HH:mm:ss") : "—";
    public string FiscalCode => string.IsNullOrWhiteSpace(Model.FiscalCode) ? "—" : Model.FiscalCode;
    public string Serial => Model.Serial == 0 ? "—" : Model.Serial.ToString();
    public decimal Total => Model.TotalSum;
    public string Payment => Model.PaymentDisplay;
    public string CashRegister => string.IsNullOrWhiteSpace(Model.CashRegisterFiscalNumber) ? "—" : Model.CashRegisterFiscalNumber;

    public ReceiptOrderMatch? OrderMatch
    {
        get => _orderMatch;
        set
        {
            if (!SetProperty(ref _orderMatch, value)) return;
            foreach (var property in new[] { nameof(Marketplace), nameof(OrderNumber), nameof(OrderBuyer), nameof(OrderStatus), nameof(OrderTracking), nameof(LinkStatus), nameof(LinkExplanation) })
                OnPropertyChanged(property);
        }
    }
    public string Marketplace => OrderMatch?.Order?.Key.Marketplace.ToString() ?? "";
    public string OrderNumber => OrderMatch?.Order?.Number ?? "";
    public string OrderBuyer => OrderMatch?.Order?.Buyer?.Name ?? "";
    public string OrderStatus => OrderMatch?.Order?.Status ?? "";
    public string OrderTracking => OrderMatch?.Order?.TrackingDisplay ?? "";
    public string LinkExplanation => OrderMatch?.Explanation ?? "Не перевірено";
    public string LinkStatus => OrderMatch?.State switch
    {
        ReceiptLinkState.Exact => OrderMatch.Explanation,
        ReceiptLinkState.Manual => "Прив’язано вручну",
        ReceiptLinkState.Candidates => "Є кандидати",
        ReceiptLinkState.NotFound => "Не знайдено у діапазоні",
        ReceiptLinkState.Conflict => "Конфлікт",
        ReceiptLinkState.Incomplete => "Перевірка неповна",
        _ => "Не перевірено"
    };
    public bool MatchesOrderSearch(string query) => OrderMatch?.Order is { } order &&
        new[] { order.Number, order.Buyer?.Name, order.Buyer?.Phone, order.Recipient?.Name, order.Recipient?.Phone,
            order.TrackingDisplay, order.StoreName }.Any(value => value?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PrintItemStatus PrintStatus
    {
        get => _printStatus;
        set { if (SetProperty(ref _printStatus, value)) OnPropertyChanged(nameof(PrintStatusText)); }
    }

    public bool IsSelectedForOrders
    {
        get => _isSelectedForOrders;
        set
        {
            if (!SetProperty(ref _isSelectedForOrders, value)) return;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string PrintError
    {
        get => _printError;
        set => SetProperty(ref _printError, value);
    }

    public string PrintStatusText => PrintStatus switch
    {
        PrintItemStatus.Waiting => "Очікує",
        PrintItemStatus.Downloading => "Завантаження",
        PrintItemStatus.Printing => "Друкується",
        PrintItemStatus.Done => "Надруковано",
        PrintItemStatus.Error => "Помилка",
        _ => "—"
    };
}

public sealed record ReceiptTypeOption(string Value, string Display)
{
    public override string ToString() => Display;
}

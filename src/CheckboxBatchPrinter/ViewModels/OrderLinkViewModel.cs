using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed class OrderLinkViewModel : ObservableObject
{
    private readonly ObservableCollection<OrderLinkRow> _rows;
    private string _search = "";
    private OrderMarketplaceFilter _marketplaceFilter;
    private OrderShopFilter _shopFilter;
    private OrderLinkRow? _selected;

    public OrderLinkViewModel(ReceiptRowViewModel receipt, ReceiptDetails? details,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<MarketplaceOrder> candidates)
    {
        Receipt = receipt;
        ReceiptItems = details?.Items ?? [];
        ReceiptDetailsHint = details is null ? "Товари чека недоступні. Звірте чек за сумою, датою та номером."
            : ReceiptItems.Count == 0 ? "У відповіді Checkbox немає товарних позицій." : "Товарні позиції з Checkbox.";

        var candidateKeys = candidates.Select(order => order.Key).ToHashSet();
        _rows = new ObservableCollection<OrderLinkRow>(orders.Concat(candidates)
            .DistinctBy(order => order.Key)
            .Select(order => new OrderLinkRow(order, candidateKeys.Contains(order.Key)))
            .OrderByDescending(row => row.IsCandidate)
            .ThenByDescending(row => row.Order.CreatedAt));
        MarketplaceFilters = [new(null, "Усі маркетплейси"), new(MarketplaceKind.Prom, "Prom.ua"), new(MarketplaceKind.Rozetka, "Rozetka")];
        ShopFilters = new[] { new OrderShopFilter(null, null, "Усі магазини") }.Concat(_rows
            .DistinctBy(row => (row.Order.Key.Marketplace, row.Order.Key.ConnectionId))
            .Select(row => new OrderShopFilter(row.Order.Key.Marketplace, row.Order.Key.ConnectionId,
                row.Shop + " · " + row.Marketplace))
            .OrderBy(filter => filter.Label)).ToArray();
        _marketplaceFilter = MarketplaceFilters[0];
        _shopFilter = ShopFilters[0];
        Orders = CollectionViewSource.GetDefaultView(_rows);
        Orders.Filter = MatchesFilter;
        LinkCommand = new RelayCommand(_ => Choose(false), _ => SelectedOrder is not null);
        RejectCommand = new RelayCommand(_ => Choose(true), _ => SelectedOrder?.IsCandidate == true);
        if (_rows.Count == 1) SelectedOrder = _rows[0];
    }

    public event Action<OrderLinkChoice>? ChoiceRequested;
    public ReceiptRowViewModel Receipt { get; }
    public IReadOnlyList<OrderItem> ReceiptItems { get; }
    public string ReceiptDetailsHint { get; }
    public ICollectionView Orders { get; }
    public IReadOnlyList<OrderMarketplaceFilter> MarketplaceFilters { get; }
    public IReadOnlyList<OrderShopFilter> ShopFilters { get; }
    public ICommand LinkCommand { get; }
    public ICommand RejectCommand { get; }
    public string ReceiptHeading => $"Чек № {Receipt.Serial} · {Receipt.Type}";
    public string ReceiptDate => Receipt.Model.DisplayDate?.LocalDateTime.ToString("dd.MM.yyyy HH:mm:ss") ?? "Дата невідома";
    public string ReceiptAmount => Receipt.Total.ToString("0.00", CultureInfo.CurrentCulture) + " грн";
    public string ReceiptFiscalNumber => "Фіскальний номер: " + Receipt.FiscalCode;
    public string ResultCount => $"Замовлень у списку: {Orders.Cast<object>().Count()} з {_rows.Count}";
    public string Search
    {
        get => _search;
        set { if (SetProperty(ref _search, value ?? "")) Refresh(); }
    }
    public OrderMarketplaceFilter MarketplaceFilter
    {
        get => _marketplaceFilter;
        set { if (value is not null && SetProperty(ref _marketplaceFilter, value)) Refresh(); }
    }
    public OrderShopFilter ShopFilter
    {
        get => _shopFilter;
        set { if (value is not null && SetProperty(ref _shopFilter, value)) Refresh(); }
    }
    public OrderLinkRow? SelectedOrder
    {
        get => _selected;
        set
        {
            if (!SetProperty(ref _selected, value)) return;
            foreach (var property in new[] { nameof(HasSelection), nameof(SelectedHeading), nameof(SelectedRelation),
                nameof(SelectedBuyer), nameof(SelectedRecipient), nameof(SelectedPayment), nameof(SelectedDelivery),
                nameof(SelectedTracking), nameof(SelectedDate), nameof(SelectedAmount), nameof(Comparison), nameof(OrderItems) })
                OnPropertyChanged(property);
            ((RelayCommand)LinkCommand).RaiseCanExecuteChanged();
            ((RelayCommand)RejectCommand).RaiseCanExecuteChanged();
        }
    }
    public bool HasSelection => SelectedOrder is not null;
    public string SelectedHeading => SelectedOrder is { } selected
        ? $"{selected.Marketplace} · {selected.Shop} · № {selected.Order.Number}" : "Виберіть замовлення зі списку";
    public string SelectedRelation => SelectedOrder is null ? ""
        : SelectedOrder.IsCandidate ? "Кандидат — зв’язок ще не підтверджено." : "Ручний вибір — зв’язок ще не підтверджено.";
    public string SelectedBuyer => Person(SelectedOrder?.Order.Buyer);
    public string SelectedRecipient => Person(SelectedOrder?.Order.Recipient);
    public string SelectedDate => SelectedOrder?.Date ?? "—";
    public string SelectedAmount => SelectedOrder?.Amount ?? "—";
    public string SelectedPayment => SelectedOrder is null ? "—"
        : Join(SelectedOrder.Order.PaymentMethod, SelectedOrder.Order.PaymentStatus);
    public string SelectedDelivery => SelectedOrder is null ? "—"
        : Join(SelectedOrder.Order.DeliveryMethod, string.Join("; ", SelectedOrder.Order.Shipments
            .Select(shipment => shipment.Destination).Where(destination => destination.Length > 0).Distinct()));
    public string SelectedTracking => Text(SelectedOrder?.Order.TrackingDisplay);
    public IReadOnlyList<OrderItem> OrderItems => SelectedOrder?.Order.Items ?? [];
    public string Comparison
    {
        get
        {
            if (SelectedOrder is null) return "Виберіть замовлення та звірте його з чеком.";
            var order = SelectedOrder.Order;
            if (order.Total is null) return "Суму замовлення не вдалося розпізнати. Перевірте товари та інші дані вручну.";
            if (string.IsNullOrWhiteSpace(order.Currency))
                return "API не вказало валюту замовлення. Збіг суми сам по собі не підтверджує зв’язок.";
            if (!string.Equals(order.Currency, "UAH", StringComparison.OrdinalIgnoreCase))
                return "Валюта замовлення відрізняється від гривні. Перевірте суми й товари вручну.";
            var difference = order.Total.Value - Receipt.Total;
            return difference == 0 ? "Суми збігаються. Додатково звірте товари, дату та покупця."
                : "Різниця сум (замовлення − чек): " + difference.ToString("+0.00;-0.00;0.00", CultureInfo.CurrentCulture) + " грн. Перевірте доставку, знижки або часткову оплату.";
        }
    }

    private void Choose(bool reject)
    {
        if (SelectedOrder is null || reject && !SelectedOrder.IsCandidate) return;
        ChoiceRequested?.Invoke(new OrderLinkChoice(SelectedOrder.Order.Key, reject));
    }
    private void Refresh()
    {
        Orders.Refresh();
        if (SelectedOrder is not null && !MatchesFilter(SelectedOrder)) SelectedOrder = null;
        OnPropertyChanged(nameof(ResultCount));
    }
    private bool MatchesFilter(object value)
    {
        if (value is not OrderLinkRow row) return false;
        var key = row.Order.Key;
        if (MarketplaceFilter.Kind is { } kind && key.Marketplace != kind) return false;
        if (ShopFilter.ConnectionId is not null && (key.ConnectionId != ShopFilter.ConnectionId || key.Marketplace != ShopFilter.Kind)) return false;
        var query = Search.Trim();
        if (query.Length == 0) return true;
        var searchable = string.Join(" ", row.Order.Number, row.Order.Key.OrderId, row.Order.Buyer?.Name,
            row.Order.Buyer?.Phone, row.Order.Recipient?.Name, row.Order.Recipient?.Phone, row.Order.TrackingDisplay);
        if (searchable.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        var digits = Digits(query);
        return digits.Length >= 4 && query.All(character => char.IsDigit(character) || " +()-".Contains(character))
            && new[] { row.Order.Buyer?.Phone, row.Order.Recipient?.Phone, row.Order.TrackingDisplay }
                .Any(text => Digits(text ?? "").Contains(digits, StringComparison.Ordinal));
    }
    private static string Digits(string value) => new(value.Where(char.IsDigit).ToArray());
    private static string Person(OrderPerson? person) => person is null ? "Не надано API" : Join(person.Name, person.Phone);
    private static string Join(params string?[] values) => Text(string.Join(" · ", values.Where(value => !string.IsNullOrWhiteSpace(value))));
    private static string Text(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}

public sealed record OrderMarketplaceFilter(MarketplaceKind? Kind, string Label);
public sealed record OrderShopFilter(MarketplaceKind? Kind, string? ConnectionId, string Label);
public sealed record OrderLinkRow(MarketplaceOrder Order, bool IsCandidate)
{
    public string Marketplace => Order.Key.Marketplace == MarketplaceKind.Prom ? "Prom.ua" : "Rozetka";
    public string Shop => string.IsNullOrWhiteSpace(Order.StoreName) ? "Магазин без назви" : Order.StoreName;
    public string Source => Marketplace + " · " + Shop;
    public string Number => Order.Number;
    public string Buyer => Order.Buyer?.Name ?? "—";
    public string Relation => IsCandidate ? "Кандидат" : "Ручний вибір";
    public string Date => Order.CreatedAt?.LocalDateTime.ToString("dd.MM.yyyy HH:mm") ?? "Дата невідома";
    public string Amount => Order.Total is { } total
        ? total.ToString("0.00", CultureInfo.CurrentCulture) + (Order.Currency.Length > 0 ? " " + Order.Currency : " (валюта невідома)")
        : string.IsNullOrWhiteSpace(Order.RawTotal) ? "Сума невідома" : Order.RawTotal + " (не розпізнано)";
}

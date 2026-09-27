using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

public sealed class MarketplaceOrderRowViewModel(MarketplaceOrder model) : ObservableObject
{
    public MarketplaceOrder Model { get; private set; } = model;
    public OrderKey Key => Model.Key;
    public string Marketplace => Key.Marketplace.ToString();
    public string StoreName => Model.StoreName;
    public string Number => Model.Number;
    public DateTime? CreatedAt => Model.CreatedAt is { } date ? TimeZoneInfo.ConvertTime(date, DateRangeBuilder.KyivZone).DateTime : null;
    public string TotalDisplay => Model.Total is { } total ? $"{total:N2} {(Model.Currency.Length > 0 ? Model.Currency : "(валюта не надана)")}" : "Сума не надана";
    public string BuyerDisplay => Model.Buyer?.Name ?? "";
    public string TrackingDisplay => Model.TrackingDisplay;
    public string Status => Model.Status;
    private bool _selectedForLabels;
    private string _labelStatus = "Не завантажено", _labelPrintStatus = "Не передано";
    public bool IsSelectedForLabels { get => _selectedForLabels; set => SetProperty(ref _selectedForLabels, value); }
    public string LabelStatus { get => _labelStatus; set => SetProperty(ref _labelStatus, value); }
    public string LabelPrintStatus { get => _labelPrintStatus; set => SetProperty(ref _labelPrintStatus, value); }
    public string DeliveryDisplay => !Model.Shipments.Any(s => !string.IsNullOrWhiteSpace(s.TrackingNumber)) ? "Накладну не створено" :
        string.Join(", ", Model.Shipments.Select(s => ShippingCarrierNames.Display(ShippingCarrierNames.FromDocumentedDeliveryField(s.Carrier))).Distinct());
    public string LinkStatus { get; private set; } = "Без прив’язаного чека";
    public string LinkedReceiptsDisplay { get; private set; } = "";
    public IReadOnlyList<ReceiptRowViewModel> LinkedReceipts { get; private set; } = [];
    public string LinkExplanation { get; private set; } = "";
    public bool HasConfirmedLink { get; private set; }
    public bool HasSuggestedLink { get; private set; }

    public void Update(MarketplaceOrder order, IReadOnlyList<ReceiptRowViewModel> receipts, IReadOnlyList<ReceiptOrderDecision> decisions)
    {
        if (!ShippingCarrierNames.ForOrder(Model).SequenceEqual(ShippingCarrierNames.ForOrder(order)))
        { LabelStatus = "Не завантажено"; LabelPrintStatus = "Не передано"; }
        Model = order;
        var linked = receipts.Where(r => r.OrderMatch?.Order?.Key == Key).ToArray();
        LinkedReceipts = linked;
        var durable = decisions.GroupBy(d => d.ReceiptId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.MaxBy(d => d.UpdatedAtUtc)!).Where(d => d.ConfirmedOrder == Key).Select(d => d.ReceiptId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        HasConfirmedLink = durable.Count > 0 || linked.Any(r => r.OrderMatch!.State is ReceiptLinkState.Exact or ReceiptLinkState.Manual);
        HasSuggestedLink = linked.Any(r => r.OrderMatch!.State == ReceiptLinkState.Suggested);
        var outsideRange = durable.Count(id => !linked.Any(r => r.Id.Equals(id, StringComparison.OrdinalIgnoreCase)));
        var candidates = receipts.Where(r => r.OrderMatch?.Order is null && r.OrderMatch?.Candidates.Any(o => o.Key == Key) == true).ToArray();
        LinkStatus = durable.Count > 0 ? "Підтверджено вручну" : linked.Length > 0
            ? string.Join("; ", linked.Select(r => r.LinkStatus).Distinct())
            : candidates.Length > 0 ? string.Join("; ", candidates.Select(r => r.LinkStatus).Distinct())
            : Model.Total is null ? "Недостатньо даних" : "Без прив’язаного чека";
        LinkExplanation = string.Join("\n", linked.Concat(candidates).Distinct().Select(r => $"Чек №{r.Serial}: {r.LinkExplanation}"));
        LinkedReceiptsDisplay = string.Join(", ", linked.Select(r => $"№{r.Serial} ({r.Type})")) +
            (outsideRange > 0 ? $"; збережено поза списком: {outsideRange}" : "");
        foreach (var property in new[] { nameof(Model), nameof(Marketplace), nameof(StoreName), nameof(Number), nameof(CreatedAt),
            nameof(TotalDisplay), nameof(BuyerDisplay), nameof(TrackingDisplay), nameof(DeliveryDisplay), nameof(Status), nameof(LinkStatus),
            nameof(LinkedReceiptsDisplay), nameof(LinkedReceipts), nameof(LinkExplanation), nameof(HasConfirmedLink), nameof(HasSuggestedLink) }) OnPropertyChanged(property);
    }
}

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
    private int _displayIndex;
    public int DisplayIndex { get => _displayIndex; internal set => SetProperty(ref _displayIndex, value); }
    public DateTime? CreatedAt => Model.CreatedAt is { } date ? TimeZoneInfo.ConvertTime(date, DateRangeBuilder.KyivZone).DateTime : null;
    public string TotalDisplay => Model.Total is { } total ? $"{total:N2} {(Model.Currency.Length > 0 ? Model.Currency : "(валюта не надана)")}" : "Сума не надана";
    public string BuyerDisplay => Model.Buyer?.Name ?? "";
    public string TrackingDisplay => Model.TrackingDisplay;
    public IReadOnlyList<OrderShipmentLink> ShipmentLinks => Model.Shipments
        .Where(s => !string.IsNullOrWhiteSpace(s.TrackingNumber))
        .DistinctBy(s => (s.Carrier, TrackingNumber: s.TrackingNumber.Trim(), s.ShipmentId))
        .Select(s => new OrderShipmentLink(this, s)).ToArray();
    public string Status => Model.Status;
    private bool _selectedForLabels;
    private bool _hasPrintedLabel;
    private bool _hasLabelSubmission;
    private string _labelStatus = "Не завантажено", _labelPrintStatus = "Не передано";
    public bool IsSelectedForLabels { get => _selectedForLabels; set => SetProperty(ref _selectedForLabels, value); }
    public string LabelStatus { get => _labelStatus; set => SetProperty(ref _labelStatus, value); }
    public string LabelPrintStatus { get => _labelPrintStatus; set => SetProperty(ref _labelPrintStatus, value); }
    public bool HasPrintedLabel { get => _hasPrintedLabel; private set => SetProperty(ref _hasPrintedLabel, value); }
    public bool HasLabelSubmission { get => _hasLabelSubmission; private set => SetProperty(ref _hasLabelSubmission, value); }
    public string DeliveryDisplay => !Model.Shipments.Any(s => !string.IsNullOrWhiteSpace(s.TrackingNumber)) ? "Накладну не створено" :
        string.Join(", ", Model.Shipments.Select(s => ShippingCarrierNames.Display(ShippingCarrierNames.FromDocumentedDeliveryField(s.Carrier))).Distinct());
    public string LinkStatus { get; private set; } = "Без прив’язаного чека";
    public string LinkedReceiptsDisplay { get; private set; } = "";
    public IReadOnlyList<ReceiptRowViewModel> LinkedReceipts { get; private set; } = [];
    public string LinkExplanation { get; private set; } = "";
    public bool HasConfirmedLink { get; private set; }
    public bool HasSuggestedLink { get; private set; }

    public void ApplyLabelHistory(IEnumerable<LabelPrintAttempt> history, Func<OrderKey, OrderKey>? normalizeOrder = null)
    {
        var shipments = ShippingCarrierNames.ForOrder(Model);
        var attempts = history.Where(a => a.Pages.Any(p => shipments.Any(s => p.Matches(s, normalizeOrder)))).ToArray();
        HasLabelSubmission = attempts.Any(a => a.State is LabelSubmissionState.Submitted or LabelSubmissionState.SubmissionUnknown);
        HasPrintedLabel = shipments.Count > 0 && shipments.All(s => attempts.Any(a => a.State == LabelSubmissionState.Submitted &&
            a.Pages.Any(p => p.Matches(s, normalizeOrder))));
        var latest = attempts.MaxBy(a => a.TimeUtc);
        LabelPrintStatus = latest?.State switch
        {
            LabelSubmissionState.Submitted => HasPrintedLabel ? $"Передано Windows, job {latest.WindowsJobId}" :
                $"Частину наклейок передано Windows, job {latest.WindowsJobId}",
            LabelSubmissionState.SubmissionUnknown => "Передавання невідоме; можливий дубль",
            _ => "Не передано"
        };
    }

    public void Update(MarketplaceOrder order, IReadOnlyList<ReceiptRowViewModel> receipts, IReadOnlyList<ReceiptOrderDecision> decisions)
    {
        if (!ShippingCarrierNames.ForOrder(Model).SequenceEqual(ShippingCarrierNames.ForOrder(order)))
        { LabelStatus = "Не завантажено"; LabelPrintStatus = "Не передано"; HasPrintedLabel = false; HasLabelSubmission = false; }
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
            nameof(TotalDisplay), nameof(BuyerDisplay), nameof(TrackingDisplay), nameof(ShipmentLinks), nameof(DeliveryDisplay), nameof(Status), nameof(LinkStatus),
            nameof(LinkedReceiptsDisplay), nameof(LinkedReceipts), nameof(LinkExplanation), nameof(HasConfirmedLink), nameof(HasSuggestedLink) }) OnPropertyChanged(property);
    }
}

public sealed record OrderShipmentLink(MarketplaceOrderRowViewModel Row, OrderShipment Shipment)
{
    public string TrackingNumber => Shipment.TrackingNumber.Trim();
}

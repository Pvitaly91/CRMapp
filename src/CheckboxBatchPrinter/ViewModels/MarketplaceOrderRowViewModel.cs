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
    private readonly Dictionary<(ShippingCarrier Carrier, string TrackingNumber, string ShipmentId), OrderShipmentTrackingStatus> _trackingStatuses = [];
    private readonly Dictionary<(ShippingCarrier Carrier, string TrackingNumber, string ShipmentId), OrderShipmentCabinetPrintStatus> _cabinetPrintStatuses = [];
    private readonly HashSet<(ShippingCarrier Carrier, string TrackingNumber, string ShipmentId)> _locallyPrintedShipments = [];
    public IReadOnlyList<OrderShipmentLink> ShipmentLinks => Model.Shipments
        .Where(s => !string.IsNullOrWhiteSpace(s.TrackingNumber))
        .DistinctBy(s => (s.Carrier, TrackingNumber: s.TrackingNumber.Trim(), s.ShipmentId))
        .Select(s => new OrderShipmentLink(this, s)).ToArray();
    public string Status => Model.Status;
    private bool _selectedForLabels;
    private bool _hasPrintedLabel;
    private bool _hasLabelSubmission;
    private bool _localHasLabelSubmission;
    private string _localLabelPrintStatus = "Не передано";
    private string _labelStatus = "Не завантажено", _labelPrintStatus = "Не передано";
    public bool IsSelectedForLabels { get => _selectedForLabels; set => SetProperty(ref _selectedForLabels, value); }
    public string LabelStatus { get => _labelStatus; set => SetProperty(ref _labelStatus, value); }
    public string LabelPrintStatus { get => _labelPrintStatus; set { _localLabelPrintStatus = value; RefreshLabelPrintState(); } }
    public string LabelPrintStatusToolTip
    {
        get
        {
            var details = new List<string> { $"Локальна історія: {_localLabelPrintStatus}" };
            foreach (var shipment in Model.Shipments.Where(s => !string.IsNullOrWhiteSpace(s.TrackingNumber)))
            {
                if (_cabinetPrintStatuses.TryGetValue(TrackingIdentity(shipment), out var status))
                {
                    details.Add($"ТТН {shipment.TrackingNumber.Trim()} · {status.Source}: " +
                        (status.Printed == true ? "позначено надрукованою" : "не позначено надрукованою"));
                    if (!string.IsNullOrWhiteSpace(status.PrintedAtText))
                        details.Add($"Дата друку з кабінету: {status.PrintedAtText} (часовий пояс не надано)");
                    if (status.CheckedAtUtc is { } checkedAt)
                        details.Add($"Перевірено: {TimeZoneInfo.ConvertTime(checkedAt, DateRangeBuilder.KyivZone):dd.MM.yyyy HH:mm}");
                    details.Add("Ознака кабінету не підтверджує фізичний вихід наклейки з принтера.");
                }
                else
                {
                    var carrier = ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier);
                    details.Add($"ТТН {shipment.TrackingNumber.Trim()} · {ShippingCarrierNames.Display(carrier)}: ознаку друку з кабінету не надано.");
                }
            }
            return string.Join("\n", details);
        }
    }
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

    public void UpdateTrackingStatuses(IEnumerable<OrderShipmentTrackingStatus> statuses)
    {
        var shipments = Model.Shipments.Select(TrackingIdentity).ToHashSet();
        var replacement = new Dictionary<(ShippingCarrier Carrier, string TrackingNumber, string ShipmentId), OrderShipmentTrackingStatus>();
        foreach (var status in statuses)
        {
            var identity = TrackingIdentity(status.Shipment);
            if (shipments.Contains(identity) && identity.TrackingNumber.Length > 0) replacement[identity] = status;
        }
        if (replacement.Count == _trackingStatuses.Count && replacement.All(pair =>
            _trackingStatuses.TryGetValue(pair.Key, out var current) && current == pair.Value)) return;
        _trackingStatuses.Clear();
        foreach (var pair in replacement) _trackingStatuses.Add(pair.Key, pair.Value);
        OnPropertyChanged(nameof(ShipmentLinks));
    }

    internal OrderShipmentTrackingStatus? TrackingStatusFor(OrderShipment shipment) =>
        _trackingStatuses.GetValueOrDefault(TrackingIdentity(shipment));

    private static (ShippingCarrier Carrier, string TrackingNumber, string ShipmentId) TrackingIdentity(OrderShipment shipment) =>
        (ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier), shipment.TrackingNumber.Trim(), shipment.ShipmentId);

    public void UpdateCabinetPrintStatuses(IEnumerable<OrderShipmentCabinetPrintStatus> statuses)
    {
        var shipments = Model.Shipments.Select(TrackingIdentity).ToHashSet();
        var changed = false;
        foreach (var status in statuses)
        {
            var identity = TrackingIdentity(status.Shipment);
            if (status.Printed is null || identity.TrackingNumber.Length == 0 || !shipments.Contains(identity)) continue;
            if (_cabinetPrintStatuses.TryGetValue(identity, out var previous) && previous == status) continue;
            _cabinetPrintStatuses[identity] = status;
            changed = true;
        }
        if (changed) RefreshLabelPrintState();
    }

    public void ClearCabinetPrintStatuses()
    {
        if (_cabinetPrintStatuses.Count == 0) return;
        _cabinetPrintStatuses.Clear();
        RefreshLabelPrintState();
    }

    public IReadOnlyList<CoreCabinetLabelPrintEvidence> GetCabinetPrintEvidence() => ShippingCarrierNames.ForOrder(Model)
        .Where(shipment => _cabinetPrintStatuses.TryGetValue((shipment.Carrier, shipment.TrackingNumber, shipment.ShipmentId), out var status) && status.Printed == true)
        .Select(shipment =>
        {
            var status = _cabinetPrintStatuses[(shipment.Carrier, shipment.TrackingNumber, shipment.ShipmentId)];
            return new CoreCabinetLabelPrintEvidence(shipment, status.Source, status.PrintedAtText, status.CheckedAtUtc);
        }).ToArray();

    private void RefreshLabelPrintState()
    {
        var shipments = ShippingCarrierNames.ForOrder(Model);
        var external = GetCabinetPrintEvidence();
        var externalIdentities = external.Select(evidence =>
            (evidence.Shipment.Carrier, evidence.Shipment.TrackingNumber, evidence.Shipment.ShipmentId)).ToHashSet();
        HasPrintedLabel = shipments.Count > 0 && shipments.All(shipment =>
            _locallyPrintedShipments.Contains((shipment.Carrier, shipment.TrackingNumber, shipment.ShipmentId)) ||
            externalIdentities.Contains((shipment.Carrier, shipment.TrackingNumber, shipment.ShipmentId)));
        HasLabelSubmission = _localHasLabelSubmission || external.Count > 0;
        var display = !_localHasLabelSubmission && external.Count > 0
            ? HasPrintedLabel ? "НП: позначено надрукованою" : "НП: частину позначено надрукованою"
            : _localLabelPrintStatus;
        SetProperty(ref _labelPrintStatus, display, nameof(LabelPrintStatus));
        OnPropertyChanged(nameof(LabelPrintStatusToolTip));
    }

    public void ApplyLabelHistory(IEnumerable<LabelPrintAttempt> history, Func<OrderKey, OrderKey>? normalizeOrder = null)
    {
        var shipments = ShippingCarrierNames.ForOrder(Model);
        var attempts = history.Where(a => a.Pages.Any(p => shipments.Any(s => p.Matches(s, normalizeOrder)))).ToArray();
        _localHasLabelSubmission = attempts.Any(a => a.State is LabelSubmissionState.Submitted or LabelSubmissionState.SubmissionUnknown);
        _locallyPrintedShipments.Clear();
        foreach (var shipment in shipments.Where(s => attempts.Any(a => a.State == LabelSubmissionState.Submitted &&
            a.Pages.Any(p => p.Matches(s, normalizeOrder)))))
            _locallyPrintedShipments.Add((shipment.Carrier, shipment.TrackingNumber, shipment.ShipmentId));
        var localComplete = shipments.Count > 0 && shipments.All(s =>
            _locallyPrintedShipments.Contains((s.Carrier, s.TrackingNumber, s.ShipmentId)));
        var latest = attempts.MaxBy(a => a.TimeUtc);
        LabelPrintStatus = latest?.State switch
        {
            LabelSubmissionState.Submitted => localComplete ? $"Передано Windows, job {latest.WindowsJobId}" :
                $"Частину наклейок передано Windows, job {latest.WindowsJobId}",
            LabelSubmissionState.SubmissionUnknown => "Передавання невідоме; можливий дубль",
            _ => "Не передано"
        };
    }

    public void Update(MarketplaceOrder order, IReadOnlyList<ReceiptRowViewModel> receipts, IReadOnlyList<ReceiptOrderDecision> decisions)
    {
        if (Model.Key != order.Key) { _trackingStatuses.Clear(); _cabinetPrintStatuses.Clear(); }
        if (!ShippingCarrierNames.ForOrder(Model).SequenceEqual(ShippingCarrierNames.ForOrder(order)))
        {
            LabelStatus = "Не завантажено"; _localLabelPrintStatus = "Не передано";
            _locallyPrintedShipments.Clear(); _localHasLabelSubmission = false;
        }
        Model = order;
        var shipmentIdentities = Model.Shipments.Select(TrackingIdentity).ToHashSet();
        foreach (var identity in _trackingStatuses.Keys.Where(identity => !shipmentIdentities.Contains(identity)).ToArray())
            _trackingStatuses.Remove(identity);
        foreach (var identity in _cabinetPrintStatuses.Keys.Where(identity => !shipmentIdentities.Contains(identity)).ToArray())
            _cabinetPrintStatuses.Remove(identity);
        RefreshLabelPrintState();
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

// A carrier status projection, deliberately separate from the cached marketplace order.
public sealed record OrderShipmentTrackingStatus(OrderShipment Shipment, string Status, string Source = "",
    DateTimeOffset? CheckedAtUtc = null, bool IsStale = false);
public sealed record OrderShipmentCabinetPrintStatus(OrderShipment Shipment, bool? Printed, string Source = "Кабінет НП",
    string PrintedAtText = "", DateTimeOffset? CheckedAtUtc = null);

public sealed record OrderShipmentLink(MarketplaceOrderRowViewModel Row, OrderShipment Shipment)
{
    public string TrackingNumber => Shipment.TrackingNumber.Trim();
    public string TrackingStatus => Row.TrackingStatusFor(Shipment)?.Status is { } status && !string.IsNullOrWhiteSpace(status)
        ? status : "Статус не завантажено";
    public string TrackingStatusToolTip
    {
        get
        {
            var status = Row.TrackingStatusFor(Shipment);
            if (status is null) return "Статус доставки цієї ТТН ще не завантажено. Натисніть «Оновити все».";
            var source = string.IsNullOrWhiteSpace(status.Source) ? "" : $"\nДжерело: {status.Source}";
            var time = status.CheckedAtUtc is { } checkedAt ?
                $"\nОновлено: {TimeZoneInfo.ConvertTime(checkedAt, DateRangeBuilder.KyivZone):dd.MM.yyyy HH:mm}" : "";
            var stale = status.IsStale ? "\nПоказано попередній статус. Актуальні дані не отримано." : "";
            return TrackingStatus + source + time + stale;
        }
    }
}

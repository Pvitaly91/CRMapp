using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceOrderRowViewModel
{
    private string _problemReason = "", _problemTracking = "", _problemChecked = "";
    private int _problemPriority = int.MaxValue;
    public string ProblemReason { get => _problemReason; private set => SetProperty(ref _problemReason, value); }
    public string ProblemTracking { get => _problemTracking; private set => SetProperty(ref _problemTracking, value); }
    public string ProblemChecked { get => _problemChecked; private set => SetProperty(ref _problemChecked, value); }
    public int ProblemPriority { get => _problemPriority; private set => SetProperty(ref _problemPriority, value); }
    public bool ProblemTrackingPending => !Model.IsCanceled && !ProblemOrderRules.IsCompletedOrder(Model) &&
        ShipmentLinks.Any(link => TrackingStatusFor(link.Shipment) is not { } status || status.IsStale ||
            status.Status == "Статус недоступний" || status.CheckedAtUtc is null || DateTimeOffset.UtcNow - status.CheckedAtUtc >= TimeSpan.FromMinutes(30));

    internal void UpdateProblems(DateTimeOffset now, bool savedReceiptEvidence)
    {
        var tracking = ShipmentLinks.Select(link => TrackingStatusFor(link.Shipment)).OfType<OrderShipmentTrackingStatus>().ToArray();
        var statuses = tracking.Select(s => s.Details ?? new ShipmentTrackingStatus(
            ShippingCarrierNames.FromDocumentedDeliveryField(s.Shipment.Carrier), s.Shipment.TrackingNumber.Trim(),
            s.Status, s.Source, s.CheckedAtUtc ?? DateTimeOffset.MinValue)).ToArray();
        var hasReceipt = !HasNoReceiptLink || savedReceiptEvidence || Model.ReceiptIds.Any(s => !string.IsNullOrWhiteSpace(s)) ||
            Model.FiscalReferences.Count > 0 || Model.FiscalReceiptNumbers.Any(s => !string.IsNullOrWhiteSpace(s)) ||
            Model.FiscalReceiptUrls.Any(s => !string.IsNullOrWhiteSpace(s));
        var problems = ProblemOrderRules.Evaluate(Model, hasReceipt, statuses, now);
        ProblemReason = string.Join("\n", problems.Select(p => p.Reason));
        ProblemPriority = problems.Count == 0 ? int.MaxValue : problems.Min(p => p.Priority);
        ProblemTracking = string.Join("\n", ShipmentLinks.Select(s => $"{s.TrackingNumber}: {s.TrackingStatus}"));
        ProblemChecked = Model.Shipments.Any(s => !string.IsNullOrWhiteSpace(s.TrackingNumber))
            ? string.Join("\n", ShipmentLinks.Select(s => s.TrackingStatusToolTip)) : "ТТН ще немає";
        ProblemChecked += $"\nЗамовлення перевірено: {TimeZoneInfo.ConvertTime(Model.RetrievedAtUtc, DateRangeBuilder.KyivZone):dd.MM.yyyy HH:mm}";
    }
}

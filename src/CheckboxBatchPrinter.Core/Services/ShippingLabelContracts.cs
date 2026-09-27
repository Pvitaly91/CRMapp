using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IShippingLabelSource
{ Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment, CancellationToken cancellationToken = default); }
public interface IShippingLabelRenderer
{ Task<IReadOnlyList<ShippingLabelPage>> RenderAsync(ShippingLabelDocument document, double dpiX, double dpiY, CancellationToken cancellationToken = default); }
public interface ILabelHistoryStore
{
    Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(LabelPrintAttempt attempt, CancellationToken cancellationToken = default);
}
public interface IShippingSettingsStore
{
    Task<ShippingLabelSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ShippingLabelSettings settings, CancellationToken cancellationToken = default);
}
public interface ILabelBatchBackend
{
    // beforeTransfer must be persisted immediately before the first Windows transmission operation.
    Task<int?> SubmitAsync(LabelPrintBatch batch, string jobName, Func<Task> beforeTransfer,
        Func<int, Task> jobCreated, CancellationToken cancellationToken = default);
}

public sealed class LabelPrintCoordinator(ILabelHistoryStore history, ILabelBatchBackend backend)
{
    public async Task<LabelPrintAttempt> SubmitAsync(LabelPrintBatch batch, CancellationToken cancellationToken = default)
    {
        batch.Settings.Validate();
        if (batch.Pages.Count == 0) throw new InvalidOperationException("Пакет наклейок порожній.");
        var pages = batch.Pages.Select(p => new LabelAttemptPage(p.Shipment.Carrier, p.ConnectionId,
            p.Shipment.TrackingNumber, p.DocumentId, p.PageNumber, p.Fingerprint)).ToArray();
        var attempt = new LabelPrintAttempt(Guid.NewGuid().ToString("N"), "", batch.Settings.PrinterName,
            DateTimeOffset.UtcNow, LabelSubmissionState.NotSubmitted, pages);
        attempt = attempt with { JobName = "CRMapp labels " + attempt.AttemptId };
        await history.SaveAsync(attempt, cancellationToken);
        try
        {
            await backend.SubmitAsync(batch, attempt.JobName, async () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var unknown = attempt with { State = LabelSubmissionState.SubmissionUnknown };
                // Persist before changing in-memory state: failed journal write cannot allow transmission.
                await history.SaveAsync(unknown, cancellationToken);
                attempt = unknown;
            }, async jobId =>
            {
                attempt = attempt with { State = LabelSubmissionState.Submitted, WindowsJobId = jobId };
                await history.SaveAsync(attempt, CancellationToken.None);
            }, cancellationToken);
            return attempt;
        }
        catch (Exception ex)
        {
            // A backend exception after the boundary never becomes a safe retry.
            throw new LabelTransmissionException(attempt, ex);
        }
    }
}
public sealed class LabelTransmissionException(LabelPrintAttempt attempt, Exception inner)
    : Exception(attempt.State == LabelSubmissionState.NotSubmitted ? "Пакет не передано Windows."
        : "Передавання пакета почалося. Частина наклейок могла надрукуватися; повтор може створити дубль.", inner)
{ public LabelPrintAttempt Attempt { get; } = attempt; }

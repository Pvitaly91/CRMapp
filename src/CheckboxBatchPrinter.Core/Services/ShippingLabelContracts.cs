using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IShippingLabelSource
{ Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment, CancellationToken cancellationToken = default); }
public interface IShippingLabelBatchSource : IShippingLabelSource
{ IShippingLabelSource CreateBatchSource(IReadOnlyList<ShipmentReference>? shipments = null); }
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
    // Validate the complete packet before its first job; this must never transmit to Windows.
    void ValidateBatch(LabelPrintBatch batch) { }
    // beforeTransfer must be persisted immediately before the first Windows transmission operation.
    Task<int?> SubmitAsync(LabelPrintBatch batch, string jobName, Func<Task> beforeTransfer,
        Func<int, Task> jobCreated, CancellationToken cancellationToken = default);
}

public sealed class LabelPrintCoordinator(ILabelHistoryStore history, ILabelBatchBackend backend)
{
    public async Task<IReadOnlyList<LabelPrintAttempt>> SubmitAsync(LabelPrintBatch batch, CancellationToken cancellationToken = default)
    {
        batch.Settings.Validate();
        if (batch.Pages.Count == 0) throw new InvalidOperationException("Пакет наклейок порожній.");
        batch = batch with { Pages = Array.AsReadOnly(batch.Pages.ToArray()), Settings = batch.Settings with { } };
        var pages = batch.Pages;
        var completed = new List<LabelPrintAttempt>();
        var totalJobs = checked(pages.Count * batch.Settings.Copies);
        foreach (var page in pages)
            for (var copy = 0; copy < batch.Settings.Copies; copy++)
            {
                var single = batch with { Pages = [page], Settings = batch.Settings with { Copies = 1 } };
                var expectedPages = Math.Max(page.Shipment.ExpectedPlaces, pages.Where(p =>
                        p.ConnectionId == page.ConnectionId && p.DocumentId == page.DocumentId && p.Fingerprint == page.Fingerprint)
                    .Max(p => p.PageNumber));
                var attempt = CreateAttempt(single, expectedPages);
                try
                {
                    await history.SaveAsync(attempt, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (completed.Count == 0) backend.ValidateBatch(batch);
                    var jobId = await backend.SubmitAsync(single, attempt.JobName, async () =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var unknown = attempt with { State = LabelSubmissionState.SubmissionUnknown };
                        // Persist before changing in-memory state: failed journal write cannot allow transmission.
                        await history.SaveAsync(unknown, cancellationToken);
                        attempt = unknown;
                    }, async jobId =>
                    {
                        if (attempt.State != LabelSubmissionState.SubmissionUnknown || jobId <= 0)
                            throw new InvalidOperationException("Windows не підтвердив коректне завдання наклейки.");
                        attempt = attempt with { WindowsJobId = jobId };
                        await history.SaveAsync(attempt, CancellationToken.None);
                    }, cancellationToken);
                    if (jobId is not > 0 || attempt.WindowsJobId != jobId)
                        throw new InvalidOperationException("Windows не підтвердив завершене завдання наклейки.");
                    attempt = attempt with { State = LabelSubmissionState.Submitted };
                    await history.SaveAsync(attempt, CancellationToken.None);
                    completed.Add(attempt);
                }
                catch (Exception ex)
                {
                    // Retain accepted jobs and the current job's risk. Never replay the prefix or send the tail.
                    throw new LabelTransmissionException(attempt, ex, completed, totalJobs);
                }
            }
        return completed.AsReadOnly();
    }

    private static LabelPrintAttempt CreateAttempt(LabelPrintBatch batch, int expectedPages)
    {
        // A single printed document can cover its verified primary and partner TTNs.
        // Record both identities without sending additional physical pages to Windows.
        var pages = batch.Pages.SelectMany(p => p.CoveredShipments.Append(p.Shipment).Distinct()
            .Select(s => new LabelAttemptPage(s.Carrier, p.ConnectionId, s.TrackingNumber,
                p.DocumentId, p.PageNumber, p.Fingerprint)
            { Order = p.Source is "RozetkaSeller.Delivery" or PromCabinetDeliveryLabelSource.SourceName ? s.Order : null,
                ExpectedPages = expectedPages })).Distinct().ToArray();
        var attempt = new LabelPrintAttempt(Guid.NewGuid().ToString("N"), "", batch.Settings.PrinterName,
            DateTimeOffset.UtcNow, LabelSubmissionState.NotSubmitted, pages);
        return attempt with { JobName = "CRMapp label " + attempt.AttemptId };
    }
}
public sealed class LabelTransmissionException(LabelPrintAttempt attempt, Exception inner,
    IReadOnlyList<LabelPrintAttempt>? completed = null, int totalJobs = 1)
    : Exception($"Завершено передавання наклейок Windows: {(completed?.Count ?? 0) + (attempt.State == LabelSubmissionState.Submitted ? 1 : 0)} із {totalJobs}. " +
        (attempt.State == LabelSubmissionState.NotSubmitted ? "Наступну наклейку не передано."
            : attempt.State == LabelSubmissionState.Submitted ? "Поточну наклейку передано Windows; не вдалося записати остаточний стан історії."
            : "Передавання поточної наклейки почалося; перевірте її перед повтором, щоб уникнути дубля.") +
        " Решту пакета зупинено; автоматичний повтор не виконувався.", inner)
{
    public LabelPrintAttempt Attempt { get; } = attempt;
    public IReadOnlyList<LabelPrintAttempt> Attempts { get; } = Array.AsReadOnly((completed ?? []).Append(attempt).ToArray());
}

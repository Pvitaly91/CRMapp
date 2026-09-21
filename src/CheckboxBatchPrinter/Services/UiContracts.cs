using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Services;

public interface IPrintService : IDisposable
{
    IReadOnlyList<string> GetInstalledPrinters();
    bool PrinterExists(string printerName);
    Task<PrintSubmissionResult> PrintReceiptAsync(
        byte[] png,
        AppSettings settings,
        PrintAttemptDescriptor attempt,
        Action<PrintAttemptDescriptor> markSubmissionStarted,
        CancellationToken cancellationToken = default);
    Task<PrintSubmissionResult> PrintReceiptsAsSingleJobAsync(
        IReadOnlyList<(byte[] Png, string ReceiptId)> receipts,
        AppSettings settings,
        PrintAttemptDescriptor attempt,
        Action<PrintAttemptDescriptor> markSubmissionStarted,
        CancellationToken cancellationToken = default);
    Task<PrintSubmissionResult> PrintTestAsync(AppSettings settings, CancellationToken cancellationToken = default);
    Task<WindowsPrintJobObservation> GetJobStatusAsync(PrintSubmissionResult submission, CancellationToken cancellationToken = default);
}

public sealed record PrintBatchConfirmation(
    int VisibleCount,
    int HiddenExcludedCount,
    string PrinterName,
    IReadOnlyList<string> OrderedReceiptLabels,
    bool ContainsPreviouslySubmittedReceipts);

public interface IUiDialogService
{
    bool ConfirmPrint(PrintBatchConfirmation confirmation);
    void ShowInfo(string message, string title = "Checkbox Batch Printer");
    void ShowError(string message, string title = "Помилка");
    Task<bool> OpenSettingsAsync();
    void ShowPreview(byte[] png, ReceiptRowViewModel receipt);
}

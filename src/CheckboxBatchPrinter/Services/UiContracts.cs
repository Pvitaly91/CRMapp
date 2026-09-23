using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Services;

public interface IPrintService
{
    IReadOnlyList<string> GetInstalledPrinters();
    bool PrinterExists(string printerName);
    Task PrintReceiptAsync(byte[] png, string receiptId, AppSettings settings, CancellationToken cancellationToken = default);
    Task PrintReceiptsAsSingleJobAsync(IReadOnlyList<(byte[] Png, string ReceiptId)> receipts, AppSettings settings,
        CancellationToken cancellationToken = default);
    Task PrintTestAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IUiDialogService
{
    bool ConfirmPrint(PrintBatchConfirmation batch);
    void ShowInfo(string message, string title = "Checkbox Batch Printer");
    void ShowError(string message, string title = "Помилка");
    Task<bool> OpenSettingsAsync(bool marketplace = false);
    void ShowPreview(byte[] png, ReceiptRowViewModel receipt);
}

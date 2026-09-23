namespace CheckboxBatchPrinter.Services;

public sealed record PrintBatchItem(int Position, string ReceiptId, string ReceiptNumber, string Marketplace, string OrderNumber);

/// <summary>Detached, immutable confirmation data, never bound to live receipt/order rows.</summary>
public sealed class PrintBatchConfirmation(string printerName, int hiddenSelectedCount, IEnumerable<PrintBatchItem> items)
{
    public string PrinterName { get; } = printerName;
    public int HiddenSelectedCount { get; } = hiddenSelectedCount;
    public IReadOnlyList<PrintBatchItem> Items { get; } = Array.AsReadOnly(items.ToArray());
    public int Count => Items.Count;
}

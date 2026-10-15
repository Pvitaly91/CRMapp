using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Services;

public sealed record OrderLinkChoice(OrderKey Key, bool Reject);
public interface IOrderLinkDialogService
{
    OrderLinkChoice? ChooseOrder(ReceiptRowViewModel receipt, ReceiptDetails? details,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<MarketplaceOrder> candidates);
    string? ChooseReceipt(MarketplaceOrder order, IReadOnlyList<ReceiptRowViewModel> receipts,
        Func<ReceiptRowViewModel, Task>? preview) => null;
    bool ConfirmUnlink();
    void CopyText(string text);
}

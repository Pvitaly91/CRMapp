using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Services;

public sealed record OrderLinkChoice(OrderKey Key, bool Reject);
public interface IOrderLinkDialogService
{
    OrderLinkChoice? ChooseOrder(ReceiptRowViewModel receipt, ReceiptDetails? details,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<MarketplaceOrder> candidates);
    bool ConfirmUnlink();
    void CopyText(string text);
}

using System.Runtime.InteropServices;
using System.Windows;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Services;

public sealed class OrderLinkDialogService : IOrderLinkDialogService
{
    public OrderLinkChoice? ChooseOrder(ReceiptRowViewModel receipt, ReceiptDetails? details,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<MarketplaceOrder> candidates)
    {
        var window = new OrderLinkWindow(new OrderLinkViewModel(receipt, details, orders, candidates));
        if (Application.Current?.MainWindow is { } owner) window.Owner = owner;
        return window.ShowDialog() == true ? window.Choice : null;
    }

    public bool ConfirmUnlink() => ShowMessage(
        "Відв’язати замовлення від цього чека? Локальний зв’язок буде прибрано. Повторне автоматичне зв’язування вимкнеться, доки ви не виберете замовлення вручну.",
        "Відв’язати замовлення", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;

    public void CopyText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try { Clipboard.SetText(text); }
        catch (Exception exception) when (exception is ExternalException or InvalidOperationException or ThreadStateException)
        {
            ShowMessage("Не вдалося скопіювати дані. Буфер обміну зайнятий; повторіть спробу.",
                "Буфер обміну", MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
        }
    }

    private static MessageBoxResult ShowMessage(string text, string caption, MessageBoxButton buttons,
        MessageBoxImage image, MessageBoxResult defaultResult) => Application.Current?.MainWindow is { } owner
        ? MessageBox.Show(owner, text, caption, buttons, image, defaultResult)
        : MessageBox.Show(text, caption, buttons, image, defaultResult);
}

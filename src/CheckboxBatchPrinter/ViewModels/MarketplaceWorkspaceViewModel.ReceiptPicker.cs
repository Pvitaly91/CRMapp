using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceWorkspaceViewModel
{
    private bool CanChooseReceiptForOrder(MarketplaceOrderRowViewModel? order) =>
        order is not null && _account.Length > 0 && _canApply && HasEnabledConnections &&
        !IsBusy && !IsWorkspaceRefreshing && CanChooseReceipt?.Invoke() != false &&
        _orderRows.Contains(order) && (Orders.Contains(order) || ProblemOrders.Contains(order)) && order.HasNoReceiptLink;

    private HashSet<string> ClaimedReceiptIds() => ActiveDecisions
        .Where(decision => decision.AccountContext == _account)
        .GroupBy(decision => decision.ReceiptId, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.MaxBy(decision => decision.UpdatedAtUtc)!)
        .Where(decision => decision.ConfirmedOrder is not null)
        .Select(decision => decision.ReceiptId)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsAvailableReceipt(ReceiptRowViewModel row, HashSet<string> claimed) =>
        !string.IsNullOrWhiteSpace(row.Id) && row.OrderMatch?.Order is null && !claimed.Contains(row.Id);

    private async Task ChooseReceiptForOrderAsync(MarketplaceOrderRowViewModel? order)
    {
        if (!CanChooseReceiptForOrder(order)) return;
        var generation = _generation;
        var account = _account;
        var orderModel = order!.Model;
        var claimed = ClaimedReceiptIds();
        // Picker filters are independent of both dashboard views. A retained manual
        // claim also reserves a receipt when its order is outside the loaded range.
        var available = _rows.Where(row => IsAvailableReceipt(row, claimed))
            .DistinctBy(row => row.Id, StringComparer.OrdinalIgnoreCase).ToArray();
        var models = available.ToDictionary(row => row.Id, row => row.Model, StringComparer.OrdinalIgnoreCase);
        try
        {
            var chosenId = _dialogs.ChooseReceipt(orderModel, available, PreviewReceipt is null ? null :
                row => generation == _generation && account == _account && _rows.Contains(row) &&
                    models.TryGetValue(row.Id, out var model) && ReferenceEquals(model, row.Model)
                    ? PreviewReceipt(row) : Task.CompletedTask);
            if (chosenId is null) return;
            if (generation != _generation || account != _account) return;
            var receipt = available.FirstOrDefault(row => row.Id.Equals(chosenId, StringComparison.OrdinalIgnoreCase));
            // ShowDialog runs a nested dispatcher: background refresh may replace
            // the account, documents or claims while the user is reviewing them.
            if (!CanChooseReceiptForOrder(order) ||
                !ReferenceEquals(order.Model, orderModel) || receipt is null || !_rows.Contains(receipt) ||
                !ReferenceEquals(models[receipt.Id], receipt.Model) || !IsAvailableReceipt(receipt, ClaimedReceiptIds()))
            {
                Status = "Дані змінилися під час вибору чека. Відкрийте «Прив’язати» повторно.";
                return;
            }
            var previous = ActiveDecisions.Where(decision => decision.AccountContext == account &&
                decision.ReceiptId.Equals(receipt.Id, StringComparison.OrdinalIgnoreCase))
                .MaxBy(decision => decision.UpdatedAtUtc);
            await _links.SaveDecisionAsync(new ReceiptOrderDecision
            {
                AccountContext = account, ReceiptId = receipt.Id, ConfirmedOrder = order.Key,
                SuppressAutomatic = false,
                RejectedOrders = previous?.RejectedOrders.Where(key => key != order.Key).ToList() ?? [],
                UpdatedAtUtc = DateTimeOffset.UtcNow
            });
            if (generation != _generation || account != _account) return;
            var decisions = await _links.LoadAsync(account);
            if (generation != _generation || account != _account) return;
            _decisions = decisions;
            SelectedOrder = order;
            SelectedReceipt = receipt;
            ApplyMatches();
            await SaveAutomaticCacheAsync(generation, _scopeCancel.Token);
        }
        catch (Exception)
        {
            if (generation == _generation && account == _account)
                Status = "Не вдалося зберегти локальну прив’язку. Повторіть дію.";
        }
    }
}

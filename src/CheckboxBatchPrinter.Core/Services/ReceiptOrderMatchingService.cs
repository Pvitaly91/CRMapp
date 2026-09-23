using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Only validated receipt UUIDs prove a link. Similarity produces suggestions, never a link.</summary>
public sealed class ReceiptOrderMatchingService
{
    public ReceiptOrderMatch Match(ReceiptRecord receipt, string accountContext,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions,
        bool coverageComplete, ReceiptDetails? details = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountContext);
        ArgumentNullException.ThrowIfNull(orders);
        ArgumentNullException.ThrowIfNull(decisions);

        var available = orders.GroupBy(order => order.Key)
            .Select(group => group.MaxBy(order => order.RetrievedAtUtc)!).ToArray();
        var decision = decisions.Where(item => item.AccountContext == accountContext &&
                SameReceiptId(item.ReceiptId, receipt.Id))
            .MaxBy(item => item.UpdatedAtUtc);
        if (decision?.ConfirmedOrder is { } confirmedKey)
        {
            var confirmed = available.FirstOrDefault(order => order.Key == confirmedKey);
            return new(ReceiptLinkState.Manual, confirmed, confirmed is null
                ? "Прив’язано вручну; дані замовлення ще не завантажено. Збережений зв’язок не втрачено."
                : "Прив’язано вручну", []);
        }

        var rejected = decision?.RejectedOrders.ToHashSet() ?? [];
        // Count ALL exact keys before applying rejections: dismissing one does not prove another correct.
        var direct = Guid.TryParse(receipt.Id, out var receiptUuid)
            ? available.Where(order => order.ReceiptIds.Any(id =>
                Guid.TryParse(id, out var candidateUuid) && candidateUuid == receiptUuid)).ToArray()
            : [];
        var related = Array.Empty<MarketplaceOrder>();
        var missingRelatedManual = false;
        if (receipt.Type == ReceiptTypes.Return && details is not null && SameReceiptId(details.Id, receipt.Id) &&
            Guid.TryParse(details.RelatedReceiptId, out var relatedUuid) && relatedUuid != receiptUuid)
        {
            // Checkbox's documented related_receipt_id identifies the original receipt, not an order.
            // An additional exact/manual original-to-order link is required to complete the chain.
            var originalDecision = decisions.Where(item => item.AccountContext == accountContext &&
                    SameReceiptId(item.ReceiptId, relatedUuid.ToString("D")))
                .MaxBy(item => item.UpdatedAtUtc);
            if (originalDecision?.ConfirmedOrder is { } originalKey)
            {
                related = available.Where(order => order.Key == originalKey).ToArray();
                missingRelatedManual = related.Length == 0;
            }
            else if (originalDecision?.SuppressAutomatic != true)
            {
                related = available.Where(order => order.ReceiptIds.Any(id =>
                    Guid.TryParse(id, out var orderReceiptUuid) && orderReceiptUuid == relatedUuid)).ToArray();
                // A rejected original link cannot become an automatic link through a return.
                if (related.Length == 1 && originalDecision?.RejectedOrders.Contains(related[0].Key) == true)
                    related = [];
            }
        }
        var exact = direct.Concat(related).DistinctBy(order => order.Key).ToArray();
        var allowedExact = exact.Where(order => !rejected.Contains(order.Key)).ToArray();
        if (exact.Length > 1)
            return new(ReceiptLinkState.Conflict, null,
                "Точні посилання чека або початкового чека вказують на кілька замовлень. Потрібне ручне рішення.", allowedExact);
        if (missingRelatedManual)
            return new(ReceiptLinkState.Incomplete, null,
                "Початковий чек прив’язано вручну, але дані його замовлення ще не завантажено.", allowedExact);

        if (exact.Length == 1 && allowedExact.Length == 1 && decision?.SuppressAutomatic != true)
        {
            if (coverageComplete)
                return new(ReceiptLinkState.Exact, exact[0], direct.Length > 0
                    ? "Збіг receipt UUID" : "Повернення: related_receipt_id → точний / підтверджений зв’язок початкового чека", []);
            return new(ReceiptLinkState.Incomplete, null,
                "Знайдено receipt UUID, але перевірка підключень неповна; однозначність ще не підтверджена.", allowedExact);
        }

        var candidates = allowedExact;
        if (receipt.Type == ReceiptTypes.Sell && receipt.DisplayDate is { } receiptDate)
        {
            candidates = candidates.Concat(available.Where(order => !rejected.Contains(order.Key) &&
                    order.Total == receipt.TotalSum && (string.IsNullOrWhiteSpace(order.Currency) ||
                        string.Equals(order.Currency, "UAH", StringComparison.OrdinalIgnoreCase)) &&
                    order.CreatedAt is { } created && created >= receiptDate.AddDays(-30) && created <= receiptDate))
                .DistinctBy(order => order.Key).OrderByDescending(order => order.CreatedAt).ToArray();
        }

        if (candidates.Length > 0)
            return new(ReceiptLinkState.Candidates, null,
                (decision?.SuppressAutomatic == true ? "Автоприв’язку вимкнено вручну. " : "") +
                (candidates.Any(order => string.IsNullOrWhiteSpace(order.Currency))
                    ? "Збіг числової суми; валюта API не підтверджена; потрібне ручне підтвердження."
                    : "Є кандидати; потрібне ручне підтвердження.") +
                (coverageComplete ? "" : " Перевірка діапазону неповна."), candidates);
        return new(coverageComplete ? ReceiptLinkState.NotFound : ReceiptLinkState.Incomplete, null,
            decision?.SuppressAutomatic == true
                ? "Прив’язку знято вручну; автоматичне відновлення вимкнено." +
                    (coverageComplete ? "" : " Перевірка діапазону неповна.")
                : coverageComplete ? "Не знайдено у перевіреному діапазоні" : "Перевірка неповна / API недоступне", []);
    }

    private static bool SameReceiptId(string left, string right) =>
        Guid.TryParse(left, out var leftUuid) && Guid.TryParse(right, out var rightUuid)
            ? leftUuid == rightUuid
            : string.Equals(left, right, StringComparison.Ordinal);
}

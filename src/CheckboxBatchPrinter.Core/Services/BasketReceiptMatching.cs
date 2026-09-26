using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>A snapshot-wide, non-iterative bipartite graph. Suggestions never reserve nodes.</summary>
internal static class BasketReceiptMatching
{
    private sealed record ProductCheck(ProductComparison State, string Reason = "");
    private sealed record Edge(ReceiptRecord Receipt, MarketplaceOrder Order, ProductCheck Check, string Issue,
        decimal? OrderQuantity, decimal? ReceiptQuantity)
    {
        public ProductComparison Products => Check.State;
        // Count is a secondary hypothesis only for complete, ordinary baskets. Missing or
        // adjusted documents remain competitors, not evidence in favor of another order.
        public bool QuantityDiffers => Issue.Length == 0 && OrderQuantity.HasValue && ReceiptQuantity.HasValue && OrderQuantity != ReceiptQuantity;
        public bool Excluded => Products == ProductComparison.Contradiction || QuantityDiffers;
        public string ExclusionReason => Products == ProductComparison.Contradiction ? Check.Reason :
            $"Кількість товарних одиниць у повних кошиках: {OrderQuantity} у замовленні, {ReceiptQuantity} у чеку.";
    }

    internal static void Apply(IDictionary<string, ReceiptOrderMatch> matches, IReadOnlyList<ReceiptRecord> receipts,
        IReadOnlyList<MarketplaceOrder> orders, string account, IReadOnlyList<ReceiptOrderDecision> decisions,
        bool coverageComplete, IReadOnlyDictionary<string, ReceiptDetails> details, AmountMatchScope scope,
        IReadOnlySet<string>? loading)
    {
        var current = decisions.Where(d => d.AccountContext == account)
            .GroupBy(d => NormalizeId(d.ReceiptId)).ToDictionary(g => g.Key, g => g.MaxBy(d => d.UpdatedAtUtc)!);
        var reservedOrders = current.Values.Where(d => d.ConfirmedOrder is not null).Select(d => d.ConfirmedOrder!)
            .Concat(matches.Values.Where(m => (m.State is ReceiptLinkState.Manual or ReceiptLinkState.Exact) && m.Order is not null)
                .Select(m => m.Order!.Key)).ToHashSet();
        var freeReceipts = receipts.Where(r => r.Type == ReceiptTypes.Sell && r.Status == "DONE" &&
            matches[r.Id].State is not (ReceiptLinkState.Manual or ReceiptLinkState.Exact)).ToArray();
        var freeOrders = orders.Where(o => !reservedOrders.Contains(o.Key)).ToArray();
        var edges = new List<Edge>();
        foreach (var receipt in freeReceipts)
        foreach (var order in freeOrders)
        {
            if (!PotentialPair(receipt, order, scope)) continue;
            details.TryGetValue(NormalizeId(receipt.Id), out var detail);
            var products = loading?.Contains(NormalizeId(receipt.Id)) == true ? new(ProductComparison.Loading) : CompareProducts(order, detail);
            var quantitiesLoading = products.State == ProductComparison.Loading;
            edges.Add(new(receipt, order, products, AmountIssue(receipt, order, detail),
                quantitiesLoading ? null : CountUnits(order.Items, order.ItemListComplete ?? order.ItemsComplete),
                quantitiesLoading || detail is null ? null : CountUnits(detail.Items, detail.ItemListComplete ?? detail.ItemsComplete)));
        }
        // Missing details, special totals and rejected hypotheses stay competitors. Only a proven
        // product/count contradiction removes an edge. No assigned suggestion is removed on a second pass.
        var possible = edges.Where(e => !e.Excluded).ToArray();
        var byReceipt = possible.ToLookup(e => e.Receipt.Id);
        var byOrder = possible.ToLookup(e => e.Order.Key);
        var rawByReceipt = edges.ToLookup(e => e.Receipt.Id);
        var components = new Dictionary<string, (HashSet<OrderKey> Orders, HashSet<string> Receipts)>();
        var scopeText = (scope.Description.Length > 0 ? scope.Description :
            $"Область: усі {receipts.Count} завантажених чеків і {orders.Count} замовлень.") +
            " Автозіставлення за сумою: одна календарна дата чека й замовлення (Київ); замовлення не пізніше чека.";
        foreach (var receipt in freeReceipts)
        {
            if (matches[receipt.Id].State == ReceiptLinkState.Conflict ||
                matches[receipt.Id].State == ReceiptLinkState.Incomplete && coverageComplete) continue;
            var raw = rawByReceipt[receipt.Id].ToArray();
            if (raw.Length == 0)
            {
                var previous = matches[receipt.Id];
                var remaining = previous.Candidates.Where(o => !reservedOrders.Contains(o.Key) && PotentialPair(receipt, o, scope)).ToArray();
                if (previous.State == ReceiptLinkState.Candidates && remaining.Length == 0)
                    matches[receipt.Id] = new(coverageComplete ? ReceiptLinkState.NotFound : ReceiptLinkState.Incomplete, null,
                        "Вільного замовлення за сумою на дату чека не знайдено; перевірено також підтверджені прив’язки поза списком. " + scopeText, []);
                continue;
            }
            var options = byReceipt[receipt.Id].ToArray();
            if (!components.TryGetValue(receipt.Id, out var component))
            {
                component = (new(), new() { receipt.Id });
                var pending = new Queue<string>(); pending.Enqueue(receipt.Id);
                while (pending.TryDequeue(out var id))
                    foreach (var edge in byReceipt[id])
                        if (component.Orders.Add(edge.Order.Key))
                            foreach (var competitor in byOrder[edge.Order.Key])
                                if (component.Receipts.Add(competitor.Receipt.Id)) pending.Enqueue(competitor.Receipt.Id);
                foreach (var id in component.Receipts) components[id] = component;
            }
            var (componentOrders, componentReceipts) = component;
            var ambiguous = componentOrders.Count > 1 || componentReceipts.Count > 1;
            current.TryGetValue(NormalizeId(receipt.Id), out var decision);
            var selected = options.Length == 1 && byOrder[options[0].Order.Key].Count() == 1 ? options[0] : null;
            var canSuggest = coverageComplete && selected is not null && selected.Issue.Length == 0 &&
                receipt.TotalKnown && receipt.TotalSum > 0 && selected.Order.Total == receipt.TotalSum &&
                receipt.DisplayDate.HasValue && selected.Order.CreatedAt.HasValue && !HasFiscalEvidence(selected.Order) &&
                decision?.SuppressAutomatic != true && decision?.RejectedOrders.Contains(selected.Order.Key) != true;
            var productState = selected?.Products ?? (options.Length == 0 ? ProductComparison.Contradiction : ProductComparison.Insufficient);
            var resolvedByQuantity = selected is not null && selected.OrderQuantity.HasValue && selected.OrderQuantity == selected.ReceiptQuantity &&
                edges.Any(e => e.QuantityDiffers && (e.Receipt.Id == receipt.Id || e.Order.Key == selected.Order.Key));
            var basis = !canSuggest ? AutomaticLinkBasis.None : productState == ProductComparison.Match ? AutomaticLinkBasis.AmountAndProducts :
                resolvedByQuantity ? AutomaticLinkBasis.AmountAndQuantity : AutomaticLinkBasis.UniqueAmount;
            var message = canSuggest
                ? (productState == ProductComparison.Match ? "За сумою й товарами за один день. " :
                    resolvedByQuantity ? "За сумою та кількістю товарів за один день: взаємно унікальна пара. " : "За сумою за один день: взаємно унікальна пара. ") +
                    "Це ймовірний висновок за даними документів, не фіскальне підтвердження спільного UUID / fiscal_code. "
                : ambiguous ? $"Неоднозначна група: замовлень — {componentOrders.Count}, чеків — {componentReceipts.Count}. Потрібен ручний вибір пари. "
                : options.Length == 0 ? "Сума збігається, товари суперечать. Автопризначення заблоковано. "
                : "Недостатньо даних для автопризначення. " + string.Join(" ", options.Select(e => e.Issue).Where(s => s.Length > 0).Distinct());
            if (!coverageComplete) message = "Перевірка неповна: нові автозв’язки не призначаються. " + message;
            if (productState == ProductComparison.Loading) message += "Товарні дані ще завантажуються; зв’язок буде перевірено повторно. ";
            else if (productState == ProductComparison.Insufficient) message += "Товари: недостатньо даних для перевірки повного кошика; невстановлена відповідність назв не є суперечністю. ";
            if (selected?.OrderQuantity is { } units && selected.ReceiptQuantity == units)
                message += $"Кількість товарних одиниць збігається: {units}. Це не доводить тотожність назв товарів. ";
            foreach (var excluded in raw.Where(e => e.Excluded)
                         .OrderBy(e => e.Order.Key.Marketplace).ThenBy(e => e.Order.Key.ConnectionId, StringComparer.Ordinal)
                         .ThenBy(e => e.Order.Key.OrderId, StringComparer.Ordinal))
                message += $"Виключено замовлення №{excluded.Order.Number}: {excluded.ExclusionReason} ";
            if (raw.Any(e => string.IsNullOrWhiteSpace(e.Order.Currency))) message += "Валюта API не підтверджена. ";
            if (decision?.SuppressAutomatic == true) message += "Автоприв’язку вимкнено вручну. ";
            if (selected is not null && HasFiscalEvidence(selected.Order)) message += "Фіскальні ключі не підтвердили цю пару. ";
            if (canSuggest && selected!.Order.DeliveryCost is > 0m)
                message += "Доставка вказана окремо: Total дорівнює відомій сумі товарів; доставку не додано й не віднято. ";
            message += scopeText;
            var candidates = raw.Select(e => e.Order).Where(o => decision?.RejectedOrders.Contains(o.Key) != true)
                .OrderBy(o => o.Key.Marketplace).ThenBy(o => o.Key.ConnectionId, StringComparer.Ordinal)
                .ThenBy(o => o.Key.OrderId, StringComparer.Ordinal).ToArray();
            matches[receipt.Id] = new(canSuggest ? ReceiptLinkState.Suggested : coverageComplete ? ReceiptLinkState.Candidates : ReceiptLinkState.Incomplete,
                canSuggest ? selected!.Order : null, message, candidates)
            {
                Basis = basis,
                Products = productState, Ambiguous = ambiguous, Scope = scopeText,
                CompetingOrderCount = componentOrders.Count,
                GroupOrders = freeOrders.Where(o => componentOrders.Contains(o.Key)).OrderBy(o => o.Key.Marketplace)
                    .ThenBy(o => o.Key.ConnectionId, StringComparer.Ordinal).ThenBy(o => o.Key.OrderId, StringComparer.Ordinal).ToArray(),
                CompetingReceiptIds = componentReceipts.Order(StringComparer.Ordinal).ToArray()
            };
        }
    }

    private static decimal? CountUnits(IReadOnlyList<OrderItem> items, bool complete)
    {
        // Raw line counts change when identical goods are split/merged. Sum normalized
        // quantities instead. Fractional/unknown quantities have no reliable piece count.
        if (!complete || items.Count == 0 || items.Any(i => i.Quantity is not > 0m || decimal.Truncate(i.Quantity.Value) != i.Quantity.Value))
            return null;
        try { return items.Sum(i => i.Quantity!.Value); }
        catch (OverflowException) { return null; }
    }

    internal static bool PotentialPair(ReceiptRecord receipt, MarketplaceOrder order, AmountMatchScope scope) =>
        (string.IsNullOrWhiteSpace(order.Currency) || order.Currency.Equals("UAH", StringComparison.OrdinalIgnoreCase)) &&
        (!receipt.TotalKnown || order.Total is null || order.Total == receipt.TotalSum) &&
        (order.CreatedAt is not { } created ||
            (scope.OrderRange is not { } range || created >= range.From && created < range.ToExclusive) &&
            (receipt.DisplayDate is not { } date || SameDayBeforeReceipt(created, date)));

    // Compare calendar dates in the business timezone, not the PC timezone or the API's
    // textual offset. Unknown dates are handled by PotentialPair/AmountIssue and stay
    // competitors. Known dates must satisfy both the same-day and not-after-receipt rules.
    internal static bool SameDayBeforeReceipt(DateTimeOffset created, DateTimeOffset receiptDate) =>
        created <= receiptDate && DateRangeBuilder.KyivDate(created) == DateRangeBuilder.KyivDate(receiptDate);

    private static string AmountIssue(ReceiptRecord receipt, MarketplaceOrder order, ReceiptDetails? details)
    {
        if (!receipt.TotalKnown || order.Total is null) return "Суму не надано або не розібрано; невідома сума не є нулем. ";
        if (!receipt.DisplayDate.HasValue || !order.CreatedAt.HasValue) return "Невідома дата документа. ";
        if (receipt.AmountComparisonIssue.Length > 0) return receipt.AmountComparisonIssue;
        if (details?.AmountComparisonIssue.Length > 0) return details.AmountComparisonIssue;
        if (order.AmountComparisonIssue.Length > 0) return order.AmountComparisonIssue;
        if (order.Discount is not (null or 0m)) return "Знижка: потрібна перевірка складу загальної суми. ";
        if (order.DeliveryCost is not (null or 0m) && !ProductsAccountForTotal(order))
            return "Доставка: включення вартості в Total потребує перевірки; суми не коригуються. ";
        if (InconsistentMoney(order.Items, order.ItemListComplete ?? order.ItemsComplete, order.Total.Value) ||
            details is not null && InconsistentMoney(details.Items, details.ItemListComplete ?? details.ItemsComplete, receipt.TotalSum))
            return "Відомі суми позицій суперечать загальній сумі або містять коригування. ";
        return "";
    }

    private static bool ProductsAccountForTotal(MarketplaceOrder order)
    {
        if (!(order.ItemListComplete ?? order.ItemsComplete) || order.Items.Count == 0 || order.Total is null) return false;
        try
        {
            decimal sum = 0;
            foreach (var item in order.Items)
            {
                if (item.Total is { } total && total >= 0) sum += total;
                else if (item.Total is null && item.Quantity is > 0m && item.UnitPrice is >= 0m)
                    sum += checked(item.Quantity.Value * item.UnitPrice.Value);
                else return false;
            }
            return sum == order.Total.Value;
        }
        catch (OverflowException) { return false; }
    }

    private static bool InconsistentMoney(IReadOnlyList<OrderItem> items, bool complete, decimal total)
    {
        if (!complete || items.Count == 0) return false;
        try
        {
            return items.Any(i => i.UnitPrice is { } p && i.Quantity is { } q && i.Total is { } t && checked(p * q) != t) ||
                items.All(i => i.Total.HasValue) && items.Sum(i => i.Total!.Value) != total;
        }
        catch (OverflowException) { return true; }
    }

    private static ProductCheck CompareProducts(MarketplaceOrder order, ReceiptDetails? details)
    {
        if (details is null || !(order.ItemListComplete ?? order.ItemsComplete) || !(details.ItemListComplete ?? details.ItemsComplete) ||
            order.Items.Count == 0 || details.Items.Count == 0 || order.Items.Concat(details.Items).Any(i => string.IsNullOrWhiteSpace(i.Name)))
            return new(ProductComparison.Insufficient);
        try
        {
            var left = Group(order.Items); var right = Group(details.Items);
            if (!left.Keys.ToHashSet().SetEquals(right.Keys))
            {
                var reorderedLeft = Group(order.Items, ignoreWordOrder: true);
                var reorderedRight = Group(details.Items, ignoreWordOrder: true);
                if (reorderedLeft.Keys.ToHashSet().SetEquals(reorderedRight.Keys))
                {
                    var differentQuantity = reorderedLeft.FirstOrDefault(p => p.Value.HasValue && reorderedRight[p.Key].HasValue && p.Value != reorderedRight[p.Key]);
                    if (differentQuantity.Key is not null)
                        return new(ProductComparison.Contradiction, $"Кількість товару «{differentQuantity.Key}» при однакових словах назви: {differentQuantity.Value} у замовленні, {reorderedRight[differentQuantity.Key]} у чеку.");
                    return new(reorderedLeft.Any(p => !p.Value.HasValue || !reorderedRight[p.Key].HasValue) ? ProductComparison.Insufficient : ProductComparison.Match);
                }
            }
            var sameNames = left.Keys.ToHashSet().SetEquals(right.Keys);
            foreach (var p in left.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                // A differently named line could be another representation of the same product.
                // Do not prove a total-quantity difference while such an alias is still possible.
                if (right.TryGetValue(p.Key, out var quantity) && p.Value.HasValue && quantity.HasValue && p.Value != quantity &&
                    left.Keys.Concat(right.Keys).Where(k => k != p.Key).All(k => ProductNameEvidence.Difference(p.Key, k) is not null))
                    return new(ProductComparison.Contradiction, $"Кількість товару «{p.Key}»: {p.Value} у замовленні, {quantity} у чеку.");
            }
            if (sameNames)
            {
                // With the same literal identities on both sides there are no unmatched aliases.
                var mismatch = left.OrderBy(p => p.Key, StringComparer.Ordinal)
                    .FirstOrDefault(p => p.Value.HasValue && right[p.Key].HasValue && p.Value != right[p.Key]);
                if (mismatch.Key is not null)
                    return new(ProductComparison.Contradiction, $"Кількість товару «{mismatch.Key}»: {mismatch.Value} у замовленні, {right[mismatch.Key]} у чеку.");
                return new(left.Any(p => !p.Value.HasValue || !right[p.Key].HasValue) ? ProductComparison.Insufficient : ProductComparison.Match);
            }
            var onlyLeft = left.Keys.Except(right.Keys).ToArray();
            var onlyRight = right.Keys.Except(left.Keys).ToArray();
            if (onlyLeft.Length == 0 || onlyRight.Length == 0) return new(ProductComparison.Insufficient);
            // Exact common identities have already been accounted for. Exclusion of a remaining
            // line requires incompatibility with EVERY remaining counterpart, not just one
            // differently spelled line. No known identity/attribute alignment means insufficient data.
            foreach (var (source, targets) in new[] { (onlyLeft, onlyRight), (onlyRight, onlyLeft) })
            foreach (var name in source.Order(StringComparer.Ordinal))
            {
                var reasons = targets.Order(StringComparer.Ordinal).Select(other => ProductNameEvidence.Difference(name, other)).ToArray();
                if (reasons.All(reason => reason is not null))
                    return new(ProductComparison.Contradiction, string.Join(" ", reasons.Distinct()));
            }
            return new(ProductComparison.Insufficient);
        }
        catch (OverflowException) { return new(ProductComparison.Insufficient); }
    }

    private static Dictionary<string, decimal?> Group(IReadOnlyList<OrderItem> items, bool ignoreWordOrder = false) =>
        items.GroupBy(i => ignoreWordOrder ? ProductNameEvidence.WordOrderIdentity(i.Name) : ProductNameEvidence.Identity(i.Name))
        .ToDictionary(g => g.Key, g => g.All(i => i.Quantity is > 0m) ? (decimal?)g.Sum(i => i.Quantity!.Value) : null);

    private static bool HasFiscalEvidence(MarketplaceOrder o) => o.ReceiptIds.Count > 0 || o.FiscalReferences.Count > 0 ||
        o.FiscalReceiptNumbers.Count > 0 || o.FiscalReceiptUrls.Count > 0;

    internal static string NormalizeId(string value) => Guid.TryParse(value, out var id) ? id.ToString("D") : value;
}

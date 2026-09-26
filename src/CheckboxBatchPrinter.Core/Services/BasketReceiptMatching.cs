using System.Text;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>A snapshot-wide, non-iterative bipartite graph. Suggestions never reserve nodes.</summary>
internal static class BasketReceiptMatching
{
    private sealed record Edge(ReceiptRecord Receipt, MarketplaceOrder Order, ProductComparison Products, string Issue);

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
            var products = loading?.Contains(NormalizeId(receipt.Id)) == true ? ProductComparison.Loading : CompareProducts(order, detail);
            edges.Add(new(receipt, order, products, AmountIssue(receipt, order, detail)));
        }
        // Missing details, special totals and rejected hypotheses stay competitors. Only a proven
        // product contradiction removes an edge. No assigned suggestion is removed on a second pass.
        var possible = edges.Where(e => e.Products != ProductComparison.Contradiction).ToArray();
        var byReceipt = possible.ToLookup(e => e.Receipt.Id);
        var byOrder = possible.ToLookup(e => e.Order.Key);
        var rawByReceipt = edges.ToLookup(e => e.Receipt.Id);
        var components = new Dictionary<string, (HashSet<OrderKey> Orders, HashSet<string> Receipts)>();
        var scopeText = scope.Description.Length > 0 ? scope.Description :
            $"Область: усі {receipts.Count} завантажених чеків і {orders.Count} замовлень; історія до дати чека — {scope.HistoryDays} днів.";
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
                        "Вільного замовлення не знайдено: перевірено також підтверджені прив’язки поза списком. " + scopeText, []);
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
            var message = canSuggest
                ? (productState == ProductComparison.Match ? "За сумою й товарами. " : "За сумою: взаємно унікальна пара. ") +
                    "Це ймовірний висновок за даними документів, не фіскальне підтвердження спільного UUID / fiscal_code. "
                : ambiguous ? $"Неоднозначна група: замовлень — {componentOrders.Count}, чеків — {componentReceipts.Count}. Потрібен ручний вибір пари. "
                : options.Length == 0 ? "Сума збігається, товари суперечать. Автопризначення заблоковано. "
                : "Недостатньо даних для автопризначення. " + string.Join(" ", options.Select(e => e.Issue).Where(s => s.Length > 0).Distinct());
            if (!coverageComplete) message = "Перевірка неповна: нові автозв’язки не призначаються. " + message;
            if (productState == ProductComparison.Loading) message += "Товарні дані ще завантажуються; зв’язок буде перевірено повторно. ";
            else if (productState == ProductComparison.Insufficient) message += "Товари: недостатньо даних для перевірки повного кошика. ";
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
                Basis = canSuggest ? productState == ProductComparison.Match ? AutomaticLinkBasis.AmountAndProducts : AutomaticLinkBasis.UniqueAmount : AutomaticLinkBasis.None,
                Products = productState, Ambiguous = ambiguous, Scope = scopeText,
                CompetingOrderCount = componentOrders.Count,
                GroupOrders = freeOrders.Where(o => componentOrders.Contains(o.Key)).OrderBy(o => o.Key.Marketplace)
                    .ThenBy(o => o.Key.ConnectionId, StringComparer.Ordinal).ThenBy(o => o.Key.OrderId, StringComparer.Ordinal).ToArray(),
                CompetingReceiptIds = componentReceipts.Order(StringComparer.Ordinal).ToArray()
            };
        }
    }

    internal static bool PotentialPair(ReceiptRecord receipt, MarketplaceOrder order, AmountMatchScope scope) =>
        (string.IsNullOrWhiteSpace(order.Currency) || order.Currency.Equals("UAH", StringComparison.OrdinalIgnoreCase)) &&
        (!receipt.TotalKnown || order.Total is null || order.Total == receipt.TotalSum) &&
        (order.CreatedAt is not { } created ||
            (scope.OrderRange is not { } range || created >= range.From && created < range.ToExclusive) &&
            (receipt.DisplayDate is not { } date || created <= date &&
                (scope.OrderRange is not null || created >= date.AddDays(-scope.HistoryDays))));

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

    internal static ProductComparison CompareProducts(MarketplaceOrder order, ReceiptDetails? details)
    {
        if (details is null || !(order.ItemListComplete ?? order.ItemsComplete) || !(details.ItemListComplete ?? details.ItemsComplete) ||
            order.Items.Count == 0 || details.Items.Count == 0 || order.Items.Concat(details.Items).Any(i => string.IsNullOrWhiteSpace(i.Name)))
            return ProductComparison.Insufficient;
        try
        {
            var left = Group(order.Items); var right = Group(details.Items);
            if (left.Any(p => right.TryGetValue(p.Key, out var quantity) && p.Value.HasValue && quantity.HasValue && p.Value != quantity))
                return ProductComparison.Contradiction;
            var onlyLeft = left.Keys.Except(right.Keys).ToArray();
            var onlyRight = right.Keys.Except(left.Keys).ToArray();
            if (onlyLeft.Length > 0 || onlyRight.Length > 0)
            {
                if (onlyLeft.Any(l => onlyRight.Any(r => UncertainNames(l, r)))) return ProductComparison.Insufficient;
                return ProductComparison.Contradiction;
            }
            if (left.Any(p => p.Value.HasValue && right[p.Key].HasValue && p.Value != right[p.Key])) return ProductComparison.Contradiction;
            return left.Any(p => !p.Value.HasValue || !right[p.Key].HasValue) ? ProductComparison.Insufficient : ProductComparison.Match;
        }
        catch (OverflowException) { return ProductComparison.Insufficient; }
    }

    private static Dictionary<string, decimal?> Group(IReadOnlyList<OrderItem> items) => items.GroupBy(i => NormalizeName(i.Name))
        .ToDictionary(g => g.Key, g => g.All(i => i.Quantity is > 0m) ? (decimal?)g.Sum(i => i.Quantity!.Value) : null);

    private static bool UncertainNames(string left, string right)
    {
        static string LettersAndNumbers(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());
        static int Script(string s) => (s.Any(c => c is >= 'А' and <= 'Я' or 'І' or 'Ї' or 'Є' or 'Ґ') ? 1 : 0) |
            (s.Any(c => c is >= 'A' and <= 'Z') ? 2 : 0);
        if ((Script(left) & Script(right)) == 0) return true;
        var leftNumbers = Regex.Matches(left, @"\d+(?:[.,/]\d+)*").Select(m => m.Value);
        var rightNumbers = Regex.Matches(right, @"\d+(?:[.,/]\d+)*").Select(m => m.Value);
        if (!leftNumbers.SequenceEqual(rightNumbers) && leftNumbers.Any() && rightNumbers.Any()) return false;
        return left.Contains(right, StringComparison.Ordinal) || right.Contains(left, StringComparison.Ordinal) ||
            LettersAndNumbers(left) == LettersAndNumbers(right) || left.EndsWith('.') || right.EndsWith('.');
    }

    private static bool HasFiscalEvidence(MarketplaceOrder o) => o.ReceiptIds.Count > 0 || o.FiscalReferences.Count > 0 ||
        o.FiscalReceiptNumbers.Count > 0 || o.FiscalReceiptUrls.Count > 0;

    private static string NormalizeName(string name)
    {
        var normalized = new string(name.Normalize(NormalizationForm.FormC).Select(c => c switch
        {
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => '-',
            '\u2018' or '\u2019' or '\u201a' or '\u201b' => '\'',
            '\u201c' or '\u201d' or '\u201e' or '\u201f' or '\u00ab' or '\u00bb' => '"',
            _ => c
        }).ToArray());
        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
    internal static string NormalizeId(string value) => Guid.TryParse(value, out var id) ? id.ToString("D") : value;
}

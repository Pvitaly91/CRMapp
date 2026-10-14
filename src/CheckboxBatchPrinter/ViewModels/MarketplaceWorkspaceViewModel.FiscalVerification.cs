using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceWorkspaceViewModel
{
    private readonly HashSet<FiscalPublicationBlock> _pendingFiscalPublications = [];
    private readonly HashSet<FiscalPublicationBlock> _pendingRefreshPublications = [];

    public IDisposable BeginRefreshPublication()
    {
        var account = _account;
        var configuration = _configurationVersion;
        var accountVersion = _parallelAccountVersion;
        var display = (_displayFrom, _displayTo);
        bool IsCurrent() => account == _account && configuration == _configurationVersion &&
            accountVersion == _parallelAccountVersion && display == (_displayFrom, _displayTo);
        var block = new FiscalPublicationBlock(IsCurrent, completed =>
        {
            _pendingRefreshPublications.Remove(completed);
            if (IsCurrent()) ApplyMatches();
        });
        _pendingRefreshPublications.Add(block);
        return block;
    }

    private async Task EnsureAttachedBeforeFiscalPublicationAsync()
    {
        using var publication = BlockFiscalPublication();
        if (publication is null) { await EnsureAttachedAsync(); return; }
        var snapshot = _snapshot;
        var coverage = _orderCoverage.ToArray();
        var complete = _coverageComplete;
        var allowRestore = _allowCachedRestore;
        var legacyVerified = _legacyCoverageVerified;
        try { await EnsureAttachedAsync(); }
        finally
        {
            // Parallel fetching has already persisted raw orders before attachment.
            // Load settings/decisions, but do not expose that new raw cache as the
            // previously published snapshot while its fiscal verification is pending.
            if (publication.IsCurrent)
            {
                _snapshot = snapshot;
                _orderCoverage.Clear(); _orderCoverage.AddRange(coverage);
                _coverageComplete = complete;
                _allowCachedRestore = allowRestore;
                _legacyCoverageVerified = legacyVerified;
            }
        }
    }

    private FiscalPublicationBlock? BlockFiscalPublication()
    {
        if (_fiscalVerifier is null || _account.Length == 0) return null;
        var selection = SelectionScope;
        var configuration = _configurationVersion;
        var account = _parallelAccountVersion;
        var block = new FiscalPublicationBlock(() => SelectionScope == selection &&
                configuration == _configurationVersion && account == _parallelAccountVersion,
            block => _pendingFiscalPublications.Remove(block));
        _pendingFiscalPublications.Add(block);
        return block;
    }

    private sealed class FiscalPublicationBlock(Func<bool> isCurrent, Action<FiscalPublicationBlock> release) : IDisposable
    {
        private bool _disposed;
        public bool IsCurrent => !_disposed && isCurrent();
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            release(this);
        }
    }

    private async Task<IReadOnlyList<MarketplaceOrder>> VerifyFiscalReferencesInPeriodAsync(
        IReadOnlyList<MarketplaceOrder> orders, DateOnly from, DateOnly to, CancellationToken token)
    {
        // Cached history remains available for existing links, but it must not trigger
        // a new HTTP fiscal-code search on every refresh of today's dashboard.
        static string NormalizeId(string id) => Guid.TryParse(id, out var uuid) ? uuid.ToString("D") : id;
        var byReceiptId = _rows.Select(row => row.Model).GroupBy(receipt => NormalizeId(receipt.Id))
            .ToDictionary(group => group.Key, group => group.First());
        var previousOrders = ActiveOrders.GroupBy(order => order.Key).ToDictionary(group => group.Key, group => group.First());
        var established = _establishedLinks.Restore(_account, byReceiptId.Values.ToArray(), orders,
            ActiveDecisions, EnabledConnectionIds);
        orders = established.Orders;
        var establishedKeys = established.Matches.Values.Where(match => match.Order is not null)
            .Select(match => match.Order!.Key).ToHashSet();
        var savedSuggested = _matcher.LinkedOrderKeys(_automaticCache, _account, byReceiptId.Values.ToArray(), orders, ActiveDecisions);
        var manual = ActiveDecisions.Where(decision => decision.AccountContext == _account)
            .GroupBy(decision => NormalizeId(decision.ReceiptId))
            .Select(group => group.MaxBy(decision => decision.UpdatedAtUtc)!)
            .Where(decision => decision.ConfirmedOrder is not null)
            .ToDictionary(decision => NormalizeId(decision.ReceiptId));
        var unchangedLinked = establishedKeys.Concat(savedSuggested).Concat(manual.Values.Select(decision => decision.ConfirmedOrder!))
            .ToHashSet();
        unchangedLinked.RemoveWhere(key => !previousOrders.TryGetValue(key, out var previous) ||
            orders.FirstOrDefault(order => order.Key == key) is not { } current || !SameFiscalDocuments(previous, current));
        var linkedReceipts = established.Matches.Where(pair => pair.Value.Order is not null)
            .ToDictionary(pair => NormalizeId(pair.Key), pair => pair.Value.Order!.Key);
        foreach (var (id, decision) in manual) linkedReceipts[id] = decision.ConfirmedOrder!;
        foreach (var row in _rows.Where(row => row.OrderMatch is { State: ReceiptLinkState.Suggested, Order: not null } &&
                     savedSuggested.Contains(row.OrderMatch.Order.Key))) linkedReceipts[NormalizeId(row.Id)] = row.OrderMatch!.Order!.Key;
        var detailsById = _receiptDetails.Values.GroupBy(details => NormalizeId(details.Id))
            .ToDictionary(group => group.Key, group => group.First());
        var relevantIds = _rows.Where(row => IsInRange(row, from, to)).Select(row => NormalizeId(row.Id)).ToHashSet();
        var pending = new Queue<string>(relevantIds);
        while (pending.TryDequeue(out var id))
        {
            // A current return can depend on a sale outside the calendar period.
            // Follow only Checkbox's typed relation, never an amount/name guess.
            if (byReceiptId.TryGetValue(id, out var receipt) && receipt.Type == ReceiptTypes.Return &&
                detailsById.TryGetValue(id, out var details) && Guid.TryParse(details.RelatedReceiptId, out var related) &&
                related != Guid.Empty && relevantIds.Add(related.ToString("D")))
                pending.Enqueue(related.ToString("D"));
        }
        var receipts = byReceiptId.Where(pair => relevantIds.Contains(pair.Key)).Select(pair => pair.Value).ToArray();
        var receiptIds = relevantIds.ToArray();
        var fiscalCodes = receipts.Select(receipt => receipt.FiscalCode).Where(code => code.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var explicitlyLinked = ActiveDecisions.Where(decision => decision.AccountContext == _account &&
                decision.ConfirmedOrder is not null && receiptIds.Any(id => CheckboxReceiptReference.SameId(id, decision.ReceiptId)))
            .Select(decision => decision.ConfirmedOrder!).ToHashSet();

        bool NeedsVerification(MarketplaceOrder order)
        {
            if (order.FiscalReferences.Count == 0) return false;
            if (unchangedLinked.Contains(order.Key) && !receipts.Any(receipt =>
                    linkedReceipts.GetValueOrDefault(NormalizeId(receipt.Id)) != order.Key &&
                    HasFiscalClaim(order, receipt))) return false;
            if (order.CreatedAt is { } created && DateRangeBuilder.KyivDate(created) is var day && day >= from && day <= to)
                return true;
            if (explicitlyLinked.Contains(order.Key) || order.ReceiptIds.Any(id => receiptIds.Any(receipt => CheckboxReceiptReference.SameId(id, receipt))))
                return true;
            return order.FiscalReferences.Where(reference => reference.Order == order.Key && reference.Source.Length > 0 &&
                reference.DocumentId.Length > 0 && string.Equals(reference.Provider, "Checkbox", StringComparison.OrdinalIgnoreCase))
                .Any(reference => reference.Kind switch
                {
                    FiscalDocumentKeyKind.CheckboxReceiptUuid => receiptIds.Any(id => CheckboxReceiptReference.SameId(reference.Value, id)),
                    FiscalDocumentKeyKind.CheckboxReceiptUrl => CheckboxReceiptReference.TryParseUrl(reference.Value, out var parsed) &&
                        (parsed.ReceiptId.Length > 0 && receiptIds.Any(id => CheckboxReceiptReference.SameId(parsed.ReceiptId, id)) ||
                         parsed.FiscalCode.Length > 0 && fiscalCodes.Contains(parsed.FiscalCode)),
                    FiscalDocumentKeyKind.FiscalCode => fiscalCodes.Contains(reference.Value),
                    _ => false
                });
        }

        var targets = orders.Where(NeedsVerification).ToArray();
        var verified = targets.Length > 0
            ? await _fiscalVerifier!.VerifyAsync(targets, _account, token)
            : [];
        token.ThrowIfCancellationRequested();
        var byKey = verified.ToDictionary(order => order.Key);
        return orders.Select(order => byKey.TryGetValue(order.Key, out var current) ? current : establishedKeys.Contains(order.Key) ? order : order with
        {
            // A skipped order receives no new account-scoped proof. Its immutable
            // UUID, code, source and cashier assertions are all retained for matching.
            FiscalReferences = order.FiscalReferences.Select(reference => reference with
                { VerifiedReceiptId = "", VerifiedAccountContext = "" }).ToArray()
        }).ToArray();
    }

    private static bool SameFiscalDocuments(MarketplaceOrder previous, MarketplaceOrder current)
    {
        static FiscalDocumentReference Raw(FiscalDocumentReference reference) => reference with
            { VerifiedReceiptId = "", VerifiedAccountContext = "" };
        static string NormalizeId(string id) => Guid.TryParse(id, out var uuid) ? uuid.ToString("D") : id;
        return previous.Key == current.Key &&
            previous.ReceiptIds.Select(NormalizeId).ToHashSet(StringComparer.Ordinal).SetEquals(current.ReceiptIds.Select(NormalizeId)) &&
            previous.FiscalReceiptNumbers.ToHashSet(StringComparer.Ordinal).SetEquals(current.FiscalReceiptNumbers) &&
            previous.FiscalReceiptUrls.ToHashSet(StringComparer.Ordinal).SetEquals(current.FiscalReceiptUrls) &&
            previous.FiscalReferences.Select(Raw).ToHashSet().SetEquals(current.FiscalReferences.Select(Raw));
    }

    private static bool HasFiscalClaim(MarketplaceOrder order, ReceiptRecord receipt) =>
        order.ReceiptIds.Any(id => CheckboxReceiptReference.SameId(id, receipt.Id)) ||
        receipt.FiscalCode.Length > 0 && order.FiscalReceiptNumbers.Contains(receipt.FiscalCode, StringComparer.Ordinal) ||
        order.FiscalReferences.Where(reference => reference.Order == order.Key).Any(reference => reference.Kind switch
        {
            FiscalDocumentKeyKind.CheckboxReceiptUuid => CheckboxReceiptReference.SameId(reference.Value, receipt.Id),
            FiscalDocumentKeyKind.FiscalCode => receipt.FiscalCode.Length > 0 && reference.Value == receipt.FiscalCode,
            FiscalDocumentKeyKind.CheckboxReceiptUrl => CheckboxReceiptReference.TryParseUrl(reference.Value, out var parsed) &&
                (CheckboxReceiptReference.SameId(parsed.ReceiptId, receipt.Id) || receipt.FiscalCode.Length > 0 && parsed.FiscalCode == receipt.FiscalCode),
            _ => false
        });
}

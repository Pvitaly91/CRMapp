using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceWorkspaceViewModel
{
    private readonly HashSet<FiscalPublicationBlock> _pendingFiscalPublications = [];

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
        return orders.Select(order => byKey.TryGetValue(order.Key, out var current) ? current : order with
        {
            // A skipped order receives no new account-scoped proof. Its immutable
            // UUID, code, source and cashier assertions are all retained for matching.
            FiscalReferences = order.FiscalReferences.Select(reference => reference with
                { VerifiedReceiptId = "", VerifiedAccountContext = "" }).ToArray()
        }).ToArray();
    }
}

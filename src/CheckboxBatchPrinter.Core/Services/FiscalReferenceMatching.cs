using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

internal static class FiscalReferenceMatching
{
    internal sealed record Evidence(bool Exact, bool Conflict, string Basis);

    internal static Evidence Evaluate(MarketplaceOrder order, ReceiptRecord receipt, string account,
        IReadOnlyList<ReceiptRecord>? scope)
    {
        var references = order.FiscalReferences.Where(r => r.Order == order.Key && r.Source.Length > 0 &&
            r.DocumentId.Length > 0 && string.Equals(r.Provider, "Checkbox", StringComparison.OrdinalIgnoreCase)).ToArray();
        var exact = order.ReceiptIds.Any(id => CheckboxReceiptReference.SameId(id, receipt.Id));
        var basis = exact ? "За UUID чека" : "";
        var conflict = false;
        foreach (var document in references.GroupBy(r => r.DocumentId))
        {
            var keys = document.ToArray();
            var ids = keys.Select(r => r.Kind switch
            {
                FiscalDocumentKeyKind.CheckboxReceiptUuid => Guid.TryParse(r.Value, out var id) && id != Guid.Empty ? id.ToString("D") : "",
                FiscalDocumentKeyKind.CheckboxReceiptUrl => CheckboxReceiptReference.TryParseUrl(r.Value, out var url) ? url.ReceiptId : "",
                _ => ""
            }).Where(id => id.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var codes = keys.Where(r => r.Kind == FiscalDocumentKeyKind.FiscalCode).Select(r => r.Value)
                .Concat(keys.Where(r => r.Kind == FiscalDocumentKeyKind.CheckboxReceiptUrl)
                    .Select(r => CheckboxReceiptReference.TryParseUrl(r.Value, out var url) ? url.FiscalCode : ""))
                .Where(code => code.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
            var idMatches = ids.Any(id => CheckboxReceiptReference.SameId(id, receipt.Id));
            var codeMatches = receipt.FiscalCode.Length > 0 && codes.Contains(receipt.FiscalCode, StringComparer.Ordinal);
            var verifiedMatches = keys.Any(r => r.VerifiedAccountContext == account &&
                CheckboxReceiptReference.SameId(r.VerifiedReceiptId, receipt.Id));
            if (!idMatches && !verifiedMatches && (!codeMatches || keys.Any(r => !CheckboxReceiptReference.ContextMatches(r, receipt)))) continue;
            // Contradictions inside ONE fiscal document are never resolved by choosing the convenient key.
            if (ids.Length > 1 || codes.Length > 1 ||
                (ids.Length == 1 && !idMatches) ||
                (codes.Length > 0 && receipt.FiscalCode.Length > 0 && !codeMatches) ||
                keys.Any(r => !CheckboxReceiptReference.ContextMatches(r, receipt)))
            { conflict = true; continue; }
            if (idMatches)
            {
                // If an additional fiscal assertion exists, wait until it can be checked.
                if (codes.Length > 0 && receipt.FiscalCode.Length == 0) continue;
                exact = true;
                basis = keys.Any(r => r.Kind == FiscalDocumentKeyKind.CheckboxReceiptUrl) ? "За посиланням на чек" : "За UUID чека";
            }
            else if (codeMatches && keys.Any(r =>
                (r.Kind == FiscalDocumentKeyKind.FiscalCode || r.Kind == FiscalDocumentKeyKind.CheckboxReceiptUrl) &&
                // A bare fiscal code/provider or a unique amount is not seller context.
                (r.CashRegisterFiscalNumber.Length > 0 ||
                    (r.VerifiedAccountContext == account && CheckboxReceiptReference.SameId(r.VerifiedReceiptId, receipt.Id))) &&
                CheckboxReceiptReference.ContextMatches(r, receipt)))
            {
                // Reject collisions even within the loaded account/range. No implicit one-row scope.
                var matches = scope?.Where(r => r.FiscalCode == receipt.FiscalCode &&
                    keys.All(k => CheckboxReceiptReference.ContextMatches(k, r))).Select(r => r.Id).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (matches?.Length == 1 && CheckboxReceiptReference.SameId(matches[0], receipt.Id))
                { exact = true; basis = "За фіскальним номером"; }
                else if (matches?.Length > 1) conflict = true;
            }
        }
        return new(exact, conflict, basis);
    }
}

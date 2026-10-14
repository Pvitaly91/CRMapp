using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IFiscalReferenceVerifier
{
    Task<IReadOnlyList<MarketplaceOrder>> VerifyAsync(IReadOnlyList<MarketplaceOrder> orders, string accountContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Exact fiscal-code searches from contextual or authoritative Seller evidence, within the current cashier's rights.</summary>
public sealed class FiscalReferenceVerifier(CheckboxApiClient api, ISettingsService settings, TimeProvider? timeProvider = null) : IFiscalReferenceVerifier
{
    private readonly FiscalCodeLookupCache _lookupCache = new(timeProvider ?? TimeProvider.System);
    public async Task<IReadOnlyList<MarketplaceOrder>> VerifyAsync(IReadOnlyList<MarketplaceOrder> orders, string accountContext,
        CancellationToken cancellationToken = default)
    {
        var config = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (PrintAccountContext.Create(config) != accountContext || config.ApiBaseUrl.TrimEnd('/') != "https://api.checkbox.ua")
            return WithoutVerification(orders, "Поточний обліковий контекст Checkbox не підтверджено. Оновіть чеки та замовлення.");
        try
        {
            return await VerifyCurrentAccountAsync(orders, accountContext, config, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Cancellation can leave an earlier lookup cached before the normal
            // final account check. Revalidate without the cancelled token; ordinary
            // cancellation must not clear a confirmed cashier's error cooldown.
            var accountUnchanged = false;
            try
            {
                var current = await settings.LoadAsync(CancellationToken.None).ConfigureAwait(false);
                accountUnchanged = PrintAccountContext.Create(current) == accountContext;
            }
            catch { }
            if (!accountUnchanged) await _lookupCache.InvalidateAccountAsync(accountContext).ConfigureAwait(false);
            throw;
        }
    }

    private async Task<IReadOnlyList<MarketplaceOrder>> VerifyCurrentAccountAsync(IReadOnlyList<MarketplaceOrder> orders,
        string accountContext, AppSettings config, CancellationToken cancellationToken)
    {
        var lookups = new Dictionary<string, IReadOnlyList<ReceiptRecord>>(StringComparer.Ordinal);
        var lookupAttempts = 0;
        var result = new List<MarketplaceOrder>();
        foreach (var order in orders)
        {
            var references = new List<FiscalDocumentReference>();
            var status = order.FiscalDataStatus;
            var uuidDocuments = order.FiscalReferences.Where(reference => reference.Order == order.Key &&
                    reference.Source.Length > 0 && reference.DocumentId.Length > 0 &&
                    string.Equals(reference.Provider, "Checkbox", StringComparison.OrdinalIgnoreCase) && HasReceiptUuid(reference))
                .Select(reference => reference.DocumentId).ToHashSet(StringComparer.Ordinal);
            foreach (var raw in order.FiscalReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = raw with { VerifiedReceiptId = "", VerifiedAccountContext = "" };
                var code = reference.Kind == FiscalDocumentKeyKind.FiscalCode ? reference.Value :
                    reference.Kind == FiscalDocumentKeyKind.CheckboxReceiptUrl && CheckboxReceiptReference.TryParseUrl(reference.Value, out var parsed) ? parsed.FiscalCode : "";
                // The matcher checks every UUID, fiscal code and cashier assertion in
                // this document together. Resolving its code again adds no evidence;
                // keep the code unchanged so disagreements still become conflicts.
                if (reference.Order == order.Key && reference.Provider == "Checkbox" && code.Length > 0 &&
                    !uuidDocuments.Contains(reference.DocumentId) &&
                    (reference.OrganizationId.Length > 0 || reference.CashRegisterFiscalNumber.Length > 0 ||
                     IsAuthoritativeRozetkaCode(order, reference)))
                {
                    try
                    {
                        if (!lookups.TryGetValue(code, out var receipts))
                        {
                            if (lookupAttempts >= 100) { status = "Ліміт перевірки фіскальних ключів; перевірка неповна."; references.Add(reference); continue; }
                            lookupAttempts++;
                            // Failed reads count toward the limit and are not retried per duplicate reference.
                            lookups[code] = [];
                            receipts = await _lookupCache.GetAsync(accountContext, code, async token =>
                            {
                                var json = await api.GetStringAsync($"{config.ApiBaseUrl.TrimEnd('/')}/api/v1/receipts/search?fiscal_code={Uri.EscapeDataString(code)}&self_receipts=true&limit=2&offset=0",
                                    "receipt.fiscal.verify", token).ConfigureAwait(false);
                                return ReceiptParser.ParsePage(json);
                            }, cancellationToken).ConfigureAwait(false);
                            lookups[code] = receipts;
                        }
                        // Two results may mean further pages: do not manufacture uniqueness by filtering them.
                        // A bare assertion cannot bypass another trusted assertion's
                        // seller/cashier context for the same fiscal document.
                        if (receipts.Count == 1 && receipts[0].FiscalCode == code &&
                            CheckboxReceiptReference.ContextMatches(reference, receipts[0]) &&
                            order.FiscalReferences.Where(assertion => assertion.Order == order.Key && assertion.Source.Length > 0 &&
                                    assertion.DocumentId == reference.DocumentId &&
                                    string.Equals(assertion.Provider, "Checkbox", StringComparison.OrdinalIgnoreCase))
                                .All(assertion => CheckboxReceiptReference.ContextMatches(assertion, receipts[0])) &&
                            Guid.TryParse(receipts[0].Id, out var receiptId) && receiptId != Guid.Empty)
                            reference = reference with { VerifiedReceiptId = receipts[0].Id, VerifiedAccountContext = accountContext };
                        else status = "Фіскальний ключ не підтверджено однозначно в поточних правах Checkbox; перевірте касу/продавця.";
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception) { status = "Перевірка фіскального ключа Checkbox недоступна. Це не означає, що чека немає."; }
                }
                references.Add(reference);
            }
            result.Add(order with { FiscalReferences = references, FiscalDataStatus = status });
        }
        var current = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (PrintAccountContext.Create(current) == accountContext) return result;
        await _lookupCache.InvalidateAccountAsync(accountContext).ConfigureAwait(false);
        return WithoutVerification(orders, "Касира змінено під час перевірки. Оновіть чеки та замовлення.");
    }

    private static bool IsAuthoritativeRozetkaCode(MarketplaceOrder order, FiscalDocumentReference reference) =>
        // Seller PRRO supplies a fiscal code but can omit both seller/cashier identifiers
        // and the receipt URL. It only authorizes a lookup: authenticated self_receipts,
        // one exact result and the loaded scope still have to establish the UUID.
        order.Key.Marketplace == MarketplaceKind.Rozetka && reference.Kind == FiscalDocumentKeyKind.FiscalCode &&
        reference.Source == "prro.prro_receipt_fiscal_code" && reference.DocumentId == "rozetka.prro" &&
        reference.Value.Length == 11 && reference.Value.All(character =>
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-');

    private static bool HasReceiptUuid(FiscalDocumentReference reference) => reference.Kind switch
    {
        FiscalDocumentKeyKind.CheckboxReceiptUuid => Guid.TryParseExact(reference.Value, "D", out var id) && id != Guid.Empty,
        FiscalDocumentKeyKind.CheckboxReceiptUrl => CheckboxReceiptReference.TryParseUrl(reference.Value, out var parsed) && parsed.ReceiptId.Length > 0,
        _ => false
    };

    private static IReadOnlyList<MarketplaceOrder> WithoutVerification(IReadOnlyList<MarketplaceOrder> orders, string status) =>
        orders.Select(order => order with
        {
            FiscalReferences = order.FiscalReferences.Select(reference => reference with
                { VerifiedReceiptId = "", VerifiedAccountContext = "" }).ToArray(),
            FiscalDataStatus = status
        }).ToArray();
}

using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IFiscalReferenceVerifier
{
    Task<IReadOnlyList<MarketplaceOrder>> VerifyAsync(IReadOnlyList<MarketplaceOrder> orders, string accountContext,
        CancellationToken cancellationToken = default);
}

/// <summary>Only exact fiscal-code searches, with explicit seller context; never broadens cashier rights.</summary>
public sealed class FiscalReferenceVerifier(CheckboxApiClient api, ISettingsService settings) : IFiscalReferenceVerifier
{
    public async Task<IReadOnlyList<MarketplaceOrder>> VerifyAsync(IReadOnlyList<MarketplaceOrder> orders, string accountContext,
        CancellationToken cancellationToken = default)
    {
        var config = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (PrintAccountContext.Create(config) != accountContext || config.ApiBaseUrl.TrimEnd('/') != "https://api.checkbox.ua")
            return WithoutVerification(orders, "Поточний обліковий контекст Checkbox не підтверджено. Оновіть чеки та замовлення.");
        var lookups = new Dictionary<string, IReadOnlyList<ReceiptRecord>>(StringComparer.Ordinal);
        var lookupAttempts = 0;
        var result = new List<MarketplaceOrder>();
        foreach (var order in orders)
        {
            var references = new List<FiscalDocumentReference>();
            var status = order.FiscalDataStatus;
            foreach (var raw in order.FiscalReferences)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = raw with { VerifiedReceiptId = "", VerifiedAccountContext = "" };
                var code = reference.Kind == FiscalDocumentKeyKind.FiscalCode ? reference.Value :
                    reference.Kind == FiscalDocumentKeyKind.CheckboxReceiptUrl && CheckboxReceiptReference.TryParseUrl(reference.Value, out var parsed) ? parsed.FiscalCode : "";
                if (reference.Order == order.Key && reference.Provider == "Checkbox" && code.Length > 0 &&
                    (reference.OrganizationId.Length > 0 || reference.CashRegisterFiscalNumber.Length > 0))
                {
                    try
                    {
                        if (!lookups.TryGetValue(code, out var receipts))
                        {
                            if (lookupAttempts >= 100) { status = "Ліміт перевірки фіскальних ключів; перевірка неповна."; references.Add(reference); continue; }
                            lookupAttempts++;
                            // Failed reads count toward the limit and are not retried per duplicate reference.
                            lookups[code] = [];
                            var json = await api.GetStringAsync($"{config.ApiBaseUrl.TrimEnd('/')}/api/v1/receipts/search?fiscal_code={Uri.EscapeDataString(code)}&self_receipts=true&limit=2&offset=0",
                                "receipt.fiscal.verify", cancellationToken).ConfigureAwait(false);
                            receipts = ReceiptParser.ParsePage(json);
                            lookups[code] = receipts;
                        }
                        // Two results may mean further pages: do not manufacture uniqueness by filtering them.
                        if (receipts.Count == 1 && receipts[0].FiscalCode == code &&
                            CheckboxReceiptReference.ContextMatches(reference, receipts[0]) && Guid.TryParse(receipts[0].Id, out _))
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
        return PrintAccountContext.Create(current) == accountContext ? result :
            WithoutVerification(orders, "Касира змінено під час перевірки. Оновіть чеки та замовлення.");
    }

    private static IReadOnlyList<MarketplaceOrder> WithoutVerification(IReadOnlyList<MarketplaceOrder> orders, string status) =>
        orders.Select(order => order with
        {
            FiscalReferences = order.FiscalReferences.Select(reference => reference with
                { VerifiedReceiptId = "", VerifiedAccountContext = "" }).ToArray(),
            FiscalDataStatus = status
        }).ToArray();
}

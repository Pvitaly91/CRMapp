using System.Net;
using System.Net.Http;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class FiscalLinkTests
{
    internal static async Task<MarketplaceOrder> FetchUnavailableRozetkaPrroAsync(string code = Code)
    {
        var fiscalReads = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            Equal("api-seller.rozetka.com.ua", request.RequestUri!.Host);
            Equal("fake-session", request.Headers.Authorization?.Parameter);
            if (request.RequestUri.AbsolutePath == "/orders/100") return Json(new
            {
                success = true,
                content = new
                {
                    id = 100, created = "2026-09-24 10:00:00", cost_with_discount = "380.00",
                    purchases = new[] { new { item_name = "Synthetic battery switch 12/24V 100A", quantity = 2, price = 190, cost = 380 } },
                    user = new { contact_fio = "Synthetic" },
                    prro = new { prro_receipt_status = 1, prro_receipt_service_name = "checkbox", prro_receipt_fiscal_code = code }
                }
            });
            Equal("/prro/receipt/100", request.RequestUri.AbsolutePath);
            Equal("?type=link", request.RequestUri.Query);
            fiscalReads++;
            return Json(new { success = false, errors = new { code = 15005 } });
        }));
        var result = (await new RozetkaOrdersClient(new MarketplaceHttpTransport(http))
            .GetOrderAsync(Connection, Credentials, "100"))!;
        Equal(1, fiscalReads);
        Equal(0, result.FiscalReceiptUrls.Count);
        Equal("", result.Currency);
        True(result.ItemsComplete);
        True(result.FiscalDataStatus.Contains("15005"));
        var reference = result.FiscalReferences.Single();
        Equal("prro.prro_receipt_fiscal_code", reference.Source);
        Equal("rozetka.prro", reference.DocumentId);
        Equal("", reference.CashRegisterFiscalNumber);
        Equal("", reference.OrganizationId);
        return result;
    }

    private static async Task BareRozetkaPrroLookupAsync()
    {
        var order = await FetchUnavailableRozetkaPrroAsync();
        var receipt = new ReceiptRecord { Id = ReceiptId, FiscalCode = Code, Serial = 4940, Type = ReceiptTypes.Sell,
            Status = "DONE", TotalSumMinor = 38000, FiscalDate = new(2026, 9, 24, 12, 0, 0, TimeSpan.FromHours(3)) };
        var settings = new Settings();
        var account = PrintAccountContext.Create(await settings.LoadAsync());
        var details = new Dictionary<string, ReceiptDetails>
        {
            [receipt.Id] = new(receipt.Id, [new("Synthetic battery switch 12/24V 100A", "", 2, 190, 380)])
        };
        var matcher = new ReceiptOrderMatchingService();
        var before = matcher.MatchAll([receipt], account, [order], [], true, details);
        Equal(ReceiptLinkState.Incomplete, before[receipt.Id].State);
        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Equal("api.checkbox.ua", request.RequestUri!.Host);
            Equal("/api/v1/receipts/search", request.RequestUri.AbsolutePath);
            Equal($"?fiscal_code={Code}&self_receipts=true&limit=2&offset=0", request.RequestUri.Query);
            Equal("fake-checkbox", request.Headers.Authorization?.Parameter);
            return ReceiptPage(receipt);
        }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
        var verified = (await verifier.VerifyAsync([order], account)).Single();
        Equal(1, calls);
        Equal(receipt.Id, verified.FiscalReferences.Single().VerifiedReceiptId);
        Equal(account, verified.FiscalReferences.Single().VerifiedAccountContext);
        Equal("", verified.FiscalReferences.Single().OrganizationId);
        Equal("", verified.FiscalReferences.Single().CashRegisterFiscalNumber);
        var matches = matcher.MatchAll([receipt], account, [verified], [], true, details);
        Equal(ReceiptLinkState.Exact, matches[receipt.Id].State);
        Equal(order.Key, matches[receipt.Id].Order!.Key);
        Equal("За UUID чека (підтверджено через фіскальний номер)", matches[receipt.Id].Explanation);
    }

    private static async Task BareRozetkaPrroProvenanceAsync()
    {
        var source = await FetchUnavailableRozetkaPrroAsync();
        var reference = source.FiscalReferences.Single();
        var settings = new Settings();
        var account = PrintAccountContext.Create(await settings.LoadAsync());
        var variants = new[]
        {
            reference with { Source = "untrusted comment" },
            reference with { Source = "" },
            reference with { Provider = "unknown" },
            reference with { Provider = "" },
            reference with { DocumentId = "untrusted document" },
            reference with { DocumentId = "" },
            reference with { Order = source.Key with { OrderId = "other-order" } },
            reference with { Kind = FiscalDocumentKeyKind.CheckboxReceiptUrl, Value = $"https://api.checkbox.ua/api/v1/receipts/{Code}/html" },
            reference with { Value = "" },
            reference with { Value = "1234567890" },
            reference with { Value = "123456789012" },
            reference with { Value = "1234567890Ж" },
            reference with { Value = "1234567890/" },
            reference with { Value = "1234567890\n" }
        };
        var calls = 0;
        using var http = new HttpClient(new Handler(_ => { calls++; return ReceiptPage(Receipt()); }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
        foreach (var variant in variants)
        {
            var verified = (await verifier.VerifyAsync([source with { FiscalReferences = [variant] }], account)).Single();
            True(verified.FiscalReferences.All(item => item.VerifiedReceiptId.Length == 0));
            True(Match(verified, account: account).State != ReceiptLinkState.Exact);
        }
        var promKey = source.Key with { Marketplace = MarketplaceKind.Prom };
        var prom = source with { Key = promKey, FiscalReferences = [reference with { Order = promKey }] };
        var rejected = (await verifier.VerifyAsync([prom], account)).Single();
        True(rejected.FiscalReferences.All(item => item.VerifiedReceiptId.Length == 0));
        Equal(0, calls);
    }

    private static async Task BareRozetkaPrroGuardsAsync()
    {
        var source = await FetchUnavailableRozetkaPrroAsync();
        foreach (var mode in new[] { "empty", "denied", "duplicate", "wrong-code", "invalid-uuid", "empty-uuid", "wrong-cash", "wrong-organization", "changed-account" })
        {
            var settings = new Settings();
            var account = PrintAccountContext.Create(await settings.LoadAsync());
            var reference = source.FiscalReferences.Single();
            if (mode == "wrong-cash") reference = reference with { CashRegisterFiscalNumber = "another-cash" };
            if (mode == "wrong-organization") reference = reference with { OrganizationId = OtherId };
            using var http = new HttpClient(new Handler(_ =>
            {
                if (mode == "changed-account") settings.Login = "another-cashier";
                return mode switch
                {
                    "empty" => ReceiptPage(),
                    "denied" => new(HttpStatusCode.Forbidden),
                    "duplicate" => ReceiptPage(Receipt(), Receipt(OtherId)),
                    "wrong-code" => ReceiptPage(Receipt(code: Code.ToLowerInvariant())),
                    "invalid-uuid" => ReceiptPage(Receipt("not-a-uuid")),
                    "empty-uuid" => ReceiptPage(Receipt(Guid.Empty.ToString("D"))),
                    _ => ReceiptPage(Receipt())
                };
            }));
            var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
            var verified = (await verifier.VerifyAsync([source with { FiscalReferences = [reference] }], account)).Single();
            True(verified.FiscalReferences.All(item => item.VerifiedReceiptId.Length == 0));
            True(Match(verified, account: account).State != ReceiptLinkState.Exact);
        }
        var validSettings = new Settings();
        var validAccount = PrintAccountContext.Create(await validSettings.LoadAsync());
        using var validHttp = new HttpClient(new Handler(_ => ReceiptPage(Receipt())));
        var validVerifier = new FiscalReferenceVerifier(new CheckboxApiClient(validHttp, new Auth(), new Logger()), validSettings);
        var valid = (await validVerifier.VerifyAsync([source], validAccount)).Single();
        Equal(ReceiptLinkState.Exact, Match(valid, account: validAccount).State);
        Equal(ReceiptLinkState.Conflict, Match(valid, scope: [Receipt(), Receipt(OtherId)], account: validAccount).State);
        True(Match(valid, account: "different-account").State != ReceiptLinkState.Exact);
        True(Match(valid, decisions: [new() { AccountContext = validAccount, ReceiptId = ReceiptId, SuppressAutomatic = true }], account: validAccount).State != ReceiptLinkState.Exact);
        True(Match(valid, decisions: [new() { AccountContext = validAccount, ReceiptId = ReceiptId, RejectedOrders = [valid.Key] }], account: validAccount).State != ReceiptLinkState.Exact);
        var contradiction = valid with { FiscalReferences = valid.FiscalReferences.Append(valid.FiscalReferences.Single() with { Value = "OTHER-CODE01" }).ToArray() };
        Equal(ReceiptLinkState.Conflict, Match(contradiction, account: validAccount).State);
        var competingKey = valid.Key with { OrderId = "another-order" };
        var competing = valid with { Key = competingKey, Number = competingKey.OrderId,
            FiscalReferences = valid.FiscalReferences.Select(reference => reference with { Order = competingKey }).ToArray() };
        var ambiguous = new ReceiptOrderMatchingService().Match(Receipt(), validAccount, [valid, competing], [], true, receiptScope: [Receipt()]);
        Equal(ReceiptLinkState.Conflict, ambiguous.State);
        var incomplete = new ReceiptOrderMatchingService().Match(Receipt(), validAccount, [valid], [], false, receiptScope: [Receipt()]);
        Equal(ReceiptLinkState.Incomplete, incomplete.State);
    }
}

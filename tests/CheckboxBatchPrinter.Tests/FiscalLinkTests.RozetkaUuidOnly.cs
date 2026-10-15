using System.Net.Http;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class FiscalLinkTests
{
    private static async Task RozetkaCodeRequiresUuidAsync()
    {
        var original = await Fetch(url: "https://unknown.example/receipt");
        var reference = original.FiscalReferences.Single() with { CashRegisterFiscalNumber = "000123" };
        var order = original with { FiscalReferences = [reference] };
        var settings = new Settings();
        var account = PrintAccountContext.Create(await settings.LoadAsync());
        var unresolved = Match(order, account: account);
        Equal(ReceiptLinkState.Incomplete, unresolved.State);
        True(unresolved.Order is null);

        // Prom's existing contextual-code contract is independent of Rozetka's UUID-only rule.
        var promKey = order.Key with { Marketplace = MarketplaceKind.Prom };
        var prom = order with { Key = promKey, FiscalReferences = [reference with { Order = promKey }] };
        Equal(ReceiptLinkState.Exact, Match(prom, account: account).State);
        Equal("За фіскальним номером", Match(prom, account: account).Explanation);

        var calls = 0;
        using var http = new HttpClient(new Handler(request =>
        {
            calls++;
            Equal("/api/v1/receipts/search", request.RequestUri!.AbsolutePath);
            Equal($"?fiscal_code={Code}&self_receipts=true&limit=2&offset=0", request.RequestUri.Query);
            Equal("fake-checkbox", request.Headers.Authorization?.Parameter);
            return ReceiptPage(Receipt());
        }));
        var verifier = new FiscalReferenceVerifier(new CheckboxApiClient(http, new Auth(), new Logger()), settings);
        var verified = (await verifier.VerifyAsync([order], account)).Single();
        Equal(1, calls);
        Equal(ReceiptId, verified.FiscalReferences.Single().VerifiedReceiptId);
        Equal(ReceiptLinkState.Exact, Match(verified, account: account).State);
        Equal("За UUID чека (підтверджено через фіскальний номер)", Match(verified, account: account).Explanation);

        // Disk cache may keep the code and cashier assertion, never authenticated UUID proof.
        var persisted = JsonSerializer.Deserialize<MarketplaceOrder>(JsonSerializer.Serialize(verified))!;
        True(Match(persisted, account: account).State != ReceiptLinkState.Exact);
    }

    private static async Task RozetkaUuidIdentityGuardsAsync()
    {
        var source = await Fetch(url: "https://unknown.example/receipt");
        var reference = source.FiscalReferences.Single() with { CashRegisterFiscalNumber = "000123" };
        var order = source with { FiscalReferences = [reference], Total = 439m };
        var receipt = Receipt(total: 39000);
        var direct = order with { ReceiptIds = [ReceiptId] };
        Equal(ReceiptLinkState.Exact, Match(direct, receipt).State);
        var typed = order with { FiscalReferences = [reference,
            new(FiscalDocumentKeyKind.CheckboxReceiptUuid, ReceiptId, "document.receipt_id", order.Key,
                "Checkbox", reference.DocumentId)] };
        Equal(ReceiptLinkState.Exact, Match(typed, receipt).State);
        var url = await Fetch();
        Equal(ReceiptLinkState.Exact, Match(url with { Total = 439m }, receipt).State);

        var proof = reference with { VerifiedReceiptId = ReceiptId, VerifiedAccountContext = "account" };
        var verified = order with { FiscalReferences = [proof] };
        Equal(ReceiptLinkState.Exact, Match(verified, receipt).State);
        foreach (var invalid in new[]
        {
            proof with { VerifiedReceiptId = "" },
            proof with { VerifiedReceiptId = OtherId },
            proof with { VerifiedReceiptId = Guid.Empty.ToString("D") },
            proof with { VerifiedReceiptId = "4961" },
            proof with { VerifiedAccountContext = "other-account" }
        }) True(Match(order with { FiscalReferences = [invalid] }, receipt).State != ReceiptLinkState.Exact);
        foreach (var invalidId in new[] { "4961", Guid.Empty.ToString("D") })
            True(Match(order with { ReceiptIds = [invalidId] }, Receipt(id: invalidId, total: 39000)).State != ReceiptLinkState.Exact);

        Equal(ReceiptLinkState.Conflict, Match(verified, Receipt(cash: "other-cash", total: 39000)).State);
        Equal(ReceiptLinkState.Conflict, Match(verified, receipt, scope: [receipt, Receipt(OtherId)]).State);
        Equal(ReceiptLinkState.Conflict, Match(order with { FiscalReferences = [proof, reference with { Value = "other-code" }] }, receipt).State);
        var manual = new ReceiptOrderDecision { AccountContext = "account", ReceiptId = receipt.Id, ConfirmedOrder = order.Key };
        Equal(ReceiptLinkState.Manual, Match(order, receipt, [manual]).State);
    }
}

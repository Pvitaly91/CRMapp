using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class WaybillOrderAssociationTests
{
    private const string Ttn = "20400000000001", OtherTtn = "20400000000002";
    private const string Reference = "11111111-2222-3333-4444-555555555555";
    private static readonly DateTimeOffset Created = new(2026, 10, 9, 10, 0, 0, TimeSpan.FromHours(3));
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("waybill orders use exact carrier shipment identity without receipt links or commercial guesses", ExactShipment),
        ("waybill orders respect bound NP accounts and aliases sharing the same key", AccountBindings),
        ("waybill orders check NP Ref consistency and RD shipment IDs independently of partner TTN", DocumentIdentity),
        ("waybill orders preserve every actual order across cancellation and unrelated display dates", MultipleOrders),
        ("waybill orders reject empty identity and unknown or different documented carriers", CarrierIdentity)
    ];

    private static MarketplaceOrder Order(string id, string store = "shop", string carrier = "nova_poshta", string number = Ttn,
        string document = "", MarketplaceKind marketplace = MarketplaceKind.Prom) => new()
    {
        Key = new(marketplace, store, id), Number = id, CreatedAt = Created,
        Shipments = [new(carrier, number) { ShipmentId = document }], Total = 100, Currency = "UAH",
        Buyer = new("Synthetic buyer"), Recipient = new("Synthetic recipient"), Status = "new"
    };
    private static WaybillRecord Np(string account = "np", string document = Reference) =>
        new(ShippingCarrier.NovaPoshta, account, "Synthetic NP", document, Ttn, Created.DateTime,
            "Created", "Synthetic recipient", "Kyiv", 100, 1);
    private static WaybillRecord Rd(string id = "RD-12345") =>
        new(ShippingCarrier.RozetkaDelivery, "rd", "Synthetic RD", id, id, Created.DateTime,
            "Created", "Synthetic recipient", "Kyiv", 100, 1);
    private static ShippingLabelSettings Config(params NovaPoshtaStoreBinding[] bindings) => new()
    {
        NovaPoshtaConnections = [new("np", "Same name", "synthetic_key"),
            new("alias", "Different name", "synthetic_key"), new("other", "Same name", "different_synthetic_key")],
        NovaPoshtaStoreBindings = bindings
    };

    private static Task ExactShipment()
    {
        var actual = Order("actual");
        var sameCommercialData = Order("same-price-and-buyer", number: OtherTtn);
        var noShipment = Order("no-shipment") with { Shipments = [] };
        var records = new[] { sameCommercialData, noShipment, actual };
        var originalShipment = actual.Shipments[0];
        var result = WaybillOrderAssociations.Find(Np(), records, Config());
        Check(result.Count == 1 && ReferenceEquals(result[0], actual), "Exact unlinked order was lost or commercial fields inferred a link.");
        Check(actual.ReceiptIds.Count == 0 && ReferenceEquals(actual.Shipments[0], originalShipment) &&
            records.Length == 3, "Informational association changed source orders or created receipt links.");
        return Task.CompletedTask;
    }

    private static Task AccountBindings()
    {
        var direct = Order("direct", "direct-shop");
        var alias = Order("alias", "alias-shop");
        var wrong = Order("wrong", "wrong-shop");
        var unknown = Order("unknown", "unbound-shop");
        var config = Config(new("direct-shop", "np"), new("alias-shop", "alias"), new("wrong-shop", "other"));
        var result = WaybillOrderAssociations.Find(Np(), [wrong, alias, direct, unknown], config);
        Check(result.Select(order => order.Number).SequenceEqual(new[] { "alias", "direct", "unknown" }),
            "Bound NP identity ignored account ownership, key aliases, or an exact unbound shipment.");
        var missing = config with { NovaPoshtaConnections = config.NovaPoshtaConnections.Where(c => c.Id != "alias").ToArray() };
        Check(WaybillOrderAssociations.Find(Np(), [alias], missing).Count == 0, "Missing alias key fabricated account equivalence.");
        var blank = config with { NovaPoshtaConnections = config.NovaPoshtaConnections.Select(c => c with { ApiKey = "" }).ToArray() };
        Check(WaybillOrderAssociations.Find(Np(), [alias], blank).Count == 0, "Two absent keys became a shared account.");
        var ambiguous = config with { NovaPoshtaStoreBindings = [new("direct-shop", "np"), new("direct-shop", "other")] };
        Check(WaybillOrderAssociations.Find(Np(), [direct], ambiguous).Count == 0, "Ambiguous store binding chose an arbitrary account.");
        return Task.CompletedTask;
    }

    private static Task DocumentIdentity()
    {
        var matching = Order("match", document: Reference.ToUpperInvariant());
        var conflicting = Order("conflict", document: "99999999-2222-3333-4444-555555555555");
        var referenceOnly = Order("reference-only", number: OtherTtn, document: Reference);
        Check(WaybillOrderAssociations.Find(Np(), [matching, conflicting, referenceOnly], Config()).SequenceEqual([matching]),
            "NP Ref contradiction was ignored or Ref replaced public TTN.");
        Check(WaybillOrderAssociations.Find(Np(document: "ttn:" + Ttn), [matching], Config()).Count == 1,
            "Typed public-TTN fallback was treated as a contradictory document Ref.");
        var rdByNumber = Order("rd-number", carrier: "rozetka_delivery", number: "RD-12345");
        var rdById = Order("rd-id", carrier: "Rozetka Delivery", number: Ttn, document: "RD-12345");
        var npSameId = Order("np-same-id", document: "RD-12345");
        var otherRd = Order("other-rd", carrier: "rozetka_delivery", number: OtherTtn, document: "RD-99999");
        Check(WaybillOrderAssociations.Find(Rd(), [rdByNumber, rdById, npSameId, otherRd], Config())
            .Select(order => order.Number).SequenceEqual(new[] { "rd-id", "rd-number" }),
            "RD identity lost partner-TTN association or crossed carriers/documents.");
        return Task.CompletedTask;
    }

    private static Task MultipleOrders()
    {
        var oldCanceled = Order("old-canceled", "same-store") with { CreatedAt = Created.AddDays(-40), Status = "canceled" };
        var latest = Order("latest", "same-store") with { CreatedAt = Created.AddMinutes(10) };
        var secondStore = Order("same-number", "other-store", marketplace: MarketplaceKind.Rozetka);
        var firstStore = Order("same-number", "same-store");
        var result = WaybillOrderAssociations.Find(Np(), [oldCanceled, firstStore, latest, secondStore, firstStore], Config());
        Check(result.Count == 4 && result[0] == latest && result[^1] == oldCanceled &&
            result.Count(order => order.Number == "same-number") == 2, "Multiple actual orders, canceled/out-of-view dates, or distinct store keys were collapsed.");
        Check(result.Select(order => order.Key).SequenceEqual(WaybillOrderAssociations.Find(Np(),
            [secondStore, latest, oldCanceled, firstStore], Config()).Select(order => order.Key)), "Informational order sorting depends on input order.");
        return Task.CompletedTask;
    }

    private static Task CarrierIdentity()
    {
        var unknown = Order("unknown", carrier: "delivery service 7");
        var otherCarrier = Order("rd", carrier: "rozetka_delivery");
        var blank = Order("blank", number: "");
        Check(WaybillOrderAssociations.Find(Np(), [unknown, otherCarrier, blank], Config()).Count == 0,
            "Unknown carrier or empty tracking identity became an association.");
        Check(WaybillOrderAssociations.Find(Np() with { Carrier = ShippingCarrier.Unknown }, [Order("actual")], Config()).Count == 0,
            "Unknown waybill carrier guessed an association.");
        var blankRd = Order("blank-rd", carrier: "rozetka_delivery", number: "");
        Check(WaybillOrderAssociations.Find(Rd() with { DocumentId = "", TrackingNumber = "" }, [blankRd], Config()).Count == 0,
            "Empty carrier-document identifiers matched empty order identifiers.");
        return Task.CompletedTask;
    }
}

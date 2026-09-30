using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task PrintedTrackingSingleAsync()
    {
        foreach (var state in new[] { ReceiptLinkState.Exact, ReceiptLinkState.Manual, ReceiptLinkState.Suggested })
        foreach (var carrier in new[] { "nova_poshta", "rozetka_delivery" })
        {
            var fixture = new Fixture();
            fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
            var (main, _) = fixture.Create(); await main.RefreshAsync();
            var row = main.Receipts.Single();
            var order = Order() with { Number = "ORDER-TTN", Shipments = [new(carrier, "SYNTHETIC-TTN")] };
            row.OrderMatch = new(state, order, "synthetic linked order", []);
            row.IsSelected = true;
            fixture.Dialogs.DuringConfirmation = () => row.OrderMatch = new(state,
                order with { Number = "CHANGED", Shipments = [new(carrier, "CHANGED-TTN")] }, "background changed data", []);
            await ExecuteAsync(main.PrintSelectedCommand);
            var expected = carrier == "nova_poshta" ? "НП: SYNTHETIC-TTN" : "Rozetka Delivery: SYNTHETIC-TTN";
            Equal(expected, fixture.Dialogs.Confirmation!.Items.Single().TrackingText);
            Equal(expected, fixture.Printer.Documents.Single().TrackingText);
            Equal("ORDER-TTN", fixture.Printer.Documents.Single().OrderNumber);
            True(!row.IsSelected); Equal(0, fixture.Dialogs.Errors.Count);
        }
    }

    private static async Task PrintedTrackingBatchAsync()
    {
        var fixture = new Fixture();
        var np = Order(receiptIds: [ReceiptOne]) with { Shipments = [new("нова пошта", "NP-FIRST"),
            new("nova_poshta", "NP-FIRST") { ShipmentId = "another-place" },
            new("nova_poshta", "NP-SECOND"), new("unknown", "DO-NOT-PRINT"), new("nova_poshta", "  ")] };
        var rz = Order(receiptIds: [ReceiptTwo]) with { Key = new(MarketplaceKind.Prom, "prom-test", "42"),
            Number = "ORDER-RZ", Shipments = [new("rozetka_delivery", "RZ-FIRST")] };
        fixture.Source.Orders = [np, rz]; // Carrier is independent of marketplace.
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        main.SelectAllCommand.Execute(null);
        fixture.Dialogs.DuringConfirmation = () =>
        {
            foreach (var row in main.Receipts.Where(r => r.OrderMatch?.Order is not null))
                row.OrderMatch = row.OrderMatch! with { Order = row.OrderMatch.Order! with
                    { Shipments = [new("nova_poshta", "CHANGED-TTN")] } };
        };
        await ExecuteAsync(main.PrintSelectedCommand);
        AssertConfirmation(fixture, [ReceiptThree, ReceiptTwo, ReceiptOne]);
        var tracking = new[] { "", "Rozetka Delivery: RZ-FIRST", "НП: NP-FIRST; НП: NP-SECOND" };
        True(fixture.Printer.Documents.Select(d => d.TrackingText).SequenceEqual(tracking));
        True(fixture.Dialogs.Confirmation!.Items.Select(d => d.TrackingText).SequenceEqual(tracking));
        Equal("", ReceiptPrintHeader.TrackingFor(Order() with { Shipments = [] }));
        Equal("", ReceiptPrintHeader.TrackingFor(null));
        Equal("", ReceiptPrintHeader.TrackingFor(Order() with { Shipments = [new("other", "UNKNOWN-TTN")] }));
        Equal(0, main.SelectedCount); Equal(0, fixture.Dialogs.Errors.Count);
    }

    private static async Task PrintedOrderNumbersAsync()
    {
        foreach (var state in new[] { ReceiptLinkState.Exact, ReceiptLinkState.Manual, ReceiptLinkState.Suggested })
        {
            var fixture = new Fixture();
            fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000)];
            var (main, _) = fixture.Create(); await main.RefreshAsync();
            var row = main.Receipts.Single();
            var number = "TEST-" + state;
            row.OrderMatch = new(state, Order() with { Number = number }, "synthetic print link", []);
            row.IsSelected = true;
            fixture.Dialogs.DuringConfirmation = () => row.OrderMatch = new(state, Order() with { Number = "CHANGED" }, "changed during confirmation", []);
            await ExecuteAsync(main.PrintSelectedCommand);
            AssertConfirmation(fixture, [ReceiptOne]);
            Equal(number, fixture.Printer.Documents.Single().OrderNumber);
            True(!row.IsSelected); Equal(0, fixture.Dialogs.Errors.Count);
        }

        var mixed = new Fixture(); mixed.Source.Orders = [Order(receiptIds: [ReceiptOne, ReceiptTwo])];
        var (batchMain, workspace) = mixed.Create();
        await batchMain.RefreshAsync(); await OpenOrdersAsync(batchMain, workspace);
        batchMain.SelectAllCommand.Execute(null);
        await ExecuteAsync(batchMain.PrintSelectedCommand);
        AssertConfirmation(mixed, [ReceiptThree, ReceiptTwo, ReceiptOne]);
        True(mixed.Printer.Documents.Select(d => d.OrderNumber).SequenceEqual(new[] { "", "ORDER-41", "ORDER-41" }));
        Equal(0, batchMain.SelectedCount); Equal(0, mixed.Dialogs.Errors.Count);
    }
}

using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
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

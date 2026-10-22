using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static Task<CombinedFixture> ReceiptSequenceFixtureAsync(int count = 7) => CombinedFixture.CreateAsync(
        orders: Enumerable.Range(1, count).Select(number => CombinedTestOrder(((char)('A' + number - 1)).ToString(),
            (40 + number).ToString(), [$"00000000-0000-0000-0000-{number:D12}"])).ToArray(),
        receipts: Enumerable.Range(1, count).Select(number => CombinedReceipt(
            $"00000000-0000-0000-0000-{number:D12}", number, 10 + number)).ToArray());

    private static string[] SelectSequence(CombinedFixture fixture, string route)
    {
        fixture.MarkOrders();
        if (route == "selected")
        {
            fixture.Main.SelectedTabIndex = 0;
            fixture.Main.SelectAllCommand.Execute(null);
            return fixture.Main.ReceiptsView.Cast<ReceiptRowViewModel>().Select(row => row.Id).ToArray();
        }
        return fixture.Workspace.Orders.Cast<MarketplaceOrderRowViewModel>()
            .SelectMany(order => order.LinkedReceipts).Select(row => row.Id).ToArray();
    }

    private static Task PrintSequence(CombinedFixture fixture, string route) => route switch
    {
        "combined" => fixture.Main.PrintOrdersWithReceiptsAsync(),
        "orders" => fixture.Main.PrintOrderReceiptsAsync(),
        _ => ExecuteAsync(fixture.Main.PrintSelectedCommand)
    };

    private static async Task ReceiptSequenceOnePerJobAsync()
    {
        foreach (var route in new[] { "combined", "orders", "selected" })
        foreach (var count in new[] { 7, 8 })
        {
            using var fixture = await ReceiptSequenceFixtureAsync(count);
            var expected = SelectSequence(fixture, route);
            fixture.ReceiptPrinter.OnSubmit = call =>
            {
                var acceptedCount = call - 1;
                True(fixture.Base.History.Records.Keys.SequenceEqual(expected.Take(acceptedCount)),
                    route + " must persist each accepted receipt before the next Windows job.");
            };
            await PrintSequence(fixture, route);

            Equal(count, fixture.ReceiptPrinter.Calls); Equal(0, fixture.ReceiptPrinter.GroupCalls);
            True(fixture.ReceiptPrinter.Jobs.Count == count && fixture.ReceiptPrinter.Jobs.All(job => job.Length == 1));
            True(fixture.ReceiptPrinter.Documents.Select(document => document.ReceiptId).SequenceEqual(expected));
            Equal(count, fixture.Base.History.Saves);
            True(fixture.Base.History.Records.Keys.SequenceEqual(expected));
            True(fixture.Main.Receipts.All(row => row.PrintStatus == PrintItemStatus.Done));
            Equal(route == "combined" ? 1 : 0, fixture.LabelPrinter.Calls);
            Equal(0, fixture.Base.Dialogs.Errors.Count);
            True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting);
            var submissions = fixture.PrintLogger.Entries.Where(entry => entry.Operation == "receipt.job.submit").ToArray();
            Equal(count, submissions.Length);
            for (var index = 0; index < count; index++)
                True(submissions[index].Status.Contains($"group={index + 1};first={index + 1};last={index + 1};count=1", StringComparison.Ordinal));
        }
    }

    private static async Task ReceiptSequencePartialFailureAsync()
    {
        foreach (var route in new[] { "combined", "orders", "selected" })
        {
            using var fixture = await ReceiptSequenceFixtureAsync();
            var expected = SelectSequence(fixture, route);
            // Legacy false must still keep a driver recovery bounded to one receipt.
            fixture.Base.Settings.App.SeparatePrintJobPerReceipt = false;
            fixture.ReceiptPrinter.FailOnCall = 2;
            fixture.ReceiptPrinter.OnSubmit = call =>
            {
                if (call == 2) True(fixture.Base.History.Records.Keys.SequenceEqual(expected.Take(1)),
                    route + " must save the accepted receipt before submitting its successor.");
            };
            await PrintSequence(fixture, route);

            Equal(2, fixture.ReceiptPrinter.Calls);
            Equal(0, fixture.ReceiptPrinter.GroupCalls);
            True(fixture.ReceiptPrinter.Jobs.Select(job => job.Length).SequenceEqual([1, 1]));
            True(fixture.ReceiptPrinter.Documents.Select(document => document.ReceiptId).SequenceEqual(expected.Take(2)));
            Equal(1, fixture.Base.History.Saves);
            True(fixture.Base.History.Records.Keys.SequenceEqual(expected.Take(1)));
            var rows = expected.Select(id => fixture.Main.Receipts.Single(row => row.Id == id)).ToArray();
            Equal(PrintItemStatus.Done, rows[0].PrintStatus);
            True(rows[1].PrintStatus == PrintItemStatus.Error && rows[1].PrintError.Length > 0);
            True(rows.Skip(2).All(row => row.PrintStatus == PrintItemStatus.Waiting));
            Equal(1, fixture.Main.FailedCount);
            True(fixture.Main.StatusText.Contains("1 із 7", StringComparison.Ordinal));
            var failedManifest = fixture.PrintLogger.Entries.Where(entry => entry.Operation == "receipt.job.failed").ToArray();
            True(failedManifest.Select(entry => entry.ReceiptId).SequenceEqual(expected.Skip(1).Take(1)));
            True(failedManifest.All(entry => entry.Status.Contains("group=2;first=2;last=2;count=1", StringComparison.Ordinal)));
            if (route == "selected")
            {
                True(!rows[0].IsSelected && rows.Skip(1).All(row => row.IsSelected));
                True(rows.All(row => !row.IsSelectedForOrders));
                // A manual retry targets only the uncertain receipt; declining the warning
                // must leave the accepted prefix and untouched suffix out of the printer.
                fixture.Base.Dialogs.AcceptPrint = false;
                await ExecuteAsync(fixture.Main.RetryFailedCommand);
                Equal(1, fixture.Base.Dialogs.Confirmation!.Count);
                Equal(1, fixture.Base.Dialogs.Confirmation.PreviouslyPrintedCount);
            }
            else
            {
                True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
                fixture.Base.Dialogs.AcceptPrint = false;
                fixture.Dialogs.Accept = false;
                await PrintSequence(fixture, route);
                Equal(2, route == "combined" ? fixture.Dialogs.Confirmation!.PreviouslyPrintedReceipts
                    : fixture.Base.Dialogs.Confirmation!.PreviouslyPrintedCount);
            }
            Equal(2, fixture.ReceiptPrinter.Calls);
            Equal(route == "combined" ? 1 : 0, fixture.LabelPrinter.Calls);
            Equal(PrintItemStatus.Done, rows[0].PrintStatus);
            True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting);
        }
    }

    private static async Task ReceiptSequencePersistenceFailureAsync()
    {
        foreach (var route in new[] { "combined", "orders", "selected" })
        {
            using var fixture = await ReceiptSequenceFixtureAsync();
            var expected = SelectSequence(fixture, route);
            fixture.Base.History.FailSave = true;
            fixture.PrintLogger.FailWrites = true;
            await PrintSequence(fixture, route);
            Equal(expected.Length, fixture.ReceiptPrinter.Calls);
            Equal(0, fixture.ReceiptPrinter.GroupCalls);
            True(fixture.ReceiptPrinter.Jobs.Count == expected.Length && fixture.ReceiptPrinter.Jobs.All(job => job.Length == 1));
            True(fixture.ReceiptPrinter.Documents.Select(document => document.ReceiptId).SequenceEqual(expected));
            Equal(0, fixture.Base.History.Saves);
            True(fixture.Main.Receipts.All(row => row.PrintStatus == PrintItemStatus.Done && row.PrintError.Contains("історію")));
            Equal(route == "combined" ? 1 : 0, fixture.LabelPrinter.Calls);
            True(fixture.Main.StatusText.Contains("історію") || fixture.Base.Dialogs.Errors.Count > 0);
            // No timer or callback retries any accepted receipt after a persistence failure.
            await DrainDispatcherAsync();
            Equal(expected.Length, fixture.ReceiptPrinter.Calls);
        }
    }

    private static async Task ReceiptSequenceCompletePreflightAsync()
    {
        foreach (var route in new[] { "combined", "orders", "selected" })
        foreach (var failImage in new[] { false, true })
        {
            using var fixture = await ReceiptSequenceFixtureAsync();
            var expected = SelectSequence(fixture, route);
            if (failImage) fixture.Images.FailId = expected[^1];
            else fixture.ReceiptPrinter.FailValidation = true;
            await PrintSequence(fixture, route);
            Equal(0, fixture.ReceiptPrinter.Calls);
            Equal(0, fixture.LabelPrinter.Calls);
            Equal(0, fixture.Base.History.Saves);
            True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
            True(fixture.Main.Receipts.All(row => row.PrintStatus != PrintItemStatus.Printing));
        }
    }
}

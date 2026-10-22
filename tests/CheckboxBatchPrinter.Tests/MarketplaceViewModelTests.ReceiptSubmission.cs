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

    private static async Task ReceiptSequenceThreePerJobAsync()
    {
        foreach (var route in new[] { "combined", "orders", "selected" })
        foreach (var count in new[] { 7, 8 })
        {
            using var fixture = await ReceiptSequenceFixtureAsync(count);
            var expected = SelectSequence(fixture, route);
            fixture.ReceiptPrinter.OnSubmit = call =>
            {
                var acceptedCount = (call - 1) * 3;
                True(fixture.Base.History.Records.Keys.SequenceEqual(expected.Take(acceptedCount)),
                    route + " must persist each complete group before the next Windows job.");
            };
            await PrintSequence(fixture, route);

            Equal(3, fixture.ReceiptPrinter.Calls); Equal(count == 7 ? 2 : 3, fixture.ReceiptPrinter.GroupCalls);
            True(fixture.ReceiptPrinter.Jobs.Select(job => job.Length).SequenceEqual([3, 3, count - 6]));
            True(fixture.ReceiptPrinter.Documents.Select(document => document.ReceiptId).SequenceEqual(expected));
            Equal(3, fixture.Base.History.Saves);
            True(fixture.Base.History.Records.Keys.SequenceEqual(expected));
            True(fixture.Main.Receipts.All(row => row.PrintStatus == PrintItemStatus.Done));
            Equal(route == "combined" ? 1 : 0, fixture.LabelPrinter.Calls);
            Equal(0, fixture.Base.Dialogs.Errors.Count);
            True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting);
            var submissions = fixture.PrintLogger.Entries.Where(entry => entry.Operation == "receipt.job.submit").ToArray();
            Equal(count, submissions.Length);
            True(submissions.Take(3).All(entry => entry.Status.Contains("group=1;first=1;last=3;count=3", StringComparison.Ordinal)));
            True(submissions.Skip(3).Take(3).All(entry => entry.Status.Contains("group=2;first=4;last=6;count=3", StringComparison.Ordinal)));
            True(submissions.Skip(6).All(entry => entry.Status.Contains($"group=3;first=7;last={count};count={count - 6}", StringComparison.Ordinal)));
        }
    }

    private static async Task ReceiptSequencePartialFailureAsync()
    {
        foreach (var route in new[] { "combined", "orders", "selected" })
        {
            using var fixture = await ReceiptSequenceFixtureAsync();
            var expected = SelectSequence(fixture, route);
            // Legacy false must still keep a driver recovery bounded to three receipts.
            fixture.Base.Settings.App.SeparatePrintJobPerReceipt = false;
            fixture.ReceiptPrinter.FailOnCall = 2;
            fixture.ReceiptPrinter.OnSubmit = call =>
            {
                if (call == 2) True(expected.Take(3).All(fixture.Base.History.Records.ContainsKey),
                    route + " must save the complete accepted group before submitting its successor.");
            };
            await PrintSequence(fixture, route);

            Equal(2, fixture.ReceiptPrinter.Calls);
            Equal(2, fixture.ReceiptPrinter.GroupCalls);
            True(fixture.ReceiptPrinter.Jobs.Select(job => job.Length).SequenceEqual([3, 3]));
            True(fixture.ReceiptPrinter.Documents.Select(document => document.ReceiptId).SequenceEqual(expected.Take(6)));
            Equal(1, fixture.Base.History.Saves);
            True(fixture.Base.History.Records.Keys.SequenceEqual(expected.Take(3)));
            var rows = expected.Select(id => fixture.Main.Receipts.Single(row => row.Id == id)).ToArray();
            True(rows.Take(3).All(row => row.PrintStatus == PrintItemStatus.Done));
            True(rows.Skip(3).Take(3).All(row => row.PrintStatus == PrintItemStatus.Error && row.PrintError.Contains("групи")));
            Equal(PrintItemStatus.Waiting, rows[6].PrintStatus);
            Equal(3, fixture.Main.FailedCount);
            True(fixture.Main.StatusText.Contains("3 із 7", StringComparison.Ordinal));
            var failedManifest = fixture.PrintLogger.Entries.Where(entry => entry.Operation == "receipt.job.failed").ToArray();
            True(failedManifest.Select(entry => entry.ReceiptId).SequenceEqual(expected.Skip(3).Take(3)));
            True(failedManifest.All(entry => entry.Status.Contains("group=2;first=4;last=6;count=3", StringComparison.Ordinal)));
            if (route == "selected")
            {
                True(rows.Take(3).All(row => !row.IsSelected) && rows.Skip(3).All(row => row.IsSelected));
                True(rows.All(row => !row.IsSelectedForOrders));
                // A manual retry targets only the uncertain group; declining the warning
                // must leave the accepted prefix and untouched suffix out of the printer.
                fixture.Base.Dialogs.AcceptPrint = false;
                await ExecuteAsync(fixture.Main.RetryFailedCommand);
                Equal(3, fixture.Base.Dialogs.Confirmation!.Count);
                Equal(3, fixture.Base.Dialogs.Confirmation.PreviouslyPrintedCount);
            }
            else
            {
                True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
                fixture.Base.Dialogs.AcceptPrint = false;
                fixture.Dialogs.Accept = false;
                await PrintSequence(fixture, route);
                Equal(6, route == "combined" ? fixture.Dialogs.Confirmation!.PreviouslyPrintedReceipts
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
            Equal(3, fixture.ReceiptPrinter.Calls);
            Equal(2, fixture.ReceiptPrinter.GroupCalls);
            True(fixture.ReceiptPrinter.Jobs.Select(job => job.Length).SequenceEqual([3, 3, 1]));
            True(fixture.ReceiptPrinter.Documents.Select(document => document.ReceiptId).SequenceEqual(expected));
            Equal(0, fixture.Base.History.Saves);
            True(fixture.Main.Receipts.All(row => row.PrintStatus == PrintItemStatus.Done && row.PrintError.Contains("історію")));
            Equal(route == "combined" ? 1 : 0, fixture.LabelPrinter.Calls);
            True(fixture.Main.StatusText.Contains("історію") || fixture.Base.Dialogs.Errors.Count > 0);
            // No timer or callback retries any accepted receipt after a persistence failure.
            await DrainDispatcherAsync();
            Equal(3, fixture.ReceiptPrinter.Calls);
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

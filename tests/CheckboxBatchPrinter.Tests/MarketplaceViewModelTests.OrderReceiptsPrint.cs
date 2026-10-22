using System.ComponentModel;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> OrderReceiptsPrintScenarios =>
    [
        ("STA order receipt printing groups every linked receipt by sorted orders without label requirements", () => StaAsync(OrderReceiptsPrintSortedAsync)),
        ("STA order receipt printing freezes visible order selection independently of both receipt filters", () => StaAsync(OrderReceiptsPrintFrozenAsync)),
        ("STA declining order receipt confirmation preserves both receipt selections and printed-state disclosure", () => StaAsync(OrderReceiptsPrintDeclinedAsync)),
        ("STA order receipt preflight rejects missing canceled unfinished and unavailable packets atomically", () => StaAsync(OrderReceiptsPrintPreparationFailuresAsync)),
        ("STA order receipt printing rejects one receipt allocated to multiple selected orders", () => StaAsync(OrderReceiptsPrintDuplicateAsync)),
        ("STA order receipt printing revalidates account links receipt content readiness and printer after confirmation", () => StaAsync(OrderReceiptsPrintStaleAsync)),
        ("STA canceling order receipt image preparation submits nothing and releases reservations", () => StaAsync(OrderReceiptsPrintCanceledAsync)),
        ("STA order receipt printing reserves all workflows through preparation and printer completion", () => StaAsync(OrderReceiptsPrintBusyAsync)),
        ("STA failed order receipt transfer retains marks and discloses possible repeats across print workflows", () => StaAsync(OrderReceiptsPrintTransferFailureAsync)),
        ("STA accepted order receipt jobs retain marks and report history failure before a disclosed repeat", () => StaAsync(OrderReceiptsPrintHistoryFailureAsync))
    ];

    private static async Task OrderReceiptsPrintSortedAsync()
    {
        const string later = "00000000-0000-0000-0000-000000000004";
        using var fixture = await CombinedFixture.CreateAsync(
            orders: [CombinedTestOrder("A", "41", [ReceiptOne, ReceiptTwo]) with { Shipments = [] },
                CombinedTestOrder("B", "42", [ReceiptThree, later]) with { Shipments = [] }],
            receipts: [CombinedReceipt(later, 10, 15), CombinedReceipt(ReceiptTwo, 7, 14),
                CombinedReceipt(ReceiptThree, 9, 15), CombinedReceipt(ReceiptOne, 8, 13)]);
        fixture.Workspace.Orders.SortDescriptions.Clear();
        fixture.Workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Descending));
        fixture.Main.AllReceiptsTab.SearchText = "no-all-receipts";
        fixture.Main.OrdersReceiptsTab.SearchText = "no-order-receipts";
        fixture.Main.OrdersReceiptsTab.SelectedType = fixture.Main.ReceiptTypes.Single(type => type.Value == ReceiptTypes.Return);
        foreach (var receipt in fixture.Main.Receipts)
        { receipt.IsSelected = receipt.Id == ReceiptOne; receipt.IsSelectedForOrders = receipt.Id == ReceiptThree; }
        var marks = OrderReceiptMarks(fixture);
        fixture.LabelSettings.Config = fixture.LabelSettings.Config with
        { RozetkaDeliveryToken = "", Print = fixture.LabelSettings.Config.Print with { PrinterName = "" } };
        fixture.MarkOrders();
        var labelState = OrderLabelState(fixture);
        True(fixture.Main.PrintOrderReceiptsCommand.CanExecute(null));

        await fixture.Main.PrintOrderReceiptsAsync();

        var expected = new[] { ReceiptThree, later, ReceiptOne, ReceiptTwo };
        Equal(4, fixture.ReceiptPrinter.Calls);
        True(fixture.ReceiptPrinter.Documents.Select(document => document.ReceiptId).SequenceEqual(expected));
        True(fixture.ReceiptPrinter.Documents.Select(document => document.OrderNumber).SequenceEqual(["ORDER-B", "ORDER-B", "ORDER-A", "ORDER-A"]));
        True(fixture.ReceiptPrinter.Documents.All(document => document.TrackingText.Length == 0), "A receipt-only job may not require a shipment.");
        var confirmation = fixture.Base.Dialogs.Confirmation!;
        Equal("mock-printer", confirmation.PrinterName); Equal(4, confirmation.Count);
        Equal(0, confirmation.PreviouslyPrintedCount); Equal(0, confirmation.HiddenSelectedCount);
        True(confirmation.Items.Select(item => item.ReceiptId).SequenceEqual(expected));
        True(confirmation.Items.Select(item => item.Position).SequenceEqual([1, 2, 3, 4]));
        True(confirmation.Items.All(item => item.Marketplace == "Prom"));
        Equal(4, fixture.Base.History.Saves); Equal(4, fixture.Base.History.Records.Count);
        True(fixture.Main.Receipts.All(receipt => receipt.PrintStatus == PrintItemStatus.Done));
        True(marks.SequenceEqual(OrderReceiptMarks(fixture)), "Receipt-only order printing may not consume either receipt grid's marks.");
        True(labelState.SequenceEqual(OrderLabelState(fixture)), "Receipt-only printing must preserve selected orders and every label status.");
        OrderReceiptsAssertNoLabels(fixture); Equal(0, fixture.Base.Dialogs.Errors.Count);
    }

    private static async Task OrderReceiptsPrintFrozenAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.MarkOrders();
        fixture.Workspace.OrderSearch = "none-visible";
        True(!fixture.Main.PrintOrderReceiptsCommand.CanExecute(null));
        await fixture.Main.PrintOrderReceiptsAsync();
        Equal(0, fixture.ReceiptPrinter.Calls);
        fixture.Workspace.OrderSearch = "ORDER-A";
        fixture.Images.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Base.Dialogs.DuringConfirmation = () =>
        {
            fixture.Workspace.OrderSearch = "ORDER-C";
            fixture.Workspace.Orders.SortDescriptions.Clear();
            fixture.Workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Descending));
            fixture.Main.SelectedTabIndex = 0;
            fixture.Main.SearchText = "no-receipts";
            fixture.Base.Settings.App.PrinterName = "changed-after-preparation";
        };
        var printing = fixture.Main.PrintOrderReceiptsAsync();
        try
        {
            await fixture.Images.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Workspace.OrderSearch = "ORDER-B";
            fixture.Main.OrdersReceiptsTab.SearchText = "hidden-receipts";
        }
        finally { fixture.Images.Gate.TrySetResult(true); }
        await printing;

        Equal(1, fixture.ReceiptPrinter.Calls); Equal("mock-printer", fixture.ReceiptPrinter.PrinterName);
        True(fixture.ReceiptPrinter.Documents.Select(document => document.ReceiptId).SequenceEqual([ReceiptOne]));
        Equal("ORDER-A", fixture.ReceiptPrinter.Documents.Single().OrderNumber);
        Equal("НП: TTN-A", fixture.ReceiptPrinter.Documents.Single().TrackingText);
        Equal(2, fixture.Base.Dialogs.Confirmation!.HiddenSelectedCount);
        True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
        Equal(1, fixture.Base.History.Records.Count); OrderReceiptsAssertNoLabels(fixture);
    }

    private static async Task OrderReceiptsPrintDeclinedAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync(previouslyPrinted: true);
        fixture.MarkOrders();
        fixture.Main.Receipts[0].IsSelected = true;
        fixture.Main.Receipts[1].IsSelectedForOrders = true;
        var marks = OrderReceiptMarks(fixture);
        var statuses = fixture.Main.Receipts.Select(row => (row.Id, row.PrintStatus, row.PrintError)).ToArray();
        fixture.Base.Dialogs.AcceptPrint = false;
        await fixture.Main.PrintOrderReceiptsAsync();

        Equal(1, fixture.Base.Dialogs.Confirmation!.PreviouslyPrintedCount);
        Equal(0, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
        True(marks.SequenceEqual(OrderReceiptMarks(fixture)));
        True(statuses.SequenceEqual(fixture.Main.Receipts.Select(row => (row.Id, row.PrintStatus, row.PrintError))));
        True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
        True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting && !fixture.Workspace.IsWorkspaceRefreshing);
        OrderReceiptsAssertNoLabels(fixture);
    }

    private static async Task OrderReceiptsPrintPreparationFailuresAsync()
    {
        foreach (var failure in new[] { "unlinked", "missing-receipt", "not-ready", "canceled", "png", "invalid-layout", "printer", "cashier" })
        {
            using var fixture = await CombinedFixture.CreateAsync();
            fixture.MarkOrders();
            var first = fixture.Workspace.AllOrderRows.Single(row => row.Number == "ORDER-A");
            switch (failure)
            {
                case "unlinked": first.Update(first.Model, [], []); break;
                case "missing-receipt": fixture.Main.Receipts.Remove(fixture.Main.Receipts.Single(row => row.Id == ReceiptOne)); break;
                case "not-ready": fixture.Main.Receipts.Single(row => row.Id == ReceiptOne).Update(CombinedReceipt(ReceiptOne, 1, 11, "CREATED")); break;
                case "canceled":
                    fixture.Workspace.ShowCanceledOrders = true;
                    first.Update(first.Model with { SourceStatus = "cancelled", Status = "Скасовано" }, fixture.Main.Receipts, []); break;
                case "png": fixture.Images.FailId = ReceiptOne; break;
                case "invalid-layout": fixture.ReceiptPrinter.FailValidation = true; break;
                case "printer": fixture.ReceiptPrinter.PrinterAvailable = false; break;
                case "cashier": fixture.Base.Settings.App.Login = "another-cashier"; break;
            }
            var marks = OrderReceiptMarks(fixture);
            await fixture.Main.PrintOrderReceiptsAsync();

            Equal(0, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
            True(fixture.Base.Dialogs.Errors.Count > 0, failure + " must explain why no receipts were submitted.");
            True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels), failure + " consumed order marks.");
            True(marks.SequenceEqual(OrderReceiptMarks(fixture)), failure + " consumed receipt marks.");
            True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting && !fixture.Workspace.IsWorkspaceRefreshing);
            True(fixture.Main.Receipts.All(row => row.PrintStatus is not (PrintItemStatus.Printing or PrintItemStatus.Downloading)));
            OrderReceiptsAssertNoLabels(fixture);
        }
    }

    private static async Task OrderReceiptsPrintDuplicateAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.MarkOrders();
        var second = fixture.Workspace.AllOrderRows.Single(row => row.Number == "ORDER-B");
        var receipt = fixture.Main.Receipts.Single(row => row.Id == ReceiptOne);
        receipt.OrderMatch = new(ReceiptLinkState.Manual, second.Model, "Synthetic conflicting allocation.", []);
        second.Update(second.Model, fixture.Main.Receipts, []);
        // The first row deliberately retains its earlier graph, as can happen during an interrupted update.
        True(fixture.Workspace.AllOrderRows.Count(row => row.LinkedReceipts.Any(link => link.Id == ReceiptOne)) == 2);
        await fixture.Main.PrintOrderReceiptsAsync();

        Equal(0, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
        True(fixture.Base.Dialogs.Errors.Count > 0);
        True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
        OrderReceiptsAssertNoLabels(fixture);
    }

    private static async Task OrderReceiptsPrintStaleAsync()
    {
        foreach (var change in new[] { "link", "status", "content", "account", "canceled", "printer" })
        {
            using var fixture = await CombinedFixture.CreateAsync();
            fixture.MarkOrders();
            var first = fixture.Workspace.AllOrderRows.Single(row => row.Number == "ORDER-A");
            var receipt = fixture.Main.Receipts.Single(row => row.Id == ReceiptOne);
            fixture.Base.Dialogs.DuringConfirmation = () =>
            {
                switch (change)
                {
                    case "link": first.Update(first.Model, [], []); break;
                    case "status": receipt.Update(new() { Id = receipt.Id, Serial = receipt.Model.Serial, Type = receipt.Model.Type,
                        FiscalDate = receipt.Model.FiscalDate, TotalSumMinor = receipt.Model.TotalSumMinor, Status = "CREATED" }); break;
                    case "content": receipt.Update(Receipt(receipt.Id, (int)receipt.Model.Serial, receipt.Model.TotalSumMinor + 100)); break;
                    case "account": fixture.Base.Settings.App.Login = "changed-during-confirmation"; break;
                    case "canceled": first.Update(first.Model with { SourceStatus = "cancelled", Status = "Скасовано" }, fixture.Main.Receipts, []); break;
                    case "printer": fixture.ReceiptPrinter.PrinterAvailable = false; break;
                }
            };
            await fixture.Main.PrintOrderReceiptsAsync();

            True(fixture.Base.Dialogs.Confirmation is not null, change + " must occur after packet preparation.");
            Equal(0, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
            True(fixture.Base.Dialogs.Errors.Count > 0, change + " must reject the stale confirmation.");
            True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
            True(!fixture.Main.IsBusy && !fixture.Workspace.IsWorkspaceRefreshing);
            OrderReceiptsAssertNoLabels(fixture);
        }
    }

    private static async Task OrderReceiptsPrintCanceledAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync(withWaybills: true);
        fixture.MarkOrders();
        fixture.Images.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var printing = fixture.Main.PrintOrderReceiptsAsync();
        try
        {
            await fixture.Images.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Main.CancelPendingOperations();
        }
        finally { fixture.Images.Gate.TrySetResult(true); }
        await printing;

        Equal(0, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
        True(fixture.Base.Dialogs.Confirmation is null); Equal(0, fixture.Base.Dialogs.Errors.Count);
        True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
        True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting && !fixture.Workspace.IsWorkspaceRefreshing && !fixture.Waybills!.IsWorkspaceRefreshing);
        OrderReceiptsAssertNoLabels(fixture);
    }

    private static async Task OrderReceiptsPrintBusyAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync(withWaybills: true);
        fixture.MarkOrders();
        fixture.Images.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReceiptPrinter.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var printing = fixture.Main.PrintOrderReceiptsAsync();
        try
        {
            await fixture.Images.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            OrderReceiptsAssertBusy(fixture);
            await fixture.Main.PrintOrderReceiptsAsync();
            await fixture.Main.PrintOrdersWithReceiptsAsync();
            await fixture.Waybills!.RunAsync(null, true);
            Equal(0, fixture.ReceiptPrinter.Calls); Equal(0, fixture.WaybillPrinter.Calls);
            fixture.Images.Gate.TrySetResult(true);
            await fixture.ReceiptPrinter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            OrderReceiptsAssertBusy(fixture);
            Equal(0, fixture.Base.History.Saves);
            True(!printing.IsCompleted);
        }
        finally { fixture.Images.Gate.TrySetResult(true); fixture.ReceiptPrinter.Gate.TrySetResult(true); }
        await printing;
        Equal(3, fixture.ReceiptPrinter.Calls); Equal(3, fixture.Base.History.Saves);
        True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting && !fixture.Workspace.IsWorkspaceRefreshing && !fixture.Waybills!.IsWorkspaceRefreshing);
        True(fixture.Main.PrintOrderReceiptsCommand.CanExecute(null)); OrderReceiptsAssertNoLabels(fixture);

        using var waybill = await CombinedFixture.CreateAsync(withWaybills: true);
        waybill.MarkOrders(); waybill.WaybillPrinter.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var waybillPrinting = waybill.Waybills!.RunAsync(null, true);
        try
        {
            await waybill.WaybillPrinter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(!waybill.Main.PrintOrderReceiptsCommand.CanExecute(null));
            await waybill.Main.PrintOrderReceiptsAsync(); Equal(0, waybill.ReceiptPrinter.Calls);
        }
        finally { waybill.WaybillPrinter.Gate.TrySetResult(true); }
        await waybillPrinting;
    }

    private static async Task OrderReceiptsPrintTransferFailureAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.MarkOrders();
        fixture.Main.Receipts[0].IsSelected = true; fixture.Main.Receipts[1].IsSelectedForOrders = true;
        var marks = OrderReceiptMarks(fixture);
        fixture.ReceiptPrinter.Fail = true;
        await fixture.Main.PrintOrderReceiptsAsync();

        Equal(1, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
        Equal(1, fixture.Main.Receipts.Count(row => row.PrintStatus == PrintItemStatus.Error && row.PrintError.Length > 0));
        Equal(2, fixture.Main.Receipts.Count(row => row.PrintStatus == PrintItemStatus.Waiting));
        True(marks.SequenceEqual(OrderReceiptMarks(fixture)));
        True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
        True(fixture.Base.Dialogs.Errors.Count > 0);
        fixture.Base.Dialogs.AcceptPrint = false;
        await fixture.Main.PrintOrderReceiptsAsync();
        Equal(1, fixture.Base.Dialogs.Confirmation!.PreviouslyPrintedCount); Equal(1, fixture.ReceiptPrinter.Calls);

        // The receipt toolbar must disclose the same uncertain transfer, even when only
        // one independently marked receipt is selected rather than the whole order batch.
        foreach (var row in fixture.Main.Receipts)
            row.IsSelectedForOrders = row.PrintStatus == PrintItemStatus.Error;
        fixture.Base.Dialogs.Confirmation = null;
        await ExecuteAsync(fixture.Main.PrintSelectedCommand);
        Equal(1, fixture.Base.Dialogs.Confirmation!.PreviouslyPrintedCount);
        Equal(1, fixture.Base.Dialogs.Confirmation.Count); Equal(1, fixture.ReceiptPrinter.Calls);
        True(fixture.Main.Receipts.Single(row => row.PrintStatus == PrintItemStatus.Error).IsSelectedForOrders);

        fixture.Base.Dialogs.Confirmation = null;
        await ExecuteAsync(fixture.Main.RetryFailedCommand);
        Equal(1, fixture.Base.Dialogs.Confirmation!.PreviouslyPrintedCount);
        Equal(1, fixture.Base.Dialogs.Confirmation.Count); Equal(1, fixture.ReceiptPrinter.Calls);
        Equal(1, fixture.Main.Receipts.Count(row => row.PrintStatus == PrintItemStatus.Error));
        Equal(0, fixture.Base.History.Saves);
        // RetryFailed intentionally marks all failed visible receipts. Restore the independent
        // selections before checking that the combined coordinator leaves them unchanged.
        foreach (var row in fixture.Main.Receipts)
        {
            var mark = marks.Single(mark => mark.Id == row.Id);
            row.IsSelected = mark.All; row.IsSelectedForOrders = mark.Orders;
        }
        fixture.Dialogs.Accept = false;
        await fixture.Main.PrintOrdersWithReceiptsAsync();
        Equal(1, fixture.Dialogs.Confirmation!.PreviouslyPrintedReceipts);
        Equal(1, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
        True(marks.SequenceEqual(OrderReceiptMarks(fixture))); OrderReceiptsAssertNoLabels(fixture, combinedPreviews: 1);
    }

    private static async Task OrderReceiptsPrintHistoryFailureAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.MarkOrders();
        fixture.Base.History.FailSave = true;
        var marks = OrderReceiptMarks(fixture);
        await fixture.Main.PrintOrderReceiptsAsync();

        Equal(3, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
        True(fixture.Main.Receipts.All(row => row.PrintStatus == PrintItemStatus.Done && row.PrintError.Length > 0));
        True(fixture.Workspace.AllOrderRows.All(row => row.IsSelectedForLabels));
        True(marks.SequenceEqual(OrderReceiptMarks(fixture)));
        True(fixture.Main.StatusText.Contains("історію", StringComparison.OrdinalIgnoreCase));
        fixture.Base.Dialogs.AcceptPrint = false;
        await fixture.Main.PrintOrderReceiptsAsync();
        Equal(3, fixture.Base.Dialogs.Confirmation!.PreviouslyPrintedCount);
        Equal(3, fixture.ReceiptPrinter.Calls); Equal(0, fixture.Base.History.Saves);
        OrderReceiptsAssertNoLabels(fixture);
    }

    private static (string Id, bool All, bool Orders)[] OrderReceiptMarks(CombinedFixture fixture) =>
        fixture.Main.Receipts.Select(row => (row.Id, row.IsSelected, row.IsSelectedForOrders)).ToArray();

    private static (OrderKey Key, bool Selected, bool Printed, bool Submission, string Status)[] OrderLabelState(CombinedFixture fixture) =>
        fixture.Workspace.AllOrderRows.Select(row => (row.Key, row.IsSelectedForLabels, row.HasPrintedLabel, row.HasLabelSubmission, row.LabelPrintStatus)).ToArray();

    private static void OrderReceiptsAssertNoLabels(CombinedFixture fixture, int combinedPreviews = 0)
    {
        Equal(0, fixture.LabelPrinter.Calls); Equal(0, fixture.LabelHistory.Attempts.Count);
        Equal(combinedPreviews, fixture.Dialogs.Previews); Equal(0, fixture.Dialogs.StandalonePreviews);
        True(fixture.Workspace.AllOrderRows.All(row => !row.HasPrintedLabel && !row.HasLabelSubmission));
    }

    private static void OrderReceiptsAssertBusy(CombinedFixture fixture)
    {
        True(fixture.Main.IsBusy && fixture.Main.IsPrinting && fixture.Workspace.IsWorkspaceRefreshing);
        True(!fixture.Main.PrintOrderReceiptsCommand.CanExecute(null) && !fixture.Main.PrintOrdersWithReceiptsCommand.CanExecute(null));
        True(!fixture.Main.PrintSelectedCommand.CanExecute(null) && !fixture.Main.RefreshAllCommand.CanExecute(null) && !fixture.Main.SettingsCommand.CanExecute(null));
        True(!fixture.Labels.PrintCommand.CanExecute(null) && !fixture.Labels.ToggleHeaderSelectionCommand.CanExecute(null));
        True(fixture.Waybills!.IsWorkspaceRefreshing && !fixture.Waybills.PrintSelectedCommand.CanExecute(null) && !fixture.Waybills.RefreshCommand.CanExecute(null));
    }
}

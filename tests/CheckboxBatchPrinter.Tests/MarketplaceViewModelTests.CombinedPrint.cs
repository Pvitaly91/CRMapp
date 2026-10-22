using System.ComponentModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task CombinedPrintOrderSortAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.Workspace.Orders.SortDescriptions.Clear();
        fixture.Workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Ascending));
        fixture.Main.OrdersReceiptsTab.View.SortDescriptions.Clear();
        fixture.Main.OrdersReceiptsTab.View.SortDescriptions.Add(new(nameof(ReceiptRowViewModel.Serial), ListSortDirection.Descending));
        foreach (var row in fixture.Main.Receipts) { row.IsSelected = true; row.IsSelectedForOrders = row.Id == ReceiptTwo; }
        var marks = fixture.Main.Receipts.Select(r => (r.Id, r.IsSelected, r.IsSelectedForOrders)).ToArray();
        True(!fixture.Main.PrintOrdersWithReceiptsCommand.CanExecute(null), "Receipt marks alone may not activate combined order printing.");
        fixture.MarkOrders();
        True(fixture.Main.PrintOrdersWithReceiptsCommand.CanExecute(null));

        await fixture.Main.PrintOrdersWithReceiptsAsync();

        var expected = new[] { ReceiptOne, ReceiptTwo, ReceiptThree };
        CombinedAssertOrder(fixture, ["ORDER-A", "ORDER-B", "ORDER-C"], expected);
        True(marks.SequenceEqual(fixture.Main.Receipts.Select(r => (r.Id, r.IsSelected, r.IsSelectedForOrders))),
            "Combined order printing must not consume either receipt tab's marks.");
        True(fixture.Workspace.AllOrderRows.All(r => !r.IsSelectedForLabels && r.HasPrintedLabel));
        True(fixture.Main.Receipts.All(r => r.PrintStatus == PrintItemStatus.Done));
        Equal(3, fixture.Base.History.Saves); Equal(3, fixture.Base.History.Records.Count);
        Equal(LabelSubmissionState.Submitted, fixture.LabelHistory.Attempts.Single().State);
        Equal(0, fixture.Base.Dialogs.Errors.Count); Equal(0, fixture.Dialogs.StandalonePreviews);
        var logs = fixture.PrintLogger.Entries.Where(entry => entry.Operation.StartsWith("combined.print.", StringComparison.Ordinal)).ToArray();
        var selected = logs.Single(entry => entry.Operation == "combined.print.selected");
        var batch = selected.Status.Split(';')[0];
        True(Guid.TryParseExact(batch["batch=".Length..], "N", out _));
        True(logs.All(entry => entry.Status.StartsWith(batch + ";", StringComparison.Ordinal)));
        True(logs.Single(entry => entry.Operation == "combined.print.prepared").Status.Contains("orders=3;receipts=3;label_pages=3;hidden_orders=0"));
        foreach (var stage in new[] { "selected", "receipts.accepted" })
        {
            var manifest = logs.Where(entry => entry.Operation == "combined.print." + stage + ".receipt").ToArray();
            True(manifest.Select(entry => entry.ReceiptId).SequenceEqual(expected));
            for (var index = 0; index < manifest.Length; index++)
                True(manifest[index].Status.Contains($"position={index + 1};order=ORDER-{(char)('A' + index)};marketplace=Prom"));
        }
        foreach (var stage in new[] { "labels.submit", "labels.accepted", "receipts.submit", "receipts.accepted" })
            Equal(1, logs.Count(entry => entry.Operation == "combined.print." + stage));
        True(logs.All(entry => !entry.Status.Contains("TTN-", StringComparison.Ordinal)),
            "The print diagnostic manifest must not include shipping tracking data.");

        using var logFailure = await CombinedFixture.CreateAsync();
        logFailure.MarkOrders();
        logFailure.PrintLogger.FailWrites = true;
        await logFailure.Main.PrintOrdersWithReceiptsAsync();
        Equal(3, logFailure.ReceiptPrinter.Calls); Equal(1, logFailure.LabelPrinter.Calls);
        True(logFailure.Main.Receipts.All(row => row.PrintStatus == PrintItemStatus.Done));
        True(logFailure.Workspace.AllOrderRows.All(row => !row.IsSelectedForLabels));
        Equal(3, logFailure.Base.History.Saves);
        Equal(0, logFailure.Base.Dialogs.Errors.Count);
    }

    private static async Task CombinedPrintMultipleReceiptsAsync()
    {
        const string later = "00000000-0000-0000-0000-000000000004";
        using var fixture = await CombinedFixture.CreateAsync(
            orders: [CombinedTestOrder("A", "41", [ReceiptOne, ReceiptTwo]), CombinedTestOrder("B", "42", [ReceiptThree, later])],
            receipts: [CombinedReceipt(later, 10, 15), CombinedReceipt(ReceiptTwo, 7, 14),
                CombinedReceipt(ReceiptThree, 9, 15), CombinedReceipt(ReceiptOne, 8, 13)]);
        fixture.Workspace.Orders.SortDescriptions.Clear();
        fixture.Workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Descending));
        fixture.MarkOrders();

        await fixture.Main.PrintOrdersWithReceiptsAsync();

        True(fixture.ReceiptPrinter.Documents.Select(r => r.ReceiptId).SequenceEqual(new[] { ReceiptThree, later, ReceiptOne, ReceiptTwo }),
            "Multiple receipts must remain with their order, sorted by fiscal date then numeric serial.");
        True(fixture.ReceiptPrinter.Documents.Select(r => r.OrderNumber).SequenceEqual(new[] { "ORDER-B", "ORDER-B", "ORDER-A", "ORDER-A" }));
        True(fixture.LabelPrinter.Batch!.Pages.Select(p => p.Shipment.OrderNumber).SequenceEqual(new[] { "ORDER-B", "ORDER-A" }));
        Equal(4, fixture.Dialogs.Confirmation!.Receipts.Count);
        True(fixture.Dialogs.Confirmation.Receipts.Items.Select(i => i.Position).SequenceEqual(new[] { 1, 2, 3, 4 }));
        Equal(4, fixture.ReceiptPrinter.Calls); Equal(1, fixture.LabelPrinter.Calls);
    }

    private static async Task CombinedPrintFrozenViewAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.Workspace.Orders.SortDescriptions.Clear();
        fixture.Workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Ascending));
        fixture.MarkOrders();
        fixture.Workspace.OrderSearch = "no-visible-orders";
        True(!fixture.Main.PrintOrdersWithReceiptsCommand.CanExecute(null), "Hidden-only order marks may not activate combined printing.");
        fixture.Workspace.OrderSearch = "ORDER-A";
        fixture.Images.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Dialogs.OnPreview = () =>
        {
            fixture.Workspace.OrderSearch = "ORDER-C";
            fixture.Workspace.Orders.SortDescriptions.Clear();
            fixture.Workspace.Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.Number), ListSortDirection.Descending));
            fixture.Main.SelectedTabIndex = 0;
            fixture.Main.SearchText = "does-not-exist";
            fixture.Base.Settings.App.PrinterName = "changed-after-preparation";
            foreach (var receipt in fixture.Main.Receipts)
                receipt.OrderMatch = null;
        };
        var print = fixture.Main.PrintOrdersWithReceiptsAsync();
        try
        {
            await fixture.Images.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Workspace.OrderSearch = "ORDER-B";
            fixture.Workspace.AllOrderRows.Single(r => r.Number == "ORDER-A").IsSelectedForLabels = false;
            fixture.Main.OrdersReceiptsTab.SearchText = "no-matching-receipts";
        }
        finally { fixture.Images.Gate.TrySetResult(true); }
        await print;

        CombinedAssertOrder(fixture, ["ORDER-A"], [ReceiptOne]);
        Equal("mock-printer", fixture.ReceiptPrinter.PrinterName);
        Equal(2, fixture.Dialogs.Confirmation!.Receipts.HiddenSelectedCount);
        True(fixture.Workspace.AllOrderRows.Where(r => r.Number != "ORDER-A").All(r => r.IsSelectedForLabels && !r.HasPrintedLabel),
            "Marks hidden when the combined operation starts must not enter either batch or be cleared.");
        Equal(1, fixture.Base.History.Records.Count);
    }

    private static async Task CombinedPrintBusyUntilBothJobsAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync(withWaybills: true);
        fixture.MarkOrders();
        fixture.Images.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.LabelPrinter.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReceiptPrinter.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var print = fixture.Main.PrintOrdersWithReceiptsAsync();
        try
        {
            await fixture.Images.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CombinedAssertBusy(fixture);
            await fixture.Waybills!.RunAsync(null, true);
            Equal(0, fixture.WaybillPrinter.Calls);
            await fixture.Main.PrintOrdersWithReceiptsAsync();
            Equal(0, fixture.ReceiptPrinter.Calls); Equal(0, fixture.LabelPrinter.Calls);
            fixture.Images.Gate.TrySetResult(true);
            await Task.WhenAll(fixture.LabelPrinter.Entered.Task, fixture.ReceiptPrinter.Entered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            CombinedAssertBusy(fixture);
            fixture.LabelPrinter.Gate.TrySetResult(true);
            await fixture.LabelPrinter.Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await DrainDispatcherAsync();
            CombinedAssertBusy(fixture);
            Equal(0, fixture.Base.History.Saves);
            True(!print.IsCompleted, "Finishing the label job must not release the combined print reservation.");
        }
        finally
        {
            fixture.Images.Gate.TrySetResult(true);
            fixture.LabelPrinter.Gate.TrySetResult(true);
            fixture.ReceiptPrinter.Gate.TrySetResult(true);
        }
        await print;
        True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting && !fixture.Labels.IsBusy && !fixture.Labels.IsPrintOperationActive);
        True(!fixture.Waybills!.IsWorkspaceRefreshing && fixture.Waybills.PrintSelectedCommand.CanExecute(null));
        Equal(3, fixture.ReceiptPrinter.Calls); Equal(1, fixture.LabelPrinter.Calls);
        await CombinedPrintSamePrinterAsync();
        await CombinedPrintWaitForWaybillAsync();
    }

    private static async Task CombinedPrintSamePrinterAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.LabelSettings.Config = fixture.LabelSettings.Config with
            { Print = fixture.LabelSettings.Config.Print with { PrinterName = "MOCK-PRINTER" } };
        fixture.MarkOrders();
        fixture.LabelPrinter.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.ReceiptPrinter.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var print = fixture.Main.PrintOrdersWithReceiptsAsync();
        try
        {
            await fixture.LabelPrinter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Equal(0, fixture.ReceiptPrinter.Calls);
            CombinedAssertBusy(fixture);
            fixture.LabelPrinter.Gate.TrySetResult(true);
            await fixture.ReceiptPrinter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            CombinedAssertBusy(fixture);
            Equal(LabelSubmissionState.Submitted, fixture.LabelHistory.Attempts.Single().State);
        }
        finally
        {
            fixture.LabelPrinter.Gate.TrySetResult(true);
            fixture.ReceiptPrinter.Gate.TrySetResult(true);
        }
        await print;
        Equal(1, fixture.LabelPrinter.Calls); Equal(3, fixture.ReceiptPrinter.Calls);
    }

    private static async Task CombinedPrintWaitForWaybillAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync(withWaybills: true);
        fixture.MarkOrders();
        fixture.WaybillPrinter.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var waybillPrint = fixture.Waybills!.RunAsync(null, true);
        try
        {
            await fixture.WaybillPrinter.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(fixture.Waybills.IsPrintingBusy && !fixture.Main.PrintOrdersWithReceiptsCommand.CanExecute(null));
            await fixture.Main.PrintOrdersWithReceiptsAsync();
            Equal(0, fixture.ReceiptPrinter.Calls); Equal(0, fixture.LabelPrinter.Calls);
            True(fixture.Workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
        }
        finally { fixture.WaybillPrinter.Gate.TrySetResult(true); }
        await waybillPrint;
        True(!fixture.Waybills.IsPrintingBusy && fixture.Main.PrintOrdersWithReceiptsCommand.CanExecute(null));
    }

    private static async Task CombinedPrintPreparationFailuresAsync()
    {
        foreach (var failure in new[] { "unlinked", "not-ready", "canceled", "missing-ttn", "missing-key", "png", "invalid-image", "printer", "cashier", "changed-ttn" })
        {
            using var fixture = await CombinedFixture.CreateAsync();
            fixture.MarkOrders();
            var first = fixture.Workspace.AllOrderRows.Single(r => r.Number == "ORDER-A");
            switch (failure)
            {
                case "unlinked": first.Update(first.Model, [], []); break;
                case "not-ready": fixture.Main.Receipts.Single(r => r.Id == ReceiptOne).Update(CombinedReceipt(ReceiptOne, 1, 11, status: "CREATED")); break;
                case "canceled":
                    fixture.Workspace.ShowCanceledOrders = true;
                    first.Update(first.Model with { SourceStatus = "cancelled", Status = "Скасовано" }, fixture.Main.Receipts, []); break;
                case "missing-ttn": first.Update(first.Model with { Shipments = [] }, fixture.Main.Receipts, []); break;
                case "missing-key": fixture.LabelSource.MissingCredentialTracking = "TTN-A"; break;
                case "png": fixture.Images.FailId = ReceiptOne; break;
                case "invalid-image": fixture.ReceiptPrinter.FailValidation = true; break;
                case "printer": fixture.ReceiptPrinter.PrinterAvailable = false; break;
                case "cashier": fixture.Base.Settings.App.Login = "different-cashier"; break;
                case "changed-ttn": fixture.Dialogs.OnPreview = () => first.Update(first.Model with { Shipments = [new("nova_poshta", "REPLACED-TTN")] }, fixture.Main.Receipts, []); break;
            }
            await fixture.Main.PrintOrdersWithReceiptsAsync();
            Equal(0, fixture.LabelPrinter.Calls); Equal(0, fixture.ReceiptPrinter.Calls);
            Equal(0, fixture.LabelHistory.Attempts.Count); Equal(0, fixture.Base.History.Saves);
            True(fixture.Workspace.AllOrderRows.All(r => r.IsSelectedForLabels), failure + " consumed the order selection.");
            True(!fixture.Main.IsBusy && !fixture.Main.IsPrinting && !fixture.Labels.IsBusy && !fixture.Labels.IsPrintOperationActive,
                failure + " left the combined operation busy.");
            True(fixture.Main.Receipts.All(r => r.PrintStatus is not (PrintItemStatus.Downloading or PrintItemStatus.Printing)),
                failure + " left transient receipt print states.");
            True(fixture.PrintLogger.Entries.Any(entry => entry.Operation == "combined.print.prepare" && entry.IsError));
            True(fixture.PrintLogger.Entries.All(entry => !entry.Operation.EndsWith(".accepted", StringComparison.Ordinal)),
                failure + " reported a packet as accepted before submission.");
        }
    }

    private static async Task CombinedPrintCanceledPreviewAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync(previouslyPrinted: true);
        fixture.MarkOrders();
        fixture.Main.Receipts[0].IsSelected = true;
        fixture.Main.Receipts[1].IsSelectedForOrders = true;
        await fixture.LabelHistory.SaveAsync(new("prior-label-attempt", "prior synthetic labels", ShippingLabelsTests.Settings.PrinterName,
            DateTimeOffset.UtcNow.AddDays(-1), LabelSubmissionState.SubmissionUnknown,
            [new(ShippingCarrier.NovaPoshta, "synthetic", "TTN-A", "doc-TTN-A", 1, "prior-fingerprint")]));
        var marks = fixture.Main.Receipts.Select(r => (r.Id, r.IsSelected, r.IsSelectedForOrders, r.PrintStatus)).ToArray();
        fixture.Dialogs.Accept = false;
        fixture.Dialogs.OnPreview = () =>
        {
            Equal(0, fixture.LabelPrinter.Calls); Equal(0, fixture.ReceiptPrinter.Calls);
            Equal(1, fixture.LabelHistory.Attempts.Count); Equal(0, fixture.Base.History.Saves);
            CombinedAssertBusy(fixture);
        };

        await fixture.Main.PrintOrdersWithReceiptsAsync();

        Equal(1, fixture.Dialogs.Previews); Equal(1, fixture.Dialogs.Confirmation!.PreviouslyPrintedReceipts);
        Equal(1, fixture.Dialogs.Previous.Count);
        Equal(0, fixture.LabelPrinter.Calls); Equal(0, fixture.ReceiptPrinter.Calls);
        Equal(1, fixture.LabelHistory.Attempts.Count); Equal(0, fixture.Base.History.Saves);
        True(fixture.Workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
        True(marks.SequenceEqual(fixture.Main.Receipts.Select(r => (r.Id, r.IsSelected, r.IsSelectedForOrders, r.PrintStatus))),
            "Declining the shared preview must preserve receipt state and both independent selections.");
    }

    private static async Task CombinedPrintLabelFailureAsync()
    {
        using var fixture = await CombinedFixture.CreateAsync();
        fixture.MarkOrders();
        fixture.LabelPrinter.FailAfterBoundary = true;

        await fixture.Main.PrintOrdersWithReceiptsAsync();

        Equal(1, fixture.LabelPrinter.Calls); Equal(3, fixture.ReceiptPrinter.Calls);
        Equal(LabelSubmissionState.SubmissionUnknown, fixture.LabelHistory.Attempts.Single().State);
        Equal(3, fixture.Base.History.Saves); Equal(3, fixture.Base.History.Records.Count);
        True(fixture.Main.Receipts.All(r => r.PrintStatus == PrintItemStatus.Done));
        True(fixture.Workspace.AllOrderRows.All(r => r.IsSelectedForLabels && r.HasLabelSubmission && !r.HasPrintedLabel),
            "A label transfer of unknown outcome must retain paired marks and its duplicate-risk history.");
        True(!fixture.Main.IsBusy && !fixture.Labels.IsBusy);
    }

    private static async Task CombinedPrintReceiptFailureAsync()
    {
        foreach (var historyFailure in new[] { false, true })
        {
            using var fixture = await CombinedFixture.CreateAsync();
            fixture.MarkOrders();
            fixture.ReceiptPrinter.Fail = !historyFailure;
            fixture.Base.History.FailSave = historyFailure;

            await fixture.Main.PrintOrdersWithReceiptsAsync();

            Equal(1, fixture.LabelPrinter.Calls); Equal(historyFailure ? 3 : 1, fixture.ReceiptPrinter.Calls);
            Equal(LabelSubmissionState.Submitted, fixture.LabelHistory.Attempts.Single().State);
            True(fixture.Workspace.AllOrderRows.All(r => r.HasPrintedLabel));
            Equal(0, fixture.Base.History.Saves);
            True(fixture.Workspace.AllOrderRows.All(r => r.IsSelectedForLabels == !historyFailure),
                "Accepted jobs may clear the paired marks despite a history-save warning; a failed receipt transfer may not.");
            if (historyFailure) True(fixture.Main.Receipts.All(r => r.PrintStatus == PrintItemStatus.Done));
            else
            {
                Equal(1, fixture.Main.Receipts.Count(r => r.PrintStatus == PrintItemStatus.Error));
                Equal(2, fixture.Main.Receipts.Count(r => r.PrintStatus == PrintItemStatus.Waiting));
            }
            True(!fixture.Main.IsBusy && !fixture.Labels.IsBusy);
            Equal(1, fixture.PrintLogger.Entries.Count(entry => entry.Operation == "combined.print.labels.accepted"));
            Equal(historyFailure ? 1 : 0, fixture.PrintLogger.Entries.Count(entry => entry.Operation == "combined.print.receipts.accepted"));
            True(fixture.PrintLogger.Entries.Any(entry => entry.Operation == (historyFailure ? "receipt.job.history" : "combined.print.receipts") && entry.IsError));
            if (!historyFailure)
            {
                fixture.Dialogs.Accept = false;
                await fixture.Main.PrintOrdersWithReceiptsAsync();
                Equal(1, fixture.Dialogs.Confirmation!.PreviouslyPrintedReceipts);
                Equal(1, fixture.ReceiptPrinter.Calls); Equal(1, fixture.LabelPrinter.Calls);
                Equal(0, fixture.Base.History.Saves);
                True(fixture.Workspace.AllOrderRows.All(r => r.IsSelectedForLabels));
            }
        }
    }

    private static void CombinedAssertBusy(CombinedFixture fixture)
    {
        True(fixture.Main.IsBusy && fixture.Main.IsPrinting && fixture.Labels.IsBusy && fixture.Labels.IsPrintOperationActive);
        True(!fixture.Main.PrintOrdersWithReceiptsCommand.CanExecute(null));
        True(!fixture.Main.PrintSelectedCommand.CanExecute(null));
        True(!fixture.Main.RefreshAllCommand.CanExecute(null));
        True(!fixture.Main.SettingsCommand.CanExecute(null));
        True(!fixture.Labels.PrintCommand.CanExecute(null));
        True(!fixture.Labels.ToggleHeaderSelectionCommand.CanExecute(null));
        if (fixture.Waybills is { } waybills)
        {
            True(waybills.IsWorkspaceRefreshing);
            True(!waybills.PrintSelectedCommand.CanExecute(null) && !waybills.PreviewCommand.CanExecute(null));
            True(!waybills.ToggleHeaderSelectionCommand.CanExecute(null) && !waybills.RefreshCommand.CanExecute(null));
        }
    }

    private static void CombinedAssertOrder(CombinedFixture fixture, string[] expectedOrders, string[] expectedReceipts)
    {
        Equal(1, fixture.LabelPrinter.Calls); Equal(expectedReceipts.Length, fixture.ReceiptPrinter.Calls);
        True(fixture.LabelPrinter.Batch!.Pages.Select(p => p.Shipment.OrderNumber).SequenceEqual(expectedOrders));
        True(fixture.ReceiptPrinter.Documents.Select(p => p.OrderNumber).SequenceEqual(expectedOrders));
        True(fixture.ReceiptPrinter.Documents.Select(p => p.ReceiptId).SequenceEqual(expectedReceipts));
        True(fixture.ReceiptPrinter.Documents.Select(p => p.TrackingText).SequenceEqual(expectedOrders.Select(n => "НП: TTN-" + n[^1])));
        True(fixture.Dialogs.Confirmation!.Receipts.Items.Select(p => p.ReceiptId).SequenceEqual(expectedReceipts));
        True(fixture.Dialogs.Confirmation.Receipts.Items.Select(p => p.OrderNumber).SequenceEqual(expectedOrders));
    }

    private static MarketplaceOrder CombinedTestOrder(string suffix, string id, IReadOnlyList<string> receiptIds) => Order(receiptIds) with
    {
        Key = new(MarketplaceKind.Prom, "prom-test", id), Number = "ORDER-" + suffix,
        CreatedAt = new DateTimeOffset(2026, 9, 23, 9 + suffix[0] - 'A', 0, 0, TimeSpan.FromHours(3)),
        Shipments = [new("nova_poshta", "TTN-" + suffix)]
    };

    private static ReceiptRecord CombinedReceipt(string id, long serial, int hour, string status = "DONE") => new()
    {
        Id = id, Serial = serial, Status = status, Type = ReceiptTypes.Sell, TotalSumMinor = 10000,
        FiscalDate = new DateTimeOffset(2026, 9, 23, hour, 0, 0, TimeSpan.FromHours(3))
    };

    private sealed class CombinedFixture : IDisposable
    {
        public Fixture Base { get; } = new();
        public CombinedImages Images { get; } = new();
        public CombinedReceiptPrinter ReceiptPrinter { get; } = new();
        public CombinedLabelPrinter LabelPrinter { get; } = new();
        public CombinedLabelPrinter WaybillPrinter { get; } = new();
        public CombinedLabelDialogs Dialogs { get; } = new();
        public ShippingLabelsTests.Stores LabelSettings { get; } = new();
        public ShippingLabelsTests.Source LabelSource { get; } = new();
        public CombinedLabelHistory LabelHistory { get; } = new();
        public CombinedPrintLogger PrintLogger { get; } = new();
        public MainViewModel Main { get; }
        public MarketplaceWorkspaceViewModel Workspace { get; }
        public ShippingLabelsViewModel Labels { get; }
        public WaybillsViewModel? Waybills { get; }

        private CombinedFixture(bool previouslyPrinted, bool withWaybills)
        {
            if (!previouslyPrinted) Base.History.Records.Clear();
            var sync = new MarketplaceSyncService([Base.Source, Base.Rozetka], Base.Settings, Base.Cache);
            Workspace = new(Base.Settings, sync, Base.Links, Base.Details, Base.Dialogs, autoLinkEnabled: false);
            Labels = new(Workspace, LabelSettings, LabelHistory, LabelSource,
                new ShippingLabelsTests.Renderer(), LabelPrinter, Dialogs);
            Workspace.Labels = Labels;
            if (withWaybills) Waybills = new(new DashboardWaybillSource(), new DashboardWaybillPreparation(),
                new DashboardWaybillSettings(), new DashboardWaybillHistory(), WaybillPrinter, new DashboardWaybillDialogs());
            Main = new(Base.ReceiptSource, Images, Base.Settings, Base.Authentication, Base.History,
                ReceiptPrinter, Base.Dialogs, PrintLogger, Workspace, waybills: Waybills) { DateFrom = new(2026, 9, 23), DateTo = new(2026, 9, 23) };
        }

        public static async Task<CombinedFixture> CreateAsync(IReadOnlyList<MarketplaceOrder>? orders = null,
            IReadOnlyList<ReceiptRecord>? receipts = null, bool previouslyPrinted = false, bool withWaybills = false)
        {
            var fixture = new CombinedFixture(previouslyPrinted, withWaybills);
            fixture.Base.Source.Orders = orders ?? [CombinedTestOrder("B", "42", [ReceiptTwo]),
                CombinedTestOrder("C", "43", [ReceiptThree]), CombinedTestOrder("A", "41", [ReceiptOne])];
            fixture.Base.ReceiptSource.Rows = receipts ?? [Receipt(ReceiptThree, 3, 10000), Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 10000)];
            await fixture.Main.RefreshAsync();
            await OpenOrdersAsync(fixture.Main, fixture.Workspace);
            if (fixture.Waybills is { } waybills)
            {
                await waybills.RefreshAsync();
                waybills.Rows.First(r => !r.IsDeleted).IsSelected = true;
            }
            return fixture;
        }

        public void MarkOrders() { foreach (var row in Workspace.AllOrderRows) row.IsSelectedForLabels = true; }
        public void Dispose() { Labels.Dispose(); Waybills?.Dispose(); }
    }

    private sealed class CombinedPrintLogger : IAppLogger
    {
        public string LogDirectory => "";
        public bool FailWrites;
        public List<(string Operation, string? ReceiptId, string Status, bool IsError)> Entries { get; } = [];
        public void Info(string operation, string? receiptId = null, int? httpStatus = null, string? printStatus = null) =>
            Record(operation, receiptId, printStatus, false);
        public void Error(string operation, Exception exception, string? receiptId = null, int? httpStatus = null, string? printStatus = null) =>
            Record(operation, receiptId, printStatus, true);
        private void Record(string operation, string? receiptId, string? printStatus, bool isError)
        {
            if (FailWrites) throw new IOException("Synthetic diagnostic disk failure.");
            if (operation.StartsWith("combined.print.", StringComparison.Ordinal) || operation.StartsWith("receipt.job.", StringComparison.Ordinal))
                Entries.Add((operation, receiptId, printStatus ?? "", isError));
        }
    }

    private sealed class CombinedImages : IReceiptImageService
    {
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string? FailId;
        public async Task<byte[]> GetPngAsync(string id, int width, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (id == FailId) throw new InvalidOperationException("Synthetic combined PNG failure.");
            var bitmap = BitmapSource.Create(100, 100, 96, 96, PixelFormats.Gray8, null, Enumerable.Repeat((byte)255, 10000).ToArray(), 100);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
        }
        public Task<int> ClearCacheAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task CleanupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class CombinedReceiptPrinter : IPrintService
    {
        public bool PrinterAvailable = true, Fail, FailValidation;
        public int Calls;
        public int FailOnCall;
        public Action<int>? OnSubmit;
        public string PrinterName = "";
        public PrintReceiptDocument[] Documents = [];
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> GetInstalledPrinters() => ["mock-printer"];
        public bool PrinterExists(string printerName) => PrinterAvailable && printerName == "mock-printer";
        public void ValidateReceiptsForPrint(IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings)
        { Documents = []; if (FailValidation) throw new InvalidOperationException("Synthetic malformed PNG/layout."); }
        public async Task PrintReceiptAsync(PrintReceiptDocument receipt, AppSettings settings, CancellationToken cancellationToken = default)
        {
            Calls++; Documents = [..Documents, receipt]; PrinterName = settings.PrinterName; Entered.TrySetResult();
            OnSubmit?.Invoke(Calls);
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (Fail || Calls == FailOnCall) throw new InvalidOperationException("Synthetic combined receipt backend failure.");
        }
        public Task PrintReceiptsAsSingleJobAsync(IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Multiple receipts must never share a Windows job.");
        public Task PrintTestAsync(AppSettings settings, CancellationToken cancellationToken = default) => throw new InvalidOperationException("Physical printing is forbidden in synthetic tests.");
    }

    private sealed class CombinedLabelPrinter : ILabelPrinter
    {
        public bool FailAfterBoundary;
        public int Calls;
        public LabelPrintBatch? Batch;
        public TaskCompletionSource<bool>? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IReadOnlyList<string> Printers() => [ShippingLabelsTests.Settings.PrinterName];
        public IReadOnlyList<LabelDriverFormat> Formats(string printer) => [new("synthetic", 101.5, 101.5)];
        public LabelPrinterGeometry Inspect(LabelPrintSettings settings) => ShippingLabelsTests.Geometry;
        public async Task<int?> SubmitAsync(LabelPrintBatch batch, string jobName, Func<Task> beforeTransfer,
            Func<int, Task> jobCreated, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await beforeTransfer(); Calls++; Batch = batch; Entered.TrySetResult();
            if (Gate is not null) await Gate.Task.WaitAsync(cancellationToken);
            if (FailAfterBoundary) throw new InvalidOperationException("Synthetic combined label backend failure after StartDoc boundary.");
            await jobCreated(84); Completed.TrySetResult(); return 84;
        }
    }

    private sealed class CombinedLabelHistory : ILabelHistoryStore
    {
        public List<LabelPrintAttempt> Attempts { get; } = [];
        public Task<IReadOnlyList<LabelPrintAttempt>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LabelPrintAttempt>>(Attempts.ToArray());
        public Task SaveAsync(LabelPrintAttempt attempt, CancellationToken cancellationToken = default)
        { Attempts.RemoveAll(a => a.AttemptId == attempt.AttemptId); Attempts.Add(attempt); return Task.CompletedTask; }
    }

    private sealed class CombinedLabelDialogs : ILabelDialogs
    {
        public int Previews, StandalonePreviews;
        public bool Accept = true;
        public Action? OnPreview;
        public CombinedPrintConfirmation? Confirmation;
        public IReadOnlyList<LabelPrintAttempt> Previous = [];
        public bool PreviewCombined(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous,
            CombinedPrintConfirmation confirmation)
        { Previews++; Confirmation = confirmation; Previous = previous; OnPreview?.Invoke(); return Accept; }
        public bool Preview(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous, bool allowPrint)
        { StandalonePreviews++; return Accept; }
        public Task SettingsAsync() => Task.CompletedTask;
        public void Error(string message) => throw new InvalidOperationException("Combined label errors should be reported through the shared coordinator: " + message);
    }
}

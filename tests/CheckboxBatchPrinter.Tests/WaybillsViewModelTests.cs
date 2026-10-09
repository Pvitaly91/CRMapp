using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static class WaybillsViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA carrier waybills include unlinked and deleted documents with independent sorting search and indices", () => Sta(DocumentsAndFilters)),
        ("STA bound waybill DataGrid receives complete snapshots without enumerating a deferred view", () => Sta(BoundGridRefresh)),
        ("STA waybill partial source errors remain visible and never inherit failed-account rows", () => Sta(SourceErrors)),
        ("STA changed waybill dates discard a late old-range result after a newer fetch completes", () => Sta(StaleDates)),
        ("STA waybill cancel and explicit restart reject late non-cooperative API completion", () => Sta(CancelAndRestart)),
        ("STA waybill first activation debounces dates and inactive tabs make no API calls", () => Sta(Activation)),
        ("STA waybill missing or reversed dates never start an unbounded carrier request", () => Sta(InvalidDates)),
        ("STA waybill settings invalidation clears previous account and rejects its late API result", () => Sta(InvalidateAccount)),
        ("STA waybill disposal cancels API and prevents later refresh or UI changes", () => Sta(Dispose))
    ];

    private static readonly DateOnly Day = new(2026, 10, 9);
    private static Task Sta(Func<Task> test) => MarketplaceViewModelTests.StaAsync(test);
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static WaybillRecord Document(string tracking, ShippingCarrier carrier = ShippingCarrier.NovaPoshta,
        int hour = 12, bool deleted = false) => new(carrier, carrier == ShippingCarrier.NovaPoshta ? "np-account" : "rd-account",
        carrier == ShippingCarrier.NovaPoshta ? "Магазин Prom" : "Кабінет RD", "doc-" + tracking, tracking,
        Day.ToDateTime(new(hour, 0)), deleted ? "Видалено" : "Створено", "Синтетичний одержувач", "Київ", 470m, 2, deleted);
    private static WaybillFetchResult Result(params WaybillRecord[] documents) => new(documents,
        [new(ShippingCarrier.NovaPoshta, "Магазин Prom", true,
            documents.Count(d => d.Carrier == ShippingCarrier.NovaPoshta), ""),
         new(ShippingCarrier.RozetkaDelivery, "Кабінет RD", true,
            documents.Count(d => d.Carrier == ShippingCarrier.RozetkaDelivery), "")]);
    private static WaybillsViewModel Create(Source source)
    {
        var model = new WaybillsViewModel(source);
        model.SetDisplayDates(Day, Day);
        return model;
    }
    private static TaskCompletionSource<WaybillFetchResult> Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task DocumentsAndFilters()
    {
        var oldest = Document("NP-001", hour: 8);
        var newest = Document("RD-003", ShippingCarrier.RozetkaDelivery, 20, true);
        var middle = Document("NP-002", hour: 15) with { Recipient = "Іван Петренко", City = "Львів" };
        var source = new Source { Handler = (_, _, _) => Task.FromResult(Result(oldest, newest, middle)) };
        using var model = Create(source);
        Check(source.Calls.Count == 0, "Constructing the independent tab called an API before activation.");
        await model.RefreshAsync();
        Check(model.Rows.Count == 3, "Documents were incorrectly gated on a linked marketplace order.");
        var rows = model.View.Cast<WaybillRowViewModel>().ToArray();
        Check(rows.Select(r => r.TrackingNumber).SequenceEqual(new[] { "RD-003", "NP-002", "NP-001" }), "Default newest-first sort is wrong.");
        Check(rows.Select(r => r.DisplayIndex).SequenceEqual(new[] { 1, 2, 3 }), "Indices do not follow the visible sorted view.");
        Check(rows[0].IsDeleted && rows[0].CarrierName == "Rozetka Delivery" && rows[0].ConnectionId == "rd-account" &&
              rows[0].ConnectionName == "Кабінет RD" && rows[0].DocumentId == "doc-RD-003" &&
              rows[0].Recipient == "Синтетичний одержувач" && rows[0].City == "Київ" &&
              rows[0].DeclaredValue == 470m && rows[0].Places == 2, "A carrier-owned field or deleted document was lost.");
        model.SelectedCarrier = model.Carriers.Single(c => c.Value == ShippingCarrier.NovaPoshta);
        model.SearchText = "  петренКО  ";
        Check(model.View.Cast<WaybillRowViewModel>().Single().TrackingNumber == "NP-002", "Case-insensitive recipient search failed.");
        Check(model.View.Cast<WaybillRowViewModel>().Single().DisplayIndex == 1 && model.CountText == "Накладних: 1 із 3", "Filtered index/count is wrong.");
        model.SearchText = "doc-np-001";
        Check(model.View.Cast<WaybillRowViewModel>().Single().TrackingNumber == "NP-001", "Document identity search failed.");
        model.SelectedCarrier = model.Carriers[0];
        model.SearchText = "видалено";
        Check(model.View.Cast<WaybillRowViewModel>().Single().IsDeleted, "Deleted documents cannot be found by their carrier status.");
        model.SearchText = "";
        model.View.SortDescriptions.Clear();
        model.View.SortDescriptions.Add(new(nameof(WaybillRowViewModel.TrackingNumber), ListSortDirection.Ascending));
        Check(model.View.Cast<WaybillRowViewModel>().Select(r => r.TrackingNumber).SequenceEqual(new[] { "NP-001", "NP-002", "RD-003" }), "User column sort ignored.");
        Check(model.View.Cast<WaybillRowViewModel>().Select(r => r.DisplayIndex).SequenceEqual(new[] { 1, 2, 3 }), "Indices did not update after user sort.");
        Check(source.Calls.Count == 1 && !model.IsBusy, "Local filters unexpectedly refetched carrier APIs.");
    }

    private static async Task SourceErrors()
    {
        var source = new Source { Handler = (_, _, _) => Task.FromResult(Result(Document("NP-OLD"), Document("RD-OLD", ShippingCarrier.RozetkaDelivery))) };
        using var model = Create(source);
        await model.RefreshAsync();
        source.Handler = (_, _, _) => Task.FromResult(new WaybillFetchResult([Document("NP-NEW")],
            [new(ShippingCarrier.NovaPoshta, "Магазин Prom", true, 1, ""),
             new(ShippingCarrier.RozetkaDelivery, "Кабінет RD", false, 0, "API-ключ не встановлений.")]));
        await model.RefreshAsync();
        Check(model.Rows.Single().TrackingNumber == "NP-NEW", "Failed source inherited stale documents from an earlier account snapshot.");
        Check(model.Status.Contains("Нова пошта") && model.Status.Contains("Rozetka Delivery") &&
              model.Status.Contains("неповне") && model.Status.Contains("API-ключ не встановлений"), "Per-source availability is hidden.");
        source.Handler = (_, _, _) => Task.FromException<WaybillFetchResult>(new Exception("Synthetic-secret-must-not-be-displayed"));
        await model.RefreshAsync();
        Check(model.Rows.Single().TrackingNumber == "NP-NEW" && model.Status.Contains("раніше завантажені"), "Total transport failure should identify retained same-period documents.");
        Check(!model.Status.Contains("Synthetic-secret") && !model.IsBusy, "Transport exception text leaked or tab stayed busy.");
    }

    private static async Task BoundGridRefresh()
    {
        var source = new Source { Handler = (_, _, _) => Task.FromResult(Result(Document("NP-ONE"), Document("RD-TWO", ShippingCarrier.RozetkaDelivery))) };
        using var model = Create(source);
        var originalRows = model.Rows;
        var originalView = model.View;
        var grid = new DataGrid { AutoGenerateColumns = false, ItemsSource = model.View,
            CanUserAddRows = false, IsReadOnly = true };
        grid.Columns.Add(new DataGridTextColumn { Binding = new Binding(nameof(WaybillRowViewModel.TrackingNumber)) });
        var count = new TextBlock { DataContext = model };
        BindingOperations.SetBinding(count, TextBlock.TextProperty, new Binding(nameof(WaybillsViewModel.CountText)));
        try
        {
            grid.Measure(new Size(720, 240));
            grid.Arrange(new Rect(0, 0, 720, 240));
            grid.UpdateLayout();
            await model.RefreshAsync();
            await Task.Yield();
            Check(grid.Items.Count == 2 && count.Text == "Накладних: 2 із 2", "Bound DataGrid received only the first document or count binding failed.");
            source.Handler = (_, _, _) => Task.FromResult(Result(Document("NP-NEW-ONE"),
                Document("NP-NEW-TWO"), Document("RD-NEW-THREE", ShippingCarrier.RozetkaDelivery)));
            await model.RefreshAsync();
            await Task.Yield();
            Check(grid.Items.Count == 3 && count.Text == "Накладних: 3 із 3" &&
                  ReferenceEquals(originalRows, model.Rows) && ReferenceEquals(originalView, model.View),
                "Snapshot replacement broke table identity or left a partially loaded collection.");
            model.SearchText = "NP-";
            await Task.Yield();
            Check(grid.Items.Count == 2 && count.Text == "Накладних: 2 із 3", "Bound counts did not follow filtering after a bulk reset.");
            model.Invalidate();
            await Task.Yield();
            Check(grid.Items.Count == 0 && count.Text == "Накладних: 0 із 0", "Account invalidation did not reset the bound DataGrid.");
        }
        finally
        {
            grid.ItemsSource = null;
            BindingOperations.ClearAllBindings(count);
        }
    }

    private static async Task StaleDates()
    {
        var old = Gate();
        var current = Gate();
        var source = new Source();
        source.Handler = (_, _, _) => source.Calls.Count == 1 ? old.Task : current.Task;
        using var model = Create(source);
        var oldFetch = model.RefreshAsync();
        Check(model.IsBusy, "Initial fetch did not mark the tab busy.");
        model.SetDisplayDates(Day.AddDays(-1), Day.AddDays(-1));
        Check(source.Calls[0].Token.IsCancellationRequested && !model.IsBusy && model.Rows.Count == 0, "Date change did not clear/cancel the old snapshot.");
        var newFetch = model.RefreshAsync();
        Check(source.Calls[1].From == Day.AddDays(-1) && source.Calls[1].To == Day.AddDays(-1), "New request used stale dates.");
        current.SetResult(Result(Document("CURRENT") with { CreatedLocal = Day.AddDays(-1).ToDateTime(new(14, 0)) }));
        await newFetch;
        var status = model.Status;
        old.SetResult(Result(Document("STALE")));
        await oldFetch;
        Check(model.Rows.Single().TrackingNumber == "CURRENT" && model.Status == status && !model.IsBusy,
            "Late old-range completion overwrote the current snapshot or status.");
    }

    private static async Task CancelAndRestart()
    {
        var first = Gate();
        var second = Gate();
        var source = new Source();
        source.Handler = (_, _, _) => source.Calls.Count == 1 ? first.Task : second.Task;
        using var model = Create(source);
        var firstFetch = model.RefreshAsync();
        Check(model.CancelCommand.CanExecute(null), "Cancel unavailable during a request.");
        var secondFetch = model.RefreshAsync();
        Check(source.Calls[0].Token.IsCancellationRequested && model.IsBusy, "Explicit refresh did not replace the in-flight request.");
        first.SetResult(Result(Document("OLD-REQUEST")));
        await firstFetch;
        Check(model.IsBusy && model.Rows.Count == 0, "Stale request stopped the newer request or published its documents.");
        model.CancelCommand.Execute(null);
        Check(source.Calls[1].Token.IsCancellationRequested && !model.IsBusy && !model.CancelCommand.CanExecute(null), "Cancel did not end the current request state.");
        second.SetResult(Result(Document("CANCELLED-REQUEST")));
        await secondFetch;
        Check(model.Rows.Count == 0 && model.Status.Contains("скасовано"), "Non-cooperative cancelled API published its result.");
        source.Handler = (_, _, _) => Task.FromResult(Result(Document("FRESH")));
        await model.RefreshAsync();
        Check(model.Rows.Single().TrackingNumber == "FRESH", "Cancelled tab cannot refresh again.");
    }

    private static async Task Activation()
    {
        var source = new Source { Handler = (_, _, _) => Task.FromResult(Result(Document("FIRST"))) };
        using var model = Create(source);
        model.SetActive(true);
        model.SetDisplayDates(Day.AddDays(-2), Day.AddDays(-1));
        model.SetDisplayDates(Day.AddDays(-3), Day);
        await Until(() => source.Calls.Count == 1 && !model.IsBusy);
        Check(source.Calls[0].From == Day.AddDays(-3) && source.Calls[0].To == Day, "Debounce fetched an intermediate calendar range.");
        model.SetActive(true);
        await Task.Delay(400);
        Check(source.Calls.Count == 1, "Repeated active assignment refetched an already loaded period.");
        model.SetActive(false);
        model.SetDisplayDates(Day, Day);
        await Task.Delay(400);
        Check(source.Calls.Count == 1 && model.Rows.Count == 0, "Inactive tab sent a background API request or retained the old range.");
        var pending = Gate();
        source.Handler = (_, _, _) => pending.Task;
        model.SetActive(true);
        await Until(() => source.Calls.Count == 2);
        model.SetActive(false);
        Check(source.Calls[1].Token.IsCancellationRequested && !model.IsBusy, "Leaving tab did not cancel an API request.");
        pending.SetResult(Result(Document("INACTIVE-LATE")));
        await Task.Delay(50);
        Check(model.Rows.Count == 0, "Inactive late result entered the dashboard.");
        source.Handler = (_, _, _) => Task.FromResult(Result(Document("REOPENED")));
        model.SetActive(true);
        await Until(() => source.Calls.Count == 3 && !model.IsBusy);
        Check(model.Rows.Single().TrackingNumber == "REOPENED", "Reopening after cancellation did not fetch fresh documents.");
    }

    private static async Task InvalidDates()
    {
        var source = new Source();
        using var model = Create(source);
        model.SetDisplayDates(null, Day);
        model.SetActive(true);
        await model.RefreshAsync();
        await Task.Delay(400);
        Check(source.Calls.Count == 0 && model.Status.Contains("укажіть дату") && model.DateRangeText.Contains("не вказано"), "Missing boundary produced an unbounded or silently altered request.");
        model.SetDisplayDates(Day, null);
        await model.RefreshAsync();
        Check(source.Calls.Count == 0, "Missing end boundary reached the carrier API.");
        model.SetDisplayDates(Day, Day.AddDays(-1));
        await model.RefreshAsync();
        Check(source.Calls.Count == 0 && model.Status.Contains("не пізнішою"), "Reversed dates reached the carrier API.");
        model.SetDisplayDates(Day, Day);
        await Until(() => source.Calls.Count == 1 && !model.IsBusy);
        Check(source.Calls[0].From == Day && source.Calls[0].To == Day, "Restoring valid dates did not re-enable one finite request.");
    }

    private static async Task InvalidateAccount()
    {
        var source = new Source { Handler = (_, _, _) => Task.FromResult(Result(Document("PREVIOUS-ACCOUNT"))) };
        using var model = Create(source);
        await model.RefreshAsync();
        var pending = Gate();
        source.Handler = (_, _, _) => pending.Task;
        var fetch = model.RefreshAsync();
        model.Invalidate();
        Check(model.Rows.Count == 0 && source.Calls[1].Token.IsCancellationRequested, "Settings change left another account's documents visible.");
        pending.SetResult(Result(Document("LATE-PREVIOUS-ACCOUNT")));
        await fetch;
        Check(model.Rows.Count == 0 && model.Status.Contains("Налаштування"), "Late API response repopulated the old account after settings invalidation.");
        source.Handler = (_, _, _) => Task.FromResult(Result(Document("NEW-ACCOUNT") with { ConnectionId = "new-account" }));
        await model.RefreshAsync();
        Check(model.Rows.Single().ConnectionId == "new-account", "Fresh account cannot load after invalidation.");
    }

    private static async Task Dispose()
    {
        var pending = Gate();
        var source = new Source { Handler = (_, _, _) => pending.Task };
        var model = Create(source);
        var fetch = model.RefreshAsync();
        model.Dispose();
        var status = model.Status;
        Check(source.Calls[0].Token.IsCancellationRequested && !model.IsBusy && !model.RefreshCommand.CanExecute(null), "Disposal did not cancel the API or disable commands.");
        pending.SetResult(Result(Document("AFTER-EXIT")));
        await fetch;
        await model.RefreshAsync();
        model.SetActive(true);
        model.Invalidate();
        model.Dispose();
        Check(source.Calls.Count == 1 && model.Rows.Count == 0 && model.Status == status, "Disposed tab fetched or changed UI state after shutdown.");
    }

    private static async Task Until(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new Exception("Timed out waiting for the waybill viewmodel state.");
            await Task.Delay(15);
        }
    }
    private sealed class Source : IWaybillService
    {
        public List<(DateOnly From, DateOnly To, CancellationToken Token)> Calls { get; } = [];
        public Func<DateOnly, DateOnly, CancellationToken, Task<WaybillFetchResult>> Handler { get; set; } = (_, _, _) => Task.FromResult(Result());
        public Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
        {
            Calls.Add((from, to, cancellationToken));
            return Handler(from, to, cancellationToken);
        }
    }
}

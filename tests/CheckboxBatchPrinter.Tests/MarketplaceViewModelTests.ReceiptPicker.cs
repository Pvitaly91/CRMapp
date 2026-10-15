using System.Globalization;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> ReceiptPickerScenarios =>
    [
        ("STA order receipt picker cancellation is read-only and confirmed manual choice survives refresh and restart", () => StaAsync(OrderReceiptPickerPersistenceAsync)),
        ("STA order receipt picker ignores independent receipt panel filters and preserves print marks", () => StaAsync(OrderReceiptPickerHiddenFiltersAsync)),
        ("STA order receipt picker excludes occupied receipts including durable unloaded manual claims", () => StaAsync(OrderReceiptPickerReservationsAsync)),
        ("STA order receipt picker rejects modal choices after account refresh configuration or receipt allocation changes", () => StaAsync(OrderReceiptPickerStaleChoiceAsync)),
        ("STA order receipt picker preview uses the existing receipt preview without printing or changing selections", () => StaAsync(OrderReceiptPickerPreviewAsync)),
        ("STA receipt picker searches UUID serial fiscal code and exact localized amounts within inclusive Kyiv dates", () => StaAsync(ReceiptPickerFiltersAsync)),
        ("STA receipt picker preview reserves confirmation and reports failures without accepting a receipt", () => StaAsync(ReceiptPickerPreviewFailureAsync)),
        ("STA receipt picker cannot bypass main busy state or release a pending print job", () => StaAsync(ReceiptPickerBusyGuardAsync))
    ];

    private static async Task ReceiptPickerBusyGuardAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with { Total = 999m }];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 15500)];
        fixture.Printer.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        main.SelectedTabIndex = 0;
        var receipt = main.Receipts.Single(); var order = workspace.AllOrderRows.Single();
        receipt.IsSelected = true;
        var printing = ExecuteAsync(main.PrintSelectedCommand);
        try
        {
            await fixture.Printer.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            True(main.IsBusy); True(!workspace.ChooseReceiptForOrderCommand.CanExecute(order));
            workspace.ChooseReceiptForOrderCommand.Execute(order);
            Equal(0, fixture.Dialogs.ReceiptChoiceCalls);
            var rejected = false;
            try { await workspace.PreviewReceipt!(receipt); }
            catch (InvalidOperationException) { rejected = true; }
            True(rejected && main.IsBusy, "Picker preview must not reset the pending print operation's busy state.");
            True(fixture.Dialogs.PreviewId is null); Equal(0, fixture.Links.Saves);
        }
        finally { fixture.Printer.Gate.TrySetResult(true); await printing; }
        True(!main.IsBusy); Equal(1, fixture.Printer.Calls);
    }

    private static async Task OrderReceiptPickerPersistenceAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with { Total = 220m }];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 15500)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        var order = workspace.AllOrderRows.Single(); var receipt = main.Receipts.Single();
        True(order.HasNoReceiptLink); True(!receipt.IsSelected && !receipt.IsSelectedForOrders);
        var detailReads = fixture.Details.Calls; var fetches = fixture.Source.FetchCalls;
        fixture.Dialogs.ReceiptChoice = null;
        await ExecuteAsync(workspace.ChooseReceiptForOrderCommand, parameter: order);
        Equal(1, fixture.Dialogs.ReceiptChoiceCalls); Equal(order.Key, fixture.Dialogs.ReceiptChoiceOrderKey);
        True(fixture.Dialogs.ReceiptChoiceRows.Select(row => row.Id).SequenceEqual([ReceiptOne]));
        Equal(0, fixture.Links.Saves); True(order.HasNoReceiptLink);

        fixture.Dialogs.ReceiptChoice = ReceiptOne;
        await ExecuteAsync(workspace.ChooseReceiptForOrderCommand, parameter: order);
        Equal(1, fixture.Links.Saves); Equal(0, fixture.Dialogs.ChoiceCalls);
        Equal(ReceiptLinkState.Manual, receipt.OrderMatch!.State); Equal(order.Key, receipt.OrderMatch.Order!.Key);
        True(!order.HasNoReceiptLink); Equal(receipt, order.LinkedReceipts.Single());
        Equal(detailReads, fixture.Details.Calls); Equal(fetches, fixture.Source.FetchCalls);
        True(!receipt.IsSelected && !receipt.IsSelectedForOrders && !order.IsSelectedForLabels);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);

        await main.RefreshAsync(); await main.PrepareOrdersAsync();
        Equal(ReceiptLinkState.Manual, main.Receipts.Single().OrderMatch!.State);
        Equal(order.Key, main.Receipts.Single().OrderMatch!.Order!.Key);
        var (restarted, restartedWorkspace) = fixture.Create();
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        Equal(ReceiptLinkState.Manual, restarted.Receipts.Single().OrderMatch!.State);
        Equal(order.Key, restarted.Receipts.Single().OrderMatch!.Order!.Key);
        True(!restartedWorkspace.AllOrderRows.Single().HasNoReceiptLink); Equal(1, fixture.Links.Saves);
    }

    private static async Task OrderReceiptPickerHiddenFiltersAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with { Total = 999m }];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 15500), Receipt(ReceiptTwo, 2, 22000)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        var receipts = main.Receipts.ToArray(); var order = workspace.AllOrderRows.Single();
        receipts[0].IsSelected = true; receipts[1].IsSelectedForOrders = true; order.IsSelectedForLabels = true;
        main.AllReceiptsTab.SearchText = "outside-receipt-picker";
        main.OrdersReceiptsTab.SearchText = "outside-receipt-picker";
        workspace.Filter = "Rozetka";
        Equal(0, main.OrdersReceiptsTab.View.Cast<object>().Count());
        fixture.Dialogs.ReceiptChoice = ReceiptTwo;
        await ExecuteAsync(workspace.ChooseReceiptForOrderCommand, parameter: order);
        True(fixture.Dialogs.ReceiptChoiceRows.Select(row => row.Id).ToHashSet().SetEquals([ReceiptOne, ReceiptTwo]));
        Equal(ReceiptTwo, order.LinkedReceipts.Single().Id);
        True(receipts[0].IsSelected && !receipts[0].IsSelectedForOrders);
        True(!receipts[1].IsSelected && receipts[1].IsSelectedForOrders && order.IsSelectedForLabels);
        Equal("outside-receipt-picker", main.AllReceiptsTab.SearchText);
        Equal("outside-receipt-picker", main.OrdersReceiptsTab.SearchText); Equal("Rozetka", workspace.Filter);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static async Task OrderReceiptPickerReservationsAsync()
    {
        const string freeId = "unclaimed-picker-receipt";
        var fixture = new Fixture();
        var wanted = Order() with { Total = 999m };
        var exact = Order([ReceiptOne]) with { Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "EXACT-42" };
        var suggested = Order() with { Key = new(MarketplaceKind.Prom, "prom-test", "43"), Number = "SUGGESTED-43", Total = 155m };
        fixture.Source.Orders = [wanted, exact, suggested];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 10000), Receipt(ReceiptTwo, 2, 15500),
            Receipt(ReceiptThree, 3, 9000), Receipt(freeId, 4, 9500)];
        // A saved decision from an inactive shop still reserves its receipt, while
        // the current shop's completed coverage can establish the other two links.
        // An absent order in the enabled shop would correctly make its API check incomplete.
        await fixture.Links.SaveDecisionAsync(new() { AccountContext = PrintAccountContext.Create(fixture.Settings.App),
            ReceiptId = ReceiptThree, ConfirmedOrder = new(MarketplaceKind.Prom, "inactive-prom-shop", "unloaded-order"),
            UpdatedAtUtc = DateTimeOffset.UtcNow });
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        Equal(ReceiptLinkState.Exact, main.Receipts.Single(row => row.Id == ReceiptOne).OrderMatch!.State);
        Equal(ReceiptLinkState.Suggested, main.Receipts.Single(row => row.Id == ReceiptTwo).OrderMatch!.State);
        var reserved = main.Receipts.Single(row => row.Id == ReceiptThree);
        Equal(ReceiptLinkState.Manual, reserved.OrderMatch!.State); True(reserved.OrderMatch.Order is null);
        fixture.Dialogs.ReceiptChoice = freeId;
        var target = workspace.AllOrderRows.Single(row => row.Key == wanted.Key);
        await ExecuteAsync(workspace.ChooseReceiptForOrderCommand, parameter: target);
        True(fixture.Dialogs.ReceiptChoiceRows.Select(row => row.Id).SequenceEqual([freeId]));
        Equal(2, fixture.Links.Saves); Equal(freeId, target.LinkedReceipts.Single().Id);
        Equal(exact.Key, main.Receipts.Single(row => row.Id == ReceiptOne).OrderMatch!.Order!.Key);
        Equal(suggested.Key, main.Receipts.Single(row => row.Id == ReceiptTwo).OrderMatch!.Order!.Key);
        Equal(0, fixture.Printer.Calls);
    }

    private static async Task OrderReceiptPickerStaleChoiceAsync()
    {
        foreach (var change in new[] { "account", "receipt-refresh", "configuration", "receipt-claimed", "forged-choice" })
        {
            var fixture = new Fixture();
            fixture.Source.Orders = [Order() with { Total = 999m }];
            fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 15500)];
            var (main, workspace) = fixture.Create();
            await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
            var receipt = main.Receipts.Single(); var order = workspace.AllOrderRows.Single();
            fixture.Dialogs.ReceiptChoice = change == "forged-choice" ? "not-in-loaded-receipts" : ReceiptOne;
            fixture.Dialogs.DuringReceiptChoice = () =>
            {
                if (change == "account") workspace.InvalidateAccount();
                else if (change == "receipt-refresh") workspace.SetReceiptScope(main.Receipts.ToArray(),
                    PrintAccountContext.Create(fixture.Settings.App), new(2026, 9, 23), new(2026, 9, 23));
                else if (change == "configuration") workspace.InvalidateConfiguration();
                else if (change == "receipt-claimed") receipt.OrderMatch = new(ReceiptLinkState.Manual,
                    Order() with { Key = new(MarketplaceKind.Prom, "prom-test", "other-order") }, "Changed while the modal dispatcher was running", []);
            };
            await ExecuteAsync(workspace.ChooseReceiptForOrderCommand, parameter: order);
            Equal(1, fixture.Dialogs.ReceiptChoiceCalls); Equal(0, fixture.Links.Saves);
            Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
        }
    }

    private static async Task OrderReceiptPickerPreviewAsync()
    {
        var fixture = new Fixture();
        fixture.Source.Orders = [Order() with { Total = 999m }];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 15500), Receipt(ReceiptTwo, 2, 22000)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        var order = workspace.AllOrderRows.Single(); var receipt = main.Receipts.First();
        receipt.IsSelected = true; receipt.IsSelectedForOrders = true; order.IsSelectedForLabels = true;
        await ExecuteAsync(workspace.ChooseReceiptForOrderCommand, parameter: order);
        True(fixture.Dialogs.ReceiptChoicePreview is not null, "Main must wire its existing preview into the receipt picker.");
        var picker = new ReceiptPickerViewModel(order.Model, fixture.Dialogs.ReceiptChoiceRows, fixture.Dialogs.ReceiptChoicePreview);
        await ExecuteAsync(picker.PreviewCommand, parameter: receipt);
        Equal(receipt.Id, fixture.Dialogs.PreviewId);
        True(receipt.IsSelected && receipt.IsSelectedForOrders && order.IsSelectedForLabels);
        True(order.HasNoReceiptLink); Equal(0, fixture.Links.Saves);
        Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static Task ReceiptPickerFiltersAsync()
    {
        ReceiptRowViewModel Row(string id, int serial, long amount, DateTimeOffset? time, bool known = true, string code = "") => new(new()
        { Id = id, Serial = serial, Status = "DONE", Type = ReceiptTypes.Sell, TotalSumMinor = amount,
            TotalKnown = known, FiscalDate = time, FiscalCode = code });
        var first = Row("d73c9a71-bae6-4fc9-97db-f5b21e49aedf", 11, 120000,
            new(2026, 9, 22, 21, 30, 0, TimeSpan.Zero), code: "Fiscal-AbC");
        var late = Row("late-receipt", 12, 120010, new(2026, 9, 23, 20, 30, 0, TimeSpan.Zero));
        var next = Row("next-receipt", 13, 1200000, new(2026, 9, 23, 21, 30, 0, TimeSpan.Zero));
        var undated = Row("undated-receipt", 14, 0, null, known: false);
        Equal("—", undated.TotalDisplay);
        var picker = new ReceiptPickerViewModel(Order(), [first, late, next, undated]);
        string[] Visible() => picker.Receipts.Cast<ReceiptRowViewModel>().Select(row => row.Id).ToArray();
        Equal(4, Visible().Length);
        picker.DateFrom = new(2026, 9, 23, 18, 0, 0); picker.DateTo = new(2026, 9, 23);
        True(Visible().ToHashSet().SetEquals([first.Id, late.Id]), "Both ends use complete Kyiv calendar dates.");
        picker.SelectedReceipt = first;
        True(picker.LinkCommand.CanExecute(null));
        picker.SearchText = "late-receipt";
        Equal(late.Id, Visible().Single()); True(picker.SelectedReceipt is null); True(!picker.LinkCommand.CanExecute(null));
        picker.SearchText = "BAE6-4FC9"; Equal(first.Id, Visible().Single());
        picker.SearchText = "fiscal-abc"; Equal(first.Id, Visible().Single());
        picker.SearchText = "11"; Equal(first.Id, Visible().Single());
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "uk-UA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var query in new[] { "1200", "1200.00", "1200,00", "1 200,00", "1\u00a0200,00", "1,200.00", "1.200,00" })
                { picker.SearchText = query; Equal(first.Id, Visible().Single()); }
                picker.SearchText = "1200,10"; Equal(late.Id, Visible().Single());
                picker.SearchText = "1200.001"; Equal(0, Visible().Length);
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
        picker.SearchText = ""; picker.DateFrom = new(2026, 9, 24); picker.DateTo = new(2026, 9, 23);
        True(picker.HasInvalidDateRange); Equal(0, Visible().Length); True(!picker.LinkCommand.CanExecute(null));
        picker.DateFrom = null; picker.DateTo = null; picker.SearchText = "0,00"; Equal(0, Visible().Length);
        picker.SearchText = first.Id; picker.SelectedReceipt = first;
        string? selectedId = null; picker.ChoiceRequested += id => selectedId = id;
        picker.LinkCommand.Execute(null); Equal(first.Id, selectedId);
        return Task.CompletedTask;
    }

    private static async Task ReceiptPickerPreviewFailureAsync()
    {
        var receipt = new ReceiptRowViewModel(Receipt(ReceiptOne, 1, 15500)) { IsSelected = true, IsSelectedForOrders = true };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picker = new ReceiptPickerViewModel(Order(), [receipt], async _ =>
        { entered.TrySetResult(); await gate.Task; throw new InvalidOperationException("Synthetic preview failure"); });
        picker.SelectedReceipt = receipt; var accepted = 0; picker.ChoiceRequested += _ => accepted++;
        var pending = ExecuteAsync(picker.PreviewCommand, parameter: receipt);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        True(picker.IsPreviewBusy); True(!picker.CanEditFilters); True(!picker.LinkCommand.CanExecute(null));
        gate.SetResult(); await pending;
        True(!picker.IsPreviewBusy); True(picker.CanEditFilters); True(!string.IsNullOrWhiteSpace(picker.PreviewError));
        Equal(0, accepted); True(receipt.IsSelected && receipt.IsSelectedForOrders);
        True(picker.LinkCommand.CanExecute(null));
    }

    private static async Task ReceiptPickerDashboardAsync()
    {
        _xamlCheckpoint = "order row receipt picker and compiled popup bindings";
        var fixture = new Fixture();
        var wanted = Order() with { Total = 220m };
        var linked = Order([ReceiptTwo]) with { Key = new(MarketplaceKind.Prom, "prom-test", "42"), Number = "ORDER-42" };
        fixture.Source.Orders = [wanted, linked];
        fixture.ReceiptSource.Rows = [Receipt(ReceiptOne, 1, 15500), Receipt(ReceiptTwo, 2, 10000)];
        var (main, workspace) = fixture.Create();
        await main.RefreshAsync(); await OpenOrdersAsync(main, workspace);
        main.CoordinatorManaged = true;
        var mainWindow = new MainWindow { DataContext = main };
        var first = main.Receipts.Single(row => row.Id == ReceiptOne);
        var second = main.Receipts.Single(row => row.Id == ReceiptTwo);
        var picker = new ReceiptPickerViewModel(wanted, [first, second], workspace.PreviewReceipt);
        var window = new ReceiptPickerWindow(picker);
        var failures = new BindingErrorListener();
        var priorTraceLevel = PresentationTraceSources.DataBindingSource.Switch.Level;
        PresentationTraceSources.DataBindingSource.Listeners.Add(failures);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            static IEnumerable<Button> CellButtons(FrameworkElement cell) => cell is Button button
                ? new[] { button }.Concat(VisualChildren<Button>(cell)) : VisualChildren<Button>(cell);
            async Task<FrameworkElement> RealizeCell(Window owner, DataGrid dataGrid, object row,
                DataGridColumn column, bool needsButtons = false)
            {
                True(dataGrid.Items.Contains(row), "Expected picker/dashboard row is outside its view.");
                ScrollViewer? scroll = null;
                for (var attempt = 0; attempt < 6; attempt++)
                {
                    LayoutContent(owner);
                    dataGrid.ScrollIntoView(row, column);
                    await DrainDispatcherAsync();
                    LayoutContent(owner); dataGrid.UpdateLayout();
                    if (column.GetCellContent(row) is { } cell)
                    {
                        cell.ApplyTemplate(); cell.UpdateLayout();
                        if (!needsButtons || CellButtons(cell).Any()) return cell;
                    }
                    scroll ??= dataGrid.Template.FindName("DG_ScrollViewer", dataGrid) as ScrollViewer
                        ?? VisualChildren<ScrollViewer>(dataGrid).FirstOrDefault();
                    if (scroll is not null)
                    {
                        var index = dataGrid.Items.IndexOf(row);
                        var itemUnits = scroll.CanContentScroll && VirtualizingPanel.GetScrollUnit(dataGrid) == ScrollUnit.Item;
                        var measured = VisualChildren<DataGridRow>(dataGrid).Select(item => item.ActualHeight).FirstOrDefault(height => height > 0);
                        var height = double.IsNaN(dataGrid.RowHeight) ? measured > 0 ? measured : 28 : dataGrid.RowHeight;
                        scroll.ScrollToVerticalOffset(itemUnits ? index : index * height);
                    }
                    await DrainDispatcherAsync();
                }
                throw new InvalidOperationException($"Picker/dashboard column {column.DisplayIndex} was not realized; " +
                    $"items: {dataGrid.Items.Count}; size: {dataGrid.ActualWidth}x{dataGrid.ActualHeight}; " +
                    $"viewport: {scroll?.ViewportHeight}; offset: {scroll?.VerticalOffset}.");
            }
            LayoutContent(window); await DrainDispatcherAsync();
            var pickerContent = (DependencyObject)window.Content;
            var grid = VisualChildren<DataGrid>(pickerContent).Single();
            True(ReferenceEquals(picker.Receipts, grid.ItemsSource));
            var dateColumn = (DataGridTextColumn)grid.Columns.Single(column => column.Header?.ToString() == "Дата й час");
            Equal(nameof(ReceiptRowViewModel.LocalDateTime), ((Binding)dateColumn.Binding).Path.Path);
            var amountColumn = (DataGridTextColumn)grid.Columns.Single(column => column.Header?.ToString() == "Сума");
            Equal(nameof(ReceiptRowViewModel.TotalDisplay), ((Binding)amountColumn.Binding).Path.Path);
            True(grid.Columns.OfType<DataGridTextColumn>().Any(column => ((Binding)column.Binding).Path.Path == nameof(ReceiptRowViewModel.Id)));
            Equal(FontWeights.Bold, ((TextBlock)await RealizeCell(window, grid, first, amountColumn)).FontWeight);
            var previewColumn = grid.Columns[amountColumn.DisplayIndex + 1];
            var preview = CellButtons(await RealizeCell(window, grid, first, previewColumn, needsButtons: true)).Single();
            True(ReferenceEquals(picker.PreviewCommand, preview.Command) && ReferenceEquals(first, preview.CommandParameter));
            await ExecuteAsync(preview.Command!, parameter: preview.CommandParameter);
            Equal(ReceiptOne, fixture.Dialogs.PreviewId);

            var dates = VisualChildren<DatePicker>(pickerContent).ToArray(); Equal(2, dates.Length);
            var from = dates.Single(control => control.GetBindingExpression(DatePicker.SelectedDateProperty)?.ParentBinding.Path.Path == nameof(ReceiptPickerViewModel.DateFrom));
            var to = dates.Single(control => control.GetBindingExpression(DatePicker.SelectedDateProperty)?.ParentBinding.Path.Path == nameof(ReceiptPickerViewModel.DateTo));
            from.SetCurrentValue(DatePicker.SelectedDateProperty, new DateTime(2026, 9, 23));
            to.SetCurrentValue(DatePicker.SelectedDateProperty, new DateTime(2026, 9, 23));
            from.GetBindingExpression(DatePicker.SelectedDateProperty)!.UpdateSource();
            to.GetBindingExpression(DatePicker.SelectedDateProperty)!.UpdateSource();
            Equal<DateTime?>(new(2026, 9, 23), picker.DateFrom); Equal<DateTime?>(new(2026, 9, 23), picker.DateTo);
            var search = VisualChildren<TextBox>(pickerContent).Single(control =>
                control.GetBindingExpression(TextBox.TextProperty)?.ParentBinding.Path.Path == nameof(ReceiptPickerViewModel.SearchText));
            search.SetCurrentValue(TextBox.TextProperty, "155,00");
            search.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            LayoutContent(window); await DrainDispatcherAsync();
            Equal("155,00", picker.SearchText); Equal(ReceiptOne, grid.Items.Cast<ReceiptRowViewModel>().Single().Id);
            var confirm = VisualChildren<Button>(pickerContent).Single(control => ReferenceEquals(picker.LinkCommand, control.Command));
            True(!confirm.IsEnabled);
            grid.SelectedItem = first; LayoutContent(window); await DrainDispatcherAsync();
            True(ReferenceEquals(first, picker.SelectedReceipt) && confirm.IsEnabled);
            RenderSyntheticContent(window, "receipt-picker.png");

            main.SelectedTabIndex = 1;
            LayoutContent(mainWindow); await DrainDispatcherAsync();
            var ordersGrid = (DataGrid)mainWindow.FindName("MarketplaceOrdersGrid");
            var target = workspace.AllOrderRows.Single(row => row.Key == wanted.Key);
            var alreadyLinked = workspace.AllOrderRows.Single(row => row.Key == linked.Key);
            var linkColumn = ordersGrid.Columns.Single(column => column.Header?.ToString() == "Відкрити чек");
            var rowButtons = CellButtons(await RealizeCell(mainWindow, ordersGrid, target, linkColumn, needsButtons: true)).ToArray();
            var choose = rowButtons.Single(button => Equals(button.Tag, "ChooseReceiptForOrder"));
            Equal(Visibility.Visible, choose.Visibility); Equal("Прив’язати", choose.Content?.ToString());
            True(ReferenceEquals(workspace.ChooseReceiptForOrderCommand, choose.Command) && ReferenceEquals(target, choose.CommandParameter));
            var linkedButtons = CellButtons(await RealizeCell(mainWindow, ordersGrid, alreadyLinked, linkColumn, needsButtons: true)).ToArray();
            Equal(Visibility.Collapsed, linkedButtons.Single(button => Equals(button.Tag, "ChooseReceiptForOrder")).Visibility);
            True(linkedButtons.Any(button => ReferenceEquals(main.PreviewCommand, button.Command) && ReferenceEquals(second, button.CommandParameter)));
            fixture.Dialogs.ReceiptChoice = ReceiptOne;
            await ExecuteAsync(choose.Command!, parameter: choose.CommandParameter);
            rowButtons = CellButtons(await RealizeCell(mainWindow, ordersGrid, target, linkColumn, needsButtons: true)).ToArray();
            Equal(Visibility.Collapsed, rowButtons.Single(button => Equals(button.Tag, "ChooseReceiptForOrder")).Visibility);
            True(rowButtons.Any(button => ReferenceEquals(main.PreviewCommand, button.Command) && ReferenceEquals(first, button.CommandParameter)));
            RenderSyntheticContent(mainWindow, "order-inline-receipt-picker.png");
            True(!first.IsSelected && !first.IsSelectedForOrders); Equal(0, fixture.Printer.Calls);
            True(failures.Messages.Count == 0, "Receipt picker binding errors: " + string.Join("\n", failures.Messages));
        }
        finally
        {
            main.CancelPendingOperations(); window.Close(); mainWindow.Close(); await DrainDispatcherAsync();
            PresentationTraceSources.DataBindingSource.Listeners.Remove(failures);
            PresentationTraceSources.DataBindingSource.Switch.Level = priorTraceLevel;
        }
    }
}

using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task ProblemOrdersArchiveAsync()
    {
        var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        fixture.Source.Orders =
        [
            Order() with { Key = Order().Key with { OrderId = "missing" }, Number = "missing", Status = "Прийнято", CreatedAt = now.AddDays(-3), Shipments = [], ReceiptIds = [] },
            Order() with { Key = Order().Key with { OrderId = "storage" }, Number = "storage", Status = "Прийнято", CreatedAt = now.AddDays(-7), Shipments = [new("nova_poshta", "20400554610503")] },
            Order() with { Key = Order().Key with { OrderId = "canceled" }, Number = "canceled", CreatedAt = now.AddDays(-5), Status = "canceled", Shipments = [] }
        ];
        var tracking = new DashboardTrackingSource { Read = (orders, _) => Task.FromResult(new ShipmentTrackingFetchResult(
            orders.SelectMany(o => o.Shipments.Select(s => new ShipmentTrackingStatus(ShippingCarrier.NovaPoshta, s.TrackingNumber,
                "Прибув у відділення", "Нова пошта", now) { StatusCode = "7", ArrivedAt = now.AddDays(-5), PaidStorageStartsOn = DateRangeBuilder.KyivDate(now).AddDays(1) })).ToArray(), [])) };
        var (main, workspace) = CreateTrackingDashboard(fixture, tracking);
        try
        {
            await main.RefreshAllAsync();
            workspace.SetDisplayDates(DateRangeBuilder.KyivDate(now), DateRangeBuilder.KyivDate(now));
            Equal(0, workspace.Orders.Cast<object>().Count());
            main.SelectedTabIndex = 3;
            await main.RefreshShipmentTrackingAsync(force: true);
            workspace.RefreshProblems(now);
            var problems = workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().ToArray();
            True(problems.Select(p => p.Number).SequenceEqual(["storage", "missing"]), "Unexpected problems: " + string.Join("; ", problems.Select(p => p.Number + ": " + p.ProblemReason)));
            True(problems[0].ProblemReason.Contains("платне з") && problems[0].ProblemReason.Contains("5 дн."), problems[0].ProblemReason);
            True(problems[1].ProblemReason.Contains("чека і ТТН"), problems[1].ProblemReason);
            True(tracking.Requests.Last().Any(o => o.Number == "storage"), "Hidden archive parcel was not tracked.");
            True(!main.IsReceiptTab && !main.IsWaybillsTab && !main.PrintSelectedCommand.CanExecute(null));
            main.SearchText = "storage";
            Equal(1, workspace.ProblemOrders.Cast<object>().Count());
            Equal("", main.AllReceiptsTab.SearchText);
            main.SearchText = "";
            workspace.ProblemFilter = "Понад 24 год без чека / ТТН";
            Equal("missing", workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Single().Number);
            True(workspace.ChooseReceiptForOrderCommand.CanExecute(problems[1]), "Problem orders outside calendar cannot use receipt picker.");
            workspace.ProblemFilter = "Усі";
            problems[0].UpdateTrackingStatuses([new(problems[0].Model.Shipments[0], "Отримано", "Нова пошта", now)
                { Details = tracking.Requests.Last().SelectMany(o => o.Shipments).Select(s => new ShipmentTrackingStatus(ShippingCarrier.NovaPoshta, s.TrackingNumber, "Отримано", "Нова пошта", now) { StatusCode = "9" }).First() }]);
            workspace.RefreshProblems(now);
            Equal("missing", workspace.ProblemOrders.Cast<MarketplaceOrderRowViewModel>().Single().Number);
        }
        finally { main.CancelPendingOperations(); }
    }

    private static async Task ProblemOrdersUiAsync()
    {
        _xamlCheckpoint = "problem orders tab archive filter receipt action and layout";
        var fixture = new Fixture();
        var now = DateTimeOffset.UtcNow;
        fixture.Source.Orders =
        [
            Order() with { Key = Order().Key with { OrderId = "missing-ui" }, Number = "433169320", CreatedAt = now.AddDays(-7), Shipments = [], ReceiptIds = [],
                Total = 141, Buyer = new("Тестовий покупець"), PaymentMethod = "Оплата на рахунок", Status = "Прийнято" },
            Order() with { Key = Order().Key with { OrderId = "storage-ui" }, Number = "433299949", CreatedAt = now.AddDays(-8), ReceiptIds = [],
                Shipments = [new("nova_poshta", " 20400554610503 "), new("rozetka_delivery", "RMP-123456")], Status = "Прийнято" }
        ];
        var (main, workspace) = CreateTrackingDashboard(fixture);
        await main.RefreshAllAsync();
        main.CoordinatorManaged = true;
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800, ShowInTaskbar = false };
        try
        {
            window.Show();
            main.SelectedTabIndex = 3;
            await DrainDispatcherAsync();
            var storage = workspace.AllOrderRows.Single(row => row.Key.OrderId == "storage-ui");
            storage.UpdateTrackingStatuses(storage.Model.Shipments.Select(shipment => new OrderShipmentTrackingStatus(
                shipment, "Прибув у відділення", "Тестовий перевізник", now)
                { Details = new(ShippingCarrierNames.FromDocumentedDeliveryField(shipment.Carrier), shipment.TrackingNumber.Trim(),
                    "Прибув у відділення", "Тестовий перевізник", now) { StatusCode = "7", ArrivedAt = now.AddDays(-5) } }));
            workspace.RefreshProblems(now);
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName("ProblemOrdersGrid");
            True(grid.IsVisible && grid.Items.Count == 2);
            True(!((FrameworkElement)window.FindName("ReceiptPrintFooter")).IsVisible);
            Equal(4, ((TabControl)window.FindName("ReceiptTabs")).Items.Count);
            True(grid.Columns.Any(c => c.Header?.ToString() == "Причина"));
            var period = (ComboBox)window.FindName("ProblemPeriodFilter");
            Equal("Останні 14 днів", period.SelectedItem?.ToString());
            period.SelectedItem = "За весь час";
            await DrainDispatcherAsync();
            Equal("За весь час", workspace.ProblemPeriod);
            period.SelectedItem = "Останні 14 днів";
            await DrainDispatcherAsync();
            window.UpdateLayout();
            Equal("Останні 14 днів", period.SelectedItem?.ToString());
            Equal(2, grid.Items.Count);
            var trackingColumn = grid.Columns.Single(column => column.Header?.ToString() == "ТТН");
            var statusColumn = grid.Columns.Single(column => column.Header?.ToString() == "Статус доставки");
            True(!grid.Columns.Any(column => column.Header?.ToString() == "ТТН / статус доставки"));
            grid.ScrollIntoView(storage, trackingColumn);
            await DrainDispatcherAsync(); window.UpdateLayout();
            var copies = VisualChildren<Button>(trackingColumn.GetCellContent(storage))
                .Where(button => Equals(button.Tag, "ProblemTrackingCopy")).ToArray();
            True(copies.Select(button => button.Content?.ToString()).SequenceEqual(["20400554610503", "RMP-123456"]));
            var oldLink = (OrderShipmentLink)copies[0].CommandParameter;
            foreach (var button in copies)
            {
                True(ReferenceEquals(button.Command, workspace.CopyShipmentTrackingCommand));
                var link = (OrderShipmentLink)button.CommandParameter;
                True(ReferenceEquals(link.Row, storage) && button.Command!.CanExecute(link));
                button.Command!.Execute(link);
                Equal(link.TrackingNumber, fixture.Dialogs.Copied);
            }
            var missing = workspace.AllOrderRows.Single(row => row.Key.OrderId == "missing-ui");
            grid.ScrollIntoView(missing, trackingColumn);
            await DrainDispatcherAsync(); window.UpdateLayout();
            True(!VisualChildren<Button>(trackingColumn.GetCellContent(missing)).Any(button => Equals(button.Tag, "ProblemTrackingCopy")));
            grid.ScrollIntoView(storage, statusColumn);
            await DrainDispatcherAsync(); window.UpdateLayout();
            var statuses = VisualChildren<CheckboxBatchPrinter.Controls.DashboardTextBox>(statusColumn.GetCellContent(storage))
                .Where(text => Equals(text.Tag, "ProblemTrackingStatus")).ToArray();
            Equal(2, statuses.Length);
            True(statuses.All(text => text.Text == "Прибув у відділення" && text.ToolTip?.ToString()?.Contains("Тестовий перевізник") == true));
            grid.ScrollIntoView(storage, grid.Columns[0]);
            await DrainDispatcherAsync(); window.UpdateLayout();
            // A deterministic render for review; no real profile or printer is involved.
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts/problem-orders-preview");
            using var file = File.Create("artifacts/problem-orders-preview/dashboard.png"); encoder.Save(file);
            storage.Update(storage.Model with { Shipments = [new("nova_poshta", "20400554610504")] }, [], []);
            fixture.Dialogs.Copied = "unchanged";
            True(!workspace.CopyShipmentTrackingCommand.CanExecute(oldLink));
            workspace.CopyShipmentTrackingCommand.Execute(oldLink);
            Equal("unchanged", fixture.Dialogs.Copied);
        }
        finally { main.CancelPendingOperations(); window.Close(); }
    }
}

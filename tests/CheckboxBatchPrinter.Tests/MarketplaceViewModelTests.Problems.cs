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
        fixture.Source.Orders = [Order() with { Number = "433169320", CreatedAt = now.AddDays(-7), Shipments = [], ReceiptIds = [],
            Total = 141, Buyer = new("Тестовий покупець"), PaymentMethod = "Оплата на рахунок", Status = "Прийнято" }];
        var (main, workspace) = CreateTrackingDashboard(fixture);
        await main.RefreshAllAsync();
        main.CoordinatorManaged = true;
        var window = new MainWindow { DataContext = main, Width = 1280, Height = 800, ShowInTaskbar = false };
        try
        {
            window.Show();
            main.SelectedTabIndex = 3;
            await DrainDispatcherAsync();
            workspace.RefreshProblems(now);
            window.UpdateLayout();
            var grid = (DataGrid)window.FindName("ProblemOrdersGrid");
            True(grid.IsVisible && grid.Items.Count == 1);
            True(!((FrameworkElement)window.FindName("ReceiptPrintFooter")).IsVisible);
            Equal(4, ((TabControl)window.FindName("ReceiptTabs")).Items.Count);
            True(grid.Columns.Any(c => c.Header?.ToString() == "Причина"));
            // A deterministic render for review; no real profile or printer is involved.
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory("artifacts/problem-orders-preview");
            using var file = File.Create("artifacts/problem-orders-preview/dashboard.png"); encoder.Save(file);
        }
        finally { main.CancelPendingOperations(); window.Close(); }
    }
}

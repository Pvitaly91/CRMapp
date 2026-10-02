using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task NameBeforePaymentUiAsync()
    {
        var fixture = new Fixture(); var cache = new AutomaticCache();
        var charger = Order() with { Total = 280, PaymentMethod = "Післяплата", DeliveryMethod = "Нова Пошта",
            Shipments = [new("nova_poshta", "")], ItemListComplete = true,
            Items = [new("Накладна USB зарядка 12–24V на 4 порти", "", 1, 280, 280)] };
        var batterySwitch = charger with { Key = charger.Key with { OrderId = "42" }, Number = "ORDER-42", PaymentMethod = "Пром-оплата",
            Items = [new("Вимикач маси АКБ 12/24V 500A, прозорий корпус", "", 1, 280, 280)] };
        fixture.Source.Orders = [charger, batterySwitch];
        // Opposite payment labels deliberately cannot swap the product-identified pairs.
        fixture.ReceiptSource.Rows = [PaymentReceipt(ReceiptOne, 1, "Платіж RozetkaPay"), PaymentReceipt(ReceiptTwo, 2, "Платіж NovaPay")];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, charger.Items) { ItemListComplete = true };
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, batterySwitch.Items) { ItemListComplete = true };
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync(); main.Receipts[0].IsSelected = true; await OpenOrdersAsync(main, workspace);
        foreach (var row in main.Receipts)
        {
            Equal(row.Id == ReceiptOne ? charger.Key : batterySwitch.Key, row.OrderMatch!.Order!.Key);
            Equal(AutomaticLinkBasis.AmountAndProducts, row.OrderMatch.Basis);
        }
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(o => o.HasSuggestedLink && !o.HasConfirmedLink && o.LinkStatus.Contains("товари")));
        True(main.Receipts[0].IsSelected); Equal(2, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
        var (restarted, _) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        True(restarted.Receipts.All(r => r.OrderMatch?.Basis == AutomaticLinkBasis.AmountAndProducts));
        Equal(charger.Key, restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.Order!.Key);
        Equal(batterySwitch.Key, restarted.Receipts.Single(r => r.Id == ReceiptTwo).OrderMatch!.Order!.Key);
        Equal(2, fixture.Details.Calls); Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static async Task PaymentAutomaticUiAsync()
    {
        var fixture = new Fixture(); var cache = new AutomaticCache();
        var cod = Order() with { Total = 280, PaymentMethod = "Наложенный платеж", DeliveryMethod = "Нова Пошта",
            Shipments = [new("nova_poshta", "")], Items = [new("Запасна кришка", "", 1, 280, 280)], ItemListComplete = true };
        var online = cod with { Key = cod.Key with { OrderId = "42" }, Number = "ORDER-42", PaymentMethod = "Пром-оплата" };
        fixture.Source.Orders = [cod, online];
        fixture.ReceiptSource.Rows = [PaymentReceipt(ReceiptOne, 1, "Платіж NovaPay"), PaymentReceipt(ReceiptTwo, 2, "Платіж RozetkaPay")];
        fixture.Details.Values[ReceiptOne] = new(ReceiptOne, cod.Items) { ItemListComplete = true };
        fixture.Details.Values[ReceiptTwo] = new(ReceiptTwo, online.Items) { ItemListComplete = true };
        var (main, workspace) = fixture.Create(automaticCache: cache);
        await main.RefreshAsync(); main.Receipts[0].IsSelected = true; await OpenOrdersAsync(main, workspace);
        foreach (var row in main.Receipts)
        {
            Equal(row.Id == ReceiptOne ? cod.Key : online.Key, row.OrderMatch!.Order!.Key);
            Equal("Автозв’язок: сума й оплата", row.LinkStatus); Equal(PaymentComparison.Match, row.OrderMatch.Payments);
        }
        True(main.Receipts[0].IsSelected); Equal(2, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
        True(workspace.Orders.Cast<MarketplaceOrderRowViewModel>().All(o => o.HasSuggestedLink && !o.HasConfirmedLink && o.LinkStatus.Contains("оплата")));
        Equal(2, fixture.Details.Calls);
        var (restarted, restored) = fixture.Create(automaticCache: cache);
        await restarted.RefreshAsync(); restarted.SelectedTabIndex = 1; await restarted.PrepareOrdersAsync();
        True(restarted.Receipts.All(r => r.OrderMatch?.Basis == AutomaticLinkBasis.AmountAndPayment && r.OrderMatch.Payments == PaymentComparison.Match));
        Equal(2, fixture.Details.Calls);
        fixture.Source.Orders = [cod, online, cod with { Key = cod.Key with { OrderId = "43" }, Number = "HIDDEN" }];
        restored.OrderSearch = "ORDER";
        await ExecuteAsync(restored.AutoMatchCommand);
        Equal(cod.Key, restarted.Receipts.Single(r => r.Id == ReceiptOne).OrderMatch!.Order!.Key);
        Equal(online.Key, restarted.Receipts.Single(r => r.Id == ReceiptTwo).OrderMatch!.Order!.Key);
        Equal(2, fixture.Details.Calls); Equal(0, fixture.Links.Saves); Equal(0, fixture.Printer.Calls); Equal(0, fixture.History.Saves);
    }

    private static ReceiptRecord PaymentReceipt(string id, int serial, string label)
    {
        var record = Receipt(id, serial, 28000);
        return new() { Id = id, Serial = serial, Type = record.Type, Status = record.Status, FiscalDate = record.FiscalDate,
            TotalSumMinor = record.TotalSumMinor, Payments = [new("CASHLESS", label, 28000)] };
    }
}

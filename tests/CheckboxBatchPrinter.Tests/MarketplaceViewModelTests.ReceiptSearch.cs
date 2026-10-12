using System.Globalization;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static async Task ReceiptAmountSearchAsync()
    {
        var fixture = new Fixture();
        fixture.ReceiptSource.Rows = [Receipt("receipt-alpha", 1, 120000),
            Receipt("receipt-beta", 2, 120010), Receipt("receipt-gamma", 3, 1200000)];
        var (main, _) = fixture.Create(); await main.RefreshAsync();
        var original = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "en-US", "uk-UA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                foreach (var text in new[] { "1200", "1200.00", "1200,00", "1 200", "1 200.00", "1 200,00",
                    "1\u00a0200,00", "1\u202f200.00", "1,200.00", "1.200,00", "1,200", "1.200", " 1200,00 " })
                {
                    main.SearchText = text;
                    True(main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Select(row => row.Id).SequenceEqual(["receipt-alpha"]),
                        $"Exact amount search differs for {text} under {culture}.");
                }
                foreach (var text in new[] { "1200.1", "1200,10" })
                {
                    main.SearchText = text;
                    Equal("receipt-beta", main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Single().Id);
                }
                foreach (var text in new[] { "1200.001", "12 00", "1,20,0.00", "1e3", new string('9', 80) })
                {
                    main.SearchText = text;
                    Equal(0, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
                }
            }
            main.SearchText = "";
            Equal(3, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
        }
        finally { CultureInfo.CurrentCulture = original; }
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Printer.Calls);
    }

    private static async Task ReceiptIdSearchAsync()
    {
        const string id = "d73c9a71-bae6-4fc9-97db-f5b21e49aedf";
        var fixture = new Fixture();
        fixture.ReceiptSource.Rows = [Receipt(id, 1, 32000), Receipt("unrelated-receipt", 2, 10000)];
        var (main, _) = fixture.Create(); await main.RefreshAsync();
        foreach (var query in new[] { id.ToUpperInvariant(), "BAE6-4FC9", "320", "320,00" })
        {
            main.SearchText = query;
            Equal(id, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Single().Id);
        }
        main.SearchText = "Продаж";
        Equal(2, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
        main.SearchText = "2";
        True(main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Any(row => row.Serial == "2"));
        main.SearchText = "does-not-exist";
        Equal(0, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Printer.Calls);
    }

    private static async Task ReceiptAmountSearchPrintAsync()
    {
        ReceiptRecord Row(string id, long serial, string type = "SELL", int day = 23, bool known = true, long total = 10000) => new()
        {
            Id = id, Serial = serial, Type = type, Status = "DONE", TotalSumMinor = total, TotalKnown = known,
            FiscalDate = new DateTimeOffset(2026, 9, day, 12, 0, 0, TimeSpan.FromHours(3))
        };
        var fixture = new Fixture();
        fixture.ReceiptSource.Rows = [Row("alpha", 1), Row("beta", 2, "RETURN"), Row("unknown", 3, known: false),
            Row("next-day", 4, day: 24), Row("unknown-zero", 5, known: false, total: 0)];
        var (main, _) = fixture.Create(); await main.RefreshAsync();
        main.SearchText = "0,00";
        Equal(0, main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Count());
        main.SearchText = "100,00";
        True(main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Select(row => row.Id).ToHashSet().SetEquals(["alpha", "beta"]));
        main.Receipts.Single(row => row.Id == "alpha").IsSelected = true;
        main.Receipts.Single(row => row.Id == "beta").IsSelected = true;
        main.DateFrom = main.DateTo = new(2026, 9, 24);
        Equal("next-day", main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Single().Id);
        main.DateFrom = main.DateTo = new(2026, 9, 23);
        main.SelectedType = main.ReceiptTypes.Single(type => type.Value == "RETURN");
        Equal("beta", main.AllReceiptsTab.View.Cast<ReceiptRowViewModel>().Single().Id);
        Equal(1, main.VisibleSelectedCount); Equal(1, main.HiddenSelectedCount);
        await ExecuteAsync(main.PrintSelectedCommand);
        True(fixture.Printer.Documents.Select(document => document.ReceiptId).SequenceEqual(["beta"]));
        True(main.Receipts.Single(row => row.Id == "alpha").IsSelected);
        True(!main.Receipts.Single(row => row.Id == "beta").IsSelected);
        Equal(0, fixture.Source.FetchCalls); Equal(0, fixture.Dialogs.Errors.Count);
    }
}

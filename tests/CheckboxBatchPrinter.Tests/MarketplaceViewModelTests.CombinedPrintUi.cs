using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Tests;

internal static partial class MarketplaceViewModelTests
{
    private static Task CombinedPrintPreviewUiAsync()
    {
        var first = new ShipmentReference(new(MarketplaceKind.Prom, "synthetic-prom", "ORDER-Z"),
            "ORDER-Z", ShippingCarrier.NovaPoshta, "20400000000002");
        var second = new ShipmentReference(new(MarketplaceKind.Rozetka, "synthetic-rozetka", "ORDER-A"),
            "ORDER-A", ShippingCarrier.NovaPoshta, "20400000000001");
        var firstPng = CombinedPreviewPng(first.OrderNumber, first.TrackingNumber);
        var secondPng = CombinedPreviewPng(second.OrderNumber, second.TrackingNumber);
        var batch = new LabelPrintBatch("synthetic-combined-preview",
        [
            new(first, "label-z", "fingerprint-z", 1, 100, 100, firstPng),
            new(second, "label-a", "fingerprint-a", 1, 100, 100, secondPng)
        ], ShippingLabelsTests.Settings) { SelectedOrderCount = 2 };
        var receipts = new PrintBatchConfirmation("synthetic-receipt-printer", 1,
        [
            new(1, ReceiptTwo, "901", "Prom", first.OrderNumber, first.TrackingNumber),
            new(2, ReceiptOne, "702", "Rozetka", second.OrderNumber, second.TrackingNumber)
        ]);
        var window = new LabelPreviewWindow(batch, ShippingLabelsTests.Geometry, [], true, new(receipts, 1));
        try
        {
            LayoutContent(window);
            Equal("Друк наклейок і чеків", window.Title);
            var content = (FrameworkElement)window.Content;
            var text = VisualChildren<TextBlock>(content).Select(block => block.Text).ToArray();
            var joined = string.Join("\n", text);
            True(joined.Contains("Принтер наклейок: synthetic-label-printer") &&
                joined.Contains("Принтер чеків: synthetic-receipt-printer"), "Both target printers must be visible in one confirmation.");
            True(joined.Contains("Замовлень: 2") && joined.Contains("етикеток: 2") && joined.Contains("чеків: 2"),
                "Combined confirmation must display both prepared batch counts.");
            True(joined.Contains("Прихованих вибраних замовлень (не включені): 1"));
            var pairing = text.Where(line => line.StartsWith("1. ", StringComparison.Ordinal) ||
                line.StartsWith("2. ", StringComparison.Ordinal)).ToArray();
            Equal(2, pairing.Length);
            True(pairing[0].Contains("ORDER-Z") && pairing[0].Contains("901") && pairing[0].Contains(first.TrackingNumber));
            True(pairing[1].Contains("ORDER-A") && pairing[1].Contains("702") && pairing[1].Contains(second.TrackingNumber),
                "Pairing summary must preserve frozen dashboard order rather than receipt or order number order.");
            True(VisualChildren<Expander>(content).Single(expander => expander.Header?.ToString() ==
                "Порядок друку: замовлення, ТТН і чеки").IsExpanded);
            Equal(2, VisualChildren<Image>(content).Count(image => image.Source is WriteableBitmap));
            var print = VisualChildren<Button>(content).Single(button => button.Content?.ToString() == "Друк наклейок і чеків");
            var repeat = VisualChildren<CheckBox>(content).Single();
            True(repeat.Visibility == Visibility.Visible && !print.IsEnabled,
                "Previously submitted receipts must block the shared confirmation even when no labels were submitted.");
            True(repeat.Content?.ToString()?.Contains("наклейок і чеків") == true);
            ClickCheckBox(repeat);
            True(repeat.IsChecked == true && print.IsEnabled);
            ClickCheckBox(repeat);
            True(repeat.IsChecked == false && !print.IsEnabled);
            ClickCheckBox(repeat);
            LayoutContent(window);
            RenderSyntheticContent(window, "combined-print-preview.png");
        }
        finally { window.Close(); }

        var previous = new LabelPrintAttempt("synthetic-attempt", "synthetic-job", batch.Settings.PrinterName,
            new DateTimeOffset(2026, 10, 11, 12, 0, 0, TimeSpan.Zero), LabelSubmissionState.Submitted, []);
        var labelRepeat = new LabelPreviewWindow(batch, ShippingLabelsTests.Geometry, [previous], true, new(receipts, 0));
        try
        {
            LayoutContent(labelRepeat);
            var content = (FrameworkElement)labelRepeat.Content;
            var print = VisualChildren<Button>(content).Single(button => button.Content?.ToString() == "Друк наклейок і чеків");
            var repeat = VisualChildren<CheckBox>(content).Single();
            True(!print.IsEnabled && repeat.Visibility == Visibility.Visible,
                "Previously submitted labels must also block the shared confirmation.");
            ClickCheckBox(repeat); True(print.IsEnabled);
        }
        finally { labelRepeat.Close(); }

        // The pure builder checks every document without opening a real printer. Production
        // preflight additionally checks these bounded pages against the selected driver.
        var settings = new AppSettings { PrinterName = "synthetic-nonexistent-printer" };
        _ = WindowsPrintService.BuildBatchDocument([new(firstPng, ReceiptTwo, first.OrderNumber, first.TrackingNumber),
            new(secondPng, ReceiptOne, second.OrderNumber, second.TrackingNumber)], settings, 1000);
        var corruptRejected = false;
        try
        {
            _ = WindowsPrintService.BuildBatchDocument([new(firstPng, ReceiptTwo), new([1, 2, 3], ReceiptOne)], settings, 1000);
        }
        catch (Exception) { corruptRejected = true; }
        True(corruptRejected, "A corrupt prepared receipt must fail preflight before either printer job starts.");
        return Task.CompletedTask;
    }

    private static byte[] CombinedPreviewPng(string order, string tracking)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, 400, 400));
            var text = new FormattedText($"Нова пошта\nСинтетична наклейка\nЗамовлення {order}\nТТН {tracking}",
                CultureInfo.GetCultureInfo("uk-UA"), FlowDirection.LeftToRight, new Typeface("Segoe UI"), 20, Brushes.Black, 1);
            drawing.DrawText(text, new Point(20, 20));
            for (var x = 20; x < 370; x += 7)
                drawing.DrawRectangle(Brushes.Black, null, new Rect(x, 160, x % 3 + 2, 85));
        }
        var bitmap = new RenderTargetBitmap(400, 400, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }
}

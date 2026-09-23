using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Views;

public partial class PreviewWindow : Window
{
    public PreviewWindow(byte[] png, ReceiptRowViewModel receipt)
    {
        InitializeComponent();
        using var stream = new MemoryStream(png, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        ReceiptImage.Source = bitmap;
        RenderOptions.SetBitmapScalingMode(ReceiptImage, BitmapScalingMode.HighQuality);
        ReceiptInfo.Text = $"№ {receipt.Serial} • {receipt.FiscalCode}";
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => FitReceiptToWidth();

    private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitReceiptToWidth();

    private void FitReceiptToWidth()
    {
        // Keep the complete official PNG visible. The remaining space covers
        // the viewer padding, receipt border and the vertical scroll bar.
        ReceiptImage.MaxWidth = Math.Max(100, PreviewScroll.ActualWidth - 60);
    }
}

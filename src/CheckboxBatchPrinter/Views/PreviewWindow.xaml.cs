using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Views;

public partial class PreviewWindow : Window
{
    private bool _fitWholeReceipt = true;

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
        var workArea = SystemParameters.WorkArea;
        var availableWidth = Math.Max(1, workArea.Width - 32);
        var availableHeight = Math.Max(1, workArea.Height - 32);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e) => FitReceipt();

    private void PreviewScroll_SizeChanged(object sender, SizeChangedEventArgs e) => FitReceipt();

    private void PreviewScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) FitReceipt();
    }

    private void FitWholeButton_Click(object sender, RoutedEventArgs e)
    {
        _fitWholeReceipt = true;
        FitReceipt();
        PreviewScroll.ScrollToTop();
    }

    private void FitWidthButton_Click(object sender, RoutedEventArgs e)
    {
        _fitWholeReceipt = false;
        FitReceipt();
    }

    private void FitReceipt()
    {
        if (PreviewScroll.ViewportWidth <= 0 || PreviewScroll.ViewportHeight <= 0) return;
        // Viewport dimensions already exclude viewer padding and scroll bars.
        // Leave a rounding allowance inside the paper border to avoid overflow.
        var padding = ReceiptPaper.Padding;
        ReceiptImage.MaxWidth = Math.Max(1, PreviewScroll.ViewportWidth - padding.Left - padding.Right - 2);
        ReceiptImage.MaxHeight = _fitWholeReceipt
            ? Math.Max(1, PreviewScroll.ViewportHeight - padding.Top - padding.Bottom - 2)
            : double.PositiveInfinity;
        FitWholeButton.IsEnabled = !_fitWholeReceipt;
        FitWidthButton.IsEnabled = _fitWholeReceipt;
    }
}

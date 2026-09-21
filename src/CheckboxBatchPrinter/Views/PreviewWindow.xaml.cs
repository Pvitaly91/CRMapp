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
}

using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.ViewModels;
using CheckboxBatchPrinter.Views;

namespace CheckboxBatchPrinter.Services;
public sealed class LabelDialogs(IShippingSettingsStore settings, IMarketplaceSettingsStore marketplaces, ILabelPrinter printer,
    NovaPoshtaDiagnostics? diagnostics = null, NovaPoshtaVerifiedSession? session = null, Func<MarketplaceOrder?>? currentOrder = null) : ILabelDialogs
{
    public MarketplaceOrder? ChoosePreviewShipment(MarketplaceOrder order)
    {
        if (order.Shipments.Count <= 1) return order;
        var window = new Window { Owner = Application.Current.MainWindow, Title = $"Замовлення №{order.Number} — вибір накладної",
            Width = 620, Height = 240, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "Виберіть конкретну накладну для перегляду. Пакетний вибір замовлення включає всі його наклейки.", TextWrapping = TextWrapping.Wrap });
        var list = new ComboBox { Margin = new Thickness(0,12,0,12) };
        foreach (var s in order.Shipments)
            list.Items.Add(new ComboBoxItem { Content = $"{ShippingCarrierNames.Display(ShippingCarrierNames.FromDocumentedDeliveryField(s.Carrier))} · ТТН {s.TrackingNumber}", Tag = s });
        list.SelectedIndex = 0; root.Children.Add(list);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var choose = new Button { Content = "Переглянути", Padding = new Thickness(10,5,10,5), Margin = new Thickness(6) };
        choose.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(new Button { Content = "Скасувати", IsCancel = true, Padding = new Thickness(10,5,10,5), Margin = new Thickness(6) });
        buttons.Children.Add(choose); root.Children.Add(buttons); window.Content = root;
        return window.ShowDialog() == true && list.SelectedItem is ComboBoxItem { Tag: OrderShipment shipment }
            ? order with { Shipments = [shipment] } : null;
    }
    public bool Preview(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous, bool allowPrint) =>
        new LabelPreviewWindow(batch, geometry, previous, allowPrint) { Owner = Application.Current.MainWindow }.ShowDialog() == true;
    public async Task SettingsAsync() => new LabelSettingsWindow(settings, printer, await settings.LoadAsync(), await marketplaces.LoadAsync(), diagnostics, session, currentOrder)
        { Owner = Application.Current.MainWindow }.ShowDialog();
    public void Error(string message) => MessageBox.Show(Application.Current.MainWindow, message, "Наклейки", MessageBoxButton.OK, MessageBoxImage.Warning);
}

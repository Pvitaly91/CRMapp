using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Views;

public sealed class LabelSettingsWindow : Window
{
    private readonly IShippingSettingsStore _store;
    private readonly ILabelPrinter _printer;
    private readonly ShippingLabelSettings _original;
    private readonly ComboBox _printers = new(), _formats = new(), _seller = new();
    private readonly TextBox _scale = new(), _x = new(), _y = new(), _copies = new();
    private readonly CheckBox _landscape = new() { Content = "Альбомна орієнтація" };
    private readonly PasswordBox _token = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    public LabelSettingsWindow(IShippingSettingsStore store, ILabelPrinter printer, ShippingLabelSettings original, MarketplaceSettings marketplaces,
        NovaPoshtaDiagnostics? diagnostics = null, NovaPoshtaVerifiedSession? session = null, Func<MarketplaceOrder?>? currentOrder = null)
    {
        _store = store; _printer = printer; _original = original;
        Title = "Налаштування — Друк наклейок"; Width = 660; Height = 650; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(16) };
        Add(root, "Окремий принтер наклейок. Не змінює принтер чеків або глобальні параметри Windows.", new TextBlock());
        Add(root, "Принтер Windows", _printers); Add(root, "Фізичний формат із драйвера", _formats);
        _printers.SelectionChanged += (_, _) => ReloadFormats();
        _printers.ItemsSource = _printer.Printers(); _printers.SelectedItem = original.Print.PrinterName;
        if (_printers.SelectedItem is null)
        {
            // Suggest based on advertised label media, never a hardcoded printer model/name.
            foreach (var name in _printer.Printers())
                try { if (_printer.Formats(name).Any(f => Math.Abs(f.WidthMm - f.HeightMm) < 2 && f.WidthMm is >= 99 and <= 103)) { _printers.SelectedItem = name; break; } }
                catch (Exception) { }
        }
        _landscape.IsChecked = original.Print.Landscape; root.Children.Add(_landscape);
        Add(root, "Масштаб, % (100 — без зменшення)", _scale); Add(root, "Зміщення X, мм", _x); Add(root, "Зміщення Y, мм", _y); Add(root, "Копій кожної етикетки", _copies);
        _scale.Text = original.Print.ScalePercent.ToString(CultureInfo.InvariantCulture); _x.Text = original.Print.OffsetXmm.ToString(CultureInfo.InvariantCulture); _y.Text = original.Print.OffsetYmm.ToString(CultureInfo.InvariantCulture); _copies.Text = original.Print.Copies.ToString();
        Add(root, "Токен Rozetka Delivery (окремий від Seller API; порожній — зберегти попередній)", _token);
        Add(root, "Нова пошта: підключення Rozetka Seller, яке має доступ до потрібних ТТН", _seller);
        _seller.DisplayMemberPath = nameof(MarketplaceConnection.Name); _seller.SelectedValuePath = nameof(MarketplaceConnection.Id);
        _seller.ItemsSource = marketplaces.Connections.Where(c => c.Enabled && c.Marketplace == MarketplaceKind.Rozetka).ToArray(); _seller.SelectedValue = original.NovaPoshtaSellerConnectionId;
        var np = new Button { Content = "Нова пошта — пряме підключення", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,10,0,10), Padding = new Thickness(8) };
        np.Click += async (_, _) =>
        {
            try
            {
                if (diagnostics is null || session is null) throw new InvalidOperationException();
                new NovaPoshtaConnectionWindow(_store, await _store.LoadAsync(), marketplaces, diagnostics, session, new WindowsShippingLabelRenderer(), currentOrder?.Invoke())
                    { Owner = this }.ShowDialog();
            }
            catch (Exception) { _status.Text = "Не вдалося відкрити пряме NP-підключення. Секрети не відображаються."; }
        };
        root.Children.Add(np);
        root.Children.Add(new TextBlock { Text = "Прямий NP не потребує Seller. Методи досліджено за SDK; спершу явно перевірте конкретну ТТН. Без підтвердженого PDF пакет блокується.", TextWrapping = TextWrapping.Wrap, FontSize = 11 });
        root.Children.Add(_status);
        var notices = new Button { Content = "Ліцензії PDF-renderer", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,8,0,4) };
        notices.Click += (_, _) =>
        {
            using var stream = typeof(LabelSettingsWindow).Assembly.GetManifestResourceStream("CheckboxBatchPrinter.PdfiumNotices");
            using var reader = new System.IO.StreamReader(stream!);
            new Window { Owner = this, Title = "PDFium — ліцензії", Width = 850, Height = 650,
                Content = new TextBox { Text = reader.ReadToEnd(), IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) } }.ShowDialog();
        };
        root.Children.Add(notices);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Зберегти", Margin = new Thickness(6), Padding = new Thickness(10, 5, 10, 5) }; save.Click += Save;
        footer.Children.Add(new Button { Content = "Скасувати", IsCancel = true, Margin = new Thickness(6), Padding = new Thickness(10, 5, 10, 5) }); footer.Children.Add(save); root.Children.Add(footer);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
    private static void Add(Panel panel, string label, FrameworkElement control)
    { panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 3) }); control.Margin = new Thickness(0, 0, 0, 3); panel.Children.Add(control); }
    private void ReloadFormats()
    {
        try
        {
            var values = _printers.SelectedItem is string name ? _printer.Formats(name) : [];
            _formats.ItemsSource = values;
            _formats.SelectedItem = values.FirstOrDefault(f => Math.Abs(f.WidthMm - _original.Print.WidthMm) < 0.2 && Math.Abs(f.HeightMm - _original.Print.HeightMm) < 0.2)
                ?? values.FirstOrDefault(f => Math.Abs(f.WidthMm - f.HeightMm) < 2 && f.WidthMm is >= 99 and <= 103) ?? values.FirstOrDefault();
        }
        catch (Exception) { _status.Text = "Не вдалося прочитати формати драйвера."; _formats.ItemsSource = null; }
    }
    private async void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_printers.SelectedItem is not string name || _formats.SelectedItem is not LabelDriverFormat format) throw new InvalidOperationException("Виберіть принтер і його формат.");
            double Number(TextBox input) => double.Parse(input.Text.Replace(',', '.'), CultureInfo.InvariantCulture);
            var print = new LabelPrintSettings { PrinterName = name, DriverFormat = format.Name, WidthMm = format.WidthMm, HeightMm = format.HeightMm,
                ScalePercent = Number(_scale), OffsetXmm = Number(_x), OffsetYmm = Number(_y), Landscape = _landscape.IsChecked == true, Copies = int.Parse(_copies.Text, CultureInfo.InvariantCulture) };
            _ = _printer.Inspect(print);
            var latest = await _store.LoadAsync(); // Preserve separate NP edits made in the child settings dialog.
            await _store.SaveAsync(latest with { Print = print, RozetkaDeliveryToken = _token.Password.Length == 0 ? latest.RozetkaDeliveryToken : _token.Password,
                NovaPoshtaSellerConnectionId = _seller.SelectedValue as string ?? "" });
            _token.Clear(); DialogResult = true;
        }
        catch (Exception ex) { _status.Text = ex is FormatException or OverflowException ? "Перевірте числові параметри." : ex.Message; }
    }
}

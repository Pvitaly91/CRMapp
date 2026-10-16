using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.Views;

public sealed class LabelSettingsWindow : Window
{
    private readonly IShippingSettingsStore _store;
    private readonly ILabelPrinter _printer;
    private readonly ShippingLabelSettings _original;
    private readonly ComboBox _printers = new(), _formats = new(), _seller = new();
    private readonly ComboBox _npStores = new();
    private readonly TextBox _scale = new(), _x = new(), _y = new(), _copies = new();
    private readonly CheckBox _landscape = new() { Content = "Альбомна орієнтація" };
    private readonly PasswordBox _token = new(), _npKey = new();
    private readonly Expander _advanced = new() { Header = "Розширені налаштування", IsExpanded = false, Margin = new Thickness(0,12,0,4) };
    private readonly TextBlock _npStatus = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0,4,0,4) };
    private readonly TextBlock _tokenStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,4) };
    private readonly TextBlock _sellerStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,4) };
    private readonly IMarketplaceSecretStore? _marketplaceSecrets;
    private int _sellerStatusGeneration;
    private readonly MarketplaceConnection[] _promStores;
    private readonly Dictionary<string, string> _npDrafts = new(StringComparer.Ordinal);
    private ShippingLabelSettings _npKnownSettings;
    private string? _npStoreId;
    private bool _sellerChanged, _printChanged, _saving;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    public LabelSettingsWindow(IShippingSettingsStore store, ILabelPrinter printer, ShippingLabelSettings original, MarketplaceSettings marketplaces,
        NovaPoshtaDiagnostics? diagnostics = null, NovaPoshtaVerifiedSession? session = null, Func<MarketplaceOrder?>? currentOrder = null,
        IMarketplaceSecretStore? marketplaceSecrets = null)
    {
        _store = store; _printer = printer; _original = original; _npKnownSettings = original; _marketplaceSecrets = marketplaceSecrets;
        _promStores = marketplaces.Connections.Where(c => c.Enabled && c.Marketplace == MarketplaceKind.Prom).ToArray();
        Title = "Налаштування — Друк наклейок"; Width = 660; Height = 590; MinHeight = 450; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = "Принтер наклейок", FontWeight = FontWeights.SemiBold,
            ToolTip = "Не змінює принтер чеків або глобальні параметри Windows." });
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
        Add(root, "Копій кожної етикетки", _copies);
        _scale.Text = original.Print.ScalePercent.ToString(CultureInfo.InvariantCulture); _x.Text = original.Print.OffsetXmm.ToString(CultureInfo.InvariantCulture); _y.Text = original.Print.OffsetYmm.ToString(CultureInfo.InvariantCulture); _copies.Text = original.Print.Copies.ToString();

        var npBasic = new StackPanel { Margin = new Thickness(10) };
        root.Children.Add(new GroupBox { Header = "Нова пошта — для замовлень Prom", Content = npBasic, Margin = new Thickness(0,12,0,0) });
        _npStores.DisplayMemberPath = nameof(MarketplaceConnection.Name); _npStores.SelectedValuePath = nameof(MarketplaceConnection.Id);
        _npStores.ItemsSource = _promStores;
        if (_promStores.Length > 1) Add(npBasic, "Магазин Prom", _npStores);
        else
        {
            _npStores.Visibility = Visibility.Collapsed;
            npBasic.Children.Add(new TextBlock { Text = _promStores.SingleOrDefault()?.Name ?? "Спочатку додайте магазин Prom у налаштуваннях маркетплейсів.", TextWrapping = TextWrapping.Wrap });
        }
        Add(npBasic, "API-ключ Нової пошти", _npKey); npBasic.Children.Add(_npStatus);
        _npKey.PasswordChanged += (_, _) => UpdateNpIndicator();
        _npKey.ToolTip = "Ключ вводиться лише локально й зберігається захищено. Новий ключ оновлює наявне підключення Нової пошти для всіх пов’язаних із ним магазинів. Порожнє поле залишає попередній ключ. Збереження не перевіряє доступ до накладної.";
        _npStores.SelectionChanged += (_, _) => { CommitNpDraft(); ShowNpStore(); };
        var selectedOrder = currentOrder?.Invoke();
        if (selectedOrder?.Key.Marketplace == MarketplaceKind.Prom) _npStores.SelectedValue = selectedOrder.Key.ConnectionId;
        if (_npStores.SelectedItem is null && _promStores.Length == 1) _npStores.SelectedIndex = 0;
        ShowNpStore();
        npBasic.Children.Add(new TextBlock { Text = "Новий ключ оновлює наявне підключення. Порожнє поле залишає попередній ключ.", TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0,2,0,4) });

        root.Children.Add(new TextBlock
        {
            Text = "Накладні замовлень Rozetka: використовується API-токен відповідного магазину з налаштувань маркетплейсів. Окремий токен Delivery не потрібний.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0,12,0,4)
        });
        root.Children.Add(new TextBlock
        {
            Text = "Доставка в Rozetka для Prom і Bigl: у «Налаштування → Маркетплейси» виберіть свій магазин Prom і натисніть «Увійти в кабінет Prom для накладних». Готові накладні завантажуються через цей вхід без окремого ключа Delivery.",
            TextWrapping = TextWrapping.Wrap, FontSize = 11, Margin = new Thickness(0,4,0,4)
        });
        root.Children.Add(_tokenStatus); root.Children.Add(_sellerStatus);
        _token.PasswordChanged += (_, _) => UpdateTokenIndicator();
        UpdateTokenIndicator();

        var advanced = new StackPanel { Margin = new Thickness(8) }; _advanced.Content = advanced; root.Children.Add(_advanced);
        _landscape.IsChecked = original.Print.Landscape; advanced.Children.Add(_landscape);
        Add(advanced, "Масштаб, % (100 — без зменшення)", _scale); Add(advanced, "Зміщення X, мм", _x); Add(advanced, "Зміщення Y, мм", _y);
        Add(advanced, "Токен окремого акаунта Rozetka Delivery (для входу через Prom не потрібний; порожній — зберегти попередній)", _token);
        Add(advanced, "Нова пошта через Rozetka Seller (не потрібно для Prom із власним NP-ключем)", _seller);
        _seller.DisplayMemberPath = nameof(MarketplaceConnection.Name); _seller.SelectedValuePath = nameof(MarketplaceConnection.Id);
        _seller.ItemsSource = marketplaces.Connections.Where(c => c.Enabled && c.Marketplace == MarketplaceKind.Rozetka).ToArray(); _seller.SelectedValue = original.NovaPoshtaSellerConnectionId;
        _seller.SelectionChanged += (_, _) => { _sellerChanged = true; _ = UpdateSellerIndicatorAsync(); };
        _ = UpdateSellerIndicatorAsync();
        async void OpenNp(object sender, RoutedEventArgs e)
        {
            if (_saving) return;
            _saving = true; IsEnabled = false;
            try
            {
                if (diagnostics is null || session is null) throw new InvalidOperationException("Перевірка накладних зараз недоступна.");
                var order = currentOrder?.Invoke();
                if (order?.Key.Marketplace != MarketplaceKind.Prom || order.Key.ConnectionId != _npStoreId)
                    throw new InvalidOperationException("Виберіть у налаштуваннях той самий магазин Prom, що й у вибраному замовленні з ТТН.");
                CommitNpDraft();
                var latest = await _store.LoadAsync(); var updated = ApplyNpDrafts(latest);
                if (!ReferenceEquals(latest, updated)) await _store.SaveAsync(updated);
                ClearNpDrafts(updated);
                new NovaPoshtaConnectionWindow(_store, await _store.LoadAsync(), marketplaces, diagnostics, session, new WindowsShippingLabelRenderer(), order)
                    { Owner = this }.ShowDialog();
                ClearNpDrafts(await _store.LoadAsync());
            }
            catch (Exception ex) { _status.Text = ex is InvalidOperationException ? ex.Message : "Не вдалося відкрити перевірку NP. Секрети не відображаються."; }
            finally { _saving = false; IsEnabled = true; }
        }
        var verify = new Button { Content = "Перевірка накладної…", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0,6,0,0),
            ToolTip = "Виберіть замовлення Prom з ТТН у таблиці та його магазин у цих налаштуваннях. Перевірка зберігає введений ключ і читає накладну та PDF; друк не запускається." };
        void UpdateVerificationAvailability()
        {
            var order = currentOrder?.Invoke();
            verify.IsEnabled = order?.Key.Marketplace == MarketplaceKind.Prom && order.Key.ConnectionId == _npStoreId &&
                _promStores.Any(s => s.Id == _npStoreId) && ShippingCarrierNames.ForOrder(order).Any(s => s.Carrier == ShippingCarrier.NovaPoshta);
        }
        _npStores.SelectionChanged += (_, _) => UpdateVerificationAvailability();
        UpdateVerificationAvailability();
        ToolTipService.SetShowOnDisabled(verify, true);
        verify.Click += OpenNp; npBasic.Children.Add(verify);
        advanced.Children.Add(new TextBlock { Text = "ⓘ Про пряме NP-підключення", FontSize = 11,
            ToolTip = "Read-only, за відкритим SDK. Не створює накладних. Доступ до конкретної ТТН, відповідність PDF і повнота місць перевіряються окремо. Без підтвердженого PDF пакет блокується." });
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
        advanced.Children.Add(notices);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "Зберегти", Margin = new Thickness(6), Padding = new Thickness(10, 5, 10, 5) }; save.Click += Save;
        footer.Children.Add(new Button { Content = "Скасувати", IsCancel = true, Margin = new Thickness(6), Padding = new Thickness(10, 5, 10, 5) }); footer.Children.Add(save); root.Children.Add(footer);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = System.Windows.Media.Brushes.White };
        // A key-only save must not revert newer printer settings to this dialog's snapshot.
        _printers.SelectionChanged += (_, _) => _printChanged = true;
        _formats.SelectionChanged += (_, _) => _printChanged = true;
        foreach (var input in new[] { _scale, _x, _y, _copies }) input.TextChanged += (_, _) => _printChanged = true;
        _landscape.Checked += (_, _) => _printChanged = true; _landscape.Unchecked += (_, _) => _printChanged = true;
        Closed += (_, _) => { _npKey.Clear(); _token.Clear(); _npDrafts.Clear(); };
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
    private void CommitNpDraft()
    {
        if (_npStoreId is null) return;
        var key = _npKey.Password.Trim();
        if (key.Length > 0) _npDrafts[_npStoreId] = key;
        else _npDrafts.Remove(_npStoreId);
    }
    private void ShowNpStore()
    {
        _npStoreId = _npStores.SelectedValue as string;
        _npKey.Password = _npStoreId is not null && _npDrafts.TryGetValue(_npStoreId, out var draft) ? draft : "";
        _npKey.IsEnabled = _npStoreId is not null;
        UpdateNpIndicator();
    }
    private void UpdateNpIndicator()
    {
        CredentialIndicator.Apply(_npStatus, NovaPoshtaKeySettings.HasSavedKey(_npKnownSettings, _npStoreId), _npKey.Password.Trim().Length > 0, "Ключ Нової пошти");
        if (_npStoreId is null) { _npStatus.Text = "Оберіть магазин Prom для налаштування ключа"; _npStatus.Tag = "Unknown"; }
    }
    private void UpdateTokenIndicator() => CredentialIndicator.Apply(_tokenStatus,
        !string.IsNullOrWhiteSpace(_original.RozetkaDeliveryToken), !string.IsNullOrWhiteSpace(_token.Password), "Токен Rozetka Delivery");
    private async Task UpdateSellerIndicatorAsync()
    {
        var generation = ++_sellerStatusGeneration;
        CredentialIndicator.Apply(_sellerStatus, false, false, "Підключення Rozetka Seller");
        var id = _seller.SelectedValue as string;
        if (string.IsNullOrEmpty(id)) { CredentialIndicator.Apply(_sellerStatus, false, false, "Підключення Rozetka Seller"); return; }
        if (_marketplaceSecrets is null) { _sellerStatus.Text = "Rozetka Seller: стан даних доступний у налаштуваннях маркетплейсу"; return; }
        _sellerStatus.Tag = "Unknown";
        _sellerStatus.Text = "Rozetka Seller: перевіряю збережені дані…";
        try
        {
            var credentials = await _marketplaceSecrets.LoadAsync(id);
            if (generation != _sellerStatusGeneration) return;
            var hasToken = RozetkaApiCredentials.ValidToken(credentials?.Token);
            CredentialIndicator.Apply(_sellerStatus, hasToken,
                _sellerChanged && id != _original.NovaPoshtaSellerConnectionId, "API-токен Rozetka Seller");
            if (!hasToken)
                _sellerStatus.Text = "Додайте API-токен цього магазину в налаштуваннях маркетплейсів. У кабінеті Rozetka: «Налаштування → Безпека API».";
        }
        catch (Exception) { if (generation == _sellerStatusGeneration) _sellerStatus.Text = "Rozetka Seller: не вдалося прочитати стан даних"; }
    }
    private ShippingLabelSettings ApplyNpDrafts(ShippingLabelSettings latest)
    {
        string TargetAccount(string storeId) =>
            latest.NovaPoshtaStoreBindings.SingleOrDefault(b => b.MarketplaceConnectionId == storeId)?.NovaPoshtaConnectionId
            ?? (latest.NovaPoshtaConnections.Count == 1 ? latest.NovaPoshtaConnections[0].Id
                : latest.NovaPoshtaConnections.Count == 0 ? "first-account" : "unbound-" + storeId);
        if (_npDrafts.GroupBy(d => TargetAccount(d.Key)).Any(group => group.Select(d => d.Value).Distinct(StringComparer.Ordinal).Count() > 1))
            throw new InvalidOperationException("Для одного підключення Нової пошти введено різні ключі в кількох магазинах. Залиште один актуальний ключ; налаштування не збережено.");
        foreach (var draft in _npDrafts)
            latest = NovaPoshtaKeySettings.Apply(latest, _promStores.SingleOrDefault(s => s.Id == draft.Key), draft.Value);
        return latest;
    }
    private void ClearNpDrafts(ShippingLabelSettings latest)
    { _npKnownSettings = latest; _npDrafts.Clear(); _npKey.Clear(); ShowNpStore(); }
    private async void Save(object sender, RoutedEventArgs e)
    {
        if (_saving) return;
        _saving = true; IsEnabled = false;
        try
        {
            CommitNpDraft();
            var latest = await _store.LoadAsync();
            double Number(TextBox input) => double.Parse(input.Text.Replace(',', '.'), CultureInfo.InvariantCulture);
            var print = latest.Print;
            if ((_printChanged || latest.Print.PrinterName.Length == 0) && _printers.SelectedItem is string name && _formats.SelectedItem is LabelDriverFormat format)
            {
                print = new LabelPrintSettings { PrinterName = name, DriverFormat = format.Name, WidthMm = format.WidthMm, HeightMm = format.HeightMm,
                    ScalePercent = Number(_scale), OffsetXmm = Number(_x), OffsetYmm = Number(_y), Landscape = _landscape.IsChecked == true, Copies = int.Parse(_copies.Text, CultureInfo.InvariantCulture) };
                _ = _printer.Inspect(print);
            }
            else if (_printChanged || latest.Print.PrinterName.Length == 0 && _npDrafts.Count == 0)
                throw new InvalidOperationException("Виберіть принтер і його формат.");
            latest = ApplyNpDrafts(latest);
            var saved = latest with { Print = print, RozetkaDeliveryToken = _token.Password.Trim().Length == 0 ? latest.RozetkaDeliveryToken : _token.Password.Trim(),
                NovaPoshtaSellerConnectionId = _sellerChanged ? _seller.SelectedValue as string ?? "" : latest.NovaPoshtaSellerConnectionId };
            await _store.SaveAsync(saved);
            ClearNpDrafts(saved); _token.Clear(); DialogResult = true;
        }
        catch (Exception ex) { _status.Text = ex is FormatException or OverflowException ? "Перевірте числові параметри." : ex.Message; }
        finally { _saving = false; IsEnabled = true; }
    }
}

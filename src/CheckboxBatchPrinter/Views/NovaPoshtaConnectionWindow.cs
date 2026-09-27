using System.Windows;
using System.Windows.Controls;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Views;

public sealed class NovaPoshtaConnectionWindow : Window
{
    private readonly IShippingSettingsStore _store;
    private readonly NovaPoshtaDiagnostics _diagnostics;
    private readonly NovaPoshtaVerifiedSession _session;
    private readonly IShippingLabelRenderer _renderer;
    private readonly MarketplaceOrder? _order;
    private readonly List<NovaPoshtaConnection> _accounts;
    private readonly Dictionary<string, string> _bindings;
    private readonly ComboBox _accountsBox = new(), _stores = new(), _boundAccount = new(), _shipments = new();
    private readonly TextBox _name = new(); private readonly PasswordBox _key = new();
    private readonly DatePicker _from = new() { SelectedDate = DateTime.Today }, _to = new() { SelectedDate = DateTime.Today };
    private readonly TextBlock _status = new() { Text = "Очікує локального введення ключа", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) };
    private readonly StackPanel _form = new();
    private readonly Button _cancel = new() { Content = "Скасувати перевірку", IsEnabled = false };
    private CancellationTokenSource? _cancellation;
    private string? _editingId, _storeId;
    private bool _updating;
    public NovaPoshtaConnectionWindow(IShippingSettingsStore store, ShippingLabelSettings original, MarketplaceSettings marketplaces,
        NovaPoshtaDiagnostics diagnostics, NovaPoshtaVerifiedSession session, IShippingLabelRenderer renderer, MarketplaceOrder? order)
    {
        _store = store; _diagnostics = diagnostics; _session = session; _renderer = renderer; _order = order;
        _accounts = original.NovaPoshtaConnections.ToList(); _bindings = original.NovaPoshtaStoreBindings.ToDictionary(b => b.MarketplaceConnectionId, b => b.NovaPoshtaConnectionId);
        Title = "Нова пошта — пряме підключення"; Width = 780; Height = 760; MinWidth = 650; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(16) }; root.Children.Add(_form);
        Add(_form, "Експериментальне read-only підключення за відкритим SDK. Не створює ТТН/реєстри. Офіційний контракт ще не підтверджено.", new TextBlock());
        ConfigureAccounts(_accountsBox); ConfigureAccounts(_boundAccount);
        Add(_form, "NP-підключення для редагування", _accountsBox);
        var add = new Button { Content = "Додати NP-підключення", HorizontalAlignment = HorizontalAlignment.Left };
        add.Click += (_, _) => { CommitAccount(); var account = new NovaPoshtaConnection(Guid.NewGuid().ToString("N"), $"NP — підключення {_accounts.Count + 1}", ""); _accounts.Add(account); ReloadAccounts(account.Id); };
        _form.Children.Add(add);
        Add(_form, "Назва підключення", _name);
        Add(_form, "API-ключ (порожнє поле зберігає попередній; значення не показується)", _key);
        _accountsBox.SelectionChanged += (_, _) =>
        {
            if (_updating) return;
            CommitAccount(); ShowAccount(_accountsBox.SelectedValue as string);
        };
        _stores.DisplayMemberPath = nameof(MarketplaceConnection.Name); _stores.SelectedValuePath = nameof(MarketplaceConnection.Id);
        _stores.ItemsSource = marketplaces.Connections.Where(c => c.Enabled && c.Marketplace == MarketplaceKind.Prom).ToArray();
        Add(_form, "Магазин Prom", _stores); Add(_form, "Явне NP-підключення цього магазину (без автоматичного перебору акаунтів)", _boundAccount);
        _stores.SelectionChanged += (_, _) =>
        {
            CommitBinding(); _storeId = _stores.SelectedValue as string;
            _boundAccount.SelectedValue = _storeId is not null && _bindings.TryGetValue(_storeId, out var id) ? id : "";
        };
        ReloadAccounts(_accounts.FirstOrDefault()?.Id);
        _stores.SelectedValue = order?.Key.ConnectionId;
        if (_stores.SelectedItem is null && _stores.Items.Count > 0) _stores.SelectedIndex = 0;
        Add(_form, "Конкретна існуюча накладна вибраного замовлення Prom", _shipments);
        if (order?.Key.Marketplace == MarketplaceKind.Prom)
            foreach (var shipment in ShippingCarrierNames.ForOrder(order).Where(s => s.Carrier == ShippingCarrier.NovaPoshta))
                _shipments.Items.Add(new ComboBoxItem { Content = $"Замовлення №{order.Number} · ТТН {shipment.TrackingNumber}", Tag = shipment });
        if (_shipments.Items.Count > 0) _shipments.SelectedIndex = 0;
        else _status.Text = "Виберіть рядок замовлення Prom із наявною ТТН у таблиці, потім відкрийте ці налаштування. Ключ можна зберегти зараз; перевірка очікує вибору накладної.";
        Add(_form, "Період створення ТТН, від (не дата замовлення; максимум 7 днів)", _from); Add(_form, "До", _to);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,12,0,12) };
        var save = new Button { Content = "Зберегти NP-підключення", Padding = new Thickness(8), Margin = new Thickness(0,0,8,0) };
        save.Click += async (_, _) => { try { _form.IsEnabled = false; await SaveAsync(); ReloadAccounts(_editingId); _status.Text = "NP-підключення захищено збережено. Це не перевірка API або доступу до PDF."; } catch (Exception) { _status.Text = "Не вдалося зберегти NP. Перевірте назву, формат ключа й вибір підключення; секрет приховано."; } finally { _form.IsEnabled = true; } };
        var check = new Button { Content = "Перевірити наявну накладну", Padding = new Thickness(8) }; check.Click += Check;
        buttons.Children.Add(save); buttons.Children.Add(check); _form.Children.Add(buttons);
        root.Children.Add(_status); _cancel.Click += (_, _) => _cancellation?.Cancel(); root.Children.Add(_cancel);
        root.Children.Add(new Button { Content = "Закрити", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0), Padding = new Thickness(10,6,10,6) });
        Closed += (_, _) => _cancellation?.Cancel();
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = System.Windows.Media.Brushes.White };
    }
    private static void ConfigureAccounts(ComboBox box) { box.DisplayMemberPath = nameof(NovaPoshtaConnection.Name); box.SelectedValuePath = nameof(NovaPoshtaConnection.Id); }
    private static void Add(Panel root, string title, FrameworkElement input)
    { root.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,3) }); root.Children.Add(input); }
    private void CommitAccount()
    {
        if (_editingId is null) return;
        var index = _accounts.FindIndex(c => c.Id == _editingId);
        if (index < 0) return;
        var old = _accounts[index];
        _accounts[index] = old with { Name = _name.Text.Trim(), ApiKey = _key.Password.Length > 0 ? _key.Password.Trim() : old.ApiKey };
        _key.Clear();
    }
    private void ShowAccount(string? id)
    {
        _editingId = id; var account = _accounts.FirstOrDefault(c => c.Id == id);
        _name.Text = account?.Name ?? ""; _key.Clear();
        _status.Text = account?.ApiKey.Length > 0 ? "Ключ збережений; доступ до вибраної ТТН та PDF ще треба перевірити." : "Очікує локального введення ключа";
    }
    private void CommitBinding()
    {
        if (_storeId is null) return;
        if (_boundAccount.SelectedValue is string id && id.Length > 0) _bindings[_storeId] = id;
        else _bindings.Remove(_storeId);
    }
    private void ReloadAccounts(string? selected)
    {
        _updating = true; var bound = _boundAccount.SelectedValue as string;
        _accountsBox.ItemsSource = _accounts.ToArray();
        _boundAccount.ItemsSource = new[] { new NovaPoshtaConnection("", "Не вибрано — прямий NP не використовується", "") }.Concat(_accounts).ToArray();
        _accountsBox.SelectedValue = selected; _boundAccount.SelectedValue = bound ?? "";
        ShowAccount(selected); _updating = false;
    }
    private async Task SaveAsync(CancellationToken ct = default)
    {
        CommitAccount(); CommitBinding();
        var latest = await _store.LoadAsync(ct);
        await _store.SaveAsync(latest with { NovaPoshtaConnections = _accounts.ToArray(),
            NovaPoshtaStoreBindings = _bindings.Select(b => new NovaPoshtaStoreBinding(b.Key, b.Value)).ToArray() }, ct);
    }
    private async void Check(object sender, RoutedEventArgs e)
    {
        if (_cancellation is not null) return;
        try
        {
            CommitAccount(); CommitBinding();
            if (_shipments.SelectedItem is not ComboBoxItem { Tag: ShipmentReference shipment } || _order is null ||
                shipment.Order.ConnectionId != _storeId) throw new InvalidOperationException("Виберіть замовлення Prom і його магазин для перевірки конкретної ТТН.");
            var connection = _bindings.TryGetValue(shipment.Order.ConnectionId, out var id) ? _accounts.SingleOrDefault(c => c.Id == id) : null;
            if (connection is null) throw new InvalidOperationException("Явно виберіть NP-підключення для магазину Prom.");
            if (connection.ApiKey.Length == 0) { _status.Text = "Очікує локального введення ключа"; return; }
            if (_from.SelectedDate is not DateTime from || _to.SelectedDate is not DateTime to) throw new InvalidOperationException("Явно виберіть період перевірки ТТН.");
            var period = new NovaPoshtaSearchPeriod(DateOnly.FromDateTime(from), DateOnly.FromDateTime(to)); period.Validate();
            _cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5)); _form.IsEnabled = false; _cancel.IsEnabled = true;
            await SaveAsync(_cancellation.Token);
            _status.Text = "Читаю лише вибраний період NP-акаунта; потім перевіряю PDF. Друк не запускається…";
            var result = await _diagnostics.CheckAsync(connection, shipment, period, _cancellation.Token);
            _status.Text = $"{result.Message}\nСторінок API: {result.Lookup.PagesRead}; місць за API: {result.Lookup.Places?.ToString() ?? "не надано"}.";
            if (result.Document is not null)
            {
                var pages = await _renderer.RenderAsync(result.Document with { ExpectedPages = 0 }, 203, 203, _cancellation.Token);
                _cancellation.Token.ThrowIfCancellationRequested();
                if (new NovaPoshtaDiagnosticPreviewWindow(result, pages) { Owner = this }.ShowDialog() == true)
                { _session.Accept(connection, result); _status.Text = "Цю ТТН дозволено для пакета поточного сеансу. Перед друком PDF перевіриться знову. Фізичного друку не було."; }
            }
        }
        catch (OperationCanceledException) { _status.Text = "Перевірку скасовано або вичерпано її час. Друк не виконувався."; }
        catch (Exception ex) { _status.Text = ex is ShippingLabelException or InvalidOperationException ? ex.Message : "Перевірка NP не завершена; технічні деталі приховано для захисту ключа."; }
        finally { _cancellation?.Dispose(); _cancellation = null; _form.IsEnabled = true; _cancel.IsEnabled = false; }
    }
}

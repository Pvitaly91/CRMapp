using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.Views;

public sealed class NovaPoshtaConnectionWindow : Window
{
    private readonly IShippingSettingsStore _store;
    private readonly NovaPoshtaDiagnostics _diagnostics;
    private readonly NovaPoshtaVerifiedSession _session;
    private readonly IShippingLabelRenderer _renderer;
    private readonly MarketplaceOrder? _order;
    private readonly MarketplaceConnection? _shop;
    private NovaPoshtaConnection? _connection;
    private readonly ComboBox _shipments = new();
    private NovaPoshtaSearchPeriod _period = null!;
    private readonly TextBlock _periodText = new() { Margin = new Thickness(0,8,0,0), FontSize = 11, TextWrapping = TextWrapping.Wrap,
        ToolTip = "Це дата створення ТТН, не замовлення. Інші періоди й акаунти автоматично не перебираються." };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,8) };
    private readonly TextBlock _credentialStatus = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,4,0,8) };
    private readonly StackPanel _form = new();
    private readonly Button _cancel = new() { Content = "Скасувати перевірку", IsEnabled = false, Visibility = Visibility.Collapsed };
    private readonly Button _check = new() { Content = "Перевірити наявну накладну", Padding = new Thickness(8) };
    private readonly DispatcherTimer _cooldownTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private CancellationTokenSource? _cancellation;
    private bool _showingCooldown;
    public NovaPoshtaConnectionWindow(IShippingSettingsStore store, ShippingLabelSettings original, MarketplaceSettings marketplaces,
        NovaPoshtaDiagnostics diagnostics, NovaPoshtaVerifiedSession session, IShippingLabelRenderer renderer, MarketplaceOrder? order)
    {
        _store = store; _diagnostics = diagnostics; _session = session; _renderer = renderer; _order = order;
        _shop = order?.Key.Marketplace == MarketplaceKind.Prom
            ? marketplaces.Connections.SingleOrDefault(c => c.Id == order.Key.ConnectionId && c.Enabled && c.Marketplace == MarketplaceKind.Prom) : null;
        _connection = ResolveConnection(original);
        UpdatePeriod();
        Title = "Нова пошта — перевірка накладної"; Width = 680; Height = 380; MinWidth = 520; MinHeight = 330;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new StackPanel { Margin = new Thickness(16) }; root.Children.Add(_form);
        _form.Children.Add(new TextBlock { Text = "Перевірка вже створеної накладної. Друк не запускається.", TextWrapping = TextWrapping.Wrap,
            ToolTip = "Read-only підключення за відкритим SDK. Не створює ТТН/реєстри. Офіційний контракт ще не підтверджено. Перевіряються доступ, PDF, номер накладної та повнота місць." });
        _form.Children.Add(new TextBlock { Text = _shop?.Name ?? "Спочатку виберіть замовлення увімкненого магазину Prom у таблиці.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,3) });
        CredentialIndicator.Apply(_credentialStatus, !string.IsNullOrWhiteSpace(_connection?.ApiKey), false, "Ключ Нової пошти");
        _form.Children.Add(_credentialStatus);
        Add(_form, "Накладна вибраного замовлення", _shipments);
        if (order?.Key.Marketplace == MarketplaceKind.Prom)
            foreach (var shipment in ShippingCarrierNames.ForOrder(order).Where(s => s.Carrier == ShippingCarrier.NovaPoshta))
                _shipments.Items.Add(new ComboBoxItem { Content = $"Замовлення №{order.Number} · ТТН {shipment.TrackingNumber}", Tag = shipment });
        if (_shipments.Items.Count > 0) _shipments.SelectedIndex = 0;
        else _status.Text = "Спочатку виберіть замовлення Prom із ТТН у таблиці.";
        if (_shipments.Items.Count > 0) _status.Text = _connection?.ApiKey.Length > 0
            ? "Використовується ключ із налаштувань наклейок. Натисніть «Перевірити наявну накладну»." : "Спочатку збережіть API-ключ у налаштуваннях наклейок.";
        _form.Children.Add(_periodText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0,12,0,12) };
        _check.Click += Check;
        buttons.Children.Add(_check); _form.Children.Add(buttons);
        root.Children.Add(_status); _cancel.Click += (_, _) => _cancellation?.Cancel(); root.Children.Add(_cancel);
        root.Children.Add(new Button { Content = "Закрити", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0), Padding = new Thickness(10,6,10,6) });
        _cooldownTimer.Tick += (_, _) => UpdateCooldown();
        Loaded += (_, _) => { UpdateCooldown(); _cooldownTimer.Start(); };
        Closed += (_, _) => { _cooldownTimer.Stop(); _cancellation?.Cancel(); };
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Background = System.Windows.Media.Brushes.White };
        UpdateCooldown();
    }
    private static void Add(Panel root, string title, FrameworkElement input)
    { root.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,10,0,3) }); root.Children.Add(input); }
    private void UpdatePeriod()
    {
        var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
        _period = new(today.AddDays(-6), today);
        _periodText.Text = $"Період перевірки ТТН (останні 7 днів): {_period.From:dd.MM.yyyy} — {_period.To:dd.MM.yyyy} (Київ).";
    }
    private NovaPoshtaConnection? ResolveConnection(ShippingLabelSettings settings)
    {
        if (_shop is null) return null;
        var binding = settings.NovaPoshtaStoreBindings.SingleOrDefault(b => b.MarketplaceConnectionId == _shop.Id);
        return binding is null ? null : settings.NovaPoshtaConnections.SingleOrDefault(a => a.Id == binding.NovaPoshtaConnectionId);
    }
    private void UpdateCooldown()
    {
        if (_cancellation is not null)
        {
            _check.IsEnabled = false;
            _check.Content = "Перевірка…";
            return;
        }
        var remaining = _connection is null || !NovaPoshtaReadOnlyContract.ValidKey(_connection.ApiKey)
            ? TimeSpan.Zero : _diagnostics.GetRetryDelay(_connection);
        _check.IsEnabled = _shop is not null && _shipments.Items.Count > 0 && remaining <= TimeSpan.Zero;
        if (remaining > TimeSpan.Zero)
        {
            var seconds = Math.Ceiling(remaining.TotalSeconds);
            _showingCooldown = true;
            _check.Content = $"Повторити через {seconds:0} с";
            _status.Text = $"Нова пошта обмежила частоту запитів. Повторна перевірка стане доступна через {seconds:0} с. Автоматичного повтору немає; ключ змінювати не потрібно.";
        }
        else
        {
            _check.Content = "Перевірити наявну накладну";
            if (_showingCooldown) _status.Text = "Пауза завершена. Можна повторити перевірку накладної.";
            _showingCooldown = false;
        }
    }
    private async void Check(object sender, RoutedEventArgs e)
    {
        UpdateCooldown();
        if (_cancellation is not null || !_check.IsEnabled) return;
        try
        {
            if (_shipments.SelectedItem is not ComboBoxItem { Tag: ShipmentReference shipment } || _order is null ||
                shipment.Order.ConnectionId != _shop?.Id) throw new InvalidOperationException("Виберіть замовлення Prom із ТТН у таблиці.");
            _cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5)); _form.IsEnabled = false; _cancel.IsEnabled = true; _cancel.Visibility = Visibility.Visible; UpdateCooldown();
            // This dialog only reads credentials saved in the one main settings form.
            // Never write a stale account/binding snapshot back during diagnostics.
            _connection = ResolveConnection(await _store.LoadAsync(_cancellation.Token));
            CredentialIndicator.Apply(_credentialStatus, !string.IsNullOrWhiteSpace(_connection?.ApiKey), false, "Ключ Нової пошти");
            var connection = _connection;
            if (connection is null || connection.ApiKey.Length == 0)
                throw new InvalidOperationException("Спочатку збережіть API-ключ для цього магазину в налаштуваннях наклейок.");
            UpdatePeriod();
            _status.Text = "Читаю лише вибраний період NP-акаунта; потім перевіряю PDF. Друк не запускається…";
            var result = await _diagnostics.CheckAsync(connection, shipment, _period, _cancellation.Token);
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
        finally { _cancellation?.Dispose(); _cancellation = null; _form.IsEnabled = true; _cancel.IsEnabled = false; _cancel.Visibility = Visibility.Collapsed; UpdateCooldown(); }
    }
}

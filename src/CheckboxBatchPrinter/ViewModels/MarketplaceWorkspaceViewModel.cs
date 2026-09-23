using System.Collections.ObjectModel;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed class MarketplaceWorkspaceViewModel : ObservableObject
{
    private readonly IMarketplaceSettingsStore _settings;
    private readonly MarketplaceSyncService _sync;
    private readonly IReceiptOrderLinkStore _links;
    private readonly IReceiptDetailsService _details;
    private readonly IOrderLinkDialogService _dialogs;
    private readonly ReceiptOrderMatchingService _matcher = new();
    private IReadOnlyList<ReceiptRowViewModel> _rows = [];
    private MarketplaceSettings _config = new();
    private MarketplaceSnapshot _snapshot = new([], []);
    private IReadOnlyList<ReceiptOrderDecision> _decisions = [];
    private readonly Dictionary<string, ReceiptDetails> _receiptDetails = [];
    private string _account = "", _status = "Маркетплейси не підключено.", _filter = "Усі";
    private bool _busy, _canApply, _coverageComplete;
    private CancellationTokenSource? _cancel;
    private Task? _activeSync;
    private DateOnly _from, _to;
    private int _extraHistory;
    private ReceiptRowViewModel? _selected;
    private int _generation;

    public MarketplaceWorkspaceViewModel(IMarketplaceSettingsStore settings, MarketplaceSyncService sync,
        IReceiptOrderLinkStore links, IReceiptDetailsService details, IOrderLinkDialogService dialogs)
    {
        _settings = settings; _sync = sync; _links = links; _details = details; _dialogs = dialogs;
        SyncCommand = new AsyncRelayCommand(_ => SyncAsync(), _ => !IsBusy && _account.Length > 0);
        ExpandCommand = new AsyncRelayCommand(async _ => { _extraHistory = Math.Min(_extraHistory + 30, 3650); await SyncAsync(); }, _ => !IsBusy && _account.Length > 0);
        CancelCommand = new RelayCommand(_ => _cancel?.Cancel(), _ => IsBusy);
        LinkCommand = new AsyncRelayCommand(_ => LinkAsync(), _ => _selected is not null && !IsBusy && _canApply);
        UnlinkCommand = new AsyncRelayCommand(_ => UnlinkAsync(), _ => _selected is not null && !IsBusy && _canApply &&
            (_selected.OrderMatch?.Order is not null || _decisions.Any(d => d.AccountContext == _account && d.ReceiptId == _selected.Id && d.ConfirmedOrder is not null)));
        CopyNumberCommand = new RelayCommand(_ => _dialogs.CopyText(_selected?.OrderMatch?.Order?.Number ?? ""));
        CopyTrackingCommand = new RelayCommand(_ => _dialogs.CopyText(_selected?.OrderMatch?.Order?.TrackingDisplay ?? ""));
    }
    public event EventHandler? MatchesChanged;
    public IReadOnlyList<string> Filters { get; } = ["Усі", "Prom", "Rozetka", "Без зв’язку", "Потрібна перевірка"];
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) MatchesChanged?.Invoke(this, EventArgs.Empty); } }
    public bool ShowExtraColumns { get; set; }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); RaiseCommands(); } }
    public ICommand SyncCommand { get; }
    public ICommand ExpandCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LinkCommand { get; }
    public ICommand UnlinkCommand { get; }
    public ICommand CopyNumberCommand { get; }
    public ICommand CopyTrackingCommand { get; }
    public ReceiptRowViewModel? SelectedReceipt
    {
        get => _selected;
        set { SetProperty(ref _selected, value); NotifyDetails(); RaiseCommands(); }
    }
    public string DetailsText => BuildDetails(_selected);
    public IReadOnlyList<OrderItem> OrderItems => _selected?.OrderMatch?.Order?.Items ?? [];
    public string ReceiptScopeText => "Checkbox: лише чеки поточного касира. Відсутній чек міг бути створений іншим касиром.";

    public async Task AttachAsync(IReadOnlyList<ReceiptRowViewModel> rows, string account, DateOnly from, DateOnly to)
    {
        _cancel?.Cancel();
        if (_activeSync is not null) await _activeSync;
        _generation++;
        if (_account != account) { _receiptDetails.Clear(); _extraHistory = 0; }
        _rows = rows; _account = account; _from = from; _to = to; _canApply = true;
        SelectedReceipt = rows.FirstOrDefault();
        try
        {
            _config = await _settings.LoadAsync();
            _snapshot = await _sync.LoadCachedAsync(_config.CacheDays);
            _decisions = await _links.LoadAsync(account);
            _coverageComplete = false; // Cached results are not a completed check of this newly selected range.
            ApplyMatches();
        }
        catch (Exception) { _canApply = false; Status = "Не вдалося прочитати локальні дані маркетплейсів. Друк чеків доступний."; }
        RaiseCommands();
    }

    public Task SyncAsync()
    {
        if (IsBusy || _account.Length == 0 || !_canApply) return Task.CompletedTask;
        _activeSync = SyncCoreAsync();
        return _activeSync;
    }

    private async Task SyncCoreAsync()
    {
        IsBusy = true;
        _cancel?.Dispose(); _cancel = new();
        var token = _cancel.Token;
        var generation = _generation;
        Status = "Завантаження замовлень… Друк чеків залишається доступним.";
        _coverageComplete = false;
        try
        {
            _config = await _settings.LoadAsync(token);
            if (generation != _generation) return;
            if (!_config.Connections.Any(c => c.Enabled)) { Status = "Додайте Prom або Rozetka: Налаштування → Маркетплейси."; ApplyMatches(); return; }
            var range = MarketplaceSyncService.BuildRange(_from, _to, Math.Min(3650, _config.HistoryDays + _extraHistory));
            var known = _decisions.Where(d => d.ConfirmedOrder is not null).Select(d => d.ConfirmedOrder!).Distinct().ToArray();
            var snapshot = await _sync.SynchronizeAsync(_config, range, known, token);
            if (generation != _generation) return;
            _snapshot = snapshot;
            var enabled = _config.Connections.Where(c => c.Enabled).ToArray();
            _coverageComplete = enabled.All(c => _snapshot.States.Any(s => s.ConnectionId == c.Id && s.Complete && s.Range == range));
            Status = $"Діапазон замовлень: {range.From:dd.MM.yyyy} — {range.ToExclusive.AddDays(-1):dd.MM.yyyy} (Київ). " +
                string.Join(" | ", enabled.Select(c => { var s = _snapshot.States.FirstOrDefault(s => s.ConnectionId == c.Id); return $"{c.Name}: {s?.Message} Успішне оновлення: {s?.LastSuccessUtc?.ToLocalTime().ToString("dd.MM HH:mm") ?? "немає"}"; }));
            // Return receipts may carry a documented original receipt ID; fetch only these details.
            foreach (var row in _rows.Where(r => r.RawType == ReceiptTypes.Return))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var details = await _details.GetAsync(row.Id, token);
                    if (generation != _generation) return;
                    _receiptDetails[row.Id] = details;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception) { _coverageComplete = false; Status += " Деталі повернення недоступні."; }
            }
            ApplyMatches();
        }
        catch (OperationCanceledException) { _coverageComplete = false; Status = "Оновлення скасовано: перевірка неповна. Друк доступний."; ApplyMatches(); }
        catch (Exception) { _coverageComplete = false; Status = "Перевірка неповна: API або локальний кеш недоступні. Друк доступний."; ApplyMatches(); }
        finally { IsBusy = false; }
    }

    public void InvalidateAccount()
    {
        _generation++; _cancel?.Cancel(); _canApply = false; _account = ""; _decisions = []; _receiptDetails.Clear();
        foreach (var row in _rows) row.OrderMatch = null;
        SelectedReceipt = null; Status = "Касира змінено. Оновіть список чеків."; RaiseCommands();
    }

    private IReadOnlyList<MarketplaceOrder> ActiveOrders => _snapshot.Orders.Where(o =>
        _config.Connections.Any(c => c.Enabled && c.Id == o.Key.ConnectionId && c.Marketplace == o.Key.Marketplace)).ToArray();

    private void ApplyMatches()
    {
        if (!_canApply) return;
        var orders = ActiveOrders;
        foreach (var row in _rows)
            row.OrderMatch = !_config.Connections.Any(c => c.Enabled) && !_decisions.Any(d => d.ReceiptId == row.Id)
                ? new(ReceiptLinkState.NotChecked, null, "Маркетплейси не підключено. Звичайний друк доступний.", [])
                : _matcher.Match(row.Model, _account, orders, _decisions, _coverageComplete, _receiptDetails.GetValueOrDefault(row.Id));
        NotifyDetails(); MatchesChanged?.Invoke(this, EventArgs.Empty); RaiseCommands();
    }

    public bool MatchesFilter(ReceiptRowViewModel row) => Filter switch
    {
        "Prom" => row.OrderMatch?.Order?.Key.Marketplace == MarketplaceKind.Prom,
        "Rozetka" => row.OrderMatch?.Order?.Key.Marketplace == MarketplaceKind.Rozetka,
        "Без зв’язку" => row.OrderMatch?.Order is null,
        "Потрібна перевірка" => row.OrderMatch?.State is ReceiptLinkState.Conflict or ReceiptLinkState.Incomplete or ReceiptLinkState.Candidates or ReceiptLinkState.NotChecked,
        _ => true
    };

    private async Task LinkAsync()
    {
        if (_selected is not { } row || !_canApply) return;
        var generation = _generation;
        try
        {
            if (!_receiptDetails.ContainsKey(row.Id))
                try
                {
                    var details = await _details.GetAsync(row.Id);
                    if (generation != _generation) return;
                    _receiptDetails[row.Id] = details;
                }
                catch (Exception) { Status = "Деталі чека недоступні. Можна порівняти номер, дату та суму."; }
            if (generation != _generation) return;
            var choice = _dialogs.ChooseOrder(row, _receiptDetails.GetValueOrDefault(row.Id), ActiveOrders, row.OrderMatch?.Candidates ?? []);
            if (choice is null) return;
            var previous = _decisions.FirstOrDefault(d => d.ReceiptId == row.Id && d.AccountContext == _account);
            var decision = new ReceiptOrderDecision { ReceiptId = row.Id, AccountContext = _account,
                ConfirmedOrder = previous?.ConfirmedOrder, SuppressAutomatic = previous?.SuppressAutomatic ?? false,
                RejectedOrders = previous?.RejectedOrders.ToList() ?? [] };
            if (choice.Reject) decision.RejectedOrders = decision.RejectedOrders.Append(choice.Key).Distinct().ToList();
            else { decision.ConfirmedOrder = choice.Key; decision.SuppressAutomatic = false; decision.RejectedOrders.RemoveAll(k => k == choice.Key); }
            decision.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _links.SaveDecisionAsync(decision);
            _decisions = await _links.LoadAsync(_account);
            ApplyMatches();
        }
        catch (Exception) { Status = "Не вдалося зберегти локальну прив’язку. Повторіть дію."; }
    }

    private async Task UnlinkAsync()
    {
        if (_selected is not { } row || !_dialogs.ConfirmUnlink()) return;
        try
        {
            var previous = _decisions.FirstOrDefault(d => d.ReceiptId == row.Id && d.AccountContext == _account);
            var rejected = previous?.RejectedOrders.ToList() ?? [];
            if (row.OrderMatch?.Order is { } order) rejected.Add(order.Key);
            await _links.SaveDecisionAsync(new() { AccountContext = _account, ReceiptId = row.Id, SuppressAutomatic = true,
                RejectedOrders = rejected.Distinct().ToList(), UpdatedAtUtc = DateTimeOffset.UtcNow });
            _decisions = await _links.LoadAsync(_account); ApplyMatches();
        }
        catch (Exception) { Status = "Не вдалося зберегти відв’язування."; }
    }

    private string BuildDetails(ReceiptRowViewModel? row)
    {
        if (row is null) return "Оберіть рядок чека для перегляду замовлення.";
        var match = row.OrderMatch;
        if (match?.Order is not { } o) return $"Чек № {row.Serial}, {row.Type}, {row.Total:N2} грн\n{match?.Explanation ?? "Зв’язок ще не перевірено."}";
        var total = o.Total is { } amount ? $"{amount:N2} {o.Currency}" : o.RawTotal.Length > 0 ? o.RawTotal : "не надана";
        var difference = o.Total is { } sum && o.Currency == "UAH" ? $"; різниця: {sum - row.Total:N2} грн" : "; порівняння валют не підтверджене";
        return $"{o.Key.Marketplace} · {o.StoreName} · № {o.Number} · {o.CreatedAt?.ToString("dd.MM.yyyy HH:mm zzz") ?? o.RawCreatedAt}\n" +
            $"Покупець: {o.Buyer?.Name} · {o.Buyer?.Phone}\nОтримувач: {o.Recipient?.Name ?? "не надано API"} · {o.Recipient?.Phone}\n" +
            $"Замовлення: {total}; чек: {row.Total:N2} грн{difference}\n" +
            $"Статус: {o.Status}; оплата: {o.PaymentMethod} / {o.PaymentStatus}\n" +
            $"Доставка: {o.DeliveryMethod}; вартість: {o.DeliveryCost?.ToString("N2") ?? "не надано"}; знижка: {o.Discount?.ToString("N2") ?? "не надано"}\n" +
            string.Join("\n", o.Shipments.Select(s => $"{s.Carrier} · ТТН: {s.TrackingNumber} · {s.Destination}")) +
            $"\n{match.Explanation} · Отримано: {o.RetrievedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}\n" +
            (o.FiscalReceiptNumbers.Count > 0 ? $"Фіскальні номери з API: {string.Join(", ", o.FiscalReceiptNumbers)}" : "");
    }
    private void NotifyDetails() { OnPropertyChanged(nameof(DetailsText)); OnPropertyChanged(nameof(OrderItems)); }
    private void RaiseCommands()
    {
        foreach (var command in new[] { SyncCommand, ExpandCommand, CancelCommand, LinkCommand, UnlinkCommand })
        { if (command is RelayCommand relay) relay.RaiseCanExecuteChanged(); if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged(); }
    }
}

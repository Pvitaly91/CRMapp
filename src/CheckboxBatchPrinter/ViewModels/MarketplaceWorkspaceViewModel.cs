using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
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
    private readonly IFiscalReferenceVerifier? _fiscalVerifier;
    private readonly ReceiptOrderMatchingService _matcher = new();
    private IReadOnlyList<ReceiptRowViewModel> _rows = [];
    private MarketplaceSettings _config = new();
    private MarketplaceSnapshot _snapshot = new([], []);
    private IReadOnlyList<ReceiptOrderDecision> _decisions = [];
    private MarketplaceOrderDeduplication _deduplication = new();
    private IReadOnlyList<ReceiptOrderDecision> ActiveDecisions => _deduplication.NormalizeDecisions(_decisions);
    private readonly Dictionary<string, ReceiptDetails> _receiptDetails = [];
    private readonly HashSet<string> _basketAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<MarketplaceOrderRowViewModel> _orderRows = [];
    private MarketplaceOrderRowViewModel? _selectedOrder;
    private string _orderSearch = "", _orderFilter = "Усі";
    private const string SetupHint = "Інтеграції не налаштовані.\nУсі чеки доступні на вкладці “Усі чеки”.";
    private string _account = "", _status = SetupHint, _filter = "Усі";
    private bool _busy, _attaching, _configurationLoaded, _canApply, _coverageComplete;
    private CancellationTokenSource _scopeCancel = new();
    private CancellationTokenSource? _cancel;
    private Task? _activeSync;
    private Task? _attachment;
    private Task? _automaticTask;
    private CancellationTokenSource? _automaticCancel;
    private bool _active, _autoLinkEnabled;
    private int _automaticGeneration = -1;
    private int _activeSyncGeneration = -1, _attachedGeneration = -1;
    private DateOnly _from = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv), _to = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
    private int _extraHistory;
    private ReceiptRowViewModel? _selected;
    private int _generation;
    private bool _orderDatesValid = true;

    public MarketplaceWorkspaceViewModel(IMarketplaceSettingsStore settings, MarketplaceSyncService sync,
        IReceiptOrderLinkStore links, IReceiptDetailsService details, IOrderLinkDialogService dialogs,
        IFiscalReferenceVerifier? fiscalVerifier = null, bool autoLinkEnabled = true)
    {
        _settings = settings; _sync = sync; _links = links; _details = details; _dialogs = dialogs;
        _fiscalVerifier = fiscalVerifier;
        _autoLinkEnabled = autoLinkEnabled;
        Orders = new ListCollectionView(_orderRows) { Filter = MatchesOrderFilter };
        Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.CreatedAt), ListSortDirection.Descending));
        SyncCommand = new AsyncRelayCommand(_ => SyncAsync(), _ => !IsBusy && (_account.Length > 0 || _orderDatesValid));
        ExpandCommand = new AsyncRelayCommand(async _ => { _extraHistory = Math.Min(_extraHistory + 30, 3650); await SyncAsync(); }, _ => !IsBusy && (_account.Length > 0 || _orderDatesValid));
        CancelCommand = new RelayCommand(_ => _cancel?.Cancel(), _ => IsBusy);
        LinkCommand = new AsyncRelayCommand(_ => LinkAsync(), _ => _account.Length > 0 && _selected is not null && !IsBusy && _canApply && HasEnabledConnections);
        LinkSelectedOrderCommand = new AsyncRelayCommand(_ => LinkAsync(_selectedOrder?.Model), _ =>
            _account.Length > 0 && _selected is not null && _selectedOrder is not null && Orders.Contains(_selectedOrder) && !IsBusy && _canApply && HasEnabledConnections);
        AutoMatchCommand = new AsyncRelayCommand(_ => AutoMatchAsync(), _ => !IsBusy && _account.Length > 0 && _rows.Count > 0 && _coverageComplete);
        UnlinkCommand = new AsyncRelayCommand(_ => UnlinkAsync(), _ => _selected is not null && !IsBusy && _canApply && HasEnabledConnections &&
            (_selected.OrderMatch?.Order is not null || _decisions.Any(d => d.AccountContext == _account && d.ReceiptId == _selected.Id && d.ConfirmedOrder is not null)));
        CopyNumberCommand = new RelayCommand(_ => _dialogs.CopyText(_selected?.OrderMatch?.Order?.Number ?? ""));
        CopyTrackingCommand = new RelayCommand(_ => _dialogs.CopyText(_selected?.OrderMatch?.Order?.TrackingDisplay ?? ""));
    }
    public event EventHandler? MatchesChanged;
    public ICollectionView Orders { get; }
    public IReadOnlyList<string> OrderFilters { get; } = ["Усі", "Prom", "Rozetka", "Без чека", "Є чек", "Ймовірний зв’язок"];
    public string OrderSearch { get => _orderSearch; set { if (SetProperty(ref _orderSearch, value ?? "")) RefreshOrdersView(); } }
    public string OrderFilter { get => _orderFilter; set { if (SetProperty(ref _orderFilter, value)) RefreshOrdersView(); } }
    public MarketplaceOrderRowViewModel? SelectedOrder
    {
        get => _selectedOrder;
        set { if (SetProperty(ref _selectedOrder, value)) RaiseCommands(); }
    }
    public string OrderListSummary => $"Замовлень: {Orders.Cast<object>().Count()} із {_orderRows.Count}. " +
        "Показано всі завантажені замовлення увімкнених магазинів, навіть без чека; дані з кешу можуть бути поза поточним періодом.";
    public IReadOnlyList<string> Filters { get; } = ["Усі", "Prom", "Rozetka", "Без зв’язку", "Потрібна перевірка"];
    public string Filter { get => _filter; set { if (SetProperty(ref _filter, value)) MatchesChanged?.Invoke(this, EventArgs.Empty); } }
    public bool ShowExtraColumns { get; set; }
    public bool AutoLinkEnabled
    {
        get => _autoLinkEnabled;
        set
        {
            if (!SetProperty(ref _autoLinkEnabled, value)) return;
            if (!value) _automaticCancel?.Cancel();
            else
            {
                _automaticGeneration = -1;
                if (_active) _ = OpenAsync();
            }
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    private string _fiscalSummary = "";
    public string FiscalSummary { get => _fiscalSummary; private set => SetProperty(ref _fiscalSummary, value); }
    public bool IsBusy { get => _busy || _attaching; private set { _busy = value; OnPropertyChanged(); RaiseCommands(); } }
    public bool HasEnabledConnections => _config.Connections.Any(c => c.Enabled);
    public bool ShowSetupHint => _configurationLoaded && !HasEnabledConnections;
    public ICommand SyncCommand { get; }
    public ICommand ExpandCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LinkCommand { get; }
    public ICommand LinkSelectedOrderCommand { get; }
    public ICommand AutoMatchCommand { get; }
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

    // Updating Checkbox never reads optional integration files or waits for an old API call.
    public void SetReceiptScope(IReadOnlyList<ReceiptRowViewModel> rows, string account, DateOnly from, DateOnly to)
    {
        var selectedId = _account == account ? _selected?.Id : null;
        _generation++;
        _scopeCancel.Cancel();
        _scopeCancel.Dispose();
        _scopeCancel = new();
        _cancel = null;
        _attachment = null;
        _attachedGeneration = -1;
        _attaching = false;
        IsBusy = false;
        if (_account != account)
        {
            _receiptDetails.Clear(); _extraHistory = 0;
            foreach (var row in rows) row.OrderMatch = null;
        }
        _rows = rows; _account = account; _from = from; _to = to; _canApply = false;
        _basketAttempted.Clear(); _orderDatesValid = to >= from;
        _coverageComplete = false;
        _configurationLoaded = false;
        _config = new(); _snapshot = new([], []); _decisions = [];
        _deduplication = new();
        _orderRows.Clear(); SelectedOrder = null; RefreshOrdersView();
        FiscalSummary = "";
        SelectedReceipt = rows.FirstOrDefault(r => r.Id == selectedId);
        Status = "Замовлення можна перевірити на вкладці «Чеки та замовлення».";
        NotifyConfiguration();
    }

    public async Task AttachAsync(IReadOnlyList<ReceiptRowViewModel> rows, string account, DateOnly from, DateOnly to)
    {
        SetReceiptScope(rows, account, from, to);
        await EnsureAttachedAsync();
    }

    // A marketplace order list does not require a successful Checkbox login or receipt query.
    public void SetOrderDatesWithoutReceipts(DateOnly? from, DateOnly? to)
    {
        if (_account.Length > 0) return;
        var valid = from.HasValue && to.HasValue && to.Value >= from.Value;
        // Clearing a date while an automatic order-only request is running must
        // invalidate its result, even if that backend ignores cancellation.
        if (!valid && _orderDatesValid) SetReceiptScope([], "", _from, _to);
        _orderDatesValid = valid;
        if (_orderDatesValid && (_from != from!.Value || _to != to!.Value))
            SetReceiptScope([], "", from.Value, to!.Value);
        if (!_orderDatesValid) Status = "Оберіть коректні дати «від» і «до» для замовлень.";
        RaiseCommands();
    }

    public void SetActive(bool active)
    {
        _active = active;
        if (!active) _automaticCancel?.Cancel();
    }

    // Only the optional tab opts into network work. One automatic attempt per
    // receipt generation prevents repeated tab events or API errors causing a retry storm.
    public Task OpenAsync()
    {
        if (!_active || !AutoLinkEnabled || (_account.Length == 0 && !_orderDatesValid)) return EnsureAttachedAsync();
        if (_automaticGeneration == _generation) return _automaticTask ?? Task.CompletedTask;
        _automaticGeneration = _generation;
        var previous = _automaticTask;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(_scopeCancel.Token);
        _automaticCancel = cancel;
        return _automaticTask = OpenAutomaticallyAsync(previous, _generation, cancel);
    }

    private async Task OpenAutomaticallyAsync(Task? previous, int generation, CancellationTokenSource cancel)
    {
        using (cancel)
        {
            try
            {
                // A quick off/on toggle must not reuse an already cancelled sync.
                if (previous is { IsCompleted: false }) await previous.WaitAsync(cancel.Token);
                if (generation != _generation || cancel.IsCancellationRequested || !_active || !AutoLinkEnabled) return;
                await EnsureAttachedAsync();
                if (generation != _generation || cancel.IsCancellationRequested || !_active ||
                    !AutoLinkEnabled || !_canApply || !HasEnabledConnections) return;
                await StartSyncAsync(cancel.Token);
            }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(_automaticCancel, cancel)) _automaticCancel = null;
            }
        }
    }

    // Local attachment stays separate: manual mode and Checkbox refresh do not contact APIs.
    public Task EnsureAttachedAsync()
    {
        if (_attachedGeneration == _generation) return Task.CompletedTask;
        if (_attachment is { IsCompleted: false }) return _attachment;
        return _attachment = AttachLocalAsync(_generation, _account, _scopeCancel.Token);
    }

    private async Task AttachLocalAsync(int generation, string account, CancellationToken token)
    {
        _attaching = true; OnPropertyChanged(nameof(IsBusy)); RaiseCommands();
        try
        {
            var config = await _settings.LoadAsync(token);
            if (generation != _generation || token.IsCancellationRequested) return;
            _config = config; _configurationLoaded = true; NotifyConfiguration();
            if (!HasEnabledConnections)
            {
                // Disabling is not deletion: do not open cache, links or credential stores.
                _snapshot = new([], []); _decisions = []; _canApply = true;
                _attachedGeneration = generation; Status = SetupHint; ApplyMatches();
                return;
            }
            var deduplication = await _sync.ResolveDuplicateConnectionsAsync(config, token);
            if (generation != _generation || token.IsCancellationRequested) return;
            _deduplication = deduplication;
            var snapshot = await _sync.LoadCachedAsync(config.CacheDays, token);
            if (generation != _generation || token.IsCancellationRequested) return;
            var decisions = account.Length > 0 ? await _links.LoadAsync(account, token) : [];
            if (generation != _generation || token.IsCancellationRequested) return;
            _snapshot = snapshot; _decisions = decisions; _canApply = true;
            _attachedGeneration = generation;
            _coverageComplete = false; // Cached results are not a completed check of this newly selected range.
            Status = "Локальні дані завантажено. Натисніть «Оновити замовлення» для перевірки API.";
            ApplyMatches();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation != _generation) return;
            _canApply = false;
            Status = "Не вдалося прочитати локальні дані маркетплейсів. Друк чеків доступний.";
        }
        finally
        {
            if (generation == _generation)
            { _attaching = false; OnPropertyChanged(nameof(IsBusy)); NotifyConfiguration(); RaiseCommands(); }
        }
    }

    public Task SyncAsync() => StartSyncAsync(CancellationToken.None);

    private Task StartSyncAsync(CancellationToken automaticCancellation)
    {
        if (_account.Length == 0 && !_orderDatesValid) return Task.CompletedTask;
        if (_activeSyncGeneration == _generation && _activeSync is { IsCompleted: false }) return _activeSync;
        var previous = _activeSync;
        _activeSyncGeneration = _generation;
        _activeSync = SyncCoreAsync(previous, _generation, automaticCancellation);
        return _activeSync;
    }

    private async Task SyncCoreAsync(Task? previous, int generation, CancellationToken automaticCancellation)
    {
        IsBusy = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_scopeCancel.Token, automaticCancellation);
        _cancel = cancel;
        var token = cancel.Token;
        try
        {
            await EnsureAttachedAsync();
            if (generation != _generation || token.IsCancellationRequested || !_canApply) return;
            if (!HasEnabledConnections) { Status = SetupHint; return; }
            var deduplication = await _sync.ResolveDuplicateConnectionsAsync(_config, token);
            if (generation != _generation || token.IsCancellationRequested) return;
            _deduplication = deduplication;
            // A cancelled backend may finish late. Wait only in this optional operation,
            // never in SetReceiptScope or the Checkbox refresh path.
            if (previous is { IsCompleted: false }) await previous.WaitAsync(token);
            token.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            Status = "Завантаження замовлень… Друк чеків залишається доступним.";
            _coverageComplete = false;
            var range = MarketplaceSyncService.BuildRange(_from, _to, Math.Min(3650, _config.HistoryDays + _extraHistory));
            var known = _decisions.Where(d => d.ConfirmedOrder is not null).Select(d => d.ConfirmedOrder!).Distinct().ToArray();
            var snapshot = await _sync.SynchronizeAsync(_config, range, known, token);
            token.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            _snapshot = snapshot;
            if (_fiscalVerifier is not null && _account.Length > 0)
            {
                var verified = await _fiscalVerifier.VerifyAsync(ActiveOrders, _account, token);
                token.ThrowIfCancellationRequested();
                if (generation != _generation) return;
                _snapshot = new(verified, snapshot.States);
            }
            var enabled = _config.Connections.Where(c => c.Enabled).ToArray();
            _coverageComplete = enabled.All(c => _snapshot.States.Any(s => s.ConnectionId == c.Id && s.Complete && s.Range == range));
            Status = $"Діапазон замовлень: {range.From:dd.MM.yyyy} — {range.ToExclusive.AddDays(-1):dd.MM.yyyy} (Київ). " +
                string.Join(" | ", enabled.Select(c => { var s = _snapshot.States.FirstOrDefault(s => s.ConnectionId == c.Id); return $"{c.Name}: {s?.Message} Успішне оновлення: {s?.LastSuccessUtc?.ToLocalTime().ToString("dd.MM HH:mm") ?? "немає"}"; }));
            ApplyMatches(); // Orders are visible before optional Checkbox detail reads finish.
            // Return receipts may carry a documented original receipt ID; fetch only these details.
            foreach (var row in _rows.Where(r => _account.Length > 0 && r.RawType == ReceiptTypes.Return))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var details = await _details.GetAsync(row.Id, token);
                    token.ThrowIfCancellationRequested();
                    if (generation != _generation) return;
                    _receiptDetails[row.Id] = details;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    if (generation != _generation) return;
                    _coverageComplete = false; Status += " Деталі повернення недоступні.";
                }
            }
            if (_coverageComplete && _account.Length > 0) await LoadBasketDetailsAsync(generation, token);
            ApplyMatches();
        }
        catch (OperationCanceledException)
        {
            if (generation != _generation) return;
            _coverageComplete = false; Status = "Оновлення скасовано: перевірка неповна. Друк доступний."; ApplyMatches();
        }
        catch (Exception)
        {
            if (generation != _generation) return;
            _coverageComplete = false; Status = "Перевірка неповна: API або локальний кеш недоступні. Друк доступний."; ApplyMatches();
        }
        finally
        {
            if (generation == _generation && ReferenceEquals(_cancel, cancel)) { _cancel = null; IsBusy = false; }
        }
    }

    public void InvalidateAccount()
    {
        SetReceiptScope(_rows, "", _from, _to);
        foreach (var row in _rows) row.OrderMatch = null;
        SelectedReceipt = null; Status = "Касира змінено. Оновіть список чеків."; RaiseCommands();
    }

    private IReadOnlyList<MarketplaceOrder> ActiveOrders => _deduplication.Merge(_snapshot.Orders.Where(o =>
        _config.Connections.Any(c => c.Enabled && c.Id == o.Key.ConnectionId && c.Marketplace == o.Key.Marketplace)));

    private bool MatchesOrderFilter(object value)
    {
        if (value is not MarketplaceOrderRowViewModel row) return false;
        var included = OrderFilter switch
        {
            "Prom" => row.Key.Marketplace == MarketplaceKind.Prom,
            "Rozetka" => row.Key.Marketplace == MarketplaceKind.Rozetka,
            "Без чека" => !row.HasConfirmedLink,
            "Є чек" => row.HasConfirmedLink,
            "Ймовірний зв’язок" => row.HasSuggestedLink,
            _ => true
        };
        if (!included) return false;
        var query = OrderSearch.Trim();
        if (query.Length == 0) return true;
        var order = row.Model;
        return new[] { row.Number, row.Key.OrderId, row.Marketplace, row.StoreName, row.Status,
            order.Buyer?.Name, order.Buyer?.Phone, order.Recipient?.Name, order.Recipient?.Phone, row.TrackingDisplay }
            .Any(text => text?.Contains(query, StringComparison.CurrentCultureIgnoreCase) == true);
    }

    private void RefreshOrdersView()
    {
        Orders.Refresh();
        if (_selectedOrder is not null && !Orders.Contains(_selectedOrder)) SelectedOrder = null;
        OnPropertyChanged(nameof(OrderListSummary)); RaiseCommands();
    }

    private void RefreshOrderRows(IReadOnlyList<MarketplaceOrder> orders)
    {
        var decisions = ActiveDecisions;
        var unique = orders.DistinctBy(o => o.Key).ToArray();
        var keys = unique.Select(o => o.Key).ToHashSet();
        var existing = _orderRows.ToDictionary(r => r.Key);
        // Keep the view queryable while WPF reevaluates selection/commands after collection changes.
        for (var i = _orderRows.Count - 1; i >= 0; i--)
            if (!keys.Contains(_orderRows[i].Key)) _orderRows.RemoveAt(i);
        foreach (var order in unique)
        {
            if (!existing.TryGetValue(order.Key, out var row))
            { row = new(order); _orderRows.Add(row); }
            row.Update(order, _rows, decisions);
        }
        RefreshOrdersView();
    }

    public Task AutoMatchAsync()
    {
        if (_account.Length == 0 || !_canApply || !_coverageComplete || !HasEnabledConnections) return Task.CompletedTask;
        if (_activeSyncGeneration == _generation && _activeSync is { IsCompleted: false }) return _activeSync;
        _activeSyncGeneration = _generation;
        return _activeSync = AutoMatchCoreAsync(_generation);
    }

    private async Task AutoMatchCoreAsync(int generation)
    {
        IsBusy = true;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_scopeCancel.Token);
        _cancel = cancel;
        try
        {
            await LoadBasketDetailsAsync(generation, cancel.Token);
            if (generation == _generation) ApplyMatches();
        }
        catch (OperationCanceledException)
        {
            if (generation == _generation) Status = "Зіставлення скасовано. Точні й ручні зв’язки збережено.";
        }
        catch (Exception)
        {
            if (generation == _generation) Status = "Товари чеків недоступні. Замовлення й звичайний друк залишаються доступними.";
        }
        finally
        {
            if (generation == _generation && ReferenceEquals(_cancel, cancel)) { _cancel = null; IsBusy = false; }
        }
    }

    private async Task LoadBasketDetailsAsync(int generation, CancellationToken token)
    {
        var orders = ActiveOrders.Where(o => o.Items.Count > 0 && o.ItemsComplete).ToArray();
        var targets = _rows.Where(row => row.RawType == ReceiptTypes.Sell && row.Model.DisplayDate is { } date &&
            orders.Any(o => o.Total == row.Total && o.CreatedAt is { } created && created <= date && created >= date.AddDays(-30)))
            .Where(row => !_receiptDetails.ContainsKey(row.Id))
            .OrderBy(row => _basketAttempted.Contains(row.Id)).ToArray();
        var failed = 0;
        var priorStatus = Status;
        var requested = 0;
        foreach (var row in targets.Take(100))
        {
            token.ThrowIfCancellationRequested();
            _basketAttempted.Add(row.Id);
            Status = $"Зіставлення за товарами: {++requested}/{Math.Min(100, targets.Length)}. Друк доступний.";
            try
            {
                var details = await _details.GetAsync(row.Id, token);
                token.ThrowIfCancellationRequested();
                if (generation != _generation) return;
                _receiptDetails[row.Id] = details;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { if (generation != _generation) return; failed++; }
        }
        if (generation != _generation) return;
        Status = priorStatus + (failed > 0 ? $" Товари {failed} чеків недоступні; такі збіги не підтверджуються." : "") +
            (targets.Length > 100 ? " Досягнуто межі 100 запитів деталей. Натисніть «Зіставити за товарами» для продовження." : "");
    }

    private void ApplyMatches()
    {
        if (!_canApply) return;
        var orders = ActiveOrders;
        var decisions = ActiveDecisions;
        var scope = _rows.Select(row => row.Model).ToArray();
        foreach (var row in _rows)
            row.OrderMatch = !HasEnabledConnections || _account.Length == 0
                ? new(ReceiptLinkState.NotChecked, null, "Маркетплейси не підключено. Звичайний друк доступний.", [])
                : _matcher.Match(row.Model, _account, orders, decisions, _coverageComplete, _receiptDetails.GetValueOrDefault(row.Id), scope, _receiptDetails);
        RefreshOrderRows(orders);
        var linked = _rows.Where(r => r.OrderMatch?.Order is not null).Select(r => r.OrderMatch!.Order!.Key).ToHashSet();
        var unmatchedKeys = orders.Count(o => o.FiscalReferences.Count > 0 && !linked.Contains(o.Key));
        var unavailable = orders.Count(o => o.FiscalDataStatus.Length > 0);
        var repeatedIds = orders.GroupBy(o => (o.Key.Marketplace, o.Key.OrderId))
            .Count(group => group.Select(o => o.Key.ConnectionId).Distinct().Skip(1).Any());
        FiscalSummary = !HasEnabledConnections ? "" : $"Завантажено замовлень: {orders.Count}. Точних зв’язків: {_rows.Count(r => r.OrderMatch?.State == ReceiptLinkState.Exact)}. Ймовірних за товарами: {_rows.Count(r => r.OrderMatch?.State == ReceiptLinkState.Suggested)}. " +
            (unmatchedKeys > 0 ? $"Замовлень із непідтвердженими фіскальними ключами: {unmatchedKeys}. Перевірте контекст каси/продавця, період і права касира; це не означає, що чека немає. " : "") +
            (unavailable > 0 ? $"Фіскальні дані потребують перевірки: {unavailable}. " : "") +
            (repeatedIds > 0 ? "Є однакові API-ID замовлень у різних підключеннях. Перевірте, чи той самий магазин не додано двічі; такі записи не об’єднуються автоматично. " : "") +
            (orders.Any(o => o.Key.Marketplace == MarketplaceKind.Prom && o.FiscalReferences.Count == 0)
                ? "Prom: API не надав точного фіскального ключа для частини замовлень; сума/дата не є автоматичною прив’язкою." : "");
        NotifyDetails(); MatchesChanged?.Invoke(this, EventArgs.Empty); RaiseCommands();
    }

    public bool MatchesFilter(ReceiptRowViewModel row) => Filter switch
    {
        "Prom" => row.OrderMatch?.Order?.Key.Marketplace == MarketplaceKind.Prom,
        "Rozetka" => row.OrderMatch?.Order?.Key.Marketplace == MarketplaceKind.Rozetka,
        "Без зв’язку" => row.OrderMatch?.Order is null,
        "Потрібна перевірка" => row.OrderMatch?.State is ReceiptLinkState.Conflict or ReceiptLinkState.Incomplete or ReceiptLinkState.Candidates or ReceiptLinkState.NotChecked or ReceiptLinkState.Suggested,
        _ => true
    };

    private async Task LinkAsync(MarketplaceOrder? selectedOrder = null)
    {
        if (_account.Length == 0 || _selected is not { } row || !_rows.Contains(row) || !_canApply || !HasEnabledConnections) return;
        var generation = _generation;
        var account = _account;
        try
        {
            if (!_receiptDetails.ContainsKey(row.Id))
                try
                {
                    var details = await _details.GetAsync(row.Id);
                    if (generation != _generation) return;
                    _receiptDetails[row.Id] = details;
                }
                catch (Exception)
                {
                    if (generation != _generation) return;
                    Status = "Деталі чека недоступні. Можна порівняти номер, дату та суму.";
                }
            if (generation != _generation) return;
            var allowed = selectedOrder is null ? ActiveOrders : ActiveOrders.Where(o => o.Key == selectedOrder.Key).ToArray();
            var candidates = selectedOrder is null ? row.OrderMatch?.Candidates ?? [] : allowed;
            var choice = _dialogs.ChooseOrder(row, _receiptDetails.GetValueOrDefault(row.Id), allowed, candidates);
            if (choice is null || generation != _generation) return;
            if (!allowed.Any(order => order.Key == choice.Key)) return;
            var previous = ActiveDecisions.FirstOrDefault(d => d.ReceiptId == row.Id && d.AccountContext == account);
            var decision = new ReceiptOrderDecision { ReceiptId = row.Id, AccountContext = account,
                ConfirmedOrder = previous?.ConfirmedOrder, SuppressAutomatic = previous?.SuppressAutomatic ?? false,
                RejectedOrders = previous?.RejectedOrders.ToList() ?? [] };
            if (choice.Reject) decision.RejectedOrders = decision.RejectedOrders.Append(choice.Key).Distinct().ToList();
            else { decision.ConfirmedOrder = choice.Key; decision.SuppressAutomatic = false; decision.RejectedOrders.RemoveAll(k => k == choice.Key); }
            decision.UpdatedAtUtc = DateTimeOffset.UtcNow;
            await _links.SaveDecisionAsync(decision);
            if (generation != _generation) return;
            var decisions = await _links.LoadAsync(account);
            if (generation != _generation) return;
            _decisions = decisions;
            ApplyMatches();
        }
        catch (Exception) { if (generation == _generation) Status = "Не вдалося зберегти локальну прив’язку. Повторіть дію."; }
    }

    private async Task UnlinkAsync()
    {
        if (_selected is not { } row || !_canApply || !HasEnabledConnections) return;
        var generation = _generation;
        var account = _account;
        if (!_dialogs.ConfirmUnlink() || generation != _generation) return;
        try
        {
            var previous = ActiveDecisions.FirstOrDefault(d => d.ReceiptId == row.Id && d.AccountContext == account);
            var rejected = previous?.RejectedOrders.ToList() ?? [];
            if (row.OrderMatch?.Order is { } order) rejected.Add(order.Key);
            await _links.SaveDecisionAsync(new() { AccountContext = account, ReceiptId = row.Id, SuppressAutomatic = true,
                RejectedOrders = rejected.Distinct().ToList(), UpdatedAtUtc = DateTimeOffset.UtcNow });
            if (generation != _generation) return;
            var decisions = await _links.LoadAsync(account);
            if (generation != _generation) return;
            _decisions = decisions; ApplyMatches();
        }
        catch (Exception) { if (generation == _generation) Status = "Не вдалося зберегти відв’язування."; }
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
            (o.FiscalReceiptNumbers.Count > 0 ? $"Фіскальні номери з API: {string.Join(", ", o.FiscalReceiptNumbers)}" : "") +
            (o.FiscalDataStatus.Length > 0 ? $"\n{o.FiscalDataStatus}" : "");
    }
    private void NotifyDetails() { OnPropertyChanged(nameof(DetailsText)); OnPropertyChanged(nameof(OrderItems)); }
    private void NotifyConfiguration()
    {
        OnPropertyChanged(nameof(HasEnabledConnections)); OnPropertyChanged(nameof(ShowSetupHint)); RaiseCommands();
    }
    private void RaiseCommands()
    {
        foreach (var command in new[] { SyncCommand, ExpandCommand, CancelCommand, LinkCommand, LinkSelectedOrderCommand, AutoMatchCommand, UnlinkCommand })
        { if (command is RelayCommand relay) relay.RaiseCanExecuteChanged(); if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged(); }
    }
}

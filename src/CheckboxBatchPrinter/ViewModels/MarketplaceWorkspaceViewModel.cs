using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MarketplaceWorkspaceViewModel : ObservableObject
{
    private readonly IMarketplaceSettingsStore _settings;
    private readonly MarketplaceSyncService _sync;
    private readonly IReceiptOrderLinkStore _links;
    private readonly IReceiptDetailsService _details;
    private readonly IOrderLinkDialogService _dialogs;
    private readonly IFiscalReferenceVerifier? _fiscalVerifier;
    private readonly CachedReceiptOrderMatchingService _matcher = new();
    private readonly EstablishedReceiptLinkSession _establishedLinks = new();
    private readonly IAutomaticMatchCacheStore? _automaticCacheStore;
    private AutomaticMatchCache _automaticCache = new();
    private bool _allowCachedRestore, _legacyCoverageVerified, _showingCachedMatches;
    private string _cacheNotice = "";
    private IReadOnlyList<ReceiptRowViewModel> _rows = [];
    private MarketplaceSettings _config = new();
    private MarketplaceSnapshot _snapshot = new([], []);
    private IReadOnlyList<ReceiptOrderDecision> _decisions = [];
    private MarketplaceOrderDeduplication _deduplication = new();
    private IReadOnlyList<ReceiptOrderDecision> ActiveDecisions => _deduplication.NormalizeDecisions(_decisions);
    private readonly Dictionary<string, ReceiptDetails> _receiptDetails = [];
    private readonly HashSet<string> _basketAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _loadingDetails = new(StringComparer.OrdinalIgnoreCase);
    private readonly ObservableCollection<MarketplaceOrderRowViewModel> _orderRows = [];
    private bool _updatingOrderRows;
    private MarketplaceOrderRowViewModel? _selectedOrder;
    private string _orderSearch = "", _orderFilter = "Усі";
    private bool _showCanceledOrders;
    private const string SetupHint = "Інтеграції не налаштовані.\nУсі чеки доступні на вкладці “Усі чеки”.";
    private string _account = "", _status = SetupHint, _filter = "Усі";
    private bool _busy, _attaching, _configurationLoaded, _canApply, _coverageComplete, _workspaceRefreshing;
    private CancellationTokenSource _scopeCancel = new();
    private CancellationTokenSource? _cancel;
    private Task? _activeSync;
    private Task? _attachment;
    private Task? _automaticTask;
    private CancellationTokenSource? _automaticCancel;
    private bool _active, _autoLinkEnabled;
    private bool _backgroundSyncing;
    public bool CoordinatorManaged { get; set; }
    public bool ReceiptCoverageFresh { get; set; } = true;
    private int _automaticGeneration = -1;
    private int _activeSyncGeneration = -1, _attachedGeneration = -1;
    private DateOnly _from = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv), _to = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
    private DateOnly? _backgroundFrom, _backgroundTo;
    private readonly List<(DateOnly From, DateOnly To)> _receiptCoverage = [];
    private readonly List<MarketplaceRange> _orderCoverage = [];
    private DateOnly? _displayFrom, _displayTo;
    private int _extraHistory;
    private ReceiptRowViewModel? _selected;
    private int _generation;
    private bool _orderDatesValid = true;
    private bool _detailsExpanded;
    private ShippingLabelsViewModel? _labels;
    public bool DetailsExpanded { get => _detailsExpanded; set => SetProperty(ref _detailsExpanded, value); }

    public MarketplaceWorkspaceViewModel(IMarketplaceSettingsStore settings, MarketplaceSyncService sync,
        IReceiptOrderLinkStore links, IReceiptDetailsService details, IOrderLinkDialogService dialogs,
        IFiscalReferenceVerifier? fiscalVerifier = null, bool autoLinkEnabled = true,
        IAutomaticMatchCacheStore? automaticCache = null)
    {
        _settings = settings; _sync = sync; _links = links; _details = details; _dialogs = dialogs;
        _fiscalVerifier = fiscalVerifier;
        _automaticCacheStore = automaticCache;
        _autoLinkEnabled = autoLinkEnabled;
        Orders = new ListCollectionView(_orderRows) { Filter = MatchesOrderFilter };
        Orders.SortDescriptions.Add(new(nameof(MarketplaceOrderRowViewModel.CreatedAt), ListSortDirection.Descending));
        InitializeProblems();
        Orders.CollectionChanged += (_, _) => UpdateDisplayedOrderNumbers();
        SyncCommand = new AsyncRelayCommand(_ => SyncAsync(), _ => !IsBusy && !IsWorkspaceRefreshing && (_account.Length > 0 || _orderDatesValid));
        ExpandCommand = new AsyncRelayCommand(async _ => { _extraHistory = Math.Min(_extraHistory + 30, 3650); await SyncAsync(); }, _ => !IsBusy && !IsWorkspaceRefreshing && (_account.Length > 0 || _orderDatesValid));
        CancelCommand = new RelayCommand(_ => _cancel?.Cancel(), _ => IsBusy);
        LinkCommand = new AsyncRelayCommand(_ => LinkAsync(), _ => _account.Length > 0 && _selected is not null && !IsBusy && !IsWorkspaceRefreshing && _canApply && HasEnabledConnections);
        LinkSelectedOrderCommand = new AsyncRelayCommand(_ => LinkAsync(_selectedOrder?.Model), _ =>
            _account.Length > 0 && _selected is not null && _selectedOrder is not null && Orders.Contains(_selectedOrder) && !IsBusy && !IsWorkspaceRefreshing && _canApply && HasEnabledConnections);
        ChooseReceiptForOrderCommand = new AsyncRelayCommand(parameter => ChooseReceiptForOrderAsync(parameter as MarketplaceOrderRowViewModel),
            parameter => CanChooseReceiptForOrder(parameter as MarketplaceOrderRowViewModel));
        AutoMatchCommand = new AsyncRelayCommand(_ => RefreshAutoLinksAsync(), _ => !IsBusy && !IsWorkspaceRefreshing && _canApply && HasEnabledConnections && _account.Length > 0 && _rows.Count > 0);
        UnlinkCommand = new AsyncRelayCommand(_ => UnlinkAsync(), _ => _selected is not null && !IsBusy && !IsWorkspaceRefreshing && _canApply && HasEnabledConnections &&
            (_selected.OrderMatch?.Order is not null || _decisions.Any(d => d.AccountContext == _account && d.ReceiptId == _selected.Id && d.ConfirmedOrder is not null)));
        CopyNumberCommand = new RelayCommand(_ => _dialogs.CopyText(_selected?.OrderMatch?.Order?.Number ?? ""));
        CopyTrackingCommand = new RelayCommand(_ => _dialogs.CopyText(_selected?.OrderMatch?.Order?.TrackingDisplay ?? ""));
        ShowOrderCommand = new RelayCommand(parameter =>
        {
            if (parameter is not ReceiptRowViewModel row || !_rows.Contains(row)) return;
            SelectedReceipt = row;
            SelectedOrder = _orderRows.FirstOrDefault(o => o.Key == row.OrderMatch?.Order?.Key);
            DetailsExpanded = true;
        });
    }
    public event EventHandler? MatchesChanged;
    public ICollectionView Orders { get; }
    public IReadOnlyList<MarketplaceOrderRowViewModel> AllOrderRows => _orderRows;
    public ShippingLabelsViewModel? Labels { get => _labels; set => SetProperty(ref _labels, value); }
    public IReadOnlyList<string> OrderFilters { get; } = ["Усі", "Prom", "Rozetka", "Без чека", "Є чек", "Ймовірний зв’язок"];
    public string OrderSearch { get => _orderSearch; set { if (SetProperty(ref _orderSearch, value ?? "")) RefreshOrdersView(); } }
    public string OrderFilter { get => _orderFilter; set { if (SetProperty(ref _orderFilter, value)) RefreshOrdersView(); } }
    public bool ShowCanceledOrders
    {
        get => _showCanceledOrders;
        set { if (SetProperty(ref _showCanceledOrders, value)) RefreshOrdersView(); }
    }
    public MarketplaceOrderRowViewModel? SelectedOrder
    {
        get => _selectedOrder;
        set { if (SetProperty(ref _selectedOrder, value)) RaiseCommands(); }
    }
    public string OrderListSummary => $"Замовлень: {Orders.Cast<object>().Count()} із {_orderRows.Count}. " +
        (ShowCanceledOrders ? "Скасовані показані. " : "Скасовані приховані. ") +
        "Спільний календар фільтрує чеки й замовлення за датою Києва. Прихований архів залишається в кеші та перевірці зв’язків.";
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
    public bool IsBusy { get => _busy || _attaching || _parallelBusy; private set { _busy = value; OnPropertyChanged(); RaiseCommands(); } }
    public bool IsWorkspaceRefreshing
    {
        get => _workspaceRefreshing;
        set
        {
            if (!SetProperty(ref _workspaceRefreshing, value)) return;
            if (value)
            {
                _automaticCancel?.Cancel();
                _cancel?.Cancel();
                _parallelCancellation?.Cancel();
            }
            RaiseCommands();
        }
    }
    public bool HasEnabledConnections => _config.Connections.Any(c => c.Enabled);
    public bool ShowSetupHint => _configurationLoaded && !HasEnabledConnections;
    public ICommand SyncCommand { get; }
    public ICommand ExpandCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand LinkCommand { get; }
    public ICommand LinkSelectedOrderCommand { get; }
    public ICommand ChooseReceiptForOrderCommand { get; }
    public Func<ReceiptRowViewModel, Task>? PreviewReceipt { get; set; }
    public Func<bool>? CanChooseReceipt { get; set; }
    public ICommand AutoMatchCommand { get; }
    public ICommand UnlinkCommand { get; }
    public ICommand CopyNumberCommand { get; }
    public ICommand CopyTrackingCommand { get; }
    public ICommand ShowOrderCommand { get; }
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
            _automaticCache = new();
            foreach (var row in rows) row.OrderMatch = null;
        }
        _receiptDetails.Clear(); // Restored only after validating each receipt fingerprint.
        _allowCachedRestore = false; _legacyCoverageVerified = false; _showingCachedMatches = false; _cacheNotice = "";
        _rows = rows; _account = account; _from = from; _to = to; _canApply = false;
        _receiptCoverage.Clear(); _orderCoverage.Clear();
        _receiptCoverage.Add((from, to));
        _basketAttempted.Clear(); _orderDatesValid = to >= from;
        _loadingDetails.Clear();
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

    // Background upsert retains the existing marketplace snapshot and user decisions.
    public void UpdateReceiptScope(IReadOnlyList<ReceiptRowViewModel> rows, string account, DateOnly from, DateOnly to,
        bool background = false, bool verifiedCoverage = true)
    {
        if (!CoordinatorManaged && !IsWorkspaceRefreshing) { SetReceiptScope(rows, account, from, to); return; }
        if (_account != account)
        {
            SetReceiptScope(rows, account, background ? _displayFrom ?? from : from, background ? _displayTo ?? to : to);
            if (!verifiedCoverage) _receiptCoverage.Clear();
            if (background)
            {
                _receiptCoverage.Clear();
                if (verifiedCoverage) RememberReceiptCoverage(from, to);
                ApplyMatches(true);
            }
            return;
        }
        _rows = rows;
        if (verifiedCoverage) RememberReceiptCoverage(from, to);
        if (!background)
        {
            var changedRange = _from != from || _to != to;
            _from = from; _to = to; _coverageComplete = false;
            if (changedRange && _canApply && _account.Length > 0)
            {
                var range = MatchScope.OrderRange;
                _legacyCoverageVerified = _config.Connections.Where(c => c.Enabled).All(c =>
                    _snapshot.States.Any(s => s.ConnectionId == c.Id && s.Complete && s.Range == range));
                _allowCachedRestore = _legacyCoverageVerified ||
                    _matcher.HasVerifiedSnapshot(_automaticCache, _account, MatchScope, EnabledConnectionIds);
            }
        }
        ApplyMatches(background);
    }

    // Settings changes require a fresh local attachment without losing the visible archive or marks.
    public void InvalidateConfiguration()
    {
        _configurationVersion++;
        _parallelCancellation?.Cancel();
        // Invalidate before cancellation: a backend may finish while cancellation is being signaled.
        _generation++;
        _automaticCancel?.Cancel();
        _cancel?.Cancel();
        _scopeCancel.Cancel();
        _scopeCancel.Dispose();
        _scopeCancel = new();
        _automaticCancel = null;
        _cancel = null;
        _attachment = null;
        _attachedGeneration = -1;
        _automaticGeneration = -1;
        _activeSyncGeneration = -1;
        // Retain old task references so the next sync can wait for a canceled backend to finish.
        _attaching = false;
        _configurationLoaded = false;
        _canApply = false;
        _coverageComplete = false;
        _orderCoverage.Clear();
        _deduplication = new();
        _allowCachedRestore = false;
        _legacyCoverageVerified = false;
        _showingCachedMatches = false;
        _cacheNotice = "";
        _basketAttempted.Clear();
        _loadingDetails.Clear();
        _backgroundSyncing = false;
        IsBusy = false;
        Status = "Налаштування маркетплейсів збережено; дані підключень буде оновлено.";
        NotifyConfiguration();
    }

    public void InvalidateReceiptDetails(string receiptId)
    {
        _receiptDetails.Remove(receiptId);
        _basketAttempted.Remove(receiptId);
        _loadingDetails.Remove(receiptId);
        if (_rows.FirstOrDefault(r => r.Id == receiptId) is { } row && IsInRange(row, _from, _to))
            _coverageComplete = false;
    }

    public async Task SyncBackgroundAsync(CancellationToken cancellationToken = default)
    {
        if (IsWorkspaceRefreshing) throw new BackgroundSourceSkippedException("Триває оновлення всіх даних");
        await EnsureAttachedAsync();
        cancellationToken.ThrowIfCancellationRequested();
        if (IsWorkspaceRefreshing) throw new BackgroundSourceSkippedException("Триває оновлення всіх даних");
        if (!HasEnabledConnections) throw new BackgroundSourceSkippedException("Не налаштовано");
        _backgroundSyncing = true;
        try { await StartSyncAsync(cancellationToken, background: true); }
        finally { _backgroundSyncing = false; }
        var incomplete = _config.Connections.Where(c => c.Enabled)
            .Select(c => _snapshot.States.FirstOrDefault(s => s.ConnectionId == c.Id))
            .Where(s => s is null || !s.Complete).ToArray();
        if (incomplete.Length == 0) return;
        var authorization = incomplete.FirstOrDefault(s => s?.FailureStatus is
            System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden);
        if (authorization is not null) throw new ApiException("Потрібна повторна перевірка облікових даних маркетплейсу.", authorization.FailureStatus);
        var limited = incomplete.FirstOrDefault(s => s?.FailureStatus == (System.Net.HttpStatusCode)429);
        if (limited is not null) throw new ApiException("Маркетплейс обмежив частоту запитів.", limited.FailureStatus,
            retryAfter: limited.RetryAfter);
        throw new InvalidOperationException("Перевірка маркетплейсів неповна.");
    }

    public void SetBackgroundRange(DateOnly from, DateOnly to)
    {
        _backgroundFrom = from; _backgroundTo = to;
    }

    public OrderNotificationSnapshot GetOrderNotificationSnapshot(DateTimeOffset? completedSince = null)
    {
        var range = _backgroundFrom is { } from && _backgroundTo is { } to ? ScopeFor(from, to).OrderRange : null;
        var sources = _config.Connections.Where(c => c.Enabled)
            .GroupBy(c => _deduplication.NormalizeKey(new(c.Marketplace, c.Id, "notification-source")))
            .Select(group =>
            {
                var states = _snapshot.States.Where(s => group.Any(c => c.Id == s.ConnectionId)).ToArray();
                var complete = states.Any(s => s.Complete && (completedSince is null ||
                    s.Range == range && s.AttemptedAtUtc >= completedSince));
                return new NotificationSourceSnapshot(new(group.Key.Marketplace, group.Key.ConnectionId),
                    complete, states.MaxBy(s => s.LastSuccessUtc)?.LastSuccessUtc);
            }).ToArray();
        return new(ActiveOrders, sources);
    }
    public void CancelPendingOperations()
    {
        _parallelCancellation?.Cancel();
        _cancel?.Cancel();
        _automaticCancel?.Cancel();
        _scopeCancel.Cancel();
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
        if ((!valid && _orderDatesValid) || valid && (_from != from!.Value || _to != to!.Value))
        {
            // Invalidate old asynchronous work without throwing away the visible local archive.
            _generation++;
            _scopeCancel.Cancel(); _scopeCancel.Dispose(); _scopeCancel = new();
            _cancel = null; _attachment = null; _attaching = false; IsBusy = false;
            _attachedGeneration = _canApply ? _generation : -1;
            _coverageComplete = false; _allowCachedRestore = false; _legacyCoverageVerified = false;
            if (valid) { _from = from!.Value; _to = to!.Value; }
        }
        _orderDatesValid = valid;
        if (!_orderDatesValid) Status = "Оберіть коректні дати «від» і «до» для замовлень.";
        RaiseCommands();
    }

    public void SetDisplayDates(DateOnly? from, DateOnly? to)
    {
        _displayFrom = from; _displayTo = to;
        RefreshOrdersView(); // A view filter only: ActiveOrders and the matching graph stay intact.
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
        if (CoordinatorManaged || IsWorkspaceRefreshing) return EnsureAttachedAsync();
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
                if (generation != _generation || cancel.IsCancellationRequested || !_active || !AutoLinkEnabled || IsWorkspaceRefreshing) return;
                await EnsureAttachedAsync();
                if (generation != _generation || cancel.IsCancellationRequested || !_active ||
                    !AutoLinkEnabled || IsWorkspaceRefreshing || !_canApply || !HasEnabledConnections) return;
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
            var enabledIds = config.Connections.Where(c => c.Enabled).Select(c => c.Id).ToArray();
            foreach (var covered in snapshot.States.Where(s => s.Complete).Select(s => s.Range).Distinct())
                if (enabledIds.All(id => snapshot.States.Any(s => s.ConnectionId == id && s.Complete && s.Range == covered)))
                    _orderCoverage.Add(covered);
            await LoadAutomaticCacheAsync(generation, account, token);
            if (generation != _generation || token.IsCancellationRequested) return;
            _attachedGeneration = generation;
            _coverageComplete = false; // Cached results are not a completed check of this newly selected range.
            var range = MatchScope.OrderRange;
            _legacyCoverageVerified = _config.Connections.Where(c => c.Enabled).All(c => snapshot.States.Any(s =>
                s.ConnectionId == c.Id && s.Complete && s.Range == range));
            _allowCachedRestore = _legacyCoverageVerified ||
                account.Length > 0 && _matcher.HasVerifiedSnapshot(_automaticCache, account, MatchScope, EnabledConnectionIds);
            Status = "Локальні дані завантажено. Натисніть «Оновити все» для перевірки API.";
            ApplyMatches();
            await FlushAcceptedLinksAsync();
            if (generation != _generation || token.IsCancellationRequested) return;
            if (_showingCachedMatches) Status = "Автоприв’язки відновлено з кешу. Оновлення перевірить нові й змінені документи.";
            Status += _cacheNotice;
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

    /// <summary>Explicitly check the selected period, even when Checkbox could not be refreshed.</summary>
    public async Task RefreshAllAsync(DateOnly from, DateOnly to, CancellationToken token = default)
    {
        if (to < from) throw new ArgumentException("Дата початку має бути не пізнішою за дату завершення.");
        token.ThrowIfCancellationRequested();
        _automaticCancel?.Cancel();
        _cancel?.Cancel();
        // A background flight may target a different period. Its completion is not
        // a completed user refresh, and a late response must finish before this one.
        if (_activeSync is { IsCompleted: false } previous) await previous.WaitAsync(token);
        token.ThrowIfCancellationRequested();
        _from = from;
        _to = to;
        _orderDatesValid = true;
        _coverageComplete = false;
        var range = MatchScope.OrderRange;
        _legacyCoverageVerified = _canApply && _config.Connections.Where(c => c.Enabled).All(c =>
            _snapshot.States.Any(s => s.ConnectionId == c.Id && s.Complete && s.Range == range));
        _allowCachedRestore = _account.Length > 0 && (_legacyCoverageVerified ||
            _matcher.HasVerifiedSnapshot(_automaticCache, _account, MatchScope, EnabledConnectionIds));
        // Existing verified receipt coverage remains available. A failed Checkbox
        // request never adds coverage merely because the calendar range changed.
        await StartSyncAsync(token);
        token.ThrowIfCancellationRequested();
    }

    private Task StartSyncAsync(CancellationToken automaticCancellation, bool background = false)
    {
        if (!background && _account.Length == 0 && !_orderDatesValid) return Task.CompletedTask;
        if (_activeSyncGeneration == _generation && _activeSync is { IsCompleted: false }) return _activeSync;
        var previous = _activeSync;
        _activeSyncGeneration = _generation;
        _activeSync = SyncCoreAsync(previous, _generation, automaticCancellation, background);
        return _activeSync;
    }

    private async Task SyncCoreAsync(Task? previous, int generation, CancellationToken automaticCancellation, bool background)
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
            if (!background)
            {
                _coverageComplete = false;
                ApplyMatches(); // Keep only previously validated local pairs while the API is in flight.
            }
            var syncFrom = background ? _backgroundFrom ?? _from : _from;
            var syncTo = background ? _backgroundTo ?? _to : _to;
            var range = MarketplaceSyncService.BuildRange(syncFrom, syncTo, Math.Min(3650, _config.HistoryDays + _extraHistory));
            var known = _decisions.Where(d => d.ConfirmedOrder is not null).Select(d => d.ConfirmedOrder!).Distinct().ToArray();
            var snapshot = await _sync.SynchronizeAsync(_config, range, known, token);
            token.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            using var fiscalPublication = BlockFiscalPublication();
            var returnsComplete = true;
            // Return receipts may carry a documented original receipt ID; fetch only these details.
            foreach (var row in _rows.Where(r => ReceiptCoverageFresh && _account.Length > 0 && r.RawType == ReceiptTypes.Return && !_receiptDetails.ContainsKey(r.Id) &&
                (!background || IsInRange(r, syncFrom, syncTo))))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var details = await _details.GetAsync(row.Id, token);
                    token.ThrowIfCancellationRequested();
                    if (generation != _generation) return;
                    RememberDetails(row, details);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception)
                {
                    if (generation != _generation) return;
                    returnsComplete = false;
                }
            }
            if (_fiscalVerifier is not null && _account.Length > 0)
            {
                var activeOrders = ActiveOrdersFrom(snapshot);
                // Verification publishes canonical rows only. Validate saved alias
                // pairs while the source snapshot still contains their original keys.
                _matcher.NormalizeConnectionAliases(_automaticCache, _account, snapshot.Orders, activeOrders, _deduplication.NormalizeKey);
                var verified = await VerifyFiscalReferencesInPeriodAsync(activeOrders, syncFrom, syncTo, token);
                token.ThrowIfCancellationRequested();
                if (generation != _generation) return;
                snapshot = new(verified, snapshot.States);
            }
            token.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            fiscalPublication?.Dispose();
            _snapshot = snapshot;
            var enabled = _config.Connections.Where(c => c.Enabled).ToArray();
            var ordersComplete = enabled.All(c => _snapshot.States.Any(s => s.ConnectionId == c.Id && s.Complete && s.Range == range));
            if (!ordersComplete && !background)
                _orderCoverage.RemoveAll(r => r.From <= range.From && r.ToExclusive >= range.ToExclusive);
            if (ordersComplete && !_orderCoverage.Contains(range))
            {
                _orderCoverage.Add(range);
                if (_orderCoverage.Count > 256)
                {
                    var old = _orderCoverage.FindIndex(r =>
                        !(r.From <= MatchScope.OrderRange!.From && r.ToExclusive >= MatchScope.OrderRange.ToExclusive));
                    _orderCoverage.RemoveAt(old < 0 ? 0 : old);
                }
            }
            _coverageComplete = HasCoverage(_from, _to) && returnsComplete;
            Status = $"Діапазон замовлень: {range.From:dd.MM.yyyy} — {range.ToExclusive.AddDays(-1):dd.MM.yyyy} (Київ). " +
                string.Join(" | ", enabled.Select(c => { var s = _snapshot.States.FirstOrDefault(s => s.ConnectionId == c.Id); return $"{c.Name}: {s?.Message} Успішне оновлення: {s?.LastSuccessUtc?.ToLocalTime().ToString("dd.MM HH:mm") ?? "немає"}"; })) +
                (returnsComplete ? "" : " Деталі повернення недоступні.");
            _loadingDetails.UnionWith(DetailTargets(syncFrom, syncTo).Take(100).Select(r => r.Id));
            ApplyMatches();
            await FlushAcceptedLinksAsync();
            token.ThrowIfCancellationRequested();
            if (generation != _generation) return;
            if (ordersComplete && ReceiptCoverageFresh && _account.Length > 0 && HasReceiptCoverage(syncFrom, syncTo))
                await LoadBasketDetailsAsync(generation, token, syncFrom, syncTo);
            ApplyMatches();
        }
        catch (OperationCanceledException)
        {
            if (generation != _generation) return;
            if (!background) _coverageComplete = false;
            Status = "Оновлення скасовано: перевірка неповна. Друк доступний."; ApplyMatches();
        }
        catch (Exception)
        {
            if (generation != _generation) return;
            if (!background) _coverageComplete = false;
            Status = "Перевірка неповна: API або локальний кеш недоступні. Друк доступний."; ApplyMatches();
        }
        finally
        {
            if (generation == _generation && ReferenceEquals(_cancel, cancel))
            {
                _loadingDetails.Clear(); ApplyMatches();
                await FlushAcceptedLinksAsync();
                await SaveAutomaticCacheAsync(generation, token);
                if (generation == _generation && ReferenceEquals(_cancel, cancel)) { _cancel = null; IsBusy = false; }
            }
        }
    }

    public void InvalidateAccount()
    {
        _parallelAccountVersion++;
        _parallelCancellation?.Cancel();
        SetReceiptScope(_rows, "", _from, _to);
        foreach (var row in _rows) row.OrderMatch = null;
        SelectedReceipt = null; Status = "Касира змінено. Оновіть список чеків."; RaiseCommands();
    }

    internal OrderKey NormalizeOrderKey(OrderKey key) => _deduplication.NormalizeKey(key);
    internal (int Generation, string Account, DateOnly From, DateOnly To, DateOnly? DisplayFrom, DateOnly? DisplayTo) SelectionScope =>
        (_generation, _account, _from, _to, _displayFrom, _displayTo);
    private IReadOnlyList<MarketplaceOrder> ActiveOrders => ActiveOrdersFrom(_snapshot);
    private IReadOnlyList<MarketplaceOrder> ActiveOrdersFrom(MarketplaceSnapshot snapshot) => _deduplication.Merge(snapshot.Orders).Where(o =>
        _config.Connections.Any(c => c.Enabled && c.Id == o.Key.ConnectionId && c.Marketplace == o.Key.Marketplace)).ToArray();

    private bool MatchesOrderFilter(object value)
    {
        if (value is not MarketplaceOrderRowViewModel row) return false;
        if (!ShowCanceledOrders && row.Model.IsCanceled) return false;
        if (_displayFrom.HasValue || _displayTo.HasValue)
        {
            if (row.Model.CreatedAt is not { } created) return false;
            var date = DateRangeBuilder.KyivDate(created);
            if (_displayFrom is { } from && date < from || _displayTo is { } to && date > to) return false;
        }
        var included = OrderFilter switch
        {
            "Prom" => row.Key.Marketplace == MarketplaceKind.Prom,
            "Rozetka" => row.Key.Marketplace == MarketplaceKind.Rozetka,
            "Без чека" => !row.HasConfirmedLink && !row.HasSuggestedLink,
            "Є чек" => row.HasConfirmedLink || row.HasSuggestedLink,
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
        RefreshProblems();
        if (_selectedOrder is not null && !Orders.Contains(_selectedOrder)) SelectedOrder = null;
        OnPropertyChanged(nameof(OrderListSummary)); RaiseCommands();
    }

    private void UpdateDisplayedOrderNumbers()
    {
        if (_updatingOrderRows) return;
        var visible = new HashSet<MarketplaceOrderRowViewModel>();
        var index = 0;
        foreach (var row in Orders.Cast<MarketplaceOrderRowViewModel>())
        {
            row.DisplayIndex = ++index;
            visible.Add(row);
        }
        foreach (var row in _orderRows)
            if (!visible.Contains(row)) row.DisplayIndex = 0;
    }

    private void RefreshOrderRows(IReadOnlyList<MarketplaceOrder> orders)
    {
        var decisions = ActiveDecisions;
        var unique = orders.DistinctBy(o => o.Key).ToArray();
        var keys = unique.Select(o => o.Key).ToHashSet();
        var markedKeys = _orderRows.Where(row => row.IsSelectedForLabels)
            .Select(row => _deduplication.NormalizeKey(row.Key)).ToHashSet();
        var existing = _orderRows.ToDictionary(r => r.Key);
        // Renumber once after a source batch, rather than once for every inserted row.
        _updatingOrderRows = true;
        try
        {
            // Keep the view queryable while WPF reevaluates selection/commands after collection changes.
            for (var i = _orderRows.Count - 1; i >= 0; i--)
                if (!keys.Contains(_orderRows[i].Key)) _orderRows.RemoveAt(i);
            foreach (var order in unique)
            {
                if (!existing.TryGetValue(order.Key, out var row))
                { row = new(order); _orderRows.Add(row); }
                row.Update(order, _rows, decisions);
                if (markedKeys.Contains(order.Key)) row.IsSelectedForLabels = true;
            }
        }
        finally { _updatingOrderRows = false; }
        if (Labels is not null) _ = Labels.RestoreHistoryAsync();
        RefreshOrdersView();
    }

    // Refresh the source snapshot first; cached probable links never reserve a node or
    // prevent a newly arrived document from invalidating an old unique pair.
    public Task RefreshAutoLinksAsync() => SyncAsync();

    public Task AutoMatchAsync()
    {
        if (_account.Length == 0 || !ReceiptCoverageFresh || !_canApply || !_coverageComplete || !HasEnabledConnections) return Task.CompletedTask;
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
            if (generation == _generation && ReferenceEquals(_cancel, cancel))
            {
                _loadingDetails.Clear(); ApplyMatches();
                await FlushAcceptedLinksAsync();
                await SaveAutomaticCacheAsync(generation, cancel.Token);
                if (generation == _generation && ReferenceEquals(_cancel, cancel)) { _cancel = null; IsBusy = false; }
            }
        }
    }

    private AmountMatchScope MatchScope => ScopeFor(_from, _to);

    private AmountMatchScope ScopeFor(DateOnly from, DateOnly to)
    {
        var days = Math.Min(3650, _config.HistoryDays + _extraHistory);
        var range = MarketplaceSyncService.BuildRange(from, to, days);
        return new(days, range,
            $"Для автозв’язку: однакова сума та той самий календарний день (Київ). Чеки {from:dd.MM.yyyy} — {to:dd.MM.yyyy}; завантажена історія замовлень {range.From:dd.MM.yyyy} — {range.ToExclusive.AddDays(-1):dd.MM.yyyy}, усі увімкнені магазини. Фільтри таблиць не звужують перевірку.");
    }

    private static bool IsInRange(ReceiptRowViewModel row, DateOnly from, DateOnly to) =>
        row.Model.DisplayDate is { } date && DateRangeBuilder.KyivDate(date) is var day && day >= from && day <= to;

    private void RememberReceiptCoverage(DateOnly from, DateOnly to)
    {
        if (_receiptCoverage.Contains((from, to))) return;
        _receiptCoverage.Add((from, to));
        if (_receiptCoverage.Count > 256)
        {
            var old = _receiptCoverage.FindIndex(r => !(r.From <= _from && r.To >= _to));
            _receiptCoverage.RemoveAt(old < 0 ? 0 : old);
        }
    }

    private bool HasReceiptCoverage(DateOnly from, DateOnly to) =>
        _receiptCoverage.Any(r => r.From <= from && r.To >= to);

    private bool HasCoverage(DateOnly from, DateOnly to)
    {
        var range = ScopeFor(from, to).OrderRange!;
        return HasReceiptCoverage(from, to) &&
            _orderCoverage.Any(r => r.From <= range.From && r.ToExclusive >= range.ToExclusive);
    }

    private IReadOnlyList<string> EnabledConnectionIds => _config.Connections.Where(c => c.Enabled)
        .Select(c => $"{c.Marketplace}:{c.Id}").Order(StringComparer.Ordinal).ToArray();

    private ReceiptRowViewModel[] DetailTargets(DateOnly? from = null, DateOnly? to = null)
    {
        if (_account.Length == 0 || _rows.Count == 0) return [];
        var receiptFrom = from ?? _from;
        var receiptTo = to ?? _to;
        var range = MarketplaceSyncService.BuildRange(receiptFrom, receiptTo,
            Math.Min(3650, _config.HistoryDays + _extraHistory));
        var activeOrders = ActiveOrders;
        _matcher.NormalizeConnectionAliases(_automaticCache, _account, _snapshot.Orders, activeOrders, _deduplication.NormalizeKey);
        var linkedOrders = _matcher.LinkedOrderKeys(_automaticCache, _account, _rows.Select(r => r.Model).ToArray(),
            activeOrders, ActiveDecisions).ToHashSet();
        linkedOrders.UnionWith(_rows.Where(r => r.OrderMatch?.Order is not null && r.OrderMatch.Products != ProductComparison.Loading)
            .Select(r => r.OrderMatch!.Order!.Key));
        linkedOrders.UnionWith(ActiveDecisions.Where(d => d.AccountContext == _account && d.ConfirmedOrder is not null)
            .Select(d => d.ConfirmedOrder!));
        var orders = activeOrders.Where(o => AutomaticReceiptLinkPolicy.CanUseAmountFallback(o) &&
            !o.IsCanceled && !linkedOrders.Contains(o.Key) && o.Items.Count > 0).ToArray();
        return _rows.Where(row => row.RawType == ReceiptTypes.Sell && row.Model.Status == "DONE" && row.Model.DisplayDate is { } date &&
            (row.OrderMatch?.Order is null || row.OrderMatch.Products == ProductComparison.Loading) &&
            IsInRange(row, receiptFrom, receiptTo) &&
            orders.Any(o => o.Total == row.Total && o.CreatedAt is { } created && created <= date &&
                DateRangeBuilder.KyivDate(created) == DateRangeBuilder.KyivDate(date) && created >= range.From && created < range.ToExclusive))
            .Where(row => !_receiptDetails.ContainsKey(row.Id))
            .OrderBy(row => _basketAttempted.Contains(row.Id)).ToArray();
    }

    private async Task LoadBasketDetailsAsync(int generation, CancellationToken token, DateOnly? from = null, DateOnly? to = null)
    {
        var targets = DetailTargets(from, to);
        _loadingDetails.UnionWith(targets.Take(100).Select(r => r.Id));
        ApplyMatches();
        var failed = 0;
        var priorStatus = Status;
        var requested = 0;
        foreach (var row in targets.Take(100))
        {
            token.ThrowIfCancellationRequested();
            _basketAttempted.Add(row.Id);
            Status = $"Зіставлення за сумою й товарами: {++requested}/{Math.Min(100, targets.Length)}. Друк доступний.";
            try
            {
                var details = await _details.GetAsync(row.Id, token);
                token.ThrowIfCancellationRequested();
                if (generation != _generation) return;
                RememberDetails(row, details);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception) { if (generation != _generation) return; failed++; }
            finally { if (generation == _generation) _loadingDetails.Remove(row.Id); }
        }
        if (generation != _generation) return;
        Status = priorStatus + (failed > 0 ? $" Товари {failed} чеків недоступні; можливий лише ймовірний зв’язок за взаємно унікальною сумою." : "") +
            (targets.Length > 100 ? " Досягнуто межі 100 запитів деталей. Натисніть «Оновити все» для продовження." : "");
    }

    private async Task LoadAutomaticCacheAsync(int generation, string account, CancellationToken token)
    {
        if (account.Length == 0) return;
        if (_automaticCacheStore is not null)
        {
            try
            {
                var cache = await _automaticCacheStore.LoadAsync(account, _config.CacheDays, token);
                if (generation != _generation || token.IsCancellationRequested) return;
                _automaticCache = cache;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                if (generation != _generation) return;
                _automaticCache = new();
                _cacheNotice = " Кеш автоприв’язок недоступний; результат буде перевірено заново.";
            }
        }
        foreach (var row in _rows)
            if (_matcher.TryGetDetails(_automaticCache, account, row.Model, out var details))
                _receiptDetails[row.Id] = details;
    }

    private void RememberDetails(ReceiptRowViewModel row, ReceiptDetails details)
    {
        _receiptDetails[row.Id] = details;
        _matcher.RememberDetails(_automaticCache, _account, row.Model, details);
    }

    private async Task SaveAutomaticCacheAsync(int generation, CancellationToken token)
    {
        if (_automaticCacheStore is null || _account.Length == 0 || generation != _generation || token.IsCancellationRequested) return;
        var account = _account;
        var cache = _automaticCache;
        var exactRevision = cache.ExactLinksRevision;
        try
        {
            await _automaticCacheStore.SaveAsync(account, cache, _config.CacheDays, token);
            if (cache.ExactLinksRevision == exactRevision) cache.ExactLinksChanged = false;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (generation != _generation) return;
            _cacheNotice = " Не вдалося зберегти кеш автоприв’язок; поточні результати доступні, після перезапуску потрібна перевірка.";
            Status += _cacheNotice;
        }
    }

    // Accepted local evidence must reach disk before a later network operation can
    // be cancelled. Account/generation checks and the store's copy still isolate the write.
    public Task FlushAcceptedLinksAsync() => _canApply && _automaticCache.ExactLinksChanged
        ? SaveAutomaticCacheAsync(_generation, CancellationToken.None) : Task.CompletedTask;

    private void ApplyMatches(bool? backgroundOnly = null)
    {
        if (!_canApply) return;
        if (!_parallelPublishing && _pendingRefreshPublications.Any(block => block.IsCurrent)) return;
        if (_pendingFiscalPublications.Any(block => block.IsCurrent)) return;
        if (_parallelPublishing && Labels?.IsPrintOperationActive == true) return;
        var backgroundMode = backgroundOnly ?? _backgroundSyncing;
        var orders = ActiveOrders;
        _matcher.NormalizeConnectionAliases(_automaticCache, _account, _snapshot.Orders, orders, _deduplication.NormalizeKey);
        var decisions = ActiveDecisions;
        var established = _establishedLinks.Restore(_automaticCache, _account, _rows.Select(row => row.Model).ToArray(),
            orders, decisions, EnabledConnectionIds);
        orders = established.Orders;
        var interactiveCoverageComplete = _coverageComplete && ReceiptCoverageFresh;
        var appliedCoverageComplete = interactiveCoverageComplete;
        var appliedScopeDescription = MatchScope.Description;
        _showingCachedMatches = false;
        var matches = new Dictionary<string, ReceiptOrderMatch>(StringComparer.OrdinalIgnoreCase);
        if (HasEnabledConnections && _account.Length > 0)
        {
            if (!backgroundMode)
            {
                var interactive = _rows.Where(r => IsInRange(r, _from, _to)).ToArray();
                foreach (var (id, match) in CalculateMatches(interactive, MatchScope, interactiveCoverageComplete,
                             orders, decisions, _legacyCoverageVerified, establishedExactLinks: established.Matches)) matches[id] = match;
            }
            if (_backgroundFrom is { } backgroundFrom && _backgroundTo is { } backgroundTo)
            {
                // Same-day matching makes these disjoint date groups independent. Never
                // let incomplete interactive coverage override a verified recent graph.
                var recent = _rows.Where(r => IsInRange(r, backgroundFrom, backgroundTo)).ToArray();
                var scope = ScopeFor(backgroundFrom, backgroundTo);
                var freshCoverage = HasCoverage(backgroundFrom, backgroundTo) &&
                    (!CoordinatorManaged || ReceiptCoverageFresh) &&
                    _config.Connections.Where(c => c.Enabled).All(c => _snapshot.States.Any(s =>
                        s.ConnectionId == c.Id && s.Complete && s.Range == scope.OrderRange));
                if (backgroundMode)
                {
                    appliedCoverageComplete = freshCoverage;
                    appliedScopeDescription = scope.Description;
                }
                foreach (var (id, match) in CalculateMatches(recent, scope,
                             freshCoverage, orders, decisions, false, restoreBackgroundCache: true,
                             establishedExactLinks: established.Matches))
                    if (!matches.ContainsKey(id)) matches[id] = match;
            }
        }
        foreach (var (id, retained) in established.Matches)
        {
            if (matches.TryGetValue(id, out var current) &&
                (current.State is ReceiptLinkState.Manual or ReceiptLinkState.Conflict ||
                 current.State == ReceiptLinkState.Exact && current.Order?.Key != retained.Order?.Key)) continue;
            matches[id] = retained;
        }
        foreach (var row in _rows)
            row.OrderMatch = !HasEnabledConnections || _account.Length == 0
                ? new(ReceiptLinkState.NotChecked, null, "Маркетплейси не підключено. Звичайний друк доступний.", [])
                : matches.TryGetValue(row.Id, out var match) ? match : row.OrderMatch ??
                    new(ReceiptLinkState.Incomplete, null, "Цей діапазон ще не перевірено.", []);
        _establishedLinks.Capture(_automaticCache, _account, _rows.Select(row => row.Model).ToArray(),
            _rows.Where(row => row.OrderMatch is not null).ToDictionary(row => row.Id, row => row.OrderMatch!), EnabledConnectionIds);
        RefreshOrderRows(orders);
        var linked = _rows.Where(r => r.OrderMatch?.Order is not null).Select(r => r.OrderMatch!.Order!.Key).ToHashSet();
        var unmatchedKeys = orders.Count(o => o.FiscalReferences.Count > 0 && !linked.Contains(o.Key));
        var unavailable = orders.Count(o => o.FiscalDataStatus.Length > 0);
        var repeatedIds = orders.GroupBy(o => (o.Key.Marketplace, o.Key.OrderId))
            .Count(group => group.Select(o => o.Key.ConnectionId).Distinct().Skip(1).Any());
        FiscalSummary = !HasEnabledConnections ? "" : $"Завантажено замовлень: {orders.Count}. Точних зв’язків: {_rows.Count(r => r.OrderMatch?.State == ReceiptLinkState.Exact)}. " +
            (_showingCachedMatches ? $"Автоприв’язок із кешу: {_rows.Count(r => r.OrderMatch?.State == ReceiptLinkState.Suggested)}; очікують оновлення API. " :
                appliedCoverageComplete ? $"Ймовірних автозв’язків: {_rows.Count(r => r.OrderMatch?.State == ReceiptLinkState.Suggested)}. " :
                "Ймовірні зв’язки: перевірка замовлень ще неповна. ") +
            (unmatchedKeys > 0 ? $"Замовлень із непідтвердженими фіскальними ключами: {unmatchedKeys}. Перевірте контекст каси/продавця, період і права касира; це не означає, що чека немає. " : "") +
            (unavailable > 0 ? $"Фіскальні дані потребують перевірки: {unavailable}. " : "") +
            (repeatedIds > 0 ? "Є однакові API-ID замовлень у різних підключеннях. Перевірте, чи той самий магазин не додано двічі; такі записи не об’єднуються автоматично. " : "") +
            (orders.Any(o => o.Key.Marketplace == MarketplaceKind.Rozetka)
                ? "Rozetka: спочатку ID чека; за відсутності фіскального ключа — зіставлення за правилами Prom. " : "") +
            (orders.Any(o => o.Key.Marketplace == MarketplaceKind.Prom && o.FiscalReferences.Count == 0)
                ? "Prom: частина замовлень без фіскальних ключів. Взаємно унікальна сума в той самий день дає ймовірний, не фіскальний автозв’язок. " : "") + appliedScopeDescription;
        NotifyDetails(); MatchesChanged?.Invoke(this, EventArgs.Empty); RaiseCommands();
    }

    private IReadOnlyDictionary<string, ReceiptOrderMatch> CalculateMatches(
        IReadOnlyList<ReceiptRowViewModel> rows, AmountMatchScope matchScope, bool complete,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions, bool legacyCoverage,
        bool restoreBackgroundCache = false, IReadOnlyDictionary<string, ReceiptOrderMatch>? establishedExactLinks = null)
    {
        if (rows.Count == 0) return new Dictionary<string, ReceiptOrderMatch>();
        var receipts = rows.Select(row => row.Model).ToArray();
        var ids = rows.Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var details = _receiptDetails.Where(p => ids.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value);
        var loading = _loadingDetails.Where(ids.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allowRestore = _allowCachedRestore || restoreBackgroundCache &&
            _matcher.HasVerifiedSnapshot(_automaticCache, _account, matchScope, EnabledConnectionIds);
        if (allowRestore && !complete &&
            _matcher.TryRestore(_automaticCache, receipts, _account, orders, decisions, out var restored,
                details, matchScope, loading, EnabledConnectionIds, legacyCoverage))
        {
            _showingCachedMatches = true;
            return restored.ToDictionary(p => p.Key, p => p.Value with
            { Explanation = "Збережений результат попередньої перевірки; очікує оновлення API. " + p.Value.Explanation });
        }
        var calculated = _matcher.MatchAll(_automaticCache, receipts, _account, orders, decisions,
            complete, details, matchScope, loading, EnabledConnectionIds, establishedExactLinks).Matches;
        if (!complete && calculated.Values.Any(m => m.State == ReceiptLinkState.Suggested)) _showingCachedMatches = true;
        if (!allowRestore || complete) return calculated;
        var known = _matcher.RestoreKnownSuggestions(_automaticCache, receipts, _account, orders,
            decisions, details, matchScope, loading, EnabledConnectionIds);
        if (known.Count == 0) return calculated;
        _showingCachedMatches = true;
        var merged = calculated.ToDictionary(p => p.Key, p => p.Value);
        foreach (var (id, match) in known)
            merged[id] = match with { Explanation = "Збережений ймовірний зв’язок; очікує оновлення API. " + match.Explanation };
        return merged;
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
                    RememberDetails(row, details);
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
            await SaveAutomaticCacheAsync(generation, _scopeCancel.Token);
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
            await SaveAutomaticCacheAsync(generation, _scopeCancel.Token);
        }
        catch (Exception) { if (generation == _generation) Status = "Не вдалося зберегти відв’язування."; }
    }

    private string BuildDetails(ReceiptRowViewModel? row)
    {
        if (row is null) return "Оберіть рядок чека для перегляду замовлення.";
        var match = row.OrderMatch;
        if (match?.Order is not { } o) return $"Чек № {row.Serial}, {row.Type}, {row.Total:N2} грн\n{match?.Explanation ?? "Зв’язок ще не перевірено."}\n" +
            string.Join("\n", (match?.GroupOrders.Count > 0 ? match.GroupOrders : match?.Candidates ?? []).Select(candidate =>
                $"Замовлення №{candidate.Number}: {candidate.CreatedAt:dd.MM.yyyy HH:mm zzz}; покупець: {candidate.Buyer?.Name ?? "не надано"}; " +
                $"товари: {string.Join("; ", candidate.Items.Select(i => $"{i.Name} × {i.Quantity?.ToString() ?? "?"}"))}")) +
            "\nЧеки групи: " + string.Join("; ", _rows.Where(r => match?.CompetingReceiptIds.Contains(r.Id) == true)
                .Select(r => $"№{r.Serial}, {r.LocalDate:dd.MM.yyyy} {r.LocalTime}; " +
                    string.Join(", ", (_receiptDetails.GetValueOrDefault(r.Id)?.Items ?? []).Select(i => $"{i.Name} × {i.Quantity}")))) +
            "\nДля ручного вибору: оберіть замовлення та чек → «Прив’язати вибрані».";
        var total = o.Total is { } amount ? $"{amount:N2} {o.Currency}" : o.RawTotal.Length > 0 ? o.RawTotal : "не надана";
        var difference = o.Key.Marketplace == MarketplaceKind.Rozetka && match.State == ReceiptLinkState.Exact
            ? "; точний зв’язок за ID чека" :
            o.Total is { } sum && o.Currency == "UAH" ? $"; різниця: {sum - row.Total:N2} грн" : "; порівняння валют не підтверджене";
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
        foreach (var command in new[] { SyncCommand, ExpandCommand, CancelCommand, LinkCommand, LinkSelectedOrderCommand, ChooseReceiptForOrderCommand, AutoMatchCommand, UnlinkCommand })
        { if (command is RelayCommand relay) relay.RaiseCanExecuteChanged(); if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged(); }
    }
}

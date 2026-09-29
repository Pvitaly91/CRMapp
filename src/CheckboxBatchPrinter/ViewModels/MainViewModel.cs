using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly IReceiptService _receiptService;
    private readonly IReceiptImageService _imageService;
    private readonly ISettingsService _settingsService;
    private readonly IAuthenticationService _authentication;
    private readonly IPrintHistoryStore _printHistoryStore;
    private readonly IPrintService _printService;
    private readonly IUiDialogService _dialogs;
    private readonly IAppLogger _logger;
    private readonly IReceiptSnapshotStore? _receiptSnapshots;
    private readonly SemaphoreSlim _receiptRefreshGate = new(1, 1);
    private readonly object _receiptFlightGate = new();
    private Task? _receiptFlight;
    private (DateOnly From, DateOnly To) _receiptFlightRange;
    private CancellationTokenSource? _operationCancellation;
    private DateTime? _dateFrom = DateRangeBuilder.TodayKyiv;
    private DateTime? _dateTo = DateRangeBuilder.TodayKyiv;
    private int _selectedTabIndex;
    private string _statusText = "Готово";
    private string _progressText = string.Empty;
    private string _backgroundStatus = "Фонове оновлення: очікує";
    private bool _isBusy;
    private string? _loadedAccountContext;
    private DateTimeOffset? _cachedReceiptSuccessUtc;
    private DateOnly? _loadedFrom, _loadedTo;
    private bool _followToday = true;
    private CancellationTokenSource? _dateLoadCancellation;
    private readonly HashSet<(DateOnly From, DateOnly To)> _historicalLoads = [];
    private readonly List<(DateOnly From, DateOnly To)> _receiptCoverage = [];

    public MainViewModel(
        IReceiptService receiptService,
        IReceiptImageService imageService,
        ISettingsService settingsService,
        IAuthenticationService authentication,
        IPrintHistoryStore printHistoryStore,
        IPrintService printService,
        IUiDialogService dialogs,
        IAppLogger logger,
        MarketplaceWorkspaceViewModel? marketplace = null, IReceiptSnapshotStore? receiptSnapshots = null)
    {
        _receiptService = receiptService;
        _imageService = imageService;
        _settingsService = settingsService;
        _authentication = authentication;
        _printHistoryStore = printHistoryStore;
        _printService = printService;
        _dialogs = dialogs;
        _logger = logger;
        Marketplace = marketplace;
        _receiptSnapshots = receiptSnapshots;

        ReceiptTypes = new ObservableCollection<ReceiptTypeOption>(
            new[] { new ReceiptTypeOption(string.Empty, "Усі типи") }
                .Concat(Core.Models.ReceiptTypes.Known.Select(x => new ReceiptTypeOption(x, Core.Models.ReceiptTypes.ToUkrainian(x)))));
        AllReceiptsTab = new(Receipts, ReceiptTypes[0]);
        OrdersReceiptsTab = new(Receipts, ReceiptTypes[0], usesOrders: true, Marketplace);
        if (Marketplace is not null) Marketplace.MatchesChanged += (_, _) => OrdersReceiptsTab.View.Refresh();

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        TodayCommand = new AsyncRelayCommand(_ => ShowTodayAsync(), _ => !IsBusy);
        ClearDateCommand = new RelayCommand(parameter =>
        {
            if (string.Equals(parameter as string, "from", StringComparison.Ordinal)) DateFrom = null;
            if (string.Equals(parameter as string, "to", StringComparison.Ordinal)) DateTo = null;
        });
        SelectAllCommand = new RelayCommand(_ => SelectVisible(true), _ => Receipts.Count > 0 && !IsBusy);
        ClearSelectionCommand = new RelayCommand(_ => ClearSelection(), _ => SelectedCount > 0 && !IsBusy);
        PrintSelectedCommand = new AsyncRelayCommand(_ => PrintSelectedAsync(), _ => VisibleSelectedCount > 0 && !IsBusy);
        RetryFailedCommand = new AsyncRelayCommand(_ => RetryFailedAsync(), _ => FailedCount > 0 && !IsBusy);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, _ => !IsBusy);
        SettingsCommand = new AsyncRelayCommand(_ => OpenSettingsAsync(), _ => !IsBusy);
        MarketplaceSettingsCommand = new AsyncRelayCommand(_ => OpenSettingsAsync(marketplace: true), _ => !IsBusy);
        foreach (var tab in new[] { AllReceiptsTab, OrdersReceiptsTab })
        {
            tab.View.CollectionChanged += (_, _) => OnSelectionChanged(this, EventArgs.Empty);
            tab.PropertyChanged += (_, e) =>
            {
                if (ReferenceEquals(tab, ActiveTab))
                {
                    if (e.PropertyName == nameof(tab.SearchText)) OnPropertyChanged(nameof(SearchText));
                    if (e.PropertyName == nameof(tab.SelectedType)) OnPropertyChanged(nameof(SelectedType));
                }
                if (tab.UsesOrders && e.PropertyName == nameof(tab.SelectedReceipt) && Marketplace is not null)
                    Marketplace.SelectedReceipt = tab.SelectedReceipt;
            };
        }
        ApplyDisplayDates();
    }

    public ObservableCollection<ReceiptRowViewModel> Receipts { get; } = [];
    public ReceiptTabViewModel AllReceiptsTab { get; }
    public ReceiptTabViewModel OrdersReceiptsTab { get; }
    public ReceiptTabViewModel ActiveTab => SelectedTabIndex == 1 ? OrdersReceiptsTab : AllReceiptsTab;
    public ICollectionView ReceiptsView => ActiveTab.View;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            if (value is not (0 or 1) || !SetProperty(ref _selectedTabIndex, value)) return;
            OnPropertyChanged(nameof(ActiveTab));
            OnPropertyChanged(nameof(ReceiptsView));
            OnPropertyChanged(nameof(SearchText));
            OnPropertyChanged(nameof(SelectedType));
            OnSelectionChanged(this, EventArgs.Empty);
            Marketplace?.SetActive(value == 1);
            if (value == 1) _ = PrepareOrdersAsync();
        }
    }
    public Task PrepareOrdersAsync()
    {
        if (Marketplace is null) return Task.CompletedTask;
        UpdateOrderDatesWithoutReceipts();
        return SelectedTabIndex == 1 ? Marketplace.OpenAsync() : Marketplace.EnsureAttachedAsync();
    }
    public ObservableCollection<ReceiptTypeOption> ReceiptTypes { get; }
    public MarketplaceWorkspaceViewModel? Marketplace { get; }

    public ICommand RefreshCommand { get; }
    public ICommand TodayCommand { get; }
    public ICommand ClearDateCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand PrintSelectedCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand MarketplaceSettingsCommand { get; }

    public DateTime? DateFrom { get => _dateFrom; set { if (SetProperty(ref _dateFrom, value)) { _followToday = false; ApplyDisplayDates(); } } }
    public DateTime? DateTo { get => _dateTo; set { if (SetProperty(ref _dateTo, value)) { _followToday = false; ApplyDisplayDates(); } } }
    public void AdvanceTodayIfFollowing(DateTime today)
    {
        if (!_followToday || DateFrom?.Date == today.Date && DateTo?.Date == today.Date) return;
        DateFrom = today.Date; DateTo = today.Date;
        _followToday = true;
    }
    private void ApplyDisplayDates()
    {
        var from = DateFrom is { } first ? DateOnly.FromDateTime(first) : (DateOnly?)null;
        var to = DateTo is { } last ? DateOnly.FromDateTime(last) : (DateOnly?)null;
        // Date inputs immediately filter loaded rows in both tabs. Never replace the
        // source collection or matching scope: hidden competitors still count.
        AllReceiptsTab.SetDisplayDates(from, to);
        OrdersReceiptsTab.SetDisplayDates(from, to);
        Marketplace?.SetDisplayDates(from, to);
        UpdateOrderDatesWithoutReceipts();
        if (CoordinatorManaged && from is { } firstDate && to is { } lastDate && lastDate >= firstDate &&
            !_receiptCoverage.Any(r => r.From <= firstDate && r.To >= lastDate) &&
            !_historicalLoads.Contains((firstDate, lastDate)))
        {
            _dateLoadCancellation?.Cancel();
            _dateLoadCancellation?.Dispose();
            _dateLoadCancellation = new CancellationTokenSource();
            _ = LoadSelectedDatesAsync(firstDate, lastDate, _dateLoadCancellation.Token);
        }
    }

    private async Task LoadSelectedDatesAsync(DateOnly from, DateOnly to, CancellationToken token)
    {
        try
        {
            await Task.Delay(350, token);
            if (IsBusy) return;
            await RefreshAsync();
            if (!token.IsCancellationRequested) _historicalLoads.Add((from, to));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
    private void UpdateOrderDatesWithoutReceipts() => Marketplace?.SetOrderDatesWithoutReceipts(
        DateFrom is { } from ? DateOnly.FromDateTime(from) : null, DateTo is { } to ? DateOnly.FromDateTime(to) : null);
    public string SearchText
    {
        get => ActiveTab.SearchText;
        set => ActiveTab.SearchText = value;
    }
    public ReceiptTypeOption? SelectedType
    {
        get => ActiveTab.SelectedType;
        set => ActiveTab.SelectedType = value;
    }
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }
    public string BackgroundStatus { get => _backgroundStatus; set => SetProperty(ref _backgroundStatus, value); }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaiseCommands();
        }
    }
    public bool IsPrinting { get; private set; }
    public bool CoordinatorManaged { get; set; }
    public int SelectedCount => Receipts.Count(ActiveTab.IsMarked);
    public int VisibleSelectedCount => ReceiptsView.Cast<ReceiptRowViewModel>().Count(ActiveTab.IsMarked);
    public int HiddenSelectedCount => SelectedCount - VisibleSelectedCount;
    public int FailedCount => ReceiptsView.Cast<ReceiptRowViewModel>().Count(x => x.PrintStatus == PrintItemStatus.Error);
    public string SelectionText => $"Вибрано видимих: {VisibleSelectedCount}\nВибрано прихованих: {HiddenSelectedCount}";

    public async Task InitializeAsync()
    {
        await _imageService.CleanupAsync();
        if (!_authentication.HasStoredCredentials)
        {
            StatusText = "Налаштуйте підключення до Checkbox";
            await OpenSettingsAsync();
        }
        if (_authentication.HasStoredCredentials)
            await RefreshAsync();
    }

    public async Task InitializeCachedAsync(CancellationToken token = default)
    {
        if (_receiptSnapshots is null) return;
        var settings = await _settingsService.LoadAsync(token);
        if (string.IsNullOrWhiteSpace(settings.Login)) return;
        var account = PrintAccountContext.Create(settings);
        var cached = await _receiptSnapshots.LoadAsync(account, token);
        if (cached is null) return;
        var printed = await _printHistoryStore.LoadAsync(account, token);
        ApplyReceipts(cached.Receipts, printed, account, cached.From, cached.To, verifiedCoverage: false);
        _cachedReceiptSuccessUtc = cached.LastSuccessUtc;
        StatusText = $"Дані станом на {cached.LastSuccessUtc.ToLocalTime():dd.MM.yyyy HH:mm}; очікується оновлення мережі";
        if (Marketplace is not null) await Marketplace.EnsureAttachedAsync();
    }

    public Task BackgroundRefreshAsync(DateOnly from, DateOnly to, CancellationToken token = default,
        bool persistSnapshot = true)
    {
        lock (_receiptFlightGate)
        {
            if (_receiptFlight is { IsCompleted: false } && _receiptFlightRange == (from, to)) return _receiptFlight;
            var result = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _receiptFlightRange = (from, to);
            _receiptFlight = result.Task;
            _ = RunReceiptFlightAsync(from, to, token, persistSnapshot, result);
            return result.Task;
        }
    }

    private async Task RunReceiptFlightAsync(DateOnly from, DateOnly to, CancellationToken token,
        bool persistSnapshot, TaskCompletionSource result)
    {
        try { await BackgroundRefreshCoreAsync(from, to, token, persistSnapshot); result.TrySetResult(); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { result.TrySetCanceled(token); }
        catch (Exception ex) { result.TrySetException(ex); }
        finally
        {
            lock (_receiptFlightGate)
                if (ReferenceEquals(_receiptFlight, result.Task)) _receiptFlight = null;
        }
    }

    private async Task BackgroundRefreshCoreAsync(DateOnly from, DateOnly to, CancellationToken token,
        bool persistSnapshot)
    {
        if (!_authentication.HasStoredCredentials) return;
        await _receiptRefreshGate.WaitAsync(token);
        try
        {
            if (persistSnapshot && Marketplace is not null) Marketplace.ReceiptCoverageFresh = false;
            var settings = await _settingsService.LoadAsync(token);
            var account = PrintAccountContext.Create(settings);
            var items = await _receiptService.GetReceiptsAsync(from, to, token);
            token.ThrowIfCancellationRequested();
            // A late response from the previous cashier must never enter the new profile.
            if (PrintAccountContext.Create(await _settingsService.LoadAsync(token)) != account) return;
            var printed = await _printHistoryStore.LoadAsync(account, token);
            ApplyReceipts(items, printed, account, from, to, background: persistSnapshot);
            if (persistSnapshot && Marketplace is not null) Marketplace.ReceiptCoverageFresh = true;
            if (persistSnapshot && _receiptSnapshots is not null)
            {
                try
                {
                    await _receiptSnapshots.SaveAsync(new(account, from, to, DateTimeOffset.UtcNow, true,
                        items, DpapiReceiptSnapshotStore.FingerprintsFor(items)), token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.Error("receipts.cache.save", exception); }
            }
            StatusText = $"Завантажено чеків: {items.Count}";
            _cachedReceiptSuccessUtc = DateTimeOffset.UtcNow;
        }
        finally { _receiptRefreshGate.Release(); }
    }

    public void MarkBackgroundOffline()
    {
        if (_cachedReceiptSuccessUtc is { } last)
            StatusText = $"Дані станом на {last.ToLocalTime():dd.MM.yyyy HH:mm}; немає з’єднання";
    }
    public void CancelPendingOperations()
    {
        _dateLoadCancellation?.Cancel();
        _operationCancellation?.Cancel();
    }

    private void ApplyReceipts(IReadOnlyList<ReceiptRecord> items,
        IReadOnlyDictionary<string, PrintedReceiptRecord> printed, string account, DateOnly from, DateOnly to,
        bool background = false, bool verifiedCoverage = true)
    {
        if (_loadedAccountContext is not null && _loadedAccountContext != account)
        {
            foreach (var old in Receipts) old.SelectionChanged -= OnSelectionChanged;
            Receipts.Clear();
            _receiptCoverage.Clear();
        }
        var known = Receipts.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var model in items)
        {
            if (known.TryGetValue(model.Id, out var existing))
            {
                if (System.Text.Json.JsonSerializer.Serialize(existing.Model) != System.Text.Json.JsonSerializer.Serialize(model))
                    Marketplace?.InvalidateReceiptDetails(model.Id);
                existing.Update(model);
            }
            else
            {
                var row = new ReceiptRowViewModel(model);
                row.SelectionChanged += OnSelectionChanged;
                Receipts.Add(row);
                known[model.Id] = row;
                existing = row;
            }
            if (printed.TryGetValue(model.Id, out var history))
            {
                existing.PrintStatus = PrintItemStatus.Done;
                existing.PrintError = $"Надруковано {history.PrintedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss} на «{history.PrinterName}».";
            }
        }
        var fetchedIds = items.Select(x => x.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var stale in Receipts.Where(row => row.Model.DisplayDate is { } date &&
            DateRangeBuilder.KyivDate(date) >= from && DateRangeBuilder.KyivDate(date) <= to &&
            !fetchedIds.Contains(row.Id)).ToArray())
        {
            stale.SelectionChanged -= OnSelectionChanged;
            Receipts.Remove(stale);
        }
        _loadedAccountContext = account;
        if (verifiedCoverage && !_receiptCoverage.Contains((from, to)))
        {
            _receiptCoverage.Add((from, to));
            if (_receiptCoverage.Count > 256) _receiptCoverage.RemoveAt(0);
        }
        if (background && DateFrom is { } displayFrom && DateTo is { } displayTo &&
            DateOnly.FromDateTime(displayFrom) >= from && DateOnly.FromDateTime(displayTo) <= to)
            _dateLoadCancellation?.Cancel();
        if (!background) { _loadedFrom = from; _loadedTo = to; }
        AllReceiptsTab.View.Refresh(); OrdersReceiptsTab.View.Refresh();
        OnSelectionChanged(this, EventArgs.Empty);
        Marketplace?.UpdateReceiptScope(Receipts.ToArray(), account, from, to, background, verifiedCoverage);
    }

    public async Task RefreshAsync()
    {
        if (DateFrom is null || DateTo is null)
        {
            _dialogs.ShowError("Оберіть обидві дати.");
            return;
        }
        if (DateTo.Value.Date < DateFrom.Value.Date)
        {
            _dialogs.ShowError("Дата «до» не може бути раніше дати «від».");
            return;
        }

        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "Завантаження чеків…";
        ProgressText = string.Empty;
        var from = DateOnly.FromDateTime(DateFrom.Value);
        var to = DateOnly.FromDateTime(DateTo.Value);
        var refreshed = false;
        try
        {
            var settings = await _settingsService.LoadAsync(_operationCancellation.Token);
            var accountContext = PrintAccountContext.Create(settings);
            await BackgroundRefreshAsync(from, to, _operationCancellation.Token, persistSnapshot: false);
            refreshed = true;
        }
        catch (OperationCanceledException) { StatusText = "Операцію скасовано"; }
        catch (ApiException exception)
        {
            StatusText = "Помилка Checkbox API";
            _dialogs.ShowError(exception.ToUserMessage());
        }
        catch (Exception exception)
        {
            _logger.Error("receipts.refresh", exception);
            StatusText = "Не вдалося завантажити чеки";
            _dialogs.ShowError($"Не вдалося завантажити чеки. {exception.Message}");
        }
        finally { IsBusy = false; }
        if (refreshed && Marketplace is not null && _loadedAccountContext is not null)
        {
            Marketplace.SelectedReceipt = OrdersReceiptsTab.SelectedReceipt;
            if (SelectedTabIndex == 1) _ = PrepareOrdersAsync();
        }
    }

    private async Task ShowTodayAsync()
    {
        var today = DateRangeBuilder.TodayKyiv;
        DateFrom = today;
        DateTo = today;
        _followToday = true;
        SearchText = string.Empty;
        SelectedType = ReceiptTypes[0];
        await RefreshAsync();
    }

    private void SelectVisible(bool selected)
    {
        SelectionService.SetVisibleSelection(ReceiptsView.Cast<ReceiptRowViewModel>(), ActiveTab.SetMarked, selected);
        OnSelectionChanged(this, EventArgs.Empty);
    }

    private void ClearSelection()
    {
        foreach (var row in Receipts) ActiveTab.SetMarked(row, false);
    }

    private async Task PrintSelectedAsync()
    {
        // Capture the active tab before the first await; tab switching and order sync
        // never change the submitted rows, their order or confirmation metadata.
        var tab = ActiveTab;
        var selected = tab.View.Cast<ReceiptRowViewModel>().Where(tab.IsMarked).ToArray();
        if (selected.Length == 0) return;
        var hiddenCount = HiddenSelectedCount;
        var items = selected.Select((row, index) => new PrintBatchItem(index + 1, row.Id, row.Serial, row.Marketplace, row.OrderNumber)).ToArray();

        IsBusy = true;
        IsPrinting = true;
        var success = 0;
        var errors = 0;
        try
        {
            var sourceSettings = await _settingsService.LoadAsync();
            // A detached copy also freezes the printer and paper settings for this job.
            var settings = new AppSettings
            {
                ApiBaseUrl = sourceSettings.ApiBaseUrl, Login = sourceSettings.Login,
                PrinterName = sourceSettings.PrinterName, PaperWidth = sourceSettings.PaperWidth,
                CustomPaperWidthMm = sourceSettings.CustomPaperWidthMm, PrintableWidthMm = sourceSettings.PrintableWidthMm,
                SeparatePrintJobPerReceipt = sourceSettings.SeparatePrintJobPerReceipt,
                CacheRetentionDays = sourceSettings.CacheRetentionDays, HttpTimeoutSeconds = sourceSettings.HttpTimeoutSeconds
            };
            if (string.IsNullOrWhiteSpace(settings.PrinterName) || !_printService.PrinterExists(settings.PrinterName))
            {
                _dialogs.ShowError("Оберіть доступний принтер у Налаштуваннях → Друк.");
                return;
            }
            var accountContext = PrintAccountContext.Create(settings);
            if (!string.Equals(_loadedAccountContext, accountContext, StringComparison.Ordinal))
            {
                _dialogs.ShowError("Касира Checkbox змінено. Натисніть «Оновити» перед друком.");
                return;
            }
            var confirmation = new PrintBatchConfirmation(settings.PrinterName, hiddenCount, items);
            if (!_dialogs.ConfirmPrint(confirmation)) return;
            foreach (var row in selected) { row.PrintStatus = PrintItemStatus.Waiting; row.PrintError = string.Empty; }
            if (selected.Length == 1)
            {
                var current = 0;
                var result = await BatchProcessor.RunAsync(selected, async (row, cancellationToken) =>
                {
                    current++;
                    ProgressText = $"Друк {current} із {selected.Length}";
                    row.PrintStatus = PrintItemStatus.Downloading;
                    var png = await _imageService.GetPngAsync(row.Id, (int)Math.Round(settings.EffectivePaperWidthMm), cancellationToken);
                    row.PrintStatus = PrintItemStatus.Printing;
                    await _printService.PrintReceiptAsync(new PrintReceiptDocument(png, row.Id, items[0].OrderNumber), settings, cancellationToken);
                    row.PrintStatus = PrintItemStatus.Done;
                    tab.SetMarked(row, false);
                    _logger.Info("receipt.print", row.Id, printStatus: "done");
                }, (row, exception) =>
                {
                    row.PrintStatus = PrintItemStatus.Error;
                    row.PrintError = exception.Message;
                    _logger.Error("receipt.print", exception, row.Id, printStatus: "error");
                    return Task.CompletedTask;
                });
                success = result.SuccessCount;
                errors = result.ErrorCount;
            }
            else
            {
                var loaded = new List<(PrintReceiptDocument Document, ReceiptRowViewModel Row)>();
                for (var i = 0; i < selected.Length; i++)
                {
                    var row = selected[i];
                    ProgressText = $"Завантаження {i + 1} із {selected.Length}";
                    try
                    {
                        row.PrintStatus = PrintItemStatus.Downloading;
                        var png = await _imageService.GetPngAsync(row.Id, (int)Math.Round(settings.EffectivePaperWidthMm));
                        loaded.Add((new PrintReceiptDocument(png, row.Id, items[i].OrderNumber), row));
                    }
                    catch (Exception exception)
                    {
                        row.PrintStatus = PrintItemStatus.Error; row.PrintError = exception.Message; errors++;
                    }
                }
                if (errors > 0)
                {
                    foreach (var item in loaded) item.Row.PrintStatus = PrintItemStatus.Waiting;
                    throw new InvalidOperationException("Не всі PNG завантажено. Підтверджений пакет не передано принтеру; частковий пакет не друкується.");
                }
                if (loaded.Count > 0)
                {
                    foreach (var item in loaded) item.Row.PrintStatus = PrintItemStatus.Printing;
                    await _printService.PrintReceiptsAsSingleJobAsync(loaded.Select(x => x.Document).ToArray(), settings);
                    foreach (var item in loaded)
                    {
                        item.Row.PrintStatus = PrintItemStatus.Done;
                        tab.SetMarked(item.Row, false);
                    }
                    success = loaded.Count;
                }
            }
            var printedRows = selected.Where(row => row.PrintStatus == PrintItemStatus.Done).ToArray();
            string? historyWarning = null;
            if (printedRows.Length > 0)
            {
                try
                {
                    await _printHistoryStore.MarkPrintedAsync(
                        accountContext,
                        printedRows.Select(row => row.Id).ToArray(),
                        settings.PrinterName);
                }
                catch (Exception exception)
                {
                    historyWarning = "Не вдалося зберегти локальну історію друку.";
                    foreach (var row in printedRows) row.PrintError = historyWarning;
                    _logger.Error("print.history.save", exception);
                }
            }

            StatusText = historyWarning is null
                ? $"Друк завершено. Успішно: {success}. Помилки: {errors}."
                : $"Друк завершено, але історію не збережено. Успішно: {success}.";
            ProgressText = string.Empty;
            OnPropertyChanged(nameof(FailedCount));
            var summary = $"Успішно: {success}\nПомилки: {errors}";
            if (historyWarning is not null) summary += $"\n\n{historyWarning}";
            _dialogs.ShowInfo(summary, "Пакетний друк завершено");
        }
        catch (Exception exception)
        {
            foreach (var row in selected.Where(x => x.PrintStatus == PrintItemStatus.Printing))
            {
                row.PrintStatus = PrintItemStatus.Error; row.PrintError = exception.Message;
            }
            _logger.Error("batch.print", exception);
            _dialogs.ShowError($"Не вдалося завершити друк. {exception.Message}");
        }
        finally { ProgressText = string.Empty; IsPrinting = false; IsBusy = false; RaiseCommands(); }
    }

    private async Task RetryFailedAsync()
    {
        var failed = ReceiptsView.Cast<ReceiptRowViewModel>().Where(x => x.PrintStatus == PrintItemStatus.Error).ToArray();
        foreach (var row in Receipts) ActiveTab.SetMarked(row, failed.Contains(row));
        await PrintSelectedAsync();
    }

    private async Task PreviewAsync(object? parameter)
    {
        var tab = ActiveTab;
        var row = parameter as ReceiptRowViewModel ?? tab.SelectedReceipt ?? tab.View.Cast<ReceiptRowViewModel>().FirstOrDefault(tab.IsMarked);
        if (row is null) { _dialogs.ShowInfo("Оберіть чек для перегляду."); return; }
        IsBusy = true;
        StatusText = "Завантаження перегляду…";
        try
        {
            var settings = await _settingsService.LoadAsync();
            var png = await _imageService.GetPngAsync(row.Id, (int)Math.Round(settings.EffectivePaperWidthMm));
            _dialogs.ShowPreview(png, row);
            StatusText = "Готово";
        }
        catch (Exception exception)
        {
            _logger.Error("receipt.preview", exception, row.Id);
            _dialogs.ShowError($"Не вдалося відкрити чек. {exception.Message}");
        }
        finally { IsBusy = false; }
    }

    private async Task OpenSettingsAsync(bool marketplace = false)
    {
        var changed = await _dialogs.OpenSettingsAsync(marketplace);
        if (changed && _authentication.HasStoredCredentials) StatusText = "Налаштування збережено";
        // Testing Checkbox credentials persists them even if the dialog is then cancelled.
        if (Marketplace is not null)
        {
            var settings = await _settingsService.LoadAsync();
            if (_loadedAccountContext != PrintAccountContext.Create(settings))
            {
                Marketplace.InvalidateAccount();
                if (CoordinatorManaged)
                {
                    foreach (var old in Receipts) old.SelectionChanged -= OnSelectionChanged;
                    Receipts.Clear();
                    _loadedAccountContext = null;
                    _receiptCoverage.Clear();
                    _cachedReceiptSuccessUtc = null;
                    await InitializeCachedAsync();
                }
            }
            else if (_loadedAccountContext is not null && _loadedFrom is { } from && _loadedTo is { } to)
            {
                Marketplace.UpdateReceiptScope(Receipts.ToArray(), _loadedAccountContext, from, to,
                    verifiedCoverage: _receiptCoverage.Any(r => r.From <= from && r.To >= to));
                Marketplace.SelectedReceipt = OrdersReceiptsTab.SelectedReceipt;
                if (SelectedTabIndex == 1) await PrepareOrdersAsync();
            }
        }
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(VisibleSelectedCount));
        OnPropertyChanged(nameof(HiddenSelectedCount));
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(SelectionText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        foreach (var command in new ICommand[] { RefreshCommand, TodayCommand, SelectAllCommand, ClearSelectionCommand, PrintSelectedCommand, RetryFailedCommand, PreviewCommand, SettingsCommand, MarketplaceSettingsCommand })
        {
            if (command is RelayCommand relay) relay.RaiseCanExecuteChanged();
            if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged();
        }
    }
}

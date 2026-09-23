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
    private CancellationTokenSource? _operationCancellation;
    private DateTime? _dateFrom = DateRangeBuilder.TodayKyiv;
    private DateTime? _dateTo = DateRangeBuilder.TodayKyiv;
    private int _selectedTabIndex;
    private string _statusText = "Готово";
    private string _progressText = string.Empty;
    private bool _isBusy;
    private string? _loadedAccountContext;
    private DateOnly? _loadedFrom, _loadedTo;

    public MainViewModel(
        IReceiptService receiptService,
        IReceiptImageService imageService,
        ISettingsService settingsService,
        IAuthenticationService authentication,
        IPrintHistoryStore printHistoryStore,
        IPrintService printService,
        IUiDialogService dialogs,
        IAppLogger logger,
        MarketplaceWorkspaceViewModel? marketplace = null)
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

        ReceiptTypes = new ObservableCollection<ReceiptTypeOption>(
            new[] { new ReceiptTypeOption(string.Empty, "Усі типи") }
                .Concat(Core.Models.ReceiptTypes.Known.Select(x => new ReceiptTypeOption(x, Core.Models.ReceiptTypes.ToUkrainian(x)))));
        AllReceiptsTab = new(Receipts, ReceiptTypes[0]);
        OrdersReceiptsTab = new(Receipts, ReceiptTypes[0], usesOrders: true, Marketplace);
        if (Marketplace is not null) Marketplace.MatchesChanged += (_, _) => OrdersReceiptsTab.View.Refresh();

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        TodayCommand = new RelayCommand(_ => { DateFrom = DateRangeBuilder.TodayKyiv; DateTo = DateRangeBuilder.TodayKyiv; });
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
            if (value == 1) _ = PrepareOrdersAsync();
        }
    }
    public Task PrepareOrdersAsync()
    {
        if (Marketplace is null) return Task.CompletedTask;
        UpdateOrderDatesWithoutReceipts();
        return Marketplace.EnsureAttachedAsync();
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

    public DateTime? DateFrom { get => _dateFrom; set { if (SetProperty(ref _dateFrom, value)) UpdateOrderDatesWithoutReceipts(); } }
    public DateTime? DateTo { get => _dateTo; set { if (SetProperty(ref _dateTo, value)) UpdateOrderDatesWithoutReceipts(); } }
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
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            RaiseCommands();
        }
    }
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
            var selectedIds = _loadedAccountContext == accountContext
                ? Receipts.Where(r => r.IsSelected).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            var orderSelectedIds = _loadedAccountContext == accountContext
                ? Receipts.Where(r => r.IsSelectedForOrders).Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) : [];
            var allCurrentId = _loadedAccountContext == accountContext ? AllReceiptsTab.SelectedReceipt?.Id : null;
            var ordersCurrentId = _loadedAccountContext == accountContext ? OrdersReceiptsTab.SelectedReceipt?.Id : null;
            var printedReceipts = await _printHistoryStore.LoadAsync(accountContext, _operationCancellation.Token);
            var items = await _receiptService.GetReceiptsAsync(
                from, to, _operationCancellation.Token);
            foreach (var old in Receipts) old.SelectionChanged -= OnSelectionChanged;
            Receipts.Clear();
            foreach (var model in items)
            {
                var row = new ReceiptRowViewModel(model)
                { IsSelected = selectedIds.Contains(model.Id), IsSelectedForOrders = orderSelectedIds.Contains(model.Id) };
                if (printedReceipts.TryGetValue(model.Id, out var history))
                {
                    row.PrintStatus = PrintItemStatus.Done;
                    row.PrintError = $"Надруковано {history.PrintedAtUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss} на «{history.PrinterName}».";
                }
                row.SelectionChanged += OnSelectionChanged;
                Receipts.Add(row);
            }
            _loadedAccountContext = accountContext;
            _loadedFrom = from; _loadedTo = to;
            AllReceiptsTab.SelectedReceipt = Receipts.FirstOrDefault(r => r.Id == allCurrentId);
            OrdersReceiptsTab.SelectedReceipt = Receipts.FirstOrDefault(r => r.Id == ordersCurrentId);
            refreshed = true;
            StatusText = items.Count == 0 ? "Чеків за обраний період не знайдено" : $"Завантажено чеків: {items.Count}";
            OnSelectionChanged(this, EventArgs.Empty);
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
            Marketplace.SetReceiptScope(Receipts.ToArray(), _loadedAccountContext, from, to);
            Marketplace.SelectedReceipt = OrdersReceiptsTab.SelectedReceipt;
            if (SelectedTabIndex == 1) _ = PrepareOrdersAsync();
        }
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
                    await _printService.PrintReceiptAsync(png, row.Id, settings, cancellationToken);
                    row.PrintStatus = PrintItemStatus.Done;
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
                var loaded = new List<(byte[] Png, string ReceiptId, ReceiptRowViewModel Row)>();
                for (var i = 0; i < selected.Length; i++)
                {
                    var row = selected[i];
                    ProgressText = $"Завантаження {i + 1} із {selected.Length}";
                    try
                    {
                        row.PrintStatus = PrintItemStatus.Downloading;
                        var png = await _imageService.GetPngAsync(row.Id, (int)Math.Round(settings.EffectivePaperWidthMm));
                        loaded.Add((png, row.Id, row));
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
                    await _printService.PrintReceiptsAsSingleJobAsync(loaded.Select(x => (x.Png, x.ReceiptId)).ToArray(), settings);
                    foreach (var item in loaded) item.Row.PrintStatus = PrintItemStatus.Done;
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
        finally { ProgressText = string.Empty; IsBusy = false; RaiseCommands(); }
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
            if (_loadedAccountContext != PrintAccountContext.Create(settings)) Marketplace.InvalidateAccount();
            else if (_loadedAccountContext is not null && _loadedFrom is { } from && _loadedTo is { } to)
            {
                Marketplace.SetReceiptScope(Receipts.ToArray(), _loadedAccountContext, from, to);
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
        foreach (var command in new ICommand[] { RefreshCommand, SelectAllCommand, ClearSelectionCommand, PrintSelectedCommand, RetryFailedCommand, PreviewCommand, SettingsCommand, MarketplaceSettingsCommand })
        {
            if (command is RelayCommand relay) relay.RaiseCanExecuteChanged();
            if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged();
        }
    }
}

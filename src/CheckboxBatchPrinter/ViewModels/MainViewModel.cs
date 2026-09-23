using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
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
    private readonly IPrintService _printService;
    private readonly IUiDialogService _dialogs;
    private readonly IAppLogger _logger;
    private CancellationTokenSource? _operationCancellation;
    private DateTime? _dateFrom = DateTime.Today;
    private DateTime? _dateTo = DateTime.Today;
    private string _searchText = string.Empty;
    private ReceiptTypeOption? _selectedType;
    private string _statusText = "Готово";
    private string _progressText = string.Empty;
    private bool _isBusy;

    public MainViewModel(
        IReceiptService receiptService,
        IReceiptImageService imageService,
        ISettingsService settingsService,
        IAuthenticationService authentication,
        IPrintService printService,
        IUiDialogService dialogs,
        IAppLogger logger)
    {
        _receiptService = receiptService;
        _imageService = imageService;
        _settingsService = settingsService;
        _authentication = authentication;
        _printService = printService;
        _dialogs = dialogs;
        _logger = logger;

        ReceiptsView = CollectionViewSource.GetDefaultView(Receipts);
        ReceiptsView.Filter = MatchesFilter;
        ReceiptsView.SortDescriptions.Add(new SortDescription(nameof(ReceiptRowViewModel.LocalDate), ListSortDirection.Descending));
        ReceiptsView.SortDescriptions.Add(new SortDescription(nameof(ReceiptRowViewModel.LocalTime), ListSortDirection.Descending));

        ReceiptTypes = new ObservableCollection<ReceiptTypeOption>(
            new[] { new ReceiptTypeOption(string.Empty, "Усі типи") }
                .Concat(Core.Models.ReceiptTypes.Known.Select(x => new ReceiptTypeOption(x, Core.Models.ReceiptTypes.ToUkrainian(x)))));
        _selectedType = ReceiptTypes[0];

        RefreshCommand = new AsyncRelayCommand(_ => RefreshAsync(), _ => !IsBusy);
        TodayCommand = new RelayCommand(_ => { DateFrom = DateTime.Today; DateTo = DateTime.Today; });
        SelectAllCommand = new RelayCommand(_ => SelectVisible(true), _ => Receipts.Count > 0 && !IsBusy);
        ClearSelectionCommand = new RelayCommand(_ => SelectVisible(false), _ => Receipts.Count > 0 && !IsBusy);
        PrintSelectedCommand = new AsyncRelayCommand(_ => PrintSelectedAsync(), _ => SelectedCount > 0 && !IsBusy);
        RetryFailedCommand = new AsyncRelayCommand(_ => RetryFailedAsync(), _ => FailedCount > 0 && !IsBusy);
        PreviewCommand = new AsyncRelayCommand(PreviewAsync, _ => !IsBusy);
        SettingsCommand = new AsyncRelayCommand(_ => OpenSettingsAsync(), _ => !IsBusy);
    }

    public ObservableCollection<ReceiptRowViewModel> Receipts { get; } = [];
    public ICollectionView ReceiptsView { get; }
    public ObservableCollection<ReceiptTypeOption> ReceiptTypes { get; }

    public ICommand RefreshCommand { get; }
    public ICommand TodayCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand PrintSelectedCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand SettingsCommand { get; }

    public DateTime? DateFrom { get => _dateFrom; set => SetProperty(ref _dateFrom, value); }
    public DateTime? DateTo { get => _dateTo; set => SetProperty(ref _dateTo, value); }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) ReceiptsView.Refresh(); }
    }
    public ReceiptTypeOption? SelectedType
    {
        get => _selectedType;
        set { if (SetProperty(ref _selectedType, value)) ReceiptsView.Refresh(); }
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
    public int SelectedCount => Receipts.Count(x => x.IsSelected);
    public int FailedCount => Receipts.Count(x => x.PrintStatus == PrintItemStatus.Error);
    public string SelectionText => $"Вибрано: {SelectedCount}";

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

    private async Task RefreshAsync()
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
        try
        {
            var items = await _receiptService.GetReceiptsAsync(
                DateOnly.FromDateTime(DateFrom.Value), DateOnly.FromDateTime(DateTo.Value), _operationCancellation.Token);
            foreach (var old in Receipts) old.SelectionChanged -= OnSelectionChanged;
            Receipts.Clear();
            foreach (var model in items)
            {
                var row = new ReceiptRowViewModel(model);
                row.SelectionChanged += OnSelectionChanged;
                Receipts.Add(row);
            }
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
    }

    private bool MatchesFilter(object item)
    {
        if (item is not ReceiptRowViewModel row) return false;
        if (!string.IsNullOrEmpty(SelectedType?.Value) && !string.Equals(row.RawType, SelectedType.Value, StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return row.FiscalCode.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Serial.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Payment.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.CashRegister.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
               row.Type.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }

    private void SelectVisible(bool selected)
    {
        SelectionService.SetVisibleSelection(ReceiptsView.Cast<ReceiptRowViewModel>(), (row, value) => row.IsSelected = value, selected);
        OnSelectionChanged(this, EventArgs.Empty);
    }

    private async Task PrintSelectedAsync()
    {
        var selected = Receipts.Where(x => x.IsSelected).ToArray();
        if (selected.Length == 0) return;
        var settings = await _settingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.PrinterName) || !_printService.PrinterExists(settings.PrinterName))
        {
            _dialogs.ShowError("Оберіть доступний принтер у Налаштуваннях → Друк.");
            return;
        }
        if (!_dialogs.ConfirmPrint(selected.Length, settings.PrinterName)) return;

        IsBusy = true;
        foreach (var row in selected) { row.PrintStatus = PrintItemStatus.Waiting; row.PrintError = string.Empty; }
        var success = 0;
        var errors = 0;
        try
        {
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
                if (loaded.Count > 0)
                {
                    foreach (var item in loaded) item.Row.PrintStatus = PrintItemStatus.Printing;
                    await _printService.PrintReceiptsAsSingleJobAsync(loaded.Select(x => (x.Png, x.ReceiptId)).ToArray(), settings);
                    foreach (var item in loaded) item.Row.PrintStatus = PrintItemStatus.Done;
                    success = loaded.Count;
                }
            }
            StatusText = $"Друк завершено. Успішно: {success}. Помилки: {errors}.";
            ProgressText = string.Empty;
            OnPropertyChanged(nameof(FailedCount));
            _dialogs.ShowInfo($"Успішно: {success}\nПомилки: {errors}", "Пакетний друк завершено");
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
        finally { IsBusy = false; RaiseCommands(); }
    }

    private async Task RetryFailedAsync()
    {
        var failed = Receipts.Where(x => x.PrintStatus == PrintItemStatus.Error).ToArray();
        foreach (var row in Receipts) row.IsSelected = failed.Contains(row);
        await PrintSelectedAsync();
    }

    private async Task PreviewAsync(object? parameter)
    {
        var row = parameter as ReceiptRowViewModel ?? Receipts.FirstOrDefault(x => x.IsSelected);
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

    private async Task OpenSettingsAsync()
    {
        var changed = await _dialogs.OpenSettingsAsync();
        if (changed && _authentication.HasStoredCredentials) StatusText = "Налаштування збережено";
    }

    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        foreach (var command in new ICommand[] { RefreshCommand, SelectAllCommand, ClearSelectionCommand, PrintSelectedCommand, RetryFailedCommand, PreviewCommand, SettingsCommand })
        {
            if (command is RelayCommand relay) relay.RaiseCanExecuteChanged();
            if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged();
        }
    }
}

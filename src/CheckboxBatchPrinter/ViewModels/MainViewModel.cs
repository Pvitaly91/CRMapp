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
    private CancellationTokenSource? _refreshCancellation;
    private CancellationTokenSource? _batchCancellation;
    private DateTime? _dateFrom = DateTime.Today;
    private DateTime? _dateTo = DateTime.Today;
    private string _searchText = string.Empty;
    private ReceiptTypeOption? _selectedType;
    private string _statusText = "Готово";
    private string _progressText = string.Empty;
    private bool _isBusy;
    private bool _isPrinting;

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
        TodayCommand = new RelayCommand(_ => { DateFrom = DateTime.Today; DateTo = DateTime.Today; }, _ => !IsBusy);
        SelectAllCommand = new RelayCommand(_ => SelectVisible(), _ => Receipts.Count > 0 && !IsBusy);
        ClearSelectionCommand = new RelayCommand(_ => ClearAllSelection(), _ => Receipts.Count > 0 && !IsBusy);
        PrintSelectedCommand = new AsyncRelayCommand(_ => PrintSelectedAsync(), _ => VisibleSelectedCount > 0 && !IsBusy);
        RetryFailedCommand = new AsyncRelayCommand(_ => RetrySafeFailuresAsync(), _ => SafeFailedCount > 0 && !IsBusy);
        StopBatchCommand = new RelayCommand(_ => StopBatch(), _ => IsPrinting && _batchCancellation?.IsCancellationRequested == false);
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
    public ICommand StopBatchCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand SettingsCommand { get; }

    public DateTime? DateFrom { get => _dateFrom; set => SetProperty(ref _dateFrom, value); }
    public DateTime? DateTo { get => _dateTo; set => SetProperty(ref _dateTo, value); }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetProperty(ref _searchText, value)) return;
            ReceiptsView.Refresh();
            NotifySelectionMetrics();
        }
    }
    public ReceiptTypeOption? SelectedType
    {
        get => _selectedType;
        set
        {
            if (!SetProperty(ref _selectedType, value)) return;
            ReceiptsView.Refresh();
            NotifySelectionMetrics();
        }
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
    public bool IsPrinting
    {
        get => _isPrinting;
        private set
        {
            if (!SetProperty(ref _isPrinting, value)) return;
            RaiseCommands();
        }
    }
    public int SelectedCount => Receipts.Count(x => x.IsSelected);
    public int VisibleSelectedCount => ReceiptsView.Cast<ReceiptRowViewModel>().Count(x => x.IsSelected);
    public int HiddenSelectedCount => Math.Max(0, SelectedCount - VisibleSelectedCount);
    public int FailedCount => Receipts.Count(x => x.PrintStatus == PrintItemStatus.Error);
    public int SafeFailedCount => Receipts.Count(x => x.CanRetryWithoutWarning);
    public string SelectionText => HiddenSelectedCount == 0
        ? $"Вибрано видимих: {VisibleSelectedCount}"
        : $"Вибрано: {SelectedCount} (видимих {VisibleSelectedCount}, прихованих {HiddenSelectedCount}; друк — лише видимі)";

    public async Task InitializeAsync()
    {
        await _imageService.CleanupAsync();
        if (!_authentication.HasStoredCredentials)
        {
            StatusText = "Налаштуйте підключення до Checkbox";
            await OpenSettingsAsync();
        }
        if (_authentication.HasStoredCredentials) await RefreshAsync();
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

        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        IsBusy = true;
        StatusText = "Завантаження чеків…";
        ProgressText = string.Empty;
        try
        {
            var items = await _receiptService.GetReceiptsAsync(
                DateOnly.FromDateTime(DateFrom.Value), DateOnly.FromDateTime(DateTo.Value), _refreshCancellation.Token);
            foreach (var old in Receipts) old.SelectionChanged -= OnSelectionChanged;
            Receipts.Clear();
            foreach (var model in items)
            {
                var row = new ReceiptRowViewModel(model);
                row.SelectionChanged += OnSelectionChanged;
                Receipts.Add(row);
            }
            StatusText = items.Count == 0 ? "Чеків за обраний період не знайдено" : $"Завантажено чеків: {items.Count}";
            NotifySelectionMetrics();
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

    private void SelectVisible()
    {
        SelectionService.SetVisibleSelection(ReceiptsView.Cast<ReceiptRowViewModel>(), (row, value) => row.IsSelected = value, true);
        NotifySelectionMetrics();
    }

    private void ClearAllSelection()
    {
        foreach (var row in Receipts) row.IsSelected = false;
        NotifySelectionMetrics();
    }

    private async Task PrintSelectedAsync()
    {
        var snapshot = PrintSelectionPlanner.CreateSnapshot(
            Receipts, ReceiptsView.Cast<ReceiptRowViewModel>(), row => row.IsSelected);
        if (snapshot.Items.Count == 0) return;

        var settings = await _settingsService.LoadAsync();
        if (string.IsNullOrWhiteSpace(settings.PrinterName) || !_printService.PrinterExists(settings.PrinterName))
        {
            _dialogs.ShowError("Оберіть доступний принтер у Налаштуваннях → Друк.");
            return;
        }

        var confirmation = new PrintBatchConfirmation(
            snapshot.Items.Count,
            snapshot.HiddenSelectedCount,
            settings.PrinterName,
            snapshot.Items.Select(x => $"чек №{x.Serial}, {x.LocalDate:dd.MM.yyyy} {x.LocalTime}").ToArray(),
            snapshot.Items.Any(x => PrintRetryPolicy.RequiresExplicitConfirmation(x.PrintStatus, x.HasBeenSubmitted)));
        if (!_dialogs.ConfirmPrint(confirmation)) return;

        // Immutable ordered snapshot: later filter/sort changes cannot alter this batch.
        var orderedBatch = snapshot.Items.ToArray();
        _batchCancellation?.Dispose();
        _batchCancellation = new CancellationTokenSource();
        IsBusy = true;
        IsPrinting = true;
        // Keep an earlier job ID until a replacement job is actually created. If
        // this attempt fails before submission, that earlier uncertain result must
        // still block a warning-free retry.
        foreach (var row in orderedBatch)
        {
            row.PrintStatus = PrintItemStatus.Waiting;
            row.PrintError = string.Empty;
        }

        try
        {
            var submitted = settings.SeparatePrintJobPerReceipt
                ? await SubmitAsSeparateJobsAsync(orderedBatch, settings, _batchCancellation.Token)
                : await SubmitAsSingleJobAsync(orderedBatch, settings, _batchCancellation.Token);

            if (submitted.Count > 0)
                await ObserveSubmittedJobsAsync(submitted);

            var submittedCount = submitted.Count;
            var errorCount = orderedBatch.Count(x => x.PrintStatus == PrintItemStatus.Error);
            var cancelledCount = orderedBatch.Count(x => x.PrintStatus == PrintItemStatus.Cancelled);
            // Every submitted job remains physically unconfirmed, including jobs
            // for which Windows reports an error.
            var unconfirmedCount = submitted.Count;
            StatusText = $"Передано в чергу Windows: {submittedCount}. Помилки: {errorCount}. Зупинено: {cancelledCount}. " +
                         $"Фізично підтверджено: 0; непідтверджено: {unconfirmedCount}.";
            ProgressText = string.Empty;
            NotifyPrintMetrics();
            _dialogs.ShowInfo(
                $"Передано в чергу Windows: {submittedCount}\nПомилки: {errorCount}\nЗупинено до передавання: {cancelledCount}\n\n" +
                "Windows-черга не підтверджує фізичний вихід чека. Перевірте папір, текст, QR-код і автообрізання на принтері.",
                "Пакет оброблено");
        }
        catch (Exception exception)
        {
            _logger.Error("batch.print", exception);
            _dialogs.ShowError($"Не вдалося завершити пакет. {exception.Message}");
        }
        finally
        {
            IsPrinting = false;
            IsBusy = false;
            RaiseCommands();
        }
    }

    private async Task<IReadOnlyList<(ReceiptRowViewModel Row, PrintSubmissionResult Submission)>> SubmitAsSeparateJobsAsync(
        IReadOnlyList<ReceiptRowViewModel> orderedBatch,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var current = 0;
        var result = await ReliableBatchRunner.RunAsync(orderedBatch, async (row, token) =>
        {
            current++;
            ProgressText = $"Підготовка {current} із {orderedBatch.Count}";
            row.PrintStatus = PrintItemStatus.Downloading;
            var png = await _imageService.GetPngAsync(row.Id, (int)Math.Round(settings.EffectivePaperWidthMm), token);
            row.PrintStatus = PrintItemStatus.Preparing;
            var submission = await _printService.PrintReceiptAsync(png, row.Id, settings, token);
            row.WindowsJobId = submission.JobId;
            row.PrintStatus = PrintItemStatus.SubmittedToWindowsQueue;
            row.PrintError = submission.InitialObservation.Details;
            _logger.Info("receipt.print.submitted", row.Id, printStatus: $"job_id={submission.JobId}");
            return submission;
        }, cancellationToken);

        foreach (var outcome in result.Items)
        {
            if (outcome.Cancelled)
            {
                outcome.Item.PrintStatus = PrintItemStatus.Cancelled;
                outcome.Item.PrintError = "Пакет зупинено до передавання цього чека у Windows.";
            }
            else if (outcome.Error is { } exception)
            {
                outcome.Item.PrintStatus = PrintItemStatus.Error;
                outcome.Item.PrintError = exception.Message;
                _logger.Error("receipt.print.before_submission", exception, outcome.Item.Id, printStatus: "not_submitted");
            }
        }

        return result.Items.Where(x => x.Submission is not null)
            .Select(x => (x.Item, x.Submission!)).ToArray();
    }

    private async Task<IReadOnlyList<(ReceiptRowViewModel Row, PrintSubmissionResult Submission)>> SubmitAsSingleJobAsync(
        IReadOnlyList<ReceiptRowViewModel> orderedBatch,
        AppSettings settings,
        CancellationToken cancellationToken)
    {
        var loaded = new List<(byte[] Png, string ReceiptId, ReceiptRowViewModel Row)>();
        for (var index = 0; index < orderedBatch.Count; index++)
        {
            var row = orderedBatch[index];
            if (cancellationToken.IsCancellationRequested)
            {
                foreach (var pending in orderedBatch.Skip(index))
                {
                    pending.PrintStatus = PrintItemStatus.Cancelled;
                    pending.PrintError = "Пакет зупинено до передавання у Windows.";
                }
                foreach (var prepared in loaded)
                {
                    prepared.Row.PrintStatus = PrintItemStatus.Cancelled;
                    prepared.Row.PrintError = "Спільне завдання не передано через зупинку пакета.";
                }
                return [];
            }

            ProgressText = $"Завантаження {index + 1} із {orderedBatch.Count}";
            try
            {
                row.PrintStatus = PrintItemStatus.Downloading;
                var png = await _imageService.GetPngAsync(row.Id, (int)Math.Round(settings.EffectivePaperWidthMm), cancellationToken);
                row.PrintStatus = PrintItemStatus.Preparing;
                loaded.Add((png, row.Id, row));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                index--;
                continue;
            }
            catch (Exception exception)
            {
                row.PrintStatus = PrintItemStatus.Error;
                row.PrintError = exception.Message;
                _logger.Error("receipt.print.before_submission", exception, row.Id, printStatus: "not_submitted");
            }
        }

        if (loaded.Count == 0) return [];
        try
        {
            var submission = await _printService.PrintReceiptsAsSingleJobAsync(
                loaded.Select(x => (x.Png, x.ReceiptId)).ToArray(), settings, cancellationToken);
            foreach (var item in loaded)
            {
                item.Row.WindowsJobId = submission.JobId;
                item.Row.PrintStatus = PrintItemStatus.SubmittedToWindowsQueue;
                item.Row.PrintError = submission.InitialObservation.Details;
            }
            return loaded.Select(x => (x.Row, submission)).ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            foreach (var item in loaded)
            {
                item.Row.PrintStatus = PrintItemStatus.Cancelled;
                item.Row.PrintError = "Спільне завдання не передано через зупинку пакета.";
            }
            return [];
        }
        catch (Exception exception)
        {
            foreach (var item in loaded)
            {
                item.Row.PrintStatus = PrintItemStatus.Error;
                item.Row.PrintError = exception.Message;
            }
            _logger.Error("batch.single_job.before_submission", exception, printStatus: "not_submitted");
            return [];
        }
    }

    private async Task ObserveSubmittedJobsAsync(
        IReadOnlyList<(ReceiptRowViewModel Row, PrintSubmissionResult Submission)> submitted)
    {
        var groups = submitted.GroupBy(x => x.Submission.JobId)
            .ToDictionary(x => x.Key, x => x.ToArray());
        var pending = groups.Keys.ToHashSet();
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (pending.Count > 0 && DateTime.UtcNow < deadline)
        {
            ProgressText = $"Контроль черги Windows: {pending.Count} завдань";
            foreach (var jobId in pending.ToArray())
            {
                var items = groups[jobId];
                try
                {
                    var observation = await _printService.GetJobStatusAsync(items[0].Submission);
                    foreach (var item in items) ApplyObservation(item.Row, observation);
                    if (observation.IsTerminalForMonitoring) pending.Remove(jobId);
                }
                catch (Exception exception)
                {
                    foreach (var item in items)
                    {
                        item.Row.PrintStatus = PrintItemStatus.ResultNotConfirmed;
                        item.Row.PrintError = $"Не вдалося прочитати стан job ID {jobId}: {exception.Message}";
                    }
                    pending.Remove(jobId);
                    _logger.Error("print.job.observe", exception, printStatus: $"job_id={jobId}");
                }
            }
            if (pending.Count > 0) await Task.Delay(500);
        }

        foreach (var jobId in pending)
        {
            foreach (var item in groups[jobId])
            {
                if (item.Row.PrintStatus == PrintItemStatus.Paused) continue;
                item.Row.PrintStatus = PrintItemStatus.ResultNotConfirmed;
                item.Row.PrintError = $"Job ID {jobId} залишився у черзі після завершення періоду спостереження.";
            }
        }
    }

    private static void ApplyObservation(ReceiptRowViewModel row, WindowsPrintJobObservation observation)
    {
        row.PrintError = observation.Details;
        row.PrintStatus = observation.State switch
        {
            WindowsPrintJobState.Paused => PrintItemStatus.Paused,
            WindowsPrintJobState.Error => PrintItemStatus.Error,
            WindowsPrintJobState.CompletedBySpooler or WindowsPrintJobState.Disappeared => PrintItemStatus.ResultNotConfirmed,
            _ => PrintItemStatus.SubmittedToWindowsQueue
        };
    }

    private void StopBatch()
    {
        if (_batchCancellation is null || _batchCancellation.IsCancellationRequested) return;
        _batchCancellation.Cancel();
        StatusText = "Зупиняємо подальше передавання чеків. Уже створені Windows jobs не скасовуються.";
        RaiseCommands();
    }

    private async Task RetrySafeFailuresAsync()
    {
        var safeFailures = Receipts.Where(x => x.CanRetryWithoutWarning).ToArray();
        SearchText = string.Empty;
        SelectedType = ReceiptTypes[0];
        foreach (var row in Receipts) row.IsSelected = safeFailures.Contains(row);
        NotifySelectionMetrics();
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

    private void OnSelectionChanged(object? sender, EventArgs e) => NotifySelectionMetrics();

    private void NotifySelectionMetrics()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(VisibleSelectedCount));
        OnPropertyChanged(nameof(HiddenSelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        RaiseCommands();
    }

    private void NotifyPrintMetrics()
    {
        OnPropertyChanged(nameof(FailedCount));
        OnPropertyChanged(nameof(SafeFailedCount));
        RaiseCommands();
    }

    private void RaiseCommands()
    {
        foreach (var command in new ICommand[]
                 {
                     RefreshCommand, TodayCommand, SelectAllCommand, ClearSelectionCommand,
                     PrintSelectedCommand, RetryFailedCommand, StopBatchCommand, PreviewCommand, SettingsCommand
                 })
        {
            if (command is RelayCommand relay) relay.RaiseCanExecuteChanged();
            if (command is AsyncRelayCommand asyncRelay) asyncRelay.RaiseCanExecuteChanged();
        }
    }
}

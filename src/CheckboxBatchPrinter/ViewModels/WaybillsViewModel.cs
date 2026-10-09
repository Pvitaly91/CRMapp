using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed record WaybillCarrierOption(ShippingCarrier? Value, string DisplayName);

/// <summary>Carrier-owned documents, without marketplace orders or Checkbox credentials.</summary>
public sealed class WaybillsViewModel : ObservableObject, IDisposable
{
    private readonly IWaybillService _service;
    private readonly IWaybillLabelPreparation? _preparation;
    private readonly IShippingSettingsStore? _settings;
    private readonly ILabelHistoryStore? _history;
    private readonly ILabelPrinter? _printer;
    private readonly ILabelDialogs? _dialogs;
    private readonly WaybillRows _rows = new();
    private CancellationTokenSource? _requestCancellation, _debounceCancellation;
    private long _requestVersion;
    private int _visibleCount;
    private DateOnly? _dateFrom, _dateTo;
    private bool _active, _disposed, _needsRefresh = true, _isBusy, _replacingRows;
    private string _searchText = "", _status = "Відкрийте вкладку, щоб завантажити накладні служб доставки.";
    private WaybillCarrierOption? _selectedCarrier;
    private WaybillRowViewModel? _selectedRow;
    private Func<WaybillRecord, IReadOnlyList<string>>? _associatedOrders;
    private CancellationTokenSource? _printCancellation;
    private bool _printingBusy, _transmitting;
    private string _printStatus = "";

    public WaybillsViewModel(IWaybillService service, IWaybillLabelPreparation? preparation = null,
        IShippingSettingsStore? settings = null, ILabelHistoryStore? history = null,
        ILabelPrinter? printer = null, ILabelDialogs? dialogs = null)
    {
        _service = service;
        _preparation = preparation; _settings = settings; _history = history; _printer = printer; _dialogs = dialogs;
        _dateFrom = _dateTo = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
        Carriers = [new(null, "Усі служби"), new(ShippingCarrier.NovaPoshta, "Нова пошта"),
            new(ShippingCarrier.RozetkaDelivery, "Rozetka Delivery")];
        _selectedCarrier = Carriers[0];
        View = new ListCollectionView(Rows) { Filter = MatchesFilter };
        View.SortDescriptions.Add(new(nameof(WaybillRowViewModel.CreatedLocal), ListSortDirection.Descending));
        View.SortDescriptions.Add(new(nameof(WaybillRowViewModel.TrackingNumber), ListSortDirection.Ascending));
        ((INotifyCollectionChanged)View).CollectionChanged += ViewChanged;
        RefreshCommand = new RelayCommand(_ => _ = RefreshAsync(), _ => !_disposed);
        CancelCommand = new RelayCommand(_ => Cancel(), _ => !_disposed && (IsBusy || _debounceCancellation is not null));
        SelectVisibleCommand = new RelayCommand(_ => { foreach (var row in Visible()) row.IsSelected = row.CanSelect; }, _ => CanChangeSelection);
        ClearSelectionCommand = new RelayCommand(_ => { foreach (var row in Rows) row.IsSelected = false; }, _ => CanChangeSelection);
        PreviewCommand = new AsyncRelayCommand(p => RunAsync(p as WaybillRowViewModel ?? SelectedRow, false),
            p => CanPrint && (p is WaybillRowViewModel row ? row.CanSelect : SelectedRow?.CanSelect == true || VisibleSelectedCount > 0));
        PrintSelectedCommand = new AsyncRelayCommand(_ => RunAsync(null, true), _ => CanPrint && VisibleSelectedCount > 0);
        RetryFailedCommand = new AsyncRelayCommand(_ => RunAsync(null, true, true), _ => CanPrint && Visible().Any(r => r.IsSelected && r.CanRetry));
        CancelPrintCommand = new RelayCommand(_ => CancelPrint(), _ => !_disposed && IsPrintingBusy && !_transmitting);
    }

    public ObservableCollection<WaybillRowViewModel> Rows => _rows;
    public ICollectionView View { get; }
    public IReadOnlyList<WaybillCarrierOption> Carriers { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand SelectVisibleCommand { get; }
    public ICommand ClearSelectionCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand PrintSelectedCommand { get; }
    public ICommand RetryFailedCommand { get; }
    public ICommand CancelPrintCommand { get; }
    public event EventHandler? DocumentsChanged;
    public event EventHandler? PrintCompleted;
    public WaybillRowViewModel? SelectedRow
    {
        get => _selectedRow;
        set { if (SetProperty(ref _selectedRow, value)) RaisePrintCommands(); }
    }
    public bool IsPrintingBusy
    {
        get => _printingBusy;
        private set { if (SetProperty(ref _printingBusy, value)) RaisePrintCommands(); }
    }
    public string PrintStatus { get => _printStatus; private set => SetProperty(ref _printStatus, value); }
    public int VisibleSelectedCount => Visible().Count(r => r.IsSelected);
    public int HiddenSelectedCount => Rows.Count(r => r.IsSelected) - VisibleSelectedCount;
    public string SelectionText => $"Вибрано видимих: {VisibleSelectedCount}\nВибрано прихованих: {HiddenSelectedCount}";
    private bool CanChangeSelection => !_disposed && !IsPrintingBusy && !IsBusy;
    private bool CanPrint => CanChangeSelection && _preparation is not null && _settings is not null &&
        _history is not null && _printer is not null && _dialogs is not null;
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
            RaisePrintCommands();
        }
    }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public string CountText => $"Накладних: {_visibleCount} із {Rows.Count}";
    public string DateRangeText
    {
        get
        {
            return $"Період: {(_dateFrom?.ToString("dd.MM.yyyy") ?? "не вказано")} — {(_dateTo?.ToString("dd.MM.yyyy") ?? "не вказано")}.";
        }
    }
    public string SearchText
    {
        get => _searchText;
        set { if (SetProperty(ref _searchText, value)) View.Refresh(); }
    }
    public WaybillCarrierOption? SelectedCarrier
    {
        get => _selectedCarrier;
        set { if (SetProperty(ref _selectedCarrier, value)) View.Refresh(); }
    }

    public void SetDisplayDates(DateOnly? from, DateOnly? to)
    {
        if (_disposed || _dateFrom == from && _dateTo == to) return;
        _dateFrom = from;
        _dateTo = to;
        StopPending();
        _needsRefresh = true;
        ReplaceRows([]);
        OnPropertyChanged(nameof(DateRangeText));
        Status = ValidRange() ? "Період змінено. Накладні ще не завантажено." : RangeError();
        if (_active && ValidRange()) ScheduleRefresh();
    }

    public void SetActive(bool active)
    {
        if (_disposed || _active == active) return;
        _active = active;
        if (active)
        {
            if (_needsRefresh && ValidRange()) ScheduleRefresh();
        }
        else
        {
            var pending = IsBusy || _debounceCancellation is not null;
            StopPending();
            if (pending)
            {
                _needsRefresh = true;
                Status = "Завантаження призупинено після виходу з вкладки.";
            }
        }
    }

    /// <summary>Clear documents after settings may have changed account identity.</summary>
    public void Invalidate()
    {
        if (_disposed) return;
        StopPending();
        _needsRefresh = true;
        ReplaceRows([]);
        Status = "Налаштування оновлено. Накладні ще не завантажено.";
        if (_active && ValidRange()) ScheduleRefresh();
    }

    public async Task RefreshAsync()
    {
        if (_disposed) return;
        StopPending();
        if (!ValidRange())
        {
            _needsRefresh = true;
            Status = RangeError();
            return;
        }
        var version = _requestVersion;
        var range = EffectiveRange();
        using var cancellation = new CancellationTokenSource();
        _requestCancellation = cancellation;
        _needsRefresh = false;
        IsBusy = true;
        Status = "Завантажую накладні Нової пошти та Rozetka Delivery…";
        try
        {
            var result = await _service.FetchAsync(range.From, range.To, cancellation.Token);
            if (_disposed || version != _requestVersion || cancellation.IsCancellationRequested) return;
            // Every result describes its own current sources. Failed sources do not
            // inherit another account's old documents or another date range.
            ReplaceRows(result.Documents);
            await RestoreHistoryAsync();
            if (_disposed || version != _requestVersion || cancellation.IsCancellationRequested) return;
            Status = result.Sources.Count == 0
                ? (Rows.Count == 0 ? "Накладних за вибраний період не знайдено." : $"Завантажено накладних: {Rows.Count}.")
                : string.Join(Environment.NewLine, result.Sources.Select(SourceStatus));
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (version != _requestVersion || _disposed) return;
            _needsRefresh = true;
            Status = "Завантаження скасовано.";
        }
        catch (Exception)
        {
            if (version != _requestVersion || _disposed) return;
            _needsRefresh = true;
            Status = "Не вдалося завантажити накладні. Перевірте API-ключі та з’єднання зі службами доставки." +
                (Rows.Count > 0 ? " Показано раніше завантажені дані цього періоду." : "");
        }
        finally
        {
            if (ReferenceEquals(_requestCancellation, cancellation)) _requestCancellation = null;
            if (!_disposed && version == _requestVersion) IsBusy = false;
        }
    }

    public void Cancel()
    {
        if (_disposed) return;
        StopPending();
        _needsRefresh = true;
        Status = "Завантаження скасовано.";
    }

    private void ScheduleRefresh()
    {
        var cancellation = new CancellationTokenSource();
        _debounceCancellation = cancellation;
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        _ = RefreshAfterDelayAsync(cancellation);
    }

    private async Task RefreshAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(350), cancellation.Token);
            if (_disposed || !_active || cancellation.IsCancellationRequested || !ReferenceEquals(_debounceCancellation, cancellation)) return;
            _debounceCancellation = null;
            await RefreshAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_debounceCancellation, cancellation)) _debounceCancellation = null;
            cancellation.Dispose();
            if (!_disposed) ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        }
    }

    private void StopPending()
    {
        _requestVersion++;
        if (!_transmitting) _printCancellation?.Cancel();
        _debounceCancellation?.Cancel();
        _debounceCancellation = null;
        _requestCancellation?.Cancel();
        _requestCancellation = null;
        IsBusy = false;
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
    }

    // Carrier listing calls require a finite period even when the other
    // dashboards use an open boundary only to filter already loaded rows.
    private (DateOnly From, DateOnly To) EffectiveRange()
    {
        var today = DateOnly.FromDateTime(DateRangeBuilder.TodayKyiv);
        return (_dateFrom ?? today, _dateTo ?? today);
    }
    private bool ValidRange() => _dateFrom is { } from && _dateTo is { } to && from <= to;
    private string RangeError() => _dateFrom is null || _dateTo is null
        ? "Для завантаження накладних укажіть дату початку й завершення."
        : "Дата початку має бути не пізнішою за дату завершення.";

    private bool MatchesFilter(object value)
    {
        if (value is not WaybillRowViewModel row) return false;
        if (SelectedCarrier?.Value is { } carrier && row.Carrier != carrier) return false;
        if (string.IsNullOrWhiteSpace(SearchText)) return true;
        var query = SearchText.Trim();
        return new[] { row.TrackingNumber, row.CarrierName, row.ConnectionName, row.Status, row.Recipient,
            row.City, row.DocumentId, row.AssociatedOrders }.Any(text => text.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ReplaceRows(IEnumerable<WaybillRecord> documents)
    {
        var oldRows = Rows.ToArray();
        var oldSelection = SelectedRow;
        var replacement = documents.Select(document =>
        {
            var row = new WaybillRowViewModel(document);
            var previous = oldRows.FirstOrDefault(r => SameIdentity(r.Model, document));
            if (previous is not null) row.RestoreState(previous);
            if (_associatedOrders is not null) row.ApplyAssociatedOrders(_associatedOrders(document));
            row.PropertyChanged += RowChanged;
            return row;
        }).ToArray();
        foreach (var row in oldRows) row.PropertyChanged -= RowChanged;
        _replacingRows = true;
        try
        {
            // DeferRefresh does not defer ObservableCollection's Add events.
            // A bound DataGrid may enumerate the deferred view while handling
            // such an event. Publish the complete collection in one Reset.
            _rows.ReplaceAll(replacement);
        }
        finally
        {
            _replacingRows = false;
            SelectedRow = oldSelection is null ? null : replacement.FirstOrDefault(r => SameIdentity(r.Model, oldSelection.Model));
            UpdateDisplayIndices();
        }
        DocumentsChanged?.Invoke(this, EventArgs.Empty);
    }
    private void ViewChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_replacingRows) UpdateDisplayIndices();
    }
    private void UpdateDisplayIndices()
    {
        var index = 0;
        foreach (var row in View.Cast<WaybillRowViewModel>()) row.DisplayIndex = ++index;
        _visibleCount = index;
        OnPropertyChanged(nameof(CountText));
        UpdateSelection();
    }

    public void UpdateAssociatedOrders(Func<WaybillRecord, IReadOnlyList<string>> resolver)
    {
        _associatedOrders = resolver;
        foreach (var row in Rows) row.ApplyAssociatedOrders(resolver(row.Model));
        if (!string.IsNullOrWhiteSpace(SearchText)) View.Refresh();
    }

    private WaybillRowViewModel[] Visible() => View.Cast<WaybillRowViewModel>().ToArray();
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(WaybillRowViewModel.IsSelected) or nameof(WaybillRowViewModel.PrintStatus) or nameof(WaybillRowViewModel.CanRetry)) UpdateSelection();
    }
    private void UpdateSelection()
    {
        OnPropertyChanged(nameof(VisibleSelectedCount));
        OnPropertyChanged(nameof(HiddenSelectedCount));
        OnPropertyChanged(nameof(SelectionText));
        RaisePrintCommands();
    }
    private void RaisePrintCommands()
    {
        foreach (var command in new[] { SelectVisibleCommand, ClearSelectionCommand, CancelPrintCommand })
            (command as RelayCommand)?.RaiseCanExecuteChanged();
        foreach (var command in new[] { PreviewCommand, PrintSelectedCommand, RetryFailedCommand })
            (command as AsyncRelayCommand)?.RaiseCanExecuteChanged();
    }
    internal static bool SameIdentity(WaybillRecord left, WaybillRecord right) =>
        left.Carrier == right.Carrier && left.ConnectionId == right.ConnectionId &&
        left.DocumentId == right.DocumentId && left.TrackingNumber == right.TrackingNumber &&
        left.CredentialFingerprint == right.CredentialFingerprint && left.Places == right.Places && left.IsDeleted == right.IsDeleted;

    public async Task RestoreHistoryAsync()
    {
        if (_history is null || _disposed || IsPrintingBusy) return;
        var version = _requestVersion;
        try
        {
            var config = _settings is null ? null : await _settings.LoadAsync();
            var history = await _history.LoadAsync();
            if (_disposed || version != _requestVersion || IsPrintingBusy) return;
            foreach (var row in Rows)
            {
                if (config is not null) row.ApplyAccountAliases(config);
                row.ApplyHistory(history);
            }
        }
        catch (Exception) { PrintStatus = "Не вдалося прочитати історію накладних; перед друком вона перевірятиметься знову."; }
    }

    public void CancelPrint()
    {
        if (_disposed || _transmitting) return;
        _printCancellation?.Cancel();
    }

    public async Task RunAsync(WaybillRowViewModel? previewRow, bool print, bool retryFailed = false)
    {
        if (!CanPrint) return;
        var rows = previewRow is null ? Visible().Where(r => r.IsSelected && r.CanSelect && (!retryFailed || r.CanRetry)).ToArray()
            : Rows.Contains(previewRow) && previewRow.CanSelect ? [previewRow] : [];
        if (!print && previewRow is null && rows.Length > 1) rows = [rows[0]];
        if (rows.Length == 0) { _dialogs!.Error("Позначте видимі накладні для друку."); return; }
        if (rows.Length > 100) { _dialogs!.Error("Один пакет може містити не більше 100 накладних. Зменшіть вибір."); return; }
        var version = _requestVersion;
        var requests = rows.Select(r => new WaybillPrintRequest(r.Model with { }, r.AssociatedOrderNumbers.ToArray())).ToArray();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _printCancellation = cancellation;
        IsPrintingBusy = true;
        try
        {
            PrintStatus = "Готую та перевіряю накладні…";
            var config = await _settings!.LoadAsync(cancellation.Token);
            CheckCurrent();
            var settings = config.Print with { };
            foreach (var row in rows) row.ApplyAccountAliases(config);
            var geometry = _printer!.Inspect(settings);
            var prepared = await _preparation!.PrepareAsync(requests, settings, geometry, cancellation.Token);
            CheckCurrent();
            var batch = prepared.Batch;
            var history = await _history!.LoadAsync(cancellation.Token);
            CheckCurrent();
            foreach (var row in rows) row.ApplyHistory(history);
            var previous = history.Where(a => a.State != LabelSubmissionState.NotSubmitted && a.Pages.Any(old =>
                rows.Any(row => row.MatchesHistoryPage(old) && batch.Pages.Any(page => Matches(row.Model, page))))).ToArray();
            if (batch.Pages.Count == 0)
            { PrintStatus = "Немає доступних накладних для друку."; return; }
            if (retryFailed && previous.Length > 0)
            { PrintStatus = "Автоматичний повтор зупинено: передавання цих накладних уже почалося або завершилося. Перевірте історію."; return; }
            if (!_dialogs!.Preview(batch, geometry, previous, print) || !print)
            { PrintStatus = "Перегляд завершено; принтер не викликався."; return; }
            CheckCurrent();
            await _preparation.ValidateAsync(prepared, cancellation.Token);
            CheckCurrent();
            var currentSettings = (await _settings.LoadAsync(cancellation.Token)).Print;
            if (currentSettings != settings || _printer.Inspect(settings) != geometry)
                throw new InvalidOperationException("Налаштування принтера змінилися. Підготуйте пакет заново.");
            CheckCurrent();
            var backend = new GuardedBackend(_printer, CheckCurrent, () =>
            {
                _transmitting = true;
                PrintStatus = "Передаю пакет Windows…";
                ((RelayCommand)CancelPrintCommand).RaiseCanExecuteChanged();
            });
            var attempt = await new LabelPrintCoordinator(_history, backend).SubmitAsync(batch, cancellation.Token);
            ApplyAttempt(attempt);
            if (attempt.State == LabelSubmissionState.Submitted)
            {
                foreach (var row in Rows.Where(r => requests.Any(request => SameIdentity(r.Model, request.Waybill)) &&
                    batch.Pages.Any(p => Matches(r.Model, p)) &&
                    !batch.SkippedShipments.Any(s => Matches(r.Model, s.Shipment)))) row.IsSelected = false;
                PrintStatus = "Пакет передано Windows. Це не підтвердження фізичного виходу накладних.";
            }
            else PrintStatus = "Передавання не підтверджено. Перевірте принтер перед повтором, щоб уникнути дубля.";
            PrintCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        { PrintStatus = _transmitting ? "Передавання почалося; перевірте принтер та історію перед повтором." : "Підготовку скасовано. Нового друку не запущено."; }
        catch (LabelTransmissionException exception)
        {
            ApplyAttempt(exception.Attempt);
            PrintStatus = exception.Message;
            _dialogs!.Error(exception.Message);
            PrintCompleted?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception exception)
        {
            foreach (var row in rows) row.MarkFailed();
            PrintStatus = "Пакет не передано; перевірте накладні та підключення.";
            _dialogs!.Error(exception is ShippingLabelException or InvalidOperationException ? exception.Message :
                "Не вдалося підготувати накладні. Перевірте локальні налаштування.");
        }
        finally
        {
            var crossedTransfer = _transmitting;
            if (ReferenceEquals(_printCancellation, cancellation)) _printCancellation = null;
            _transmitting = false;
            IsPrintingBusy = false;
            if (crossedTransfer && !_disposed) await RestoreHistoryAsync();
        }

        void CheckCurrent()
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || version != _requestVersion || requests.Any(request =>
                !Rows.Any(row => SameIdentity(row.Model, request.Waybill))))
                throw new InvalidOperationException("Накладні оновлено або підключення змінено. Підготуйте й підтвердьте актуальний пакет заново.");
        }
        void ApplyAttempt(LabelPrintAttempt attempt)
        {
            foreach (var row in Rows.Where(row => requests.Any(request => SameIdentity(row.Model, request.Waybill)))) row.ApplyAttempt(attempt);
        }
    }
    private static bool Matches(WaybillRecord document, ShippingLabelPage page) =>
        Matches(document, page.Shipment) && document.ConnectionId == page.ConnectionId;
    private static bool Matches(WaybillRecord document, ShipmentReference shipment) =>
        document.Carrier == shipment.Carrier && document.TrackingNumber == shipment.TrackingNumber &&
        document.ConnectionId == shipment.CarrierConnectionId;

    private sealed class GuardedBackend(ILabelBatchBackend inner, Action checkCurrent, Action startTransfer) : ILabelBatchBackend
    {
        public Task<int?> SubmitAsync(LabelPrintBatch batch, string jobName, Func<Task> beforeTransfer,
            Func<int, Task> jobCreated, CancellationToken cancellationToken = default)
        {
            checkCurrent();
            return inner.SubmitAsync(batch, jobName, async () =>
            {
                checkCurrent();
                await beforeTransfer();
                checkCurrent();
                startTransfer();
            }, jobCreated, cancellationToken);
        }
    }
    private static string SourceStatus(WaybillSourceStatus source)
    {
        var description = WaybillRowViewModel.NameFor(source.Carrier) +
            (string.IsNullOrWhiteSpace(source.ConnectionName) ? "" : " · " + source.ConnectionName);
        return source.Complete
            ? $"{description}: {source.Count} накладних." + (string.IsNullOrWhiteSpace(source.Message) ? "" : " " + source.Message)
            : $"{description}: завантаження неповне ({source.Count}). {source.Message}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        StopPending();
        _disposed = true;
        ((INotifyCollectionChanged)View).CollectionChanged -= ViewChanged;
        ((RelayCommand)RefreshCommand).RaiseCanExecuteChanged();
        ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
        foreach (var row in Rows) row.PropertyChanged -= RowChanged;
        RaisePrintCommands();
    }

    private sealed class WaybillRows : ObservableCollection<WaybillRowViewModel>
    {
        public void ReplaceAll(IReadOnlyList<WaybillRowViewModel> replacement)
        {
            CheckReentrancy();
            Items.Clear();
            foreach (var row in replacement) Items.Add(row);
            OnPropertyChanged(new(nameof(Count)));
            OnPropertyChanged(new("Item[]"));
            OnCollectionChanged(new(NotifyCollectionChangedAction.Reset));
        }
    }
}

public sealed class WaybillRowViewModel(WaybillRecord model) : ObservableObject
{
    private int _displayIndex;
    private bool _selected, _failed, _hasPrintedLabel;
    private LabelSubmissionState _printStatus;
    private string _printStatusText = "Не передано";
    private IReadOnlyList<string> _associatedOrderNumbers = [];
    private HashSet<string> _accountConnectionIds = new(StringComparer.Ordinal) { model.ConnectionId };
    public WaybillRecord Model { get; } = model;
    public int DisplayIndex { get => _displayIndex; internal set => SetProperty(ref _displayIndex, value); }
    public ShippingCarrier Carrier => Model.Carrier;
    public string CarrierName => NameFor(Carrier);
    public string ConnectionId => Model.ConnectionId;
    public string ConnectionName => Model.ConnectionName;
    public string DocumentId => Model.DocumentId;
    public string TrackingNumber => Model.TrackingNumber;
    public DateTime? CreatedLocal => Model.CreatedLocal;
    public string Status => Model.Status;
    public string Recipient => Model.Recipient;
    public string City => Model.City;
    public decimal? DeclaredValue => Model.DeclaredValue;
    public int? Places => Model.Places;
    public bool IsDeleted => Model.IsDeleted;
    public bool CanSelect => !IsDeleted;
    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value && CanSelect); }
    public LabelSubmissionState PrintStatus { get => _printStatus; private set => SetProperty(ref _printStatus, value); }
    public string PrintStatusText { get => _printStatusText; private set => SetProperty(ref _printStatusText, value); }
    public bool HasPrintedLabel { get => _hasPrintedLabel; private set => SetProperty(ref _hasPrintedLabel, value); }
    public bool CanRetry => _failed && PrintStatus == LabelSubmissionState.NotSubmitted && !HasPrintedLabel;
    public IReadOnlyList<string> AssociatedOrderNumbers => _associatedOrderNumbers;
    public string AssociatedOrders => string.Join(", ", _associatedOrderNumbers);

    public void ApplyAssociatedOrders(IReadOnlyList<string> numbers)
    {
        var current = numbers.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToArray();
        if (_associatedOrderNumbers.SequenceEqual(current)) return;
        _associatedOrderNumbers = current;
        OnPropertyChanged(nameof(AssociatedOrderNumbers));
        OnPropertyChanged(nameof(AssociatedOrders));
    }
    public void ApplyAccountAliases(ShippingLabelSettings config)
    {
        _accountConnectionIds = new(StringComparer.Ordinal) { ConnectionId };
        if (Carrier != ShippingCarrier.NovaPoshta) return;
        var own = config.NovaPoshtaConnections.Where(connection => connection.Id == ConnectionId).ToArray();
        if (own.Length != 1 || string.IsNullOrWhiteSpace(own[0].ApiKey)) return;
        if (Model.CredentialFingerprint.Length > 0 && Model.CredentialFingerprint !=
            WaybillAccountProof.Create(Carrier, ConnectionId, own[0].ApiKey)) return;
        _accountConnectionIds.UnionWith(config.NovaPoshtaConnections.Where(connection =>
            connection.ApiKey.Equals(own[0].ApiKey, StringComparison.Ordinal)).Select(connection => connection.Id));
    }
    internal void RestoreState(WaybillRowViewModel row)
    {
        IsSelected = row.IsSelected;
        _failed = row._failed;
        PrintStatus = row.PrintStatus;
        PrintStatusText = row.PrintStatusText;
        HasPrintedLabel = row.HasPrintedLabel;
    }
    public void ApplyHistory(IEnumerable<LabelPrintAttempt> history)
    {
        var attempts = history.Where(a => a.Pages.Any(MatchesHistoryPage)).OrderBy(a => a.TimeUtc).ToArray();
        var latest = attempts.LastOrDefault();
        // A failed repeat cannot erase an earlier crossing of the Windows boundary.
        var submitted = latest?.State == LabelSubmissionState.NotSubmitted
            ? attempts.LastOrDefault(a => a.State != LabelSubmissionState.NotSubmitted) : latest;
        HasPrintedLabel = attempts.Any(a => a.State == LabelSubmissionState.Submitted &&
            a.Pages.Where(MatchesHistoryPage).Select(p => p.PageNumber).Distinct().Count() >= Math.Max(1, Model.Places ?? 1));
        PrintStatus = submitted?.State ?? LabelSubmissionState.NotSubmitted;
        _failed = latest?.State == LabelSubmissionState.NotSubmitted && PrintStatus == LabelSubmissionState.NotSubmitted;
        PrintStatusText = PrintStatus switch
        {
            LabelSubmissionState.Submitted => HasPrintedLabel ? $"Передано Windows, job {submitted?.WindowsJobId}" : "Частину накладної передано Windows",
            LabelSubmissionState.SubmissionUnknown => "Передавання невідоме; можливий дубль",
            _ => _failed ? "Не передано; можна повторити" : "Не передано"
        };
        OnPropertyChanged(nameof(CanRetry));
    }
    internal void ApplyAttempt(LabelPrintAttempt attempt)
    {
        if (!attempt.Pages.Any(MatchesHistoryPage)) return;
        if (attempt.State == LabelSubmissionState.Submitted &&
            attempt.Pages.Where(MatchesHistoryPage).Select(p => p.PageNumber).Distinct().Count() >= Math.Max(1, Model.Places ?? 1)) HasPrintedLabel = true;
        if (attempt.State == LabelSubmissionState.NotSubmitted && PrintStatus != LabelSubmissionState.NotSubmitted)
        { _failed = false; OnPropertyChanged(nameof(CanRetry)); return; }
        PrintStatus = attempt.State;
        _failed = attempt.State == LabelSubmissionState.NotSubmitted && PrintStatus == LabelSubmissionState.NotSubmitted && !HasPrintedLabel;
        PrintStatusText = PrintStatus switch
        {
            LabelSubmissionState.Submitted => HasPrintedLabel ? $"Передано Windows, job {attempt.WindowsJobId}" : "Частину накладної передано Windows",
            LabelSubmissionState.SubmissionUnknown => "Передавання невідоме; можливий дубль",
            _ => "Не передано; можна повторити"
        };
        OnPropertyChanged(nameof(CanRetry));
    }
    internal void MarkFailed()
    {
        // A preparation failure cannot make an earlier ambiguous/submitted job safe to retry.
        _failed = PrintStatus == LabelSubmissionState.NotSubmitted && !HasPrintedLabel;
        if (_failed) PrintStatusText = "Не передано; помилка підготовки";
        OnPropertyChanged(nameof(CanRetry));
    }
    internal bool MatchesHistoryPage(LabelAttemptPage page) => page.Carrier == Carrier && _accountConnectionIds.Contains(page.ConnectionId) &&
        page.TrackingNumber == TrackingNumber;
    internal static string NameFor(ShippingCarrier carrier) => carrier switch
    {
        ShippingCarrier.NovaPoshta => "Нова пошта",
        ShippingCarrier.RozetkaDelivery => "Rozetka Delivery",
        _ => "Невідома служба"
    };
}

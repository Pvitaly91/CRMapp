using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

public sealed record WaybillCarrierOption(ShippingCarrier? Value, string DisplayName);

/// <summary>Carrier-owned documents, without marketplace orders or Checkbox credentials.</summary>
public sealed class WaybillsViewModel : ObservableObject, IDisposable
{
    private readonly IWaybillService _service;
    private readonly WaybillRows _rows = new();
    private CancellationTokenSource? _requestCancellation, _debounceCancellation;
    private long _requestVersion;
    private int _visibleCount;
    private DateOnly? _dateFrom, _dateTo;
    private bool _active, _disposed, _needsRefresh = true, _isBusy, _replacingRows;
    private string _searchText = "", _status = "Відкрийте вкладку, щоб завантажити накладні служб доставки.";
    private WaybillCarrierOption? _selectedCarrier;

    public WaybillsViewModel(IWaybillService service)
    {
        _service = service;
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
    }

    public ObservableCollection<WaybillRowViewModel> Rows => _rows;
    public ICollectionView View { get; }
    public IReadOnlyList<WaybillCarrierOption> Carriers { get; }
    public ICommand RefreshCommand { get; }
    public ICommand CancelCommand { get; }
    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;
            ((RelayCommand)CancelCommand).RaiseCanExecuteChanged();
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
            row.City, row.DocumentId }.Any(text => text.Contains(query, StringComparison.CurrentCultureIgnoreCase));
    }

    private void ReplaceRows(IEnumerable<WaybillRecord> documents)
    {
        var replacement = documents.Select(document => new WaybillRowViewModel(document)).ToArray();
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
            UpdateDisplayIndices();
        }
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
    internal static string NameFor(ShippingCarrier carrier) => carrier switch
    {
        ShippingCarrier.NovaPoshta => "Нова пошта",
        ShippingCarrier.RozetkaDelivery => "Rozetka Delivery",
        _ => "Невідома служба"
    };
}

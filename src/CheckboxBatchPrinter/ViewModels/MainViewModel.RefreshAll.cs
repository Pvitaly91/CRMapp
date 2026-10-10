using System.ComponentModel;
using System.Windows.Input;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MainViewModel
{
    private Task? _refreshAllTask;
    private CancellationTokenSource? _refreshAllCancellation;
    private bool _isRefreshingAll;
    private string _refreshAllStatus = "";
    private ShippingLabelsViewModel? _refreshObservedLabels;

    public ICommand RefreshAllCommand { get; }
    public ICommand CancelRefreshAllCommand { get; }
    public bool IsRefreshingAll
    {
        get => _isRefreshingAll;
        private set
        {
            if (!SetProperty(ref _isRefreshingAll, value)) return;
            OnPropertyChanged(nameof(RefreshAllButtonText));
            NotifyBusyChanged();
        }
    }
    public string RefreshAllButtonText => IsRefreshingAll ? "Оновлення…" : "Оновити все";
    public string RefreshAllStatus { get => _refreshAllStatus; private set => SetProperty(ref _refreshAllStatus, value); }
    private bool CanRefreshAll => !IsBusy && Marketplace?.Labels?.IsBusy != true && Waybills?.IsPrintingBusy != true;

    private void NotifyBusyChanged()
    {
        OnPropertyChanged(nameof(IsBusy));
        AllReceiptsTab.IsHeaderSelectionBusy = IsBusy;
        OrdersReceiptsTab.IsHeaderSelectionBusy = IsBusy;
        RaiseCommands();
    }

    private void ObserveRefreshLabels()
    {
        if (ReferenceEquals(_refreshObservedLabels, Marketplace?.Labels)) return;
        if (_refreshObservedLabels is not null) _refreshObservedLabels.PropertyChanged -= RefreshLabelsChanged;
        _refreshObservedLabels = Marketplace?.Labels;
        if (_refreshObservedLabels is not null) _refreshObservedLabels.PropertyChanged += RefreshLabelsChanged;
        RaiseCommands();
    }
    private void RefreshLabelsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShippingLabelsViewModel.IsBusy)) RaiseCommands();
    }
    private void CancelGlobalRefreshForDateChange()
    {
        if (IsRefreshingAll) CancelGlobalRefresh();
    }
    private void CancelGlobalRefresh()
    {
        _refreshAllCancellation?.Cancel();
        RaiseCommands();
    }

    public Task RefreshAllAsync()
    {
        if (_refreshAllTask is { IsCompleted: false }) return _refreshAllTask;
        if (!CanRefreshAll) return Task.CompletedTask;
        if (DateFrom is null || DateTo is null)
        {
            _dialogs.ShowError("Оберіть обидві дати.");
            return Task.CompletedTask;
        }
        if (DateTo.Value.Date < DateFrom.Value.Date)
        {
            _dialogs.ShowError("Дата «до» не може бути раніше дати «від».");
            return Task.CompletedTask;
        }
        var from = DateOnly.FromDateTime(DateFrom.Value);
        var to = DateOnly.FromDateTime(DateTo.Value);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshAllTask = completion.Task;
        _ = RunRefreshAllAsync(from, to, completion);
        return completion.Task;
    }

    private async Task RunRefreshAllAsync(DateOnly from, DateOnly to, TaskCompletionSource completion)
    {
        using var cancellation = new CancellationTokenSource();
        _refreshAllCancellation = cancellation;
        IsRefreshingAll = true;
        if (Marketplace is not null)
        {
            Marketplace.IsWorkspaceRefreshing = true;
            Marketplace.ReceiptCoverageFresh = false;
        }
        if (Waybills is not null) Waybills.IsWorkspaceRefreshing = true;
        _dateLoadCancellation?.Cancel();
        var token = cancellation.Token;
        try
        {
            RefreshAllStatus = "Оновлення всіх даних: чеки…";
            if (_authentication.HasStoredCredentials) await RefreshReceiptsAsync(prepareOrders: false);
            else StatusText = "Checkbox не налаштовано; оновлюю інші служби.";
            token.ThrowIfCancellationRequested();

            if (Marketplace is not null)
            {
                RefreshAllStatus = "Оновлення всіх даних: замовлення та автоприв’язки…";
                try { await Marketplace.RefreshAllAsync(from, to, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.Error("refresh.all.marketplace", exception); }
            }
            token.ThrowIfCancellationRequested();
            if (Waybills is not null)
            {
                RefreshAllStatus = "Оновлення всіх даних: накладні…";
                try { await Waybills.RefreshAllAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.Error("refresh.all.waybills", exception); }
                token.ThrowIfCancellationRequested();
                await UpdateWaybillOrderAssociationsAsync();
            }
            token.ThrowIfCancellationRequested();
            RefreshAllStatus = "Оновлення завершено. Стан кожної служби — у підказці «Оновити все».";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            RefreshAllStatus = "Оновлення скасовано. Для нового періоду натисніть «Оновити все».";
        }
        catch (Exception exception)
        {
            _logger.Error("refresh.all", exception);
            RefreshAllStatus = "Не вдалося завершити оновлення. Перевірте стан підключень.";
        }
        finally
        {
            if (Marketplace is not null) Marketplace.IsWorkspaceRefreshing = false;
            if (Waybills is not null) Waybills.IsWorkspaceRefreshing = false;
            _refreshAllCancellation = null;
            IsRefreshingAll = false;
            completion.TrySetResult();
            if (ReferenceEquals(_refreshAllTask, completion.Task)) _refreshAllTask = null;
        }
    }
}

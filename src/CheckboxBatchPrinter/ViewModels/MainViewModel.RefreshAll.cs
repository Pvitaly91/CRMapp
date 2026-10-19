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
        if (e.PropertyName is nameof(ShippingLabelsViewModel.IsBusy) or nameof(ShippingLabelsViewModel.HeaderSelectionState)) RaiseCommands();
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
        if (!IsProblemOrdersTab && (DateFrom is null || DateTo is null))
        {
            _dialogs.ShowError("Оберіть обидві дати.");
            return Task.CompletedTask;
        }
        if (!IsProblemOrdersTab && DateTo!.Value.Date < DateFrom!.Value.Date)
        {
            _dialogs.ShowError("Дата «до» не може бути раніше дати «від».");
            return Task.CompletedTask;
        }
        var to = IsProblemOrdersTab ? DateOnly.FromDateTime(CheckboxBatchPrinter.Core.Services.DateRangeBuilder.TodayKyiv) : DateOnly.FromDateTime(DateTo!.Value);
        var from = IsProblemOrdersTab ? to.AddDays(-13) : DateOnly.FromDateTime(DateFrom!.Value);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _refreshAllTask = completion.Task;
        _ = RunRefreshAllAsync(from, to, completion, IsProblemOrdersTab);
        return completion.Task;
    }

    private async Task RunRefreshAllAsync(DateOnly from, DateOnly to, TaskCompletionSource completion, bool problemScope = false)
    {
        using var cancellation = new CancellationTokenSource();
        var publication = Marketplace?.BeginRefreshPublication();
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
            RefreshAllStatus = "Паралельно оновлюю Checkbox, Prom, Rozetka та накладні…";
            var expectedAccount = "";
            if (_authentication.HasStoredCredentials)
            {
                try { expectedAccount = CheckboxBatchPrinter.Core.Services.PrintAccountContext.Create(await _settingsService.LoadAsync(token)); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.Error("refresh.all.checkbox.settings", exception); }
            }
            var receipts = _authentication.HasStoredCredentials ? RefreshReceiptsAsync(prepareOrders: false, from, to) : Task.CompletedTask;
            if (!_authentication.HasStoredCredentials) StatusText = "Checkbox не налаштовано; оновлюю інші служби.";
            async Task RefreshMarketplaceAsync()
            {
                if (Marketplace is null) return;
                try { await Marketplace.RefreshAllAsync(from, to, receipts, expectedAccount, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.Error("refresh.all.marketplace", exception); }
            }
            async Task RefreshWaybillsAsync()
            {
                if (Waybills is null || problemScope) return; // Problems use exact-TTN tracking, not a cabinet history scan.
                try { await Waybills.RefreshAllAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception exception) { _logger.Error("refresh.all.waybills", exception); }
            }
            var marketplace = RefreshMarketplaceAsync();
            var waybills = RefreshWaybillsAsync();
            await Task.WhenAll(receipts, marketplace, waybills);
            token.ThrowIfCancellationRequested();
            if (Waybills is not null) await UpdateWaybillOrderAssociationsAsync();
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
            publication?.Dispose();
            if (Marketplace is not null) await Marketplace.FlushAcceptedLinksAsync();
            if (Marketplace is not null) Marketplace.IsWorkspaceRefreshing = false;
            if (Waybills is not null) Waybills.IsWorkspaceRefreshing = false;
            _refreshAllCancellation = null;
            IsRefreshingAll = false;
            if (!token.IsCancellationRequested) QueueShipmentTrackingRefresh(force: true);
            completion.TrySetResult();
            if (ReferenceEquals(_refreshAllTask, completion.Task)) _refreshAllTask = null;
        }
    }
}

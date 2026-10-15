using System.ComponentModel;
using System.Text.Json;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MainViewModel
{
    public ICommand PrintOrderReceiptsCommand { get; }
    private readonly HashSet<MarketplaceOrderRowViewModel> _observedOrderReceiptRows = [];
    private bool CanPrintOrderReceipts => SelectedTabIndex == 1 && !IsBusy && Waybills?.IsPrintingBusy != true &&
        Marketplace is { IsWorkspaceRefreshing: false } workspace && workspace.Labels?.IsBusy != true &&
        workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Any(row => row.IsSelectedForLabels);

    private void ObserveOrderReceiptSelection()
    {
        if (Marketplace is null) return;
        var current = Marketplace.AllOrderRows.ToHashSet();
        foreach (var removed in _observedOrderReceiptRows.Where(row => !current.Contains(row)).ToArray())
        { removed.PropertyChanged -= OrderReceiptSelectionChanged; _observedOrderReceiptRows.Remove(removed); }
        foreach (var added in current.Where(row => _observedOrderReceiptRows.Add(row)))
            added.PropertyChanged += OrderReceiptSelectionChanged;
        RaiseCommands();
    }

    private void OrderReceiptSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MarketplaceOrderRowViewModel.IsSelectedForLabels) or
            nameof(MarketplaceOrderRowViewModel.LinkedReceipts) or nameof(MarketplaceOrderRowViewModel.Model)) RaiseCommands();
    }

    public async Task PrintOrderReceiptsAsync()
    {
        if (!CanPrintOrderReceipts || Marketplace is not { } workspace) return;
        var scope = workspace.SelectionScope;
        var orders = workspace.Orders.Cast<MarketplaceOrderRowViewModel>().Where(row => row.IsSelectedForLabels)
            .Select(row => new CombinedOrder(row, row.Model with
            { Shipments = row.Model.Shipments.Select(shipment => shipment with { }).ToArray() },
                row.LinkedReceipts.DistinctBy(receipt => receipt.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(receipt => receipt.Model.DisplayDate).ThenBy(receipt => receipt.Model.Serial)
                    .ThenBy(receipt => receipt.Id, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        var hidden = workspace.AllOrderRows.Count(row => row.IsSelectedForLabels && !orders.Any(order => order.Row == row));
        var receipts = orders.SelectMany(order => order.Receipts).ToArray();
        var items = orders.SelectMany(order => order.Receipts.Select(receipt =>
            new PrintBatchItem(0, receipt.Id, receipt.Serial, order.Model.Key.Marketplace.ToString(), order.Model.Number,
                ReceiptPrintHeader.TrackingFor(order.Model)))).Select((item, index) => item with { Position = index + 1 }).ToArray();
        var receiptModels = receipts.DistinctBy(row => row.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(row => row.Id, row => JsonSerializer.Serialize(row.Model), StringComparer.OrdinalIgnoreCase);
        var workspaceWasRefreshing = workspace.IsWorkspaceRefreshing;
        var waybillsWereRefreshing = Waybills?.IsWorkspaceRefreshing == true;
        _operationCancellation?.Dispose();
        _operationCancellation = new CancellationTokenSource();
        var token = _operationCancellation.Token;
        var submitted = false;
        var transferStarted = false;
        IsBusy = true;
        IsPrinting = true;
        workspace.IsWorkspaceRefreshing = true;
        if (Waybills is not null) Waybills.IsWorkspaceRefreshing = true;
        void Log(string stage, Exception? exception = null)
        {
            try
            {
                var status = $"orders={orders.Length};receipts={receipts.Length};hidden_orders={hidden}";
                if (exception is null) _logger.Info("order.receipts.print." + stage, printStatus: status);
                else _logger.Error("order.receipts.print." + stage, exception, printStatus: status);
            }
            catch (Exception) { /* Diagnostics cannot turn an accepted job into a retry. */ }
        }
        try
        {
            foreach (var order in orders)
            {
                if (order.Model.IsCanceled) throw new InvalidOperationException($"Замовлення №{order.Model.Number} скасоване. Зніміть його вибір для друку чеків.");
                if (order.Receipts.Length == 0) throw new InvalidOperationException($"Замовлення №{order.Model.Number}: немає завантаженого пов’язаного чека. Перевірте прив’язку або оновіть дані.");
                if (order.Receipts.Any(row => row.Model.Status != "DONE")) throw new InvalidOperationException($"Замовлення №{order.Model.Number}: пов’язаний чек ще не готовий до друку.");
            }
            if (receipts.Length == 0 || receipts.Length > 250 || receiptModels.Count != receipts.Length)
                throw new InvalidOperationException("Перевірте вибір: до 250 чеків, кожний пов’язаний лише з одним вибраним замовленням.");
            var settings = FreezeReceiptPrintSettings(await _settingsService.LoadAsync(token));
            var account = PrintAccountContext.Create(settings);
            if (account != _loadedAccountContext) throw new InvalidOperationException("Касира Checkbox змінено. Натисніть «Оновити все» перед друком.");
            if (string.IsNullOrWhiteSpace(settings.PrinterName) || !_printService.PrinterExists(settings.PrinterName))
                throw new InvalidOperationException("Оберіть доступний принтер чеків у Налаштуваннях → Друк.");
            var documents = new PrintReceiptDocument[receipts.Length];
            long bytes = 0;
            for (var index = 0; index < receipts.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                ProgressText = $"Готую чек {index + 1} із {receipts.Length}…";
                var png = await _imageService.GetPngAsync(items[index].ReceiptId, (int)Math.Round(settings.EffectivePaperWidthMm), token);
                bytes += png.LongLength;
                if (bytes > 80L * 1024 * 1024) throw new InvalidOperationException("Зображення чеків перевищують 80 МіБ. Виберіть менший пакет.");
                documents[index] = new(png.ToArray(), items[index].ReceiptId, items[index].OrderNumber, items[index].TrackingText);
            }
            _printService.ValidateReceiptsForPrint(documents, settings);
            var history = await _printHistoryStore.LoadAsync(account, token);
            var repeated = receipts.Count(row => row.PrintStatus == PrintItemStatus.Done || history.Keys.Any(id => id.Equals(row.Id, StringComparison.OrdinalIgnoreCase)) ||
                _combinedReceiptTransferRisks.Contains((account, row.Id.ToUpperInvariant())));
            token.ThrowIfCancellationRequested();
            if (!_dialogs.ConfirmPrint(new(settings.PrinterName, hidden, items, repeated)))
            { StatusText = "Друк чеків замовлень скасовано. Вибір збережено."; return; }
            token.ThrowIfCancellationRequested();
            var currentSettings = await _settingsService.LoadAsync(token);
            if (_loadedAccountContext != account || PrintAccountContext.Create(currentSettings) != account ||
                workspace.SelectionScope.Generation != scope.Generation || workspace.SelectionScope.Account != scope.Account)
                throw new InvalidOperationException("Обліковий запис або підключення змінено. Підготуйте пакет чеків заново.");
            ValidateCombinedReceiptLinks(orders);
            foreach (var receipt in receipts)
            {
                var current = Receipts.FirstOrDefault(row => row.Id.Equals(receipt.Id, StringComparison.OrdinalIgnoreCase));
                if (current is null || JsonSerializer.Serialize(current.Model) != receiptModels[receipt.Id])
                    throw new InvalidOperationException("Дані чека змінилися. Підготуйте пакет заново.");
            }
            if (!_printService.PrinterExists(settings.PrinterName)) throw new InvalidOperationException("Принтер чеків більше недоступний. Підготуйте пакет заново.");
            token.ThrowIfCancellationRequested();
            foreach (var row in receipts) { row.PrintStatus = PrintItemStatus.Printing; row.PrintError = ""; }
            foreach (var row in receipts) _combinedReceiptTransferRisks.Add((account, row.Id.ToUpperInvariant()));
            transferStarted = true;
            ProgressText = "Передаю чеки замовлень принтеру…";
            Log("submit");
            if (documents.Length == 1) await _printService.PrintReceiptAsync(documents[0], settings, token);
            else await _printService.PrintReceiptsAsSingleJobAsync(documents, settings, token);
            submitted = true;
            foreach (var row in receipts) row.PrintStatus = PrintItemStatus.Done;
            Log("accepted");
            try
            {
                await _printHistoryStore.MarkPrintedAsync(account, items.Select(item => item.ReceiptId).ToArray(), settings.PrinterName);
                foreach (var row in receipts) _combinedReceiptTransferRisks.Remove((account, row.Id.ToUpperInvariant()));
                StatusText = $"Чеки замовлень передано: {receipts.Length}.";
            }
            catch (Exception exception)
            {
                Log("history", exception);
                StatusText = "Чеки передано, але не вдалося зберегти історію їх друку.";
                foreach (var row in receipts) row.PrintError = StatusText;
            }
            _dialogs.ShowInfo(StatusText, "Друк чеків замовлень");
        }
        catch (OperationCanceledException) when (!transferStarted)
        { StatusText = "Підготовку чеків замовлень скасовано. Пакет не передано принтеру."; }
        catch (Exception exception)
        {
            Log("failed", exception);
            if (transferStarted && !submitted)
                foreach (var row in receipts) { row.PrintStatus = PrintItemStatus.Error; row.PrintError = exception.Message + " Частина могла надрукуватися; перевірте перед повтором."; }
            StatusText = submitted ? "Чеки передано принтеру." : transferStarted
                ? "Не вдалося підтвердити друк чеків. Частина могла надрукуватися; перевірте перед повтором."
                : "Не вдалося підготувати чеки замовлень. Пакет не передано принтеру.";
            _dialogs.ShowError(exception is InvalidOperationException ? exception.Message : StatusText, "Друк чеків замовлень");
        }
        finally
        {
            workspace.IsWorkspaceRefreshing = workspaceWasRefreshing;
            if (Waybills is not null) Waybills.IsWorkspaceRefreshing = waybillsWereRefreshing;
            ProgressText = "";
            IsPrinting = false;
            IsBusy = false;
            OnPropertyChanged(nameof(FailedCount));
            RaiseCommands();
        }
    }
}

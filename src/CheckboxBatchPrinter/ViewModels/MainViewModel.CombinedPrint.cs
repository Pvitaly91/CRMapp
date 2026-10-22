using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MainViewModel
{
    public ICommand PrintOrdersWithReceiptsCommand { get; }
    private readonly HashSet<(string Account, string Receipt)> _combinedReceiptTransferRisks = [];
    private bool CanPrintOrdersWithReceipts => SelectedTabIndex == 1 && !IsBusy && Waybills?.IsPrintingBusy != true &&
        Marketplace is { IsWorkspaceRefreshing: false, Labels.IsBusy: false } &&
        Marketplace.Orders.Cast<MarketplaceOrderRowViewModel>().Any(row => row.IsSelectedForLabels);

    private sealed record CombinedOrder(MarketplaceOrderRowViewModel Row, MarketplaceOrder Model,
        ReceiptRowViewModel[] Receipts);

    public async Task PrintOrdersWithReceiptsAsync()
    {
        if (!CanPrintOrdersWithReceipts || Marketplace?.Labels is not { } labels) return;
        // The order grid, rather than either receipt grid, owns both immutable sequences.
        var orders = Marketplace.Orders.Cast<MarketplaceOrderRowViewModel>().Where(row => row.IsSelectedForLabels)
            .Select(row => new CombinedOrder(row, row.Model with
            { Shipments = row.Model.Shipments.Select(shipment => shipment with { }).ToArray() },
                row.LinkedReceipts.DistinctBy(receipt => receipt.Id, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(receipt => receipt.Model.DisplayDate).ThenBy(receipt => receipt.Model.Serial)
                    .ThenBy(receipt => receipt.Id, StringComparer.OrdinalIgnoreCase).ToArray())).ToArray();
        var hiddenCount = Marketplace.AllOrderRows.Count(row => row.IsSelectedForLabels && !orders.Any(order => order.Row == row));
        var receipts = orders.SelectMany(order => order.Receipts).ToArray();
        var items = orders.SelectMany(order => order.Receipts.Select(receipt =>
            new PrintBatchItem(0, receipt.Id, receipt.Serial, order.Model.Key.Marketplace.ToString(),
                order.Model.Number, ReceiptPrintHeader.TrackingFor(order.Model))))
            .Select((item, index) => item with { Position = index + 1 }).ToArray();
        ShippingLabelsViewModel.CombinedLabelPrintSession? session = null;
        var batchId = Guid.NewGuid().ToString("N");
        var labelPageCount = 0;
        void LogBatch(string stage, Exception? error = null, bool includeReceipts = false)
        {
            // Diagnostics must never change the result of an accepted printer job.
            try
            {
                var status = $"batch={batchId};orders={orders.Length};receipts={receipts.Length};label_pages={labelPageCount};hidden_orders={hiddenCount}";
                if (error is null) _logger.Info("combined.print." + stage, printStatus: status);
                else _logger.Error("combined.print." + stage, error, printStatus: status);
                if (includeReceipts)
                    foreach (var item in items)
                    {
                        var orderNumber = new string(item.OrderNumber.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').Take(80).ToArray());
                        _logger.Info("combined.print." + stage + ".receipt", item.ReceiptId,
                            printStatus: $"batch={batchId};position={item.Position};order={orderNumber};marketplace={item.Marketplace}");
                    }
            }
            catch (Exception) { /* A failed diagnostic write must not cause a duplicate-print retry. */ }
        }
        var waybillsWereReserved = Waybills?.IsWorkspaceRefreshing == true;
        IsBusy = true;
        IsPrinting = true;
        if (Waybills is not null) Waybills.IsWorkspaceRefreshing = true;
        try
        {
            LogBatch("selected", includeReceipts: true);
            foreach (var order in orders)
            {
                if (order.Model.IsCanceled)
                    throw new InvalidOperationException($"Замовлення №{order.Model.Number} скасоване. Зніміть його вибір для спільного друку.");
                if (order.Receipts.Length == 0)
                    throw new InvalidOperationException($"Замовлення №{order.Model.Number}: немає завантаженого пов’язаного чека. Перевірте прив’язку або оновіть дані.");
                if (order.Receipts.Any(receipt => receipt.Model.Status != "DONE"))
                    throw new InvalidOperationException($"Замовлення №{order.Model.Number}: пов’язаний чек ще не готовий до друку.");
            }
            if (receipts.Length > 250 || receipts.Select(row => row.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != receipts.Length)
                throw new InvalidOperationException("Перевірте вибір: до 250 чеків, кожний пов’язаний лише з одним вибраним замовленням.");
            session = labels.BeginCombinedPrint(orders.Select(order => order.Model).ToArray());
            var token = session.Token;
            var sourceSettings = await _settingsService.LoadAsync(token);
            var settings = FreezeReceiptPrintSettings(sourceSettings);
            var account = PrintAccountContext.Create(settings);
            if (string.IsNullOrWhiteSpace(settings.PrinterName) || !_printService.PrinterExists(settings.PrinterName))
                throw new InvalidOperationException("Оберіть доступний принтер чеків у Налаштуваннях → Друк.");
            if (!string.Equals(_loadedAccountContext, account, StringComparison.Ordinal))
                throw new InvalidOperationException("Касира Checkbox змінено. Натисніть «Оновити все» перед друком.");
            var documents = new PrintReceiptDocument[receipts.Length];
            async Task PrepareReceiptsAsync()
            {
                long bytes = 0;
                for (var index = 0; index < receipts.Length; index++)
                {
                    token.ThrowIfCancellationRequested();
                    ProgressText = $"Готую чек {index + 1} із {receipts.Length} і наклейки…";
                    var png = await _imageService.GetPngAsync(items[index].ReceiptId,
                        (int)Math.Round(settings.EffectivePaperWidthMm), token);
                    bytes += png.LongLength;
                    if (bytes > 80L * 1024 * 1024) throw new InvalidOperationException("Зображення чеків перевищують 80 МіБ. Виберіть менший пакет.");
                    documents[index] = new(png.ToArray(), items[index].ReceiptId, items[index].OrderNumber, items[index].TrackingText);
                }
                _printService.ValidateReceiptsForPrint(documents, settings);
            }
            // Both complete packets must be ready before the single confirmation or any transfer.
            await Task.WhenAll(session.PrepareAsync(), PrepareReceiptsAsync());
            token.ThrowIfCancellationRequested();
            labelPageCount = session.Batch.Pages.Count;
            LogBatch("prepared");
            var history = await _printHistoryStore.LoadAsync(account, token);
            var previouslyPrinted = receipts.Count(row => row.PrintStatus == PrintItemStatus.Done || history.ContainsKey(row.Id) ||
                _combinedReceiptTransferRisks.Contains((account, row.Id.ToUpperInvariant())));
            var confirmation = new CombinedPrintConfirmation(new(settings.PrinterName, hiddenCount, items), previouslyPrinted);
            if (!session.Confirm(confirmation))
            {
                LogBatch("declined");
                StatusText = "Спільний друк скасовано. Вибір замовлень збережено.";
                return;
            }
            token.ThrowIfCancellationRequested();
            ValidateCombinedReceiptLinks(orders);
            session.ValidateCurrent();
            if (!_printService.PrinterExists(settings.PrinterName))
                throw new InvalidOperationException("Принтер чеків більше недоступний. Підготуйте пакет заново.");

            var labelsSucceeded = false;
            var receiptsSucceeded = false;
            var receiptsSubmitted = 0;
            var warnings = new List<string>();
            async Task SubmitLabelsAsync()
            {
                LogBatch("labels.submit");
                try { await session.SubmitAsync(); labelsSucceeded = true; LogBatch("labels.accepted"); }
                catch (Exception exception)
                {
                    LogBatch("labels", exception);
                    warnings.Add("Наклейки: " + exception.Message);
                }
            }
            async Task SubmitReceiptsAsync()
            {
                LogBatch("receipts.submit");
                var submitted = await SubmitReceiptSequenceAsync(documents, receipts, settings, account, token);
                receiptsSubmitted = submitted.Submitted;
                receiptsSucceeded = submitted.AllSubmitted;
                warnings.AddRange(submitted.Warnings);
                if (receiptsSucceeded) LogBatch("receipts.accepted", includeReceipts: true);
                else LogBatch("receipts", submitted.Failure);
            }
            ProgressText = "Передаю наклейки й чеки принтерам…";
            if (string.Equals(session.Batch.Settings.PrinterName, settings.PrinterName, StringComparison.OrdinalIgnoreCase))
            {
                await SubmitLabelsAsync();
                await SubmitReceiptsAsync();
            }
            else await Task.WhenAll(SubmitLabelsAsync(), SubmitReceiptsAsync());
            if (labelsSucceeded && receiptsSucceeded)
                foreach (var order in orders) order.Row.IsSelectedForLabels = false;
            var result = $"Наклейки: {(labelsSucceeded ? "передано" : "помилка")}. Чеки передано: {receiptsSubmitted} із {receipts.Length}.";
            StatusText = result;
            if (warnings.Count > 0)
            {
                if (!labelsSucceeded || !receiptsSucceeded) StatusText += " Вибір незавершених замовлень збережено.";
                _dialogs.ShowError(result + "\n\n" + string.Join("\n", warnings), "Спільний друк");
            }
            else _dialogs.ShowInfo(result, "Друк наклейок і чеків завершено");
        }
        catch (OperationCanceledException)
        {
            LogBatch("canceled");
            StatusText = "Підготовку спільного друку скасовано. Пакети не передано принтерам.";
        }
        catch (Exception exception)
        {
            LogBatch("prepare", exception);
            StatusText = "Не вдалося підготувати спільний друк. Пакети не передано принтерам.";
            _dialogs.ShowError(exception is ShippingLabelException or InvalidOperationException ? exception.Message :
                "Не вдалося підготувати наклейки й чеки. Перевірте підключення та налаштування принтерів.", "Спільний друк");
        }
        finally
        {
            session?.Dispose();
            if (Waybills is not null) Waybills.IsWorkspaceRefreshing = waybillsWereReserved;
            ProgressText = "";
            IsPrinting = false;
            IsBusy = false;
            OnPropertyChanged(nameof(FailedCount));
            RaiseCommands();
        }
    }

    private void ValidateCombinedReceiptLinks(IReadOnlyList<CombinedOrder> orders)
    {
        foreach (var frozen in orders)
        {
            var current = Marketplace!.AllOrderRows.FirstOrDefault(row => row.Key == frozen.Model.Key);
            if (current is null || current.Model.IsCanceled ||
                !current.LinkedReceipts.Select(row => row.Id).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(frozen.Receipts.Select(row => row.Id)) ||
                current.LinkedReceipts.Any(row => row.Model.Status != "DONE"))
                throw new InvalidOperationException($"Замовлення №{frozen.Model.Number}: прив’язка або стан чека змінилися. Підготуйте пакет заново.");
        }
    }

    private static AppSettings FreezeReceiptPrintSettings(AppSettings source) => new()
    {
        ApiBaseUrl = source.ApiBaseUrl, Login = source.Login, PrinterName = source.PrinterName,
        PaperWidth = source.PaperWidth, CustomPaperWidthMm = source.CustomPaperWidthMm,
        PrintableWidthMm = source.PrintableWidthMm, SeparatePrintJobPerReceipt = source.SeparatePrintJobPerReceipt,
        CacheRetentionDays = source.CacheRetentionDays, HttpTimeoutSeconds = source.HttpTimeoutSeconds
    };
}

using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class ShippingLabelsViewModel
{
    // Keep the label operation reserved until the receipt leg also finishes. Refresh and standalone
    // label commands must not replace the frozen order/label snapshot between the two submissions.
    internal CombinedLabelPrintSession BeginCombinedPrint(IReadOnlyList<MarketplaceOrder> frozenOrders)
    {
        if (!CanOperate) throw new InvalidOperationException("Зачекайте завершення оновлення або поточного друку.");
        if (frozenOrders.Count is 0 or > 100 || frozenOrders.Select(o => o.Key).Distinct().Count() != frozenOrders.Count)
            throw new InvalidOperationException("Виберіть від 1 до 100 різних замовлень для друку наклейок і чеків.");
        if (frozenOrders.Any(o => o.IsCanceled))
            throw new InvalidOperationException("Скасовані замовлення не можна включати до спільного друку наклейок і чеків.");

        var orders = frozenOrders.Select(o => o with
        {
            Shipments = Array.AsReadOnly(o.Shipments.Select(s => s with { }).ToArray())
        }).ToArray();
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        _printOperationCompletion = completion;
        _cancel = cancel;
        IsBusy = true;
        return new(this, orders, cancel, completion);
    }

    internal sealed class CombinedLabelPrintSession : IDisposable
    {
        private readonly ShippingLabelsViewModel _owner;
        private readonly IReadOnlyList<MarketplaceOrder> _orders;
        private readonly CancellationTokenSource _cancel;
        private readonly TaskCompletionSource _completion;
        private LabelPrintSettings? _settings;
        private LabelPrintBatch? _batch;
        private LabelPrinterGeometry? _geometry;
        private IReadOnlyList<LabelPrintAttempt> _history = [];
        private IReadOnlyList<LabelPrintAttempt> _previous = [];
        private bool _prepareStarted, _confirmed, _submitStarted, _disposed;

        internal CombinedLabelPrintSession(ShippingLabelsViewModel owner, IReadOnlyList<MarketplaceOrder> orders,
            CancellationTokenSource cancel, TaskCompletionSource completion)
        { _owner = owner; _orders = orders; _cancel = cancel; _completion = completion; }

        internal CancellationToken Token => _cancel.Token;
        internal LabelPrintBatch Batch => _batch ?? throw new InvalidOperationException("Пакет наклейок ще не підготовлено.");
        internal LabelPrinterGeometry Geometry => _geometry ?? throw new InvalidOperationException("Параметри принтера наклейок ще не перевірено.");
        internal IReadOnlyList<LabelPrintAttempt> Previous => _previous;

        internal async Task PrepareAsync()
        {
            EnsureActive();
            if (_prepareStarted) throw new InvalidOperationException("Пакет наклейок уже готується або підготовлений.");
            _prepareStarted = true;
            _owner.Status = "Готую наклейки та чеки у порядку вибраних замовлень…";
            _settings = (await _owner._settings.LoadAsync(Token)).Print with { };
            var geometry = _owner._printer.Inspect(_settings);
            var batch = await _owner._preparation.PrepareAsync(_orders, _settings, geometry, Token);
            foreach (var row in CurrentRows())
            {
                var omitted = batch.SkippedShipments.Where(s => s.Shipment.Order == row.Key).ToArray();
                row.LabelStatus = omitted.Length == 0 ? "Готова до перегляду" :
                    "Пропущено без ключа: " + string.Join("; ", omitted.Select(s => $"ТТН {s.Shipment.TrackingNumber}: {s.Reason}"));
            }
            if (batch.SkippedShipments.Count > 0)
                throw new InvalidOperationException("Спільний друк не розпочато: для частини наклейок немає ключа доставки. " +
                    string.Join("; ", batch.SkippedShipments.Select(s => $"Замовлення №{s.Shipment.OrderNumber} · ТТН {s.Shipment.TrackingNumber}: {s.Reason}")));
            if (batch.Pages.Count == 0 || batch.SelectedOrderCount != _orders.Count || _orders.Any(o =>
                !batch.Pages.Any(p => p.Shipment.Order == o.Key || p.CoveredShipments.Any(s => s.Order == o.Key))))
                throw new InvalidOperationException("Спільний друк не розпочато: кожне вибране замовлення повинно мати готову наклейку.");

            _history = (await _owner._history.LoadAsync(Token)).ToArray();
            foreach (var row in CurrentRows()) row.ApplyLabelHistory(_history, _owner._workspace.NormalizeOrderKey);
            _previous = _history.Where(a => a.State != LabelSubmissionState.NotSubmitted && a.Pages.Any(old =>
                batch.Pages.Any(p => p.CoveredShipments.Append(p.Shipment).Any(s =>
                    old.Matches(s, _owner._workspace.NormalizeOrderKey) && old.PageNumber == p.PageNumber)))).ToArray();
            Token.ThrowIfCancellationRequested();
            _batch = AddCabinetPrintEvidence(batch, CurrentRows());
            _geometry = geometry;
        }

        internal bool Confirm(CombinedPrintConfirmation confirmation)
        {
            EnsureActive();
            if (_confirmed || _submitStarted) throw new InvalidOperationException("Пакет уже підтверджено.");
            _confirmed = _owner._dialogs.PreviewCombined(Batch, Geometry, Previous, confirmation);
            Token.ThrowIfCancellationRequested();
            if (!_confirmed) _owner.Status = "Перегляд завершено; принтери не викликалися.";
            return _confirmed;
        }

        internal void ValidateCurrent()
        {
            EnsureActive();
            _ = Batch;
            ValidateCabinetPrintDisclosure(Batch, CurrentRows());
            foreach (var frozen in _orders)
            {
                var current = _owner._workspace.AllOrderRows.FirstOrDefault(r => r.Key == frozen.Key)?.Model;
                if (current is null || current.IsCanceled ||
                    !ShippingCarrierNames.ForOrder(current).SequenceEqual(ShippingCarrierNames.ForOrder(frozen)))
                    throw new InvalidOperationException("ТТН замінено, замовлення скасовано або видалено. Підготуйте й підтвердьте актуальний пакет заново.");
            }
            if (_owner._printer.Inspect(_settings!) != Geometry)
                throw new InvalidOperationException("Параметри драйвера наклейок змінилися. Підготуйте пакет заново.");
        }

        internal async Task<LabelPrintAttempt> SubmitAsync()
        {
            EnsureActive();
            if (!_confirmed || _submitStarted) throw new InvalidOperationException("Підтвердьте підготовлений пакет наклейок і чеків перед друком.");
            ValidateCurrent();
            _submitStarted = true;
            try
            {
                var result = await new LabelPrintCoordinator(_owner._history, _owner._printer).SubmitAsync(Batch, Token);
                foreach (var row in CurrentRows()) row.ApplyLabelHistory(_history.Append(result), _owner._workspace.NormalizeOrderKey);
                _owner.Status = "Пакет наклейок передано Windows у порядку вибраних замовлень.";
                return result;
            }
            catch (LabelTransmissionException ex)
            {
                foreach (var row in CurrentRows())
                {
                    row.ApplyLabelHistory(_history.Append(ex.Attempt), _owner._workspace.NormalizeOrderKey);
                    row.LabelPrintStatus = ex.Attempt.State == LabelSubmissionState.NotSubmitted
                        ? "Не передано" : "Можливий дубль: передавання почалося";
                }
                _owner.Status = ex.Message;
                throw;
            }
        }

        private IEnumerable<MarketplaceOrderRowViewModel> CurrentRows()
        {
            var keys = _orders.Select(o => o.Key).ToHashSet();
            return _owner._workspace.AllOrderRows.Where(r => keys.Contains(r.Key));
        }

        private void EnsureActive()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CombinedLabelPrintSession));
            Token.ThrowIfCancellationRequested();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _cancel.Cancel(); }
            finally
            {
                _cancel.Dispose();
                if (ReferenceEquals(_owner._cancel, _cancel)) _owner._cancel = null;
                _owner.IsBusy = false;
                _completion.TrySetResult();
                if (ReferenceEquals(_owner._printOperationCompletion, _completion)) _owner._printOperationCompletion = null;
            }
        }
    }
}

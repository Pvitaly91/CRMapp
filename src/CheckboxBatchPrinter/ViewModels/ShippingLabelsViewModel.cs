using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public interface ILabelDialogs
{
    bool Preview(LabelPrintBatch batch, LabelPrinterGeometry geometry, IReadOnlyList<LabelPrintAttempt> previous, bool allowPrint);
    Task SettingsAsync();
    void Error(string message);
    MarketplaceOrder? ChoosePreviewShipment(MarketplaceOrder order) => order;
}
public sealed class ShippingLabelsViewModel : ObservableObject
{
    private readonly MarketplaceWorkspaceViewModel _workspace;
    private readonly IShippingSettingsStore _settings;
    private readonly ILabelHistoryStore _history;
    private readonly LabelBatchPreparation _preparation;
    private readonly ILabelPrinter _printer;
    private readonly ILabelDialogs _dialogs;
    private bool _busy;
    private string _status = "";
    private CancellationTokenSource? _cancel;
    public ShippingLabelsViewModel(MarketplaceWorkspaceViewModel workspace, IShippingSettingsStore settings,
        ILabelHistoryStore history, IShippingLabelSource source, IShippingLabelRenderer renderer,
        ILabelPrinter printer, ILabelDialogs dialogs)
    {
        _workspace = workspace; _settings = settings; _history = history; _preparation = new(source, renderer); _printer = printer; _dialogs = dialogs;
        SelectVisibleCommand = new RelayCommand(_ => { foreach (var row in Visible()) row.IsSelectedForLabels = row.Model.Shipments.Any(s => s.TrackingNumber.Length > 0); }, _ => !IsBusy);
        ClearCommand = new RelayCommand(_ => { foreach (var row in _workspace.AllOrderRows) row.IsSelectedForLabels = false; }, _ => !IsBusy);
        PrintCommand = new AsyncRelayCommand(_ => RunAsync(null, true), _ => !IsBusy);
        PreviewCommand = new AsyncRelayCommand(p => RunAsync(p as MarketplaceOrderRowViewModel, false), _ => !IsBusy);
        PreviewShipmentCommand = new AsyncRelayCommand(p => p is OrderShipmentLink link
            ? RunAsync(link.Row, false, link.Shipment) : Task.CompletedTask, _ => !IsBusy);
        SettingsCommand = new AsyncRelayCommand(async _ =>
        {
            try { await _dialogs.SettingsAsync(); }
            catch (Exception) { _dialogs.Error("Не вдалося відкрити налаштування наклейок. Перевірте доступність принтера та локальних даних."); }
        }, _ => !IsBusy);
        CancelCommand = new RelayCommand(_ => _cancel?.Cancel(), _ => IsBusy);
    }
    public bool IsBusy { get => _busy; private set { SetProperty(ref _busy, value); foreach (var c in new[] { SelectVisibleCommand, ClearCommand, CancelCommand }) ((RelayCommand)c).RaiseCanExecuteChanged(); foreach (var c in new[] { PrintCommand, PreviewCommand, PreviewShipmentCommand, SettingsCommand }) ((AsyncRelayCommand)c).RaiseCanExecuteChanged(); } }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public ICommand SelectVisibleCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand PrintCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand PreviewShipmentCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand CancelCommand { get; }
    private MarketplaceOrderRowViewModel[] Visible() => _workspace.Orders.Cast<MarketplaceOrderRowViewModel>().ToArray();
    public async Task RestoreHistoryAsync()
    {
        try
        {
            var history = await _history.LoadAsync();
            if (IsBusy) return;
            foreach (var row in _workspace.AllOrderRows)
                row.ApplyLabelHistory(history);
        }
        catch (Exception) { Status = "Не вдалося прочитати історію наклейок; перед друком вона перевірятиметься знову."; }
    }
    public Task RunAsync(MarketplaceOrderRowViewModel? previewRow, bool print) => RunAsync(previewRow, print, null);

    private async Task RunAsync(MarketplaceOrderRowViewModel? previewRow, bool print, OrderShipment? selectedShipment)
    {
        if (IsBusy) return;
        var rows = previewRow is null ? Visible().Where(r => r.IsSelectedForLabels).ToArray() : new[] { previewRow };
        if (rows.Length == 0) { _dialogs.Error("Позначте видимі замовлення для наклейок (окремі галочки ліворуч)."); return; }
        // MarketplaceOrder is immutable; clone shipment list before any await. Live view/filter changes do not touch this snapshot.
        var orders = rows.Select(r => r.Model with { Shipments = r.Model.Shipments.Select(s => s with { }).ToArray() }).ToArray();
        if (previewRow is not null && !print)
        {
            if (selectedShipment is not null)
            {
                if (!orders[0].Shipments.Contains(selectedShipment))
                { _dialogs.Error("ТТН замінено. Виберіть її актуальний номер у таблиці."); return; }
                orders = [orders[0] with { Shipments = [selectedShipment with { }] }];
            }
            else
            {
                var chosen = _dialogs.ChoosePreviewShipment(orders[0]);
                if (chosen is null) return;
                orders = [chosen];
            }
        }
        IsBusy = true;
        _cancel = new();
        _cancel.CancelAfter(TimeSpan.FromMinutes(5));
        var printableOrders = new HashSet<OrderKey>();
        try
        {
            Status = "Готую та перевіряю етикетки…";
            var settings = (await _settings.LoadAsync(_cancel.Token)).Print with { };
            var geometry = _printer.Inspect(settings);
            var batch = await _preparation.PrepareAsync(orders, settings, geometry, _cancel.Token);
            printableOrders.UnionWith(batch.SkippedShipments.Count == 0 ? orders.Select(o => o.Key) : batch.Pages.Select(p => p.Shipment.Order));
            foreach (var row in rows)
            {
                var omitted = batch.SkippedShipments.Where(s => s.Shipment.Order == row.Key).ToArray();
                row.LabelStatus = omitted.Length == 0 ? "Готова до перегляду" :
                    (printableOrders.Contains(row.Key) ? "Частина наклейок готова; " : "") +
                    "Пропущено без ключа: " + string.Join("; ", omitted.Select(s => $"ТТН {s.Shipment.TrackingNumber}: {s.Reason}"));
            }
            if (batch.Pages.Count == 0)
            {
                Status = $"Немає доступних наклейок для друку. Пропущено без ключа: {batch.SkippedShipments.Count} відправлень.";
                return;
            }
            var history = await _history.LoadAsync(_cancel.Token);
            foreach (var row in rows) row.ApplyLabelHistory(history);
            var previous = history.Where(a => a.State != LabelSubmissionState.NotSubmitted && a.Pages.Any(old => batch.Pages.Any(p =>
                old.Carrier == p.Shipment.Carrier && old.TrackingNumber == p.Shipment.TrackingNumber && old.PageNumber == p.PageNumber))).ToArray();
            _cancel.Token.ThrowIfCancellationRequested();
            if (!_dialogs.Preview(batch, geometry, previous, print) || !print) { Status = "Перегляд завершено; принтер не викликався."; return; }
            _cancel.Token.ThrowIfCancellationRequested();
            foreach (var frozen in orders.Where(o => printableOrders.Contains(o.Key)))
            {
                var current = _workspace.AllOrderRows.FirstOrDefault(r => r.Key == frozen.Key)?.Model;
                if (current is null || !ShippingCarrierNames.ForOrder(current).SequenceEqual(ShippingCarrierNames.ForOrder(frozen)))
                    throw new InvalidOperationException("ТТН замінено або замовлення оновлено. Підготуйте й підтвердьте актуальний пакет заново.");
            }
            var currentGeometry = _printer.Inspect(settings);
            if (currentGeometry != geometry) throw new InvalidOperationException("Параметри драйвера змінилися. Підготуйте пакет заново.");
            var result = await new LabelPrintCoordinator(_history, _printer).SubmitAsync(batch, _cancel.Token);
            var omittedOrders = batch.SkippedShipments.Select(s => s.Shipment.Order).ToHashSet();
            foreach (var row in rows)
            {
                row.ApplyLabelHistory(history.Append(result));
                if (printableOrders.Contains(row.Key) && !omittedOrders.Contains(row.Key)) row.IsSelectedForLabels = false;
            }
            Status = "Пакет передано Windows. " + (batch.SkippedShipments.Count > 0
                ? $"Пропущено без ключа: {batch.SkippedShipments.Count} відправлень. " : "") +
                "Це не підтвердження фізичного виходу наклейок.";
        }
        catch (OperationCanceledException) { Status = "Скасовано. Нового друку не запущено."; }
        catch (LabelTransmissionException ex)
        {
            foreach (var row in rows.Where(r => printableOrders.Contains(r.Key))) row.LabelPrintStatus = ex.Attempt.State == LabelSubmissionState.NotSubmitted ? "Не передано" : "Можливий дубль: передавання почалося";
            Status = ex.Message; _dialogs.Error(ex.Message);
        }
        catch (Exception ex) { Status = "Пакет не передано; перевірте етикетки / підключення."; _dialogs.Error(ex is ShippingLabelException or InvalidOperationException ? ex.Message : "Не вдалося підготувати етикетки. Перевірте локальні налаштування."); }
        finally { _cancel.Dispose(); _cancel = null; IsBusy = false; }
    }
}

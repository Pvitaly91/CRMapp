using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

public sealed class ReceiptRowViewModel : ObservableObject
{
    private bool _isSelected;
    private PrintItemStatus _printStatus = PrintItemStatus.Waiting;
    private string _printError = string.Empty;
    private int? _windowsJobId;
    private bool _hasSubmissionRisk;

    public ReceiptRowViewModel(ReceiptRecord model) => Model = model;

    public event EventHandler? SelectionChanged;
    public ReceiptRecord Model { get; }
    public string Id => Model.Id;
    public string RawType => Model.Type;
    public string Type => ReceiptTypes.ToUkrainian(Model.Type);
    public string Status => ReceiptStatuses.ToUkrainian(Model.Status);
    public DateTime? LocalDate => Model.DisplayDate?.LocalDateTime.Date;
    public string LocalTime => Model.DisplayDate?.LocalDateTime.ToString("HH:mm:ss") ?? "—";
    public string FiscalCode => string.IsNullOrWhiteSpace(Model.FiscalCode) ? "—" : Model.FiscalCode;
    public string Serial => Model.Serial == 0 ? "—" : Model.Serial.ToString();
    public decimal Total => Model.TotalSum;
    public string Payment => Model.PaymentDisplay;
    public string CashRegister => string.IsNullOrWhiteSpace(Model.CashRegisterFiscalNumber) ? "—" : Model.CashRegisterFiscalNumber;
    public int? WindowsJobId
    {
        get => _windowsJobId;
        set
        {
            if (!SetProperty(ref _windowsJobId, value)) return;
            OnPropertyChanged(nameof(WindowsJobDisplay));
            if (value.HasValue) HasSubmissionRisk = true;
            OnPropertyChanged(nameof(CanRetryWithoutWarning));
        }
    }
    public bool HasSubmissionRisk
    {
        get => _hasSubmissionRisk;
        set
        {
            if (!SetProperty(ref _hasSubmissionRisk, value)) return;
            OnPropertyChanged(nameof(CanRetryWithoutWarning));
        }
    }
    public bool CanRetryWithoutWarning => PrintRetryPolicy.CanRetryWithoutWarning(PrintStatus, HasSubmissionRisk);
    public string WindowsJobDisplay => WindowsJobId is { } id ? id.ToString() : "—";

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetProperty(ref _isSelected, value)) return;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public PrintItemStatus PrintStatus
    {
        get => _printStatus;
        set
        {
            if (!SetProperty(ref _printStatus, value)) return;
            OnPropertyChanged(nameof(PrintStatusText));
            OnPropertyChanged(nameof(CanRetryWithoutWarning));
        }
    }

    public string PrintError
    {
        get => _printError;
        set => SetProperty(ref _printError, value);
    }

    public string PrintStatusText => PrintStatus switch
    {
        PrintItemStatus.Waiting => "Очікує",
        PrintItemStatus.Downloading => "Завантаження",
        PrintItemStatus.Preparing => "Підготовка",
        PrintItemStatus.SubmittedToWindowsQueue => "Передано Windows",
        PrintItemStatus.Paused => "Призупинено",
        PrintItemStatus.ResultNotConfirmed => "Результат не підтверджено",
        PrintItemStatus.Error => "Помилка",
        PrintItemStatus.Cancelled => "Зупинено",
        _ => "—"
    };
}

public sealed record ReceiptTypeOption(string Value, string Display)
{
    public override string ToString() => Display;
}

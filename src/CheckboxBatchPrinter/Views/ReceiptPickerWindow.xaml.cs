using System.ComponentModel;
using System.Windows;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Views;

public partial class ReceiptPickerWindow : Window
{
    private readonly ReceiptPickerViewModel _viewModel;

    public ReceiptPickerWindow(ReceiptPickerViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        var availableWidth = Math.Max(1, SystemParameters.WorkArea.Width - 32);
        var availableHeight = Math.Max(1, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(MinWidth, availableWidth);
        MinHeight = Math.Min(MinHeight, availableHeight);
        Width = Math.Min(Width, availableWidth);
        Height = Math.Min(Height, availableHeight);
        viewModel.ChoiceRequested += HandleChoice;
        Closed += (_, _) => viewModel.ChoiceRequested -= HandleChoice;
    }

    public string? ReceiptId { get; private set; }

    private void HandleChoice(string receiptId)
    {
        ReceiptId = receiptId;
        DialogResult = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsPreviewBusy) e.Cancel = true;
        base.OnClosing(e);
    }
}

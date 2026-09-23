using System.Windows;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Views;

public partial class OrderLinkWindow : Window
{
    public OrderLinkWindow(OrderLinkViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height - 32);
        viewModel.ChoiceRequested += HandleChoice;
        Closed += (_, _) => viewModel.ChoiceRequested -= HandleChoice;
    }

    public OrderLinkChoice? Choice { get; private set; }

    private void HandleChoice(OrderLinkChoice choice)
    {
        Choice = choice;
        DialogResult = true;
    }
}

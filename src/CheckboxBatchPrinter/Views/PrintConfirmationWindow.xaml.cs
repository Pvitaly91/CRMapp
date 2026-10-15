using System.Windows;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Views;

public partial class PrintConfirmationWindow : Window
{
    public PrintConfirmationWindow(PrintBatchConfirmation batch)
    {
        InitializeComponent();
        DataContext = batch;
        ConfirmButton.IsEnabled = !batch.HasPreviouslyPrinted;
    }

    private void RepeatAcknowledgement_Changed(object sender, RoutedEventArgs e)
    {
        if (ConfirmButton is not null && DataContext is PrintBatchConfirmation batch)
            ConfirmButton.IsEnabled = !batch.HasPreviouslyPrinted || RepeatAcknowledgement.IsChecked == true;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is PrintBatchConfirmation batch && (!batch.HasPreviouslyPrinted || RepeatAcknowledgement.IsChecked == true)) DialogResult = true;
    }
}

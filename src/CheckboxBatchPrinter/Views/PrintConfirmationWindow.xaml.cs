using System.Windows;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Views;

public partial class PrintConfirmationWindow : Window
{
    public PrintConfirmationWindow(PrintBatchConfirmation batch)
    {
        InitializeComponent();
        DataContext = batch;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

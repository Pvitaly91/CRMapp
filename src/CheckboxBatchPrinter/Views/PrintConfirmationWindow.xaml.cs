using System.Windows;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Views;

public partial class PrintConfirmationWindow : Window
{
    public PrintConfirmationWindow(PrintBatchConfirmation confirmation)
    {
        InitializeComponent();
        SummaryText.Text = $"Буде передано: {confirmation.VisibleCount} чеків\nПринтер: {confirmation.PrinterName}";
        HiddenText.Text = confirmation.HiddenExcludedCount > 0
            ? $"Приховані вибрані чеки не входять до пакета: {confirmation.HiddenExcludedCount}."
            : "Прихованих вибраних чеків немає.";
        ReceiptList.ItemsSource = confirmation.OrderedReceiptLabels.Select((label, index) => $"{index + 1}. {label}");
        RepeatWarning.Visibility = confirmation.ContainsPreviouslySubmittedReceipts
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}

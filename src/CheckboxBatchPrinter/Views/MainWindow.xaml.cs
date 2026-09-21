using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Views;

public partial class MainWindow : Window
{
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || DataContext is not MainViewModel viewModel) return;
        _initialized = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        await viewModel.InitializeAsync();
    }

    private void ReceiptsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel viewModel && ReceiptsGrid.SelectedItem is ReceiptRowViewModel row && viewModel.PreviewCommand.CanExecute(row))
            viewModel.PreviewCommand.Execute(row);
    }

    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // WPF performs stable collection-view sorting. The handler exists to make the intent explicit.
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        var command = e.Key switch
        {
            Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) => viewModel.SelectAllCommand,
            Key.P when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) => viewModel.PrintSelectedCommand,
            Key.F5 => viewModel.RefreshCommand,
            _ => null
        };
        if (command?.CanExecute(null) != true) return;
        command.Execute(null);
        e.Handled = true;
    }
}

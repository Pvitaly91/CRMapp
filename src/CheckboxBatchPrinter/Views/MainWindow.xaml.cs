using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Data;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Views;

public partial class MainWindow : Window
{
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        BindHeaderToggle(AllReceiptsHeaderToggle, "AllReceiptsTab");
        BindHeaderToggle(OrdersReceiptsHeaderToggle, "OrdersReceiptsTab");
        BindHeaderToggle(OrdersHeaderToggle, "Marketplace.Labels");
        BindHeaderToggle(WaybillsHeaderToggle, "Waybills");
        Title = Infrastructure.AppEnvironment.WindowTitle;
        Loaded += OnLoaded;
    }

    private void BindHeaderToggle(CheckBox toggle, string ownerPath)
    {
        // Column Header objects can be disconnected from their presenter. Use
        // the owning window directly so recycled/hidden tabs retain their scope.
        var path = "DataContext." + ownerPath + ".";
        toggle.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
            new Binding(path + "HeaderSelectionState") { Source = this, Mode = BindingMode.OneWay });
        toggle.SetBinding(IsEnabledProperty,
            new Binding(path + "CanToggleHeaderSelection") { Source = this, Mode = BindingMode.OneWay, FallbackValue = false, TargetNullValue = false });
        toggle.SetBinding(System.Windows.Controls.Primitives.ButtonBase.CommandProperty,
            new Binding(path + "ToggleHeaderSelectionCommand") { Source = this, Mode = BindingMode.OneWay });
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || DataContext is not MainViewModel viewModel) return;
        _initialized = true;
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        if (!viewModel.CoordinatorManaged) await viewModel.InitializeAsync();
    }

    private void ReceiptsGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<System.Windows.Controls.Primitives.TextBoxBase>(e.OriginalSource as DependencyObject) is not null) return;
        if (DataContext is MainViewModel viewModel && sender is DataGrid { SelectedItem: ReceiptRowViewModel row }
            && viewModel.PreviewCommand.CanExecute(row))
            viewModel.PreviewCommand.Execute(row);
    }

    private void DataGrid_Sorting(object sender, DataGridSortingEventArgs e)
    {
        // WPF performs stable collection-view sorting. The handler exists to make the intent explicit.
    }

    private void OrderLinkInfo_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ToolTip: ToolTip tip } button)
        {
            tip.PlacementTarget = button;
            tip.IsOpen = !tip.IsOpen;
        }
    }

    private void OrderLinkInfo_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is Button { ToolTip: ToolTip tip }) tip.IsOpen = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel viewModel) return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.A or Key.C &&
            Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) return;
        var focusedGrid = Ancestor<DataGrid>(Keyboard.FocusedElement as DependencyObject);
        if (focusedGrid is not null && Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.A or Key.C)
        {
            if (e.Key == Key.A) focusedGrid.SelectAllCells();
            else if (BuildDashboardClipboardText(focusedGrid) is { Length: > 0 } text) Clipboard.SetText(text);
            e.Handled = true; return;
        }
        var command = e.Key switch
        {
            Key.A when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) => viewModel.SelectAllCommand,
            Key.P when Keyboard.Modifiers.HasFlag(ModifierKeys.Control) => viewModel.PrintSelectedCommand,
            Key.F5 => viewModel.RefreshAllCommand,
            _ => null
        };
        if (command?.CanExecute(null) != true) return;
        command.Execute(null);
        e.Handled = true;
    }

    private static T? Ancestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T match) return match;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
    private void Dashboard_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not DataGrid grid || grid.CommandBindings.Cast<CommandBinding>().Any(b => b.Command == ApplicationCommands.Copy)) return;
        grid.CommandBindings.Add(new(ApplicationCommands.Copy, DashboardCopy_Executed,
            (s, args) =>
            {
                if (Ancestor<System.Windows.Controls.Primitives.TextBoxBase>(args.OriginalSource as DependencyObject) is not null) return;
                args.CanExecute = grid.SelectedCells.Count > 0; args.Handled = true;
            }));
    }
    private void DashboardCopy_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (Ancestor<System.Windows.Controls.Primitives.TextBoxBase>(e.OriginalSource as DependencyObject) is not null) return;
        if (sender is DataGrid grid && BuildDashboardClipboardText(grid) is { Length: > 0 } text) Clipboard.SetText(text);
        e.Handled = true;
    }
    internal static string BuildDashboardClipboardText(DataGrid grid)
    {
        var cells = grid.SelectedCells.Where(c => c.IsValid && c.Column.Visibility == Visibility.Visible).ToArray();
        var columns = cells.Select(c => c.Column).Distinct().OrderBy(c => c.DisplayIndex).ToArray();
        var items = cells.Select(c => c.Item).Distinct().OrderBy(item => grid.Items.IndexOf(item));
        return string.Join(Environment.NewLine, items.Select(item => string.Join("\t", columns.Select(column =>
            cells.Any(c => ReferenceEquals(c.Item, item) && c.Column == column)
                ? ClipboardCellText(column, item) : ""))));
    }
    private static string ClipboardCellText(DataGridColumn column, object item)
    {
        var binding = column.ClipboardContentBinding ?? (column as DataGridBoundColumn)?.Binding;
        if (binding is null) return "";
        var value = new TextBlock { DataContext = item };
        BindingOperations.SetBinding(value, TextBlock.TextProperty, binding);
        var text = value.Text;
        BindingOperations.ClearAllBindings(value);
        return text;
    }
    private void Dashboard_CurrentCellChanged(object sender, EventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not DataGrid grid) return;
        if (grid.CurrentCell.Item is MarketplaceOrderRowViewModel order && vm.Marketplace is { } workspace) workspace.SelectedOrder = order;
        else if (grid.CurrentCell.Item is ReceiptRowViewModel receipt)
        {
            if (ReferenceEquals(grid, AllReceiptsGrid)) vm.AllReceiptsTab.SelectedReceipt = receipt;
            else if (ReferenceEquals(grid, OrdersReceiptsGrid)) vm.OrdersReceiptsTab.SelectedReceipt = receipt;
        }
        else if (grid.CurrentCell.Item is WaybillRowViewModel waybill && vm.Waybills is { } waybills)
            waybills.SelectedRow = waybill;
    }
    private void Dashboard_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (Ancestor<System.Windows.Controls.Primitives.TextBoxBase>(e.OriginalSource as DependencyObject) is not null) return;
        if (sender is not DataGrid grid || Ancestor<DataGridCell>(e.OriginalSource as DependencyObject) is not { } cell) return;
        if (!cell.IsSelected)
        {
            grid.SelectedCells.Clear(); cell.IsSelected = true;
            grid.CurrentCell = new DataGridCellInfo(cell);
        }
    }
}

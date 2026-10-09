using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace CheckboxBatchPrinter.Controls;

/// <summary>Read-only dashboard text with native character selection and optional click preview.</summary>
public class DashboardTextBox : TextBox
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(DashboardTextBox),
        new PropertyMetadata(null, ClickCommandChanged));
    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter), typeof(object), typeof(DashboardTextBox),
        new PropertyMetadata(null, ClickCommandChanged));

    private DispatcherTimer? _previewTimer;
    private bool _singleClick;
    private bool _selectedDuringPress;
    private Point _pressPosition;
    private object? _pressContext;
    private ICommand? _pressCommand;
    private object? _pressParameter;

    static DashboardTextBox()
    {
        IsReadOnlyProperty.OverrideMetadata(typeof(DashboardTextBox),
            new FrameworkPropertyMetadata(true, null, (_, _) => true));
    }

    public DashboardTextBox()
    {
        IsReadOnlyCaretVisible = true;
        IsUndoEnabled = false;
        BorderThickness = new Thickness(0);
        Background = Brushes.Transparent;
        Padding = new Thickness(0);
        Margin = new Thickness(2, 0, 2, 0);
        VerticalAlignment = VerticalAlignment.Center;
        VerticalContentAlignment = VerticalAlignment.Center;
        Cursor = Cursors.IBeam;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden;
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        SetResourceReference(StyleProperty, "DashboardTextStyle");
        CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, args) => { Copy(); args.Handled = true; },
            (_, args) => { args.CanExecute = IsEnabled && SelectionLength > 0; args.Handled = true; }));
        SelectionChanged += (_, _) =>
        {
            CancelClickPreview();
            if (_singleClick && SelectionLength > 0) _selectedDuringPress = true;
        };
        DataContextChanged += (_, _) => { _singleClick = false; CancelClickPreview(); };
        Unloaded += (_, _) => { _singleClick = false; CancelClickPreview(); };
    }

    public ICommand? Command { get => (ICommand?)GetValue(CommandProperty); set => SetValue(CommandProperty, value); }
    public object? CommandParameter { get => GetValue(CommandParameterProperty); set => SetValue(CommandParameterProperty, value); }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        SynchronizeCurrentCell();
        base.OnGotKeyboardFocus(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _singleClick = false;
        CancelClickPreview();
        base.OnLostKeyboardFocus(e);
    }

    protected override void OnPreviewMouseRightButtonDown(MouseButtonEventArgs e)
    {
        _singleClick = false;
        CancelClickPreview();
        base.OnPreviewMouseRightButtonDown(e);
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        CancelClickPreview();
        SynchronizeCurrentCell(selectCell: true);
        _singleClick = e.ClickCount == 1;
        _selectedDuringPress = false;
        _pressPosition = e.GetPosition(this);
        _pressContext = DataContext;
        _pressCommand = Command;
        _pressParameter = CommandParameter;
        base.OnPreviewMouseLeftButtonDown(e);
    }

    protected override void OnPreviewMouseMove(MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            var delta = e.GetPosition(this) - _pressPosition;
            if (Math.Abs(delta.X) >= SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(delta.Y) >= SystemParameters.MinimumVerticalDragDistance)
                _selectedDuringPress = true;
            CancelClickPreview();
        }
        base.OnPreviewMouseMove(e);
    }

    protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonUp(e);
        var shouldPreview = _singleClick && !_selectedDuringPress && SelectionLength == 0 && Keyboard.Modifiers == ModifierKeys.None &&
            ReferenceEquals(_pressContext, DataContext) && ReferenceEquals(_pressCommand, Command) &&
            ReferenceEquals(_pressParameter, CommandParameter);
        _singleClick = false;
        if (shouldPreview) ScheduleClickPreview();
    }

    // Waiting for the double-click interval lets a second click select a word without opening its label.
    internal void ScheduleClickPreview()
    {
        CancelClickPreview();
        if (Command is not { } command || SelectionLength != 0 || !IsLoaded) return;
        var parameter = CommandParameter;
        var context = DataContext;
        var text = Text;
        var timer = new DispatcherTimer(DispatcherPriority.Input, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime)
        };
        _previewTimer = timer;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_previewTimer, timer)) return;
            _previewTimer = null;
            if (!IsLoaded || SelectionLength != 0 || !ReferenceEquals(context, DataContext) ||
                !ReferenceEquals(command, Command) || !ReferenceEquals(parameter, CommandParameter) || text != Text) return;
            if (command is RoutedCommand routed)
            {
                if (routed.CanExecute(parameter, this)) routed.Execute(parameter, this);
            }
            else if (command.CanExecute(parameter)) command.Execute(parameter);
        };
        timer.Start();
    }

    private static void ClickCommandChanged(DependencyObject control, DependencyPropertyChangedEventArgs e)
    {
        var text = (DashboardTextBox)control;
        text._singleClick = false;
        text.CancelClickPreview();
    }

    private void CancelClickPreview()
    {
        _previewTimer?.Stop();
        _previewTimer = null;
    }

    private void SynchronizeCurrentCell(bool selectCell = false)
    {
        var cell = FindAncestor<DataGridCell>(this);
        var grid = FindAncestor<DataGrid>(cell);
        if (cell is not null && grid is not null)
        {
            var current = new DataGridCellInfo(cell);
            if (!grid.CurrentCell.Equals(current)) grid.CurrentCell = current;
            // Finish plain-click cell selection before TextBox takes capture for character selection.
            // Existing ranges are retained; modifier clicks keep the grid's own selection behavior.
            if (selectCell && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0 && !cell.IsSelected)
                cell.IsSelected = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null)
        {
            if (element is T result) return result;
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }
        return null;
    }
}

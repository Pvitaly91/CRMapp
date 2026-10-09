using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CheckboxBatchPrinter.Controls;

namespace CheckboxBatchPrinter.Tests;

internal static class DashboardTextControlTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA dashboard text selects substrings and native copy remains read-only", () => Sta(SubstringSelection)),
        ("STA dashboard text columns preserve format, clipboard binding and readonly source", () => Sta(FormattedColumn)),
        ("STA bound dashboard text keeps mouse selection and substring copy ahead of grid commands", () => Sta(BoundGridSelection)),
        ("STA shipment text clicks preview once while selection cancels preview", () => Sta(ClickAndSelection)),
        ("STA recycled shipment text cancels stale pending previews", () => Sta(StalePreview))
    ];

    private static Task Sta(Action test)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { test(); } catch (Exception ex) { failure = ex; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) throw failure;
        return Task.CompletedTask;
    }

    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    private static void SubstringSelection()
    {
        var text = new DashboardTextBox { Text = "Замовлення 432472190 / 20400554151693" };
        text.IsReadOnly = false;
        Check(text.IsReadOnly && text.IsReadOnlyCaretVisible, "Dashboard text became editable or lost its caret.");
        text.Select(11, 9);
        Check(text.SelectedText == "432472190", "Selected substring includes other cell text.");
        Check(ApplicationCommands.Copy.CanExecute(null, text), "Native Copy is unavailable for selected read-only text.");
        string? copied = null;
        DataObject.AddCopyingHandler(text, (_, args) =>
        {
            copied = args.DataObject.GetData(DataFormats.UnicodeText) as string;
            args.CancelCommand(); // Inspect the real copy payload without replacing the user's clipboard.
        });
        ApplicationCommands.Copy.Execute(null, text);
        Check(copied == "432472190", "Copy command did not use the selected text fragment.");
        Check(!ApplicationCommands.Cut.CanExecute(null, text), "Read-only dashboard text permits cutting.");
        Check(text.BorderThickness == new Thickness(0) && ReferenceEquals(text.Background, Brushes.Transparent),
            "Text selection masks the dashboard row color.");
        Check(text.Text == "Замовлення 432472190 / 20400554151693", "Selection modified the displayed value.");
    }

    private static void FormattedColumn()
    {
        var model = new DisplayModel();
        var binding = new Binding(nameof(DisplayModel.Amount)) { StringFormat = "N2", ConverterCulture = CultureInfo.InvariantCulture };
        var column = new TestColumn { Binding = binding, ClipboardContentBinding = new Binding(nameof(DisplayModel.Tracking)), SortMemberPath = nameof(DisplayModel.Amount) };
        var text = column.Display(model);
        using var amountHost = Host(text);
        Check(text.Text == "1,234.50", $"Displayed amount formatting changed: '{text.Text}'.");
        Check(column.IsReadOnly && BindingOperations.GetBinding(text, TextBox.TextProperty)?.Mode == BindingMode.OneWay,
            "Dashboard display binding can write into its source.");
        Check(ReferenceEquals(column.Binding, binding) && column.SortMemberPath == nameof(DisplayModel.Amount) &&
            ((Binding)column.ClipboardContentBinding).Path.Path == nameof(DisplayModel.Tracking), "Column clipboard or sorting binding changed.");
        text.Text = "replacement";
        BindingOperations.GetBindingExpression(text, TextBox.TextProperty)?.UpdateSource();
        Check(model.Amount == 1234.5m && model.Writes == 0, "Readonly text wrote back into model.");

        var date = new TestColumn { Binding = new Binding(nameof(DisplayModel.Created)) { StringFormat = "dd.MM.yyyy HH:mm" } }.Display(model);
        using var dateHost = Host(date);
        Check(date.Text == "09.10.2026 15:18", "Date format no longer matches dashboard values.");
        var readOnly = new TestColumn { Binding = new Binding(nameof(DisplayModel.Tracking)) }.Display(model);
        using var trackingHost = Host(readOnly);
        Check(readOnly.Text == model.Tracking, "Readonly source property cannot bind to selection surface.");
    }

    private static void ClickAndSelection()
    {
        var command = new RecordingCommand(); var parameter = new object();
        var text = new DashboardTextBox { Text = "20400554151693", Command = command, CommandParameter = parameter };
        using var host = Host(text);
        text.ScheduleClickPreview(); PumpClickDelay();
        Check(command.Count == 1 && ReferenceEquals(command.LastParameter, parameter), "Click preview opened wrong shipment or ran twice.");
        text.ScheduleClickPreview(); text.Select(3, 5); PumpClickDelay();
        Check(command.Count == 1 && text.SelectedText == "00554", "Selecting a TTN substring opened its preview.");
        Check(ApplicationCommands.Copy.CanExecute(null, text), "Native Copy cannot target selected shipment text.");
    }

    private static void BoundGridSelection()
    {
        var model = new DisplayModel();
        var tracking = new DashboardTextColumn { Binding = new Binding(nameof(DisplayModel.Tracking)), Width = 180 };
        var amount = new DashboardTextColumn { Binding = new Binding(nameof(DisplayModel.Amount)), Width = 160 };
        var grid = new DataGrid
        {
            AutoGenerateColumns = false, CanUserAddRows = false, IsReadOnly = true,
            SelectionUnit = DataGridSelectionUnit.CellOrRowHeader, SelectionMode = DataGridSelectionMode.Extended,
            ItemsSource = new[] { model }
        };
        grid.Columns.Add(tracking); grid.Columns.Add(amount);
        grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy,
            (_, _) => throw new Exception("The grid copied the whole cell instead of the text fragment."),
            (_, args) => { args.CanExecute = true; args.Handled = true; }));
        using var host = Host(grid); grid.UpdateLayout();
        var text = VisualChildren<DashboardTextBox>(grid).Single(t => t.Text == model.Tracking);
        grid.SelectedCells.Add(new DataGridCellInfo(model, amount));
        var down = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent
        };
        text.RaiseEvent(down); // WPF routed event only; no OS input, mouse capture or user focus injection.
        Check(grid.CurrentCell.Item == model && grid.CurrentCell.Column == tracking,
            "Clicking dashboard text did not synchronize its current cell.");
        Check(grid.SelectedCells.Count == 2 && grid.SelectedCells.Contains(new DataGridCellInfo(model, tracking)),
            "Text mouse-down failed to preselect its cell while preserving the existing range.");
        text.Select(3, 5);
        Check(ApplicationCommands.Copy.CanExecute(null, text), "A parent grid blocked the text's Copy command.");
        string? copied = null;
        DataObject.AddCopyingHandler(text, (_, args) =>
        {
            copied = args.DataObject.GetData(DataFormats.UnicodeText) as string;
            args.CancelCommand();
        });
        ApplicationCommands.Copy.Execute(null, text);
        Check(copied == "00554" && model.Writes == 0, "Bound text did not copy exactly the selected substring.");
    }

    private static void StalePreview()
    {
        var command = new RecordingCommand();
        var text = new DashboardTextBox { Text = "old TTN", Command = command, CommandParameter = new object(), DataContext = new object() };
        using var host = Host(text);
        text.ScheduleClickPreview(); text.CommandParameter = new object(); PumpClickDelay();
        Check(command.Count == 0, "Recycled text opened an earlier shipment parameter.");
        text.ScheduleClickPreview(); text.DataContext = new object(); PumpClickDelay();
        Check(command.Count == 0, "Recycled text opened a stale row.");
        text.ScheduleClickPreview(); text.Text = "new TTN"; PumpClickDelay();
        Check(command.Count == 0, "Changed TTN opened a pending old preview.");
        text.ScheduleClickPreview(); host.RootVisual = null; PumpClickDelay();
        Check(command.Count == 0, "Unloaded shipment cell opened a preview.");
    }

    private static HwndSource Host(UIElement text)
    {
        // An invisible native surface establishes WPF Loaded state without touching user windows or focus.
        var host = new HwndSource(new HwndSourceParameters("Dashboard text control test") { Width = 400, Height = 80, WindowStyle = 0 });
        host.RootVisual = text;
        text.Measure(new Size(400, 80)); text.Arrange(new Rect(0, 0, 400, 80));
        Pump(TimeSpan.FromMilliseconds(30));
        Check((text as FrameworkElement)?.IsLoaded == true, "Synthetic dashboard text did not load.");
        return host;
    }

    private static void PumpClickDelay() => Pump(TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 80));
    private static IEnumerable<T> VisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T result) yield return result;
            foreach (var descendant in VisualChildren<T>(child)) yield return descendant;
        }
    }
    private static void Pump(TimeSpan delay)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = delay };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }

    private sealed class TestColumn : DashboardTextColumn
    {
        public DashboardTextBox Display(object model)
        {
            var text = (DashboardTextBox)GenerateElement(new DataGridCell(), model);
            text.DataContext = model;
            BindingOperations.GetBindingExpression(text, TextBox.TextProperty)?.UpdateTarget();
            return text;
        }
    }
    private sealed class DisplayModel
    {
        private decimal _amount = 1234.5m;
        public decimal Amount { get => _amount; set { _amount = value; Writes++; } }
        public int Writes { get; private set; }
        public DateTime Created => new(2026, 10, 9, 15, 18, 0);
        public string Tracking => "20400554151693";
    }
    private sealed class RecordingCommand : ICommand
    {
        public int Count { get; private set; }
        public object? LastParameter { get; private set; }
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) { Count++; LastParameter = parameter; }
    }
}

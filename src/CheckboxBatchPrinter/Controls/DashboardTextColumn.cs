using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace CheckboxBatchPrinter.Controls;

/// <summary>A bound dashboard column whose displayed value supports native text selection.</summary>
public class DashboardTextColumn : DataGridTextColumn
{
    public DashboardTextColumn() => IsReadOnly = true;

    protected override FrameworkElement GenerateElement(DataGridCell cell, object dataItem)
    {
        var text = new DashboardTextBox();
        if (ElementStyle is { } style && style.TargetType.IsAssignableFrom(typeof(DashboardTextBox))) text.Style = style;
        if (Binding is { } binding) BindingOperations.SetBinding(text, TextBox.TextProperty, DisplayBinding(binding));
        return text;
    }

    protected override FrameworkElement GenerateEditingElement(DataGridCell cell, object dataItem) => GenerateElement(cell, dataItem);

    private static BindingBase DisplayBinding(BindingBase original)
    {
        BindingBase result;
        if (original is Binding binding)
        {
            var clone = new Binding
            {
                Path = binding.Path, XPath = binding.XPath, Mode = BindingMode.OneWay,
                Converter = binding.Converter, ConverterParameter = binding.ConverterParameter,
                ConverterCulture = binding.ConverterCulture, IsAsync = binding.IsAsync,
                AsyncState = binding.AsyncState, BindsDirectlyToSource = binding.BindsDirectlyToSource
            };
            if (binding.RelativeSource is not null) clone.RelativeSource = binding.RelativeSource;
            else if (!string.IsNullOrEmpty(binding.ElementName)) clone.ElementName = binding.ElementName;
            else if (binding.Source is not null) clone.Source = binding.Source;
            result = clone;
        }
        else if (original is MultiBinding multiple)
        {
            var clone = new MultiBinding
            {
                Mode = BindingMode.OneWay, Converter = multiple.Converter,
                ConverterParameter = multiple.ConverterParameter, ConverterCulture = multiple.ConverterCulture
            };
            foreach (var child in multiple.Bindings) clone.Bindings.Add(DisplayBinding(child));
            result = clone;
        }
        else if (original is PriorityBinding priority)
        {
            var clone = new PriorityBinding();
            foreach (var child in priority.Bindings) clone.Bindings.Add(DisplayBinding(child));
            result = clone;
        }
        else return original;
        result.StringFormat = original.StringFormat;
        result.FallbackValue = original.FallbackValue;
        result.TargetNullValue = original.TargetNullValue;
        result.BindingGroupName = original.BindingGroupName;
        return result;
    }
}

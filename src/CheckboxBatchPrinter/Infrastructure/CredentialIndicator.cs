using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace CheckboxBatchPrinter.Infrastructure;

internal static class CredentialIndicator
{
    public static string Text(bool saved, bool draft, string noun) => draft
        ? "● Нове значення ще не збережено" : saved ? $"✓ {noun} збережено" : $"○ {noun} не налаштовано";
    public static string Kind(bool saved, bool draft) => draft ? "Draft" : saved ? "Saved" : "Missing";
    public static void Apply(TextBlock target, bool saved, bool draft, string noun)
    {
        target.Text = Text(saved, draft, noun); target.Tag = Kind(saved, draft);
        target.Foreground = draft ? Brushes.DarkOrange : saved ? Brushes.DarkGreen : Brushes.DimGray;
        target.Background = draft ? new SolidColorBrush(Color.FromRgb(255, 245, 224)) :
            saved ? new SolidColorBrush(Color.FromRgb(232, 245, 233)) : new SolidColorBrush(Color.FromRgb(242, 244, 247));
        target.FontWeight = FontWeights.SemiBold; target.FontSize = 12; target.Padding = new Thickness(6, 4, 6, 4);
    }
}

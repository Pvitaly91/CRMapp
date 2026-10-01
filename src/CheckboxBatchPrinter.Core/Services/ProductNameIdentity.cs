using System.Text;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Comparison-only cleaned title, without translation, fuzzy matching or product dictionaries.</summary>
internal static class ProductNameIdentity
{
    internal static string Normalize(string name)
    {
        var result = new StringBuilder();
        var whitespace = false; var removedSymbol = false; var previousDigit = false;
        foreach (var rune in name.Normalize(NormalizationForm.FormC).ToUpperInvariant().EnumerateRunes())
        {
            // These apostrophe characters are Unicode modifier letters, not punctuation.
            var apostrophe = rune.Value is 0x02BC or 0x02BB;
            if (!apostrophe && Rune.IsLetterOrDigit(rune))
            {
                var digit = Rune.IsDigit(rune);
                // Remove punctuation without joining distinct numeric fragments:
                // 1.5 => "1 5", not "15"; 12/24 => "12 24", not "1224".
                if (result.Length > 0 && (whitespace || removedSymbol && previousDigit && digit)) result.Append(' ');
                result.Append(rune.ToString());
                whitespace = false; removedSymbol = false; previousDigit = digit;
            }
            else if (Rune.IsWhiteSpace(rune)) whitespace = true;
            else removedSymbol = true;
        }
        return result.ToString();
    }
}

using System.Text;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Whole marketplace title identity, without translation, fuzzy matching or product dictionaries.</summary>
internal static class ProductNameIdentity
{
    internal static string Normalize(string name)
    {
        var normalized = new string(name.Normalize(NormalizationForm.FormC).Select(c => c switch
        {
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => '-',
            '\u2018' or '\u2019' or '\u201a' or '\u201b' => '\'',
            '\u201c' or '\u201d' or '\u201e' or '\u201f' or '\u00ab' or '\u00bb' => '"',
            _ => c
        }).ToArray());
        // Keep word order, numbers, connectors, model codes and all non-formatting text.
        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }
}

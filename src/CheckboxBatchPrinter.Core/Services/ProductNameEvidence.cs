using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Literal identity and narrowly typed evidence; never guesses language, SKU namespace or translation.</summary>
internal static class ProductNameEvidence
{
    private sealed record AttributeRule(string Label, Regex Pattern, Func<Match, string> Value);
    private sealed record Description(string Anchor, Dictionary<string, string> Attributes);
    private static Regex Pattern(string value) => new(value, RegexOptions.CultureInvariant);
    private static readonly AttributeRule[] Rules =
    [
        // Only explicit model labels. A random number or an article from another system is not a model key.
        // Extract the model first so e.g. MODEL DC-12V is not misread as a voltage field.
        new("Модель", Pattern(@"(?<![\p{L}\p{N}])(?:МОДЕЛЬ|MODEL)\s*:?\s+(?<value>[\p{L}\p{N}][\p{L}\p{N}/.\-]*)(?![\p{L}\p{N}])"),
            m => m.Groups["value"].Value),
        new("Напруга", Pattern(@"(?<![\p{L}\p{N}/.\-])(?<value>\d+(?:[.,]\d+)?(?:\s*/\s*\d+(?:[.,]\d+)?)*)\s*(?:ВОЛЬТ|V|В)(?![\p{L}\p{N}/.\-])"),
            m => string.Join("/", m.Groups["value"].Value.Split('/').Select(Number).Distinct().Order(StringComparer.Ordinal)) + " В"),
        new("Розмір", Pattern(@"(?<![\p{L}\p{N}])(?:РОЗМІР|РАЗМЕР|SIZE)\s*:?\s+(?:(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>ММ|MM|СМ|CM|М|M)|(?<clothing>XXXL|XXL|XL|XS|S|M|L))(?![\p{L}\p{N}])"), Size),
        new("Колір", Pattern(@"(?<![\p{L}\p{N}/.\-])(?<value>ЧЕРВОНИЙ|КРАСНЫЙ|RED|ЧОРНИЙ|ЧЕРНЫЙ|BLACK|БІЛИЙ|БЕЛЫЙ|WHITE|СИНІЙ|СИНИЙ|BLUE|ЗЕЛЕНИЙ|ЗЕЛЕНЫЙ|GREEN)(?![\p{L}\p{N}/.\-])"), Color),
        new("Роз’єм", Pattern(@"(?<![\p{L}\p{N}/.\-])(?<value>USB-[ACB]|RJ-?45|RJ-?11)(?![\p{L}\p{N}/.\-])"),
            m => m.Groups["value"].Value.Replace("RJ-", "RJ", StringComparison.Ordinal))
    ];

    internal static string Identity(string name)
    {
        var normalized = new string(name.Normalize(NormalizationForm.FormC).Select(c => c switch
        {
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' => '-',
            '\u2018' or '\u2019' or '\u201a' or '\u201b' => '\'',
            '\u201c' or '\u201d' or '\u201e' or '\u201f' or '\u00ab' or '\u00bb' => '"',
            _ => c
        }).ToArray());
        return string.Join(" ", normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
    }

    /// <returns>A concrete exclusion reason, or null when difference is not established.</returns>
    internal static string? Difference(string left, string right)
    {
        var a = Describe(left); var b = Describe(right);
        // Without the same remaining literal product words the attributes are not aligned.
        if (!a.Anchor.Any(char.IsLetter) || a.Anchor != b.Anchor) return null;
        foreach (var rule in Rules)
            if (a.Attributes.TryGetValue(rule.Label, out var av) && b.Attributes.TryGetValue(rule.Label, out var bv) && av != bv)
                return $"{rule.Label}: «{av}» та «{bv}» для зіставленої назви «{a.Anchor}» (порівняно «{left}» / «{right}»).";
        return null;
    }

    private static Description Describe(string name)
    {
        var remaining = Identity(name);
        var values = new Dictionary<string, string>();
        foreach (var rule in Rules)
        {
            var matches = rule.Pattern.Matches(remaining);
            // Multiple values can refer to different roles (input/output, both connector ends).
            // Do not guess the alignment or reduce them to a universal sequence of digits.
            if (matches.Count != 1) continue;
            try { values[rule.Label] = rule.Value(matches[0]); }
            catch (FormatException) { continue; }
            catch (OverflowException) { continue; }
            remaining = remaining.Remove(matches[0].Index, matches[0].Length).Insert(matches[0].Index, " ");
        }
        var anchor = string.Join(" ", remaining.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));
        return new(anchor, values);
    }

    private static string Number(string value) => decimal.Parse(value.Trim().Replace(',', '.'), CultureInfo.InvariantCulture)
        .ToString("G29", CultureInfo.InvariantCulture);
    private static string Size(Match match)
    {
        if (match.Groups["clothing"].Success) return match.Groups["clothing"].Value;
        var multiplier = match.Groups["unit"].Value switch { "ММ" or "MM" => 1m, "СМ" or "CM" => 10m, _ => 1000m };
        return checked(decimal.Parse(Number(match.Groups["value"].Value), CultureInfo.InvariantCulture) * multiplier)
            .ToString("G29", CultureInfo.InvariantCulture) + " мм";
    }
    private static string Color(Match match) => match.Groups["value"].Value switch
    {
        "ЧЕРВОНИЙ" or "КРАСНЫЙ" or "RED" => "червоний",
        "ЧОРНИЙ" or "ЧЕРНЫЙ" or "BLACK" => "чорний",
        "БІЛИЙ" or "БЕЛЫЙ" or "WHITE" => "білий",
        "СИНІЙ" or "СИНИЙ" or "BLUE" => "синій",
        _ => "зелений"
    };
}

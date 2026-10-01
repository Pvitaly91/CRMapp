using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Literal identity and narrowly typed evidence; never guesses language, SKU namespace or translation.</summary>
internal static class ProductNameEvidence
{
    private sealed record AttributeRule(string Label, Regex Pattern, Func<Match, string> Value);
    private sealed record KindRule(string Label, Regex Pattern);
    private sealed record Description(string Anchor, Dictionary<string, string> Attributes);
    private const string ContactKind = "клеми / наконечники проводів";
    private static Regex Pattern(string value) => new(value, RegexOptions.CultureInvariant);
    // A bounded vocabulary of explicit product nouns, not a translation or fuzzy-name
    // guess. Broad nouns such as adapter/module/device are intentionally not classified.
    private static readonly KindRule[] Kinds =
    [
        Kind("кабель", "КАБЕЛЬ|CABLE"),
        Kind("датчик", "ДАТЧИК|SENSOR"),
        // USB charging sockets may also be called chargers/connectors. Keep them in
        // one broad family: those different nouns alone cannot exclude a pair.
        Kind("живлення / роз’єм / розетка", "ЗАРЯДНИЙ ПРИСТРІЙ|ЗАРЯДНОЕ УСТРОЙСТВО|ЗАРЯДКА|CHARGER|БЛОК ЖИВЛЕННЯ|БЛОК ПИТАНИЯ|POWER SUPPLY|РОЗ'ЄМ|РАЗЪ[ЕЁ]М|CONNECTOR|РОЗЕТКА|SOCKET|ГНІЗДО|ГНЕЗДО"),
        Kind("вимикач", "ВИМИКАЧ|ВЫКЛЮЧАТЕЛЬ|SWITCH"),
        Kind(ContactKind, "КЛЕМА|КЛЕМИ|КЛЕМ|КЛЕМЫ|КЛЕММ(?:А|Ы)?|ELECTRICAL TERMINALS?|НАКОНЕЧНИК(?:И|ІВ|ОВ)?\\s+(?:ДЛЯ\\s+)?(?:ПРОВОД(?:ІВ|ОВ)|ДРОТ(?:ІВ|ОВ))"),
        Kind("резистор", "РЕЗИСТОР|RESISTOR"),
        Kind("освітлення", "ЛАМПА|ЛАМПОЧКА|LAMP|BULB|ПІДСВІТКА|ПІДСВІЧУВАННЯ|ПОДСВЕТКА|BACKLIGHT")
    ];
    private static KindRule Kind(string label, string nouns) => new(label, Pattern(@"(?<![\p{L}\p{N}])(?:" + nouns + @")(?![\p{L}\p{N}])"));
    private static readonly HashSet<string> HeadModifiers = new(StringComparer.Ordinal)
    {
        "USB", "USB-A", "USB-B", "USB-C", "DC", "AC", "PIR", "LED", "ВРІЗНА", "ВРЕЗНАЯ", "АВТО", "АВТОМОБІЛЬНИЙ", "АВТОМОБІЛЬНА",
        "АВТОМОБИЛЬНЫЙ", "АВТОМОБИЛЬНАЯ", "БЫСТРАЯ", "БЫСТРЫЙ", "ШВИДКА", "ШВИДКИЙ", "CAR", "FAST",
        "ЧЕРВОНИЙ", "КРАСНЫЙ", "RED", "ЧОРНИЙ", "ЧЕРНЫЙ", "BLACK", "БІЛИЙ", "БЕЛЫЙ", "WHITE",
        "СИНІЙ", "СИНИЙ", "BLUE", "ЗЕЛЕНИЙ", "ЗЕЛЕНЫЙ", "GREEN"
    };
    private static readonly HashSet<string> PositionalWords = new(StringComparer.Ordinal)
    {
        "ДЛЯ", "З", "ІЗ", "ЗІ", "БЕЗ", "ДО", "ВІД", "В", "У", "НА", "МІЖ", "МЕЖДУ", "С", "ОТ", "НЕ",
        "FOR", "WITH", "WITHOUT", "TO", "FROM", "OF", "IN", "OUT", "INPUT", "OUTPUT", "NO", "NOT",
        "ВХІД", "ВИХІД", "ВХОД", "ВЫХОД", "МОДЕЛЬ", "MODEL", "РОЗМІР", "РАЗМЕР", "SIZE",
        "КОЛІР", "ЦВЕТ", "COLOR", "ТИП", "TYPE", "ЛІВИЙ", "ПРАВИЙ", "ЛЕВЫЙ", "LEFT", "RIGHT"
    };
    private static readonly Regex CompositeKind = Pattern(@"(?<![\p{L}\p{N}])(?:ТЕСТЕР|TESTER|КОМПЛЕКТ|НАБІР|НАБОР|SET|KIT|ЧОХОЛ|КОРПУС|CASE|HOLDER)(?![\p{L}\p{N}])");
    private static readonly HashSet<string> ContactModifiers = new(StringComparer.Ordinal)
    {
        "МІДНІ", "МЕДНЫЕ", "COPPER", "ЛАТУННІ", "ЛАТУННЫЕ", "BRASS", "ЕЛЕКТРИЧНІ", "ЭЛЕКТРИЧЕСКИЕ", "ELECTRICAL",
        "КОНТАКТНІ", "КОНТАКТНЫЕ", "СРІБЛЯСТІ", "СЕРЕБРИСТЫЕ", "SILVER", "ЧЕРВОНІ", "КРАСНЫЕ", "RED",
        "ЧОРНІ", "ЧЕРНЫЕ", "BLACK", "БІЛІ", "БЕЛЫЕ", "WHITE"
    };
    private static readonly HashSet<string> CollectionWords = new(StringComparer.Ordinal) { "НАБІР", "НАБОР", "КОМПЛЕКТ", "SET", "KIT" };
    private static readonly Regex ContactSetCount = Pattern(@"\A(?:НАБІР|НАБОР|КОМПЛЕКТ|SET|KIT)\s+[1-9]\d*\s*(?:ШТ(?:УК)?\.?|PCS\.?|PIECES)(?![\p{L}\p{N}])");
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

    internal static string WordOrderIdentity(string name)
    {
        var identity = Identity(name);
        var words = identity.Split(' ');
        // Never reorder numbers/models or role-marked characteristics: input12/output24
        // is not the same as input24/output12. Preserve punctuation-bearing codes too.
        return words.Length > 1 && words.All(w => w.All(char.IsLetter) && !PositionalWords.Contains(w))
            ? string.Join(" ", words.Order(StringComparer.Ordinal)) : identity;
    }

    /// <returns>A concrete exclusion reason, or null when difference is not established.</returns>
    internal static string? Difference(string left, string right)
    {
        var leftKind = ProductKind(left); var rightKind = ProductKind(right);
        if (leftKind is not null && rightKind is not null && leftKind != rightKind)
            return $"Тип товару в назві: «{leftKind}» та «{rightKind}» (порівняно «{left}» / «{right}»).";
        var a = Describe(left); var b = Describe(right);
        // Without the same remaining literal product words the attributes are not aligned.
        if (!a.Anchor.Any(char.IsLetter) || a.Anchor != b.Anchor) return null;
        foreach (var rule in Rules)
            if (a.Attributes.TryGetValue(rule.Label, out var av) && b.Attributes.TryGetValue(rule.Label, out var bv) && av != bv)
                return $"{rule.Label}: «{av}» та «{bv}» для зіставленої назви «{a.Anchor}» (порівняно «{left}» / «{right}»).";
        return null;
    }

    private static string? ProductKind(string name)
    {
        var normalized = Identity(name);
        var mentions = Kinds.SelectMany(kind => kind.Pattern.Matches(normalized).Select(match => (kind.Label, Match: match))).ToArray();
        if (mentions.Length == 0) return null;
        var first = mentions.MinBy(m => m.Match.Index);
        var prefix = normalized[..first.Match.Index].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var contactHead = first.Label == ContactKind && prefix.Length <= 3 &&
            prefix.All(word => HeadModifiers.Contains(word) || ContactModifiers.Contains(word) || CollectionWords.Contains(word));
        if (!contactHead && !(prefix.Length <= 3 && prefix.All(HeadModifiers.Contains))) return null;
        // A quantified set of the same electrical contacts still has a known main product.
        // Mixed kits, testers, cases and unrelated collection descriptions remain uncertain.
        foreach (Match composite in CompositeKind.Matches(normalized))
            if (!contactHead || !CollectionWords.Contains(composite.Value) ||
                !(composite.Index < first.Match.Index || ContactSetCount.IsMatch(normalized[composite.Index..]))) return null;
        var mainKinds = mentions.Where(m => !(m.Label == ContactKind && first.Label != ContactKind &&
            IsContactDescription(normalized, first.Match, m.Match))).Select(m => m.Label).Distinct().ToArray();
        return mainKinds.Length == 1 ? first.Label : null;
    }

    private static bool IsContactDescription(string name, Match mainProduct, Match contact)
    {
        // "Вимикач ..., сріблясті клеми" describes its contacts. A bare enumeration
        // "Вимикач, клеми" or "вимикач та клеми" may be a kit and is not discarded.
        var comma = name.LastIndexOf(',', contact.Index);
        if (comma < mainProduct.Index + mainProduct.Length) return false;
        var words = name[(comma + 1)..contact.Index].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length is > 0 and <= 3 && words.All(ContactModifiers.Contains);
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

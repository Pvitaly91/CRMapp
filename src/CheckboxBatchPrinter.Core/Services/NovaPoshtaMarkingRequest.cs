using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public static class NovaPoshtaMarkingRequest
{
    // Application safety bound, not a claim about a Nova Poshta quota.
    public const int MaximumTrackingNumbers = 100;

    // SDK Marking::printZebraMarking at sashalenz/nova-poshta-api revision 1c7027d:
    // one number uses orders[]; multiple numbers use orders and a comma-separated list.
    // Duplicate numbers are removed in first-occurrence order before choosing the route.
    public static string CreateUrl(string apiKey, IReadOnlyList<string> trackingNumbers)
    {
        if (apiKey is null || !NovaPoshtaReadOnlyContract.ValidKey(apiKey))
            throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Очікує локального введення ключа Нової пошти.");
        if (trackingNumbers is null || trackingNumbers.Count is < 1 or > MaximumTrackingNumbers ||
            trackingNumbers.Any(number => number is null || !Regex.IsMatch(number, @"\A[0-9]{14}\z")))
            throw new ShippingLabelException(LabelFailureKind.InvalidDocument,
                "Запит етикеток Нової пошти має містити від 1 до 100 коректних номерів ТТН.");
        var numbers = trackingNumbers.Distinct(StringComparer.Ordinal).ToArray();
        var route = numbers.Length == 1 ? "orders%5B%5D/" : "orders/";
        return "https://my.novaposhta.ua/orders/printMarking100x100/" + route + string.Join(',', numbers) +
            "/type/pdf/zebra/zebra/apiKey/" + apiKey;
    }
}

using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Classify only carrier tracking, never marketplace order status or a print marker.
public static class ShipmentTrackingProgress
{
    public static bool IsDispatched(ShippingCarrier carrier, string status)
    {
        if (carrier is not (ShippingCarrier.NovaPoshta or ShippingCarrier.RozetkaDelivery) || status.Length > 500)
            return false;
        var text = string.Join(" ", status.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant().Replace('’', '\'').Replace('ʼ', '\'').TrimEnd('.', '!');
        // Do not infer progress from an occurrence of a positive word in a refusal/return message.
        if (Exclusions.Any(text.Contains)) return false;
        if (Common.Any(prefix => Matches(text, prefix))) return true;
        var prefixes = carrier == ShippingCarrier.NovaPoshta ? NovaPoshta : RozetkaDelivery;
        return prefixes.Any(prefix => Matches(text, prefix));
    }

    private static readonly string[] Exclusions = ["скасован", "скасовано", "отмен", "відмов", "отказ", "повернен", "поверта", "возврат",
        "відправником", "отправителем", "не передан", "не прийнят", "не принят", "втрач", "пошкоджен"];
    private static readonly string[] Common = ["передано перевізнику", "прийнято перевізником", "передано кур'єру", "передано на доставку",
        "в дорозі", "у дорозі", "доставляється", "готово до видачі", "готовий до видачі",
        "передано перевозчику", "принято перевозчиком", "передано курьеру", "в пути", "готово к выдаче"];
    private static readonly string[] NovaPoshta = ["відправлення прийнято", "відправлення прийняте", "відправлення прямує", "відправлення у м.",
        "відправлення в м.", "відправлення у місті", "відправлення прибуло", "прибуло у відділення",
        "прибув у відділення", "відправлення передано кур'єру", "відправлення отримано", "отримано",
        "отправление принято", "отправление направляется", "отправление в г.", "отправление прибыло",
        "прибыло в отделение", "отправление передано курьеру", "отправление получено", "получено"];
    private static readonly string[] RozetkaDelivery = ["прийнято від відправника", "прийнято в пункті прийому", "прийнято у пункті прийому",
        "прийнято за реєстром", "прийнято на доставку", "посилка отримана на рц", "відправлення у відділенні",
        "термін зберігання продовжено", "видано", "частково отримано",
        "прийнято на сортувальному центрі", "прибуло на сортувальний центр", "прийнято у відділенні",
        "прийнято у відділення", "замовлення видано отримувачу", "видано отримувачу", "замовлення доставлено",
        "acceptedfromreception", "acceptedfrommerchant", "acceptedbycarrier", "intheway",
        "acceptedondistributioncenter", "forwarding", "arrivedtodistributioncenter", "containeronacceptance",
        "acceptedindepartment", "placedinstore", "readytoissueoctopus", "gaveout", "gaveoutpartially"];

    private static bool Matches(string text, string prefix) => text == prefix ||
        text.StartsWith(prefix + " ", StringComparison.Ordinal) || text.StartsWith(prefix + ",", StringComparison.Ordinal) ||
        text.StartsWith(prefix + ":", StringComparison.Ordinal);
}

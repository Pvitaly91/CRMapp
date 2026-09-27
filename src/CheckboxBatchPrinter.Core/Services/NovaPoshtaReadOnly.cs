using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface ILabelPdfInspector
{ Task<LabelPdfInspection> InspectAsync(byte[] pdf, CancellationToken cancellationToken = default); }

// Candidate contract from sashalenz/nova-poshta-api revision 1c7027d, NOT official API documentation.
public static class NovaPoshtaReadOnlyContract
{
    public const string EntryPoint = "https://api.novaposhta.ua/v2.0/json/";
    public const int MaximumPages = 10;
    public static bool ValidKey(string value) => Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{1,128}\z");
    public static void ValidateBody(HttpRequestMessage request)
    {
        try
        {
            // Only bounded, already buffered JSON content; no arbitrary streaming API console.
            if (request.Content is not StringContent || request.Content.Headers.ContentType?.MediaType != "application/json" ||
                request.Content.Headers.ContentLength is not (>= 1 and <= 4096)) throw new InvalidOperationException();
            using var json = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var root = json.RootElement;
            ExactKeys(root, ["apiKey", "modelName", "calledMethod", "methodProperties"]);
            if (!ValidKey(root.GetProperty("apiKey").GetString() ?? "") || root.GetProperty("modelName").GetString() != "InternetDocument" ||
                root.GetProperty("calledMethod").GetString() != "getDocumentList") throw new InvalidOperationException();
            var props = root.GetProperty("methodProperties");
            ExactKeys(props, ["DateTimeFrom", "DateTimeTo", "GetFullList", "Page"]);
            if (props.GetProperty("GetFullList").ValueKind != JsonValueKind.False ||
                !int.TryParse(props.GetProperty("Page").GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out var page) || page is < 1 or > MaximumPages ||
                !DateOnly.TryParseExact(props.GetProperty("DateTimeFrom").GetString(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) ||
                !DateOnly.TryParseExact(props.GetProperty("DateTimeTo").GetString(), "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to)) throw new InvalidOperationException();
            new NovaPoshtaSearchPeriod(from, to).Validate();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new InvalidOperationException("NP: дозволено лише обмежене читання InternetDocument/getDocumentList з перевіреними параметрами."); }
    }
    private static void ExactKeys(JsonElement value, string[] keys)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidOperationException();
        var actual = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (actual.Length != keys.Length || actual.Distinct(StringComparer.Ordinal).Count() != keys.Length ||
            actual.Any(k => !keys.Contains(k, StringComparer.Ordinal))) throw new InvalidOperationException();
    }
}

public sealed class NovaPoshtaReadOnlyClient(ShippingLabelHttp http)
{
    public async Task<NovaPoshtaLookup> LookupAsync(NovaPoshtaConnection connection, ShipmentReference shipment,
        NovaPoshtaSearchPeriod period, CancellationToken ct = default)
    {
        Validate(connection, shipment); period.Validate();
        var seenPages = new HashSet<string>(); var matches = new Dictionary<string, (string Ref, int? Places)>();
        for (var page = 1; page <= NovaPoshtaReadOnlyContract.MaximumPages; page++)
        {
            var body = JsonSerializer.Serialize(new { apiKey = connection.ApiKey, modelName = "InternetDocument", calledMethod = "getDocumentList",
                methodProperties = new { DateTimeFrom = period.From.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                    DateTimeTo = period.To.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), GetFullList = false, Page = page.ToString(CultureInfo.InvariantCulture) } });
            var bytes = await http.SendAsync(() => new(HttpMethod.Post, NovaPoshtaReadOnlyContract.EntryPoint)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") }, "application/json", ct);
            try
            {
                using var json = JsonDocument.Parse(bytes);
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) ||
                    success.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || !root.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
                    throw InvalidResponse();
                if (success.ValueKind == JsonValueKind.False || errors.GetArrayLength() != 0)
                    throw new ShippingLabelException(LabelFailureKind.AccessDenied, "NP відхилила читання. Перевірте ключ і права саме вибраного акаунта; тіло помилки приховано.");
                if (data.GetArrayLength() > 1000) throw InvalidResponse();
                if (data.GetArrayLength() == 0) return Result(true, page);
                var identities = new List<string>();
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("IntDocNumber", out var number) ||
                        number.ValueKind != JsonValueKind.String || !Regex.IsMatch(number.GetString()!, @"\A[0-9]{14}\z")) throw InvalidResponse();
                    var ttn = number.GetString()!;
                    if (item.TryGetProperty("Ref", out var referenceField) && referenceField.ValueKind is not (JsonValueKind.String or JsonValueKind.Null)) throw InvalidResponse();
                    var reference = item.TryGetProperty("Ref", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()! : "";
                    if (reference.Length > 0 && (!Guid.TryParse(reference, out var id) || id == Guid.Empty)) throw InvalidResponse();
                    int? places = null;
                    if (item.TryGetProperty("SeatsAmount", out var seats) && seats.ValueKind != JsonValueKind.Null)
                    {
                        if (seats.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
                            !int.TryParse(seats.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 1 or > 100) throw InvalidResponse();
                        places = n;
                    }
                    identities.Add(ttn + ":" + reference);
                    if (ttn != shipment.TrackingNumber) continue;
                    if (item.TryGetProperty("DeletionMark", out var deleted) && deleted.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidResponse();
                    if (deleted.ValueKind == JsonValueKind.True) throw new ShippingLabelException(LabelFailureKind.MissingDocument, "Вибрана ТТН позначена видаленою в цьому акаунті.");
                    if (shipment.ShipmentId.Length > 0 && shipment.ShipmentId != reference)
                        throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "NP повернула інший document Ref для вибраної ТТН.");
                    if (matches.Count > 0 && (!matches.TryGetValue(reference, out var previous) || previous.Places != places)) throw InvalidResponse();
                    matches[reference] = (reference, places);
                }
                // The SDK has no verified page-size/total contract. Only an empty page proves termination.
                var pageIdentity = string.Join("|", identities.Order(StringComparer.Ordinal));
                if (!seenPages.Add(pageIdentity)) return Result(false, page);
            }
            catch (JsonException) { throw InvalidResponse(); }
        }
        return Result(false, NovaPoshtaReadOnlyContract.MaximumPages);

        NovaPoshtaLookup Result(bool complete, int pagesRead)
        {
            var match = matches.Values.SingleOrDefault();
            return new(shipment.TrackingNumber, match.Ref ?? "", match.Places, complete, pagesRead,
                !complete ? "Вибірка неповна: досягнуто ліміт або сервер повторив сторінку. Пошук не завершений; змініть період і запустіть окрему перевірку."
                : matches.Count == 0 ? "Не знайдено у перевіреній вибірці цього акаунта. Це не означає, що ТТН не існує або ключ неправильний."
                : string.IsNullOrEmpty(match.Ref) ? "Номер знайдено, але document Ref не надано; його значення не підставляється. Маршрут SDK використовує публічний номер."
                : "Точний номер ТТН знайдено у перевіреній вибірці вибраного акаунта.", matches.Count == 1);
        }
    }
    public async Task<ShippingLabelDocument> ReadMarkingAsync(NovaPoshtaConnection connection, ShipmentReference shipment,
        NovaPoshtaLookup lookup, CancellationToken ct = default)
    {
        Validate(connection, shipment);
        if (!lookup.Complete || !lookup.Found || lookup.TrackingNumber != shipment.TrackingNumber || lookup.DocumentRef.Length > 0 && !Guid.TryParse(lookup.DocumentRef, out _))
            throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Спершу потрібен завершений пошук точної ТТН; Ref перевіряється лише якщо наданий.");
        var url = "https://my.novaposhta.ua/orders/printMarking100x100/orders%5B%5D/" + shipment.TrackingNumber + "/type/pdf/zebra/zebra/apiKey/" + connection.ApiKey;
        var pdf = await http.SendAsync(() => new(HttpMethod.Get, url), "application/pdf", ct);
        ShippingLabelHttp.ValidatePdf(pdf);
        // DocumentId is a typed public-TTN identity when no Ref was returned, NOT a fabricated Ref.
        return new(shipment, connection.Id, lookup.DocumentRef.Length > 0 ? lookup.DocumentRef : "ttn:" + lookup.TrackingNumber,
            pdf, Convert.ToHexString(SHA256.HashData(pdf)), DateTimeOffset.UtcNow, lookup.Places ?? 0)
        { Source = "NovaPoshta.Direct.SDKCandidate" };
    }
    private static void Validate(NovaPoshtaConnection connection, ShipmentReference shipment)
    {
        if (!NovaPoshtaReadOnlyContract.ValidKey(connection.ApiKey)) throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Очікує локального введення ключа Нової пошти.");
        if (shipment.Carrier != ShippingCarrier.NovaPoshta || !Regex.IsMatch(shipment.TrackingNumber, @"\A[0-9]{14}\z"))
            throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Оберіть існуючу ТТН Нової пошти з 14 цифр із замовлення Prom.");
    }
    private static ShippingLabelException InvalidResponse() => new(LabelFailureKind.InvalidDocument, "NP: структура відповіді не відповідає дослідженому SDK. Запит зупинено без вгадування методів або полів.");
}

public sealed class NovaPoshtaDiagnostics(NovaPoshtaReadOnlyClient client, ILabelPdfInspector inspector)
{
    public async Task<NovaPoshtaDiagnosticResult> CheckAsync(NovaPoshtaConnection connection, ShipmentReference shipment,
        NovaPoshtaSearchPeriod period, CancellationToken ct = default)
    {
        var lookup = await client.LookupAsync(connection, shipment, period, ct);
        if (!lookup.Complete || !lookup.Found) return new(lookup, null, null, LabelIdentityCheck.Unconfirmed, false, lookup.Message);
        var document = await client.ReadMarkingAsync(connection, shipment, lookup, ct);
        return Evaluate(lookup, document, await inspector.InspectAsync(document.Pdf, ct));
    }
    public static NovaPoshtaDiagnosticResult Evaluate(NovaPoshtaLookup lookup, ShippingLabelDocument document, LabelPdfInspection inspection)
    {
        var statuses = inspection.Pages.Select(page =>
        {
            // Maximal numeric runs: never accept 14-digit prefixes/suffixes of longer grouped numbers.
            // If adjacent numeric fields cannot be separated reliably, identity stays unconfirmed.
            var numbers = Regex.Matches(page.Text, @"(?<![0-9])[0-9]+(?:[ \t-]+[0-9]+)*")
                .Select(m => Regex.Replace(m.Value, @"[^0-9]", "")).Where(n => n.Length == 14).ToArray();
            return numbers.Any(n => n != lookup.TrackingNumber) ? LabelIdentityCheck.Contradiction
                : numbers.Contains(lookup.TrackingNumber, StringComparer.Ordinal) ? LabelIdentityCheck.Verified : LabelIdentityCheck.Unconfirmed;
        }).ToArray();
        var identity = statuses.Contains(LabelIdentityCheck.Contradiction) ? LabelIdentityCheck.Contradiction
            : statuses.Length > 0 && statuses.All(s => s == LabelIdentityCheck.Verified) ? LabelIdentityCheck.Verified : LabelIdentityCheck.Unconfirmed;
        // Count alone does not prove all places for a multi-place document. Keep it diagnostic-only.
        var allPlaces = identity == LabelIdentityCheck.Verified && lookup.Places == 1 && inspection.Pages.Count == 1;
        var message = identity == LabelIdentityCheck.Contradiction ? "У PDF виявлено інший номер; документ відхилено для пакета."
            : identity != LabelIdentityCheck.Verified ? "Ідентичність ТТН не підтверджена текстовим шаром. Лише діагностичний перегляд, не пакет."
            : !allPlaces ? "ТТН у текстовому шарі збігається. Повнота місць не підтверджена: кількість сторінок не доводить номери всіх місць. Лише діагностичний перегляд."
            : "ТТН підтверджена текстовим шаром; одне місце та одна сторінка. Фізичний друк не виконувався.";
        return new(lookup, document, inspection, identity, allPlaces, message);
    }
}

// Session-only evidence. No PDF or buyer text retained; key rotation invalidates evidence without losing history.
public sealed class NovaPoshtaVerifiedSession
{
    private readonly Dictionary<(string, string), (string Key, NovaPoshtaLookup Lookup)> _verified = [];
    public void Accept(NovaPoshtaConnection connection, NovaPoshtaDiagnosticResult result)
    {
        if (!result.CanUseInBatch || result.Document!.ConnectionId != connection.Id) throw new InvalidOperationException("Документ не підтверджений для пакета.");
        if (_verified.Count >= 100) _verified.Clear();
        _verified[(connection.Id, result.Lookup.TrackingNumber)] = (connection.ApiKey, result.Lookup);
    }
    public NovaPoshtaLookup Require(NovaPoshtaConnection connection, string ttn) =>
        _verified.TryGetValue((connection.Id, ttn), out var evidence) && evidence.Key == connection.ApiKey ? evidence.Lookup :
        throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Цю ТТН спочатку явно перевірте через «Перевірити наявну накладну» у поточному сеансі та вибраному NP-акаунті.");
}

public sealed class NovaPoshtaDirectLabelSource(IShippingSettingsStore settings, NovaPoshtaReadOnlyClient client,
    ILabelPdfInspector inspector, NovaPoshtaVerifiedSession session) : IShippingLabelSource
{
    public async Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment, CancellationToken cancellationToken = default)
    {
        var config = await settings.LoadAsync(cancellationToken);
        var binding = config.NovaPoshtaStoreBindings.SingleOrDefault(b => b.MarketplaceConnectionId == shipment.Order.ConnectionId);
        var connection = config.NovaPoshtaConnections.SingleOrDefault(c => c.Id == binding?.NovaPoshtaConnectionId)
            ?? throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Виберіть пряме NP-підключення для цього магазину Prom.");
        if (connection.ApiKey.Length == 0) throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Очікує локального введення ключа Нової пошти.");
        var lookup = session.Require(connection, shipment.TrackingNumber);
        if (shipment.ShipmentId.Length > 0 && shipment.ShipmentId != lookup.DocumentRef) throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "ТТН/Ref змінилися після діагностики.");
        var document = await client.ReadMarkingAsync(connection, shipment, lookup, cancellationToken);
        var result = NovaPoshtaDiagnostics.Evaluate(lookup, document, await inspector.InspectAsync(document.Pdf, cancellationToken));
        if (!result.CanUseInBatch) throw new ShippingLabelException(LabelFailureKind.InvalidDocument, "Актуальний PDF не пройшов повторну перевірку. " + result.Message);
        return document;
    }
}

public sealed class ConfiguredShippingLabelSource(IShippingSettingsStore settings, IShippingLabelSource direct,
    IShippingLabelSource existing) : IShippingLabelSource
{
    public async Task<ShippingLabelDocument> GetAsync(ShipmentReference shipment, CancellationToken cancellationToken = default)
    {
        var config = await settings.LoadAsync(cancellationToken);
        if (shipment.Carrier == ShippingCarrier.NovaPoshta && shipment.Order.Marketplace == MarketplaceKind.Prom)
        {
            if (config.NovaPoshtaStoreBindings.Any(b => b.MarketplaceConnectionId == shipment.Order.ConnectionId))
                return await direct.GetAsync(shipment, cancellationToken);
            if (config.NovaPoshtaSellerConnectionId.Length == 0)
                throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Для магазину Prom явно виберіть NP-підключення у налаштуваннях наклейок. Rozetka Seller не потрібний; акаунти не перебираються.");
        }
        return await existing.GetAsync(shipment, cancellationToken);
    }
}

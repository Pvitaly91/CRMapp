using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Reads carrier accounts directly. Order caches, marketplace availability and receipt links are not inputs.
public sealed class WaybillService(ShippingLabelHttp http, IShippingSettingsStore settings) : IWaybillService
{
    public const int MaximumDocumentsPerSource = 100000;
    public const int DeliveryPageSize = 100;
    private const string DeliveryUrl = "https://rz-delivery.rozetka.ua/api/track";

    public async Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from) throw new InvalidOperationException("Дата до має бути не раніше дати від.");
        cancellationToken.ThrowIfCancellationRequested();
        var loaded = await settings.LoadAsync(cancellationToken).ConfigureAwait(false);
        var config = loaded with { NovaPoshtaConnections = loaded.NovaPoshtaConnections.ToArray(),
            NovaPoshtaStoreBindings = loaded.NovaPoshtaStoreBindings.ToArray() };
        // Carriers have independent credentials and request policies. Keep each carrier's
        // accounts/pages serial, while allowing its network wait to overlap the other carrier.
        var novaPoshtaTask = ReadNovaPoshtaSourcesAsync(config, from, to, cancellationToken);
        var deliveryTask = ReadDeliverySourceAsync(config, from, to, cancellationToken);
        await Task.WhenAll(novaPoshtaTask, deliveryTask).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        var novaPoshta = await novaPoshtaTask.ConfigureAwait(false);
        var delivery = await deliveryTask.ConfigureAwait(false);
        return new(novaPoshta.Documents.Concat(delivery.Documents).OrderByDescending(r => r.CreatedLocal)
            .ThenBy(r => r.TrackingNumber, StringComparer.Ordinal).ToArray(),
            novaPoshta.Sources.Concat(delivery.Sources).ToArray());
    }

    private async Task<WaybillFetchResult> ReadNovaPoshtaSourcesAsync(ShippingLabelSettings config,
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        var documents = new List<WaybillRecord>();
        var sources = new List<WaybillSourceStatus>();
        var readKeys = new HashSet<string>(StringComparer.Ordinal);
        if (config.NovaPoshtaConnections.Count == 0)
            sources.Add(new(ShippingCarrier.NovaPoshta, "Нова пошта", false, 0, "API-ключ Нової пошти не встановлений."));
        foreach (var connection in config.NovaPoshtaConnections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = SafeText(connection.Name, 120);
            if (name.Length == 0) name = "Нова пошта";
            if (!NovaPoshtaReadOnlyContract.ValidKey(connection.ApiKey))
            {
                sources.Add(new(ShippingCarrier.NovaPoshta, name, false, 0,
                    string.IsNullOrWhiteSpace(connection.ApiKey) ? "API-ключ не встановлений." : "Перевірте формат збереженого API-ключа."));
                continue;
            }
            // Store aliases may share an account key. Scan that account once using its first stable connection identity.
            if (!readKeys.Add(NovaPoshtaRequestPolicy.KeyFingerprint(connection.ApiKey))) continue;
            var rows = new Dictionary<string, WaybillRecord>(StringComparer.Ordinal);
            var status = await ReadSourceAsync(ShippingCarrier.NovaPoshta, name, rows,
                () => ReadNovaPoshtaAsync(connection, name, from, to, rows, cancellationToken), cancellationToken).ConfigureAwait(false);
            documents.AddRange(rows.Values); sources.Add(status);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(documents, sources);
    }

    private async Task<WaybillFetchResult> ReadDeliverySourceAsync(ShippingLabelSettings config,
        DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(config.RozetkaDeliveryToken))
            return new([], [new(ShippingCarrier.RozetkaDelivery, "Rozetka Delivery", false, 0, "Токен Rozetka Delivery не встановлений.")]);
        var rows = new Dictionary<string, WaybillRecord>(StringComparer.Ordinal);
        var status = await ReadSourceAsync(ShippingCarrier.RozetkaDelivery, "Rozetka Delivery", rows,
            () => ReadDeliveryAsync(config, from, to, rows, cancellationToken), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new(rows.Values.ToArray(), [status]);
    }

    private static async Task<WaybillSourceStatus> ReadSourceAsync(ShippingCarrier carrier, string name,
        Dictionary<string, WaybillRecord> rows, Func<Task> read, CancellationToken ct)
    {
        try
        {
            await read().ConfigureAwait(false);
            return new(carrier, name, true, rows.Count, "Оновлено. Усі сторінки за вибраний період завантажені.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ShippingLabelException ex)
        {
            // Raw carrier response, request URLs and credentials never become UI messages.
            var message = ex.Kind is LabelFailureKind.AccessDenied or LabelFailureKind.ConnectionRequired
                ? "Немає доступу до API цього підключення. Перевірте збережений ключ і права акаунта."
                : ex.Kind == LabelFailureKind.Temporary ? "API цього підключення тимчасово недоступне. Повторіть оновлення пізніше."
                : "API не надало очікувані дані накладних.";
            return new(carrier, name, false, rows.Count, "Вибірка неповна. " + message);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or FormatException or InvalidOperationException or ArgumentException)
        { return new(carrier, name, false, rows.Count, "Вибірка неповна. API повернуло неочікувані дані, повторило сторінку або досягнуто обмеження читання."); }
    }

    private async Task ReadNovaPoshtaAsync(NovaPoshtaConnection connection, string name, DateOnly from, DateOnly to,
        Dictionary<string, WaybillRecord> rows, CancellationToken ct)
    {
        foreach (var (start, end) in Periods(from, to))
        {
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            var seenDocuments = new HashSet<string>(StringComparer.Ordinal);
            int? expectedTotal = null;
            var complete = false;
            for (var page = 1; page <= NovaPoshtaReadOnlyContract.MaximumListPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                var body = JsonSerializer.Serialize(new { apiKey = connection.ApiKey, modelName = "InternetDocument", calledMethod = "getDocumentList",
                    methodProperties = new { DateTimeFrom = start.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                        DateTimeTo = end.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture), GetFullList = false, Page = page.ToString(CultureInfo.InvariantCulture) } });
                using var json = JsonDocument.Parse(await http.SendAsync(() => new(HttpMethod.Post, NovaPoshtaReadOnlyContract.EntryPoint)
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") }, "application/json", ct).ConfigureAwait(false));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Field(root, "success").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    Field(root, "data").ValueKind != JsonValueKind.Array || Field(root, "errors").ValueKind != JsonValueKind.Array) throw Invalid();
                if (Field(root, "success").ValueKind != JsonValueKind.True || Field(root, "errors").GetArrayLength() != 0)
                    throw new ShippingLabelException(LabelFailureKind.AccessDenied, "API відхилило читання накладних.");
                var data = Field(root, "data");
                if (data.GetArrayLength() > 1000) throw Invalid();
                var totalField = Field(Field(root, "info"), "totalCount");
                if (totalField.ValueKind != JsonValueKind.Undefined)
                {
                    // This numeric field was verified in the live read-only API response. Do not infer totals from page size.
                    if (totalField.ValueKind != JsonValueKind.Number || !totalField.TryGetInt32(out var total) ||
                        total is < 0 or > MaximumDocumentsPerSource || expectedTotal is { } previous && previous != total) throw Invalid();
                    expectedTotal = total;
                }
                if (data.GetArrayLength() == 0)
                {
                    if (expectedTotal is { } total && seenDocuments.Count != total) throw Invalid();
                    complete = true; break;
                }
                var records = data.EnumerateArray().Select(item => ParseNovaPoshta(item, connection.Id, name) with
                { CredentialFingerprint = WaybillAccountProof.Create(ShippingCarrier.NovaPoshta, connection.Id, connection.ApiKey) }).ToArray();
                GuardPage(records, seenPages);
                if (records.Any(record => !seenDocuments.Add(record.DocumentId))) throw Invalid();
                foreach (var record in records) AddRecord(rows, record, from, to);
                if (expectedTotal is { } expected)
                {
                    if (seenDocuments.Count > expected) throw Invalid();
                    if (seenDocuments.Count == expected) { complete = true; break; }
                }
            }
            if (!complete) throw Invalid();
        }
    }

    private async Task ReadDeliveryAsync(ShippingLabelSettings config, DateOnly from, DateOnly to,
        Dictionary<string, WaybillRecord> rows, CancellationToken ct)
    {
        foreach (var (start, end) in Periods(from, to))
        foreach (var archived in new[] { false, true })
        foreach (var deleted in new[] { false, true })
        {
            var seenPages = new HashSet<string>(StringComparer.Ordinal);
            var seenDocuments = new HashSet<string>(StringComparer.Ordinal);
            int? expectedTotal = null, expectedPages = null;
            var readCount = 0; var complete = false;
            for (var page = 1; page <= NovaPoshtaReadOnlyContract.MaximumListPages; page++)
            {
                ct.ThrowIfCancellationRequested();
                var url = DeliveryUrl + "?page=" + page.ToString(CultureInfo.InvariantCulture) + "&limit=" + DeliveryPageSize +
                    "&sort=DESC&created_date_start=" + start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    "&created_date_end=" + end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                    "&partner_type=rz-delivery&partner_type=rozetka-marketplace&is_archived=" + (archived ? "true" : "false") +
                    "&is_deleted=" + (deleted ? "true" : "false");
                using var json = JsonDocument.Parse(await http.SendAsync(() => DeliveryRequest(url, config.RozetkaDeliveryToken), "application/json", ct).ConfigureAwait(false));
                var root = json.RootElement;
                if (root.ValueKind != JsonValueKind.Object || Field(root, "statusCode").ToString() != "0")
                    throw new ShippingLabelException(LabelFailureKind.AccessDenied, "API відхилило читання накладних.");
                var data = Field(root, "data"); var pagination = Field(root, "pagination");
                if (data.ValueKind != JsonValueKind.Array || data.GetArrayLength() > DeliveryPageSize) throw Invalid();
                var total = RequiredInt(pagination, "total_count", 0, MaximumDocumentsPerSource);
                var pages = RequiredInt(pagination, "page_count", 0, NovaPoshtaReadOnlyContract.MaximumListPages);
                if (RequiredInt(pagination, "page", 1, NovaPoshtaReadOnlyContract.MaximumListPages) != page ||
                    RequiredInt(pagination, "limit", 1, DeliveryPageSize) != DeliveryPageSize ||
                    (total == 0 ? pages is > 1 : pages != (total + DeliveryPageSize - 1) / DeliveryPageSize) ||
                    expectedTotal is { } previousTotal && previousTotal != total || expectedPages is { } previousPages && previousPages != pages) throw Invalid();
                expectedTotal = total; expectedPages = pages;
                var records = data.EnumerateArray().Select(item => ParseDelivery(item, config.RozetkaDeliveryConnectionId) with
                { CredentialFingerprint = WaybillAccountProof.Create(ShippingCarrier.RozetkaDelivery, config.RozetkaDeliveryConnectionId, config.RozetkaDeliveryToken) }).ToArray();
                if (records.Length > 0) GuardPage(records, seenPages);
                if (records.Any(record => !seenDocuments.Add(record.DocumentId))) throw Invalid();
                readCount += records.Length;
                foreach (var record in records) AddRecord(rows, record, from, to);
                if (page >= pages)
                {
                    if (readCount != total) throw Invalid();
                    complete = true; break;
                }
                if (records.Length == 0 || readCount >= total) throw Invalid();
            }
            if (!complete) throw Invalid();
        }
    }

    private static WaybillRecord ParseNovaPoshta(JsonElement item, string connectionId, string name)
    {
        var number = Text(item, "IntDocNumber");
        if (!Regex.IsMatch(number, @"\A[0-9]{14}\z")) throw Invalid();
        var reference = Text(item, "Ref");
        if (reference.Length > 0 && (!Guid.TryParse(reference, out var id) || id == Guid.Empty)) throw Invalid();
        var deleted = Flag(item, "DeletionMark");
        return new(ShippingCarrier.NovaPoshta, connectionId, name, reference.Length == 0 ? "ttn:" + number : reference, number,
            Date(item, "DateTime"), deleted ? "Видалена" : Text(item, "StateName"), Text(item, "RecipientContactPerson"),
            Text(item, "CityRecipientDescription"), Money(item, "Cost"), NullableInt(item, "SeatsAmount", 1, 1000), deleted);
    }

    private static WaybillRecord ParseDelivery(JsonElement item, string connectionId)
    {
        var id = Text(item, "id");
        if (!Regex.IsMatch(id, @"\A[0-9A-Za-z-]{1,80}\z")) throw Invalid();
        var recipient = Field(item, "recipient");
        var recipientName = Text(recipient, "name");
        if (recipientName.Length == 0) recipientName = string.Join(" ", new[] { Text(recipient, "last_name"), Text(recipient, "first_name"), Text(recipient, "middle_name") }.Where(s => s.Length > 0));
        var department = Field(recipient, "department");
        var city = Text(department, "public_name");
        if (city.Length == 0) city = Text(department, "name");
        var deleted = Flag(item, "is_deleted"); var archived = Flag(item, "is_archived");
        var status = Text(item, "last_status_name");
        if (archived) status = "Архів" + (status.Length > 0 ? " · " + status : "");
        if (deleted) status = "Видалено" + (status.Length > 0 ? " · " + status : "");
        return new(ShippingCarrier.RozetkaDelivery, connectionId, "Rozetka Delivery", id, id, Date(item, "created_at"),
            status, recipientName, city, Money(item, "insurance_cost"), NullableInt(item, "places", 1, 1000), deleted);
    }

    private static void AddRecord(Dictionary<string, WaybillRecord> rows, WaybillRecord record, DateOnly from, DateOnly to)
    {
        if (record.CreatedLocal is { } created && (DateOnly.FromDateTime(created) < from || DateOnly.FromDateTime(created) > to)) return;
        if (rows.TryGetValue(record.DocumentId, out var existing) && existing.TrackingNumber != record.TrackingNumber) throw Invalid();
        if (!rows.ContainsKey(record.DocumentId) && rows.Count >= MaximumDocumentsPerSource) throw Invalid();
        rows[record.DocumentId] = record;
    }
    private static void GuardPage(WaybillRecord[] records, HashSet<string> seen)
    {
        var identities = records.Select(r => r.DocumentId).Order(StringComparer.Ordinal).ToArray();
        if (identities.Distinct(StringComparer.Ordinal).Count() != identities.Length || !seen.Add(string.Join("|", identities))) throw Invalid();
    }
    private static IEnumerable<(DateOnly From, DateOnly To)> Periods(DateOnly from, DateOnly to)
    {
        for (var start = from; start <= to;)
        {
            var end = DateOnly.FromDayNumber((int)Math.Min((long)start.DayNumber + 6, to.DayNumber));
            yield return (start, end);
            if (end == to) yield break;
            start = end.AddDays(1);
        }
    }
    private static HttpRequestMessage DeliveryRequest(string url, string token)
    {
        var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.TryAddWithoutValidation("Content-Language", "uk");
        return message;
    }
    private static JsonElement Field(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    private static string Text(JsonElement value, string name)
    {
        var field = Field(value, name);
        return field.ValueKind == JsonValueKind.String ? SafeText(field.GetString()!, 240) : "";
    }
    private static string SafeText(string value, int maximum) => new(value.Where(c => !char.IsControl(c)).Take(maximum).ToArray());
    private static DateTime? Date(JsonElement value, string name)
    {
        var text = Text(value, name);
        if (text.Length == 0) return null;
        if (Regex.IsMatch(text, @"(?:Z|[+-][0-9]{2}:[0-9]{2})\z") && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant))
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTime(instant, DateRangeBuilder.KyivZone).DateTime, DateTimeKind.Unspecified);
        return DateTime.TryParseExact(text, ["dd.MM.yyyy HH:mm:ss", "dd.MM.yyyy", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd", "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss.FFFFFFF"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? DateTime.SpecifyKind(date, DateTimeKind.Unspecified) : throw Invalid();
    }
    private static decimal? Money(JsonElement value, string name)
    {
        var field = Field(value, name);
        if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || field.ValueKind == JsonValueKind.String && field.GetString()!.Length == 0) return null;
        if (field.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) || !decimal.TryParse(field.ToString(), NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var result) || result < 0) throw Invalid();
        return result;
    }
    private static int RequiredInt(JsonElement value, string name, int minimum, int maximum) => NullableInt(value, name, minimum, maximum) ?? throw Invalid();
    private static int? NullableInt(JsonElement value, string name, int minimum, int maximum)
    {
        var field = Field(value, name);
        if (field.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null || field.ValueKind == JsonValueKind.String && field.GetString()!.Length == 0) return null;
        return field.ValueKind is JsonValueKind.Number or JsonValueKind.String && int.TryParse(field.ToString(), NumberStyles.None, CultureInfo.InvariantCulture,
            out var result) && result >= minimum && result <= maximum ? result : throw Invalid();
    }
    private static bool Flag(JsonElement value, string name) => Field(value, name) switch
    {
        { ValueKind: JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.False } => false,
        { ValueKind: JsonValueKind.True } => true,
        var field when field.ValueKind == JsonValueKind.String && field.GetString() == "0" => false,
        var field when field.ValueKind == JsonValueKind.String && field.GetString() == "1" => true,
        _ => throw Invalid()
    };
    private static InvalidDataException Invalid() => new("Неочікуваний формат списку накладних.");
}

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Globalization;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IShipmentTrackingService
{
    Task<ShipmentTrackingFetchResult> FetchAsync(IReadOnlyList<MarketplaceOrder> orders,
        CancellationToken cancellationToken = default);
}

// Public NP tracking is read-only and accepts an explicitly empty apiKey. The 50-document and
// 4096-byte limits are application bounds, rather than claims about the provider's maximum.
public static class NovaPoshtaTrackingReadOnlyContract
{
    public const int MaximumBatchDocuments = 50;
    public const string PolicyScope = "public-tracking";
    public const string ModelName = "TrackingDocument";
    public const string MethodName = "getStatusDocuments";
    public const string Source = "Нова пошта";

    public static bool ValidNumber(string number) => Regex.IsMatch(number, @"\A[0-9]{14}\z");

    internal static bool IsTrackingModel(HttpRequestMessage request)
    {
        if (request.Content is not StringContent) return false;
        try
        {
            using var body = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return body.RootElement.ValueKind == JsonValueKind.Object &&
                body.RootElement.TryGetProperty("modelName", out var model) &&
                model.ValueKind == JsonValueKind.String && model.GetString() == ModelName;
        }
        catch (JsonException) { return false; }
    }

    public static void ValidateBody(HttpRequestMessage request)
    {
        if (request.Content is not StringContent || request.Content.Headers.ContentType?.MediaType != "application/json" ||
            request.Content.Headers.ContentLength is not (>= 1 and <= 4096)) throw Invalid();
        try
        {
            using var json = JsonDocument.Parse(request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            var body = json.RootElement;
            if (!ExactProperties(body, "apiKey", "modelName", "calledMethod", "methodProperties") ||
                !StringEquals(body.GetProperty("apiKey"), "") || !StringEquals(body.GetProperty("modelName"), ModelName) ||
                !StringEquals(body.GetProperty("calledMethod"), MethodName)) throw Invalid();
            var properties = body.GetProperty("methodProperties");
            if (!ExactProperties(properties, "Documents")) throw Invalid();
            var documents = properties.GetProperty("Documents");
            if (documents.ValueKind != JsonValueKind.Array || documents.GetArrayLength() is < 1 or > MaximumBatchDocuments)
                throw Invalid();
            var numbers = new HashSet<string>(StringComparer.Ordinal);
            foreach (var document in documents.EnumerateArray())
            {
                if (!ExactProperties(document, "DocumentNumber", "Phone") || !StringEquals(document.GetProperty("Phone"), ""))
                    throw Invalid();
                var number = document.GetProperty("DocumentNumber");
                if (number.ValueKind != JsonValueKind.String || !ValidNumber(number.GetString()!) || !numbers.Add(number.GetString()!))
                    throw Invalid();
            }
        }
        catch (JsonException) { throw Invalid(); }
    }

    private static bool StringEquals(JsonElement value, string expected) => value.ValueKind == JsonValueKind.String && value.GetString() == expected;
    private static bool ExactProperties(JsonElement value, params string[] expected)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var names = value.EnumerateObject().Select(p => p.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }
    private static InvalidOperationException Invalid() => new("Дозволено лише читання публічного стану вибраних ТТН Нової пошти.");
}

public sealed class ShipmentTrackingService(ShippingLabelHttp http, TimeProvider? clock = null) : IShipmentTrackingService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ShipmentTrackingFetchResult> FetchAsync(IReadOnlyList<MarketplaceOrder> orders,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var statuses = new List<ShipmentTrackingStatus>();
        var errors = new List<string>();
        var selected = orders.SelectMany(ShippingCarrierNames.ForOrder)
            .Where(shipment => shipment.Carrier == ShippingCarrier.NovaPoshta).Select(shipment => shipment.TrackingNumber)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (selected.Any(number => !NovaPoshtaTrackingReadOnlyContract.ValidNumber(number)))
            errors.Add("Нова пошта: частина ТТН має некоректний формат; їхні стани не перевірено.");
        var numbers = selected.Where(NovaPoshtaTrackingReadOnlyContract.ValidNumber).ToArray();
        foreach (var chunk in numbers.Chunk(NovaPoshtaTrackingReadOnlyContract.MaximumBatchDocuments))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = JsonSerializer.Serialize(new
            {
                apiKey = "", modelName = NovaPoshtaTrackingReadOnlyContract.ModelName,
                calledMethod = NovaPoshtaTrackingReadOnlyContract.MethodName,
                methodProperties = new { Documents = chunk.Select(number => new { DocumentNumber = number, Phone = "" }).ToArray() }
            });
            try
            {
                var bytes = await http.SendAsync(() => new(HttpMethod.Post, NovaPoshtaReadOnlyContract.EntryPoint)
                { Content = new StringContent(body, Encoding.UTF8, "application/json") }, "application/json", cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                // Validate the entire chunk before retaining any result; unexpected/duplicate/missing
                // TTNs never leak an unrelated parcel's status into a selected order.
                var results = Parse(bytes, chunk, _clock.GetUtcNow().ToUniversalTime());
                cancellationToken.ThrowIfCancellationRequested();
                statuses.AddRange(results);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                errors.Add(exception is ShippingLabelException { Kind: LabelFailureKind.Temporary }
                    ? "Нова пошта: перевірка станів тимчасово недоступна. Повторіть оновлення пізніше."
                    : "Нова пошта: не вдалося отримати однозначні стани частини вибраних ТТН.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(statuses.ToArray()), Array.AsReadOnly(errors.Distinct(StringComparer.Ordinal).ToArray()));
    }

    private static IReadOnlyList<ShipmentTrackingStatus> Parse(byte[] bytes, IReadOnlyList<string> requested, DateTimeOffset checkedAt)
    {
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True ||
            !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0 ||
            !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array || data.GetArrayLength() != requested.Count)
            throw new InvalidDataException("Неочікувана відповідь публічного відстеження.");
        var expected = requested.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, ShipmentTrackingStatus>(StringComparer.Ordinal);
        foreach (var row in data.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("Number", out var numberField) || numberField.ValueKind != JsonValueKind.String ||
                !row.TryGetProperty("Status", out var statusField) || statusField.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("Неочікувана відповідь публічного відстеження.");
            var number = numberField.GetString()!;
            var rawStatus = statusField.GetString()!;
            var status = new string(rawStatus.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            if (!expected.Contains(number) || status.Length is < 1 or > 240 || found.ContainsKey(number))
                throw new InvalidDataException("Неоднозначна відповідь публічного відстеження.");
            found.Add(number, new(ShippingCarrier.NovaPoshta, number, status, NovaPoshtaTrackingReadOnlyContract.Source, checkedAt)
            {
                StatusCode = ReadText(row, "StatusCode"),
                ArrivedAt = ReadKyivDateTime(row, "ActualDeliveryDate"),
                PaidStorageStartsOn = ReadDate(row, "DateFirstDayStorage") ?? ReadDate(row, "DatePayedKeeping")
            });
        }
        return requested.Select(number => found[number]).ToArray();
    }

    private static string ReadText(JsonElement row, string field) =>
        row.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= 64
            ? value.GetString()!.Trim() : "";

    private static DateOnly? ReadDate(JsonElement row, string field)
    {
        var text = ReadText(row, field);
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) && date.Year >= 2000) return date;
        return ReadKyivDateTime(row, field) is { } time ? DateRangeBuilder.KyivDate(time) : null;
    }

    private static DateTimeOffset? ReadKyivDateTime(JsonElement row, string field)
    {
        if (!DateTime.TryParseExact(ReadText(row, field), ["yyyy-MM-dd HH:mm:ss", "dd-MM-yyyy HH:mm:ss"],
            CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) || time.Year < 2000 ||
            DateRangeBuilder.KyivZone.IsInvalidTime(time) || DateRangeBuilder.KyivZone.IsAmbiguousTime(time)) return null;
        return new DateTimeOffset(time, DateRangeBuilder.KyivZone.GetUtcOffset(time));
    }
}

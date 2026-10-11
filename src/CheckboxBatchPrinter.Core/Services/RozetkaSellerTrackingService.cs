using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public static class RozetkaSellerTrackingReadOnlyContract
{
    public const string ListPath = "/delivery-rozetka/ttn-list";
    public const int MaximumPages = 10;
    public const int PageSize = 100;
    public const int MaximumRows = MaximumPages * PageSize;

    // This account-wide read is bounded by the selected orders' dates, not one HTTP request per order.
    public static bool ValidRoute(HttpRequestMessage request)
    {
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != "https" || uri.Host != "api-seller.rozetka.com.ua" ||
            !uri.IsDefaultPort || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 ||
            request.Method != HttpMethod.Get || request.Content is not null || uri.AbsolutePath != ListPath ||
            uri.Query.Length is < 1 or > 512) return false;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var segment in uri.Query[1..].Split('&'))
            {
                var pair = segment.Split('=', 2);
                if (pair.Length != 2 || !values.TryAdd(Uri.UnescapeDataString(pair[0]), Uri.UnescapeDataString(pair[1]))) return false;
            }
            return values.Count == 5 && values.Keys.All(k => k is "page" or "pageSize" or "date_from" or "date_to" or "expand") &&
                values.TryGetValue("page", out var pageText) && int.TryParse(pageText, NumberStyles.None, CultureInfo.InvariantCulture, out var page) &&
                page is >= 1 and <= MaximumPages && values.TryGetValue("pageSize", out var size) && size == "100" &&
                values.TryGetValue("expand", out var expand) && expand == "delivery_status_title" &&
                values.TryGetValue("date_from", out var fromText) && DateOnly.TryParseExact(fromText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var from) &&
                values.TryGetValue("date_to", out var toText) && DateOnly.TryParseExact(toText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var to) &&
                from <= to && to.DayNumber - from.DayNumber <= 3650;
        }
        catch (UriFormatException) { return false; }
    }
}

/// <summary>Reads existing delivery statuses through the selected orders' Rozetka Seller accounts.</summary>
public sealed class RozetkaSellerTrackingService(ShippingLabelHttp http, IMarketplaceSettingsStore settings,
    IMarketplaceSecretStore secrets, TimeProvider? timeProvider = null) : IShipmentTrackingService
{
    private const string Host = "https://api-seller.rozetka.com.ua";
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;

    public async Task<ShipmentTrackingFetchResult> FetchAsync(IReadOnlyList<MarketplaceOrder> orders, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var statuses = new List<ShipmentTrackingStatus>();
        var errors = new List<string>();
        var candidates = orders.Where(o => o.Key.Marketplace == MarketplaceKind.Rozetka &&
            RozetkaSellerDeliveryReadOnlyContract.ValidOrderId(o.Key.OrderId))
            .SelectMany(o => ShippingCarrierNames.ForOrder(o).Where(s => s.Carrier == ShippingCarrier.RozetkaDelivery &&
                RozetkaSellerDeliveryReadOnlyContract.ValidPrimary(s.TrackingNumber)).Select(s => new Target(o, s)))
            .DistinctBy(t => (t.Order.Key, t.Shipment.TrackingNumber)).ToArray();
        if (candidates.Length == 0) return new([], []);
        if (candidates.Length > RozetkaSellerTrackingReadOnlyContract.MaximumRows)
            return new([], ["Rozetka Seller: надто багато вибраних накладних для одного оновлення статусів."]);

        MarketplaceSettings config;
        try { config = await settings.LoadAsync(cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return new([], ["Rozetka Seller: не вдалося прочитати налаштування підключень."]); }
        var groups = new Dictionary<(string Login, string Password), List<Target>>();
        var today = DateRangeBuilder.KyivDate(_clock.GetUtcNow());
        foreach (var connectionTargets in candidates.GroupBy(t => t.Order.Key.ConnectionId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var datedTargets = connectionTargets.Where(t => t.Order.CreatedAt is { } created &&
                DateRangeBuilder.KyivDate(created) <= today && today.DayNumber - DateRangeBuilder.KyivDate(created).DayNumber <= 3650).ToArray();
            if (datedTargets.Length != connectionTargets.Count())
                errors.Add("Rozetka Seller: дата частини замовлень невідома; їхні статуси доставки не запитувалися.");
            if (datedTargets.Length == 0) continue;
            if (config.Connections.Count(c => c.Id == connectionTargets.Key && c.Enabled && c.Marketplace == MarketplaceKind.Rozetka) != 1)
            { errors.Add("Rozetka Seller: підключення замовлення вимкнене або недоступне."); continue; }
            MarketplaceCredentials? credentials;
            try { credentials = await secrets.LoadAsync(connectionTargets.Key, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { errors.Add("Rozetka Seller: не вдалося прочитати облікові дані."); continue; }
            if (credentials is null || string.IsNullOrWhiteSpace(credentials.Login) || credentials.Password.Length == 0 ||
                credentials.Login.Any(char.IsControl) || credentials.Password.Any(char.IsControl))
            { errors.Add("Rozetka Seller: збережіть логін і пароль продавця для статусів доставки."); continue; }
            var identity = (credentials.Login.Trim(), credentials.Password);
            if (!groups.TryGetValue(identity, out var targets)) groups[identity] = targets = [];
            targets.AddRange(datedTargets);
        }

        // Independent accounts may respond concurrently; aliases of one account share one auth/list scan.
        var results = await Task.WhenAll(groups.Select(async group =>
        {
            try { return new ShipmentTrackingFetchResult(await ReadGroupAsync(group.Key.Login, group.Key.Password, group.Value, cancellationToken).ConfigureAwait(false), []); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { return new ShipmentTrackingFetchResult([], ["Rozetka Seller: статуси доставки цього підключення недоступні; попередні дані збережено."]); }
        })).ConfigureAwait(false);
        foreach (var result in results) { statuses.AddRange(result.Statuses); errors.AddRange(result.Errors); }
        cancellationToken.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(statuses.ToArray()), Array.AsReadOnly(errors.Distinct(StringComparer.Ordinal).ToArray()));
    }

    private async Task<IReadOnlyList<ShipmentTrackingStatus>> ReadGroupAsync(string login, string password, IReadOnlyList<Target> targets,
        CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        var to = DateRangeBuilder.KyivDate(now);
        // Missing/future dates cannot justify an unbounded account scan.
        if (targets.Any(t => t.Order.CreatedAt is null)) throw Invalid();
        var from = targets.Min(t => DateRangeBuilder.KyivDate(t.Order.CreatedAt!.Value));
        if (from > to || to.DayNumber - from.DayNumber > 3650) throw Invalid();
        var body = JsonSerializer.Serialize(new { username = login, password = Convert.ToBase64String(Encoding.UTF8.GetBytes(password)) });
        using var auth = await JsonAsync(() => new(HttpMethod.Post, Host + "/sites")
            { Content = new StringContent(body, Encoding.UTF8, "application/json") }, cancellationToken).ConfigureAwait(false);
        CheckSuccess(auth.RootElement);
        var authContent = Field(auth.RootElement, "content");
        var token = Text(authContent, "access_token");
        var permissions = Field(authContent, "permissions");
        if (string.IsNullOrWhiteSpace(token) || token.Any(char.IsControl) || permissions.ValueKind != JsonValueKind.Array ||
            !permissions.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "module_octopus")) throw Invalid();

        var requested = targets.GroupBy(t => (t.Order.Key.OrderId, t.Shipment.TrackingNumber)).ToDictionary(g => g.Key, g => g.ToArray());
        var requestedNumbers = targets.Select(t => t.Shipment.TrackingNumber).ToHashSet(StringComparer.Ordinal);
        var matched = new Dictionary<(string Order, string Tracking), string>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var seenPairs = new HashSet<(string Order, string Tracking)>();
        int? expectedTotal = null, expectedPages = null, expectedSize = null;
        for (var page = 1; page <= RozetkaSellerTrackingReadOnlyContract.MaximumPages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = RozetkaSellerTrackingReadOnlyContract.ListPath + "?page=" + page.ToString(CultureInfo.InvariantCulture) +
                "&pageSize=100&date_from=" + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) +
                "&date_to=" + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "&expand=delivery_status_title";
            using var response = await JsonAsync(() => Request(path, token), cancellationToken).ConfigureAwait(false);
            CheckSuccess(response.RootElement);
            var content = Field(response.RootElement, "content");
            var models = Field(content, "models");
            var meta = Field(content, "_meta");
            var current = Integer(meta, "currentPage", 1, RozetkaSellerTrackingReadOnlyContract.MaximumPages);
            var pageCount = Integer(meta, "pageCount", 0, RozetkaSellerTrackingReadOnlyContract.MaximumPages);
            var total = Integer(meta, "totalCount", 0, RozetkaSellerTrackingReadOnlyContract.MaximumRows);
            var size = Integer(meta, "perPage", 1, RozetkaSellerTrackingReadOnlyContract.PageSize);
            if (models.ValueKind != JsonValueKind.Array || current != page || models.GetArrayLength() > size ||
                (pageCount != (total == 0 ? 0 : (total + size - 1) / size) && !(total == 0 && pageCount == 1)) ||
                page > Math.Max(pageCount, 1) || expectedTotal is { } previousTotal && previousTotal != total ||
                expectedPages is { } previousPages && previousPages != pageCount || expectedSize is { } previousSize && previousSize != size)
                throw Invalid();
            expectedTotal = total; expectedPages = pageCount; expectedSize = size;
            foreach (var model in models.EnumerateArray())
            {
                var id = Text(model, "id");
                var orderId = Text(model, "order_id");
                var number = Text(model, "ttn");
                if (!RozetkaSellerDeliveryReadOnlyContract.ValidOrderId(id) || !RozetkaSellerDeliveryReadOnlyContract.ValidOrderId(orderId) ||
                    !Regex.IsMatch(number, @"\A[0-9A-Za-z-]{1,80}\z") || !seenIds.Add(id) || !seenPairs.Add((orderId, number))) throw Invalid();
                if (!requested.TryGetValue((orderId, number), out _))
                {
                    // A matching TTN attached to another order contradicts the requested identity.
                    if (requestedNumbers.Contains(number)) throw Invalid();
                    continue;
                }
                var statusField = Field(model, "delivery_status_title");
                if (statusField.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) continue;
                if (statusField.ValueKind != JsonValueKind.String) throw Invalid();
                var status = statusField.GetString()!.Trim();
                if (status.Length > 500 || status.Any(char.IsControl)) throw Invalid();
                if (status.Length > 0) matched.Add((orderId, number), status);
            }
            if (page >= Math.Max(pageCount, 1)) break;
            if (models.GetArrayLength() == 0) throw Invalid();
        }
        // Publish only a completed account scan; contradictory or missing pages cannot refresh old statuses.
        if (seenIds.Count != expectedTotal) throw Invalid();
        var checkedAt = _clock.GetUtcNow();
        return Array.AsReadOnly(matched.SelectMany(pair => requested[pair.Key].Select(target =>
            new ShipmentTrackingStatus(ShippingCarrier.RozetkaDelivery, target.Shipment.TrackingNumber,
                pair.Value, "Rozetka Seller", checkedAt, target.Shipment.ShipmentId) { Order = target.Order.Key })).ToArray());
    }

    private async Task<JsonDocument> JsonAsync(Func<HttpRequestMessage> request, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await http.SendAsync(request, "application/json", cancellationToken).ConfigureAwait(false));
    private static HttpRequestMessage Request(string path, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, Host + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("Content-Language", "uk");
        return request;
    }
    private static void CheckSuccess(JsonElement root)
    { if (Field(root, "success").ValueKind != JsonValueKind.True) throw Invalid(); }
    private static int Integer(JsonElement value, string name, int minimum, int maximum)
    {
        var text = Text(value, name);
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var result) || result < minimum || result > maximum) throw Invalid();
        return result;
    }
    private static JsonElement Field(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var field) ? field : default;
    private static string Text(JsonElement value, string name) =>
        Field(value, name).ValueKind is JsonValueKind.String or JsonValueKind.Number ? Field(value, name).ToString() : "";
    private static InvalidDataException Invalid() => new("Некоректні або неповні дані статусів Rozetka Seller.");
    private sealed record Target(MarketplaceOrder Order, ShipmentReference Shipment);
}

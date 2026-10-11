using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class WaybillServiceTests
{
    private static readonly DateOnly Day = new(2026, 10, 9);
    private const string Key = "synthetic_waybill_np_key", Token = "synthetic_waybill_rd_token";
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("all waybills enumerate NP beyond ten pages without any marketplace orders or bindings", NovaPoshtaPagination),
        ("all waybills NP verified stable total avoids an unnecessary next-page request", NovaPoshtaTotals),
        ("all waybills NP invalid totals, overlap and changing counts preserve an incomplete source", NovaPoshtaBadTotals),
        ("all waybills query every NP account and split inclusive ranges into seven-day windows", AccountsAndPeriods),
        ("all waybills NP aliases sharing one key enumerate one account only", AccountAliases),
        ("all waybills report absent credentials without sending requests", MissingCredentials),
        ("all waybills preserve earlier pages and other carrier accounts after a sanitized source failure", PartialFailure),
        ("all waybills repeated NP pages and page limits visibly mark incomplete sources", NovaPoshtaIncomplete),
        ("all waybills RD reads all archived and deleted states with both partner types and verified pagination", DeliveryPagination),
        ("all waybills RD empty lists accept zero or one reported page without another request", DeliveryEmpty),
        ("all waybills RD inconsistent totals, overlapping or repeated pages never claim completeness", DeliveryIncomplete),
        ("all waybills retain one carrier document across overlapping state and date windows", DeliveryDeduplication),
        ("all waybills offset timestamps use Kyiv dates and out-of-range dates are excluded", DateOffsets),
        ("all waybills reject malformed identity metadata without showing response secrets", InvalidData),
        ("all waybills cancellation propagates before and during carrier reads", Cancellation),
        ("all waybills start NovaPoshta and Delivery in parallel while provider pagination and failure statuses stay independent", ParallelCarriers),
        ("all waybills cancellation reaches both already-started carriers without requesting more pages", ParallelCarrierCancellation)
    ];

    private sealed class Store(ShippingLabelSettings value) : IShippingSettingsStore
    {
        public Task<ShippingLabelSettings> LoadAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(value); }
        public Task SaveAsync(ShippingLabelSettings _, CancellationToken ct = default) => throw new Exception("Waybill enumeration attempted to change settings.");
    }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> read) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return read(request, ct); }
    }
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Np(params object[] rows) => Json(new { success = true, data = rows, errors = Array.Empty<string>() });
    private static HttpResponseMessage NpTotal(object[] rows, int total) => Json(new
    { success = true, data = rows, errors = Array.Empty<string>(), info = new { totalCount = total } });
    private static HttpResponseMessage Rd(object[] rows, int total, int page = 1) => Json(new
    { statusCode = 0, data = rows, pagination = new { total_count = total, page_count = (total + 99) / 100, page, limit = 100 } });
    private static object NpRow(int n, string date = "09.10.2026 12:32:01") => new
    {
        IntDocNumber = (20400000000000L + n).ToString(), Ref = new Guid(n, 0, 0, new byte[8]).ToString(),
        DateTime = date, RecipientContactPerson = "Одержувач", CityRecipientDescription = "Київ", Cost = "125.50", SeatsAmount = "2", DeletionMark = "0"
    };
    private static object RdRow(string id, bool archived = false, bool deleted = false, string date = "2026-10-09 13:20:21") => new
    {
        id, created_at = date, last_status_name = "Створено", places = 1, insurance_cost = 210m, is_archived = archived, is_deleted = deleted,
        recipient = new { first_name = "Ім'я", last_name = "Прізвище", middle_name = "По батькові", department = new { name = "Київ", public_name = "Київ, відділення" } }
    };
    private static Dictionary<string, string[]> Query(HttpRequestMessage request) => request.RequestUri!.Query.TrimStart('?').Split('&')
        .Select(s => s.Split('=', 2)).GroupBy(s => Uri.UnescapeDataString(s[0]), StringComparer.Ordinal)
        .ToDictionary(g => g.Key, g => g.Select(s => Uri.UnescapeDataString(s[1])).ToArray(), StringComparer.Ordinal);
    private static (string Key, int Page, string From, string To) NpQuery(HttpRequestMessage request)
    {
        using var json = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        var root = json.RootElement; var properties = root.GetProperty("methodProperties");
        Check(root.GetProperty("modelName").GetString() == "InternetDocument" && root.GetProperty("calledMethod").GetString() == "getDocumentList" &&
            !properties.GetProperty("GetFullList").GetBoolean(), "An unbounded or writing NP method was used.");
        return (root.GetProperty("apiKey").GetString()!, int.Parse(properties.GetProperty("Page").GetString()!),
            properties.GetProperty("DateTimeFrom").GetString()!, properties.GetProperty("DateTimeTo").GetString()!);
    }
    private static ShippingLabelSettings NpSettings(params NovaPoshtaConnection[] connections) => new()
    { NovaPoshtaConnections = connections.Length == 0 ? [new("np", "НП акаунт", Key)] : connections, NovaPoshtaStoreBindings = [] };
    private static WaybillService Service(HttpClient client, ShippingLabelSettings config) =>
        new(NovaPoshtaRequestPolicyTests.FastTransport(client), new Store(config));
    private static void NoSecrets(WaybillFetchResult result) => Check(!string.Join("|", result.Sources.Select(s => s.Message)).Contains(Key) &&
        !string.Join("|", result.Sources.Select(s => s.Message)).Contains(Token), "A source message exposed credentials.");

    private static async Task NovaPoshtaPagination()
    {
        var pages = new List<int>();
        var handler = new Handler((request, _) =>
        {
            var query = NpQuery(request); pages.Add(query.Page);
            Check(query.Key == Key && query.From == "09.10.2026" && query.To == query.From, "Wrong account or period.");
            return Task.FromResult(query.Page <= 12 ? Np(NpRow(query.Page)) : Np());
        });
        using var client = new HttpClient(handler);
        var result = await Service(client, NpSettings()).FetchAsync(Day, Day);
        Check(pages.SequenceEqual(Enumerable.Range(1, 13)) && result.Documents.Count == 12 && result.Sources[0].Complete, "Full-account list stopped at the legacy lookup limit.");
        var first = result.Documents[0];
        Check(first.Carrier == ShippingCarrier.NovaPoshta && first.Recipient == "Одержувач" && first.City == "Київ" && first.DeclaredValue == 125.50m && first.Places == 2 &&
            first.CreatedLocal == new DateTime(2026, 10, 9, 12, 32, 1) && !first.IsDeleted, "NP metadata mapping differs.");
        Check(NovaPoshtaReadOnlyContract.MaximumPages == 10, "Dashboard changed single-label lookup bounds.");
    }
    private static async Task AccountsAndPeriods()
    {
        var calls = new List<(string Key, int Page, string From, string To)>();
        var handler = new Handler((request, _) =>
        {
            var q = NpQuery(request); calls.Add(q);
            return Task.FromResult(q.Page == 1 ? Np(NpRow(1, q.From + " 10:00:00")) : Np());
        });
        using var client = new HttpClient(handler);
        var config = NpSettings(new("first", "Перший", Key), new("second", "Другий", "synthetic_other_np_key"));
        var result = await Service(client, config).FetchAsync(new(2026, 10, 1), new(2026, 10, 8));
        Check(calls.Count == 8 && calls.Any(q => q.From == "01.10.2026" && q.To == "07.10.2026") && calls.Any(q => q.From == "08.10.2026" && q.To == q.From), "Inclusive seven-day slices dropped the last day.");
        Check(result.Documents.Count == 2 && result.Documents.Select(r => r.ConnectionId).Distinct().Count() == 2 && result.Sources.Take(2).All(s => s.Complete),
            "Different carrier accounts were deduplicated together or required marketplace bindings.");
    }
    private static async Task NovaPoshtaTotals()
    {
        foreach (var total in new[] { 0, 2, 16 })
        {
            var handler = new Handler((request, _) =>
            {
                var q = NpQuery(request);
                if (total is 0 or 16)
                {
                    Check(q.Page == 1, "A completed NP total still requested a second page.");
                    return Task.FromResult(NpTotal(Enumerable.Range(1, total).Select(n => NpRow(n)).ToArray(), total));
                }
                Check(q.Page <= 2, "NP requested past the stable total.");
                return Task.FromResult(NpTotal([NpRow(q.Page)], 2));
            });
            using var client = new HttpClient(handler);
            var result = await Service(client, NpSettings()).FetchAsync(Day, Day);
            Check(result.Sources[0].Complete && result.Documents.Count == total && handler.Calls == (total == 2 ? 2 : 1),
                "Verified stable total did not prove list completion.");
        }
    }
    private static async Task NovaPoshtaBadTotals()
    {
        foreach (var failure in new[] { "negative", "exceeded", "missing-page", "changed", "duplicate", "overlap" })
        {
            var handler = new Handler((request, _) =>
            {
                var q = NpQuery(request);
                return Task.FromResult(failure switch
                {
                    "negative" => NpTotal([], -1),
                    "exceeded" => NpTotal([NpRow(1), NpRow(2)], 1),
                    "missing-page" => q.Page == 1 ? NpTotal([NpRow(1)], 2) : NpTotal([], 2),
                    "changed" => NpTotal([NpRow(q.Page)], q.Page == 1 ? 2 : 3),
                    "duplicate" => NpTotal([NpRow(1), NpRow(1)], 2),
                    "overlap" => q.Page == 1 ? NpTotal([NpRow(1), NpRow(2)], 4) : NpTotal([NpRow(2), NpRow(3)], 4),
                    _ => throw new Exception("Unknown fixture.")
                });
            });
            using var client = new HttpClient(handler);
            var result = await Service(client, NpSettings()).FetchAsync(Day, Day);
            Check(!result.Sources[0].Complete && handler.Calls <= 2, "Unstable or contradictory NP total claimed completion or kept paging.");
            if (failure is "missing-page" or "changed" or "overlap")
                Check(result.Documents.Count == (failure == "overlap" ? 2 : 1), "Count mismatch discarded a validated earlier NP page.");
            NoSecrets(result);
        }
    }
    private static async Task MissingCredentials()
    {
        var handler = new Handler((_, _) => throw new Exception("Credentials missing but HTTP was attempted."));
        using var client = new HttpClient(handler);
        foreach (var config in new[] { new ShippingLabelSettings(), NpSettings(new NovaPoshtaConnection("empty", "Без ключа", "")) })
        {
            var result = await Service(client, config).FetchAsync(Day, Day);
            Check(result.Documents.Count == 0 && result.Sources.Count == 2 && result.Sources.All(s => !s.Complete && s.Count == 0), "Missing credentials were reported as a complete empty carrier.");
        }
        Check(handler.Calls == 0, "Missing keys triggered a carrier request.");
    }
    private static async Task AccountAliases()
    {
        var handler = new Handler((request, _) => Task.FromResult(NpQuery(request).Page == 1 ? Np(NpRow(1)) : Np()));
        using var client = new HttpClient(handler);
        var result = await Service(client, NpSettings(new("first", "Перший", Key), new("alias", "Інший магазин", Key))).FetchAsync(Day, Day);
        Check(result.Documents.Count == 1 && result.Documents[0].ConnectionId == "first" && handler.Calls == 2 && result.Sources.Count == 2,
            "Same-key store aliases duplicated account documents or scanned twice.");
    }
    private static async Task PartialFailure()
    {
        var config = NpSettings(new("broken", "Перший", Key), new("good", "Другий", "synthetic_other_np_key")) with { RozetkaDeliveryToken = Token };
        var handler = new Handler((request, _) =>
        {
            if (request.Method == HttpMethod.Post)
            {
                var q = NpQuery(request);
                return Task.FromResult(q.Key == Key ? q.Page == 1 ? Np(NpRow(1)) : new(HttpStatusCode.Forbidden)
                    { Content = new StringContent(Key + " carrier private response") } : q.Page == 1 ? Np(NpRow(2)) : Np());
            }
            var query = Query(request);
            return Task.FromResult(query["is_archived"][0] == "false" && query["is_deleted"][0] == "false" ? Rd([RdRow("RD-001")], 1) : Rd([], 0));
        });
        using var client = new HttpClient(handler);
        var result = await Service(client, config).FetchAsync(Day, Day);
        Check(result.Documents.Count == 3 && !result.Sources[0].Complete && result.Sources[0].Count == 1 && result.Sources[1].Complete && result.Sources[2].Complete,
            "A failed account discarded its earlier page or blocked another account/carrier.");
        NoSecrets(result);
    }
    private static async Task NovaPoshtaIncomplete()
    {
        foreach (var repeat in new[] { true, false })
        {
            var handler = new Handler((request, _) => Task.FromResult(Np(NpRow(repeat ? 1 : NpQuery(request).Page))));
            using var client = new HttpClient(handler);
            var result = await Service(client, NpSettings()).FetchAsync(Day, Day);
            Check(!result.Sources[0].Complete && result.Documents.Count == (repeat ? 1 : NovaPoshtaReadOnlyContract.MaximumListPages) &&
                handler.Calls == (repeat ? 2 : NovaPoshtaReadOnlyContract.MaximumListPages), "Repeated or limit-truncated NP pages claimed completeness.");
        }
    }
    private static async Task DeliveryPagination()
    {
        var calls = new List<(bool Archived, bool Deleted, int Page)>();
        var handler = new Handler((request, _) =>
        {
            var q = Query(request); var page = int.Parse(q["page"][0]); var archived = q["is_archived"][0] == "true"; var deleted = q["is_deleted"][0] == "true";
            calls.Add((archived, deleted, page));
            Check(request.Headers.Authorization?.Parameter == Token && q["partner_type"].SequenceEqual(new[] { "rz-delivery", "rozetka-marketplace" }) &&
                q["created_date_start"][0] == "2026-10-09" && q["created_date_end"][0] == "2026-10-09" && q["sort"][0] == "DESC" && q["limit"][0] == "100", "RD query omitted a partner, state, token or selected date.");
            return Task.FromResult(!archived && !deleted ? Rd(page == 1 ? Enumerable.Range(1, 100).Select(n => RdRow("RD-" + n)).ToArray() : [RdRow("RD-101")], 101, page)
                : Rd([RdRow($"RD-{archived}-{deleted}", archived, deleted)], 1));
        });
        using var client = new HttpClient(handler);
        var result = await Service(client, new() { RozetkaDeliveryToken = Token, RozetkaDeliveryConnectionId = "rd" }).FetchAsync(Day, Day);
        Check(calls.Count == 5 && result.Documents.Count == 104 && result.Sources[1].Complete && result.Documents.Count(d => d.IsDeleted) == 2, "RD pages or archived/deleted combinations were lost.");
        var active = result.Documents.First(d => d.TrackingNumber == "RD-1");
        Check(active.DocumentId == "RD-1" && active.CreatedLocal == new DateTime(2026, 10, 9, 13, 20, 21) && active.Recipient == "Прізвище Ім'я По батькові" &&
            active.City == "Київ, відділення" && active.DeclaredValue == 210m && active.Places == 1, "RD display metadata differs.");
        Check(result.Documents.Any(d => d.Status.Contains("Архів")) && result.Documents.Where(d => d.IsDeleted).All(d => d.Status.Contains("Видалено")), "Archived/deleted rows look active.");
    }
    private static async Task DeliveryIncomplete()
    {
        foreach (var failure in new[] { "count", "repeat", "overlap", "changed-total" })
        {
            var handler = new Handler((request, _) =>
            {
                var page = int.Parse(Query(request)["page"][0]);
                if (failure == "count") return Task.FromResult(Rd([RdRow("RD-1")], 2));
                var rows = page == 1 || failure == "repeat" ? Enumerable.Range(1, 100).Select(n => RdRow("RD-" + n)).ToArray()
                    : new[] { RdRow(failure == "overlap" ? "RD-1" : "RD-101") };
                return Task.FromResult(Rd(rows, failure == "changed-total" && page == 2 ? 102 : 101, page));
            });
            using var client = new HttpClient(handler);
            var result = await Service(client, new() { RozetkaDeliveryToken = Token }).FetchAsync(Day, Day);
            Check(!result.Sources[1].Complete && result.Documents.Count > 0 && handler.Calls <= 2, "Incomplete RD metadata or overlapping pages claimed a complete list.");
        }
    }
    private static async Task DeliveryEmpty()
    {
        foreach (var pages in new[] { 0, 1 })
        {
            var handler = new Handler((request, _) =>
            {
                Check(Query(request)["page"][0] == "1", "Empty RD response still requested another page.");
                return Task.FromResult(Json(new { statusCode = 0, data = Array.Empty<object>(),
                    pagination = new { total_count = 0, page_count = pages, page = 1, limit = 100 } }));
            });
            using var client = new HttpClient(handler);
            var result = await Service(client, new() { RozetkaDeliveryToken = Token }).FetchAsync(Day, Day);
            Check(result.Documents.Count == 0 && result.Sources[1].Complete && handler.Calls == 4, "Empty RD pagination convention rejected an otherwise complete zero-record list.");
        }
    }
    private static async Task DeliveryDeduplication()
    {
        var handler = new Handler((_, _) => Task.FromResult(Rd([RdRow("RD-1", date: "2026-10-08 12:30:00")], 1)));
        using var client = new HttpClient(handler);
        var result = await Service(client, new() { RozetkaDeliveryToken = Token }).FetchAsync(new(2026, 10, 1), new(2026, 10, 8));
        Check(handler.Calls == 8 && result.Documents.Count == 1 && result.Sources[1].Complete && result.Sources[1].Count == 1, "One source document duplicated across date/state windows.");
    }
    private static async Task InvalidData()
    {
        foreach (var row in new object[] { new { IntDocNumber = Key }, new { IntDocNumber = "20400000000001", Ref = Token },
            new { IntDocNumber = "20400000000001", DeletionMark = "unexpected" } })
        {
            var handler = new Handler((_, _) => Task.FromResult(Np(row)));
            using var client = new HttpClient(handler);
            var result = await Service(client, NpSettings()).FetchAsync(Day, Day);
            Check(result.Documents.Count == 0 && !result.Sources[0].Complete, "Invalid carrier identities entered the dashboard.");
            NoSecrets(result);
        }
    }
    private static async Task DateOffsets()
    {
        var handler = new Handler((_, _) => Task.FromResult(Rd([
            RdRow("RD-1", date: "2026-10-08T21:15:01.1234567Z"),
            RdRow("RD-2", date: "2026-10-09T12:00:00+03:00"),
            RdRow("RD-3", date: "2026-10-10 10:00:00")], 3)));
        using var client = new HttpClient(handler);
        var result = await Service(client, new() { RozetkaDeliveryToken = Token }).FetchAsync(Day, Day);
        Check(result.Sources[1].Complete && result.Documents.Count == 2 &&
            result.Documents.Single(d => d.TrackingNumber == "RD-1").CreatedLocal == new DateTime(2026, 10, 9, 0, 15, 1).AddTicks(1234567) &&
            result.Documents.Single(d => d.TrackingNumber == "RD-2").CreatedLocal == new DateTime(2026, 10, 9, 12, 0, 0),
            "UTC timestamps shifted the selected Kyiv date or an outside-range document appeared.");
    }
    private static async Task ParallelCarriers()
    {
        foreach (var failNovaPoshta in new[] { false, true })
        {
            var npEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var npActive = 0; var deliveryActive = 0; var npMaximum = 0; var deliveryMaximum = 0;
            var handler = new Handler(async (request, ct) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    npMaximum = Math.Max(npMaximum, Interlocked.Increment(ref npActive));
                    try
                    {
                        npEntered.TrySetResult(); await release.Task.WaitAsync(ct);
                        Check(NpQuery(request).Page == 1, "A verified NP total triggered another page.");
                        return failNovaPoshta ? new(HttpStatusCode.Forbidden) { Content = new StringContent(Key) }
                            : NpTotal([NpRow(1)], 1);
                    }
                    finally { Interlocked.Decrement(ref npActive); }
                }
                deliveryMaximum = Math.Max(deliveryMaximum, Interlocked.Increment(ref deliveryActive));
                try
                {
                    deliveryEntered.TrySetResult(); await release.Task.WaitAsync(ct);
                    var q = Query(request);
                    var active = q["is_archived"][0] == "false" && q["is_deleted"][0] == "false";
                    return active ? Rd([RdRow("RD-parallel")], 1) : Rd([], 0);
                }
                finally { Interlocked.Decrement(ref deliveryActive); }
            });
            using var client = new HttpClient(handler);
            var refreshing = Service(client, NpSettings() with { RozetkaDeliveryToken = Token }).FetchAsync(Day, Day);
            try
            {
                await Task.WhenAll(npEntered.Task, deliveryEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
                Check(handler.Calls == 2 && !refreshing.IsCompleted,
                    "A carrier waited for another provider's result before sending its first request.");
                release.TrySetResult();
                var result = await refreshing.WaitAsync(TimeSpan.FromSeconds(5));
                Check(handler.Calls == 5 && npMaximum == 1 && deliveryMaximum == 1,
                    "Provider pages ran concurrently or a completed page was requested again.");
                Check(result.Sources[0].Carrier == ShippingCarrier.NovaPoshta && result.Sources[0].Complete == !failNovaPoshta &&
                    result.Sources[1].Carrier == ShippingCarrier.RozetkaDelivery && result.Sources[1].Complete &&
                    result.Documents.Count == (failNovaPoshta ? 1 : 2), "Provider failure lost independent results or changed status ordering.");
                NoSecrets(result);
            }
            finally { release.TrySetResult(); await refreshing; }
        }
    }

    private static async Task ParallelCarrierCancellation()
    {
        var npEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveryEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken npToken = default, deliveryToken = default;
        var handler = new Handler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Post) { npToken = token; npEntered.TrySetResult(); }
            else { deliveryToken = token; deliveryEntered.TrySetResult(); }
            await release.Task.WaitAsync(token);
            return request.Method == HttpMethod.Post ? Np() : Rd([], 0);
        });
        using var client = new HttpClient(handler); using var cancellation = new CancellationTokenSource();
        var refreshing = Service(client, NpSettings() with { RozetkaDeliveryToken = Token }).FetchAsync(Day, Day, cancellation.Token);
        try
        {
            await Task.WhenAll(npEntered.Task, deliveryEntered.Task).WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            try { await refreshing.WaitAsync(TimeSpan.FromSeconds(5)); throw new Exception("Parallel carrier cancellation ignored."); }
            catch (OperationCanceledException) { }
            Check(npToken.IsCancellationRequested && deliveryToken.IsCancellationRequested && handler.Calls == 2,
                "Cancellation did not stop every started carrier or allowed another page request.");
        }
        finally
        {
            cancellation.Cancel(); release.TrySetResult();
            try { await refreshing; } catch (OperationCanceledException) { }
        }
    }

    private static async Task Cancellation()
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var handler = new Handler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Np(); });
        using var client = new HttpClient(handler);
        var service = Service(client, NpSettings());
        try { await service.FetchAsync(Day, Day, cancel.Token); throw new Exception("Pre-cancellation ignored."); }
        catch (OperationCanceledException) { }
        Check(handler.Calls == 0, "A cancelled refresh sent HTTP.");
        using var running = new CancellationTokenSource();
        var read = service.FetchAsync(Day, Day, running.Token); running.Cancel();
        try { await read; throw new Exception("In-flight cancellation ignored."); }
        catch (OperationCanceledException) { }
        Check(handler.Calls == 1, "Cancelled source continued to another carrier.");
    }
}

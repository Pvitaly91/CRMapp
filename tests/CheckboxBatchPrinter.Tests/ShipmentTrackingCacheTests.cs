using System.IO;
using System.Text;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class ShipmentTrackingCacheTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);
    private const string Context = "synthetic-tracking-account";
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("tracking cache: encrypted restart preserves storage dates and attempt cooldown metadata", Restart),
        ("tracking cache: account contexts stay isolated and wrong-context writes preserve previous cache", ContextIsolation),
        ("tracking cache: latest entries win while historical and distinct shipment identities survive", MergeAndDeduplicate),
        ("tracking cache: corrupt optional cache does not block reads or overwrite prior bytes", Corruption),
        ("tracking cache: invalid metadata and canceled writes preserve the last valid cache", ValidationAndCancellation),
        ("tracking cache: concurrent store instances serialize updates without losing statuses", ConcurrentWrites)
    ];

    private static OrderKey Order(int id = 1) => new(MarketplaceKind.Prom, "f8000000000000000000000000000001", id.ToString());
    private static ShipmentTrackingStatus Status(int id = 1, DateTimeOffset? checkedAt = null) =>
        new(ShippingCarrier.NovaPoshta, $"2045000000{id:D4}", "Прибув у відділення", "Нова пошта", checkedAt ?? Now, $"document-{id}")
        {
            Order = Order(id), StatusCode = "7", ArrivedAt = Now.AddDays(-5), PaidStorageStartsOn = new(2026, 10, 20)
        };
    private static ShipmentTrackingCacheAttempt Attempt(int id = 1, DateTimeOffset? attemptedAt = null) =>
        new(ShippingCarrier.NovaPoshta, $"2045000000{id:D4}", Order(id), $"document-{id}", attemptedAt ?? Now);
    private static ShipmentTrackingCache Cache(IReadOnlyList<ShipmentTrackingStatus>? statuses = null,
        IReadOnlyList<ShipmentTrackingCacheAttempt>? attempts = null, string context = Context) =>
        new() { AccountContext = context, Statuses = statuses ?? [Status()], Attempts = attempts ?? [Attempt()] };
    private static string DirectoryPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "checkbox-tracking-cache-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
    private static void Eq<T>(T expected, T actual)
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}."); }
    private static void Yes(bool value) { if (!value) throw new Exception("Tracking cache assertion failed."); }

    private static async Task Restart()
    {
        var directory = DirectoryPath();
        try
        {
            var path = Path.Combine(directory, "tracking.dpapi");
            var status = Status() with { Carrier = ShippingCarrier.RozetkaDelivery, TrackingNumber = "RMP-12345678", Source = "Rozetka Seller" };
            var attempt = Attempt() with { Carrier = status.Carrier, TrackingNumber = status.TrackingNumber };
            await new DpapiShipmentTrackingCacheStore(path).SaveAsync(Context, Cache([status], [attempt]));
            var clear = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path));
            Yes(!clear.Contains(Context) && !clear.Contains(status.TrackingNumber) && !clear.Contains(status.Status));
            var loaded = await new DpapiShipmentTrackingCacheStore(path).LoadAsync(Context);
            Eq(1, loaded.Schema); Eq(Context, loaded.AccountContext);
            Eq(status, loaded.Statuses.Single()); Eq(attempt, loaded.Attempts.Single());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task ContextIsolation()
    {
        var directory = DirectoryPath();
        try
        {
            var path = Path.Combine(directory, "tracking.dpapi");
            var store = new DpapiShipmentTrackingCacheStore(path);
            await store.SaveAsync(Context, Cache());
            var other = await store.LoadAsync("other-context");
            Eq(0, other.Statuses.Count); Eq(0, other.Attempts.Count); Eq("other-context", other.AccountContext);
            var bytes = await File.ReadAllBytesAsync(path);
            try { await store.SaveAsync("other-context", Cache()); throw new Exception("Wrong context was accepted."); }
            catch (ArgumentException) { }
            Yes(bytes.SequenceEqual(await File.ReadAllBytesAsync(path)));
            await store.SaveAsync("other-context", Cache([Status(2)], [Attempt(2)], "other-context"));
            Eq(Status(), (await new DpapiShipmentTrackingCacheStore(path).LoadAsync(Context)).Statuses.Single());
            Eq(Status(2), (await new DpapiShipmentTrackingCacheStore(path).LoadAsync("other-context")).Statuses.Single());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task MergeAndDeduplicate()
    {
        var directory = DirectoryPath();
        try
        {
            var path = Path.Combine(directory, "tracking.dpapi");
            var store = new DpapiShipmentTrackingCacheStore(path);
            await store.SaveAsync(Context, Cache([Status(1, Now.AddYears(-2)), Status(2)], [Attempt(1, Now.AddYears(-2)), Attempt(2)]));
            var newer = Status(2, Now.AddMinutes(30)) with { Status = "Отримано", StatusCode = "9" };
            var newAttempt = Attempt(2, Now.AddMinutes(30));
            var otherOrder = newer with { Order = Order(3) };
            var otherDocument = newer with { ShipmentId = "other-document" };
            await store.SaveAsync(Context, Cache([newer, Status(2, Now.AddMinutes(-5)), otherOrder, otherDocument],
                [newAttempt, Attempt(2, Now.AddMinutes(-5))]));
            var loaded = await new DpapiShipmentTrackingCacheStore(path).LoadAsync(Context);
            Eq(4, loaded.Statuses.Count); Eq(2, loaded.Attempts.Count);
            Yes(loaded.Statuses.Contains(Status(1, Now.AddYears(-2))));
            Yes(loaded.Statuses.Contains(newer) && loaded.Statuses.Contains(otherOrder) && loaded.Statuses.Contains(otherDocument));
            Eq(newAttempt, loaded.Attempts.Single(x => x.Order == Order(2)));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task Corruption()
    {
        var directory = DirectoryPath();
        try
        {
            var path = Path.Combine(directory, "tracking.dpapi");
            var store = new DpapiShipmentTrackingCacheStore(path);
            await store.SaveAsync(Context, Cache());
            var bytes = await File.ReadAllBytesAsync(path);
            bytes[0] ^= 0xff;
            await File.WriteAllBytesAsync(path, bytes);
            Eq(0, (await store.LoadAsync(Context)).Statuses.Count);
            var failed = false;
            try { await store.SaveAsync(Context, Cache([Status(2)], [Attempt(2)])); }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidDataException)
            { failed = true; }
            Yes(failed && bytes.SequenceEqual(await File.ReadAllBytesAsync(path)));
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task ValidationAndCancellation()
    {
        var directory = DirectoryPath();
        try
        {
            var path = Path.Combine(directory, "tracking.dpapi");
            var store = new DpapiShipmentTrackingCacheStore(path);
            await store.SaveAsync(Context, Cache());
            var bytes = await File.ReadAllBytesAsync(path);
            var invalid = new[]
            {
                Cache([Status() with { Carrier = ShippingCarrier.Unknown }]),
                Cache([Status() with { CheckedAtUtc = default }]),
                Cache(attempts: [Attempt() with { AttemptedAtUtc = default }]),
                Cache([Status() with { TrackingNumber = "bad\nnumber" }]),
                Cache() with { Schema = 2 }
            };
            foreach (var entry in invalid)
            {
                var failed = false;
                try { await store.SaveAsync(Context, entry); }
                catch (InvalidDataException) { failed = true; }
                Yes(failed);
            }
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await store.SaveAsync(Context, Cache([Status(2)]), cancellation.Token); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            Yes(bytes.SequenceEqual(await File.ReadAllBytesAsync(path)));
            Eq(Status(), (await store.LoadAsync(Context)).Statuses.Single());
        }
        finally { Directory.Delete(directory, true); }
    }

    private static async Task ConcurrentWrites()
    {
        var directory = DirectoryPath();
        try
        {
            var path = Path.Combine(directory, "tracking.dpapi");
            await Task.WhenAll(Enumerable.Range(1, 10).Select(id => new DpapiShipmentTrackingCacheStore(path)
                .SaveAsync(Context, Cache([Status(id)], [Attempt(id)]))));
            var loaded = await new DpapiShipmentTrackingCacheStore(path).LoadAsync(Context);
            Eq(10, loaded.Statuses.Count); Eq(10, loaded.Attempts.Count);
            Yes(!Directory.EnumerateFiles(directory, "*.tmp").Any());
        }
        finally { Directory.Delete(directory, true); }
    }
}

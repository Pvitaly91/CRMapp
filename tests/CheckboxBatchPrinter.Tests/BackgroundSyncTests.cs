using System.IO;
using System.Net.Http;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Services;
using CheckboxBatchPrinter.ViewModels;

namespace CheckboxBatchPrinter.Tests;

internal static class BackgroundSyncTests
{
    public static IEnumerable<(string, Func<Task>)> All => new (string, Func<Task>)[]
    {
        ("background startup, timer and manual requests coalesce under controlled time", CoordinatorAsync),
        ("429 Retry-After and authorization pause do not cause retry storms", BackoffAsync),
        ("transient network recovery bypasses 30-minute local backoff once", NetworkRecoveryAsync),
        ("recovery events during another source coalesce into one follow-up", InFlightRecoveryAsync),
        ("network recovery and manual refresh respect 429 Retry-After", RateLimitRecoveryAsync),
        ("authorization recovery waits for verified credentials", AuthorizationRecoveryAsync),
        ("protected receipt snapshot restores only matching account", SnapshotAsync),
        ("background receipt upsert preserves row selection and print state", UpsertAsync),
        ("concurrent F5 and background receipt refresh share one API request", ReceiptFlightAsync),
        ("development and production single-instance keys remain separate", SingletonAsync),
        ("tray icon constructs and disposes on a Windows STA thread", TrayAsync)
    };

    private static async Task CoordinatorAsync()
    {
        var clock = new ManualClock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiptCalls = 0; var orderCalls = 0;
        await using var coordinator = new BackgroundSyncCoordinator(async ct =>
        {
            receiptCalls++; entered.TrySetResult();
            if (receiptCalls == 1) await release.Task.WaitAsync(ct);
        }, _ => { orderCalls++; return Task.CompletedTask; }, _ =>
            Task.FromResult(new AppSettings { BackgroundIntervalMinutes = 2 }), clock);
        coordinator.Start();
        await entered.Task;
        var first = coordinator.RefreshNowAsync();
        var again = coordinator.RefreshNowAsync();
        if (!ReferenceEquals(first, again) || receiptCalls != 1) throw new Exception("Concurrent manual requests duplicated receipt fetch.");
        release.SetResult();
        await first;
        for (var i = 0; i < 20 && !clock.HasTimer; i++) await Task.Yield();
        if (!clock.HasTimer) throw new Exception("Timer was not scheduled after initial cycle.");
        clock.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 100 && receiptCalls < 2; i++) await Task.Yield();
        if (receiptCalls != 2 || orderCalls != 2) throw new Exception("Controlled timer did not run one complete cycle.");
        coordinator.SetPaused(true);
        clock.Advance(TimeSpan.FromMinutes(10));
        for (var i = 0; i < 50; i++) await Task.Yield();
        if (receiptCalls != 2) throw new Exception("Paused timer performed another refresh.");
    }

    private static async Task BackoffAsync()
    {
        var clock = new ManualClock(); var calls = 0;
        await using var coordinator = new BackgroundSyncCoordinator(_ =>
        {
            calls++;
            throw new ApiException("synthetic limit", System.Net.HttpStatusCode.TooManyRequests,
                retryAfter: TimeSpan.FromMinutes(10));
        }, _ => Task.CompletedTask, _ => Task.FromResult(new AppSettings { BackgroundIntervalMinutes = 2 }), clock);
        await coordinator.RefreshNowAsync();
        var state = coordinator.States.Single(s => s.Name == "Checkbox");
        if (state.NextAttemptUtc != clock.GetUtcNow().AddMinutes(10)) throw new Exception("Server Retry-After was shortened.");
        clock.Advance(TimeSpan.FromMinutes(2));
        await coordinator.RefreshNowAsync();
        if (calls != 1) throw new Exception("Manual refresh bypassed rate-limit backoff.");
        await using var auth = new BackgroundSyncCoordinator(_ =>
            throw new ApiException("synthetic denied", System.Net.HttpStatusCode.Unauthorized),
            _ => Task.CompletedTask, _ => Task.FromResult(new AppSettings()), clock);
        await auth.RefreshNowAsync(); await auth.RefreshNowAsync();
        if (auth.States.Single(s => s.Name == "Checkbox").NextAttemptUtc != DateTimeOffset.MaxValue)
            throw new Exception("Authorization failure was not paused.");
    }

    private static async Task NetworkRecoveryAsync()
    {
        var clock = new ManualClock(); var calls = 0;
        await using var coordinator = new BackgroundSyncCoordinator(_ =>
        {
            if (++calls <= 5) throw new HttpRequestException("synthetic network failure");
            return Task.CompletedTask;
        }, _ => Task.CompletedTask, _ => Task.FromResult(new AppSettings()), clock);
        for (var i = 0; i < 5; i++) await coordinator.RefreshNowAsync(true);
        var state = coordinator.States.Single(s => s.Name == "Checkbox");
        if (state.CooldownReason != BackgroundCooldownReason.Transient ||
            state.NextAttemptUtc != clock.GetUtcNow().AddMinutes(30))
            throw new Exception("Synthetic network failure did not enter 30-minute transient backoff.");
        clock.Advance(TimeSpan.FromMinutes(1));
        await Task.WhenAll(coordinator.RecoveryRefreshAsync(), coordinator.RecoveryRefreshAsync());
        await coordinator.RecoveryRefreshAsync();
        if (calls != 6 || coordinator.States.Single(s => s.Name == "Checkbox").CooldownReason != BackgroundCooldownReason.None)
            throw new Exception("Recovery did not make exactly one immediate request.");
    }

    private static async Task InFlightRecoveryAsync()
    {
        var clock = new ManualClock(); var receiptCalls = 0; var holdOrders = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var coordinator = new BackgroundSyncCoordinator(_ =>
        {
            if (++receiptCalls == 1) throw new HttpRequestException("synthetic offline");
            return Task.CompletedTask;
        }, async ct =>
        {
            if (!holdOrders) return;
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        }, _ => Task.FromResult(new AppSettings()), clock);
        await coordinator.RefreshNowAsync();
        holdOrders = true;
        var active = coordinator.RefreshNowAsync(); // Checkbox is cooling; orders are still running.
        await entered.Task;
        var first = coordinator.RecoveryRefreshAsync();
        var second = coordinator.RecoveryRefreshAsync();
        if (!ReferenceEquals(first, second)) throw new Exception("Recovery events did not coalesce.");
        release.SetResult();
        await Task.WhenAll(active, first, second);
        await coordinator.RecoveryRefreshAsync();
        if (receiptCalls != 2) throw new Exception("Recovery during an active cycle was lost or duplicated.");
    }

    private static async Task RateLimitRecoveryAsync()
    {
        var clock = new ManualClock(); var calls = 0;
        await using var coordinator = new BackgroundSyncCoordinator(_ =>
        {
            calls++;
            throw new ApiException("synthetic limit", System.Net.HttpStatusCode.TooManyRequests,
                retryAfter: TimeSpan.FromMinutes(10));
        }, _ => Task.CompletedTask, _ => Task.FromResult(new AppSettings()), clock);
        await coordinator.RefreshNowAsync();
        clock.Advance(TimeSpan.FromMinutes(1));
        coordinator.SetPaused(true);
        coordinator.SetPaused(false);
        await coordinator.RecoveryRefreshAsync();
        await coordinator.RefreshNowAsync(true);
        if (calls != 1 || coordinator.States.Single(s => s.Name == "Checkbox").CooldownReason != BackgroundCooldownReason.RateLimited)
            throw new Exception("Recovery or manual refresh bypassed Retry-After.");
        clock.Advance(TimeSpan.FromMinutes(9));
        await coordinator.RefreshNowAsync();
        if (calls != 2) throw new Exception("Rate-limit request did not resume at the deadline.");
    }

    private static async Task AuthorizationRecoveryAsync()
    {
        var clock = new ManualClock(); var calls = 0;
        await using var coordinator = new BackgroundSyncCoordinator(_ =>
        {
            calls++;
            throw new ApiException("synthetic denied", System.Net.HttpStatusCode.Forbidden);
        }, _ => Task.CompletedTask, _ => Task.FromResult(new AppSettings()), clock);
        await coordinator.RefreshNowAsync();
        coordinator.SetPaused(true);
        coordinator.SetPaused(false);
        await coordinator.RecoveryRefreshAsync();
        await coordinator.RefreshNowAsync(true);
        if (calls != 1 || coordinator.States.Single(s => s.Name == "Checkbox").CooldownReason != BackgroundCooldownReason.Authorization)
            throw new Exception("Unauthorized source was retried without verified credentials.");
        coordinator.CredentialsVerified("Checkbox");
        await coordinator.RefreshNowAsync(true);
        if (calls != 2) throw new Exception("Verified credentials did not release authorization pause.");
    }

    private static async Task SnapshotAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cbp-background-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new DpapiReceiptSnapshotStore(Path.Combine(root, "receipts.dpapi"));
            var day = new DateOnly(2026, 9, 28);
            ReceiptRecord[] rows = [new() { Id = "synthetic-receipt", Status = "DONE", Type = "SELL",
                FiscalDate = new DateTimeOffset(2026, 9, 28, 12, 0, 0, TimeSpan.FromHours(3)), TotalSumMinor = 10000 }];
            await store.SaveAsync(new("account-a", day, day, DateTimeOffset.UtcNow, true, rows,
                DpapiReceiptSnapshotStore.FingerprintsFor(rows)));
            var restored = await new DpapiReceiptSnapshotStore(Path.Combine(root, "receipts.dpapi")).LoadAsync("account-a");
            if (restored?.Receipts.Single().Id != rows[0].Id || restored.Fingerprints.Count != 1 ||
                await store.LoadAsync("account-b") is not null) throw new Exception("DPAPI receipt cache crossed account scope.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static async Task UpsertAsync()
    {
        var receipts = new FakeReceipts();
        var settings = new FakeSettings();
        var vm = new MainViewModel(receipts, new FakeImages(), settings, new FakeAuth(), new FakeHistory(),
            new FakePrinter(), new FakeDialogs(), new FakeLogger());
        var day = new DateOnly(2026, 9, 28);
        receipts.Rows = [new() { Id = "r1", Type = "SELL", Status = "DONE", TotalSumMinor = 10000,
            FiscalDate = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(3)) }];
        await vm.BackgroundRefreshAsync(day, day);
        var row = vm.Receipts.Single();
        row.IsSelected = true; row.IsSelectedForOrders = true; row.PrintStatus = PrintItemStatus.Done;
        receipts.Rows = [new() { Id = "r1", Type = "SELL", Status = "DONE", TotalSumMinor = 12000,
            FiscalDate = new DateTimeOffset(2026, 9, 28, 10, 0, 0, TimeSpan.FromHours(3)) }];
        await vm.BackgroundRefreshAsync(day, day);
        if (!ReferenceEquals(row, vm.Receipts.Single()) || !row.IsSelected || !row.IsSelectedForOrders ||
            row.PrintStatus != PrintItemStatus.Done || row.Total != 120m)
            throw new Exception("Receipt upsert lost row identity, marks, print state or new metadata.");
    }

    private static async Task ReceiptFlightAsync()
    {
        var receipts = new FakeReceipts { Gate = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var settings = new FakeSettings();
        var vm = new MainViewModel(receipts, new FakeImages(), settings, new FakeAuth(), new FakeHistory(),
            new FakePrinter(), new FakeDialogs(), new FakeLogger())
            { DateFrom = new DateTime(2026, 9, 28), DateTo = new DateTime(2026, 9, 28) };
        var day = new DateOnly(2026, 9, 28);
        var first = vm.BackgroundRefreshAsync(day, day);
        await receipts.Entered.Task;
        var manual = vm.RefreshAsync();
        receipts.Gate.SetResult();
        await Task.WhenAll(first, manual);
        if (receipts.Calls != 1) throw new Exception("Timer and F5 duplicated the same Checkbox request.");
    }

    private static async Task SingletonAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cbp-single-" + Guid.NewGuid().ToString("N"));
        using var dev = new CheckboxBatchPrinter.Infrastructure.SingleInstanceGuard("Development", root);
        using var duplicate = new CheckboxBatchPrinter.Infrastructure.SingleInstanceGuard("Development", root);
        using var prod = new CheckboxBatchPrinter.Infrastructure.SingleInstanceGuard("Production", root);
        if (!dev.IsPrimary || duplicate.IsPrimary || !prod.IsPrimary)
            throw new Exception("Single instance is not scoped by channel and data root.");
        var opens = 0;
        dev.StartServer(() => { Interlocked.Increment(ref opens); return Task.CompletedTask; }, "synthetic-dev-1");
        if (await duplicate.ActivateExistingAsync() != "synthetic-dev-1" || opens != 1)
            throw new Exception("Second process failed to activate existing profile or read its version.");
    }

    private static async Task TrayAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var tray = new CheckboxBatchPrinter.Infrastructure.TrayController(
                    () => { }, () => { }, () => { }, () => { }, () => { });
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completion.Task;
        thread.Join();
    }

    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        private readonly List<Timer> _timers = [];
        public bool HasTimer => _timers.Any(x => !x.Disposed);
        public override DateTimeOffset GetUtcNow() => _now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state, _now + dueTime, period);
            _timers.Add(timer); return timer;
        }
        public void Advance(TimeSpan value)
        {
            _now += value;
            foreach (var timer in _timers.ToArray()) timer.Fire(_now);
        }
        private sealed class Timer(ManualClock clock, TimerCallback callback, object? state,
            DateTimeOffset due, TimeSpan period) : ITimer
        {
            public bool Disposed { get; private set; }
            public bool Change(TimeSpan dueTime, TimeSpan newPeriod)
            { due = clock._now + dueTime; period = newPeriod; return !Disposed; }
            public void Fire(DateTimeOffset now)
            {
                if (Disposed || now < due) return;
                due = period == Timeout.InfiniteTimeSpan ? DateTimeOffset.MaxValue : now + period;
                callback(state);
            }
            public void Dispose() => Disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class FakeReceipts : IReceiptService
    {
        public IReadOnlyList<ReceiptRecord> Rows = [];
        public TaskCompletionSource? Gate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls;
        public async Task<IReadOnlyList<ReceiptRecord>> GetReceiptsAsync(DateOnly from, DateOnly to, CancellationToken ct = default)
        { Calls++; Entered.TrySetResult(); if (Gate is not null) await Gate.Task.WaitAsync(ct); return Rows; }
    }
    private sealed class FakeSettings : ISettingsService
    {
        public Task<AppSettings> LoadAsync(CancellationToken ct = default) => Task.FromResult(new AppSettings { Login = "synthetic-cashier" });
        public Task SaveAsync(AppSettings settings, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class FakeAuth : IAuthenticationService
    {
        public bool HasStoredCredentials => true;
        public Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default) => Task.FromResult("synthetic");
        public Task SignInAndStoreAsync(string login, string password, CancellationToken ct = default) => Task.CompletedTask;
        public void InvalidateToken() { }
    }
    private sealed class FakeImages : IReceiptImageService
    {
        public Task<byte[]> GetPngAsync(string id, int width, CancellationToken ct = default) => throw new Exception("Background invoked PNG.");
        public Task<int> ClearCacheAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task CleanupAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class FakeHistory : IPrintHistoryStore
    {
        public Task<IReadOnlyDictionary<string, PrintedReceiptRecord>> LoadAsync(string account, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, PrintedReceiptRecord>>(new Dictionary<string, PrintedReceiptRecord>());
        public Task MarkPrintedAsync(string account, IReadOnlyCollection<string> ids, string printer, CancellationToken ct = default) =>
            throw new Exception("Background invoked print history write.");
    }
    private sealed class FakePrinter : IPrintService
    {
        public IReadOnlyList<string> GetInstalledPrinters() => [];
        public bool PrinterExists(string name) => false;
        public Task PrintReceiptAsync(PrintReceiptDocument receipt, AppSettings settings, CancellationToken ct = default) => throw new Exception("Background printed.");
        public Task PrintReceiptsAsSingleJobAsync(IReadOnlyList<PrintReceiptDocument> receipts, AppSettings settings, CancellationToken ct = default) => throw new Exception("Background printed.");
        public Task PrintTestAsync(AppSettings settings, CancellationToken ct = default) => throw new Exception("Background printed.");
    }
    private sealed class FakeDialogs : IUiDialogService
    {
        public bool ConfirmPrint(PrintBatchConfirmation batch) => throw new Exception("Unexpected print confirmation.");
        public void ShowInfo(string message, string title = "Checkbox Batch Printer") => throw new Exception("Unexpected dialog.");
        public void ShowError(string message, string title = "Помилка") => throw new Exception("Unexpected dialog.");
        public Task<bool> OpenSettingsAsync(bool marketplace = false) => throw new Exception("Unexpected settings dialog.");
        public void ShowPreview(byte[] png, ReceiptRowViewModel row) => throw new Exception("Unexpected preview.");
    }
    private sealed class FakeLogger : IAppLogger
    {
        public string LogDirectory => Path.GetTempPath();
        public void Info(string operation, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { }
        public void Error(string operation, Exception exception, string? receiptId = null, int? httpStatus = null, string? printStatus = null) { }
    }
}

using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Views;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class NovaPoshtaCooldownUiTests
{
    private const string Tracking = "20400000000001";
    private static readonly NovaPoshtaConnection Connection = new("10000000000000000000000000000002", "Synthetic NP", "synthetic_cooldown_key");

    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA NP cooldown disables repeat clicks, survives reopening and expires without an automatic request", () => MarketplaceViewModelTests.StaAsync(CooldownSurvivesReopening)),
        ("STA NP cancellation interrupts an active page request and restores manual checking", () => MarketplaceViewModelTests.StaAsync(CancelPageRequest))
    ];

    private sealed class Clock : TimeProvider
    {
        private long _elapsedTicks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _elapsedTicks, duration.Ticks);
    }

    private sealed class Handler(Func<int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == NovaPoshtaReadOnlyContract.EntryPoint,
                "Cooldown/cancellation reached an unexpected route, including a PDF request.");
            return respond(Interlocked.Increment(ref _calls), ct);
        }
    }

    private sealed class NoDocuments : ILabelPdfInspector, IShippingLabelRenderer
    {
        public int Inspections, Renders;
        public Task<LabelPdfInspection> InspectAsync(byte[] pdf, CancellationToken ct = default)
        { Inspections++; throw new Exception("An incomplete check reached PDF inspection."); }
        public Task<IReadOnlyList<ShippingLabelPage>> RenderAsync(ShippingLabelDocument document, double x, double y, CancellationToken ct = default)
        { Renders++; throw new Exception("An incomplete check reached PDF rendering."); }
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class ReadOnlyStore : IShippingSettingsStore
    {
        public ShippingLabelSettings Config = new();
        public MarketplaceSettings Market = new();
        public int Saves;
        public Task<ShippingLabelSettings> LoadAsync(CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return Task.FromResult(Config); }
        public Task SaveAsync(ShippingLabelSettings value, CancellationToken ct = default)
        { Saves++; throw new Exception("Read-only diagnostic tried to save settings during cooldown/cancellation."); }
    }

    private static ReadOnlyStore Settings()
    {
        var order = Order("selected", ShippingCarrier.NovaPoshta, Tracking);
        var stores = new ReadOnlyStore();
        stores.Config = stores.Config with { NovaPoshtaConnections = [Connection], NovaPoshtaStoreBindings = [new(order.Key.ConnectionId, Connection.Id)] };
        stores.Market.Connections.Add(new() { Id = order.Key.ConnectionId, Name = "Synthetic Prom", Enabled = true, Marketplace = MarketplaceKind.Prom });
        return stores;
    }

    private static NovaPoshtaConnectionWindow Window(ReadOnlyStore stores, NovaPoshtaDiagnostics diagnostics, NoDocuments documents) =>
        new(stores, stores.Config, stores.Market, diagnostics, new(), documents, Order("selected", ShippingCarrier.NovaPoshta, Tracking));

    private static T Field<T>(NovaPoshtaConnectionWindow window, string name) =>
        (T)(typeof(NovaPoshtaConnectionWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window)
            ?? throw new Exception("Missing NP connection field " + name));

    private static async Task Until(Func<bool> condition, string failure)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        try { while (!condition()) await Task.Delay(10, timeout.Token); }
        catch (OperationCanceledException) { throw new Exception(failure); }
    }

    private static void RenderCountdown(NovaPoshtaConnectionWindow window)
    {
        if (Environment.GetEnvironmentVariable("CHECKBOX_TEST_RENDER_DIR") is not { Length: > 0 } output) return;
        Directory.CreateDirectory(output);
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(660, 340)); content.Arrange(new Rect(0, 0, 660, 340)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(660, 340, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, "nova-poshta-cooldown.png")); encoder.Save(file);
    }

    private static async Task CooldownSurvivesReopening()
    {
        foreach (var useHttp429 in new[] { false, true })
        {
            var clock = new Clock();
            var policy = new NovaPoshtaRequestPolicy(clock);
            var handler = new Handler((call, _) =>
            {
                if (call > 1) return Task.FromResult(Json(new { success = true, data = Array.Empty<object>(), errors = Array.Empty<string>() }));
                if (!useHttp429) return Task.FromResult(Json(new
                {
                    success = false, data = Array.Empty<object>(), errors = new[] { "Too many requests" },
                    additionalData = Connection.ApiKey + " synthetic buyer data"
                }));
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                { Content = new StringContent(Connection.ApiKey + " synthetic buyer data") };
                response.Headers.RetryAfter = new(TimeSpan.FromMinutes(2));
                return Task.FromResult(response);
            });
            using var client = new HttpClient(handler);
            var documents = new NoDocuments();
            var diagnostics = new NovaPoshtaDiagnostics(new(new ShippingLabelHttp(client, novaPoshtaPolicy: policy)), documents);
            var stores = Settings();
            NovaPoshtaConnectionWindow? window = Window(stores, diagnostics, documents);
            try
            {
                var check = Field<Button>(window, "_check");
                Check(handler.Calls == 0 && check.IsEnabled, "Opening a fresh NP check sent a request or disabled the manual action.");
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => handler.Calls == 1 && Field<StackPanel>(window, "_form").IsEnabled,
                    "Rate-limited check did not return control to the form.");
                Check(!check.IsEnabled && check.Content.ToString()!.StartsWith("Повторити через ", StringComparison.Ordinal),
                    "Rate-limited check did not disable the button with remaining-wait guidance.");
                var status = Field<TextBlock>(window, "_status").Text;
                Check(!status.Contains(Connection.ApiKey, StringComparison.Ordinal) && !status.Contains("synthetic buyer data", StringComparison.Ordinal),
                    "Cooldown UI exposed the response body or saved key.");
                if (!useHttp429) RenderCountdown(window);
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Yield();
                Check(handler.Calls == 1, "A programmatic click bypassed the disabled cooldown button.");

                window.Close();
                Check(!Field<DispatcherTimer>(window, "_cooldownTimer").IsEnabled, "Closing the window left its cooldown timer running.");
                window = Window(stores, diagnostics, documents);
                check = Field<Button>(window, "_check");
                Check(!check.IsEnabled && handler.Calls == 1 && check.Content.ToString()!.StartsWith("Повторити через ", StringComparison.Ordinal),
                    "Reopening reset the shared cooldown or performed a new request.");
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(handler.Calls == 1, "Reopened window bypassed the shared cooldown.");

                // Exercise the real loaded/timer handlers without displaying a native window.
                window.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
                Check(Field<DispatcherTimer>(window, "_cooldownTimer").IsEnabled, "Loading the window did not start the countdown timer.");
                var remaining = diagnostics.GetRetryDelay(Connection);
                Check(remaining > TimeSpan.FromSeconds(1), "Rate-limit cooldown is missing or too short for a useful countdown.");
                clock.Advance(remaining - TimeSpan.FromMilliseconds(500));
                await Until(() => Equals(check.Content, "Повторити через 1 с"), "The loaded window did not update its remaining-wait countdown.");
                Check(!check.IsEnabled && handler.Calls == 1, "The last fractional second enabled a request early.");
                clock.Advance(TimeSpan.FromMilliseconds(500));
                await Until(() => check.IsEnabled, "Cooldown expiry did not re-enable the manual check.");
                Check(Equals(check.Content, "Перевірити наявну накладну") && handler.Calls == 1,
                    "Cooldown expiry sent an automatic request or left a stale button label.");
                check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Until(() => handler.Calls == 2 && Field<StackPanel>(window, "_form").IsEnabled,
                    "Explicit retry after expiry did not complete its request.");
                Check(documents.Inspections == 0 && documents.Renders == 0 && handler.Calls == 2 && stores.Saves == 0,
                    "Rate-limited or empty lookup reached PDF work, sent an extra request, or saved settings.");
            }
            finally
            {
                window?.Close();
                if (window is not null) Check(!Field<DispatcherTimer>(window, "_cooldownTimer").IsEnabled,
                    "Closing the loaded window left its cooldown timer running.");
            }
        }
    }

    private static async Task CancelPageRequest()
    {
        var enteredRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelledRequest = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new Clock();
        var policy = new NovaPoshtaRequestPolicy(clock);
        var handler = new Handler(async (call, ct) =>
        {
            if (call == 1) return Json(new
            {
                success = true, data = new[] { new { IntDocNumber = "20400000000002", Ref = "10000000-0000-0000-0000-000000000003", SeatsAmount = "1", DeletionMark = "0" } },
                errors = Array.Empty<string>()
            });
            if (call == 2)
            {
                enteredRequest.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, ct); }
                catch (OperationCanceledException) { cancelledRequest.TrySetResult(); throw; }
            }
            return Json(new { success = true, data = Array.Empty<object>(), errors = Array.Empty<string>() });
        });
        using var client = new HttpClient(handler);
        var documents = new NoDocuments();
        var diagnostics = new NovaPoshtaDiagnostics(new(new ShippingLabelHttp(client, novaPoshtaPolicy: policy)), documents);
        var stores = Settings();
        var window = Window(stores, diagnostics, documents);
        try
        {
            var check = Field<Button>(window, "_check");
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await enteredRequest.Task.WaitAsync(TimeSpan.FromSeconds(4));
            var cancel = Field<Button>(window, "_cancel");
            Check(handler.Calls == 2 && !check.IsEnabled && cancel.IsEnabled && cancel.Visibility == Visibility.Visible,
                "An active page request did not leave cancellation available while blocking repeat checks.");
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await cancelledRequest.Task.WaitAsync(TimeSpan.FromSeconds(4));
            await Until(() => Field<StackPanel>(window, "_form").IsEnabled, "Cancellation did not restore the form after the page request.");
            Check(handler.Calls == 2 && check.IsEnabled && !cancel.IsEnabled && cancel.Visibility == Visibility.Collapsed &&
                Field<TextBlock>(window, "_status").Text.Contains("скасовано", StringComparison.Ordinal) && diagnostics.GetRetryDelay(Connection) == TimeSpan.Zero,
                "Cancelling pagination sent another request, lost cancellation status, or fabricated a rate-limit cooldown.");
            Check(documents.Inspections == 0 && documents.Renders == 0 && stores.Saves == 0,
                "Cancelled pagination reached PDF work or saved settings.");
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => handler.Calls == 3 && Field<StackPanel>(window, "_form").IsEnabled,
                "Cancelled pagination retained the request or scan guard and blocked a new manual check.");
            Check(documents.Inspections == 0 && documents.Renders == 0 && stores.Saves == 0,
                "An empty manual retry reached PDF work or saved settings.");
        }
        finally { window.Close(); }
    }
}

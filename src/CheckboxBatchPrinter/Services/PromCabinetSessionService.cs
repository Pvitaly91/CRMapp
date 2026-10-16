using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace CheckboxBatchPrinter.Services;

// One browser profile per existing marketplace connection, below the current app channel's
// private profile. Browser-managed cookies are never exported to the app or shared with API clients.
public sealed partial class PromCabinetSessionService(string root, IMarketplaceSettingsStore settings,
    Dispatcher dispatcher) : IPromCabinetSession, IPromCabinetLoginService, IDisposable
{
    private readonly Dictionary<string, BrowserSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;
    private sealed class BrowserSession(WebView2 browser, HwndSource host, Grid backgroundPanel)
    {
        public WebView2 Browser { get; } = browser;
        public HwndSource Host { get; } = host;
        public Grid BackgroundPanel { get; } = backgroundPanel;
        public bool LoginMode { get; set; }
        public string? AllowedLabelUri { get; set; }
    }
    private sealed record SessionState(bool Configured = false, DateTimeOffset? RetryAfterUtc = null);
    public string GetStatus(string connectionId)
    {
        var state = ReadState(connectionId);
        return state.Configured ? "Сесію кабінету Prom збережено. Доступ до ТТН перевіряється під час друку."
            : "Для накладних Rozetka Delivery замовлень Prom / Bigl увійдіть у кабінет Prom.";
    }
    public async Task<string> ConnectAsync(MarketplaceConnection connection)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await RequireConnectionAsync(connection.Id, CancellationToken.None);
            return await OnUiAsync(async () =>
            {
                var session = await GetBrowserAsync(connection.Id);
                session.LoginMode = true;
                var saved = false;
                var window = new Window { Title = "Кабінет Prom — " + connection.Name, Width = 1000, Height = 760,
                    MinWidth = 650, MinHeight = 500, WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) };
                var panel = new DockPanel();
                var header = new StackPanel { Margin = new Thickness(12) };
                header.Children.Add(new TextBlock { Text = "Увійдіть у магазин «" + connection.Name + "». Після входу натисніть «Зберегти сесію».", TextWrapping = TextWrapping.Wrap });
                var message = new TextBlock { Text = "Сесія належатиме цьому підключенню Prom і використовуватиметься для його накладних.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
                header.Children.Add(message);
                DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
                var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
                var finish = new Button { Content = "Зберегти сесію", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(6, 0, 0, 0), IsEnabled = false };
                var close = new Button { Content = "Закрити", Padding = new Thickness(12, 6, 12, 6) };
                close.Click += (_, _) => window.Close();
                finish.Click += (_, _) =>
                {
                    try
                    {
                        if (!Uri.TryCreate(session.Browser.CoreWebView2.Source, UriKind.Absolute, out var uri) || !PromCabinetBrowserProtocol.IsCabinetPage(uri)) return;
                        WriteState(connection.Id, new(true)); saved = true; window.Close();
                    }
                    catch (IOException) { message.Text = "Не вдалося зберегти стан сесії. Перевірте доступ до папки даних."; }
                    catch (UnauthorizedAccessException) { message.Text = "Немає доступу до папки даних для збереження сесії."; }
                };
                controls.Children.Add(close); controls.Children.Add(finish);
                DockPanel.SetDock(controls, Dock.Bottom); panel.Children.Add(controls);
                session.BackgroundPanel.Children.Remove(session.Browser);
                panel.Children.Add(session.Browser); window.Content = panel;
                void NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
                {
                    finish.IsEnabled = e.IsSuccess && Uri.TryCreate(session.Browser.CoreWebView2.Source, UriKind.Absolute, out var uri) && PromCabinetBrowserProtocol.IsCabinetPage(uri);
                    if (!e.IsSuccess) message.Text = "Не вдалося відкрити Prom. Перевірте з’єднання та повторно відкрийте вхід.";
                }
                session.Browser.CoreWebView2.NavigationCompleted += NavigationCompleted;
                try
                {
                    session.Browser.CoreWebView2.Navigate("https://my.prom.ua/cms");
                    window.ShowDialog();
                }
                finally
                {
                    session.Browser.CoreWebView2.NavigationCompleted -= NavigationCompleted;
                    panel.Children.Remove(session.Browser);
                    session.BackgroundPanel.Children.Add(session.Browser);
                    session.BackgroundPanel.UpdateLayout();
                    session.LoginMode = false;
                    session.Browser.CoreWebView2.Navigate("about:blank");
                }
                return saved ? GetStatus(connection.Id) : "Вікно входу закрито. " + GetStatus(connection.Id);
            });
        }
        finally { _gate.Release(); }
    }

    public async Task<PromCabinetLabelResponse> ReadLabelAsync(PromCabinetLabelRequest request, CancellationToken cancellationToken = default)
    {
        _ = request.RequestUri; // Validate before looking up profiles or initiating browser work.
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await RequireConnectionAsync(request.ConnectionId, cancellationToken);
            var state = ReadState(request.ConnectionId);
            if (!state.Configured) throw LoginRequired();
            if (state.RetryAfterUtc is { } retry && retry > DateTimeOffset.UtcNow)
                throw new ShippingLabelException(LabelFailureKind.Temporary, "Кабінет Prom тимчасово призупинено після помилки. Повторіть друк пізніше.") { RetryAtUtc = retry, AppliesToBatch = true };
            PromCabinetBrowserResult result;
            try
            {
                result = await OnUiAsync(async () =>
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(TimeSpan.FromSeconds(55));
                    var session = await GetBrowserAsync(request.ConnectionId, timeout.Token);
                    await NavigateBridgeAsync(session, timeout.Token);
                    return await ReadInBrowserAsync(session, request, timeout.Token);
                });
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Pause(request.ConnectionId, state, TimeSpan.FromMinutes(1));
                throw new ShippingLabelException(LabelFailureKind.Temporary, "Перевищено час отримання накладної Prom.") { AppliesToBatch = true };
            }
            catch (ShippingLabelException ex) when (ex.Kind == LabelFailureKind.Temporary)
            {
                Pause(request.ConnectionId, state, TimeSpan.FromMinutes(1)); throw;
            }
            catch (ShippingLabelException ex) when (ex.Kind == LabelFailureKind.ConnectionRequired && ex.MissingCredentials)
            {
                WriteState(request.ConnectionId, new(false)); throw LoginRequired();
            }
            var response = result.Response;
            if (response.StatusCode is 401 or 403 || response.StatusCode == 200 && response.ContentType == "text/html")
            {
                WriteState(request.ConnectionId, new(false));
                throw LoginRequired();
            }
            if (response.StatusCode == 429 || response.StatusCode >= 500)
            {
                var until = RetryAt(result.RetryAfter);
                WriteState(request.ConnectionId, state with { RetryAfterUtc = until });
                throw new ShippingLabelException(LabelFailureKind.Temporary, "Кабінет Prom тимчасово не надав накладну. Повторні запити призупинено.") { AppliesToBatch = true, RetryAtUtc = until };
            }
            await RequireConnectionAsync(request.ConnectionId, cancellationToken);
            return response;
        }
        finally { _gate.Release(); }
    }

    private async Task<BrowserSession> GetBrowserAsync(string id, CancellationToken ct = default)
    {
        dispatcher.VerifyAccess();
        ct.ThrowIfCancellationRequested();
        if (_sessions.TryGetValue(id, out var existing)) return existing;
        var browser = new WebView2();
        // WPF WebView2 needs a real parent HWND even for background fetches. This popup has
        // no WS_VISIBLE style, is never shown, and is separate from the user's login window.
        var host = new HwndSource(new HwndSourceParameters("CRMapp Prom session")
        { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000, WindowStyle = unchecked((int)0x80000000) });
        var backgroundPanel = new Grid { Width = 1, Height = 1 };
        backgroundPanel.Children.Add(browser); host.RootVisual = backgroundPanel;
        backgroundPanel.Measure(new Size(1, 1)); backgroundPanel.Arrange(new Rect(0, 0, 1, 1)); backgroundPanel.UpdateLayout();
        var session = new BrowserSession(browser, host, backgroundPanel);
        try
        {
            var folder = Path.Combine(ConnectionDirectory(id), "Browser");
            var environmentTask = CoreWebView2Environment.CreateAsync(userDataFolder: folder);
            ObserveLateFailure(environmentTask);
            var environment = await environmentTask.WaitAsync(TimeSpan.FromSeconds(30), ct);
            if (!Path.GetFullPath(environment.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar)
                    .Equals(Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Browser profile was overridden.");
            var initialization = browser.EnsureCoreWebView2Async(environment);
            ObserveLateFailure(initialization);
            await initialization.WaitAsync(TimeSpan.FromSeconds(30), ct);
            ct.ThrowIfCancellationRequested();
            var core = browser.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (session.LoginMode && Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && PromCabinetBrowserProtocol.IsLoginNavigation(uri)) core.Navigate(e.Uri);
            };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri == "about:blank" || e.Uri == PromCabinetBrowserProtocol.BridgeUri) return;
                e.Cancel = !session.LoginMode || !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || !PromCabinetBrowserProtocol.IsLoginNavigation(uri);
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += (_, e) =>
            {
                if (e.Request.Uri == PromCabinetBrowserProtocol.BridgeUri && e.Request.Method == "GET")
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(Encoding.UTF8.GetBytes(PromCabinetBrowserProtocol.BridgeHtml)), 200, "OK", "Content-Type: text/html; charset=utf-8\r\nCache-Control: no-store");
                else if (!session.LoginMode && !(e.Request.Method == "GET" && e.Request.Uri == session.AllowedLabelUri &&
                    Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && PromCabinetLabelReadOnlyContract.ValidUri(uri)))
                    e.Response = environment.CreateWebResourceResponse(new MemoryStream(), 403, "Blocked", "Content-Type: text/plain");
            };
            _sessions.Add(id, session);
            return session;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            browser.Dispose(); host.Dispose();
            throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Для входу в Prom установіть Microsoft Edge WebView2 Runtime: https://developer.microsoft.com/microsoft-edge/webview2/") { AppliesToBatch = true };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            browser.Dispose(); host.Dispose(); throw;
        }
        catch (TimeoutException)
        {
            browser.Dispose(); host.Dispose();
            throw new ShippingLabelException(LabelFailureKind.Temporary, "Перевищено час запуску сесії Prom. Повторіть спробу пізніше.") { AppliesToBatch = true };
        }
        catch
        {
            browser.Dispose(); host.Dispose();
            throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Не вдалося відкрити збережену сесію Prom. Перевірте WebView2 Runtime і доступ до папки даних.") { AppliesToBatch = true };
        }
    }

    private static void ObserveLateFailure(Task task) => _ = task.ContinueWith(completed => { _ = completed.Exception; },
        CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private static async Task NavigateBridgeAsync(BrowserSession session, CancellationToken ct)
    {
        var core = session.Browser.CoreWebView2;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        ulong? navigationId = null;
        void Starting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            if (e.Uri == PromCabinetBrowserProtocol.BridgeUri) navigationId = e.NavigationId;
        }
        void Completed(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (navigationId != e.NavigationId) return;
            if (e.IsSuccess && core.Source == PromCabinetBrowserProtocol.BridgeUri) completion.TrySetResult();
            else if (!e.IsSuccess) completion.TrySetException(new ShippingLabelException(LabelFailureKind.Temporary, "Не вдалося підготувати сесію Prom.") { AppliesToBatch = true });
        }
        core.NavigationCompleted += Completed;
        core.NavigationStarting += Starting;
        try
        {
            core.Navigate(PromCabinetBrowserProtocol.BridgeUri);
            await completion.Task.WaitAsync(ct);
        }
        finally { core.NavigationCompleted -= Completed; core.NavigationStarting -= Starting; }
    }

    private static async Task<PromCabinetBrowserResult> ReadInBrowserAsync(BrowserSession session, PromCabinetLabelRequest request, CancellationToken ct)
    {
        var core = session.Browser.CoreWebView2;
        var nonce = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<PromCabinetBrowserResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var result = PromCabinetBrowserProtocol.Parse(e.Source, e.WebMessageAsJson, nonce, request);
                if (result is not null) completion.TrySetResult(result);
            }
            catch (ShippingLabelException ex) { completion.TrySetException(ex); }
            catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or KeyNotFoundException)
            { completion.TrySetException(new ShippingLabelException(LabelFailureKind.InvalidDocument, "Некоректна відповідь кабінету Prom. Друк зупинено.") { AppliesToBatch = true }); }
        }
        core.WebMessageReceived += Message;
        session.AllowedLabelUri = request.RequestUri.AbsoluteUri;
        try
        {
            ct.ThrowIfCancellationRequested();
            await core.ExecuteScriptAsync(PromCabinetBrowserProtocol.DownloadScript(request, nonce));
            return await completion.Task.WaitAsync(ct);
        }
        finally
        {
            core.WebMessageReceived -= Message;
            session.AllowedLabelUri = null;
            // Navigation aborts fetch even when cancellation occurred before the JS timer fired.
            core.Navigate("about:blank");
        }
    }

    private async Task RequireConnectionAsync(string id, CancellationToken ct)
    {
        var configuration = await settings.LoadAsync(ct);
        if (configuration.Connections.Count(c => c.Id == id && c.Marketplace == MarketplaceKind.Prom && c.Enabled) != 1)
            throw new ShippingLabelException(LabelFailureKind.ConnectionRequired, "Спочатку збережіть і ввімкніть це підключення Prom у налаштуваннях маркетплейсів.") { AppliesToBatch = true };
    }
    private string ConnectionDirectory(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 200) throw new ArgumentException("Invalid connection identity.");
        return Path.Combine(root, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))));
    }
    private SessionState ReadState(string id)
    {
        try
        {
            var path = Path.Combine(ConnectionDirectory(id), "session.json");
            return File.Exists(path) ? JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }
    private void WriteState(string id, SessionState state)
    {
        var folder = ConnectionDirectory(id); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "session.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(state)); File.Move(temporary, path, true);
    }
    private void Pause(string id, SessionState state, TimeSpan duration) => WriteState(id, state with { RetryAfterUtc = DateTimeOffset.UtcNow + duration });
    private static DateTimeOffset RetryAt(string value)
    {
        var now = DateTimeOffset.UtcNow;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds >= 0)
            return now.AddSeconds(Math.Max(60, seconds));
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) && at > now.AddMinutes(1)) return at;
        return now.AddMinutes(5);
    }
    private static ShippingLabelException LoginRequired() => new(LabelFailureKind.ConnectionRequired,
        "Увійдіть у кабінет Prom: Налаштування → Маркетплейси → Prom → «Увійти в кабінет Prom для накладних». Окремий ключ Rozetka Delivery не потрібний.") { MissingCredentials = true, AppliesToBatch = true };
    private Task<T> OnUiAsync<T>(Func<Task<T>> action) => dispatcher.CheckAccess() ? action() : dispatcher.InvokeAsync(action).Task.Unwrap();
    public void Dispose()
    {
        dispatcher.VerifyAccess(); _disposed = true;
        foreach (var session in _sessions.Values) { session.Browser.Dispose(); session.Host.Dispose(); }
        _sessions.Clear();
    }
}

using System.IO;
using System.Windows.Threading;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.Tests;

internal static class PromCabinetOfflineBrowserTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("STA isolated offline WebView2 bridge restores one Prom profile, reads PDF, expires auth and honors cooldown", RunAsync)
    ];
    internal static async Task RunAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher? testDispatcher = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher; testDispatcher = dispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.UnhandledException += (_, e) => { e.Handled = true; completion.TrySetException(e.Exception); dispatcher.BeginInvokeShutdown(DispatcherPriority.Send); };
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await VerifyAsync(); completion.TrySetResult(); }
                catch (Exception ex) { completion.TrySetException(ex); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.ContextIdle); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(90)); }
        finally
        {
            if (testDispatcher is { HasShutdownStarted: false }) testDispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
            if (!thread.Join(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Offline browser test dispatcher did not terminate.");
        }
    }
    private static async Task VerifyAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "cbp-prom-browser-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "allow-offline-verification"), "Synthetic local-only verification");
        Console.WriteLine("OFFLINE_BROWSER_FIXTURE " + root);
        if (!await PromCabinetSessionService.VerifyOfflineAsync(root))
            Console.WriteLine("SKIP offline browser integration: WebView2 Runtime unavailable; pure protocol/source tests still run.");
        // WebView2 can release profile files asynchronously. Leave this named temporary
        // fixture for ordinary Temp cleanup rather than risking deletion of an active profile.
    }
}

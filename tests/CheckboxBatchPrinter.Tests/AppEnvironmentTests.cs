using System.IO;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.Tests;

internal static class AppEnvironmentTests
{
    public static IEnumerable<(string, Func<Task>)> All => new (string, Func<Task>)[]
    {
        ("embedded channel has independent stable data root and version", ChannelAsync),
        ("production settings and DPAPI credentials survive restart without touching development", IsolationAsync)
    };

    private static Task ChannelAsync()
    {
        if (AppEnvironment.Channel is not ("Development" or "Production")) throw new Exception("Invalid channel");
        var original = Environment.CurrentDirectory;
        var root = AppEnvironment.DataRoot;
        try { Environment.CurrentDirectory = Path.GetTempPath(); if (AppEnvironment.DataRoot != root) throw new Exception("CWD changed data root"); }
        finally { Environment.CurrentDirectory = original; }
        if (!AppEnvironment.DisplayVersion.Contains(AppEnvironment.Channel) || string.IsNullOrEmpty(AppEnvironment.Version)) throw new Exception("Missing identity");
        if (Path.GetFileName(root) != (AppEnvironment.Channel == "Production" ? "CheckboxBatchPrinter-Production" : "CheckboxBatchPrinter")) throw new Exception("Wrong root");
        return Task.CompletedTask;
    }

    private static async Task IsolationAsync()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "cbp-environment-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            var dev = AppEnvironment.GetDataRoot(temporary, "Development");
            var prod = AppEnvironment.GetDataRoot(temporary, "Production");
            var devSettings = new JsonSettingsService(Path.Combine(dev, "settings.json"));
            var prodSettings = new JsonSettingsService(Path.Combine(prod, "settings.json"));
            await devSettings.SaveAsync(new AppSettings { PrinterName = "fake-dev" });
            await prodSettings.SaveAsync(new AppSettings { PrinterName = "fake-prod" });
            var devCredentials = new DpapiCredentialStore(Path.Combine(dev, "credential.bin"));
            await devCredentials.SavePasswordAsync("synthetic-dev-only");
            await new DpapiCredentialStore(Path.Combine(prod, "credential.bin")).SavePasswordAsync("synthetic-prod-only");
            if ((await new JsonSettingsService(Path.Combine(prod, "settings.json")).LoadAsync()).PrinterName != "fake-prod") throw new Exception("Restart lost settings");
            if ((await devSettings.LoadAsync()).PrinterName != "fake-dev" || await devCredentials.LoadPasswordAsync() != "synthetic-dev-only") throw new Exception("Production changed development");
            await devSettings.SaveAsync(new AppSettings { PrinterName = "fake-dev-edited" });
            if ((await prodSettings.LoadAsync()).PrinterName != "fake-prod" || await new DpapiCredentialStore(Path.Combine(prod, "credential.bin")).LoadPasswordAsync() != "synthetic-prod-only") throw new Exception("Development changed production");
        }
        finally { Directory.Delete(temporary, true); }
    }
}

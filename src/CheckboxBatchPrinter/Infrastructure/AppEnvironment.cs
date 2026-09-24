using System.IO;
using System.Reflection;

namespace CheckboxBatchPrinter.Infrastructure;

/// <summary>Embedded channel, never inferred from Release, EXE location or working directory.</summary>
public static class AppEnvironment
{
    private static string? Metadata(string key) => typeof(AppEnvironment).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(a => a.Key == key)?.Value;

    public static string Channel => Metadata("AppChannel") ?? "Development";
    public static string Commit => Metadata("BuildCommit") is { Length: > 0 } sha ? sha : "unknown";
    public static string ShortCommit => Commit[..Math.Min(12, Commit.Length)];
    public static string Version => typeof(AppEnvironment).Assembly.GetName().Version?.ToString(3) ?? "unknown";
    public static string DisplayVersion => $"{Version} · {Channel} · {ShortCommit}";
    public static string WindowTitle => Channel == "Production" ? "CRMapp — робоча версія" : "Checkbox Batch Printer";
    public static string DataRoot => GetDataRoot(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Channel);

    // Explicit base allows tests to use temporary data, never a real user's profile.
    public static string GetDataRoot(string localAppData, string channel) => Path.Combine(localAppData, channel switch
    {
        "Development" => "CheckboxBatchPrinter",
        "Production" => "CheckboxBatchPrinter-Production",
        _ => throw new ArgumentException("Unknown application channel.", nameof(channel))
    });
}

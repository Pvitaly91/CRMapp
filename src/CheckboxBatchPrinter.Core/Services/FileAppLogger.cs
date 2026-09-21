using System.Text.RegularExpressions;

namespace CheckboxBatchPrinter.Core.Services;

public sealed partial class FileAppLogger : IAppLogger
{
    private readonly object _sync = new();

    public FileAppLogger(string logDirectory)
    {
        LogDirectory = logDirectory;
        Directory.CreateDirectory(LogDirectory);
        CleanupOldLogs();
    }

    public string LogDirectory { get; }

    public void Info(string operation, string? receiptId = null, int? httpStatus = null, string? printStatus = null) =>
        Write("INFO", operation, null, receiptId, httpStatus, printStatus);

    public void Error(string operation, Exception exception, string? receiptId = null, int? httpStatus = null, string? printStatus = null) =>
        Write("ERROR", operation, exception, receiptId, httpStatus, printStatus);

    private void Write(string level, string operation, Exception? exception, string? receiptId, int? httpStatus, string? printStatus)
    {
        var fields = new[]
        {
            DateTimeOffset.Now.ToString("O"), level, Clean(operation),
            $"receipt_id={Clean(receiptId)}", $"http_status={httpStatus?.ToString() ?? string.Empty}",
            $"print_status={Clean(printStatus)}", $"exception={Clean(exception?.ToString())}"
        };
        var line = string.Join("\t", fields) + Environment.NewLine;
        var path = Path.Combine(LogDirectory, $"checkbox-{DateTime.Now:yyyyMMdd}.log");
        lock (_sync)
            File.AppendAllText(path, line);
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        value = NewLineRegex().Replace(value, " ");
        value = SecretRegex().Replace(value, "$1=[REDACTED]");
        value = BearerRegex().Replace(value, "Bearer [REDACTED]");
        return value.Length <= 4000 ? value : value[..4000];
    }

    private void CleanupOldLogs()
    {
        foreach (var file in Directory.EnumerateFiles(LogDirectory, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddDays(-30)) File.Delete(file);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [GeneratedRegex("[\\r\\n]+")]
    private static partial Regex NewLineRegex();

    [GeneratedRegex("(?i)(password|access_token|refresh_token|authorization)\\s*[:=]\\s*[^\\s,;]+")]
    private static partial Regex SecretRegex();

    [GeneratedRegex("(?i)Bearer\\s+[^\\s,;]+")]
    private static partial Regex BearerRegex();
}

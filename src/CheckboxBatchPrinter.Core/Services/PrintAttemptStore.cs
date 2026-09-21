using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public static class PrintAccountContext
{
    public static string Create(AppSettings settings)
    {
        var source = $"{settings.ApiBaseUrl.Trim().TrimEnd('/').ToLowerInvariant()}\n{settings.Login.Trim().ToUpperInvariant()}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
    }
}

public static class PrintAttemptFactory
{
    public static PrintAttemptDescriptor CreateReceipt(
        string receiptId,
        string printerName,
        Guid? attemptId = null,
        DateTimeOffset? startedAtUtc = null)
    {
        var id = attemptId ?? Guid.NewGuid();
        var safeReceiptId = string.Concat(receiptId.Take(24)
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        return new PrintAttemptDescriptor(
            id,
            printerName,
            $"Checkbox {safeReceiptId} {id:N}",
            startedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public static PrintAttemptDescriptor CreateBatch(
        int receiptCount,
        string printerName,
        Guid? attemptId = null,
        DateTimeOffset? startedAtUtc = null)
    {
        if (receiptCount <= 0) throw new ArgumentOutOfRangeException(nameof(receiptCount));
        var id = attemptId ?? Guid.NewGuid();
        return new PrintAttemptDescriptor(
            id,
            printerName,
            $"Checkbox batch {receiptCount} {id:N}",
            startedAtUtc ?? DateTimeOffset.UtcNow);
    }
}

public sealed class JsonPrintAttemptStore : IPrintAttemptStore
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(30);
    private const int MaxRecords = 5000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _path;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    public JsonPrintAttemptStore(
        string path,
        TimeSpan? retention = null,
        TimeProvider? timeProvider = null)
    {
        _path = path;
        _retention = retention ?? DefaultRetention;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public IReadOnlyList<PrintAttemptRecord> Load(string accountContext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountContext);
        lock (_gate)
        {
            var all = ReadAll();
            var retained = RetainRecent(all);
            if (retained.Count != all.Count) WriteAll(retained);
            return retained
                .Where(x => string.Equals(x.AccountContext, accountContext, StringComparison.Ordinal))
                .OrderBy(x => x.UpdatedAtUtc)
                .ToArray();
        }
    }

    public void UpsertMany(IReadOnlyList<PrintAttemptRecord> attempts)
    {
        if (attempts.Count == 0) return;
        if (attempts.Any(x => string.IsNullOrWhiteSpace(x.AccountContext) ||
                              string.IsNullOrWhiteSpace(x.ReceiptId) ||
                              string.IsNullOrWhiteSpace(x.PrinterName) ||
                              string.IsNullOrWhiteSpace(x.UniqueJobName)))
            throw new ArgumentException("Print attempt contains an empty required field.", nameof(attempts));

        lock (_gate)
        {
            var records = RetainRecent(ReadAll());
            foreach (var attempt in attempts)
            {
                records.RemoveAll(x => x.AttemptId == attempt.AttemptId &&
                                       string.Equals(x.AccountContext, attempt.AccountContext, StringComparison.Ordinal) &&
                                       string.Equals(x.ReceiptId, attempt.ReceiptId, StringComparison.Ordinal));
                records.Add(attempt);
            }
            records = records
                .OrderByDescending(x => x.UpdatedAtUtc)
                .Take(MaxRecords)
                .OrderBy(x => x.UpdatedAtUtc)
                .ToList();
            WriteAll(records);
        }
    }

    private List<PrintAttemptRecord> ReadAll()
    {
        if (!File.Exists(_path)) return [];
        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return JsonSerializer.Deserialize<List<PrintAttemptRecord>>(stream, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Локальний журнал спроб друку пошкоджений; друк заблоковано до перевірки журналу.", exception);
        }
    }

    private List<PrintAttemptRecord> RetainRecent(IEnumerable<PrintAttemptRecord> records)
    {
        var cutoff = _timeProvider.GetUtcNow() - _retention;
        return records.Where(x => x.UpdatedAtUtc >= cutoff).ToList();
    }

    private void WriteAll(IReadOnlyList<PrintAttemptRecord> records)
    {
        var directory = Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Print attempt journal path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, records, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, true);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

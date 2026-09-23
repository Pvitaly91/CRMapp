using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

public sealed class JsonPrintHistoryStore : IPrintHistoryStore
{
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(365);
    private const int MaxRecords = 50_000;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;
    private readonly TimeSpan _retention;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonPrintHistoryStore(string path, TimeSpan? retention = null, TimeProvider? timeProvider = null)
    {
        _path = path;
        _retention = retention ?? DefaultRetention;
        _timeProvider = timeProvider ?? TimeProvider.System;
        if (_retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public async Task<IReadOnlyDictionary<string, PrintedReceiptRecord>> LoadAsync(
        string accountContext,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountContext);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await ReadAllAsync(cancellationToken).ConfigureAwait(false);
            var retained = RetainRecent(all);
            if (retained.Count != all.Count)
                await WriteAllAsync(retained, cancellationToken).ConfigureAwait(false);

            return retained
                .Where(item => string.Equals(item.AccountContext, accountContext, StringComparison.Ordinal))
                .GroupBy(item => item.ReceiptId, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.MaxBy(item => item.PrintedAtUtc)!, StringComparer.Ordinal);
        }
        finally { _gate.Release(); }
    }

    public async Task MarkPrintedAsync(
        string accountContext,
        IReadOnlyCollection<string> receiptIds,
        string printerName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountContext);
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        var ids = receiptIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) return;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var records = RetainRecent(await ReadAllAsync(cancellationToken).ConfigureAwait(false));
            var printedAt = _timeProvider.GetUtcNow();
            foreach (var receiptId in ids)
            {
                records.RemoveAll(item =>
                    string.Equals(item.AccountContext, accountContext, StringComparison.Ordinal) &&
                    string.Equals(item.ReceiptId, receiptId, StringComparison.Ordinal));
                records.Add(new PrintedReceiptRecord(accountContext, receiptId, printerName, printedAt));
            }

            records = records
                .OrderByDescending(item => item.PrintedAtUtc)
                .Take(MaxRecords)
                .OrderBy(item => item.PrintedAtUtc)
                .ToList();
            await WriteAllAsync(records, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task<List<PrintedReceiptRecord>> ReadAllAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path)) return [];
        try
        {
            await using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return await JsonSerializer.DeserializeAsync<List<PrintedReceiptRecord>>(stream, JsonOptions, cancellationToken)
                .ConfigureAwait(false) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Локальний файл історії друку пошкоджений.", exception);
        }
    }

    private List<PrintedReceiptRecord> RetainRecent(IEnumerable<PrintedReceiptRecord> records)
    {
        var cutoff = _timeProvider.GetUtcNow() - _retention;
        return records.Where(item => item.PrintedAtUtc >= cutoff).ToList();
    }

    private async Task WriteAllAsync(IReadOnlyList<PrintedReceiptRecord> records, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path)
            ?? throw new InvalidOperationException("Не визначено папку історії друку.");
        Directory.CreateDirectory(directory);
        var temporaryPath = _path + ".tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, records, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
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

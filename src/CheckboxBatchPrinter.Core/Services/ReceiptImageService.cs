using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class ReceiptImageService(
    CheckboxApiClient apiClient,
    ISettingsService settingsService,
    string cacheDirectory,
    IAppLogger logger) : IReceiptImageService
{
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];

    public async Task<byte[]> GetPngAsync(string receiptId, int paperWidthMm, CancellationToken cancellationToken = default)
    {
        paperWidthMm = Math.Clamp(paperWidthMm, 40, 80);
        Directory.CreateDirectory(cacheDirectory);
        var safeId = string.Concat(receiptId.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_'));
        var path = Path.Combine(cacheDirectory, $"{safeId}-{paperWidthMm}.png");
        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= DateTime.UtcNow.AddDays(-settings.CacheRetentionDays))
            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);

        var url = $"{settings.ApiBaseUrl}/api/v1/receipts/{Uri.EscapeDataString(receiptId)}/png" +
                  $"?paper_width={paperWidthMm}&qrcode_scale=75";
        var bytes = await apiClient.GetBytesAsync(url, "receipt.png", receiptId, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < PngSignature.Length || !bytes.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature))
            throw new InvalidDataException("Checkbox повернув дані, які не є PNG-зображенням чека.");

        await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        logger.Info("receipt.cache.write", receiptId);
        return bytes;
    }

    public Task<int> ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var count = 0;
        if (!Directory.Exists(cacheDirectory)) return Task.FromResult(0);
        foreach (var file in Directory.EnumerateFiles(cacheDirectory, "*.png"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Delete(file); count++; }
            catch (IOException) { }
        }
        return Task.FromResult(count);
    }

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(cacheDirectory)) return;
        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var files = new DirectoryInfo(cacheDirectory).EnumerateFiles("*.png")
            .OrderByDescending(x => x.LastWriteTimeUtc).ToList();
        long total = 0;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += file.Length;
            if (file.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-settings.CacheRetentionDays) || total > 500L * 1024 * 1024)
            {
                try { file.Delete(); }
                catch (IOException) { }
            }
        }
    }
}

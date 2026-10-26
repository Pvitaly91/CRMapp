using System.ComponentModel;
using System.Text.Json;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class LocalCacheException() : IOException(
    "Локальний кеш пошкоджений або недоступний. Потрібне відновлення з резервної копії; файли не видалено.");

// Only rebuildable caches opt in. Credentials, manual decisions and print journals must never
// silently fall back to an older value. Callers hold their existing per-file lock across both files.
internal static class ProtectedCacheFile
{
    public static async Task<T?> ReadAsync<T>(string path, CancellationToken token, long maximumBytes = long.MaxValue) where T : class
    {
        try
        {
            var value = await ReadBoundedAsync<T>(path, token, maximumBytes).ConfigureAwait(false);
            if (value is not null) return value;
            if (!File.Exists(path) && !File.Exists(path + ".bak")) return null;
        }
        catch (Exception ex) when (Recoverable(ex)) { }
        try
        {
            var backup = await ReadBoundedAsync<T>(path + ".bak", token, maximumBytes).ConfigureAwait(false);
            if (backup is not null) return backup;
        }
        catch (Exception ex) when (Recoverable(ex)) { }
        throw new LocalCacheException();
    }

    public static async Task WriteAsync<T>(string path, T value, CancellationToken token)
    {
        var json = JsonSerializer.Serialize(value, MarketplaceStoreFiles.JsonOptions);
        // Complete and flush the independent recovery file before replacing the primary.
        // Both copies hold the same validated snapshot; a torn primary write cannot blank the archive.
        await new DpapiCredentialStore(path + ".bak").SavePasswordAsync(json, token).ConfigureAwait(false);
        await new DpapiCredentialStore(path).SavePasswordAsync(json, token).ConfigureAwait(false);
    }

    private static bool Recoverable(Exception ex) => ex is Win32Exception or InvalidDataException or IOException;

    private static Task<T?> ReadBoundedAsync<T>(string path, CancellationToken token, long maximumBytes) where T : class
    {
        if (File.Exists(path) && new FileInfo(path).Length > maximumBytes)
            throw new InvalidDataException("Локальний кеш завеликий.");
        return MarketplaceStoreFiles.ReadProtectedAsync<T>(path, token);
    }
}

using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface ISecureCredentialStore
{
    bool HasPassword { get; }
    Task SavePasswordAsync(string password, CancellationToken cancellationToken = default);
    Task<string?> LoadPasswordAsync(CancellationToken cancellationToken = default);
    Task ClearAsync(CancellationToken cancellationToken = default);
}

public interface ISettingsService
{
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IAppLogger
{
    string LogDirectory { get; }
    void Info(string operation, string? receiptId = null, int? httpStatus = null, string? printStatus = null);
    void Error(string operation, Exception exception, string? receiptId = null, int? httpStatus = null, string? printStatus = null);
}

public interface IAuthenticationService
{
    bool HasStoredCredentials { get; }
    Task<string> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken cancellationToken = default);
    Task SignInAndStoreAsync(string login, string password, CancellationToken cancellationToken = default);
    void InvalidateToken();
}

public interface IReceiptService
{
    Task<IReadOnlyList<ReceiptRecord>> GetReceiptsAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

public interface IReceiptImageService
{
    Task<byte[]> GetPngAsync(string receiptId, int paperWidthMm, CancellationToken cancellationToken = default);
    Task<int> ClearCacheAsync(CancellationToken cancellationToken = default);
    Task CleanupAsync(CancellationToken cancellationToken = default);
}

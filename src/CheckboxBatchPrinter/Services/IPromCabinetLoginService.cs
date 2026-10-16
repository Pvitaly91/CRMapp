using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

// Browser credentials stay in the store's local WebView profile. Settings only
// display a non-secret state; a saved session is verified when reading a label.
public interface IPromCabinetLoginService
{
    string GetStatus(string connectionId);
    Task<string> ConnectAsync(MarketplaceConnection connection);
}

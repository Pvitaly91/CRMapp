using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// The simple UI still creates an explicit shop -> account binding. It never searches
// other accounts, and saving a key is not evidence that a TTN/PDF can be printed.
public static class NovaPoshtaKeySettings
{
    public static bool HasSavedKey(ShippingLabelSettings settings, string? storeId)
    {
        var binding = settings.NovaPoshtaStoreBindings.SingleOrDefault(b => b.MarketplaceConnectionId == storeId);
        return binding is not null && settings.NovaPoshtaConnections.Any(c => c.Id == binding.NovaPoshtaConnectionId && c.ApiKey.Length > 0);
    }

    public static ShippingLabelSettings Apply(ShippingLabelSettings latest, MarketplaceConnection? selectedStore, string apiKey)
    {
        var key = apiKey.Trim();
        if (key.Length == 0) return latest;
        if (selectedStore is not { Enabled: true, Marketplace: MarketplaceKind.Prom })
            throw new InvalidOperationException("Виберіть магазин Prom для ключа Нової пошти.");
        _ = MarketplaceStoreFiles.ConnectionFileId(selectedStore.Id);
        if (!NovaPoshtaReadOnlyContract.ValidKey(key))
            throw new InvalidOperationException("Перевірте формат API-ключа Нової пошти. Значення ключа приховано.");

        var binding = latest.NovaPoshtaStoreBindings.SingleOrDefault(b => b.MarketplaceConnectionId == selectedStore.Id);
        var account = binding is null ? null : latest.NovaPoshtaConnections.SingleOrDefault(c => c.Id == binding.NovaPoshtaConnectionId)
            ?? throw new InvalidOperationException("Збережене NP-підключення магазину недоступне. Потрібно відновити прив’язку магазину.");
        if (account?.ApiKey == key) return latest;
        var shared = account is not null && latest.NovaPoshtaStoreBindings.Any(b =>
            b.NovaPoshtaConnectionId == account.Id && b.MarketplaceConnectionId != selectedStore.Id);
        if (account is not null && !shared)
            return latest with { NovaPoshtaConnections = latest.NovaPoshtaConnections.Select(c => c.Id == account.Id ? c with { ApiKey = key } : c).ToArray() };

        if (latest.NovaPoshtaConnections.Count >= 30)
            throw new InvalidOperationException("Досягнуто межі 30 NP-підключень. Новий ключ не збережено; наявні підключення не змінені.");
        var created = new NovaPoshtaConnection(Guid.NewGuid().ToString("N"), "Нова пошта — підключення магазину", key);
        return latest with
        {
            NovaPoshtaConnections = latest.NovaPoshtaConnections.Append(created).ToArray(),
            NovaPoshtaStoreBindings = latest.NovaPoshtaStoreBindings.Where(b => b.MarketplaceConnectionId != selectedStore.Id)
                .Append(new NovaPoshtaStoreBinding(selectedStore.Id, created.Id)).ToArray()
        };
    }
}

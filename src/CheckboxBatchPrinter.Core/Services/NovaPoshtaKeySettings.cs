using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

// Key rotation updates the existing carrier account, including shared shop bindings.
// Saving a key is not evidence that a TTN/PDF can be printed.
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
        var account = binding is null
            ? latest.NovaPoshtaConnections.Count switch
            {
                0 => null,
                1 => latest.NovaPoshtaConnections[0],
                _ => throw new InvalidOperationException("Збережено кілька підключень Нової пошти. Спочатку відновіть прив’язку цього магазину до потрібного наявного підключення; нове підключення не створено.")
            }
            : latest.NovaPoshtaConnections.SingleOrDefault(c => c.Id == binding.NovaPoshtaConnectionId)
                ?? throw new InvalidOperationException("Збережене NP-підключення магазину недоступне. Потрібно відновити прив’язку магазину.");
        if (account is not null)
        {
            if (account.ApiKey == key && binding is not null) return latest;
            return latest with
            {
                NovaPoshtaConnections = latest.NovaPoshtaConnections.Select(c => c.Id == account.Id ? c with { ApiKey = key } : c).ToArray(),
                NovaPoshtaStoreBindings = binding is not null ? latest.NovaPoshtaStoreBindings
                    : latest.NovaPoshtaStoreBindings.Append(new NovaPoshtaStoreBinding(selectedStore.Id, account.Id)).ToArray()
            };
        }

        // Create only the first carrier account. Subsequent saves retain its identity/history.
        var created = new NovaPoshtaConnection(Guid.NewGuid().ToString("N"), "Нова пошта — підключення магазину", key);
        return latest with
        {
            NovaPoshtaConnections = latest.NovaPoshtaConnections.Append(created).ToArray(),
            NovaPoshtaStoreBindings = latest.NovaPoshtaStoreBindings.Where(b => b.MarketplaceConnectionId != selectedStore.Id)
                .Append(new NovaPoshtaStoreBinding(selectedStore.Id, created.Id)).ToArray()
        };
    }
}

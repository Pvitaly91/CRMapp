using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

internal static class MarketplaceConnectionAliases
{
    internal static IReadOnlyDictionary<(MarketplaceKind Marketplace, string ConnectionId), string> Resolve(MarketplaceSettings settings)
    {
        if (settings.ConnectionAliases is null || settings.ConnectionAliases.Count > 4096)
            throw new InvalidDataException("Некоректний список об’єднаних підключень маркетплейсів.");
        if (settings.ConnectionAliases.Count == 0) return new Dictionary<(MarketplaceKind, string), string>();
        var connections = settings.Connections.ToDictionary(c => Identity(c.Id), StringComparer.Ordinal);
        var aliases = new Dictionary<string, MarketplaceConnectionAlias>(StringComparer.Ordinal);
        foreach (var alias in settings.ConnectionAliases)
        {
            if (alias is null || !Enum.IsDefined(alias.Marketplace))
                throw new InvalidDataException("Некоректне об’єднання підключень маркетплейсу.");
            var source = MarketplaceStoreFiles.ConnectionFileId(alias.SourceConnectionId);
            MarketplaceStoreFiles.ConnectionFileId(alias.TargetConnectionId);
            if (connections.ContainsKey(source) || !aliases.TryAdd(source, alias))
                throw new InvalidDataException("Об’єднане підключення повинно мати один історичний ідентифікатор без окремого активного налаштування.");
        }

        var resolved = new Dictionary<(MarketplaceKind Marketplace, string ConnectionId), string>();
        foreach (var alias in aliases.Values)
        {
            var source = Identity(alias.SourceConnectionId);
            var visited = new HashSet<string>(StringComparer.Ordinal) { source };
            var target = Identity(alias.TargetConnectionId);
            while (aliases.TryGetValue(target, out var next))
            {
                if (!visited.Add(target) || next.Marketplace != alias.Marketplace)
                    throw new InvalidDataException("Об’єднання підключень містить цикл або різні маркетплейси.");
                target = Identity(next.TargetConnectionId);
            }
            if (!connections.TryGetValue(target, out var connection) || connection.Marketplace != alias.Marketplace)
                throw new InvalidDataException("Для об’єднаного підключення відсутнє налаштування того самого маркетплейсу.");
            resolved[(alias.Marketplace, alias.SourceConnectionId)] = connection.Id;
            resolved[(alias.Marketplace, source)] = connection.Id;
        }
        return resolved;
    }

    private static string Identity(string id) => Guid.TryParse(id, out var parsed) ? parsed.ToString("N") : id;
}

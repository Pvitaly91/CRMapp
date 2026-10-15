using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed partial class EstablishedReceiptLinkSession
{
    private const string MigratableGraphVersion = "same-kyiv-day-rozetka-id-first-fallback-v17";

    public void Capture(AutomaticMatchCache cache, string account, IReadOnlyList<ReceiptRecord> receipts,
        IReadOnlyDictionary<string, ReceiptOrderMatch> matches, IReadOnlyList<string> enabledConnectionIds)
    {
        if (!Import(cache, account, enabledConnectionIds)) return;
        Capture(account, receipts, matches, enabledConnectionIds);
        foreach (var id in _pairs.Keys) MarkAttempt(cache, id);
        Export(cache);
    }

    public EstablishedLinkReuse Restore(AutomaticMatchCache cache, string account, IReadOnlyList<ReceiptRecord> receipts,
        IReadOnlyList<MarketplaceOrder> orders, IReadOnlyList<ReceiptOrderDecision> decisions,
        IReadOnlyList<string> enabledConnectionIds)
    {
        if (!Import(cache, account, enabledConnectionIds)) return new(orders, new Dictionary<string, ReceiptOrderMatch>());
        // Per-receipt attempts leave unseen date ranges available for later migration,
        // while preventing invalidated pairs from returning from an older graph.
        if (receipts.Count > 0 && orders.Count > 0) ImportLegacyGraph(cache, account, receipts, orders);
        var result = Restore(account, receipts, orders, decisions, enabledConnectionIds);
        Export(cache);
        return result;
    }

    private bool Import(AutomaticMatchCache cache, string account, IReadOnlyList<string> connections)
    {
        ArgumentNullException.ThrowIfNull(cache);
        if (string.IsNullOrWhiteSpace(account) || cache.AccountContext.Length > 0 && cache.AccountContext != account)
        { Bind(account, connections); return false; }
        if (cache.AccountContext.Length == 0) cache.AccountContext = account;
        Bind(account, connections);
        PruneExactLinks(cache, _clock.GetUtcNow());
        foreach (var (id, saved) in cache.ExactLinks)
        {
            if (!saved.EnabledConnectionIds.SequenceEqual(_connections, StringComparer.Ordinal) || !Enabled(saved.Order)) continue;
            var verified = saved.Attestations.Select(attestation => Raw(attestation.Reference) with
                { VerifiedReceiptId = attestation.ReceiptId, VerifiedAccountContext = account }).ToArray();
            _pairs[id] = new(saved.Order, saved.ReceiptFingerprint, saved.FiscalFingerprint, verified,
                new(ReceiptLinkState.Exact, null, "Збережений точний фіскальний зв’язок; дані чека й документа перевірено повторно.", []),
                saved.AcceptedAtUtc);
        }
        return true;
    }

    private void Export(AutomaticMatchCache cache)
    {
        var entries = _pairs.ToDictionary(pair => pair.Key, pair => new CachedExactReceiptLink(pair.Value.Order,
            pair.Value.ReceiptFingerprint, pair.Value.FiscalFingerprint,
            pair.Value.Verified.Select(reference => new CachedFiscalAttestation(Raw(reference), Normalize(reference.VerifiedReceiptId))).ToArray(),
            _connections, pair.Value.AcceptedAtUtc), StringComparer.Ordinal);
        SetEntries(cache, entries);
        PruneExactLinks(cache, _clock.GetUtcNow());
    }

    private void ImportLegacyGraph(AutomaticMatchCache cache, string account, IReadOnlyList<ReceiptRecord> receipts,
        IReadOnlyList<MarketplaceOrder> orders)
    {
        var byReceipt = receipts.GroupBy(receipt => Normalize(receipt.Id)).ToDictionary(group => group.Key, group => group.First());
        var byOrder = orders.GroupBy(order => order.Key).ToDictionary(group => group.Key, group => group.MaxBy(order => order.RetrievedAtUtc)!);
        var cutoff = _clock.GetUtcNow().AddDays(-Math.Clamp(cache.RetentionDays, 1, 90));
        foreach (var snapshot in cache.Snapshots.Where(snapshot => snapshot.AlgorithmVersion == MigratableGraphVersion &&
                     snapshot.CachedAtUtc >= cutoff && snapshot.CachedAtUtc <= _clock.GetUtcNow()).OrderByDescending(snapshot => snapshot.CachedAtUtc))
        foreach (var (savedId, saved) in snapshot.Matches)
        {
            var id = Normalize(savedId);
            if (_pairs.Count >= MaximumPairs && !_pairs.ContainsKey(id)) continue;
            if (saved.State != ReceiptLinkState.Exact || _pairs.ContainsKey(id) || cache.ExactLinkMigrationAttempts.ContainsKey(id) ||
                !byReceipt.TryGetValue(id, out var receipt)) continue;
            if (saved.Order is not null && !byOrder.ContainsKey(saved.Order)) continue;
            MarkAttempt(cache, id);
            if (saved.State != ReceiptLinkState.Exact || saved.Ambiguous || saved.Order is null ||
                !snapshot.EnabledConnectionIds.SequenceEqual(_connections, StringComparer.Ordinal) ||
                !Enabled(saved.Order) || !byOrder.TryGetValue(saved.Order, out var order) ||
                order.IsCanceled || saved.ReceiptFingerprint != CachedReceiptOrderMatchingService.ReceiptFingerprint(receipt)) continue;
            // This is a hypothesis solely for reproducing an authenticated historical
            // fingerprint. It is never published if even one original model field differs.
            var raw = order with { FiscalReferences = order.FiscalReferences.Select(Raw).ToArray() };
            var hypothesis = raw with { FiscalReferences = raw.FiscalReferences.Select(reference =>
                CanAttest(reference, raw.Key, receipt) ? reference with
                    { VerifiedReceiptId = receipt.Id, VerifiedAccountContext = account } : reference).ToArray() };
            var matched = new[] { raw, hypothesis }.FirstOrDefault(candidate =>
                saved.OrderFingerprint == CachedReceiptOrderMatchingService.OrderFingerprint(candidate) &&
                FiscalReferenceMatching.Evaluate(candidate, receipt, account, receipts) is { Exact: true, Conflict: false });
            if (matched is null) continue;
            var verified = matched.FiscalReferences.Where(reference => reference.VerifiedAccountContext == account &&
                CheckboxReceiptReference.SameId(reference.VerifiedReceiptId, receipt.Id)).ToArray();
            _pairs[id] = new(order.Key, ReceiptFingerprint(receipt), FiscalFingerprint(order), verified,
                new(ReceiptLinkState.Exact, matched, "Відновлений раніше прийнятий точний фіскальний зв’язок.", []), snapshot.CachedAtUtc);
        }
    }

    private static bool CanAttest(FiscalDocumentReference reference, OrderKey order, ReceiptRecord receipt) =>
        reference.Order == order && reference.Source.Length > 0 && reference.DocumentId.Length > 0 &&
        string.Equals(reference.Provider, "Checkbox", StringComparison.OrdinalIgnoreCase) &&
        Guid.TryParse(receipt.Id, out var uuid) && uuid != Guid.Empty && receipt.FiscalCode.Length > 0 &&
        CheckboxReceiptReference.ContextMatches(reference, receipt) &&
        (reference.Kind == FiscalDocumentKeyKind.FiscalCode && reference.Value == receipt.FiscalCode ||
         reference.Kind == FiscalDocumentKeyKind.CheckboxReceiptUrl && CheckboxReceiptReference.TryParseUrl(reference.Value, out var url) &&
            url.FiscalCode == receipt.FiscalCode);

    internal static void PruneExactLinks(AutomaticMatchCache cache, DateTimeOffset now)
    {
        var cutoff = now.AddDays(-Math.Clamp(cache.RetentionDays, 1, 90));
        if (cache.ExactLinkSchema is < 0 or > 1)
        { cache.ExactLinkSchema = 1; SetEntries(cache, []); Changed(cache); }
        if (cache.ExactLinkSchema == 0) { cache.ExactLinkSchema = 1; Changed(cache); }
        var attempts = cache.ExactLinkMigrationAttempts ?? [];
        var retainedAttempts = attempts.Where(pair => Guid.TryParse(pair.Key, out var uuid) && uuid != Guid.Empty &&
                pair.Key == uuid.ToString("D") && pair.Value >= cutoff && pair.Value <= now)
            .OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(MaximumPairs)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        if (cache.ExactLinkMigrationAttempts is null || attempts.Count != retainedAttempts.Count)
        {
            // Removing a tombstone also consumes its old graph entries. A bounded
            // migration history must never resurrect proof after evidence changed.
            var discarded = attempts.Keys.Except(retainedAttempts.Keys, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
            if (discarded.Count > 0)
                cache.Snapshots = cache.Snapshots.Select(snapshot => snapshot with { Matches = snapshot.Matches
                    .Where(pair => pair.Value.State != ReceiptLinkState.Exact || !discarded.Contains(Normalize(pair.Key)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal) }).ToList();
            cache.ExactLinkMigrationAttempts = retainedAttempts; Changed(cache);
        }
        var valid = (cache.ExactLinks ?? []).Where(pair => ValidEntry(pair.Key, pair.Value) &&
                pair.Value.AcceptedAtUtc >= cutoff && pair.Value.AcceptedAtUtc <= now)
            .OrderByDescending(pair => pair.Value.AcceptedAtUtc).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(MaximumPairs)
            .ToDictionary(pair => Normalize(pair.Key), pair => pair.Value, StringComparer.Ordinal);
        SetEntries(cache, valid);
    }

    private static bool ValidEntry(string id, CachedExactReceiptLink? entry) =>
        Guid.TryParse(id, out var receiptId) && receiptId != Guid.Empty && id == receiptId.ToString("D") &&
        entry is { Schema: 1, Order: not null, Attestations: not null, EnabledConnectionIds: not null } &&
        Enum.IsDefined(entry.Order.Marketplace) && !string.IsNullOrWhiteSpace(entry.Order.ConnectionId) &&
        !string.IsNullOrWhiteSpace(entry.Order.OrderId) && HashShape(entry.ReceiptFingerprint) && HashShape(entry.FiscalFingerprint) &&
        entry.EnabledConnectionIds.Count > 0 && entry.EnabledConnectionIds.All(value => !string.IsNullOrWhiteSpace(value)) &&
        entry.EnabledConnectionIds.Contains($"{entry.Order.Marketplace}:{entry.Order.ConnectionId}", StringComparer.Ordinal) &&
        entry.Attestations.Count <= 100 && entry.Attestations.All(attestation => attestation is { Reference: not null } &&
            CheckboxReceiptReference.SameId(attestation.ReceiptId, id) && attestation.Reference.Order == entry.Order &&
            Enum.IsDefined(attestation.Reference.Kind) && !string.IsNullOrWhiteSpace(attestation.Reference.Value) &&
            !string.IsNullOrWhiteSpace(attestation.Reference.Source) && !string.IsNullOrWhiteSpace(attestation.Reference.DocumentId) &&
            string.Equals(attestation.Reference.Provider, "Checkbox", StringComparison.OrdinalIgnoreCase) &&
            attestation.Reference.OrganizationId is not null && attestation.Reference.CashRegisterFiscalNumber is not null);

    private static bool HashShape(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);

    private void MarkAttempt(AutomaticMatchCache cache, string id)
    {
        if (!Guid.TryParse(id, out var uuid) || uuid == Guid.Empty) return;
        var current = _clock.GetUtcNow();
        // A current accepted pair renews the tombstone only after a meaningful
        // interval, avoiding a new disk write for every local projection.
        if (cache.ExactLinkMigrationAttempts.TryGetValue(id, out var previous) && current - previous < TimeSpan.FromDays(1)) return;
        cache.ExactLinkMigrationAttempts[id] = current; Changed(cache);
    }

    private static void SetEntries(AutomaticMatchCache cache, Dictionary<string, CachedExactReceiptLink> entries)
    {
        if (cache.ExactLinks is not null && cache.ExactLinks.Count == entries.Count && entries.All(pair =>
                cache.ExactLinks.TryGetValue(pair.Key, out var previous) && SameEntry(previous, pair.Value))) return;
        cache.ExactLinks = entries; Changed(cache);
    }

    private static bool SameEntry(CachedExactReceiptLink? first, CachedExactReceiptLink second) =>
        first is not null && first.Schema == second.Schema && first.Order == second.Order &&
        first.ReceiptFingerprint == second.ReceiptFingerprint && first.FiscalFingerprint == second.FiscalFingerprint &&
        first.AcceptedAtUtc == second.AcceptedAtUtc && first.EnabledConnectionIds is not null && first.Attestations is not null &&
        first.EnabledConnectionIds.SequenceEqual(second.EnabledConnectionIds, StringComparer.Ordinal) &&
        first.Attestations.SequenceEqual(second.Attestations);

    private static void Changed(AutomaticMatchCache cache)
    { cache.ExactLinksChanged = true; cache.ExactLinksRevision++; }
}

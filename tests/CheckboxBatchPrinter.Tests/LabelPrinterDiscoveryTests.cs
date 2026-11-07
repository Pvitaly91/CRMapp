using System.Collections.Concurrent;
using System.Net;
using CheckboxBatchPrinter.Services;
using static CheckboxBatchPrinter.Tests.ShippingLabelsTests;

namespace CheckboxBatchPrinter.Tests;

internal static class LabelPrinterDiscoveryTests
{
    public static IReadOnlyList<(string Name, Func<Task> Test)> All =>
    [
        ("label printer discovery tries the last successful print address first", PrefersLastPrinted),
        ("label printer discovery rejects other devices and scans only the configured range", ScansAfterPrinterMoves),
        ("label printer discovery returns no destination when the printer is absent", NoPrinterFound),
        ("label printer discovery honors cancellation before and during probes", CancellationStopsSearch)
    ];

    private static IPAddress Address(int lastOctet) => IPAddress.Parse($"192.168.0.{lastOctet}");

    private static async Task PrefersLastPrinted()
    {
        var lastPrinted = Address(104);
        var current = Address(100);
        var probed = new ConcurrentQueue<IPAddress>();

        var found = await LabelPrinterDiscovery.FindAddressAsync(lastPrinted, current, (address, _) =>
        {
            probed.Enqueue(address);
            return Task.FromResult(true);
        }, CancellationToken.None);

        Check(lastPrinted.Equals(found) && probed.SequenceEqual([lastPrinted]),
            "A verified address from the last successful print must suppress all other probes.");
    }

    private static async Task ScansAfterPrinterMoves()
    {
        var oldAddress = Address(106);
        var current = Address(102);
        var newAddress = Address(105);
        var probed = new ConcurrentQueue<IPAddress>();
        var oldProbe = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var search = LabelPrinterDiscovery.FindAddressAsync(oldAddress, current, (address, _) =>
        {
            probed.Enqueue(address);
            // The old address is occupied by another device, so the identity probe rejects it.
            return address.Equals(oldAddress) ? oldProbe.Task : Task.FromResult(address.Equals(newAddress));
        }, CancellationToken.None);
        var firstProbeOnly = probed.SequenceEqual([oldAddress]);
        oldProbe.SetResult(false);
        var found = await search;
        var probes = probed.ToArray();
        var allowed = Enumerable.Range(100, 7).Select(Address).ToHashSet();

        Check(firstProbeOnly, "Range scanning began before the old address was checked.");
        Check(newAddress.Equals(found), "Discovery did not select the verified printer at its new address.");
        Check(probes.Length == probes.Distinct().Count() && probes.All(allowed.Contains),
            "Discovery repeated an address or probed outside 192.168.0.100–192.168.0.106.");
    }

    private static async Task NoPrinterFound()
    {
        var allowed = Enumerable.Range(100, 7).Select(Address).ToHashSet();
        foreach (var (lastPrinted, current) in new (IPAddress?, IPAddress?)[]
        {
            (null, null),
            (Address(99), Address(107)),
            (null, Address(103))
        })
        {
            var probed = new ConcurrentQueue<IPAddress>();
            var found = await LabelPrinterDiscovery.FindAddressAsync(lastPrinted, current, (address, _) =>
            {
                probed.Enqueue(address);
                return Task.FromResult(false);
            }, CancellationToken.None);

            Check(found is null && probed.Count == allowed.Count && allowed.SetEquals(probed),
                "An unsuccessful search must check each address once and return no destination.");
            if (current is not null && allowed.Contains(current))
                Check(current.Equals(probed.First()), "Use the current port first when no successful address is known.");
        }
    }

    private static async Task CancellationStopsSearch()
    {
        var address = Address(104);
        foreach (var cancelBeforeSearch in new[] { true, false })
        {
            using var cancellation = new CancellationTokenSource();
            var probeCount = 0;
            if (cancelBeforeSearch) cancellation.Cancel();
            try
            {
                await LabelPrinterDiscovery.FindAddressAsync(address, null, (_, token) =>
                {
                    Check(token == cancellation.Token, "Discovery did not pass the cancellation token to the probe.");
                    probeCount++;
                    cancellation.Cancel();
                    token.ThrowIfCancellationRequested();
                    return Task.FromResult(false);
                }, cancellation.Token);
                throw new Exception("Discovery swallowed cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }

            Check(probeCount == (cancelBeforeSearch ? 0 : 1), "Discovery sent probes after cancellation.");
        }
    }
}

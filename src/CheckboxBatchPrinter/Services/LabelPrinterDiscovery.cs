using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Services;

internal sealed class LabelPrinterDiscovery(string bindingPath)
{
    internal const string BindingFileName = "network-printer-mac.txt";
    internal const string QueueName = "Xprinter XP-420B LAN";
    internal const int PrintPort = 9100;
    private static readonly IPAddress[] Addresses = Enumerable.Range(100, 7)
        .Select(lastOctet => IPAddress.Parse($"192.168.0.{lastOctet}")).ToArray();

    internal async Task PrepareAsync(string queueName, CancellationToken cancellationToken)
    {
        if (!string.Equals(queueName, QueueName, StringComparison.OrdinalIgnoreCase)) return;

        string macText;
        try { macText = (await File.ReadAllTextAsync(bindingPath, cancellationToken).ConfigureAwait(false)).Trim(); }
        catch (FileNotFoundException) { return; } // Network discovery is enabled by the local device binding.
        catch (DirectoryNotFoundException) { return; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw Failure("Не вдалося прочитати прив’язку мережевого принтера наклейок."); }
        if (!PhysicalAddress.TryParse(macText, out var mac) || mac.GetAddressBytes().Length != 6)
            throw Failure("У прив’язці мережевого принтера наклейок некоректна MAC-адреса.");
        var expectedMac = mac.GetAddressBytes();

        try
        {
            var (current, lastPrinted) = await Task.Run(() => WindowsLabelQueue.ReadAddresses(queueName), cancellationToken)
                .ConfigureAwait(false);
            var address = await FindAddressAsync(lastPrinted, current, (candidate, token) =>
                IsPrinterAsync(candidate, expectedMac, token), cancellationToken).ConfigureAwait(false);
            if (address is null)
                throw Failure("Принтер наклейок Xprinter недоступний за адресами 192.168.0.100–192.168.0.106. Перевірте живлення та мережу принтера.");

            await Task.Run(() => WindowsLabelQueue.UseAddress(queueName, address), cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex) { throw Failure(ex.Message); }
    }

    internal static async Task<IPAddress?> FindAddressAsync(IPAddress? lastPrinted, IPAddress? current,
        Func<IPAddress, CancellationToken, Task<bool>> isPrinter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var first = Addresses.Contains(lastPrinted) ? lastPrinted : Addresses.Contains(current) ? current : null;
        if (first is not null)
        {
            var verified = await isPrinter(first, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (verified) return first;
        }

        var remaining = Addresses.Where(address => !address.Equals(first));
        var checkedAddresses = await Task.WhenAll(remaining.Select(async address =>
            await isPrinter(address, cancellationToken).ConfigureAwait(false) ? address : null)).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return checkedAddresses.FirstOrDefault(address => address is not null);
    }

    private static async Task<bool> IsPrinterAsync(IPAddress address, byte[] expectedMac, CancellationToken cancellationToken)
    {
        using var client = new TcpClient(AddressFamily.InterNetwork);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try { await client.ConnectAsync(address, PrintPort, timeout.Token).ConfigureAwait(false); }
        catch (SocketException) { return false; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return false; }

        return await Task.Run(() =>
        {
            // TCP connectivity alone could be a different device after DHCP reassigns an address.
            var buffer = new byte[8];
            uint size = (uint)buffer.Length;
            return SendARP(BitConverter.ToUInt32(address.GetAddressBytes()), 0, buffer, ref size) == 0 &&
                size == expectedMac.Length && buffer.AsSpan(0, (int)size).SequenceEqual(expectedMac);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static ShippingLabelException Failure(string message) => new(LabelFailureKind.ConnectionRequired, message);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint SendARP(uint destination, uint source, [Out] byte[] mac, ref uint size);
}

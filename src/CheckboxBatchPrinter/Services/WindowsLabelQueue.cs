using System.ComponentModel;
using System.Diagnostics.Eventing.Reader;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Win32.SafeHandles;

namespace CheckboxBatchPrinter.Services;

internal static class WindowsLabelQueue
{
    private const string PrintLog = "Microsoft-Windows-PrintService/Operational";
    private const uint PrinterUse = 8, PrinterAdminister = 4, ServerAdminister = 1;
    private const uint RawProtocol = 1, RawPort = 9100, LocalPrinter = 0x40;
    private const int AccessDenied = 5, InsufficientBuffer = 122, AlreadyExists = 183;

    internal static (IPAddress? Current, IPAddress? LastPrinted) ReadAddresses(string queueName)
    {
        using var printer = Open(queueName, PrinterUse, "прочитати чергу принтера наклейок");
        var info = ReadQueue(printer);
        if (!IsSingleLocalPort(info)) return (null, null);
        var current = ReadPortAddress(info.PortName);
        var lastPort = ReadLastPrintedPort(info.PrinterName);
        var lastPrinted = string.Equals(lastPort, info.PortName, StringComparison.OrdinalIgnoreCase)
            ? current : ReadPortAddress(lastPort);
        return (current, lastPrinted);
    }

    internal static void UseAddress(string queueName, IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
            throw new InvalidOperationException("Для порту принтера наклейок потрібна адреса IPv4.");
        using (var reader = Open(queueName, PrinterUse, "прочитати чергу принтера наклейок"))
            if (RequireAddress(ReadQueue(reader)).Equals(address)) return;

        using var printer = Open(queueName, PrinterUse | PrinterAdminister, "змінити порт принтера наклейок");
        var info = ReadQueue(printer);
        if (RequireAddress(info).Equals(address)) return;
        var desiredPort = "IP_" + address;
        var ports = ReadPorts();
        // Windows can register TCPMON under different names on different installations.
        var monitorName = ports.FirstOrDefault(port =>
            string.Equals(port.PortName, info.PortName, StringComparison.OrdinalIgnoreCase)).MonitorName;
        if (string.IsNullOrEmpty(monitorName))
            throw new InvalidOperationException("Windows не знайшла монітор TCP/IP порту принтера наклейок.");
        var matchingPort = ports.Where(port => string.Equals(port.MonitorName, monitorName, StringComparison.OrdinalIgnoreCase))
            .Select(port => port.PortName).FirstOrDefault(name => address.Equals(ReadPortAddress(name)));
        if (matchingPort is null)
        {
            AddTcpPort(desiredPort, address);
            matchingPort = desiredPort;
        }
        if (!address.Equals(ReadPortAddress(matchingPort)))
            throw new InvalidOperationException("Налаштування мережевого порту змінилися. Повторіть друк наклейок.");

        // Preserve attributes and timeouts while changing only the queue's port binding.
        info = ReadQueue(printer);
        _ = RequireAddress(info);
        if (string.Equals(info.PortName, matchingPort, StringComparison.OrdinalIgnoreCase)) return;
        info.PortName = matchingPort;
        if (!SetPrinter(printer, 5, in info, 0)) throw WindowsFailure("змінити порт принтера наклейок");
        if (!string.Equals(ReadQueue(printer).PortName, matchingPort, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Windows не підтвердила зміну порту. Перевірте чергу принтера наклейок.");
    }

    private static bool IsSingleLocalPort(PrinterInfo info) =>
        (info.Attributes & LocalPrinter) != 0 && !string.IsNullOrEmpty(info.PortName) && !info.PortName.Contains(',');

    private static IPAddress RequireAddress(PrinterInfo info) =>
        (IsSingleLocalPort(info) ? ReadPortAddress(info.PortName) : null)
        ?? throw new InvalidOperationException("Перевірте, що черга принтера наклейок у Windows використовує один стандартний TCP/IP порт RAW 9100 з адресою IPv4.");

    private static string? ReadLastPrintedPort(string queueName)
    {
        // A successful EndDoc only spools the document. Event 307 identifies the port
        // Windows reported after processing the job, even when the queue has since moved.
        var xpath = "*[System[EventID=307]]";
        var delimiter = !queueName.Contains('\'') ? "'" : !queueName.Contains('"') ? "\"" : null;
        if (delimiter is not null)
            xpath = $"*[System[EventID=307] and UserData[DocumentPrinted[Param5={delimiter}{queueName}{delimiter}]]]";
        try
        {
            var query = new EventLogQuery(PrintLog, PathType.LogName, xpath) { ReverseDirection = true };
            using var reader = new EventLogReader(query);
            XNamespace eventNs = "http://schemas.microsoft.com/win/2004/08/events/event";
            XNamespace printNs = "http://manifests.microsoft.com/win/2005/08/windows/printing/spooler/core/events";
            while (true)
            {
                using var record = reader.ReadEvent();
                if (record is null) return null;
                var document = XElement.Parse(record.ToXml()).Element(eventNs + "UserData")?.Element(printNs + "DocumentPrinted");
                if (string.Equals((string?)document?.Element(printNs + "Param5"), queueName, StringComparison.OrdinalIgnoreCase))
                    return (string?)document?.Element(printNs + "Param6");
            }
        }
        catch (Exception exception) when (exception is EventLogException or UnauthorizedAccessException or XmlException)
        {
            return null;
        }
    }

    private static IPAddress? ReadPortAddress(string? portName)
    {
        if (string.IsNullOrEmpty(portName)) return null;
        if (!OpenPrinter(",XcvPort " + portName, out var monitor, in NoAccess)) return null;
        using (monitor)
        {
            var input = new ConfigInfo { Reserved = new byte[128], Version = 1 };
            if (!ReadConfig(monitor, "GetConfigInfo", in input, (uint)Marshal.SizeOf<ConfigInfo>(),
                    out var data, (uint)Marshal.SizeOf<PortData>(), out _, out var status) ||
                status != 0 || data.Protocol != RawProtocol || data.PortNumber != RawPort)
                return null;
            var host = string.IsNullOrWhiteSpace(data.HostAddress) ? data.IpAddress : data.HostAddress;
            return IPAddress.TryParse(host, out var address) && address.AddressFamily == AddressFamily.InterNetwork
                ? address : null;
        }
    }

    private static IReadOnlyList<PortInfo> ReadPorts()
    {
        if (EnumPorts(null, 2, IntPtr.Zero, 0, out var size, out _)) return [];
        if (Marshal.GetLastWin32Error() != InsufficientBuffer) throw WindowsFailure("прочитати порти принтерів");
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!EnumPorts(null, 2, buffer, size, out _, out var count)) throw WindowsFailure("прочитати порти принтерів");
            var ports = new List<PortInfo>();
            for (var index = 0; index < count; index++)
                ports.Add(Marshal.PtrToStructure<PortInfo>(IntPtr.Add(buffer, index * Marshal.SizeOf<PortInfo>())));
            return ports;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static void AddTcpPort(string portName, IPAddress address)
    {
        // EnumPorts can return TCPMON.DLL, but the Xcv monitor opens by its registered name.
        using var monitor = Open(",XcvMonitor Standard TCP/IP Port", ServerAdminister, "створити TCP/IP порт принтера");
        var data = new PortData
        {
            PortName = portName,
            Version = 1,
            Protocol = RawProtocol,
            Size = (uint)Marshal.SizeOf<PortData>(),
            HostAddress = address.ToString(),
            SnmpCommunity = "",
            Queue = "",
            IpAddress = "",
            Reserved = new byte[540],
            PortNumber = RawPort
        };
        if (!AddPort(monitor, "AddPort", in data, data.Size, IntPtr.Zero, 0, out _, out var status))
            throw WindowsFailure("створити TCP/IP порт принтера");
        // A concurrent creation is harmless only if UseAddress verifies the final port config.
        if (status != 0 && status != AlreadyExists)
            throw WindowsFailure("створити TCP/IP порт принтера", checked((int)status));
    }

    private static SafePrinterHandle Open(string name, uint access, string operation)
    {
        var defaults = new PrinterDefaults { DesiredAccess = access };
        if (!OpenPrinter(name, out var handle, in defaults)) throw WindowsFailure(operation);
        return handle;
    }

    private static PrinterInfo ReadQueue(SafePrinterHandle printer)
    {
        GetPrinter(printer, 5, IntPtr.Zero, 0, out var size);
        if (Marshal.GetLastWin32Error() != InsufficientBuffer) throw WindowsFailure("прочитати порт принтера наклейок");
        var buffer = Marshal.AllocHGlobal(checked((int)size));
        try
        {
            if (!GetPrinter(printer, 5, buffer, size, out _)) throw WindowsFailure("прочитати порт принтера наклейок");
            return Marshal.PtrToStructure<PrinterInfo>(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static InvalidOperationException WindowsFailure(string operation, int? code = null)
    {
        var error = code ?? Marshal.GetLastWin32Error();
        var guidance = error == AccessDenied ? " Перевірте права на керування принтером."
            : " Перевірте чергу принтера в налаштуваннях Windows.";
        return new InvalidOperationException($"Windows не вдалося {operation} (код {error})." + guidance,
            new Win32Exception(error));
    }

    private static readonly PrinterDefaults NoAccess = default;

    [StructLayout(LayoutKind.Sequential)]
    private struct PrinterDefaults
    {
        public IntPtr DataType, DevMode;
        public uint DesiredAccess;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PrinterInfo
    {
        public string PrinterName, PortName;
        public uint Attributes, DeviceNotSelectedTimeout, TransmissionRetryTimeout;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PortInfo
    {
        public string PortName, MonitorName, Description;
        public uint PortType, Reserved;
    }

    // Layouts from the Windows SDK tcpxcv.h. All reserved bytes are zero on input.
    [StructLayout(LayoutKind.Sequential)]
    private struct ConfigInfo
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 128)] public byte[] Reserved;
        public uint Version;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PortData
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string PortName;
        public uint Version, Protocol, Size, ReservedWord;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 49)] public string HostAddress;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string SnmpCommunity;
        public uint DoubleSpool;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string Queue;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string IpAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 540)] public byte[] Reserved;
        public uint PortNumber, SnmpEnabled, SnmpDeviceIndex;
    }

    private sealed class SafePrinterHandle() : SafeHandleZeroOrMinusOneIsInvalid(true)
    {
        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool OpenPrinter(string name, out SafePrinterHandle printer, in PrinterDefaults defaults);
    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr printer);
    [DllImport("winspool.drv", EntryPoint = "GetPrinterW", SetLastError = true)]
    private static extern bool GetPrinter(SafePrinterHandle printer, uint level, IntPtr buffer, uint size, out uint needed);
    [DllImport("winspool.drv", EntryPoint = "SetPrinterW", SetLastError = true)]
    private static extern bool SetPrinter(SafePrinterHandle printer, uint level, in PrinterInfo info, uint command);
    [DllImport("winspool.drv", EntryPoint = "EnumPortsW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumPorts(string? server, uint level, IntPtr buffer, uint size, out uint needed, out uint returned);
    [DllImport("winspool.drv", EntryPoint = "XcvDataW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ReadConfig(SafePrinterHandle monitor, string command, in ConfigInfo input,
        uint inputSize, out PortData output, uint outputSize, out uint needed, out uint status);
    [DllImport("winspool.drv", EntryPoint = "XcvDataW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool AddPort(SafePrinterHandle monitor, string command, in PortData input,
        uint inputSize, IntPtr output, uint outputSize, out uint needed, out uint status);
}

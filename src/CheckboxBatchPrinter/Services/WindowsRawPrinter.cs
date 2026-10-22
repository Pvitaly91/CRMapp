using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CheckboxBatchPrinter.Services;

// Sends complete printer-language bytes through the spooler. In particular, this
// is not an alternative way to submit an XPS, PDF, or PNG document to a driver.
internal static class WindowsRawPrinter
{
    private const int MaximumWriteBytes = 64 * 1024;

    internal static bool IsSupportedDriver(string? driverName) =>
        string.Equals(driverName, "RPP210", StringComparison.OrdinalIgnoreCase);

    internal static void ValidatePrinter(string printerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        using var connection = NativeConnection.Open(printerName);
    }

    internal static uint Write(string printerName, byte[] data, string jobName,
        CancellationToken cancellationToken = default) =>
        Write(printerName, data, jobName, NativeConnection.Open, cancellationToken);

    internal static uint Write(string printerName, byte[] data, string jobName,
        Func<string, IConnection> open, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(printerName);
        ArgumentException.ThrowIfNullOrWhiteSpace(jobName);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(open);
        if (data.Length == 0) throw new ArgumentException("Завдання друку порожнє.", nameof(data));
        cancellationToken.ThrowIfCancellationRequested();

        using var connection = open(printerName);
        var started = false;
        var completed = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var jobId = connection.StartDocument(jobName, "RAW");
            if (jobId == 0) throw new InvalidOperationException("Windows не створила завдання друку.");
            started = true;
            cancellationToken.ThrowIfCancellationRequested();
            connection.StartPage();
            for (var offset = 0; offset < data.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(MaximumWriteBytes, data.Length - offset);
                var written = connection.Write(data, offset, count);
                if (written <= 0 || written > count)
                    throw new InvalidOperationException("Windows не підтвердила передавання даних друку. Результат поточного завдання невизначений.");
                // A successful partial write advances only by the accepted bytes.
                // A failed write throws; the failed portion is never retransmitted.
                offset += written;
            }
            cancellationToken.ThrowIfCancellationRequested();
            connection.EndPage();
            cancellationToken.ThrowIfCancellationRequested();
            connection.EndDocument();
            completed = true;
            // Cancellation after EndDoc must not turn an accepted job into a retry.
            return jobId;
        }
        finally
        {
            if (started && !completed)
            {
                // This handle owns only the job created above. Never purge a queue
                // or touch other users' jobs; abort cannot undo physical output.
                try { connection.Abort(); }
                catch (Exception) { /* Preserve the original failure/uncertainty. */ }
            }
        }
    }

    internal interface IConnection : IDisposable
    {
        uint StartDocument(string name, string dataType);
        void StartPage();
        int Write(byte[] data, int offset, int count);
        void EndPage();
        void EndDocument();
        void Abort();
    }

    private sealed class NativeConnection(SafePrinterHandle handle) : IConnection
    {
        internal static IConnection Open(string printerName)
        {
            if (!OpenPrinter(printerName, out var handle, IntPtr.Zero))
            {
                var error = Marshal.GetLastWin32Error();
                handle?.Dispose();
                throw Failure(error, "відкрити принтер");
            }
            try
            {
                VerifyDriver(handle);
                return new NativeConnection(handle);
            }
            catch { handle.Dispose(); throw; }
        }

        private static void VerifyDriver(SafePrinterHandle handle)
        {
            // Re-read the actual driver on the opened handle, after UI preflight.
            // A renamed/reconfigured queue must never receive another model's bytes.
            _ = GetPrinterDriver(handle, null, 2, IntPtr.Zero, 0, out var needed);
            if (needed <= 0 || needed > 1024 * 1024)
                throw Failure(Marshal.GetLastWin32Error(), "перевірити драйвер принтера");
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetPrinterDriver(handle, null, 2, buffer, needed, out _))
                    throw Failure(Marshal.GetLastWin32Error(), "перевірити драйвер принтера");
                var driver = Marshal.PtrToStructure<DriverInfo>(buffer);
                if (!IsSupportedDriver(Marshal.PtrToStringUni(driver.Name)))
                    throw new InvalidOperationException("Прямий друк чеків підтримується лише драйвером RPP210. Драйвер змінився; завдання не створено.");
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public uint StartDocument(string name, string dataType)
        {
            var info = new DocumentInfo { DocumentName = name, DataType = dataType };
            var jobId = StartDocPrinter(handle, 1, ref info);
            if (jobId == 0) throw Failure(Marshal.GetLastWin32Error(), "створити завдання друку");
            return jobId;
        }

        public void StartPage()
        {
            if (!StartPagePrinter(handle)) throw Failure(Marshal.GetLastWin32Error(), "почати сторінку друку");
        }

        public int Write(byte[] data, int offset, int count)
        {
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                if (!WritePrinter(handle, IntPtr.Add(pin.AddrOfPinnedObject(), offset), count, out var written))
                    throw Failure(Marshal.GetLastWin32Error(), "передати дані друку");
                return written;
            }
            finally { pin.Free(); }
        }

        public void EndPage()
        {
            if (!EndPagePrinter(handle)) throw Failure(Marshal.GetLastWin32Error(), "завершити сторінку друку");
        }

        public void EndDocument()
        {
            if (!EndDocPrinter(handle)) throw Failure(Marshal.GetLastWin32Error(), "завершити передавання завдання");
        }

        public void Abort()
        {
            if (!AbortPrinter(handle)) throw Failure(Marshal.GetLastWin32Error(), "скасувати перерване завдання");
        }

        public void Dispose() => handle.Dispose();

        private static Win32Exception Failure(int code, string action) =>
            new(code, $"Не вдалося {action} (Windows {code}). Поточне завдання не буде повторено автоматично.");
    }

    private sealed class SafePrinterHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafePrinterHandle() : base(true) { }
        protected override bool ReleaseHandle() => ClosePrinter(handle);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DocumentInfo
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string DocumentName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? OutputFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string DataType;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DriverInfo
    {
        public uint Version;
        public IntPtr Name;
        public IntPtr Environment;
        public IntPtr DriverPath;
        public IntPtr DataFile;
        public IntPtr ConfigurationFile;
    }

    [DllImport("winspool.drv", EntryPoint = "OpenPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenPrinter(string printerName, out SafePrinterHandle handle, IntPtr defaults);

    [DllImport("winspool.drv", EntryPoint = "GetPrinterDriverW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPrinterDriver(SafePrinterHandle handle, string? environment, int level,
        IntPtr driverInfo, int bufferSize, out int needed);

    [DllImport("winspool.drv", EntryPoint = "StartDocPrinterW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint StartDocPrinter(SafePrinterHandle handle, int level, ref DocumentInfo info);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartPagePrinter(SafePrinterHandle handle);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WritePrinter(SafePrinterHandle handle, IntPtr data, int count, out int written);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndPagePrinter(SafePrinterHandle handle);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndDocPrinter(SafePrinterHandle handle);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AbortPrinter(SafePrinterHandle handle);

    [DllImport("winspool.drv", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ClosePrinter(IntPtr handle);
}

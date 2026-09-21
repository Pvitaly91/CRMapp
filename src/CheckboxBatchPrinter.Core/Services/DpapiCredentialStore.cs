using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class DpapiCredentialStore(string credentialPath) : ISecureCredentialStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private const int CryptProtectAudit = 0x10;

    public bool HasPassword => File.Exists(credentialPath) && new FileInfo(credentialPath).Length > 0;

    public async Task SavePasswordAsync(string password, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        var protectedBytes = Protect(Encoding.UTF8.GetBytes(password));
        Directory.CreateDirectory(Path.GetDirectoryName(credentialPath)!);
        var temporaryPath = credentialPath + ".tmp";
        await File.WriteAllBytesAsync(temporaryPath, protectedBytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporaryPath, credentialPath, true);
    }

    public async Task<string?> LoadPasswordAsync(CancellationToken cancellationToken = default)
    {
        if (!HasPassword)
            return null;

        var protectedBytes = await File.ReadAllBytesAsync(credentialPath, cancellationToken).ConfigureAwait(false);
        var plain = Unprotect(protectedBytes);
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            Array.Clear(plain);
        }
    }

    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (File.Exists(credentialPath))
            File.Delete(credentialPath);
        return Task.CompletedTask;
    }

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);
    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DataBlob();
        var output = new DataBlob();
        try
        {
            input.Size = data.Length;
            input.Data = Marshal.AllocHGlobal(data.Length);
            Marshal.Copy(data, 0, input.Data, data.Length);

            var success = protect
                ? CryptProtectData(ref input, "Checkbox Batch Printer credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden | CryptProtectAudit, ref output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, ref output);
            if (!success)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI operation failed.");

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        finally
        {
            if (input.Data != IntPtr.Zero)
            {
                for (var i = 0; i < input.Size; i++) Marshal.WriteByte(input.Data, i, 0);
                Marshal.FreeHGlobal(input.Data);
            }
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, ref DataBlob dataOut);

    [DllImport("Crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, IntPtr optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, ref DataBlob dataOut);

    [DllImport("Kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

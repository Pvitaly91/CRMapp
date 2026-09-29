using System.IO.Pipes;
using System.IO;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CheckboxBatchPrinter.Infrastructure;

internal sealed class SingleInstanceGuard : IDisposable
{
    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly string _pipeName;
    private Task? _server;
    public bool IsPrimary { get; }

    public SingleInstanceGuard(string channel, string dataRoot)
    {
        var sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        var key = $"{sid}|{channel}|{Path.GetFullPath(dataRoot).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant()}";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32];
        _pipeName = $"CRMapp-{hash}";
        // Lifetime is the named object's open handle, not thread-affine mutex ownership.
        _mutex = new Mutex(false, $"Local\\CRMapp-{hash}", out var created);
        IsPrimary = created;
    }

    public void StartServer(Func<Task> openWindow, string version)
    {
        if (!IsPrimary) throw new InvalidOperationException("Сервер може запустити лише основний екземпляр.");
        _server = ServeAsync(openWindow, version);
    }

    private async Task ServeAsync(Func<Task> openWindow, string version)
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                using var pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_shutdown.Token);
                try
                {
                    var command = new byte[1];
                    if (await pipe.ReadAsync(command, _shutdown.Token) == 1 && command[0] == 1)
                    {
                        await openWindow();
                        var payload = Encoding.UTF8.GetBytes(version);
                        if (payload.Length > 120) throw new InvalidOperationException("Version response too long.");
                        await pipe.WriteAsync(new[] { (byte)payload.Length }, _shutdown.Token);
                        await pipe.WriteAsync(payload, _shutdown.Token);
                        await pipe.FlushAsync(_shutdown.Token);
                    }
                }
                catch (IOException) when (!_shutdown.IsCancellationRequested) { } // Disconnected duplicate; accept the next one.
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (IOException) when (_shutdown.IsCancellationRequested) { }
    }

    public async Task<string?> ActivateExistingAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(new byte[] { 1 }, timeout.Token);
        await pipe.FlushAsync(timeout.Token);
        var size = new byte[1];
        await pipe.ReadExactlyAsync(size, timeout.Token);
        if (size[0] == 0 || size[0] > 120) throw new InvalidDataException("Invalid local activation response.");
        var response = new byte[size[0]];
        await pipe.ReadExactlyAsync(response, timeout.Token);
        return Encoding.UTF8.GetString(response);
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _shutdown.Dispose();
        _mutex.Dispose();
    }
}

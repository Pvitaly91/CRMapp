using System.Collections.Concurrent;

namespace CheckboxBatchPrinter.Services;

internal sealed class StaPrintWorker : IDisposable
{
    private readonly BlockingCollection<IWorkItem> _queue = new();
    private readonly Thread _thread;
    private bool _disposed;

    public StaPrintWorker()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "CheckboxBatchPrinter.STA.PrintWorker"
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<T>(cancellationToken);

        var item = new WorkItem<T>(action, cancellationToken);
        _queue.Add(item, cancellationToken);
        return item.Task;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join(TimeSpan.FromSeconds(3));
        _queue.Dispose();
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable()) item.Execute();
    }

    private interface IWorkItem { void Execute(); }

    private sealed class WorkItem<T>(Func<T> action, CancellationToken cancellationToken) : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> Task => _completion.Task;

        public void Execute()
        {
            if (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
                return;
            }
            try { _completion.TrySetResult(action()); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _completion.TrySetCanceled(cancellationToken);
            }
            catch (Exception exception) { _completion.TrySetException(exception); }
        }
    }
}

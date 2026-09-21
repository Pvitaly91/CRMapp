using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public static class BatchProcessor
{
    public static async Task<BatchResult<T>> RunAsync<T>(
        IEnumerable<T> items,
        Func<T, CancellationToken, Task> process,
        Func<T, Exception, Task>? onError = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<BatchItemResult<T>>();
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await process(item, cancellationToken);
                results.Add(new BatchItemResult<T>(item, true, null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                results.Add(new BatchItemResult<T>(item, false, exception));
                if (onError is not null)
                    await onError(item, exception);
            }
        }

        return new BatchResult<T>(results);
    }
}

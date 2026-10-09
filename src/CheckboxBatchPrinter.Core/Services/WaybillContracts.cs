using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public interface IWaybillService
{
    Task<WaybillFetchResult> FetchAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

using System.Globalization;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public sealed class ReceiptService(
    CheckboxApiClient apiClient,
    ISettingsService settingsService) : IReceiptService
{
    private const int PageSize = 100;
    private const int SafetyPageLimit = 1000;

    public async Task<IReadOnlyList<ReceiptRecord>> GetReceiptsAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        var settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        var range = DateRangeBuilder.ForLocalDates(from, to);
        var receipts = new List<ReceiptRecord>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var offset = 0;

        for (var page = 0; page < SafetyPageLimit; page++)
        {
            var url = BuildSearchUrl(settings.ApiBaseUrl, range.From, range.ToExclusive, PageSize, offset);
            var json = await apiClient.GetStringAsync(url, "receipts.search", cancellationToken).ConfigureAwait(false);
            var pageItems = ReceiptParser.ParsePage(json);
            var added = 0;
            foreach (var receipt in pageItems)
            {
                if (seen.Add(receipt.Id)) { receipts.Add(receipt); added++; }
            }

            if (pageItems.Count < PageSize || added == 0) break;
            offset += pageItems.Count;
        }

        return receipts;
    }

    public static string BuildSearchUrl(string baseUrl, DateTimeOffset from, DateTimeOffset toExclusive, int limit, int offset)
    {
        var fromValue = Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture));
        var toValue = Uri.EscapeDataString(toExclusive.ToString("O", CultureInfo.InvariantCulture));
        return $"{baseUrl.TrimEnd('/')}/api/v1/receipts/search?from_date={fromValue}&to_date={toValue}" +
               $"&desc=true&self_receipts=false&limit={limit}&offset={offset}";
    }
}

using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public static class LabelPrintHistory
{
    // Pages from separate Windows jobs still belong to one verified document. Copies of a page
    // must not fill a missing place, and pages from different PDF versions must not be combined.
    public static bool HasCompleteDocument(IEnumerable<LabelPrintAttempt> attempts,
        Func<LabelAttemptPage, bool> matches, int minimumPages = 1) =>
        attempts.Where(a => a.State == LabelSubmissionState.Submitted).SelectMany(a => a.Pages).Where(matches)
            .GroupBy(p => (p.Carrier, p.ConnectionId, p.DocumentId, p.Fingerprint)).Any(document =>
            {
                var expected = Math.Max(Math.Max(1, minimumPages), document.Max(p => p.ExpectedPages));
                var pages = document.Select(p => p.PageNumber).ToHashSet();
                return expected <= pages.Count && Enumerable.Range(1, expected).All(pages.Contains);
            });
}

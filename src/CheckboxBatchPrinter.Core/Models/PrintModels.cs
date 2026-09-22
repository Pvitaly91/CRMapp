namespace CheckboxBatchPrinter.Core.Models;

public enum PrintItemStatus
{
    Waiting,
    Downloading,
    Preparing,
    SubmittedToWindowsQueue,
    Paused,
    ResultNotConfirmed,
    Error,
    Cancelled
}

public enum WindowsPrintJobState
{
    Queued,
    Spooling,
    Printing,
    Paused,
    Error,
    CompletedBySpooler,
    Disappeared,
    Unknown
}

public enum PrintSubmissionState
{
    NotSubmitted,
    SubmissionUnknown,
    Submitted
}

public sealed record PrintAttemptDescriptor(
    Guid AttemptId,
    string PrinterName,
    string UniqueJobName,
    DateTimeOffset StartedAtUtc);

public sealed record PrintAttemptRecord(
    Guid AttemptId,
    string AccountContext,
    string ReceiptId,
    string PrinterName,
    string UniqueJobName,
    int? JobId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    PrintSubmissionState SubmissionState,
    PrintItemStatus ItemStatus);

public sealed record WindowsPrintJobObservation(
    int JobId,
    WindowsPrintJobState State,
    string Details,
    bool IsTerminalForMonitoring);

public sealed record PrinterPageValidation(
    double RequestedWidthDip,
    double RequestedHeightDip,
    double AcceptedWidthDip,
    double AcceptedHeightDip,
    double ImageableOriginXDip,
    double ImageableOriginYDip,
    double ImageableWidthDip,
    double ImageableHeightDip,
    int AdvertisedMediaSizeCount,
    bool DriverResolvedConflict);

public sealed record PrintSubmissionResult(
    PrintAttemptDescriptor Attempt,
    int JobId,
    PrinterPageValidation PageValidation,
    WindowsPrintJobObservation InitialObservation)
{
    public string UniqueJobName => Attempt.UniqueJobName;
    public string PrinterName => Attempt.PrinterName;
}

public sealed class PrintSubmissionUnknownException(PrintAttemptDescriptor attempt, Exception innerException, int? jobId = null)
    : Exception(
        $"Передавання завдання «{attempt.UniqueJobName}» у чергу Windows почалося, але результат невідомий.",
        innerException)
{
    public PrintAttemptDescriptor Attempt { get; } = attempt;
    public int? JobId { get; } = jobId;
}

public sealed record PrintBatchSnapshot<T>(IReadOnlyList<T> Items, int HiddenSelectedCount);

public sealed record BatchItemResult<T>(T Item, bool Success, Exception? Error);

public sealed record BatchResult<T>(IReadOnlyList<BatchItemResult<T>> Items)
{
    public int SuccessCount => Items.Count(x => x.Success);
    public int ErrorCount => Items.Count - SuccessCount;
}

public sealed record BatchSubmissionOutcome<T>(
    T Item,
    PrintSubmissionState SubmissionState,
    PrintSubmissionResult? Submission,
    Exception? Error,
    bool Cancelled);

public sealed record BatchSubmissionResult<T>(IReadOnlyList<BatchSubmissionOutcome<T>> Items)
{
    public int SubmittedCount => Items.Count(x => x.Submission is not null);
    public int UnknownCount => Items.Count(x => x.SubmissionState == PrintSubmissionState.SubmissionUnknown);
    public int ErrorCount => Items.Count(x => x.Error is not null);
    public int CancelledCount => Items.Count(x => x.Cancelled);
}

public readonly record struct PrintGeometry(double WidthDip, double HeightDip, double MarginDip)
{
    public const double DipPerMillimeter = 96d / 25.4d;

    public static PrintGeometry Calculate(
        int sourcePixelWidth,
        int sourcePixelHeight,
        double printableWidthMm,
        double paperWidthMm,
        double marginMm = 0.8)
    {
        if (sourcePixelWidth <= 0 || sourcePixelHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourcePixelWidth), "PNG dimensions must be positive.");
        if (paperWidthMm < 20 || printableWidthMm <= 0 || printableWidthMm > paperWidthMm)
            throw new ArgumentOutOfRangeException(nameof(printableWidthMm), "Printable width must fit the paper.");

        var margin = Math.Max(0, marginMm) * DipPerMillimeter;
        var width = printableWidthMm * DipPerMillimeter;
        var height = width * sourcePixelHeight / sourcePixelWidth;
        return new PrintGeometry(width, height, margin);
    }
}

public readonly record struct RequestedPageLayout(
    double PageWidthDip,
    double PageHeightDip,
    double ImageWidthDip,
    double ImageHeightDip,
    double MarginDip);

public readonly record struct DriverPageMetrics(
    double AcceptedWidthDip,
    double AcceptedHeightDip,
    double ImageableOriginXDip,
    double ImageableOriginYDip,
    double ImageableWidthDip,
    double ImageableHeightDip,
    int AdvertisedMediaSizeCount,
    bool DriverResolvedConflict);

public readonly record struct ValidatedPageLayout(
    double PageWidthDip,
    double PageHeightDip,
    double ImageLeftDip,
    double ImageTopDip,
    double ImageWidthDip,
    double ImageHeightDip,
    PrinterPageValidation Validation);

public sealed class UnsupportedPrinterPageException(string message) : Exception(message);

public static class PrinterPageValidator
{
    private static readonly double SizeToleranceDip = 0.5 * PrintGeometry.DipPerMillimeter;

    public static ValidatedPageLayout Validate(RequestedPageLayout request, DriverPageMetrics driver)
    {
        RequirePositive(request.PageWidthDip, nameof(request.PageWidthDip));
        RequirePositive(request.PageHeightDip, nameof(request.PageHeightDip));
        RequirePositive(driver.AcceptedWidthDip, nameof(driver.AcceptedWidthDip));
        RequirePositive(driver.AcceptedHeightDip, nameof(driver.AcceptedHeightDip));
        RequirePositive(driver.ImageableWidthDip, nameof(driver.ImageableWidthDip));
        RequirePositive(driver.ImageableHeightDip, nameof(driver.ImageableHeightDip));

        if (Math.Abs(request.PageWidthDip - driver.AcceptedWidthDip) > SizeToleranceDip ||
            Math.Abs(request.PageHeightDip - driver.AcceptedHeightDip) > SizeToleranceDip)
        {
            throw new UnsupportedPrinterPageException(
                $"Драйвер підмінив розмір сторінки: запитано {ToMm(request.PageWidthDip):0.##}×{ToMm(request.PageHeightDip):0.##} мм, " +
                $"прийнято {ToMm(driver.AcceptedWidthDip):0.##}×{ToMm(driver.AcceptedHeightDip):0.##} мм. Друк зупинено, щоб не обрізати чек.");
        }

        if (driver.ImageableOriginXDip < -SizeToleranceDip || driver.ImageableOriginYDip < -SizeToleranceDip ||
            driver.ImageableOriginXDip + driver.ImageableWidthDip > driver.AcceptedWidthDip + SizeToleranceDip ||
            driver.ImageableOriginYDip + driver.ImageableHeightDip > driver.AcceptedHeightDip + SizeToleranceDip)
        {
            throw new UnsupportedPrinterPageException("Драйвер повідомив некоректну доступну область друку.");
        }

        if (request.ImageWidthDip > driver.ImageableWidthDip + SizeToleranceDip)
            throw new UnsupportedPrinterPageException(
                $"Область друку принтера має ширину лише {ToMm(driver.ImageableWidthDip):0.##} мм, " +
                $"а налаштовано {ToMm(request.ImageWidthDip):0.##} мм.");

        if (request.ImageHeightDip + request.MarginDip * 2 > driver.ImageableHeightDip + SizeToleranceDip)
            throw new UnsupportedPrinterPageException(
                $"Драйвер не підтримує потрібну висоту чека {ToMm(request.PageHeightDip):0.##} мм без обрізання.");

        var left = driver.ImageableOriginXDip + Math.Max(0, (driver.ImageableWidthDip - request.ImageWidthDip) / 2);
        var top = driver.ImageableOriginYDip + request.MarginDip;
        var validation = new PrinterPageValidation(
            request.PageWidthDip, request.PageHeightDip,
            driver.AcceptedWidthDip, driver.AcceptedHeightDip,
            driver.ImageableOriginXDip, driver.ImageableOriginYDip,
            driver.ImageableWidthDip, driver.ImageableHeightDip,
            driver.AdvertisedMediaSizeCount, driver.DriverResolvedConflict);
        return new ValidatedPageLayout(
            driver.AcceptedWidthDip, driver.AcceptedHeightDip, left, top,
            request.ImageWidthDip, request.ImageHeightDip, validation);
    }

    private static void RequirePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
            throw new UnsupportedPrinterPageException($"Принтер не повідомив коректне значення {name}.");
    }

    private static double ToMm(double dip) => dip / PrintGeometry.DipPerMillimeter;
}

public static class PrintSelectionPlanner
{
    public static PrintBatchSnapshot<T> CreateSnapshot<T>(
        IEnumerable<T> allItems,
        IEnumerable<T> visibleItemsInCurrentOrder,
        Func<T, bool> isSelected)
    {
        var visibleSelected = visibleItemsInCurrentOrder.Where(isSelected).ToArray();
        var totalSelected = allItems.Count(isSelected);
        return new PrintBatchSnapshot<T>(visibleSelected, Math.Max(0, totalSelected - visibleSelected.Length));
    }
}

public static class PrintRetryPolicy
{
    public static bool CanRetryWithoutWarning(PrintItemStatus status, bool hasSubmissionRisk) =>
        status == PrintItemStatus.Error && !hasSubmissionRisk;

    public static bool RequiresExplicitConfirmation(PrintItemStatus status, bool hasSubmissionRisk) =>
        hasSubmissionRisk || status is PrintItemStatus.SubmittedToWindowsQueue or PrintItemStatus.Paused or PrintItemStatus.ResultNotConfirmed;
}

public static class PrintSubmissionBoundary
{
    public static T Execute<T>(
        PrintAttemptDescriptor attempt,
        Action<PrintAttemptDescriptor> markSubmissionStarted,
        Func<T> submit,
        Func<T?> tryRecoverSubmittedJob)
        where T : class
    {
        // This callback must persist SubmissionUnknown before the spooler call.
        // If it fails, submit is never invoked and the attempt remains NotSubmitted.
        markSubmissionStarted(attempt);
        try
        {
            return submit();
        }
        catch (Exception exception)
        {
            try
            {
                var recovered = tryRecoverSubmittedJob();
                if (recovered is not null) return recovered;
            }
            catch (Exception)
            {
                // Recovery is best-effort. Absence or lookup failure is never proof
                // that the spooler did not accept the job.
            }
            if (exception is PrintSubmissionUnknownException) throw;
            throw new PrintSubmissionUnknownException(attempt, exception);
        }
    }
}

public static class ReliableBatchRunner
{
    public static async Task<BatchSubmissionResult<T>> RunAsync<T>(
        IReadOnlyList<T> items,
        Func<T, CancellationToken, Task<PrintSubmissionResult>> submit,
        CancellationToken cancellationToken = default)
    {
        var outcomes = new List<BatchSubmissionOutcome<T>>(items.Count);
        for (var index = 0; index < items.Count; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                for (; index < items.Count; index++)
                    outcomes.Add(new BatchSubmissionOutcome<T>(items[index], PrintSubmissionState.NotSubmitted, null, null, true));
                break;
            }

            var item = items[index];
            try
            {
                var submission = await submit(item, cancellationToken);
                outcomes.Add(new BatchSubmissionOutcome<T>(item, PrintSubmissionState.Submitted, submission, null, false));
            }
            catch (PrintSubmissionUnknownException exception)
            {
                outcomes.Add(new BatchSubmissionOutcome<T>(item, PrintSubmissionState.SubmissionUnknown, null, exception, false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(new BatchSubmissionOutcome<T>(item, PrintSubmissionState.NotSubmitted, null, null, true));
                for (index++; index < items.Count; index++)
                    outcomes.Add(new BatchSubmissionOutcome<T>(items[index], PrintSubmissionState.NotSubmitted, null, null, true));
                break;
            }
            catch (Exception exception)
            {
                outcomes.Add(new BatchSubmissionOutcome<T>(item, PrintSubmissionState.NotSubmitted, null, exception, false));
            }
        }
        return new BatchSubmissionResult<T>(outcomes);
    }
}

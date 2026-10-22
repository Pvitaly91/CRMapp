using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed partial class MainViewModel
{
    private sealed record ReceiptSubmissionResult(int Submitted, int Total, Exception? Failure,
        IReadOnlyList<string> Warnings)
    {
        public bool AllSubmitted => Submitted == Total;
        public string Summary => $"Чеки передано: {Submitted} із {Total}.";
    }

    // The caller preflights the whole immutable packet before starting either printer.
    // Separate sequential jobs keep spooler recovery from replaying the whole packet.
    // A successful write means accepted by Windows, not confirmed physical output.
    private async Task<ReceiptSubmissionResult> SubmitReceiptSequenceAsync(
        IReadOnlyList<PrintReceiptDocument> documents, IReadOnlyList<ReceiptRowViewModel> rows,
        AppSettings settings, string account, CancellationToken token = default,
        Action<ReceiptRowViewModel>? accepted = null)
    {
        if (documents.Count != rows.Count || documents.Where((document, index) =>
            !document.ReceiptId.Equals(rows[index].Id, StringComparison.OrdinalIgnoreCase)).Any())
            throw new InvalidOperationException("Підготовлені чеки не відповідають підтвердженому порядку друку.");
        foreach (var row in rows) { row.PrintStatus = PrintItemStatus.Waiting; row.PrintError = ""; }
        var warnings = new List<string>();
        var submitted = 0;
        var position = 0;
        var sequenceId = Guid.NewGuid().ToString("N");
        void Log(string stage, ReceiptRowViewModel row, Exception? error = null)
        {
            try
            {
                var status = $"sequence={sequenceId};position={position};total={rows.Count}";
                if (error is null) _logger.Info("receipt.job." + stage, row.Id, printStatus: status);
                else _logger.Error("receipt.job." + stage, error, row.Id, printStatus: status);
            }
            catch (Exception) { /* Logging cannot replay an accepted receipt. */ }
        }
        for (var index = 0; index < documents.Count; index++)
        {
            var row = rows[index];
            position = index + 1;
            if (token.IsCancellationRequested)
            {
                var canceled = new OperationCanceledException(token);
                warnings.Add("Передавання решти чеків скасовано. Уже передані чеки залишаються в черзі Windows.");
                return new(submitted, rows.Count, canceled, warnings);
            }
            row.PrintStatus = PrintItemStatus.Printing;
            _combinedReceiptTransferRisks.Add((account, row.Id.ToUpperInvariant()));
            ProgressText = $"Передаю чек {index + 1} із {documents.Count}…";
            Log("submit", row);
            try
            {
                await _printService.PrintReceiptAsync(documents[index], settings, token);
            }
            catch (Exception exception)
            {
                row.PrintStatus = PrintItemStatus.Error;
                row.PrintError = exception.Message + " Чек міг потрапити до черги Windows; перевірте перед повтором.";
                Log("failed", row, exception);
                warnings.Add($"Чек №{row.Serial}: {row.PrintError} Наступні чеки не передано.");
                return new(submitted, rows.Count, exception, warnings);
            }
            row.PrintStatus = PrintItemStatus.Done;
            Log("accepted", row);
            submitted++;
            // Save each accepted prefix immediately. Cancellation or failure of a later
            // receipt must never erase it or cause an automatic repeat.
            try
            {
                await _printHistoryStore.MarkPrintedAsync(account, [row.Id], settings.PrinterName);
                _combinedReceiptTransferRisks.Remove((account, row.Id.ToUpperInvariant()));
            }
            catch (Exception exception)
            {
                const string warning = "Чек передано, але не вдалося зберегти історію друку.";
                row.PrintError = warning;
                if (!warnings.Contains(warning)) warnings.Add(warning);
                Log("history", row, exception);
            }
            try { accepted?.Invoke(row); }
            catch (Exception exception)
            {
                const string warning = "Чек передано, але не вдалося оновити його вибір у таблиці.";
                if (!warnings.Contains(warning)) warnings.Add(warning);
                Log("selection", row, exception);
            }
        }
        return new(submitted, rows.Count, null, warnings);
    }
}

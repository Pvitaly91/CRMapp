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
    // Separate sequential jobs limit a spooler recovery to one receipt.
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
        var sequenceId = Guid.NewGuid().ToString("N");
        for (var index = 0; index < documents.Count; index += ReceiptPrintPolicy.MaxReceiptsPerJob)
        {
            var groupDocuments = documents.Skip(index).Take(ReceiptPrintPolicy.MaxReceiptsPerJob).ToArray();
            var groupRows = rows.Skip(index).Take(groupDocuments.Length).ToArray();
            var first = index + 1;
            var last = index + groupRows.Length;
            void Log(string stage, ReceiptRowViewModel row, Exception? error = null)
            {
                try
                {
                    var position = index + Array.IndexOf(groupRows, row) + 1;
                    var status = $"sequence={sequenceId};position={position};total={rows.Count};group={index / ReceiptPrintPolicy.MaxReceiptsPerJob + 1};first={first};last={last};count={groupRows.Length}";
                    if (error is null) _logger.Info("receipt.job." + stage, row.Id, printStatus: status);
                    else _logger.Error("receipt.job." + stage, error, row.Id, printStatus: status);
                }
                catch (Exception) { /* Logging cannot replay an accepted group. */ }
            }
            if (token.IsCancellationRequested)
            {
                var canceled = new OperationCanceledException(token);
                warnings.Add("Передавання решти чеків скасовано. Уже передані чеки залишаються в черзі Windows.");
                return new(submitted, rows.Count, canceled, warnings);
            }
            foreach (var row in groupRows)
            {
                row.PrintStatus = PrintItemStatus.Printing;
                _combinedReceiptTransferRisks.Add((account, row.Id.ToUpperInvariant()));
                Log("submit", row);
            }
            ProgressText = groupRows.Length == 1 ? $"Передаю чек {first} із {documents.Count}…"
                : $"Передаю чеки {first}–{last} із {documents.Count}…";
            try
            {
                if (groupDocuments.Length == 1)
                    await _printService.PrintReceiptAsync(groupDocuments[0], settings, token);
                else
                    await _printService.PrintReceiptsAsSingleJobAsync(groupDocuments, settings, token);
            }
            catch (Exception exception)
            {
                foreach (var row in groupRows)
                {
                    row.PrintStatus = PrintItemStatus.Error;
                    row.PrintError = exception.Message + " Завдання могло потрапити до черги Windows; перевірте цей чек перед повтором.";
                    Log("failed", row, exception);
                }
                warnings.Add($"Чек {first}: {groupRows[0].PrintError} Наступні чеки не передано.");
                return new(submitted, rows.Count, exception, warnings);
            }
            foreach (var row in groupRows)
            {
                row.PrintStatus = PrintItemStatus.Done;
                Log("accepted", row);
            }
            submitted += groupRows.Length;
            // Save the whole accepted group before its successor. A later failure must
            // never erase the accepted prefix or cause an automatic repeat.
            try
            {
                await _printHistoryStore.MarkPrintedAsync(account, groupRows.Select(row => row.Id).ToArray(), settings.PrinterName);
                foreach (var row in groupRows)
                    _combinedReceiptTransferRisks.Remove((account, row.Id.ToUpperInvariant()));
            }
            catch (Exception exception)
            {
                const string warning = "Чеки передано, але не вдалося зберегти історію друку.";
                foreach (var row in groupRows)
                {
                    row.PrintError = warning;
                    Log("history", row, exception);
                }
                if (!warnings.Contains(warning)) warnings.Add(warning);
            }
            foreach (var row in groupRows)
            {
                try { accepted?.Invoke(row); }
                catch (Exception exception)
                {
                    const string warning = "Чек передано, але не вдалося оновити його вибір у таблиці.";
                    if (!warnings.Contains(warning)) warnings.Add(warning);
                    Log("selection", row, exception);
                }
            }
        }
        return new(submitted, rows.Count, null, warnings);
    }
}

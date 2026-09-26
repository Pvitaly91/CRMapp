namespace CheckboxBatchPrinter.Core.Models;

public sealed class ReceiptRecord
{
    public required string Id { get; init; }
    public string Type { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public long Serial { get; init; }
    public string FiscalCode { get; init; } = string.Empty;
    public DateTimeOffset? FiscalDate { get; init; }
    public DateTimeOffset? CreatedAt { get; init; }
    public long TotalSumMinor { get; init; }
    public bool TotalKnown { get; init; } = true;
    public string AmountComparisonIssue { get; init; } = "";
    public IReadOnlyList<ReceiptPayment> Payments { get; init; } = [];
    public string CashRegisterFiscalNumber { get; init; } = string.Empty;
    public string OrganizationId { get; init; } = string.Empty;
    public string BranchName { get; init; } = string.Empty;

    public DateTimeOffset? DisplayDate => FiscalDate ?? CreatedAt;
    public decimal TotalSum => TotalSumMinor / 100m;
    public string PaymentDisplay => Payments.Count == 0
        ? "—"
        : string.Join(", ", Payments.Select(p => string.IsNullOrWhiteSpace(p.Label) ? p.Type : p.Label).Distinct());
}

public sealed record ReceiptPayment(string Type, string Label, long ValueMinor);

public static class ReceiptTypes
{
    public const string Sell = "SELL";
    public const string Return = "RETURN";

    // Values are copied from Checkbox OpenAPI 2.107.0. Unknown future values remain displayable.
    public static readonly IReadOnlyList<string> Known =
    [
        "SELL", "RETURN", "SERVICE_IN", "SERVICE_OUT", "SERVICE_CURRENCY",
        "CURRENCY_EXCHANGE", "PAWNSHOP", "CASH_WITHDRAWAL"
    ];

    public static string ToUkrainian(string value) => value switch
    {
        "SELL" => "Продаж",
        "RETURN" => "Повернення",
        "SERVICE_IN" => "Службове внесення",
        "SERVICE_OUT" => "Службова видача",
        "SERVICE_CURRENCY" => "Службова валютна операція",
        "CURRENCY_EXCHANGE" => "Обмін валют",
        "PAWNSHOP" => "Ломбард",
        "CASH_WITHDRAWAL" => "Видача готівки",
        _ when string.IsNullOrWhiteSpace(value) => "—",
        _ => value
    };
}

public static class ReceiptStatuses
{
    public static string ToUkrainian(string value) => value switch
    {
        "CREATED" => "Створено",
        "DONE" => "Готово",
        "ERROR" => "Помилка",
        "CANCELLATION" => "Скасовується",
        "CANCELLED" => "Скасовано",
        _ when string.IsNullOrWhiteSpace(value) => "—",
        _ => value
    };
}

using System.Globalization;
using System.Text.Json;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

public static class ReceiptParser
{
    public static IReadOnlyList<ReceiptRecord> ParsePage(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
            throw new JsonException("Checkbox response does not contain a results array.");

        var records = new List<ReceiptRecord>();
        foreach (var item in results.EnumerateArray())
        {
            var id = GetString(item, "id");
            if (string.IsNullOrWhiteSpace(id))
                continue;

            records.Add(new ReceiptRecord
            {
                Id = id,
                Type = GetString(item, "type"),
                Status = GetString(item, "status"),
                Serial = GetLong(item, "serial"),
                FiscalCode = GetString(item, "fiscal_code"),
                FiscalDate = GetDate(item, "fiscal_date"),
                CreatedAt = GetDate(item, "created_at"),
                TotalSumMinor = GetLong(item, "total_sum"),
                Payments = ParsePayments(item),
                CashRegisterFiscalNumber = GetCashRegister(item),
                BranchName = GetNestedString(item, "branch", "name")
            });
        }

        return records;
    }

    private static IReadOnlyList<ReceiptPayment> ParsePayments(JsonElement item)
    {
        if (!item.TryGetProperty("payments", out var payments) || payments.ValueKind != JsonValueKind.Array)
            return [];

        return payments.EnumerateArray()
            .Select(x => new ReceiptPayment(GetString(x, "type"), GetString(x, "label"), GetLong(x, "value")))
            .ToArray();
    }

    private static string GetCashRegister(JsonElement item)
    {
        var shortValue = GetNestedString(item, "cash_register", "fiscal_number");
        if (!string.IsNullOrWhiteSpace(shortValue))
            return shortValue;

        if (item.TryGetProperty("shift", out var shift) && shift.ValueKind == JsonValueKind.Object)
        {
            var fullValue = GetNestedString(shift, "cash_register", "fiscal_number");
            if (!string.IsNullOrWhiteSpace(fullValue))
                return fullValue;
        }

        return string.Empty;
    }

    private static string GetNestedString(JsonElement parent, string objectName, string propertyName) =>
        parent.TryGetProperty(objectName, out var nested) && nested.ValueKind == JsonValueKind.Object
            ? GetString(nested, propertyName)
            : string.Empty;

    private static string GetString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return string.Empty;
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
    }

    private static long GetLong(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
            return number;
        return long.TryParse(value.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : 0;
    }

    private static DateTimeOffset? GetDate(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
}

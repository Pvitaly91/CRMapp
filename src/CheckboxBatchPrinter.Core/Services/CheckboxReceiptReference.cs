using System.Text.RegularExpressions;
using CheckboxBatchPrinter.Core.Models;

namespace CheckboxBatchPrinter.Core.Services;

/// <summary>Pure parser. Never follows links and never forwards Authorization.</summary>
public static class CheckboxReceiptReference
{
    public sealed record ParsedUrl(string ReceiptId, string FiscalCode, string OrganizationId);

    public static bool TryParseUrl(string value, out ParsedUrl result)
    {
        result = new("", "", "");
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl) || value.Contains('\\') || value.Contains('%') ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !string.Equals(uri.Host, "api.checkbox.ua", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0) return false;
        // Only the officially documented receipt HTML route. No redirect/short-link guessing.
        var match = Regex.Match(uri.AbsolutePath, @"^/api/v1/receipts/([A-Za-z0-9_-]+)/html$");
        if (!match.Success || value.Contains("/../", StringComparison.Ordinal) || value.Contains("/./", StringComparison.Ordinal)) return false;
        var organization = "";
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (uri.Query.Length > 0)
            foreach (var parameter in uri.Query[1..].Split('&'))
            {
                var parts = parameter.Split('=', 2);
                if (parts.Length != 2 || !seen.Add(parts[0])) return false;
                if (parts[0] == "organization_id")
                {
                    if (!Guid.TryParseExact(parts[1], "D", out var id) || id == Guid.Empty) return false;
                    organization = id.ToString("D");
                }
                else if (parts[0] is "simple" or "show_buttons" or "is_second_copy")
                {
                    if (parts[1] is not ("true" or "false" or "1" or "0")) return false;
                }
                else return false;
            }
        var key = match.Groups[1].Value;
        if (Guid.TryParseExact(key, "D", out var uuid) && uuid != Guid.Empty)
            result = new(uuid.ToString("D"), "", organization);
        else if (Regex.IsMatch(key, @"^[A-Za-z0-9_-]{11}$"))
            result = new("", key, organization);
        else return false;
        return true;
    }

    public static IReadOnlyList<FiscalDocumentReference> FromRozetka(OrderKey key, string fiscalCode, string provider, string? url)
    {
        const string document = "rozetka.prro";
        var references = new List<FiscalDocumentReference>();
        var verifiedUrl = TryParseUrl(url ?? "", out var parsed);
        var normalizedProvider = verifiedUrl || string.Equals(provider, "checkbox", StringComparison.OrdinalIgnoreCase) ? "Checkbox" : provider;
        if (fiscalCode.Length > 0)
            references.Add(new(FiscalDocumentKeyKind.FiscalCode, fiscalCode, "prro.prro_receipt_fiscal_code", key,
                normalizedProvider, document, OrganizationId: verifiedUrl ? parsed.OrganizationId : ""));
        if (verifiedUrl)
            references.Add(new(FiscalDocumentKeyKind.CheckboxReceiptUrl, url!, "GET /prro/receipt/{order_id}?type=link: content.url",
                key, "Checkbox", document, OrganizationId: parsed.OrganizationId));
        return references;
    }

    public static bool SameId(string left, string right) => Guid.TryParse(left, out var a) && Guid.TryParse(right, out var b) && a == b;
    public static bool ContextMatches(FiscalDocumentReference reference, ReceiptRecord receipt) =>
        (reference.OrganizationId.Length == 0 || SameId(reference.OrganizationId, receipt.OrganizationId)) &&
        (reference.CashRegisterFiscalNumber.Length == 0 || reference.CashRegisterFiscalNumber == receipt.CashRegisterFiscalNumber);
}

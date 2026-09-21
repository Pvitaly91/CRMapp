namespace CheckboxBatchPrinter.Core.Models;

public sealed class AppSettings
{
    public const string DefaultApiBaseUrl = "https://api.checkbox.ua";

    public string ApiBaseUrl { get; set; } = DefaultApiBaseUrl;
    public string Login { get; set; } = string.Empty;
    public string PrinterName { get; set; } = string.Empty;
    public PaperWidth PaperWidth { get; set; } = PaperWidth.Mm58;
    public double CustomPaperWidthMm { get; set; } = 58;
    public double PrintableWidthMm { get; set; } = 54;
    public bool SeparatePrintJobPerReceipt { get; set; } = true;
    public int CacheRetentionDays { get; set; } = 7;
    public int HttpTimeoutSeconds { get; set; } = 30;

    public double EffectivePaperWidthMm => PaperWidth switch
    {
        PaperWidth.Mm50 => 50,
        PaperWidth.Mm58 => 58,
        PaperWidth.Mm80 => 80,
        _ => Math.Clamp(CustomPaperWidthMm, 40, 80)
    };

    public void Normalize()
    {
        ApiBaseUrl = string.IsNullOrWhiteSpace(ApiBaseUrl)
            ? DefaultApiBaseUrl
            : ApiBaseUrl.Trim().TrimEnd('/');
        CustomPaperWidthMm = Math.Clamp(CustomPaperWidthMm, 40, 80);
        PrintableWidthMm = Math.Clamp(PrintableWidthMm, 20, EffectivePaperWidthMm);
        CacheRetentionDays = Math.Clamp(CacheRetentionDays, 1, 30);
        HttpTimeoutSeconds = Math.Clamp(HttpTimeoutSeconds, 10, 120);
    }
}

public enum PaperWidth
{
    Mm50,
    Mm58,
    Mm80,
    Custom
}

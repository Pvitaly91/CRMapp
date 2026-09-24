using System.Collections.ObjectModel;
using System.Diagnostics;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;
using CheckboxBatchPrinter.Services;

namespace CheckboxBatchPrinter.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settingsService;
    private readonly IAuthenticationService _authentication;
    private readonly IReceiptImageService _imageService;
    private readonly IPrintService _printService;
    private readonly IAppLogger _logger;
    private AppSettings _settings = new();
    private string _login = string.Empty;
    private string _password = string.Empty;
    private string _selectedPrinter = string.Empty;
    private PaperWidth _paperWidth;
    private double _customPaperWidthMm;
    private double _printableWidthMm;
    private bool _separatePrintJob;
    private string _diagnosticStatus = "Готово";
    private bool _isBusy;
    private string _originalLogin = string.Empty;
    private Task? _marketplaceLoad;

    public SettingsViewModel(ISettingsService settingsService, IAuthenticationService authentication,
        IReceiptImageService imageService, IPrintService printService, IAppLogger logger,
        MarketplaceSettingsViewModel? marketplace = null)
    {
        _settingsService = settingsService;
        _authentication = authentication;
        _imageService = imageService;
        _printService = printService;
        _logger = logger;
        Marketplace = marketplace;
        PaperWidths = new ObservableCollection<PaperWidthOption>
        {
            new(PaperWidth.Mm50, "50 мм"), new(PaperWidth.Mm58, "58 мм"),
            new(PaperWidth.Mm80, "80 мм"), new(PaperWidth.Custom, "Custom")
        };
    }

    public ObservableCollection<string> Printers { get; } = [];
    public MarketplaceSettingsViewModel? Marketplace { get; }
    public ObservableCollection<PaperWidthOption> PaperWidths { get; }
    public string Login { get => _login; set => SetProperty(ref _login, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string SelectedPrinter { get => _selectedPrinter; set => SetProperty(ref _selectedPrinter, value); }
    public PaperWidth PaperWidth { get => _paperWidth; set { if (SetProperty(ref _paperWidth, value)) OnPropertyChanged(nameof(IsCustomPaperWidth)); } }
    public bool IsCustomPaperWidth => PaperWidth == PaperWidth.Custom;
    public double CustomPaperWidthMm { get => _customPaperWidthMm; set => SetProperty(ref _customPaperWidthMm, value); }
    public double PrintableWidthMm { get => _printableWidthMm; set => SetProperty(ref _printableWidthMm, value); }
    public bool SeparatePrintJobPerReceipt { get => _separatePrintJob; set => SetProperty(ref _separatePrintJob, value); }
    public string DiagnosticStatus { get => _diagnosticStatus; private set => SetProperty(ref _diagnosticStatus, value); }
    public bool IsBusy { get => _isBusy; private set => SetProperty(ref _isBusy, value); }
    public string AppVersion => AppEnvironment.DisplayVersion;
    public string DataDirectory => AppEnvironment.DataRoot;
    public string ApiVersion => "Checkbox API 2.107.0+7e00a4c6 (перевірено 21.09.2026)";
    public string LogDirectory => _logger.LogDirectory;

    public async Task LoadAsync()
    {
        _settings = await _settingsService.LoadAsync();
        Login = _settings.Login;
        _originalLogin = _settings.Login;
        SelectedPrinter = _settings.PrinterName;
        PaperWidth = _settings.PaperWidth;
        CustomPaperWidthMm = _settings.CustomPaperWidthMm;
        PrintableWidthMm = _settings.PrintableWidthMm;
        SeparatePrintJobPerReceipt = _settings.SeparatePrintJobPerReceipt;
        Printers.Clear();
        foreach (var printer in _printService.GetInstalledPrinters()) Printers.Add(printer);
        if (string.IsNullOrWhiteSpace(SelectedPrinter) && Printers.Count > 0) SelectedPrinter = Printers[0];
    }

    // Checkbox/printer settings must not wait for or read optional integration files.
    public Task EnsureMarketplaceLoadedAsync() => Marketplace is null
        ? Task.CompletedTask : _marketplaceLoad ??= Marketplace.LoadAsync();

    public async Task SaveAsync()
    {
        if (Marketplace?.IsBusy == true) throw new InvalidOperationException("Дочекайтеся завершення перевірки маркетплейсу.");
        ValidatePrintSettings();
        IsBusy = true;
        try
        {
            ApplyToSettings();
            if (!string.IsNullOrWhiteSpace(Password))
                await _authentication.SignInAndStoreAsync(Login, Password);
            else
            {
                if (!string.Equals(Login.Trim(), _originalLogin, StringComparison.Ordinal) && _authentication.HasStoredCredentials)
                    throw new InvalidOperationException("Для зміни логіна введіть пароль Checkbox.");
            }
            await _settingsService.SaveAsync(_settings);
            if (_marketplaceLoad is not null && Marketplace is not null)
            {
                await _marketplaceLoad;
                await Marketplace.SaveAsync();
            }
            _originalLogin = _settings.Login;
            Password = string.Empty;
            DiagnosticStatus = "Налаштування збережено";
        }
        finally { IsBusy = false; }
    }

    public async Task TestApiAsync()
    {
        IsBusy = true;
        DiagnosticStatus = "Перевірка Checkbox API…";
        try
        {
            ApplyToSettings();
            if (!string.IsNullOrWhiteSpace(Password))
                await _authentication.SignInAndStoreAsync(Login, Password);
            else
            {
                if (!string.Equals(Login.Trim(), _originalLogin, StringComparison.Ordinal))
                    throw new InvalidOperationException("Для перевірки нового логіна введіть пароль Checkbox.");
                await _authentication.GetAccessTokenAsync(forceRefresh: true);
            }
            await _settingsService.SaveAsync(_settings);
            _originalLogin = _settings.Login;
            DiagnosticStatus = "Підключення до Checkbox успішне";
        }
        finally { IsBusy = false; }
    }

    public async Task TestPrintAsync()
    {
        ValidatePrintSettings();
        IsBusy = true;
        DiagnosticStatus = "Тестовий друк…";
        try
        {
            ApplyToSettings();
            await _printService.PrintTestAsync(_settings);
            DiagnosticStatus = "Тестове завдання надіслано на принтер";
        }
        finally { IsBusy = false; }
    }

    public async Task<int> ClearCacheAsync()
    {
        IsBusy = true;
        try
        {
            var count = await _imageService.ClearCacheAsync();
            DiagnosticStatus = $"Кеш очищено. Видалено файлів: {count}";
            return count;
        }
        finally { IsBusy = false; }
    }

    public void OpenLogs() => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_logger.LogDirectory}\"") { UseShellExecute = true });

    private void ApplyToSettings()
    {
        _settings.Login = Login.Trim();
        _settings.PrinterName = SelectedPrinter ?? string.Empty;
        _settings.PaperWidth = PaperWidth;
        _settings.CustomPaperWidthMm = CustomPaperWidthMm;
        _settings.PrintableWidthMm = PrintableWidthMm;
        _settings.SeparatePrintJobPerReceipt = SeparatePrintJobPerReceipt;
        _settings.Normalize();
    }

    private void ValidatePrintSettings()
    {
        var paper = PaperWidth switch
        {
            PaperWidth.Mm50 => 50,
            PaperWidth.Mm58 => 58,
            PaperWidth.Mm80 => 80,
            _ => CustomPaperWidthMm
        };
        if (paper is < 40 or > 80) throw new InvalidOperationException("Ширина паперу має бути від 40 до 80 мм.");
        if (PrintableWidthMm <= 0 || PrintableWidthMm > paper)
            throw new InvalidOperationException("Ширина області друку має бути більшою за 0 і не перевищувати ширину паперу.");
        if (!string.IsNullOrWhiteSpace(SelectedPrinter) && !_printService.PrinterExists(SelectedPrinter))
            throw new InvalidOperationException("Обраний принтер зараз недоступний.");
    }
}

public sealed record PaperWidthOption(PaperWidth Value, string Display)
{
    public override string ToString() => Display;
}

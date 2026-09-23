using System.Collections.ObjectModel;
using System.Windows.Input;
using CheckboxBatchPrinter.Core.Models;
using CheckboxBatchPrinter.Core.Services;
using CheckboxBatchPrinter.Infrastructure;

namespace CheckboxBatchPrinter.ViewModels;

public sealed class MarketplaceSettingsViewModel : ObservableObject
{
    private readonly IMarketplaceSettingsStore _settings;
    private readonly IMarketplaceSecretStore _secrets;
    private readonly MarketplaceSyncService _sync;
    private MarketplaceConnection? _selected;
    private string _token = "", _login = "", _password = "", _status = "Локальні секрети захищено для поточного користувача Windows.";
    private bool _busy;
    private bool _canEdit = true;
    private int _historyDays = 30, _cacheDays = 30;
    private readonly Dictionary<string, MarketplaceCredentials> _pending = [];

    public MarketplaceSettingsViewModel(IMarketplaceSettingsStore settings, IMarketplaceSecretStore secrets, MarketplaceSyncService sync)
    {
        _settings = settings; _secrets = secrets; _sync = sync;
        AddPromCommand = new RelayCommand(_ => Add(MarketplaceKind.Prom), _ => !IsBusy && CanEdit);
        AddRozetkaCommand = new RelayCommand(_ => Add(MarketplaceKind.Rozetka), _ => !IsBusy && CanEdit);
        TestCommand = new AsyncRelayCommand(_ => TestAsync(), _ => Selected is not null && !IsBusy && CanEdit);
    }
    public ObservableCollection<MarketplaceConnection> Connections { get; } = [];
    public ICommand AddPromCommand { get; }
    public ICommand AddRozetkaCommand { get; }
    public ICommand TestCommand { get; }
    public event EventHandler? CredentialsCleared;
    public MarketplaceConnection? Selected
    {
        get => _selected;
        set
        {
            StageCredentials();
            if (!SetProperty(ref _selected, value)) return;
            Token = Login = Password = "";
            CredentialsCleared?.Invoke(this, EventArgs.Empty);
            OnPropertyChanged(nameof(IsProm)); OnPropertyChanged(nameof(IsRozetka));
            ((AsyncRelayCommand)TestCommand).RaiseCanExecuteChanged();
        }
    }
    public bool IsProm => Selected?.Marketplace == MarketplaceKind.Prom;
    public bool IsRozetka => Selected?.Marketplace == MarketplaceKind.Rozetka;
    public string Token { get => _token; set => SetProperty(ref _token, value); }
    public string Login { get => _login; set => SetProperty(ref _login, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            SetProperty(ref _busy, value);
            ((AsyncRelayCommand)TestCommand).RaiseCanExecuteChanged();
            ((RelayCommand)AddPromCommand).RaiseCanExecuteChanged();
            ((RelayCommand)AddRozetkaCommand).RaiseCanExecuteChanged();
        }
    }
    public bool CanEdit { get => _canEdit; private set => SetProperty(ref _canEdit, value); }
    public int HistoryDays { get => _historyDays; set => SetProperty(ref _historyDays, value); }
    public int CacheDays { get => _cacheDays; set => SetProperty(ref _cacheDays, value); }

    public async Task LoadAsync()
    {
        MarketplaceSettings settings;
        try { settings = await _settings.LoadAsync(); }
        catch (Exception)
        {
            CanEdit = false;
            Status = "Файл налаштувань маркетплейсів недоступний або пошкоджений. Його не буде перезаписано; налаштування Checkbox і принтера доступні.";
            ((RelayCommand)AddPromCommand).RaiseCanExecuteChanged();
            ((RelayCommand)AddRozetkaCommand).RaiseCanExecuteChanged();
            return;
        }
        Connections.Clear();
        foreach (var connection in settings.Connections) Connections.Add(connection);
        HistoryDays = settings.HistoryDays; CacheDays = settings.CacheDays;
        Selected = Connections.FirstOrDefault();
        MarketplaceSnapshot snapshot;
        try { snapshot = await _sync.LoadCachedAsync(CacheDays); }
        catch (Exception)
        {
            Status = "Не вдалося прочитати захищений кеш маркетплейсів. Налаштування Checkbox та принтера доступні. Збережені файли не видалено.";
            return;
        }
        Status = snapshot.States.Count == 0 ? "Підключення ще не оновлювалися." : string.Join("\n", snapshot.States.Select(s =>
            $"{Connections.FirstOrDefault(c => c.Id == s.ConnectionId)?.Name ?? "Підключення"}: {s.Message} Останнє успішне: {s.LastSuccessUtc?.ToLocalTime().ToString("dd.MM.yyyy HH:mm") ?? "немає"}"));
    }
    private void Add(MarketplaceKind kind)
    {
        var connection = new MarketplaceConnection { Marketplace = kind, Name = kind + " — магазин", Enabled = true };
        Connections.Add(connection); Selected = connection;
    }
    private void StageCredentials()
    {
        if (Selected is null) return;
        if (IsProm && !string.IsNullOrWhiteSpace(Token)) _pending[Selected.Id] = new(Token: Token.Trim());
        if (IsRozetka && (!string.IsNullOrEmpty(Password) || !string.IsNullOrEmpty(Login)))
        {
            var pending = _pending.GetValueOrDefault(Selected.Id) ?? new();
            _pending[Selected.Id] = new(Login: Login.Trim().Length > 0 ? Login.Trim() : pending.Login,
                Password: Password.Length > 0 ? Password : pending.Password);
        }
    }
    private async Task<MarketplaceCredentials> ResolveAsync(MarketplaceConnection connection)
    {
        var saved = await _secrets.LoadAsync(connection.Id) ?? new();
        if (!_pending.TryGetValue(connection.Id, out var draft)) return saved;
        if (connection.Marketplace == MarketplaceKind.Prom) return draft;
        var login = draft.Login.Length > 0 ? draft.Login : saved.Login;
        if (saved.Login.Length > 0 && !string.Equals(login, saved.Login, StringComparison.Ordinal))
            throw new InvalidOperationException("Для іншого логіна Rozetka додайте нове підключення, щоб зберегти ізоляцію замовлень і прив’язок.");
        if (draft.Login.Length > 0 && draft.Login != saved.Login && draft.Password.Length == 0)
            throw new InvalidOperationException("Для нового логіна Rozetka введіть пароль. Для іншого магазину додайте нове підключення.");
        return new(Login: login, Password: draft.Password.Length > 0 ? draft.Password : saved.Password);
    }
    public async Task SaveAsync()
    {
        if (!CanEdit) return;
        if (HistoryDays is < 0 or > 3650 || CacheDays is < 1 or > 90)
            throw new InvalidOperationException("Запас історії: 0–3650 днів. Строк кешу: 1–90 днів.");
        StageCredentials();
        foreach (var connection in Connections)
        {
            if (string.IsNullOrWhiteSpace(connection.Name)) throw new InvalidOperationException("Вкажіть назву магазину.");
            if (_pending.ContainsKey(connection.Id)) await _secrets.SaveAsync(connection.Id, await ResolveAsync(connection));
        }
        await _settings.SaveAsync(new() { Connections = Connections.ToList(), HistoryDays = HistoryDays, CacheDays = CacheDays });
        _pending.Clear(); Token = Login = Password = ""; CredentialsCleared?.Invoke(this, EventArgs.Empty);
    }
    private async Task TestAsync()
    {
        if (Selected is not { } connection) return;
        IsBusy = true; Status = "Перевірка читання замовлень…";
        try
        {
            StageCredentials();
            await _sync.TestConnectionAsync(connection, await ResolveAsync(connection));
            Status = $"{connection.Name}: підключення успішне. Натисніть «Зберегти».";
        }
        catch (Exception ex) { Status = ex is MarketplaceApiException or InvalidOperationException ? ex.Message : "Не вдалося перевірити підключення."; }
        finally { IsBusy = false; }
    }
}

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
    private string _token = "", _status = "Локальні секрети захищено для поточного користувача Windows.";
    private bool _busy;
    private bool _canEdit;
    private bool _saving;
    private bool _updatingCredentialFields;
    private int _historyDays, _cacheDays = 30;
    private readonly Dictionary<string, MarketplaceCredentials> _pending = [];
    private readonly Dictionary<string, bool?> _savedPresence = [];
    private readonly HashSet<string> _legacyCredentials = [];
    private readonly HashSet<string> _knownConnectionIds = [];
    private readonly Dictionary<string, long> _credentialReadRevisions = [];
    private readonly HashSet<string> _presenceUnavailable = [];
    private List<MarketplaceConnectionAlias> _connectionAliases = [];
    public Task CredentialStatusRefresh { get; private set; } = Task.CompletedTask;

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
    public event EventHandler? CredentialFieldsChanged;
    public MarketplaceConnection? Selected
    {
        get => _selected;
        set
        {
            if (_saving || ReferenceEquals(_selected, value)) return;
            StageCredentials();
            if (!SetProperty(ref _selected, value)) return;
            RestoreCredentialFields();
            OnPropertyChanged(nameof(IsProm)); OnPropertyChanged(nameof(IsRozetka));
            ((AsyncRelayCommand)TestCommand).RaiseCanExecuteChanged();
            CredentialStatusRefresh = RefreshCredentialStatusAsync(value);
            NotifyCredentialStatus();
        }
    }
    public bool IsProm => Selected?.Marketplace == MarketplaceKind.Prom;
    public bool IsRozetka => Selected?.Marketplace == MarketplaceKind.Rozetka;
    public string Token
    {
        get => _token;
        set
        {
            if (_saving && !_updatingCredentialFields || !SetProperty(ref _token, value)) return;
            if (!_updatingCredentialFields && string.IsNullOrWhiteSpace(value) && Selected is { } selected)
                _pending.Remove(selected.Id);
            NotifyCredentialStatus();
        }
    }
    private bool HasCredentialDraft => Selected is not null && (!string.IsNullOrWhiteSpace(Token) ||
        _pending.TryGetValue(Selected.Id, out var draft) && !string.IsNullOrWhiteSpace(draft.Token));
    public string CredentialStatusKind => HasCredentialDraft ? "Draft" : Selected is null ||
        !_savedPresence.TryGetValue(Selected.Id, out var saved) || saved is null ? "Unknown" : saved.Value ? "Saved" : "Missing";
    public string CredentialStatus => Selected is null ? "Оберіть підключення" : HasCredentialDraft
        ? CredentialIndicator.Text(false, true, "Ключ") : _presenceUnavailable.Contains(Selected.Id)
            ? "Не вдалося прочитати стан збережених даних" : CredentialStatusKind == "Unknown"
                ? "Перевіряю наявність збережених даних…" : _legacyCredentials.Contains(Selected.Id)
                    ? "Для Rozetka потрібен API-токен: у кабінеті відкрийте «Налаштування → Безпека API». Збережені логін і пароль більше не використовуються."
                    : CredentialIndicator.Text(CredentialStatusKind == "Saved", false, IsProm ? "Токен Prom" : "API-токен Rozetka");
    private void NotifyCredentialStatus() { OnPropertyChanged(nameof(CredentialStatus)); OnPropertyChanged(nameof(CredentialStatusKind)); }
    private void SetCredentialFields(string token)
    {
        _updatingCredentialFields = true;
        try { Token = token; }
        finally { _updatingCredentialFields = false; }
        CredentialFieldsChanged?.Invoke(this, EventArgs.Empty);
        NotifyCredentialStatus();
    }
    private void RestoreCredentialFields()
    {
        SetCredentialFields(Selected is { } connection ? _pending.GetValueOrDefault(connection.Id)?.Token ?? "" : "");
    }
    private void RememberSavedCredentials(MarketplaceConnection connection, MarketplaceCredentials? credentials)
    {
        _savedPresence[connection.Id] = connection.Marketplace == MarketplaceKind.Rozetka
            ? RozetkaApiCredentials.ValidToken(credentials?.Token) : !string.IsNullOrWhiteSpace(credentials?.Token);
        if (connection.Marketplace == MarketplaceKind.Rozetka && _savedPresence[connection.Id] != true &&
            (!string.IsNullOrEmpty(credentials?.Login) || !string.IsNullOrEmpty(credentials?.Password)))
            _legacyCredentials.Add(connection.Id);
        else _legacyCredentials.Remove(connection.Id);
    }
    private async Task RefreshCredentialStatusAsync(MarketplaceConnection? connection)
    {
        if (connection is null) return;
        var readRevision = NextCredentialReadRevision(connection.Id);
        _savedPresence[connection.Id] = null; _presenceUnavailable.Remove(connection.Id); NotifyCredentialStatus();
        try
        {
            var credentials = await _secrets.LoadAsync(connection.Id);
            if (_credentialReadRevisions.GetValueOrDefault(connection.Id) != readRevision) return;
            RememberSavedCredentials(connection, credentials);
            // Presence reads never restore saved secrets or overwrite an in-flight token draft.
        }
        catch (Exception)
        {
            if (_credentialReadRevisions.GetValueOrDefault(connection.Id) == readRevision)
                _presenceUnavailable.Add(connection.Id);
        }
        NotifyCredentialStatus();
    }
    private long NextCredentialReadRevision(string connectionId) =>
        _credentialReadRevisions[connectionId] = _credentialReadRevisions.GetValueOrDefault(connectionId) + 1;
    public string Status { get => _status; private set => SetProperty(ref _status, value); }
    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (!SetProperty(ref _busy, value)) return;
            OnPropertyChanged(nameof(CanEdit));
            RaiseCommands();
        }
    }
    public bool CanEdit { get => _canEdit && !IsBusy; private set { if (SetProperty(ref _canEdit, value)) RaiseCommands(); } }
    private void RaiseCommands()
    {
        ((AsyncRelayCommand)TestCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddPromCommand).RaiseCanExecuteChanged();
        ((RelayCommand)AddRozetkaCommand).RaiseCanExecuteChanged();
    }
    public int HistoryDays { get => _historyDays; set => SetProperty(ref _historyDays, value); }
    public int CacheDays { get => _cacheDays; set => SetProperty(ref _cacheDays, value); }

    public async Task LoadAsync()
    {
        IsBusy = true;
        CanEdit = false;
        var settingsLoaded = false;
        try
        {
            MarketplaceSettings settings;
            try { settings = await _settings.LoadAsync(); }
            catch (Exception)
            {
                Status = "Файл налаштувань маркетплейсів недоступний або пошкоджений. Його не буде перезаписано; налаштування Checkbox і принтера доступні.";
                return;
            }
            Connections.Clear();
            _knownConnectionIds.Clear();
            foreach (var connection in settings.Connections) { Connections.Add(connection); _knownConnectionIds.Add(connection.Id); }
            _connectionAliases = settings.ConnectionAliases.ToList();
            HistoryDays = settings.HistoryDays; CacheDays = settings.CacheDays;
            Selected = Connections.FirstOrDefault();
            await CredentialStatusRefresh;
            settingsLoaded = true;
            if (!Connections.Any(connection => connection.Enabled))
            {
                // Disabled integrations retain their files and secrets without opening them.
                Status = "Інтеграції не налаштовані.\nУсі чеки доступні на вкладці “Усі чеки”.";
                return;
            }
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
        finally
        {
            // Lazy tab entry must not let a later Connections.Clear overwrite early edits.
            CanEdit = settingsLoaded;
            IsBusy = false;
        }
    }
    private void Add(MarketplaceKind kind)
    {
        if (!CanEdit) return;
        var connection = new MarketplaceConnection { Marketplace = kind, Name = kind + " — магазин", Enabled = true };
        Connections.Add(connection); Selected = connection;
    }
    private void StageCredentials()
    {
        if (Selected is null) return;
        if (!string.IsNullOrWhiteSpace(Token)) _pending[Selected.Id] = new(Token: Token);
    }
    private async Task<MarketplaceCredentials> ResolveAsync(MarketplaceConnection connection,
        IReadOnlyDictionary<string, MarketplaceCredentials> drafts)
    {
        var saved = await _secrets.LoadAsync(connection.Id) ?? new();
        var credentials = drafts.GetValueOrDefault(connection.Id) ?? saved;
        if (connection.Marketplace == MarketplaceKind.Rozetka && string.IsNullOrWhiteSpace(credentials.Token))
            throw new InvalidOperationException($"{connection.Name}: введіть API-токен Rozetka з кабінету «Налаштування → Безпека API». Логін і пароль кабінету більше не використовуються.");
        if (connection.Marketplace == MarketplaceKind.Rozetka && !RozetkaApiCredentials.ValidToken(credentials.Token))
            throw new InvalidOperationException($"{connection.Name}: некоректний API-токен Rozetka. Скопіюйте його повністю з «Налаштування → Безпека API» без пробілів усередині або перенесень рядків.");
        if (credentials.Token.Any(char.IsControl) || credentials.Token.Trim().Any(char.IsWhiteSpace))
            throw new InvalidOperationException($"{connection.Name}: API-токен не повинен містити пробіли або керівні символи.");
        return new(Token: credentials.Token.Trim());
    }
    public async Task<bool> SaveAsync()
    {
        if (IsBusy) throw new InvalidOperationException("Дочекайтеся завершення збереження або перевірки маркетплейсу.");
        if (!CanEdit) return false;
        if (HistoryDays is < 0 or > 3650 || CacheDays is < 1 or > 90)
            throw new InvalidOperationException("Запас історії: 0–3650 днів. Строк кешу: 1–90 днів.");
        StageCredentials();
        var connections = Connections.Select(CloneConnection).ToList();
        var drafts = new Dictionary<string, MarketplaceCredentials>(_pending);
        var settings = new MarketplaceSettings { Connections = connections, HistoryDays = HistoryDays, CacheDays = CacheDays,
            ConnectionAliases = _connectionAliases.ToList() };
        _saving = true;
        IsBusy = true;
        try
        {
            foreach (var connection in connections)
                if (string.IsNullOrWhiteSpace(connection.Name)) throw new InvalidOperationException("Вкажіть назву магазину.");
            var resolved = new List<(MarketplaceConnection Connection, MarketplaceCredentials Credentials)>();
            // Resolve and validate every draft before the first protected file is changed.
            foreach (var connection in connections)
                if (drafts.ContainsKey(connection.Id) || connection.Marketplace == MarketplaceKind.Rozetka &&
                    connection.Enabled && !_knownConnectionIds.Contains(connection.Id))
                    resolved.Add((connection, await ResolveAsync(connection, drafts)));
            foreach (var (connection, credentials) in resolved)
            {
                // An earlier presence read cannot replace the state of this newer write.
                NextCredentialReadRevision(connection.Id);
                await _secrets.SaveAsync(connection.Id, credentials);
                RememberSavedCredentials(connection, credentials);
                _presenceUnavailable.Remove(connection.Id);
            }
            await _settings.SaveAsync(settings);
            foreach (var connection in connections) _knownConnectionIds.Add(connection.Id);
            _pending.Clear();
            RestoreCredentialFields();
            CredentialsCleared?.Invoke(this, EventArgs.Empty);
            Status = "Налаштування маркетплейсів збережено.";
            NotifyCredentialStatus();
            return resolved.Count > 0;
        }
        finally { _saving = false; IsBusy = false; }
    }
    private static MarketplaceConnection CloneConnection(MarketplaceConnection connection) => new()
    { Id = connection.Id, Marketplace = connection.Marketplace, Name = connection.Name, Enabled = connection.Enabled };
    private async Task TestAsync()
    {
        if (!CanEdit || Selected is not { } selected) return;
        StageCredentials();
        var connection = CloneConnection(selected);
        var drafts = new Dictionary<string, MarketplaceCredentials>(_pending);
        IsBusy = true; Status = "Перевірка читання замовлень…";
        try
        {
            await _sync.TestConnectionAsync(connection, await ResolveAsync(connection, drafts));
            Status = $"{connection.Name}: підключення успішне. Натисніть «Зберегти».";
        }
        catch (Exception ex) { Status = ex is MarketplaceApiException or InvalidOperationException ? ex.Message : "Не вдалося перевірити підключення."; }
        finally { IsBusy = false; }
    }
}

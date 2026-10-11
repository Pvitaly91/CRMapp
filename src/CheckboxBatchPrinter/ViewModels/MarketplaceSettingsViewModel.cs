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
    private bool _canEdit;
    private bool _saving;
    private bool _updatingCredentialFields;
    private long _selectionRevision, _loginEditRevision;
    private int _historyDays = 30, _cacheDays = 30;
    private readonly Dictionary<string, MarketplaceCredentials> _pending = [];
    private readonly Dictionary<string, bool?> _savedPresence = [];
    private readonly Dictionary<string, string> _savedLogins = [];
    private readonly Dictionary<string, long> _credentialReadRevisions = [];
    private readonly HashSet<string> _presenceUnavailable = [];
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
            _selectionRevision++;
            RestoreCredentialFields();
            OnPropertyChanged(nameof(IsProm)); OnPropertyChanged(nameof(IsRozetka));
            ((AsyncRelayCommand)TestCommand).RaiseCanExecuteChanged();
            CredentialStatusRefresh = RefreshCredentialStatusAsync(value);
            NotifyCredentialStatus();
        }
    }
    public bool IsProm => Selected?.Marketplace == MarketplaceKind.Prom;
    public bool IsRozetka => Selected?.Marketplace == MarketplaceKind.Rozetka;
    public string Token { get => _token; set { if (!_saving || _updatingCredentialFields) { if (SetProperty(ref _token, value)) NotifyCredentialStatus(); } } }
    public string Login
    {
        get => _login;
        set
        {
            if (_saving && !_updatingCredentialFields) return;
            if (!SetProperty(ref _login, value)) return;
            if (!_updatingCredentialFields) _loginEditRevision++;
            NotifyCredentialStatus();
        }
    }
    public string Password { get => _password; set { if (!_saving || _updatingCredentialFields) { if (SetProperty(ref _password, value)) NotifyCredentialStatus(); } } }
    private bool HasLoginDraft => IsRozetka && Selected is not null && !string.IsNullOrWhiteSpace(Login) &&
        !string.Equals(Login.Trim(), _savedLogins.GetValueOrDefault(Selected.Id, "").Trim(), StringComparison.Ordinal);
    private bool HasCredentialDraft => Selected is not null && (IsProm ? !string.IsNullOrWhiteSpace(Token) ||
        _pending.TryGetValue(Selected.Id, out var prom) && !string.IsNullOrWhiteSpace(prom.Token) :
        HasLoginDraft || !string.IsNullOrEmpty(Password) || _pending.ContainsKey(Selected.Id));
    public string CredentialStatusKind => HasCredentialDraft ? "Draft" : Selected is null ||
        !_savedPresence.TryGetValue(Selected.Id, out var saved) || saved is null ? "Unknown" : saved.Value ? "Saved" : "Missing";
    public string CredentialStatus => Selected is null ? "Оберіть підключення" : HasCredentialDraft
        ? CredentialIndicator.Text(false, true, "Ключ") : _presenceUnavailable.Contains(Selected.Id)
            ? "Не вдалося прочитати стан збережених даних" : CredentialStatusKind == "Unknown"
                ? "Перевіряю наявність збережених даних…" : CredentialIndicator.Text(CredentialStatusKind == "Saved", false, IsProm ? "Токен Prom" : "Облікові дані Rozetka");
    private void NotifyCredentialStatus() { OnPropertyChanged(nameof(CredentialStatus)); OnPropertyChanged(nameof(CredentialStatusKind)); }
    private void SetCredentialFields(string token, string login, string password)
    {
        _updatingCredentialFields = true;
        try { Token = token; Login = login; Password = password; }
        finally { _updatingCredentialFields = false; }
        CredentialFieldsChanged?.Invoke(this, EventArgs.Empty);
        NotifyCredentialStatus();
    }
    private void RestoreCredentialFields()
    {
        if (Selected is not { } connection) { SetCredentialFields("", "", ""); return; }
        var draft = _pending.GetValueOrDefault(connection.Id);
        SetCredentialFields(connection.Marketplace == MarketplaceKind.Prom ? draft?.Token ?? "" : "",
            connection.Marketplace == MarketplaceKind.Rozetka ? !string.IsNullOrEmpty(draft?.Login)
                ? draft.Login : _savedLogins.GetValueOrDefault(connection.Id, "") : "",
            connection.Marketplace == MarketplaceKind.Rozetka ? draft?.Password ?? "" : "");
    }
    private void RememberSavedCredentials(MarketplaceConnection connection, MarketplaceCredentials? credentials)
    {
        _savedPresence[connection.Id] = connection.Marketplace == MarketplaceKind.Prom
            ? !string.IsNullOrWhiteSpace(credentials?.Token)
            : !string.IsNullOrWhiteSpace(credentials?.Login) && !string.IsNullOrEmpty(credentials?.Password);
        if (connection.Marketplace == MarketplaceKind.Rozetka)
        {
            _savedLogins[connection.Id] = credentials?.Login ?? "";
            if (_pending.TryGetValue(connection.Id, out var draft) &&
                string.Equals(draft.Login.Trim(), _savedLogins[connection.Id].Trim(), StringComparison.Ordinal))
            {
                if (draft.Password.Length == 0) _pending.Remove(connection.Id);
                else _pending[connection.Id] = draft with { Login = "" };
            }
        }
    }
    private async Task RefreshCredentialStatusAsync(MarketplaceConnection? connection)
    {
        if (connection is null) return;
        var selectionRevision = _selectionRevision;
        var loginEditRevision = _loginEditRevision;
        var readRevision = NextCredentialReadRevision(connection.Id);
        _savedPresence[connection.Id] = null; _presenceUnavailable.Remove(connection.Id); NotifyCredentialStatus();
        try
        {
            var credentials = await _secrets.LoadAsync(connection.Id);
            if (_credentialReadRevisions.GetValueOrDefault(connection.Id) != readRevision) return;
            RememberSavedCredentials(connection, credentials);
            // A slow store read may supply only the saved login, never saved secrets or later edits.
            if (connection.Marketplace == MarketplaceKind.Rozetka && ReferenceEquals(Selected, connection) &&
                selectionRevision == _selectionRevision && loginEditRevision == _loginEditRevision)
            {
                var draftLogin = _pending.GetValueOrDefault(connection.Id)?.Login;
                SetCredentialFields(Token, !string.IsNullOrEmpty(draftLogin) ? draftLogin :
                    _savedLogins.GetValueOrDefault(connection.Id, ""), Password);
            }
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
            foreach (var connection in settings.Connections) Connections.Add(connection);
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
        if (IsProm && !string.IsNullOrWhiteSpace(Token)) _pending[Selected.Id] = new(Token: Token.Trim());
        if (IsRozetka)
        {
            var pending = _pending.GetValueOrDefault(Selected.Id) ?? new();
            var draft = new MarketplaceCredentials(Login: Login.Trim().Length > 0 ? HasLoginDraft ? Login.Trim() : "" : pending.Login,
                Password: Password.Length > 0 ? Password : pending.Password);
            if (draft.Login.Length == 0 && draft.Password.Length == 0) _pending.Remove(Selected.Id);
            else _pending[Selected.Id] = draft;
        }
    }
    private async Task<MarketplaceCredentials> ResolveAsync(MarketplaceConnection connection,
        IReadOnlyDictionary<string, MarketplaceCredentials> drafts)
    {
        var saved = await _secrets.LoadAsync(connection.Id) ?? new();
        if (!drafts.TryGetValue(connection.Id, out var draft)) return saved;
        if (connection.Marketplace == MarketplaceKind.Prom) return draft;
        var login = draft.Login.Length > 0 ? draft.Login : saved.Login;
        if (saved.Login.Length > 0 && !string.Equals(login, saved.Login, StringComparison.Ordinal))
            throw new InvalidOperationException("Для іншого логіна Rozetka додайте нове підключення, щоб зберегти ізоляцію замовлень і прив’язок.");
        var password = draft.Password.Length > 0 ? draft.Password : saved.Password;
        if (string.IsNullOrWhiteSpace(login) || password.Length == 0)
            throw new InvalidOperationException($"{connection.Name}: введіть логін і пароль продавця Rozetka. Порожні поля залишають лише вже збережені дані.");
        return new(Login: login, Password: password);
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
        var settings = new MarketplaceSettings { Connections = connections, HistoryDays = HistoryDays, CacheDays = CacheDays };
        _saving = true;
        IsBusy = true;
        try
        {
            foreach (var connection in connections)
                if (string.IsNullOrWhiteSpace(connection.Name)) throw new InvalidOperationException("Вкажіть назву магазину.");
            var resolved = new List<(MarketplaceConnection Connection, MarketplaceCredentials Credentials)>();
            // Resolve and validate every draft before the first protected file is changed.
            foreach (var connection in connections)
                if (drafts.ContainsKey(connection.Id)) resolved.Add((connection, await ResolveAsync(connection, drafts)));
            foreach (var (connection, credentials) in resolved)
            {
                // An earlier presence read cannot replace the state of this newer write.
                NextCredentialReadRevision(connection.Id);
                await _secrets.SaveAsync(connection.Id, credentials);
                RememberSavedCredentials(connection, credentials);
                _presenceUnavailable.Remove(connection.Id);
            }
            await _settings.SaveAsync(settings);
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

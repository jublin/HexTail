using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Json;
using System.Text.Json.Nodes;
using HexTail.Elastic;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class ElasticConnectionEditorViewModel : ReactiveObject
{
    private readonly SettingsViewModel _owner;
    private ElasticAuthMode _authMode;
    private string? _error;
    private bool _isTesting;
    private int _connectionVersion;
    private string _name = string.Empty;
    private string? _status = "Not tested";
    private string _kibanaUrl = string.Empty;
    private string _elasticsearchUrl = string.Empty;
    private string _username = string.Empty;
    private string _secret = string.Empty;
    private string? _savedSnapshot;
    private bool _isSaving;
    private bool _saveFailed;
    private string _cloudId = string.Empty;
    private string? _cloudIdError;
    private int _editBatchDepth;
    private bool _editPending;
    private bool _credentialsTested;

    public ElasticConnectionEditorViewModel(SettingsViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        EndpointResults =
        [
            new ElasticEndpointResultViewModel("Kibana"),
            new ElasticEndpointResultViewModel("Elasticsearch"),
            new ElasticEndpointResultViewModel("Credentials"),
        ];
        Views.CollectionChanged += (_, _) => NotifySavePrerequisitesChanged();
    }

    /// <summary>Raised whenever a field that affects the connection test changes.</summary>
    public event EventHandler? ConnectionEdited;

    public string Id { get; }
    public string Name
    {
        get => _name;
        set
        {
            this.RaiseAndSetIfChanged(ref _name, value);
            this.RaisePropertyChanged(nameof(NameError));
            NotifySavePrerequisitesChanged();
        }
    }

    public string? NameError => string.IsNullOrWhiteSpace(Name) ? "Enter a connection name." : null;

    /// <summary>Pasting a Cloud ID fills the URLs; it is a convenience input and is never persisted.</summary>
    public string CloudId
    {
        get => _cloudId;
        set
        {
            value ??= string.Empty;
            if (_cloudId == value)
                return;
            this.RaiseAndSetIfChanged(ref _cloudId, value);
            ApplyCloudId(value);
        }
    }
    public string? CloudIdError
    {
        get => _cloudIdError;
        private set => this.RaiseAndSetIfChanged(ref _cloudIdError, value);
    }
    public string KibanaUrl
    {
        get => _kibanaUrl;
        set
        {
            if (_kibanaUrl == value)
                return;
            this.RaiseAndSetIfChanged(ref _kibanaUrl, value);
            InvalidateConnectionTest();
            this.RaisePropertyChanged(nameof(KibanaUrlError));
            NotifySavePrerequisitesChanged();
        }
    }
    public string ElasticsearchUrl
    {
        get => _elasticsearchUrl;
        set
        {
            if (_elasticsearchUrl == value)
                return;
            this.RaiseAndSetIfChanged(ref _elasticsearchUrl, value);
            InvalidateConnectionTest();
            this.RaisePropertyChanged(nameof(ElasticsearchUrlError));
            NotifySavePrerequisitesChanged();
        }
    }
    public ElasticAuthMode AuthMode
    {
        get => _authMode;
        set
        {
            if (_authMode == value)
                return;
            this.RaiseAndSetIfChanged(ref _authMode, value);
            this.RaisePropertyChanged(nameof(IsAuthenticated));
            this.RaisePropertyChanged(nameof(IsBasic));
            InvalidateConnectionTest();
            NotifySavePrerequisitesChanged();
        }
    }

    /// <summary>The login methods with the text people read; the enum values stay what is stored.</summary>
    public IReadOnlyList<ElasticAuthModeChoice> AuthModeChoices { get; } =
    [
        new(ElasticAuthMode.Anonymous, "Anonymous"),
        new(ElasticAuthMode.Basic, "Username and password"),
        new(ElasticAuthMode.ApiKey, "API key"),
    ];
    public bool IsAuthenticated => AuthMode != ElasticAuthMode.Anonymous;
    public bool IsBasic => AuthMode == ElasticAuthMode.Basic;
    public string Username
    {
        get => _username;
        set
        {
            if (_username == value)
                return;
            this.RaiseAndSetIfChanged(ref _username, value);
            InvalidateConnectionTest();
            NotifySavePrerequisitesChanged();
        }
    }
    public string Secret
    {
        get => _secret;
        set
        {
            if (_secret == value)
                return;
            this.RaiseAndSetIfChanged(ref _secret, value);
            InvalidateConnectionTest();
            NotifySavePrerequisitesChanged();
        }
    }
    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ObservableCollection<ElasticViewEditorViewModel> Views { get; } = [];
    public ObservableCollection<ElasticDataViewChoiceViewModel> DataViews { get; } = [];
    public IReadOnlyList<ElasticEndpointResultViewModel> EndpointResults { get; }
    public bool EndpointsOk => EndpointResults[0].IsOk && EndpointResults[1].IsOk;
    public bool CanTestConnection =>
        KibanaUrlError is null
        && ElasticsearchUrlError is null
        && AuthMode switch
        {
            ElasticAuthMode.Basic => !string.IsNullOrWhiteSpace(Username) && HasSecret,
            ElasticAuthMode.ApiKey => HasSecret,
            _ => true,
        };
    public bool IsSaved => SavedConnection is not null;

    /// <summary>What saving the connection on its own needs: valid endpoints and no save running.</summary>
    internal bool CanSaveConnectionOnly =>
        !IsSaving
        && NameError is null
        && KibanaUrlError is null
        && ElasticsearchUrlError is null
        && (!NeedsSecret || HasSecret);
    private ElasticConnectionSettings? SavedConnection => _owner.SavedConnection(Id);
    private bool NeedsSecret => AuthMode is ElasticAuthMode.Basic or ElasticAuthMode.ApiKey;

    // The stored secret belongs to the saved login method and servers; with another method or host
    // it would be sent where it was never meant to go, so a new one has to be typed.
    private bool HasStoredSecret =>
        SavedConnection is { AuthMode: ElasticAuthMode.Basic or ElasticAuthMode.ApiKey } saved
        && saved.AuthMode == AuthMode
        && ElasticUrls.SameOrigin(saved.KibanaUrl, KibanaUrl)
        && ElasticUrls.SameOrigin(saved.ElasticsearchUrl, ElasticsearchUrl);
    private bool HasSecret => !string.IsNullOrWhiteSpace(Secret) || HasStoredSecret;
    public bool CanSave =>
        !IsTesting
        && !IsSaving
        && KibanaUrlError is null
        && ElasticsearchUrlError is null
        && Views.All(view => view.CanSave);
    public bool IsDirty => !string.IsNullOrEmpty(Secret) || _savedSnapshot != DraftSnapshot();
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSaving, value);
            NotifySavePrerequisitesChanged();
        }
    }
    public string SaveStatus =>
        IsSaving ? "Saving…"
        : _saveFailed ? "Save failed"
        : IsDirty ? "Unsaved"
        : "Saved";
    public string? KibanaUrlError => UrlError(KibanaUrl, "Kibana");
    public string? ElasticsearchUrlError => UrlError(ElasticsearchUrl, "Elasticsearch");
    public string? Error
    {
        get => _error;
        private set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(IsFailed));
        }
    }
    public string? Status
    {
        get => _status;
        private set
        {
            this.RaiseAndSetIfChanged(ref _status, value);
            this.RaisePropertyChanged(nameof(IsConnected));
        }
    }
    public bool IsConnected => Status?.StartsWith("Connected", StringComparison.Ordinal) is true;
    public bool IsFailed => Error is not null;
    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isTesting, value);
            NotifySavePrerequisitesChanged();
        }
    }

    internal void Sync(ElasticConnectionSettings settings)
    {
        Name = settings.Name;
        KibanaUrl = settings.KibanaUrl;
        ElasticsearchUrl = settings.ElasticsearchUrl;
        AuthMode = settings.AuthMode;
        Username = settings.Username ?? string.Empty;
        DataViews.Clear();
        foreach (
            var view in settings
                .Views.Where(view =>
                    !string.IsNullOrWhiteSpace(view.DataViewId)
                    && !string.IsNullOrWhiteSpace(view.DataViewTitle)
                )
                .Select(view => new ElasticDataViewSummary(view.DataViewId!, view.DataViewTitle!))
                .DistinctBy(view => view.Id, StringComparer.Ordinal)
        )
            DataViews.Add(new ElasticDataViewChoiceViewModel(view.Id, view.Title));
        while (Views.Count > settings.Views.Count)
            Views.RemoveAt(Views.Count - 1);
        for (var index = 0; index < settings.Views.Count; index++)
        {
            if (index != Views.Count)
                continue;
            var view = new ElasticViewEditorViewModel(this, settings.Views[index].Id);
            Views.Add(view);
            view.Sync(settings.Views[index]);
        }
        _savedSnapshot = DraftSnapshot();
        NotifySavePrerequisitesChanged();
    }

    internal ElasticConnectionSettings ToSettings(bool includeViews = true) =>
        new()
        {
            Id = Id,
            Name = Name,
            KibanaUrl = KibanaUrl,
            ElasticsearchUrl = ElasticsearchUrl,
            AuthMode = AuthMode,
            Username = Username,
            Views = includeViews ? Views.Select(view => view.ToSettings()).ToList() : [],
        };

    internal Task<IReadOnlyList<ElasticDataViewSummary>> GetDataViewsAsync() =>
        _owner.GetDataViewsAsync(ToSettings(includeViews: false), Secret);

    internal Task<ElasticDataView> GetDataViewAsync(string dataViewId) =>
        _owner.GetDataViewAsync(ToSettings(includeViews: false), dataViewId, Secret);

    internal System.Reactive.Concurrency.IScheduler Scheduler => _owner.Scheduler;

    internal Task<long> CountAsync(string title, string timeField, string field, string value) =>
        _owner.CountAsync(ToSettings(includeViews: false), title, timeField, field, value, Secret);

    internal Task<IReadOnlyList<ElasticHit>> PreviewAsync(
        string title,
        string timeField,
        string? field,
        string? value,
        IReadOnlyList<string> outputFields
    ) =>
        _owner.PreviewAsync(
            ToSettings(includeViews: false),
            title,
            timeField,
            field,
            value,
            outputFields,
            Secret
        );

    /// <summary>Adds an empty view editor; the setup flow passes a blank name and fills it in later.</summary>
    internal ElasticViewEditorViewModel AddNewView(string name = "New view")
    {
        var view = new ElasticViewEditorViewModel(this, Guid.NewGuid().ToString("N"))
        {
            Name = name,
        };
        Views.Add(view);
        return view;
    }

    private void InvalidateConnectionTest()
    {
        if (_editBatchDepth > 0)
        {
            _editPending = true;
            return;
        }

        _connectionVersion++;
        Status = "Not tested";
        Error = null;
        _credentialsTested = false;
        foreach (var result in EndpointResults)
            result.Reset();
        this.RaisePropertyChanged(nameof(EndpointsOk));
        ConnectionEdited?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCloudId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            CloudIdError = null;
            return;
        }

        if (!HexTail.Elastic.CloudId.TryDecode(value, out var endpoints) || endpoints is null)
        {
            CloudIdError = "Enter a valid Cloud ID (name:base64…).";
            return;
        }

        CloudIdError = null;
        // Both URLs change together, so the test is invalidated once, not once per field.
        _editBatchDepth++;
        try
        {
            ElasticsearchUrl = endpoints.ElasticsearchUrl;
            if (endpoints.KibanaUrl is not null)
                KibanaUrl = endpoints.KibanaUrl;
        }
        finally
        {
            _editBatchDepth--;
        }

        if (_editBatchDepth == 0 && _editPending)
        {
            _editPending = false;
            InvalidateConnectionTest();
        }
    }

    private async Task TestConnectionAsync()
    {
        var version = _connectionVersion;
        IsTesting = true;
        Error = null;
        Status = "Checking…";
        _credentialsTested = false;
        EndpointResults[0].Set(EndpointState.Checking, "Checking…");
        EndpointResults[1].Set(EndpointState.Checking, "Checking…");
        EndpointResults[2].Reset();
        this.RaisePropertyChanged(nameof(EndpointsOk));
        try
        {
            IReadOnlyList<ElasticDataViewSummary>? views = null;
            var viewsError = CaptureAsync(async () => views = await GetDataViewsAsync());
            var elasticsearchError = CaptureAsync(() =>
                _owner.CheckElasticsearchAsync(ToSettings(includeViews: false), Secret)
            );
            var kibanaFailure = await viewsError;
            var elasticsearchFailure = await elasticsearchError;
            if (version != _connectionVersion)
                return;
            EndpointResults[0]
                .Set(
                    kibanaFailure is null ? EndpointState.Ok : EndpointState.Failed,
                    kibanaFailure is null
                        ? $"✓ {DataViewCount(views!.Count)}"
                        : $"✗ {kibanaFailure.Message}"
                );
            EndpointResults[1]
                .Set(
                    elasticsearchFailure is null ? EndpointState.Ok : EndpointState.Failed,
                    elasticsearchFailure is null
                        ? "✓ reachable"
                        : $"✗ {elasticsearchFailure.Message}"
                );
            ShowCredentialsResult();
            this.RaisePropertyChanged(nameof(EndpointsOk));
            if (kibanaFailure is null)
            {
                UpdateDataViews(views!);
                foreach (var editor in Views)
                    editor.NotifyDataViewSelectionChanged();
                foreach (var editor in Views)
                    await editor.RefreshDataViewAsync(
                        views!.FirstOrDefault(view => view.Id == editor.SelectedDataViewId)
                    );
                if (version != _connectionVersion)
                    return;
            }

            var failure = kibanaFailure ?? elasticsearchFailure;
            if (failure is not null)
            {
                Error = failure.Message;
                Status = "Connection failed";
            }
            else
                Status = $"Connected ({DataViewCount(views!.Count)})";
        }
        catch (Exception exception)
        {
            if (version != _connectionVersion)
                return;
            Error = exception.Message;
            Status = "Connection failed";
        }
        finally
        {
            IsTesting = false;
        }
    }

    // Every failure counts, cancellation included: no token is wired, so a cancelled request is a timeout.
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static string DataViewCount(int count) =>
        $"{count} data view{(count == 1 ? string.Empty : "s")}";

    private void ShowCredentialsResult()
    {
        _credentialsTested = true;
        var credentials = EndpointResults[2];
        if (!NeedsSecret)
            credentials.Set(EndpointState.Ok, "no credentials (anonymous)");
        else if (HasStoredSecret && string.IsNullOrWhiteSpace(Secret))
            credentials.Set(EndpointState.Ok, "stored in keyring");
        else
            credentials.Set(EndpointState.NotTested, "will be stored in keyring on Next");
    }

    private void UpdateDataViews(IReadOnlyList<ElasticDataViewSummary> views)
    {
        for (var index = DataViews.Count - 1; index >= 0; index--)
        {
            var current = views.FirstOrDefault(view => view.Id == DataViews[index].Id);
            if (current is null)
                DataViews.RemoveAt(index);
            else
                DataViews[index].Title = current.Title;
        }

        foreach (var view in views)
            if (!DataViews.Any(existing => existing.Id == view.Id))
                DataViews.Add(new ElasticDataViewChoiceViewModel(view.Id, view.Title));
    }

    internal void NotifySavePrerequisitesChanged()
    {
        this.RaisePropertyChanged(nameof(CanSave));
        this.RaisePropertyChanged(nameof(CanTestConnection));
        this.RaisePropertyChanged(nameof(IsSaved));
        this.RaisePropertyChanged(nameof(IsDirty));
        this.RaisePropertyChanged(nameof(SaveStatus));
    }

    private string DraftSnapshot() =>
        JsonSerializer.Serialize(
            new
            {
                Name,
                KibanaUrl,
                ElasticsearchUrl,
                AuthMode,
                Username,
                Views = Views.Select(view => new
                {
                    view.Id,
                    view.Name,
                    view.SelectedDataViewId,
                    view.DataViewTitle,
                    view.TimeFieldName,
                    view.ServerField,
                    view.NamespaceField,
                    OutputFields = view.OutputFieldNames,
                    Sources = view.Sources.Select(source => source.ToSettings()),
                }),
            }
        );

    private static string? UrlError(string value, string what) =>
        !ElasticUrls.IsHttp(value) ? $"Enter an absolute HTTP or HTTPS {what} URL."
        : ElasticUrls.HasUserInfo(value)
            ? $"Enter an absolute HTTP or HTTPS {what} URL without a user name or password."
        : null;

    /// <summary>
    /// Persists only the connection (with the views already saved), leaving any in-progress view
    /// out so the setup flow can store the credentials before the views are complete.
    /// </summary>
    internal async Task<bool> SaveConnectionOnlyAsync()
    {
        if (!CanSaveConnectionOnly)
        {
            // Say why, so the setup flow has something to show instead of a silent refusal.
            if (!IsSaving && NameError is { } nameError)
                Error = nameError;
            else if (!IsSaving && NeedsSecret && !HasSecret)
                Error =
                    AuthMode == ElasticAuthMode.ApiKey
                        ? "Enter the API key for this connection."
                        : "Enter the password for this connection.";
            return false;
        }
        var secret = Secret;
        var snapshot = DraftSnapshot();
        _saveFailed = false;
        IsSaving = true;
        try
        {
            var connection = ToSettings(includeViews: false) with { Views = _owner.SavedViews(Id) };
            await _owner.SaveElasticConnectionAsync(connection, secret);
            if (Secret == secret)
            {
                // The stored secret is now what a test would use, so earlier results stay valid.
                _secret = string.Empty;
                this.RaisePropertyChanged(nameof(Secret));
            }
            UpdateSavedConnectionSnapshot(snapshot);
            if (_credentialsTested)
                ShowCredentialsResult();
            Error = null;
            return true;
        }
        catch (Exception exception)
        {
            _saveFailed = true;
            Error = exception.Message;
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>
    /// Persists the connection and one finished view, together with the views that were already
    /// saved. Other views still being edited are left out, so they can neither be written
    /// half-done nor make this save fail.
    /// </summary>
    internal async Task<bool> SaveViewAsync(ElasticViewEditorViewModel view)
    {
        if (!CanSaveConnectionOnly || !view.CanSave)
            return false;
        var secret = Secret;
        var snapshot = DraftSnapshot();
        _saveFailed = false;
        IsSaving = true;
        try
        {
            var viewSettings = view.ToSettings();
            var views = _owner.SavedViews(Id);
            var existing = views.FindIndex(item => item.Id == viewSettings.Id);
            if (existing >= 0)
                views[existing] = viewSettings;
            else
                views.Add(viewSettings);
            var connection = ToSettings(includeViews: false) with { Views = views };
            await _owner.SaveElasticConnectionAsync(connection, secret);
            if (Secret == secret)
            {
                _secret = string.Empty;
                this.RaisePropertyChanged(nameof(Secret));
            }
            UpdateSavedConnectionSnapshot(snapshot, view.Id);
            if (_credentialsTested)
                ShowCredentialsResult();
            Error = null;
            return true;
        }
        catch (Exception exception)
        {
            _saveFailed = true;
            Error = exception.Message;
            return false;
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void UpdateSavedConnectionSnapshot(string submitted, string? savedViewId = null)
    {
        // Only the connection fields were written; the views part keeps describing what is saved.
        // The snapshot is the draft as it was submitted, so an edit made during the save stays unsaved.
        var current = JsonNode.Parse(submitted)!.AsObject();
        var saved = _savedSnapshot is null
            ? current.DeepClone().AsObject()
            : JsonNode.Parse(_savedSnapshot)!.AsObject();
        if (_savedSnapshot is null)
            saved["Views"] = new JsonArray();
        else
            foreach (var key in ConnectionSnapshotKeys)
                saved[key] = current[key]?.DeepClone();
        if (
            savedViewId is not null
            && saved["Views"] is JsonArray savedViews
            && current["Views"] is JsonArray currentViews
            && currentViews.FirstOrDefault(node => node?["Id"]?.GetValue<string>() == savedViewId)
                is { } currentView
        )
        {
            var index = -1;
            for (var position = 0; position < savedViews.Count; position++)
                if (savedViews[position]?["Id"]?.GetValue<string>() == savedViewId)
                    index = position;
            if (index >= 0)
                savedViews[index] = currentView.DeepClone();
            else
                savedViews.Add(currentView.DeepClone());
        }
        _savedSnapshot = saved.ToJsonString();
    }

    private static readonly string[] ConnectionSnapshotKeys =
    [
        nameof(Name),
        nameof(KibanaUrl),
        nameof(ElasticsearchUrl),
        nameof(AuthMode),
        nameof(Username),
    ];
}

internal sealed record ElasticAuthModeChoice(ElasticAuthMode Mode, string DisplayName);

internal sealed class ElasticDataViewChoiceViewModel(string id, string title) : ReactiveObject
{
    private string _title = title;
    public string Id { get; } = id;
    public string Title
    {
        get => _title;
        set => this.RaiseAndSetIfChanged(ref _title, value);
    }
}

using System.Collections.ObjectModel;
using System.Reactive;
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
    private string _name = string.Empty;
    private string? _status = "Not tested";
    private string _kibanaUrl = string.Empty;
    private string _elasticsearchUrl = string.Empty;

    public ElasticConnectionEditorViewModel(SettingsViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        AddViewCommand = ReactiveCommand.Create(AddView);
        RemoveViewCommand = ReactiveCommand.Create<ElasticViewEditorViewModel>(RemoveView);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        SaveCommand = ReactiveCommand.CreateFromTask(
            SaveAsync,
            this.WhenAnyValue(editor => editor.CanSave)
        );
        Views.CollectionChanged += (_, _) => NotifySavePrerequisitesChanged();
    }

    public string Id { get; }
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }
    public string KibanaUrl
    {
        get => _kibanaUrl;
        set
        {
            if (_kibanaUrl == value)
                return;
            this.RaiseAndSetIfChanged(ref _kibanaUrl, value);
            Status = "Not tested";
            Error = null;
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
            Status = "Not tested";
            Error = null;
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
            Status = "Not tested";
            Error = null;
        }
    }
    public IReadOnlyList<ElasticAuthMode> AuthModes { get; } = Enum.GetValues<ElasticAuthMode>();
    public bool IsAuthenticated => AuthMode != ElasticAuthMode.Anonymous;
    public bool IsBasic => AuthMode == ElasticAuthMode.Basic;
    public string Username { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public ReactiveCommand<Unit, Unit> AddViewCommand { get; }
    public ReactiveCommand<ElasticViewEditorViewModel, Unit> RemoveViewCommand { get; }
    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ObservableCollection<ElasticViewEditorViewModel> Views { get; } = [];
    public ObservableCollection<ElasticDataViewChoiceViewModel> DataViews { get; } = [];
    public bool CanSave =>
        !IsTesting
        && KibanaUrlError is null
        && ElasticsearchUrlError is null
        && Views.All(view => view.CanSave);
    public string? KibanaUrlError =>
        IsHttpUrl(KibanaUrl) ? null : "Enter an absolute HTTP or HTTPS Kibana URL.";
    public string? ElasticsearchUrlError =>
        IsHttpUrl(ElasticsearchUrl) ? null : "Enter an absolute HTTP or HTTPS Elasticsearch URL.";
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

    private void AddView()
    {
        Views.Add(
            new ElasticViewEditorViewModel(this, Guid.NewGuid().ToString("N")) { Name = "New view" }
        );
    }

    private void RemoveView(ElasticViewEditorViewModel view) => Views.Remove(view);

    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        Error = null;
        Status = "Checking…";
        try
        {
            var viewsTask = GetDataViewsAsync();
            var elasticsearchTask = _owner.CheckElasticsearchAsync(
                ToSettings(includeViews: false),
                Secret
            );
            await Task.WhenAll(viewsTask, elasticsearchTask);
            var views = await viewsTask;
            UpdateDataViews(views);
            foreach (var editor in Views)
                editor.NotifyDataViewSelectionChanged();
            foreach (var editor in Views)
                await editor.RefreshDataViewAsync(
                    views.FirstOrDefault(view => view.Id == editor.SelectedDataViewId)
                );
            Status =
                $"Connected ({views.Count} data view{(views.Count == 1 ? string.Empty : "s")})";
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            Status = "Connection failed";
        }
        finally
        {
            IsTesting = false;
        }
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

    internal void NotifySavePrerequisitesChanged() => this.RaisePropertyChanged(nameof(CanSave));

    private static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private async Task SaveAsync()
    {
        try
        {
            await _owner.SaveElasticConnectionAsync(ToSettings(), Secret);
            Secret = string.Empty;
            Error = null;
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
    }
}

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

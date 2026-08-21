using System.Collections.ObjectModel;
using System.Reactive;
using HexTailSharp.Elastic;
using HexTailSharp.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class ElasticConnectionEditorViewModel : ReactiveObject
{
    private readonly SettingsViewModel _owner;
    private ElasticAuthMode _authMode;
    private string? _error;
    private string _name = string.Empty;

    public ElasticConnectionEditorViewModel(SettingsViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        AddViewCommand = ReactiveCommand.Create(AddView);
        RemoveViewCommand = ReactiveCommand.Create<ElasticViewEditorViewModel>(RemoveView);
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
    }

    public string Id { get; }
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
    }
    public string KibanaUrl { get; set; } = string.Empty;
    public string ElasticsearchUrl { get; set; } = string.Empty;
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
        }
    }
    public IReadOnlyList<ElasticAuthMode> AuthModes { get; } = Enum.GetValues<ElasticAuthMode>();
    public bool IsAuthenticated => AuthMode != ElasticAuthMode.Anonymous;
    public bool IsBasic => AuthMode == ElasticAuthMode.Basic;
    public string Username { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public ReactiveCommand<Unit, Unit> AddViewCommand { get; }
    public ReactiveCommand<ElasticViewEditorViewModel, Unit> RemoveViewCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ObservableCollection<ElasticViewEditorViewModel> Views { get; } = [];
    public string? Error
    {
        get => _error;
        private set => this.RaiseAndSetIfChanged(ref _error, value);
    }

    internal void Sync(ElasticConnectionSettings settings)
    {
        Name = settings.Name;
        KibanaUrl = settings.KibanaUrl;
        ElasticsearchUrl = settings.ElasticsearchUrl;
        AuthMode = settings.AuthMode;
        Username = settings.Username ?? string.Empty;
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

    internal ElasticConnectionSettings ToSettings() =>
        new()
        {
            Id = Id,
            Name = Name,
            KibanaUrl = KibanaUrl,
            ElasticsearchUrl = ElasticsearchUrl,
            AuthMode = AuthMode,
            Username = Username,
            Views = Views.Select(view => view.ToSettings()).ToList(),
        };

    internal Task<IReadOnlyList<ElasticDataViewSummary>> GetDataViewsAsync() =>
        _owner.GetDataViewsAsync(ToSettings(), Secret);

    internal Task<ElasticDataView> GetDataViewAsync(string dataViewId) =>
        _owner.GetDataViewAsync(ToSettings(), dataViewId, Secret);

    private void AddView()
    {
        Views.Add(
            new ElasticViewEditorViewModel(this, Guid.NewGuid().ToString("N")) { Name = "New view" }
        );
    }

    private void RemoveView(ElasticViewEditorViewModel view) => Views.Remove(view);

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

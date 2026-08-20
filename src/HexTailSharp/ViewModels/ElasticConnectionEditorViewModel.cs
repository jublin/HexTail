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
    private bool _isTesting;
    private string? _selectedDataViewId;

    public ElasticConnectionEditorViewModel(SettingsViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync);
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        AddSourceCommand = ReactiveCommand.Create(AddSource);
        RemoveSourceCommand = ReactiveCommand.Create<ElasticSourceSettingViewModel>(RemoveSource);
    }

    public string Id { get; }
    public string Name { get; set; } = string.Empty;
    public string KibanaUrl { get; set; } = string.Empty;
    public string ElasticsearchUrl { get; set; } = string.Empty;
    public ElasticAuthMode AuthMode { get; set; }
    public IReadOnlyList<ElasticAuthMode> AuthModes { get; } = Enum.GetValues<ElasticAuthMode>();
    public bool IsAuthenticated => AuthMode != ElasticAuthMode.Anonymous;
    public bool IsBasic => AuthMode == ElasticAuthMode.Basic;
    public string Username { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSourceCommand { get; }
    public ReactiveCommand<ElasticSourceSettingViewModel, Unit> RemoveSourceCommand { get; }
    public ObservableCollection<ElasticDataViewSummary> DataViews { get; } = [];
    public string? Error { get; private set; }
    public bool IsTesting
    {
        get => _isTesting;
        private set => this.RaiseAndSetIfChanged(ref _isTesting, value);
    }
    public string? DataViewId { get; set; }
    public string? SelectedDataViewId
    {
        get => _selectedDataViewId;
        set
        {
            if (string.Equals(_selectedDataViewId, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _selectedDataViewId, value);
            if (value is null)
                return;
            DataViewId = value;
            _ = LoadDataViewAsync(value);
        }
    }
    public string? DataViewTitle { get; set; }
    public string? TimeFieldName { get; set; }
    public string? ServerField { get; set; }
    public string? NamespaceField { get; set; }
    public ObservableCollection<ElasticFieldOptionViewModel> Fields { get; } = [];
    public ObservableCollection<ElasticSourceSettingViewModel> Sources { get; } = [];
    public IEnumerable<string> FieldNames => Fields.Select(option => option.Name);

    public ElasticConnectionSettings ToSettings() =>
        new()
        {
            Id = Id,
            Name = Name,
            KibanaUrl = KibanaUrl,
            ElasticsearchUrl = ElasticsearchUrl,
            AuthMode = AuthMode,
            Username = Username,
            DataViewId = DataViewId,
            DataViewTitle = DataViewTitle,
            TimeFieldName = TimeFieldName,
            ServerField = ServerField,
            NamespaceField = NamespaceField,
            OutputFields = Fields
                .Where(field => field.IsOutput)
                .Select(field => field.Name)
                .ToList(),
            Sources = Sources.Select(source => source.ToSettings()).ToList(),
        };

    internal async Task SaveAsync()
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

    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        Error = null;
        try
        {
            var views = await _owner.GetDataViewsAsync(ToSettings());
            DataViews.Clear();
            foreach (var view in views)
                DataViews.Add(view);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private async Task LoadDataViewAsync(string id)
    {
        try
        {
            var view = await _owner.GetDataViewAsync(ToSettings() with { DataViewId = id }, id);
            DataViewId = view.Id;
            DataViewTitle = view.Title;
            TimeFieldName = view.TimeFieldName;
            Fields.Clear();
            foreach (var field in view.Fields)
                Fields.Add(new ElasticFieldOptionViewModel(field.Name));
            this.RaisePropertyChanged(nameof(FieldNames));
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
    }

    private void AddSource() =>
        Sources.Add(new ElasticSourceSettingViewModel(Guid.NewGuid().ToString("N")));

    private void RemoveSource(ElasticSourceSettingViewModel source) => Sources.Remove(source);
}

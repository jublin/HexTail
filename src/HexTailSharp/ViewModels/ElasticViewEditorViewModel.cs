using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using HexTailSharp.Elastic;
using HexTailSharp.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class ElasticViewEditorViewModel : ReactiveObject
{
    private readonly ElasticConnectionEditorViewModel _owner;
    private string? _selectedDataViewId;
    private string? _error;
    private string _name = string.Empty;
    private string _outputFieldQuery = string.Empty;
    private readonly DispatcherTimer _fieldFilterTimer;
    private readonly List<ElasticFieldOptionViewModel> _fieldSnapshot = [];
    private int _fieldFilterVersion;

    public ElasticViewEditorViewModel(ElasticConnectionEditorViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        AddSourceCommand = ReactiveCommand.Create(AddSource);
        RemoveSourceCommand = ReactiveCommand.Create<ElasticSourceSettingViewModel>(RemoveSource);
        _fieldFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fieldFilterTimer.Tick += (_, _) => ApplyQueuedFieldFilter();
    }

    public string Id { get; }
    public string Name
    {
        get => _name;
        set => this.RaiseAndSetIfChanged(ref _name, value);
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
    public ObservableCollection<ElasticDataViewSummary> DataViews => _owner.DataViews;
    public ObservableCollection<ElasticFieldOptionViewModel> Fields { get; } = [];
    public ObservableCollection<ElasticSourceSettingViewModel> Sources { get; } = [];
    public IEnumerable<string> FieldNames => Fields.Select(option => option.Name);
    public ObservableCollection<ElasticFieldOptionViewModel> VisibleFields { get; } = [];
    public string OutputFieldQuery
    {
        get => _outputFieldQuery;
        set
        {
            if (string.Equals(_outputFieldQuery, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _outputFieldQuery, value);
            QueueFieldFilter();
        }
    }
    public string FilterValue
    {
        get => Sources.FirstOrDefault()?.ServerValue ?? string.Empty;
        set
        {
            var source = Sources.FirstOrDefault();
            if (source is null)
            {
                AddSource();
                source = Sources[0];
            }
            source.ServerValue = value;
            this.RaisePropertyChanged();
        }
    }
    public ReactiveCommand<Unit, Unit> AddSourceCommand { get; }
    public ReactiveCommand<ElasticSourceSettingViewModel, Unit> RemoveSourceCommand { get; }
    public string? Error
    {
        get => _error;
        private set => this.RaiseAndSetIfChanged(ref _error, value);
    }

    internal void Sync(ElasticViewSettings settings)
    {
        Name = settings.Name;
        DataViewId = settings.DataViewId;
        _selectedDataViewId = settings.DataViewId;
        DataViewTitle = settings.DataViewTitle;
        TimeFieldName = settings.TimeFieldName;
        ServerField = settings.ServerField;
        NamespaceField = settings.NamespaceField;
        Fields.Clear();
        _fieldSnapshot.Clear();
        foreach (var field in settings.OutputFields.Distinct(StringComparer.Ordinal))
            AddField(new ElasticFieldOptionViewModel(field) { IsOutput = true });
        this.RaisePropertyChanged(nameof(FieldNames));
        RefreshVisibleFields();
        Sources.Clear();
        foreach (var source in settings.Sources)
            Sources.Add(
                new ElasticSourceSettingViewModel(source.Id)
                {
                    ServerValue = source.ServerValue,
                    NamespaceValue = source.NamespaceValue,
                }
            );
        this.RaisePropertyChanged(nameof(FilterValue));
    }

    internal ElasticViewSettings ToSettings() =>
        new()
        {
            Id = Id,
            Name = Name,
            DataViewId = DataViewId,
            DataViewTitle = DataViewTitle,
            TimeFieldName = TimeFieldName,
            ServerField = ServerField,
            NamespaceField = NamespaceField ?? ServerField,
            OutputFields = Fields
                .Where(field => field.IsOutput)
                .Select(field => field.Name)
                .ToList(),
            Sources = Sources
                .Select(source =>
                {
                    var settings = source.ToSettings();
                    return string.IsNullOrWhiteSpace(settings.NamespaceValue)
                        ? settings with
                        {
                            NamespaceValue = settings.ServerValue,
                        }
                        : settings;
                })
                .ToList(),
        };

    private async Task LoadDataViewAsync(string id)
    {
        try
        {
            var view = await _owner.GetDataViewAsync(id);
            DataViewId = view.Id;
            DataViewTitle = view.Title;
            TimeFieldName = view.TimeFieldName;
            Fields.Clear();
            _fieldSnapshot.Clear();
            foreach (var field in view.Fields)
                AddField(new ElasticFieldOptionViewModel(field.Name));
            this.RaisePropertyChanged(nameof(FieldNames));
            RefreshVisibleFields();
        }
        catch (Exception exception)
        {
            Error = exception.Message;
        }
    }

    private void AddSource() =>
        Sources.Add(new ElasticSourceSettingViewModel(Guid.NewGuid().ToString("N")));

    private void RemoveSource(ElasticSourceSettingViewModel source) => Sources.Remove(source);

    private void AddField(ElasticFieldOptionViewModel field)
    {
        _fieldSnapshot.Add(field);
        field.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ElasticFieldOptionViewModel.IsOutput))
            {
                _fieldFilterVersion++;
                _fieldFilterTimer.Stop();
                RefreshVisibleFields();
            }
        };
        Fields.Add(field);
    }

    private void QueueFieldFilter()
    {
        _fieldFilterVersion++;
        _fieldFilterTimer.Stop();
        _fieldFilterTimer.Start();
    }

    private void ApplyQueuedFieldFilter()
    {
        _fieldFilterTimer.Stop();
        var version = _fieldFilterVersion;
        var query = OutputFieldQuery.Trim();
        var fields = _fieldSnapshot.Select(field => (field, field.Name, field.IsOutput)).ToArray();
        _ = ApplyFieldFilterAsync(version, query, fields);
    }

    private async Task ApplyFieldFilterAsync(
        int version,
        string query,
        IReadOnlyList<(ElasticFieldOptionViewModel Field, string Name, bool IsOutput)> fields
    )
    {
        var visible = await Task.Run(() => FilterFields(fields, query)).ConfigureAwait(false);
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _fieldFilterVersion)
                return;
            VisibleFields.Clear();
            foreach (var field in visible)
                VisibleFields.Add(field);
        });
    }

    internal static IReadOnlyList<ElasticFieldOptionViewModel> FilterFields(
        IReadOnlyList<(ElasticFieldOptionViewModel Field, string Name, bool IsOutput)> fields,
        string query
    ) =>
        fields
            .Where(item =>
                string.IsNullOrWhiteSpace(query)
                    ? item.IsOutput
                    : item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .Select(item => item.Field)
            .ToArray();

    private void RefreshVisibleFields()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshVisibleFields);
            return;
        }
        var query = OutputFieldQuery.Trim();
        var fields = string.IsNullOrWhiteSpace(query)
            ? Fields.Where(option => option.IsOutput)
            : Fields.Where(option =>
                option.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            );
        VisibleFields.Clear();
        foreach (var field in fields)
            VisibleFields.Add(field);
    }
}

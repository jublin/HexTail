using System.Collections.ObjectModel;
using System.Reactive;
using Avalonia.Threading;
using HexTail.Elastic;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

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
    private int _metadataVersion;
    private bool _isLoading;
    private bool _isSelectionResolved;

    public ElasticViewEditorViewModel(ElasticConnectionEditorViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        AddSource();
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
            var version = ++_metadataVersion;
            IsSelectionResolved = false;
            Error = null;
            if (value is null)
            {
                IsLoading = false;
                return;
            }
            IsLoading = true;
            _ = LoadDataViewAsync(value, version);
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
    public string? Error
    {
        get => _error;
        private set => this.RaiseAndSetIfChanged(ref _error, value);
    }
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }
    public bool IsSelectionResolved
    {
        get => _isSelectionResolved;
        private set => this.RaiseAndSetIfChanged(ref _isSelectionResolved, value);
    }

    internal void Sync(ElasticViewSettings settings)
    {
        ++_metadataVersion;
        IsLoading = false;
        IsSelectionResolved = true;
        Error = null;
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
        if (Sources.Count == 0)
            AddSource();
        this.RaisePropertyChanged(nameof(FilterValue));
    }

    internal ElasticViewSettings ToSettings()
    {
        if (
            !IsSelectionResolved
            || !string.Equals(SelectedDataViewId, DataViewId, StringComparison.Ordinal)
        )
            throw new InvalidOperationException(
                IsLoading
                    ? "Wait for the selected data view to finish loading before saving."
                    : "Load the selected data view successfully before saving."
            );
        return new()
        {
            Id = Id,
            Name = Name,
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
    }

    private async Task LoadDataViewAsync(string id, int version)
    {
        try
        {
            var view = await _owner.GetDataViewAsync(id);
            if (version != _metadataVersion)
                return;
            var selectedOutputFields = Fields
                .Where(field => field.IsOutput)
                .Select(field => field.Name)
                .ToHashSet(StringComparer.Ordinal);
            DataViewId = view.Id;
            DataViewTitle = view.Title;
            TimeFieldName = view.TimeFieldName;
            Fields.Clear();
            _fieldSnapshot.Clear();
            foreach (var field in view.Fields)
                AddField(
                    new ElasticFieldOptionViewModel(field.Name)
                    {
                        IsOutput = selectedOutputFields.Contains(field.Name),
                    }
                );
            this.RaisePropertyChanged(nameof(FieldNames));
            RefreshVisibleFields();
            IsSelectionResolved = true;
        }
        catch (Exception exception)
        {
            if (version == _metadataVersion)
                Error = exception.Message;
        }
        finally
        {
            if (version == _metadataVersion)
                IsLoading = false;
        }
    }

    private void AddSource() =>
        Sources.Add(new ElasticSourceSettingViewModel(Guid.NewGuid().ToString("N")));

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

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
    private string? _timeFieldName;
    private string? _serverField;

    public ElasticViewEditorViewModel(ElasticConnectionEditorViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        Sources.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null)
                foreach (ElasticSourceSettingViewModel source in args.OldItems)
                    source.PropertyChanged -= SourceChanged;
            if (args.NewItems is not null)
                foreach (ElasticSourceSettingViewModel source in args.NewItems)
                    source.PropertyChanged += SourceChanged;
            NotifyPrerequisitesChanged();
        };
        AddSource();
        _fieldFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fieldFilterTimer.Tick += (_, _) => ApplyQueuedFieldFilter();
    }

    public string Id { get; }
    public string Name
    {
        get => _name;
        set
        {
            this.RaiseAndSetIfChanged(ref _name, value);
            NotifyPrerequisitesChanged();
        }
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
            this.RaisePropertyChanged(nameof(SelectedDataView));
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
    public ElasticDataViewChoiceViewModel? SelectedDataView
    {
        get => DataViews.FirstOrDefault(choice => choice.Id == SelectedDataViewId);
        set
        {
            // Native selection briefly clears while items are bound or refreshed.
            if (value is not null)
                SelectedDataViewId = value.Id;
        }
    }
    public string? TimeFieldName
    {
        get => _timeFieldName;
        set
        {
            this.RaiseAndSetIfChanged(ref _timeFieldName, value);
            NotifyPrerequisitesChanged();
        }
    }
    public string? ServerField
    {
        get => _serverField;
        set
        {
            this.RaiseAndSetIfChanged(ref _serverField, value);
            NotifyPrerequisitesChanged();
        }
    }
    public string? NamespaceField { get; set; }
    public ObservableCollection<ElasticDataViewChoiceViewModel> DataViews => _owner.DataViews;
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
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            NotifyPrerequisitesChanged();
        }
    }
    public bool IsSelectionResolved
    {
        get => _isSelectionResolved;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isSelectionResolved, value);
            NotifyPrerequisitesChanged();
        }
    }

    public string MetadataStatus =>
        IsLoading ? "Loading data-view metadata…"
        : string.IsNullOrWhiteSpace(SelectedDataViewId)
            ? "Test connection, then choose a data view."
        : !IsSelectionResolved ? "Metadata is unavailable. Test connection to retry."
        : Fields.Count == 0 ? "No fields returned. Check the data view in Kibana."
        : "Data-view metadata loaded.";
    public string TimestampStatus =>
        IsLoading ? "Timestamp field: loading…"
        : !IsSelectionResolved ? "Timestamp field: not loaded"
        : string.IsNullOrWhiteSpace(TimeFieldName) ? "Timestamp field: not detected"
        : $"Timestamp field: {TimeFieldName}";
    public string? NameError => string.IsNullOrWhiteSpace(Name) ? "Enter a view name." : null;
    public string? TimestampError =>
        IsSelectionResolved && string.IsNullOrWhiteSpace(TimeFieldName)
            ? "Choose a data view with a timestamp field configured in Kibana."
            : null;
    public string? FilterFieldError =>
        string.IsNullOrWhiteSpace(ServerField)
            ? "Choose the field used to filter this source."
            : null;
    public string? FilterValueError =>
        Sources.Count == 0 || Sources.Any(source => string.IsNullOrWhiteSpace(source.ServerValue))
            ? "Enter a filter value for every source."
        : Sources
            .Select(source => source.ServerValue.Trim())
            .Distinct(StringComparer.Ordinal)
            .Count() != Sources.Count
            ? "Each source needs a unique filter value."
        : null;
    public string? OutputFieldsError =>
        Fields.Any(option => option.IsOutput) ? null : "Select at least one field to display.";
    public bool CanSave =>
        !IsLoading
        && IsSelectionResolved
        && !string.IsNullOrWhiteSpace(SelectedDataViewId)
        && string.Equals(SelectedDataViewId, DataViewId, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(DataViewTitle)
        && NameError is null
        && TimestampError is null
        && FilterFieldError is null
        && FilterValueError is null
        && OutputFieldsError is null;

    private void SourceChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs args
    ) => NotifyPrerequisitesChanged();

    private void NotifyPrerequisitesChanged()
    {
        foreach (
            var property in new[]
            {
                nameof(MetadataStatus),
                nameof(TimestampStatus),
                nameof(NameError),
                nameof(TimestampError),
                nameof(FilterFieldError),
                nameof(FilterValueError),
                nameof(OutputFieldsError),
                nameof(CanSave),
            }
        )
            this.RaisePropertyChanged(property);
        _owner.NotifySavePrerequisitesChanged();
    }

    internal Task RefreshDataViewAsync(ElasticDataViewSummary? summary)
    {
        if (string.IsNullOrWhiteSpace(SelectedDataViewId))
            return Task.CompletedTask;
        if (summary is null)
        {
            ++_metadataVersion;
            IsLoading = false;
            IsSelectionResolved = false;
            Error = "The selected data view is no longer available. Choose another data view.";
            return Task.CompletedTask;
        }
        if (IsSelectionResolved)
        {
            DataViewTitle = summary.Title;
            this.RaisePropertyChanged(nameof(DataViewTitle));
            NotifyPrerequisitesChanged();
            return Task.CompletedTask;
        }
        Error = null;
        IsLoading = true;
        return LoadDataViewAsync(SelectedDataViewId, ++_metadataVersion);
    }

    internal void NotifyDataViewSelectionChanged()
    {
        this.RaisePropertyChanged(nameof(SelectedDataViewId));
        this.RaisePropertyChanged(nameof(SelectedDataView));
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
        NotifyDataViewSelectionChanged();
        NotifyPrerequisitesChanged();
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
            Error = null;
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
                NotifyPrerequisitesChanged();
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

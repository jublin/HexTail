using System.Collections.ObjectModel;
using System.Collections.Specialized;
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
    private static readonly string[] SuggestionPriority =
    [
        "@timestamp",
        "timestamp",
        "message",
        "log.level",
        "level",
        "service.name",
        "service",
        "host.name",
        "host",
    ];
    private const int MaxSuggestions = 12;
    private int _fieldFilterVersion;
    private int _metadataVersion;
    private int _previewVersion;
    private bool _syncing;
    private string? _previewMessage;
    private bool _isLoading;
    private bool _isSelectionResolved;
    private bool _isMetadataLoaded;
    private string? _timeFieldName;
    private string? _serverField;
    private string? _namespaceField;

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
        SelectedOutputFields.CollectionChanged += OnSelectedOutputFieldsChanged;
        AddSource();
        _fieldFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _fieldFilterTimer.Tick += (_, _) => ApplyQueuedFieldFilter();
        RefreshFieldsCommand = ReactiveCommand.CreateFromTask(RefreshFieldsAsync);
        RefreshCountsCommand = ReactiveCommand.CreateFromTask(RefreshCountsAsync, owner.Scheduler);
        RefreshPreviewCommand = ReactiveCommand.CreateFromTask(
            RefreshPreviewAsync,
            owner.Scheduler
        );
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
            InvalidateResults();
            SetMetadataLoaded(false);
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
            var changed = !string.Equals(_serverField, value, StringComparison.Ordinal);
            this.RaiseAndSetIfChanged(ref _serverField, value);
            NotifyPrerequisitesChanged();
            if (changed && !_syncing)
                _ = StartAllCountsAsync();
        }
    }
    public string? NamespaceField
    {
        get => _namespaceField;
        set
        {
            this.RaiseAndSetIfChanged(ref _namespaceField, value);
            NotifyPrerequisitesChanged();
        }
    }
    internal IReadOnlyList<string> OutputFieldNames => SelectedOutputFields;

    /// <summary>The output columns in display order.</summary>
    public ObservableCollection<string> SelectedOutputFields { get; } = [];
    public ObservableCollection<ElasticDataViewChoiceViewModel> DataViews => _owner.DataViews;
    public ObservableCollection<ElasticFieldOptionViewModel> Fields { get; } = [];
    public ObservableCollection<ElasticSourceSettingViewModel> Sources { get; } = [];
    public IEnumerable<string> FieldNames => Fields.Select(option => option.Name);
    public ObservableCollection<ElasticFieldOptionViewModel> VisibleFields { get; } = [];
    public ReactiveCommand<Unit, Unit> RefreshFieldsCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshCountsCommand { get; }
    public ReactiveCommand<Unit, Unit> RefreshPreviewCommand { get; }
    public ObservableCollection<ElasticPreviewRowViewModel> PreviewRows { get; } = [];
    public string? PreviewMessage
    {
        get => _previewMessage;
        private set => this.RaiseAndSetIfChanged(ref _previewMessage, value);
    }
    public IEnumerable<ElasticFieldOptionViewModel> SearchableFields =>
        Fields.Where(option => option.Searchable != false);
    public IReadOnlyList<ElasticFieldOptionViewModel> SuggestedFields => BuildSuggestions();
    public string SuggestedName
    {
        get
        {
            var title = DataViewTitle ?? string.Empty;
            var first = Sources
                .Select(source => source.ServerValue.Trim())
                .FirstOrDefault(value => value.Length > 0);
            return first is null ? title : $"{title} · {first}";
        }
    }
    public int SelectedFieldCount => SelectedOutputFields.Count;
    public string SelectedFieldsSummary =>
        SelectedFieldCount == 0
            ? "No output fields selected."
            : string.Join(" → ", SelectedOutputFields);
    public string RowPreview => string.Join(' ', SelectedOutputFields.Select(name => $"<{name}>"));
    public string FieldDiscoveryStatus =>
        "Showing up to 50 matching candidates plus selected fields. Search by field name to find more.";
    public string FilterFieldStatus => DescribeField(ServerField);
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

    /// <summary>
    /// True once the data view's fields were fetched for the current selection. A view restored
    /// from saved settings is resolved but holds only its output fields until this is true.
    /// </summary>
    public bool IsMetadataLoaded => _isMetadataLoaded;

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
        : !IsSelectionResolved ? "Metadata could not be loaded. Choose Retry."
        : Fields.Count == 0 ? "No fields returned. Check the data view in Kibana."
        : "Data-view metadata loaded.";
    public string TimestampStatus =>
        IsLoading ? "Timestamp field: loading…"
        : !IsSelectionResolved ? "Timestamp field: not loaded"
        : string.IsNullOrWhiteSpace(TimeFieldName) ? "Timestamp field: not detected"
        : $"Timestamp field: {TimeFieldName} · {DescribeField(TimeFieldName)}";
    public string? NameError => string.IsNullOrWhiteSpace(Name) ? "Enter a view name." : null;
    public string? TimestampError =>
        IsSelectionResolved && string.IsNullOrWhiteSpace(TimeFieldName)
            ? "Choose a data view with a timestamp field configured in Kibana."
            : null;
    public string? FilterFieldError =>
        string.IsNullOrWhiteSpace(ServerField) ? "Choose the field used to filter this source."
        : Fields.FirstOrDefault(option => option.Name == ServerField)?.Searchable == false
            ? "This field is not searchable. Choose a searchable filter field."
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

    private void SourceChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        // Count results never change what can be saved.
        if (
            args.PropertyName
            is nameof(ElasticSourceSettingViewModel.CountText)
                or nameof(ElasticSourceSettingViewModel.IsZeroCount)
        )
            return;
        // A count belongs to the value it was taken for; editing the value makes it stale.
        if (
            args.PropertyName == nameof(ElasticSourceSettingViewModel.ServerValue)
            && sender is ElasticSourceSettingViewModel source
        )
            source.InvalidateCount();
        NotifyPrerequisitesChanged();
    }

    private void OnSelectedOutputFieldsChanged(
        object? sender,
        NotifyCollectionChangedEventArgs args
    )
    {
        NotifyPrerequisitesChanged();
        this.RaisePropertyChanged(nameof(SuggestedFields));
    }

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
                nameof(SelectedFieldCount),
                nameof(SelectedFieldsSummary),
                nameof(RowPreview),
                nameof(FilterFieldStatus),
                nameof(SuggestedName),
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
            SetMetadataLoaded(false);
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

    private Task RefreshFieldsAsync()
    {
        if (IsLoading || string.IsNullOrWhiteSpace(SelectedDataViewId))
            return Task.CompletedTask;
        Error = null;
        SetMetadataLoaded(false);
        IsSelectionResolved = false;
        IsLoading = true;
        return LoadDataViewAsync(SelectedDataViewId, ++_metadataVersion);
    }

    private void SetMetadataLoaded(bool value)
    {
        if (_isMetadataLoaded == value)
            return;
        _isMetadataLoaded = value;
        this.RaisePropertyChanged(nameof(IsMetadataLoaded));
    }

    private string DescribeField(string? name) =>
        Fields.FirstOrDefault(field => field.Name == name)?.MetadataDescription
        ?? "Metadata not loaded";

    private void ClearFields()
    {
        ++_fieldFilterVersion;
        _fieldFilterTimer.Stop();
        Fields.Clear();
        _fieldSnapshot.Clear();
        SelectedOutputFields.Clear();
    }

    internal void NotifyDataViewSelectionChanged()
    {
        this.RaisePropertyChanged(nameof(SelectedDataViewId));
        this.RaisePropertyChanged(nameof(SelectedDataView));
    }

    internal void Sync(ElasticViewSettings settings)
    {
        _syncing = true;
        try
        {
            SyncCore(settings);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncCore(ElasticViewSettings settings)
    {
        ++_metadataVersion;
        InvalidateResults();
        SetMetadataLoaded(false);
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
        ClearFields();
        foreach (var field in settings.OutputFields.Distinct(StringComparer.Ordinal))
            AddField(new ElasticFieldOptionViewModel(field) { IsOutput = true });
        this.RaisePropertyChanged(nameof(FieldNames));
        this.RaisePropertyChanged(nameof(SearchableFields));
        this.RaisePropertyChanged(nameof(SuggestedFields));
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
            OutputFields = SelectedOutputFields.ToList(),
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
            var selectedOutputFields = SelectedOutputFields.ToArray();
            var selectedNames = selectedOutputFields.ToHashSet(StringComparer.Ordinal);
            DataViewId = view.Id;
            DataViewTitle = view.Title;
            TimeFieldName = view.TimeFieldName;
            ClearFields();
            foreach (var field in view.Fields)
                AddField(
                    new ElasticFieldOptionViewModel(field.Name, field.Type, field.Searchable)
                    {
                        IsOutput = selectedNames.Contains(field.Name),
                    }
                );
            var availableNames = Fields
                .Select(field => field.Name)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var name in selectedOutputFields.Where(name => !availableNames.Contains(name)))
                AddField(new ElasticFieldOptionViewModel(name) { IsOutput = true });
            SelectedOutputFields.Clear();
            foreach (var name in selectedOutputFields)
                SelectedOutputFields.Add(name);
            this.RaisePropertyChanged(nameof(FieldNames));
            this.RaisePropertyChanged(nameof(SearchableFields));
            this.RaisePropertyChanged(nameof(SuggestedFields));
            RefreshVisibleFields();
            SetMetadataLoaded(true);
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

    /// <summary>Moves a selected output column; out-of-range or no-op moves are ignored.</summary>
    public void MoveOutputField(int from, int to)
    {
        var count = SelectedOutputFields.Count;
        if (from < 0 || from >= count || to < 0 || to >= count || from == to)
            return;
        SelectedOutputFields.Move(from, to);
    }

    /// <summary>Adds a filter value as a new source and starts counting its documents.</summary>
    public bool AddFilterValue(string value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return false;
        if (
            Sources.Any(source =>
                string.Equals(source.ServerValue.Trim(), trimmed, StringComparison.Ordinal)
            )
        )
            return false;
        ElasticSourceSettingViewModel target;
        if (Sources is [{ } placeholder] && string.IsNullOrWhiteSpace(placeholder.ServerValue))
        {
            // Fill the lone blank placeholder instead of leaving an invalid empty source behind.
            target = placeholder;
            target.ServerValue = trimmed;
        }
        else
        {
            target = new ElasticSourceSettingViewModel(Guid.NewGuid().ToString("N"))
            {
                ServerValue = trimmed,
            };
            Sources.Add(target);
        }
        this.RaisePropertyChanged(nameof(FilterValue));
        _ = StartCountAsync(target);
        return true;
    }

    public void RemoveFilterValue(ElasticSourceSettingViewModel source)
    {
        if (!Sources.Remove(source))
            return;
        source.InvalidateCount();
        if (Sources.Count == 0)
            AddSource();
        this.RaisePropertyChanged(nameof(FilterValue));
    }

    private bool TryGetQueryTarget(out string title, out string timeField)
    {
        title = DataViewTitle ?? string.Empty;
        timeField = TimeFieldName ?? string.Empty;
        return IsSelectionResolved
            && !IsLoading
            && string.Equals(SelectedDataViewId, DataViewId, StringComparison.Ordinal)
            && !string.IsNullOrWhiteSpace(title)
            && !string.IsNullOrWhiteSpace(timeField);
    }

    private void InvalidateResults()
    {
        foreach (var source in Sources)
            source.InvalidateCount();
        ++_previewVersion;
        PreviewRows.Clear();
        PreviewMessage = null;
    }

    private Task StartAllCountsAsync() => Task.WhenAll(Sources.ToArray().Select(StartCountAsync));

    private Task RefreshCountsAsync() => StartAllCountsAsync();

    private Task StartCountAsync(ElasticSourceSettingViewModel source)
    {
        var field = ServerField?.Trim();
        var value = source.ServerValue.Trim();
        if (
            !TryGetQueryTarget(out var title, out var timeField)
            || string.IsNullOrEmpty(field)
            || value.Length == 0
        )
        {
            source.InvalidateCount();
            return Task.CompletedTask;
        }
        var version = source.BeginCount();
        return CountSourceAsync(source, version, title, timeField, field, value);
    }

    private async Task CountSourceAsync(
        ElasticSourceSettingViewModel source,
        int version,
        string title,
        string timeField,
        string field,
        string value
    )
    {
        try
        {
            var count = await _owner.CountAsync(title, timeField, field, value);
            if (Sources.Contains(source))
                source.CompleteCount(version, count);
        }
        catch (OperationCanceledException)
        {
            source.AbandonCount(version);
        }
        catch (Exception)
        {
            source.FailCount(version);
        }
    }

    private async Task RefreshPreviewAsync()
    {
        var version = ++_previewVersion;
        if (!TryGetQueryTarget(out var title, out var timeField))
        {
            PreviewRows.Clear();
            PreviewMessage = "Choose a data view to preview.";
            return;
        }
        PreviewMessage = "Loading preview…";
        try
        {
            var filterValue = Sources
                .Select(source => source.ServerValue.Trim())
                .FirstOrDefault(text => text.Length > 0);
            var filterField = string.IsNullOrWhiteSpace(ServerField) ? null : ServerField.Trim();
            var useFilter = filterField is not null && filterValue is not null;
            var hits = await _owner.PreviewAsync(
                title,
                timeField,
                useFilter ? filterField : null,
                useFilter ? filterValue : null,
                SelectedOutputFields.ToList()
            );
            var rows = hits.Select(hit => new ElasticPreviewRowViewModel(hit)).ToList();
            if (version != _previewVersion)
                return;
            PreviewRows.Clear();
            foreach (var row in rows)
                PreviewRows.Add(row);
            PreviewMessage = rows.Count == 0 ? "No documents in the last 15 minutes" : null;
        }
        catch (OperationCanceledException)
        {
            if (version == _previewVersion)
                PreviewMessage = null;
        }
        catch (Exception exception)
        {
            if (version != _previewVersion)
                return;
            PreviewRows.Clear();
            PreviewMessage = string.IsNullOrWhiteSpace(exception.Message)
                ? "Preview failed."
                : exception.Message;
        }
    }

    private static readonly Dictionary<string, int> SuggestionRank = SuggestionPriority
        .Select((name, index) => (name, index))
        .ToDictionary(item => item.name, item => item.index, StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<ElasticFieldOptionViewModel> BuildSuggestions() =>
        Fields
            .Where(field => !field.IsOutput)
            .OrderBy(field => SuggestionRank.GetValueOrDefault(field.Name, int.MaxValue))
            .ThenBy(field => field.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxSuggestions)
            .ToArray();

    private void AddField(ElasticFieldOptionViewModel field)
    {
        _fieldSnapshot.Add(field);
        if (field.IsOutput)
            SelectedOutputFields.Add(field.Name);
        field.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ElasticFieldOptionViewModel.IsOutput))
            {
                if (!Fields.Contains(field))
                    return;
                if (field.IsOutput)
                    SelectedOutputFields.Add(field.Name);
                else
                    SelectedOutputFields.Remove(field.Name);
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
    )
    {
        var matching = fields
            .Where(item =>
                string.IsNullOrWhiteSpace(query)
                || item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            )
            .ToArray();
        var candidates = matching
            .Where(item => !item.IsOutput)
            .Take(50)
            .Select(item => item.Field)
            .ToHashSet();
        return matching
            .Where(item => item.IsOutput || candidates.Contains(item.Field))
            .Select(item => item.Field)
            .ToArray();
    }

    private void RefreshVisibleFields()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshVisibleFields);
            return;
        }
        var query = OutputFieldQuery.Trim();
        var fields = FilterFields(
            Fields.Select(field => (field, field.Name, field.IsOutput)).ToArray(),
            query
        );
        VisibleFields.Clear();
        foreach (var field in fields)
            VisibleFields.Add(field);
    }
}

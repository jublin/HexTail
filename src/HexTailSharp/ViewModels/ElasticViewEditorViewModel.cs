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
    private bool _isTesting;
    private string? _selectedDataViewId;
    private string? _error;
    private string? _status;
    private string _name = string.Empty;
    private string _outputFieldQuery = string.Empty;
    private CancellationTokenSource? _fieldFilterCancellation;

    public ElasticViewEditorViewModel(ElasticConnectionEditorViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
        TestConnectionCommand = ReactiveCommand.CreateFromTask(TestConnectionAsync);
        AddSourceCommand = ReactiveCommand.Create(AddSource);
        RemoveSourceCommand = ReactiveCommand.Create<ElasticSourceSettingViewModel>(RemoveSource);
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
    public ObservableCollection<ElasticDataViewSummary> DataViews { get; } = [];
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
    public ReactiveCommand<Unit, Unit> TestConnectionCommand { get; }
    public ReactiveCommand<Unit, Unit> AddSourceCommand { get; }
    public ReactiveCommand<ElasticSourceSettingViewModel, Unit> RemoveSourceCommand { get; }
    public string? Error
    {
        get => _error;
        private set => this.RaiseAndSetIfChanged(ref _error, value);
    }
    public string? Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }
    public bool IsTesting
    {
        get => _isTesting;
        private set => this.RaiseAndSetIfChanged(ref _isTesting, value);
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
        DataViews.Clear();
        if (
            !string.IsNullOrWhiteSpace(settings.DataViewId)
            && !string.IsNullOrWhiteSpace(settings.DataViewTitle)
        )
            DataViews.Add(new ElasticDataViewSummary(settings.DataViewId, settings.DataViewTitle));
        Fields.Clear();
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

    private async Task TestConnectionAsync()
    {
        IsTesting = true;
        Error = null;
        Status = "Checking…";
        try
        {
            var views = await _owner.GetDataViewsAsync();
            DataViews.Clear();
            foreach (var view in views)
                DataViews.Add(view);
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

    private async Task LoadDataViewAsync(string id)
    {
        try
        {
            var view = await _owner.GetDataViewAsync(id);
            DataViewId = view.Id;
            DataViewTitle = view.Title;
            TimeFieldName = view.TimeFieldName;
            Fields.Clear();
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
        field.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ElasticFieldOptionViewModel.IsOutput))
                RefreshVisibleFields();
        };
        Fields.Add(field);
    }

    private void QueueFieldFilter()
    {
        _fieldFilterCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _fieldFilterCancellation = cancellation;
        _ = ApplyFieldFilterAsync(cancellation);
    }

    private async Task ApplyFieldFilterAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(150, cancellation.Token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() =>
            {
                if (!cancellation.IsCancellationRequested)
                    RefreshVisibleFields();
            });
        }
        catch (OperationCanceledException) { }
    }

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

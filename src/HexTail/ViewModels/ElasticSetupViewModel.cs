using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia.Threading;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>
/// The stepped Elastic setup flow. Each step saves what it can as soon as it is completed, so
/// closing the flow never loses a working connection; only the unfinished view is dropped.
/// </summary>
internal sealed class ElasticSetupViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private readonly BehaviorSubject<bool> _canNext = new(false);
    private readonly BehaviorSubject<bool> _canBack = new(false);
    private readonly BehaviorSubject<bool> _canGoTo = new(false);
    private readonly BehaviorSubject<bool> _canClose = new(true);
    private bool _isOpen;
    private ElasticSetupStep _step;
    private int _furthest;
    private ElasticConnectionEditorViewModel? _connection;
    private ElasticViewEditorViewModel? _view;
    private bool _createdConnection;
    private bool _openNow = true;
    private string? _error;
    private bool _isBusy;
    private string _dataViewSearch = string.Empty;
    private string _newFilterValue = string.Empty;
    private string? _filterMessage;
    private string? _selectedColumn;
    private bool _advancedOpen;
    private readonly BehaviorSubject<bool> _canMoveUp = new(false);
    private readonly BehaviorSubject<bool> _canMoveDown = new(false);

    // The automatic connection test runs once the edits have stopped coming in. A DispatcherTimer
    // rather than Rx's Throttle on the owner scheduler: tests hand the owner an immediate
    // scheduler, on which a delayed Throttle would sleep inside every property setter.
    private DispatcherTimer? _autoTestTimer;
    private int _editStamp;
    private int _testStartedStamp;
    private bool _retestPending;

    // Bumped whenever a flow starts or ends, so work still running for an old flow stops quietly.
    private int _session;

    internal ElasticSetupViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        Steps =
        [
            new(ElasticSetupStep.Connection, "Connection"),
            new(ElasticSetupStep.DataView, "Data view"),
            new(ElasticSetupStep.Filter, "Filter"),
            new(ElasticSetupStep.Columns, "Columns"),
            new(ElasticSetupStep.Finish, "Name & open"),
        ];
        NextCommand = ReactiveCommand.CreateFromTask(NextAsync, _canNext, owner.Scheduler);
        BackCommand = ReactiveCommand.CreateFromTask(BackAsync, _canBack, owner.Scheduler);
        CloseCommand = ReactiveCommand.CreateFromTask(
            () =>
            {
                // Closing mid-save would leave the editor out of step with what was saved.
                if (!IsBusy)
                    Close();
                return Task.CompletedTask;
            },
            _canClose,
            owner.Scheduler
        );
        GoToStepCommand = ReactiveCommand.CreateFromTask<ElasticSetupStepViewModel>(
            GoToStepAsync,
            _canGoTo,
            owner.Scheduler
        );
        AddFilterValueCommand = ReactiveCommand.CreateFromTask(
            () =>
            {
                Run(() => AddFilterText(NewFilterValue, keepRemainder: false));
                return Task.CompletedTask;
            },
            null,
            owner.Scheduler
        );
        RemoveFilterValueCommand = ReactiveCommand.CreateFromTask<ElasticSourceSettingViewModel>(
            source =>
            {
                Run(() => View?.RemoveFilterValue(source));
                return Task.CompletedTask;
            },
            null,
            owner.Scheduler
        );
        MoveColumnUpCommand = ReactiveCommand.CreateFromTask(
            () => MoveColumnAsync(-1),
            _canMoveUp,
            owner.Scheduler
        );
        MoveColumnDownCommand = ReactiveCommand.CreateFromTask(
            () => MoveColumnAsync(1),
            _canMoveDown,
            owner.Scheduler
        );
        RemoveColumnCommand = ReactiveCommand.CreateFromTask<string>(
            name =>
            {
                Run(() => RemoveColumn(name));
                return Task.CompletedTask;
            },
            null,
            owner.Scheduler
        );
        Reevaluate();
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public ElasticSetupStep Step => _step;
    public bool IsConnectionStep => _step == ElasticSetupStep.Connection;
    public bool IsDataViewStep => _step == ElasticSetupStep.DataView;
    public bool IsFilterStep => _step == ElasticSetupStep.Filter;
    public bool IsColumnsStep => _step == ElasticSetupStep.Columns;
    public bool IsFinishStep => _step == ElasticSetupStep.Finish;
    public IReadOnlyList<ElasticSetupStepViewModel> Steps { get; }

    public ElasticConnectionEditorViewModel? Connection
    {
        get => _connection;
        private set
        {
            if (ReferenceEquals(_connection, value))
                return;
            if (_connection is not null)
            {
                _connection.PropertyChanged -= EditorChanged;
                _connection.ConnectionEdited -= OnConnectionEdited;
                _connection.DataViews.CollectionChanged -= OnDataViewsChanged;
            }
            CancelAutoTest();
            this.RaiseAndSetIfChanged(ref _connection, value);
            if (value is not null)
            {
                value.PropertyChanged += EditorChanged;
                value.ConnectionEdited += OnConnectionEdited;
                value.DataViews.CollectionChanged += OnDataViewsChanged;
            }
            RebuildFilteredDataViews();
        }
    }

    /// <summary>How long the connection fields stay unedited before the test runs by itself.</summary>
    internal TimeSpan AutoTestDelay { get; set; } = TimeSpan.FromMilliseconds(700);

    /// <summary>Filters <see cref="FilteredDataViews"/> by title, ignoring case.</summary>
    public string DataViewSearch
    {
        get => _dataViewSearch;
        set
        {
            value ??= string.Empty;
            if (_dataViewSearch == value)
                return;
            this.RaiseAndSetIfChanged(ref _dataViewSearch, value);
            RebuildFilteredDataViews();
        }
    }

    public ObservableCollection<ElasticDataViewChoiceViewModel> FilteredDataViews { get; } = [];

    public ElasticViewEditorViewModel? View
    {
        get => _view;
        private set
        {
            if (ReferenceEquals(_view, value))
                return;
            if (_view is not null)
            {
                _view.PropertyChanged -= EditorChanged;
                _view.PreviewInputsChanged -= OnPreviewInputsChanged;
                _view.SelectedOutputFields.CollectionChanged -= OnSelectedColumnsChanged;
            }
            this.RaiseAndSetIfChanged(ref _view, value);
            if (value is not null)
            {
                value.PropertyChanged += EditorChanged;
                value.PreviewInputsChanged += OnPreviewInputsChanged;
                value.SelectedOutputFields.CollectionChanged += OnSelectedColumnsChanged;
            }
            _selectedColumn = null;
            _newFilterValue = string.Empty;
            _filterMessage = null;
            this.RaisePropertyChanged(nameof(SelectedColumn));
            this.RaisePropertyChanged(nameof(NewFilterValue));
            this.RaisePropertyChanged(nameof(FilterMessage));
            this.RaisePropertyChanged(nameof(SelectedSearchableField));
            this.RaisePropertyChanged(nameof(Summary));
            UpdateColumnMoves();
        }
    }

    /// <summary>
    /// What the filter box holds. A comma ends a value: everything before the last comma is added
    /// as filter values and the text after it stays to be finished.
    /// </summary>
    public string NewFilterValue
    {
        get => _newFilterValue;
        set
        {
            var raw = value ?? string.Empty;
            var kept = raw;
            var comma = raw.LastIndexOf(',');
            if (comma >= 0)
            {
                // The setter is also reached from a text box; a failure must not escape into it.
                Run(() => AddFilterText(raw[..comma], keepRemainder: true));
                kept = raw[(comma + 1)..].TrimStart();
            }
            else if (!string.Equals(raw, _newFilterValue, StringComparison.Ordinal))
                FilterMessage = null;
            var changed = !string.Equals(_newFilterValue, kept, StringComparison.Ordinal);
            _newFilterValue = kept;
            // The box has to show what was kept even when the stored text did not change (the page
            // also corrects the box itself, because a binding does not repeat a value it knows).
            if (changed || !string.Equals(raw, kept, StringComparison.Ordinal))
                this.RaisePropertyChanged();
        }
    }

    /// <summary>A short note under the filter box, for instance that a value was already there.</summary>
    public string? FilterMessage
    {
        get => _filterMessage;
        private set => this.RaiseAndSetIfChanged(ref _filterMessage, value);
    }

    /// <summary>The filter field as one of the searchable options; picking one sets the field.</summary>
    public ElasticFieldOptionViewModel? SelectedSearchableField
    {
        get =>
            View is { } view
                ? view.SearchableFields.FirstOrDefault(option =>
                    string.Equals(option.Name, view.ServerField, StringComparison.Ordinal)
                )
                : null;
        set
        {
            // A combo box clears its selection while its items are rebuilt; that is not a choice.
            if (value is null || View is not { } view)
                return;
            view.ServerField = value.Name;
        }
    }

    /// <summary>Whether the Advanced section (the saved namespace metadata) is expanded.</summary>
    public bool AdvancedOpen
    {
        get => _advancedOpen;
        set => this.RaiseAndSetIfChanged(ref _advancedOpen, value);
    }

    /// <summary>The column the Up/Down buttons act on.</summary>
    public string? SelectedColumn
    {
        get => _selectedColumn;
        set
        {
            // Moving a row makes the list let go of it while it rebuilds its rows. The column is
            // still there, so that is not a choice (the page puts the selection back in the list).
            if (
                value is null
                && _selectedColumn is { } current
                && View?.SelectedOutputFields.Contains(current) == true
            )
                return;
            if (string.Equals(_selectedColumn, value, StringComparison.Ordinal))
                return;
            this.RaiseAndSetIfChanged(ref _selectedColumn, value);
            UpdateColumnMoves();
        }
    }

    /// <summary>One line on the last step: where the logs come from and what the rows show.</summary>
    public string Summary
    {
        get
        {
            if (View is not { } view)
                return string.Empty;
            var count = view.Sources.Count(source =>
                !string.IsNullOrWhiteSpace(source.ServerValue)
            );
            var parts = new List<string>();
            if (
                Connection?.Name is { } connectionName
                && !string.IsNullOrWhiteSpace(connectionName)
            )
                parts.Add(connectionName);
            if (!string.IsNullOrWhiteSpace(view.DataViewTitle))
                parts.Add(view.DataViewTitle);
            parts.Add(count == 1 ? "1 source" : $"{count} sources");
            parts.Add($"columns: {string.Join(" → ", view.SelectedOutputFields)}");
            return string.Join(" · ", parts);
        }
    }

    public ReactiveCommand<Unit, Unit> AddFilterValueCommand { get; }
    public ReactiveCommand<ElasticSourceSettingViewModel, Unit> RemoveFilterValueCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveColumnUpCommand { get; }
    public ReactiveCommand<Unit, Unit> MoveColumnDownCommand { get; }
    public ReactiveCommand<string, Unit> RemoveColumnCommand { get; }

    public bool CanGoBack => IsOpen && Step > ElasticSetupStep.Connection;

    public bool CanGoNext =>
        IsOpen
        && Step switch
        {
            ElasticSetupStep.Connection => Connection
                is { EndpointsOk: true, IsTesting: false, NameError: null },
            ElasticSetupStep.DataView => View
                is { IsSelectionResolved: true, IsLoading: false, SelectedDataViewId: { } id }
                && !string.IsNullOrWhiteSpace(id),
            ElasticSetupStep.Filter => View is { FilterFieldError: null, FilterValueError: null },
            ElasticSetupStep.Columns => View is { OutputFieldsError: null },
            ElasticSetupStep.Finish => View is { CanSave: true }
                && Connection is { CanSaveConnectionOnly: true },
            _ => false,
        };

    public string NextLabel =>
        Step != ElasticSetupStep.Finish ? "Next"
        : OpenNow ? "Save & open"
        : "Save";

    public bool OpenNow
    {
        get => _openNow;
        set
        {
            // RaiseAndSetIfChanged returns the new value, not whether it changed.
            if (_openNow == value)
                return;
            this.RaiseAndSetIfChanged(ref _openNow, value);
            Reevaluate();
        }
    }

    public string? Error
    {
        get => _error;
        private set => this.RaiseAndSetIfChanged(ref _error, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (_isBusy == value)
                return;
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            Reevaluate();
        }
    }

    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<ElasticSetupStepViewModel, Unit> GoToStepCommand { get; }

    /// <summary>
    /// Runs a flow entry point from a command or the palette so that a failure lands in
    /// <see cref="Error"/> instead of escaping into the command's unobserved exception stream.
    /// </summary>
    internal void Run(Action start)
    {
        try
        {
            start();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
    }

    /// <summary>Starts a brand new connection at the first step.</summary>
    internal void StartNew()
    {
        Begin();
        Connection = _owner.Settings.AddElasticConnection();
        _createdConnection = true;
        Enter(ElasticSetupStep.Connection);
    }

    /// <summary>Re-checks and re-saves a saved connection; the test has to pass again before Next.</summary>
    internal void EditConnection(string connectionId)
    {
        if (_owner.Settings.EditorFor(connectionId) is not { } editor)
            return;
        Begin();
        Connection = editor;
        Enter(ElasticSetupStep.Connection);
    }

    /// <summary>Opens a saved view for editing, starting at the filter.</summary>
    internal void EditView(string connectionId, string viewId)
    {
        if (
            _owner.Settings.EditorFor(connectionId) is not { } editor
            || editor.Views.FirstOrDefault(view => view.Id == viewId) is not { } view
        )
            return;
        Begin();
        Connection = editor;
        View = view;
        Enter(ElasticSetupStep.Filter);
    }

    /// <summary>Adds another view to a saved connection, starting at the data view.</summary>
    internal void AddSource(string connectionId)
    {
        if (_owner.Settings.EditorFor(connectionId) is not { } editor)
            return;
        Begin();
        Connection = editor;
        View = editor.AddNewView(string.Empty);
        Enter(ElasticSetupStep.DataView);
    }

    /// <summary>
    /// Ends the flow. What is saved stays saved; a connection that was never saved, any view that
    /// is not in the saved settings and any other unsaved edit are dropped. Nothing asks for
    /// confirmation. The close command refuses to run while a save is pending; this method itself
    /// is also used by the finish step and by starting a new flow.
    /// </summary>
    internal void Close()
    {
        _session++;
        CancelAutoTest();
        if (Connection is { } connection)
        {
            var saved = _owner
                .Settings.SavedViews(connection.Id)
                .Select(view => view.Id)
                .ToHashSet();
            foreach (var view in connection.Views.Where(view => !saved.Contains(view.Id)).ToArray())
                connection.Views.Remove(view);
            if (_createdConnection)
                _owner.Settings.DiscardUnsavedElasticConnection(connection);
            // Whatever was changed on a saved connection (fields, views, a typed secret) and not
            // saved is abandoned with the flow.
            if (connection.IsSaved && connection.IsDirty)
                _owner.Settings.RevertElasticConnection(connection);
        }
        _createdConnection = false;
        Connection = null;
        View = null;
        Error = null;
        _isBusy = false;
        this.RaisePropertyChanged(nameof(IsBusy));
        IsOpen = false;
        Reevaluate();
    }

    private void Begin()
    {
        // A flow that is still open is finished first so nothing of it leaks into the new one.
        if (IsOpen)
            Close();
        _session++;
        // Nothing of an earlier flow's editing or testing may carry over into this one.
        CancelAutoTest();
        _editStamp = 0;
        _testStartedStamp = 0;
        _createdConnection = false;
        DataViewSearch = string.Empty;
        AdvancedOpen = false;
        OpenNow = true;
        Error = null;
        IsOpen = true;
    }

    private void Enter(ElasticSetupStep step)
    {
        _furthest = (int)step;
        SetStep(step);
    }

    private void SetStep(ElasticSetupStep step)
    {
        _step = step;
        _furthest = Math.Max(_furthest, (int)step);
        if (
            step == ElasticSetupStep.Finish
            && View is { } view
            && string.IsNullOrWhiteSpace(view.Name)
        )
            view.Name = view.SuggestedName;
        this.RaisePropertyChanged(nameof(Step));
        this.RaisePropertyChanged(nameof(IsConnectionStep));
        this.RaisePropertyChanged(nameof(IsDataViewStep));
        this.RaisePropertyChanged(nameof(IsFilterStep));
        this.RaisePropertyChanged(nameof(IsColumnsStep));
        this.RaisePropertyChanged(nameof(IsFinishStep));
        this.RaisePropertyChanged(nameof(Summary));
        Reevaluate();
        if (step != ElasticSetupStep.Connection)
            EnsureMetadataLoaded();
        if (step == ElasticSetupStep.Filter && View is { } filterView)
            // Saved namespace metadata has no other home, so it must not hide behind a closed section.
            AdvancedOpen =
                !string.IsNullOrWhiteSpace(filterView.NamespaceField)
                || filterView.Sources.Any(source =>
                    !string.IsNullOrWhiteSpace(source.NamespaceValue)
                );
        RefreshCurrentStep();
    }

    /// <summary>
    /// The filter step shows a count per value and the columns step a preview, and both go stale
    /// whenever the step is left, so entering one fetches them again. A view that is still waiting
    /// for its metadata does it when the load ends instead.
    /// </summary>
    private void RefreshCurrentStep()
    {
        if (
            !IsOpen
            || View
                is not { IsLoading: false, IsMetadataLoaded: true, IsSelectionResolved: true } view
        )
            return;
        if (Step == ElasticSetupStep.Filter)
        {
            if (view.Sources.Any(source => !string.IsNullOrWhiteSpace(source.ServerValue)))
                view.RefreshCountsCommand.Execute().Subscribe(_ => { }, ReportBackgroundFailure);
        }
        else if (Step == ElasticSetupStep.Columns)
            view.RefreshPreviewCommand.Execute().Subscribe(_ => { }, ReportBackgroundFailure);
    }

    private void OnPreviewInputsChanged(object? sender, EventArgs args)
    {
        if (
            IsOpen
            && Step == ElasticSetupStep.Columns
            && View is { IsLoading: false } view
            && ReferenceEquals(sender, view)
        )
            view.RefreshPreviewCommand.Execute().Subscribe(_ => { }, ReportBackgroundFailure);
    }

    private void OnSelectedColumnsChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (
            _selectedColumn is not null
            && View?.SelectedOutputFields.Contains(_selectedColumn) != true
        )
        {
            _selectedColumn = null;
            this.RaisePropertyChanged(nameof(SelectedColumn));
        }
        UpdateColumnMoves();
        this.RaisePropertyChanged(nameof(Summary));
    }

    private void UpdateColumnMoves()
    {
        var index =
            _selectedColumn is null || View is null
                ? -1
                : View.SelectedOutputFields.IndexOf(_selectedColumn);
        var count = View?.SelectedOutputFields.Count ?? 0;
        _canMoveUp.OnNext(index > 0);
        _canMoveDown.OnNext(index >= 0 && index < count - 1);
    }

    private Task MoveColumnAsync(int delta)
    {
        try
        {
            if (View is { } view && _selectedColumn is { } column)
            {
                var index = view.SelectedOutputFields.IndexOf(column);
                view.MoveOutputField(index, index + delta);
                this.RaisePropertyChanged(nameof(SelectedColumn));
                UpdateColumnMoves();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        return Task.CompletedTask;
    }

    private void RemoveColumn(string name)
    {
        if (View is not { } view)
            return;
        // Unticking the field keeps the available list and the selection in step.
        if (view.Fields.FirstOrDefault(field => field.Name == name) is { IsOutput: true } field)
            field.IsOutput = false;
        else
            view.SelectedOutputFields.Remove(name);
    }

    private void AddFilterText(string text, bool keepRemainder)
    {
        if (View is not { } view)
            return;
        var duplicates = new List<string>();
        foreach (
            var value in text.Split(',').Select(part => part.Trim()).Where(part => part.Length > 0)
        )
            if (!view.AddFilterValue(value))
                duplicates.Add(value);
        FilterMessage = duplicates.Count switch
        {
            0 => null,
            1 => $"{duplicates[0]} is already in the list.",
            _ => $"{string.Join(", ", duplicates)} are already in the list.",
        };
        if (keepRemainder)
            return;
        _newFilterValue = string.Empty;
        this.RaisePropertyChanged(nameof(NewFilterValue));
    }

    /// <summary>
    /// A view that picked a data view but has no usable metadata yet (a saved view only holds its
    /// output fields, a failed load left nothing) fetches it as soon as a step needs it.
    /// </summary>
    private void EnsureMetadataLoaded()
    {
        if (
            View is not { IsLoading: false, IsMetadataLoaded: false } view
            || string.IsNullOrWhiteSpace(view.SelectedDataViewId)
        )
            return;
        view.RefreshFieldsCommand.Execute().Subscribe(_ => { }, ReportBackgroundFailure);
    }

    private void ReportBackgroundFailure(Exception exception)
    {
        if (exception is not OperationCanceledException)
            Error = exception.Message;
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (
            ReferenceEquals(sender, Connection)
            && args.PropertyName == nameof(ElasticConnectionEditorViewModel.IsTesting)
        )
        {
            var connection = Connection!;
            if (connection.IsTesting)
                _testStartedStamp = _editStamp;
            else
            {
                FillBlankName(connection);
                if (_retestPending)
                {
                    // The flow may be replaced before the posted job runs, so it checks again.
                    var session = _session;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (session == _session && _retestPending)
                            TryRunAutoTest();
                    });
                }
            }
        }
        else if (
            ReferenceEquals(sender, Connection)
            && args.PropertyName == nameof(ElasticConnectionEditorViewModel.Status)
        )
            // Testing refreshes the titles of the data views in place.
            RebuildFilteredDataViews();
        else if (ReferenceEquals(sender, View))
            OnViewEdited(args.PropertyName);
        Reevaluate();
    }

    private void OnViewEdited(string? property)
    {
        switch (property)
        {
            case nameof(ElasticViewEditorViewModel.ServerField):
                this.RaisePropertyChanged(nameof(SelectedSearchableField));
                break;
            case nameof(ElasticViewEditorViewModel.SearchableFields):
                this.RaisePropertyChanged(nameof(SelectedSearchableField));
                break;
            case nameof(ElasticViewEditorViewModel.IsLoading) when View is { IsLoading: false }:
                // The data view finished (re)loading, which dropped what was fetched before it.
                RefreshCurrentStep();
                break;
            case nameof(ElasticViewEditorViewModel.SelectedFieldsSummary)
            or nameof(ElasticViewEditorViewModel.SuggestedName)
            or nameof(ElasticViewEditorViewModel.DataViewTitle):
                this.RaisePropertyChanged(nameof(Summary));
                break;
        }
    }

    private void OnConnectionEdited(object? sender, EventArgs args)
    {
        _editStamp++;
        if (!IsOpen || Step != ElasticSetupStep.Connection)
            return;
        // Every edit restarts the wait, so a burst of typing ends in a single test.
        _autoTestTimer ??= CreateAutoTestTimer();
        _autoTestTimer.Stop();
        _autoTestTimer.Interval = AutoTestDelay;
        _autoTestTimer.Start();
    }

    private DispatcherTimer CreateAutoTestTimer()
    {
        var timer = new DispatcherTimer();
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            TryRunAutoTest();
        };
        return timer;
    }

    /// <summary>
    /// Stops a pending automatic test and forgets a queued retest; also used when the window closes
    /// so no timer outlives its owner.
    /// </summary>
    internal void CancelAutoTest()
    {
        _retestPending = false;
        _autoTestTimer?.Stop();
    }

    private void TryRunAutoTest()
    {
        if (
            !IsOpen
            || Step != ElasticSetupStep.Connection
            || Connection is not { } connection
            // Something (the button, an earlier edit's timer) already tested the latest edit.
            || _testStartedStamp == _editStamp
            || !connection.CanTestConnection
        )
        {
            _retestPending = false;
            return;
        }
        if (connection.IsTesting)
        {
            // The running test belongs to older edits; it retests when it is done.
            _retestPending = true;
            return;
        }
        _retestPending = false;
        connection.TestConnectionCommand.Execute().Subscribe(_ => { }, ReportBackgroundFailure);
    }

    // A test that passed names a still-unnamed connection after the server it reached, so the
    // person only has to type a name when they want a different one.
    private static void FillBlankName(ElasticConnectionEditorViewModel connection)
    {
        if (!connection.EndpointsOk || !string.IsNullOrWhiteSpace(connection.Name))
            return;
        var cloudId = connection.CloudId.Trim();
        var colon = cloudId.LastIndexOf(':');
        var cloudName =
            colon > 0 && connection.CloudIdError is null ? cloudId[..colon].Trim() : string.Empty;
        var host = HostOf(connection.ElasticsearchUrl) ?? HostOf(connection.KibanaUrl);
        var name = cloudName.Length > 0 ? cloudName : host;
        if (!string.IsNullOrWhiteSpace(name))
            connection.Name = name;
    }

    private static string? HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host)
            ? uri.Host
            : null;

    private void OnDataViewsChanged(object? sender, NotifyCollectionChangedEventArgs args) =>
        RebuildFilteredDataViews();

    private void RebuildFilteredDataViews()
    {
        var term = _dataViewSearch.Trim();
        var wanted = (Connection?.DataViews ?? [])
            .Where(choice =>
                term.Length == 0 || choice.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
            )
            .ToList();
        var changed = false;
        for (var index = FilteredDataViews.Count - 1; index >= 0; index--)
            if (!wanted.Contains(FilteredDataViews[index]))
            {
                FilteredDataViews.RemoveAt(index);
                changed = true;
            }
        for (var index = 0; index < wanted.Count; index++)
        {
            if (
                index < FilteredDataViews.Count
                && ReferenceEquals(FilteredDataViews[index], wanted[index])
            )
                continue;
            var current = FilteredDataViews.IndexOf(wanted[index]);
            if (current >= 0)
                FilteredDataViews.Move(current, index);
            else
                FilteredDataViews.Insert(index, wanted[index]);
            changed = true;
        }
        // The list control clears its selection while items come and go; say again what is selected.
        if (changed)
            View?.NotifyDataViewSelectionChanged();
    }

    private void Reevaluate()
    {
        this.RaisePropertyChanged(nameof(CanGoBack));
        this.RaisePropertyChanged(nameof(CanGoNext));
        this.RaisePropertyChanged(nameof(NextLabel));
        var canNext = CanGoNext;
        for (var index = 0; index < Steps.Count; index++)
            Steps[index]
                .Update(
                    isCurrent: IsOpen && index == (int)Step,
                    isReachable: IsOpen
                        && (index <= _furthest || (index == (int)Step + 1 && canNext)),
                    isDone: IsOpen && index < (int)Step
                );
        _canNext.OnNext(canNext && !IsBusy);
        _canBack.OnNext(CanGoBack && !IsBusy);
        _canGoTo.OnNext(IsOpen && !IsBusy);
        _canClose.OnNext(!IsBusy);
    }

    private async Task NextAsync()
    {
        // The command's canExecute is advisory for direct Execute() calls, so guard here too.
        if (!IsOpen || IsBusy || !CanGoNext)
            return;
        var session = _session;
        IsBusy = true;
        Error = null;
        try
        {
            await AdvanceAsync(session);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (session == _session)
                Error = ex.Message;
        }
        finally
        {
            if (session == _session)
                IsBusy = false;
        }
    }

    private async Task AdvanceAsync(int session)
    {
        var connection = Connection!;
        switch (Step)
        {
            case ElasticSetupStep.Connection:
                if (!await connection.SaveConnectionOnlyAsync())
                {
                    if (session == _session)
                        Error = connection.Error ?? "Could not save the connection.";
                    return;
                }
                if (session != _session)
                    return;
                View ??= connection.Views.FirstOrDefault() ?? connection.AddNewView(string.Empty);
                SetStep(ElasticSetupStep.DataView);
                break;
            case ElasticSetupStep.Finish:
                var view = View!;
                if (!await connection.SaveViewAsync(view))
                {
                    if (session == _session)
                        Error = connection.Error ?? "Fix the highlighted fields before saving.";
                    return;
                }
                if (session != _session)
                    return;
                if (OpenNow)
                    await OpenSourcesAsync(connection.Id, view.Id, session);
                if (session == _session)
                    Close();
                break;
            default:
                SetStep(Step + 1);
                break;
        }
    }

    private async Task OpenSourcesAsync(string connectionId, string viewId, int session)
    {
        var sourceIds = _owner
            .State.Settings.ElasticConnections.FirstOrDefault(item => item.Id == connectionId)
            ?.Views.FirstOrDefault(item => item.Id == viewId)
            ?.Sources.Select(source => source.Id)
            .ToArray();
        if (sourceIds is null)
            return;
        for (var index = 0; index < sourceIds.Length && session == _session; index++)
            await _owner.State.OpenElasticSourceAsync(
                sourceIds[index],
                save: index == sourceIds.Length - 1
            );
    }

    private Task BackAsync()
    {
        if (CanGoBack && !IsBusy)
        {
            Error = null;
            SetStep(Step - 1);
        }
        return Task.CompletedTask;
    }

    private async Task GoToStepAsync(ElasticSetupStepViewModel target)
    {
        if (!IsOpen || IsBusy || !target.IsReachable || target.Step == Step)
            return;
        Error = null;
        if (target.Step < Step)
        {
            SetStep(target.Step);
            return;
        }
        // Moving forward goes through each step's own gating (and autosave), one step at a time.
        var session = _session;
        while (session == _session && Step < target.Step && CanGoNext && Error is null)
            await NextAsync();
    }
}

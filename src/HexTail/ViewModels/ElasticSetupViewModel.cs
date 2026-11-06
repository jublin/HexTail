using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
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
        Reevaluate();
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public ElasticSetupStep Step => _step;
    public IReadOnlyList<ElasticSetupStepViewModel> Steps { get; }

    public ElasticConnectionEditorViewModel? Connection
    {
        get => _connection;
        private set
        {
            if (ReferenceEquals(_connection, value))
                return;
            if (_connection is not null)
                _connection.PropertyChanged -= EditorChanged;
            this.RaiseAndSetIfChanged(ref _connection, value);
            if (value is not null)
                value.PropertyChanged += EditorChanged;
        }
    }

    public ElasticViewEditorViewModel? View
    {
        get => _view;
        private set
        {
            if (ReferenceEquals(_view, value))
                return;
            if (_view is not null)
                _view.PropertyChanged -= EditorChanged;
            this.RaiseAndSetIfChanged(ref _view, value);
            if (value is not null)
                value.PropertyChanged += EditorChanged;
        }
    }

    public bool CanGoBack => IsOpen && Step > ElasticSetupStep.Connection;

    public bool CanGoNext =>
        IsOpen
        && Step switch
        {
            ElasticSetupStep.Connection => Connection is { EndpointsOk: true, IsTesting: false },
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
        _createdConnection = false;
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
        Reevaluate();
    }

    private void EditorChanged(object? sender, PropertyChangedEventArgs args) => Reevaluate();

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

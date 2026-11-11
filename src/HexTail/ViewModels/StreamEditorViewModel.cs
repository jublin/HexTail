using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Reactive;
using System.Reactive.Linq;
using HexTail.Application;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>One source the stream editor can include. A missing one is a saved member that is not open now.</summary>
internal sealed class StreamMemberChoice : ReactiveObject
{
    private bool _isChecked;

    internal StreamMemberChoice(
        string key,
        string displayName,
        FileTabState? tab,
        bool isChecked,
        string? toolTip = null
    )
    {
        Key = key;
        DisplayName = displayName;
        Tab = tab;
        _isChecked = isChecked;
        ToolTip = toolTip ?? displayName;
    }

    /// <summary>The member key (<c>file:…</c> or <c>elastic:…</c>) the definition stores.</summary>
    public string Key { get; }
    public string DisplayName { get; }
    public string ToolTip { get; }

    /// <summary>The open source, or null for a missing or closed member.</summary>
    internal FileTabState? Tab { get; private set; }
    public string? TabId => Tab?.Id;
    public bool IsMissing => Tab is null;

    /// <summary>True for a source that was open when the editor opened (or was listed) and has been closed since.</summary>
    public bool IsClosed { get; private set; }
    public string Label =>
        IsClosed ? $"{DisplayName} (closed)"
        : IsMissing ? $"{DisplayName} (missing)"
        : DisplayName;

    /// <summary>Points the row at the source that is open now, or marks it closed when there is none.</summary>
    internal void Resolve(FileTabState? current)
    {
        var closed = current is null && (Tab is not null || IsClosed);
        if (ReferenceEquals(current, Tab) && closed == IsClosed)
            return;
        Tab = current;
        IsClosed = closed;
        this.RaisePropertyChanged(nameof(Tab));
        this.RaisePropertyChanged(nameof(TabId));
        this.RaisePropertyChanged(nameof(IsMissing));
        this.RaisePropertyChanged(nameof(IsClosed));
        this.RaisePropertyChanged(nameof(Label));
    }

    public bool IsChecked
    {
        get => _isChecked;
        set => this.RaiseAndSetIfChanged(ref _isChecked, value);
    }
}

/// <summary>
/// Creates a stream or edits a saved one: a name and a checkbox per open source. Saving a new stream opens
/// and selects it. Editing keeps members that are currently missing, listed checked so they can be removed.
/// </summary>
internal sealed class StreamEditorViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private readonly StreamSettings? _existing;
    private string _name;
    private string? _error;
    private bool _canSave;
    private bool _isBusy;
    private bool _isConfirmingDelete;

    internal StreamEditorViewModel(MainWindowViewModel owner, StreamSettings? existing)
    {
        _owner = owner;
        _existing = existing;
        _name = existing?.Name ?? NextDefaultName(owner.State.Streams);

        var checkedKeys = existing is null
            ? new HashSet<string>(StreamMember.KeyComparer)
            : new HashSet<string>(existing.Members.Select(m => m.Key), StreamMember.KeyComparer);
        var open = new HashSet<string>(StreamMember.KeyComparer);
        foreach (var tab in owner.Files.Select(file => file.Model))
        {
            if (tab.Source.Kind == LogSourceKind.Stream)
                continue;
            var key = AppState.MemberKey(tab);
            if (!open.Add(key))
                continue;
            Choices.Add(
                new StreamMemberChoice(
                    key,
                    tab.DisplayName,
                    tab,
                    checkedKeys.Contains(key),
                    tab.Source.ToolTip
                )
            );
        }
        if (existing is not null)
            foreach (var member in existing.Members.Where(member => !open.Contains(member.Key)))
                Choices.Add(
                    new StreamMemberChoice(
                        member.Key,
                        owner.DescribeMissingMember(member.Key),
                        null,
                        isChecked: true,
                        member.Key
                    )
                );

        foreach (var choice in Choices)
            choice.PropertyChanged += OnChoiceChanged;
        Recompute();

        var scheduler = owner.Scheduler;
        var canSave = this.WhenAnyValue(
            vm => vm.CanSave,
            vm => vm.IsBusy,
            (can, busy) => can && !busy
        );
        SaveCommand = ReactiveCommand.CreateFromTask(SaveAsync, canSave, scheduler);
        CancelCommand = ReactiveCommand.Create(() => _owner.CloseStreamEditor(this), scheduler);
        DeleteCommand = ReactiveCommand.Create(
            () =>
            {
                IsConfirmingDelete = true;
            },
            this.WhenAnyValue(vm => vm.IsBusy).Select(busy => IsEditMode && !busy),
            scheduler
        );
        CancelDeleteCommand = ReactiveCommand.Create(
            () =>
            {
                IsConfirmingDelete = false;
            },
            outputScheduler: scheduler
        );
        ConfirmDeleteCommand = ReactiveCommand.CreateFromTask(
            DeleteAsync,
            this.WhenAnyValue(vm => vm.IsBusy).Select(busy => IsEditMode && !busy),
            scheduler
        );
        // The bodies catch everything themselves; this is the net for what escapes them.
        SaveCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
        ConfirmDeleteCommand.ThrownExceptions.Subscribe(ex => Error = ex.Message);
    }

    /// <summary>Raised when the editor is asked to open again while it is already open: the view focuses its name box.</summary>
    internal event Action? FocusRequested;

    internal void RequestFocus() => FocusRequested?.Invoke();

    /// <summary>True once the stream this editor was editing has been deleted.</summary>
    internal bool WasDeleted { get; private set; }

    public bool IsEditMode => _existing is not null;
    public string Title => IsEditMode ? "Edit stream" : "New stream";
    public string? StreamId => _existing?.Id;
    public ObservableCollection<StreamMemberChoice> Choices { get; } = [];
    public bool NeedsMoreSources => Choices.Count < 2;

    public ReactiveCommand<Unit, Unit> SaveCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }

    /// <summary>Asks for confirmation (edit mode only); <see cref="ConfirmDeleteCommand"/> does the deleting.</summary>
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> ConfirmDeleteCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelDeleteCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            this.RaiseAndSetIfChanged(ref _name, value ?? string.Empty);
            Recompute();
        }
    }

    public string? Error
    {
        get => _error;
        private set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    /// <summary>A non-blank name and at least two checked sources (missing members count).</summary>
    public bool CanSave
    {
        get => _canSave;
        private set => this.RaiseAndSetIfChanged(ref _canSave, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    public bool IsConfirmingDelete
    {
        get => _isConfirmingDelete;
        private set => this.RaiseAndSetIfChanged(ref _isConfirmingDelete, value);
    }

    private void OnChoiceChanged(object? sender, PropertyChangedEventArgs e) => Recompute();

    private void Recompute() =>
        CanSave = !string.IsNullOrWhiteSpace(_name) && Choices.Count(c => c.IsChecked) >= 2;

    /// <summary>
    /// Escape inside the editor: with the delete confirmation showing it dismisses only that, otherwise it
    /// cancels the editor. Always true: the key is consumed so the window does not also act on it.
    /// </summary>
    internal bool HandleEscape()
    {
        if (IsConfirmingDelete)
            IsConfirmingDelete = false;
        else if (!IsBusy)
            _owner.CloseStreamEditor(this);
        return true;
    }

    /// <summary>
    /// Brings the choices in line with the sources open now: a row whose source was closed is marked closed
    /// (and points at it again if it was reopened), a missing member that was opened becomes a real one, and
    /// newly opened sources are appended unchecked. Checked state is never touched. Returns the closed rows
    /// that are still ticked.
    /// </summary>
    internal IReadOnlyList<StreamMemberChoice> RefreshChoices()
    {
        var open = new Dictionary<string, FileTabState>(StreamMember.KeyComparer);
        foreach (var tab in _owner.State.Files)
            if (tab.Source.Kind != LogSourceKind.Stream)
                open.TryAdd(AppState.MemberKey(tab), tab);

        var known = new HashSet<string>(StreamMember.KeyComparer);
        foreach (var choice in Choices)
        {
            known.Add(choice.Key);
            choice.Resolve(open.GetValueOrDefault(choice.Key));
        }
        foreach (var (key, tab) in open)
        {
            if (known.Contains(key))
                continue;
            var added = new StreamMemberChoice(
                key,
                tab.DisplayName,
                tab,
                isChecked: false,
                tab.Source.ToolTip
            );
            added.PropertyChanged += OnChoiceChanged;
            Choices.Add(added);
        }
        this.RaisePropertyChanged(nameof(NeedsMoreSources));
        Recompute();
        return Choices.Where(choice => choice.IsChecked && choice.IsClosed).ToArray();
    }

    /// <summary>"Stream N" with the lowest N past the saved count that no stream already uses.</summary>
    internal static string NextDefaultName(IReadOnlyList<StreamSettings> streams)
    {
        var taken = new HashSet<string>(
            streams.Select(stream => stream.Name),
            StringComparer.OrdinalIgnoreCase
        );
        for (var number = streams.Count + 1; ; number++)
        {
            var candidate = $"Stream {number}";
            if (!taken.Contains(candidate))
                return candidate;
        }
    }

    private async Task SaveAsync()
    {
        if (!CanSave)
            return;
        IsBusy = true;
        Error = null;
        try
        {
            var closed = RefreshChoices();
            if (closed.Count > 0)
            {
                Error =
                    closed.Count == 1
                        ? $"{closed[0].DisplayName} was closed. Reopen it or untick it."
                        : $"{string.Join(", ", closed.Select(c => c.DisplayName))} were closed. Reopen them or untick them.";
                return;
            }
            var tabs = Choices
                .Where(c => c.IsChecked && c.Tab is not null)
                .Select(c => c.Tab!)
                .ToArray();
            if (_existing is null)
            {
                var made = await _owner.State.CreateStreamAsync(Name, tabs);
                _owner.CloseStreamEditor(this);
                await _owner.OpenStreamAsync(made.Id);
            }
            else
            {
                var keep = Choices
                    .Where(c => c.IsChecked && c.IsMissing)
                    .Select(c => c.Key)
                    .ToArray();
                await _owner.State.UpdateStreamAsync(
                    _existing.Id,
                    Name,
                    tabs,
                    keepMissingKeys: keep
                );
                _owner.CloseStreamEditor(this);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DeleteAsync()
    {
        if (_existing is null)
            return;
        IsBusy = true;
        Error = null;
        try
        {
            await _owner.State.DeleteStreamAsync(_existing.Id);
            WasDeleted = true;
            _owner.CloseStreamEditor(this);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Error = ex.Message;
            IsConfirmingDelete = false;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

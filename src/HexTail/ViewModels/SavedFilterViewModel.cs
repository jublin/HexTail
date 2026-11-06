using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using HexTail.Domain;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>One saved filter in the sidebar: apply merges it into the selected source, delete forgets it.</summary>
internal sealed class SavedFilterViewModel : ReactiveObject, IDisposable
{
    private static readonly (LogLevels Level, string Letter)[] LevelLetters =
    [
        (LogLevels.Trace, "T"),
        (LogLevels.Debug, "D"),
        (LogLevels.Info, "I"),
        (LogLevels.Warn, "W"),
        (LogLevels.Error, "E"),
    ];

    internal SavedFilterViewModel(MainWindowViewModel owner, SavedFilter filter)
    {
        Name = filter.Name;
        Summary = Summarize(filter);
        // Applying merges into the selected source, so it needs one.
        var hasSource = owner
            .WhenAnyValue(vm => vm.SelectedFile)
            .Select(file => file is not null)
            .TakeUntil(_retired);
        ApplyCommand = ReactiveCommand.CreateFromTask(
            () => owner.State.ApplySavedFilterAsync(owner.SelectedFile?.Model, Name).AsTask(),
            hasSource,
            owner.Scheduler
        );
        DeleteCommand = ReactiveCommand.CreateFromTask(
            () => owner.State.DeleteSavedFilterAsync(Name).AsTask(),
            owner.Scheduler
        );
        // Failures surface on the window's error line, like every other sidebar command.
        ApplyCommand.ThrownExceptions.Subscribe(ex => owner.SetFileError(ex.Message));
        DeleteCommand.ThrownExceptions.Subscribe(ex => owner.SetFileError(ex.Message));
        hasSource.Subscribe(has =>
        {
            _hasSource = has;
            this.RaisePropertyChanged(nameof(ApplyToolTip));
        });
    }

    public string Name { get; }
    public string Summary { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

    private readonly Subject<Unit> _retired = new();
    private bool _hasSource;

    public string ApplyToolTip =>
        _hasSource ? "Merge into the selected source" : "Select a source to apply";

    /// <summary>Stops following the owner's selection so a replaced row can be collected. The commands stay usable: a delete still in flight must be able to report its failure.</summary>
    public void Dispose() => _retired.OnNext(Unit.Default);

    /// <summary>Like <c>2 rules · levels W E</c>; the level part is omitted when every level is shown.</summary>
    internal static string Summarize(SavedFilter filter)
    {
        var count = filter.Rules.Count;
        var text = $"{count} rule{(count == 1 ? string.Empty : "s")}";
        if (filter.Levels == LogLevels.All)
            return text;
        var letters = LevelLetters
            .Where(item => (filter.Levels & item.Level) != 0)
            .Select(item => item.Letter);
        return $"{text} · levels {string.Join(' ', letters)}";
    }
}

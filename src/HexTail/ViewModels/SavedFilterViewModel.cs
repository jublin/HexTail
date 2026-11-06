using System.Reactive;
using HexTail.Domain;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>One saved filter in the sidebar: apply merges it into the selected source, delete forgets it.</summary>
internal sealed class SavedFilterViewModel : ReactiveObject
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
        ApplyCommand = ReactiveCommand.CreateFromTask(
            () => owner.State.ApplySavedFilterAsync(owner.SelectedFile?.Model, Name).AsTask(),
            owner.Scheduler
        );
        DeleteCommand = ReactiveCommand.CreateFromTask(
            () => owner.State.DeleteSavedFilterAsync(Name).AsTask(),
            owner.Scheduler
        );
        // Failures surface on the window's error line, like every other sidebar command.
        ApplyCommand.ThrownExceptions.Subscribe(ex => owner.SetFileError(ex.Message));
        DeleteCommand.ThrownExceptions.Subscribe(ex => owner.SetFileError(ex.Message));
    }

    public string Name { get; }
    public string Summary { get; }
    public ReactiveCommand<Unit, Unit> ApplyCommand { get; }
    public ReactiveCommand<Unit, Unit> DeleteCommand { get; }

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

using System.Reactive;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>One T/D/I/W/E button of the level control. The selected source owns the state.</summary>
internal sealed class LevelToggleViewModel : ReactiveObject
{
    private readonly FilterBarViewModel _owner;

    internal LevelToggleViewModel(
        FilterBarViewModel owner,
        LogLevels level,
        string label,
        string name
    )
    {
        _owner = owner;
        Level = level;
        Label = label;
        Name = name;
        ToggleCommand = ReactiveCommand.CreateFromTask(
            () => _owner.ToggleLevelAsync(this),
            owner.Scheduler
        );
    }

    public LogLevels Level { get; }
    public string Label { get; }
    public string Name { get; }
    public bool IsOn => _owner.IsLevelOn(Level);
    public string AutomationName => $"{Name} {(IsOn ? "shown" : "hidden")}";
    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    internal void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsOn));
        this.RaisePropertyChanged(nameof(AutomationName));
    }
}

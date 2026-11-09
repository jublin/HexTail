using System.Reactive;
using Avalonia.Media;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>
/// One member toggle of a stream tab's filter bar: a colour dot and the member's name. Checked means
/// the member's rows are shown; a member that is not open is disabled and labelled "missing".
/// </summary>
internal sealed class SourceChipViewModel : ReactiveObject
{
    private readonly FilterBarViewModel _owner;

    internal SourceChipViewModel(
        FilterBarViewModel owner,
        string key,
        string? tabId,
        string name,
        string color
    )
    {
        _owner = owner;
        Key = key;
        TabId = tabId;
        Name = name;
        Color = color;
        Brush = StreamBrushes.For(color);
        ToggleCommand = ReactiveCommand.CreateFromTask(
            () => _owner.ToggleSourceAsync(this),
            owner.Scheduler
        );
    }

    /// <summary>The member key (<c>file:…</c> or <c>elastic:…</c>) that identifies the chip across rebuilds.</summary>
    internal string Key { get; }

    /// <summary>The member's tab id (its rows' <c>SourceId</c>); null while the member is not open.</summary>
    internal string? TabId { get; }

    public string Name { get; }
    public string Color { get; }
    public IBrush Brush { get; }
    public bool IsMissing => TabId is null;
    public bool IsEnabled => TabId is not null;

    /// <summary>The text on the chip: the name, plus "missing" for a member that is not open.</summary>
    public string Label => IsMissing ? $"{Name} (missing)" : Name;

    /// <summary>True when the member's rows are shown.</summary>
    public bool IsOn => TabId is not null && _owner.IsSourceShown(TabId);

    public string AutomationName =>
        IsMissing ? $"{Name} missing"
        : IsOn ? $"{Name} shown"
        : $"{Name} hidden";

    public string ToolTip =>
        IsMissing ? $"{Name} is not open, so it contributes no rows."
        : IsOn ? $"Hide rows from {Name}"
        : $"Show rows from {Name}";

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    internal void Refresh()
    {
        this.RaisePropertyChanged(nameof(IsOn));
        this.RaisePropertyChanged(nameof(AutomationName));
        this.RaisePropertyChanged(nameof(ToolTip));
    }
}

using Avalonia.Media;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>One saved stream in the sidebar, open or closed. Rows are kept across syncs so focus survives.</summary>
internal sealed class StreamRowViewModel : ReactiveObject
{
    private string _name = string.Empty;
    private string _toolTip = string.Empty;
    private string _colorKey = string.Empty;
    private IReadOnlyList<IBrush> _dots = [];
    private FileTabViewModel? _tab;
    private bool _isSelected;

    internal StreamRowViewModel(string id) => Id = id;

    public string Id { get; }

    public string Name
    {
        get => _name;
        private set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public string ToolTip
    {
        get => _toolTip;
        private set => this.RaiseAndSetIfChanged(ref _toolTip, value);
    }

    /// <summary>One brush per member, in member order.</summary>
    public IReadOnlyList<IBrush> Dots
    {
        get => _dots;
        private set => this.RaiseAndSetIfChanged(ref _dots, value);
    }

    /// <summary>The stream's open tab, or null while it is closed.</summary>
    public FileTabViewModel? Tab
    {
        get => _tab;
        private set
        {
            if (ReferenceEquals(_tab, value))
                return;
            this.RaiseAndSetIfChanged(ref _tab, value);
            this.RaisePropertyChanged(nameof(IsOpen));
        }
    }

    public bool IsOpen => _tab is not null;

    public bool IsSelected
    {
        get => _isSelected;
        private set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }

    internal void Sync(StreamSettings definition, FileTabViewModel? tab, bool selected)
    {
        Name = definition.Name;
        ToolTip =
            definition.Members.Count == 0
                ? definition.Name
                : $"{definition.Name}\n{string.Join('\n', definition.Members.Select(m => "• " + MainWindowViewModel.DescribeMemberKey(m.Key)))}";
        var colorKey = string.Join(',', definition.Members.Select(member => member.Color));
        if (colorKey != _colorKey)
        {
            _colorKey = colorKey;
            Dots = definition.Members.Select(member => StreamBrushes.For(member.Color)).ToArray();
        }
        Tab = tab;
        IsSelected = selected;
    }
}

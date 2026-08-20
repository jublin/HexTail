using HexTailSharp.Application;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class ElasticSourceOptionViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private bool _isOpen;

    internal ElasticSourceOptionViewModel(
        MainWindowViewModel owner,
        string sourceId,
        string displayName,
        string toolTip
    )
    {
        _owner = owner;
        SourceId = sourceId;
        DisplayName = displayName;
        ToolTip = toolTip;
    }

    public string SourceId { get; }
    public string DisplayName { get; }
    public string ToolTip { get; }
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (!this.RaiseAndSetIfChanged(ref _isOpen, value))
                return;
            _ = ToggleAsync(value);
        }
    }

    private async Task CloseAsync()
    {
        var tab = _owner.State.Files.FirstOrDefault(file =>
            file.Source.ElasticSourceId == SourceId
        );
        if (tab is not null)
            await _owner.State.CloseFileAsync(tab);
    }

    private async Task ToggleAsync(bool open)
    {
        if (open)
            await _owner.State.OpenElasticSourceAsync(SourceId);
        else
            await CloseAsync();
    }
}

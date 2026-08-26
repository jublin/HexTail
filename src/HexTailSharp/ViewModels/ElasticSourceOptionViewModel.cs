using HexTailSharp.Application;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class ElasticSourceOptionViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private bool _isOpen;
    private string _status = "Checking";

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
    public string Status
    {
        get => _status;
        private set => this.RaiseAndSetIfChanged(ref _status, value);
    }
    public string StatusGlyph =>
        Status switch
        {
            "Connected" => "mdi-cloud-check",
            "Checking" => "mdi-cloud-sync",
            _ => "mdi-cloud-alert",
        };
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
        try
        {
            if (open)
                await _owner.State.OpenElasticSourceAsync(SourceId);
            else
                await CloseAsync();
        }
        catch
        {
            _isOpen = !open;
            this.RaisePropertyChanged(nameof(IsOpen));
        }
    }

    internal void Sync(bool isOpen, string status)
    {
        if (_isOpen != isOpen)
        {
            _isOpen = isOpen;
            this.RaisePropertyChanged(nameof(IsOpen));
        }
        if (Status == status)
            return;
        Status = status;
        this.RaisePropertyChanged(nameof(StatusGlyph));
    }
}

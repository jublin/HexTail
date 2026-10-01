using HexTail.Application;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class ElasticSourceOptionViewModel : ReactiveObject
{
    private readonly MainWindowViewModel _owner;
    private bool _isOpen;
    private string _status = "Not tested";
    private string? _openError;
    private bool _isOpening;
    private string _healthMessage = "Server reachability: not tested";
    private string _loadStatus = "Logs: closed";

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
    public string? OpenError
    {
        get => _openError;
        private set => this.RaiseAndSetIfChanged(ref _openError, value);
    }
    public string HealthMessage
    {
        get => _healthMessage;
        private set => this.RaiseAndSetIfChanged(ref _healthMessage, value);
    }
    public string LoadStatus
    {
        get => _loadStatus;
        private set => this.RaiseAndSetIfChanged(ref _loadStatus, value);
    }
    public bool IsOpening
    {
        get => _isOpening;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isOpening, value);
            this.RaisePropertyChanged(nameof(CanToggle));
        }
    }
    public bool CanToggle => !IsOpening;
    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            if (IsOpening || !this.RaiseAndSetIfChanged(ref _isOpen, value))
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
        IsOpening = true;
        OpenError = null;
        try
        {
            if (open)
                await _owner.State.OpenElasticSourceAsync(SourceId);
            else
                await CloseAsync();
        }
        catch (Exception exception)
        {
            OpenError = exception.Message;
            _isOpen = !open;
            this.RaisePropertyChanged(nameof(IsOpen));
        }
        finally
        {
            IsOpening = false;
        }
    }

    internal void Sync(
        bool isOpen,
        string status,
        string? healthMessage = null,
        string loadStatus = "Logs: closed"
    )
    {
        HealthMessage = $"Server reachability: {healthMessage ?? status}";
        LoadStatus = loadStatus;
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

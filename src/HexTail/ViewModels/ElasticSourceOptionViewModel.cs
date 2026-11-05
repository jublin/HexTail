using System.Reactive;
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
    private bool _isSelected;
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
        ShortName = displayName;
        ActivateCommand = ReactiveCommand.CreateFromTask(ActivateAsync, owner.Scheduler);
        CloseCommand = ReactiveCommand.CreateFromTask(CloseAsync, owner.Scheduler);
    }

    public string SourceId { get; }
    public string ConnectionId { get; private set; } = string.Empty;
    public string ShortName { get; private set; }
    public bool IsSelected
    {
        get => _isSelected;
        private set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
    public bool HasOpenError => !string.IsNullOrEmpty(OpenError);
    public ReactiveCommand<Unit, Unit> ActivateCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public string DisplayName { get; private set; }
    public string ToolTip { get; private set; }
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
        private set
        {
            this.RaiseAndSetIfChanged(ref _openError, value);
            this.RaisePropertyChanged(nameof(HasOpenError));
        }
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

    private async Task ActivateAsync()
    {
        if (IsOpening)
            return;
        IsOpening = true;
        OpenError = null;
        try
        {
            await _owner.State.OpenElasticSourceAsync(SourceId);
        }
        catch (Exception exception)
        {
            OpenError = exception.Message;
        }
        finally
        {
            IsOpening = false;
        }
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

    internal void SyncIdentity(LogSourceDescriptor source)
    {
        if (DisplayName != source.DisplayName)
        {
            DisplayName = source.DisplayName;
            this.RaisePropertyChanged(nameof(DisplayName));
        }
        if (ToolTip != source.ToolTip)
        {
            ToolTip = source.ToolTip;
            this.RaisePropertyChanged(nameof(ToolTip));
        }
    }

    internal void SyncConnection(string connectionId, string shortName)
    {
        ConnectionId = connectionId;
        if (ShortName == shortName)
            return;
        ShortName = shortName;
        this.RaisePropertyChanged(nameof(ShortName));
    }

    internal void SyncSelection(bool selected) => IsSelected = selected;

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

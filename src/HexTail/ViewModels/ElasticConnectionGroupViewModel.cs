using System.Collections.ObjectModel;
using System.Reactive;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class ElasticConnectionGroupViewModel : ReactiveObject
{
    private string _name = string.Empty;
    private string _status = "Checking";
    private string _statusMessage = string.Empty;

    internal ElasticConnectionGroupViewModel(MainWindowViewModel owner, string id)
    {
        Id = id;
        EditCommand = ReactiveCommand.CreateFromTask(
            () =>
            {
                owner.ElasticSetup.Run(() => owner.ElasticSetup.EditConnection(Id));
                owner.DismissNarrowSidebar();
                return Task.CompletedTask;
            },
            null,
            owner.Scheduler
        );
        AddSourceCommand = ReactiveCommand.CreateFromTask(
            () =>
            {
                owner.ElasticSetup.Run(() => owner.ElasticSetup.AddSource(Id));
                owner.DismissNarrowSidebar();
                return Task.CompletedTask;
            },
            null,
            owner.Scheduler
        );
    }

    public string Id { get; }

    /// <summary>Opens the setup flow on this connection's first step.</summary>
    public ReactiveCommand<Unit, Unit> EditCommand { get; }

    /// <summary>Opens the setup flow to add another source to this connection.</summary>
    public ReactiveCommand<Unit, Unit> AddSourceCommand { get; }
    public ObservableCollection<ElasticSourceOptionViewModel> Sources { get; } = [];

    public string Name
    {
        get => _name;
        private set => this.RaiseAndSetIfChanged(ref _name, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    public bool IsConnected => _status == "Connected";
    public bool IsFailed => _status is not ("Connected" or "Checking" or "Not tested");
    public string StatusText =>
        _status switch
        {
            "Connected" => string.Empty,
            "Checking" or "Not tested" => "checking",
            "Unauthorized" => "auth failed",
            "Unreachable" => "unreachable",
            "Misconfigured" => "misconfigured",
            _ => _status.ToLowerInvariant(),
        };

    internal void Sync(string name, IReadOnlyList<ElasticSourceOptionViewModel> sources)
    {
        Name = name;
        if (!Sources.SequenceEqual(sources))
        {
            Sources.Clear();
            foreach (var source in sources)
                Sources.Add(source);
        }
        var first = sources.FirstOrDefault();
        StatusMessage = first?.HealthMessage ?? string.Empty;
        var status = first?.Status ?? "Checking";
        if (_status == status)
            return;
        _status = status;
        this.RaisePropertyChanged(nameof(IsConnected));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(StatusText));
    }
}

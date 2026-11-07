using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Linq;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class ElasticConnectionGroupViewModel : ReactiveObject
{
    private const string NoSources = "No sources";
    private string _name = string.Empty;
    private string _status = "Checking";
    private string _statusMessage = string.Empty;

    internal ElasticConnectionGroupViewModel(MainWindowViewModel owner, string id)
    {
        Id = id;
        // Starting another flow while one is saving would drop what that save is writing.
        var idle = owner.ElasticSetup.WhenAnyValue(setup => setup.IsBusy).Select(busy => !busy);
        EditCommand = ReactiveCommand.CreateFromTask(
            () =>
            {
                owner.ElasticSetup.Run(() => owner.ElasticSetup.EditConnection(Id));
                owner.DismissNarrowSidebar();
                return Task.CompletedTask;
            },
            idle,
            owner.Scheduler
        );
        AddSourceCommand = ReactiveCommand.CreateFromTask(
            () =>
            {
                owner.ElasticSetup.Run(() => owner.ElasticSetup.AddSource(Id));
                owner.DismissNarrowSidebar();
                return Task.CompletedTask;
            },
            idle,
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
    public bool IsFailed => _status is not ("Connected" or "Checking" or "Not tested" or NoSources);
    public string StatusText =>
        _status switch
        {
            "Connected" => string.Empty,
            "Checking" or "Not tested" => "checking",
            NoSources => "no sources",
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
        // A connection abandoned after its first step has no views yet; nothing is being checked.
        var status = first?.Status ?? (sources.Count == 0 ? NoSources : "Checking");
        if (_status == status)
            return;
        _status = status;
        this.RaisePropertyChanged(nameof(IsConnected));
        this.RaisePropertyChanged(nameof(IsFailed));
        this.RaisePropertyChanged(nameof(StatusText));
    }
}

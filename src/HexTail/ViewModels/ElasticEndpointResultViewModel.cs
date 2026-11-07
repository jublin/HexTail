using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal enum EndpointState
{
    NotTested,
    Checking,
    Ok,
    Failed,
}

/// <summary>The outcome of testing one part of a connection (Kibana, Elasticsearch or credentials).</summary>
internal sealed class ElasticEndpointResultViewModel : ReactiveObject
{
    private EndpointState _state;
    private string _message = "Not tested";

    public ElasticEndpointResultViewModel(string label) => Label = label;

    public string Label { get; }
    public EndpointState State => _state;
    public string Message => _message;
    public bool IsOk => _state == EndpointState.Ok;
    public bool IsFailed => _state == EndpointState.Failed;

    internal void Set(EndpointState state, string message)
    {
        if (_state == state && _message == message)
            return;
        _state = state;
        _message = message;
        this.RaisePropertyChanged(nameof(State));
        this.RaisePropertyChanged(nameof(Message));
        this.RaisePropertyChanged(nameof(IsOk));
        this.RaisePropertyChanged(nameof(IsFailed));
    }

    internal void Reset() => Set(EndpointState.NotTested, "Not tested");
}

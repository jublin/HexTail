using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class ElasticSourceSettingViewModel : ReactiveObject
{
    private string _serverValue = string.Empty;
    private string _namespaceValue = string.Empty;

    public ElasticSourceSettingViewModel(string id) => Id = id;

    public string Id { get; }
    public string ServerValue
    {
        get => _serverValue;
        set => this.RaiseAndSetIfChanged(ref _serverValue, value);
    }
    public string NamespaceValue
    {
        get => _namespaceValue;
        set => this.RaiseAndSetIfChanged(ref _namespaceValue, value);
    }

    public ElasticSourceSettings ToSettings() =>
        new()
        {
            Id = Id,
            ServerValue = ServerValue.Trim(),
            NamespaceValue = NamespaceValue.Trim(),
        };
}

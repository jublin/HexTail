using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class ElasticFieldOptionViewModel(
    string name,
    string type = "unknown",
    bool? searchable = null
) : ReactiveObject
{
    private bool _isOutput;
    public string Name { get; } = name;
    public string Type { get; } = type;
    public bool? Searchable { get; } = searchable;
    public string MetadataDescription =>
        Searchable is null
            ? "Metadata not loaded"
            : $"{Type} · {(Searchable.Value ? "searchable" : "not searchable")}";
    public bool IsOutput
    {
        get => _isOutput;
        set => this.RaiseAndSetIfChanged(ref _isOutput, value);
    }
}

using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class ElasticFieldOptionViewModel(string name) : ReactiveObject
{
    private bool _isOutput;
    public string Name { get; } = name;
    public bool IsOutput
    {
        get => _isOutput;
        set => this.RaiseAndSetIfChanged(ref _isOutput, value);
    }
}

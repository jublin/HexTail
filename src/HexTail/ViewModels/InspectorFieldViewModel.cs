using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class InspectorFieldViewModel : ReactiveObject
{
    internal InspectorFieldViewModel(InspectorViewModel owner, string key, string value)
    {
        Owner = owner;
        Key = key;
        Value = value;
    }

    internal InspectorViewModel Owner { get; }
    public string Key { get; }
    public string Value { get; }
}

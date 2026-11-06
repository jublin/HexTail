using HexTail.Domain;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>One neighbouring line in the inspector's context list.</summary>
internal sealed class InspectorContextRowViewModel : ReactiveObject
{
    internal InspectorContextRowViewModel(Line line, bool isSelected)
    {
        Line = line;
        IsSelected = isSelected;
    }

    public Line Line { get; }
    public string Text => Line.Raw;
    public bool IsSelected { get; }
}

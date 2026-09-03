using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using HexTail.ViewModels;

namespace HexTail.Views;

public sealed class HighlightedTextBlock : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<LogTextSegmentViewModel>?> SegmentsProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IReadOnlyList<LogTextSegmentViewModel>?>(
            nameof(Segments)
        );

    public IReadOnlyList<LogTextSegmentViewModel>? Segments
    {
        get => GetValue(SegmentsProperty);
        set => SetValue(SegmentsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SegmentsProperty)
            RebuildInlines();
    }

    private void RebuildInlines()
    {
        var segments = Segments;
        if (segments is null)
        {
            Text = string.Empty;
            return;
        }

        Text = null;
        var inlines = new InlineCollection();
        foreach (var segment in segments)
            inlines.Add(
                new Run
                {
                    Text = segment.Text,
                    Background = segment.Background,
                    Foreground = segment.Foreground,
                }
            );
        Inlines = inlines;
    }
}

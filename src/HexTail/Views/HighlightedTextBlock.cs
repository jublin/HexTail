using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;
using HexTail.Application;

namespace HexTail.Views;

public sealed class HighlightedTextBlock : TextBlock
{
    public static readonly StyledProperty<IReadOnlyList<HighlightSpan>?> SpansProperty =
        AvaloniaProperty.Register<HighlightedTextBlock, IReadOnlyList<HighlightSpan>?>(
            nameof(Spans)
        );

    private readonly TextRunCache _runCache = new();

    public IReadOnlyList<HighlightSpan>? Spans
    {
        get => GetValue(SpansProperty);
        set => SetValue(SpansProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SpansProperty)
            InvalidateTextLayout();
    }

    protected override void OnMeasureInvalidated()
    {
        _runCache.Invalidate();
        base.OnMeasureInvalidated();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _runCache.Dispose();
        InvalidateTextLayout();
    }

    protected override TextLayout CreateTextLayout(string? text)
    {
        if (Spans is not { Count: > 0 })
            return base.CreateTextLayout(text);
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        var styles = new List<ValueSpan<TextRunProperties>>(Spans?.Count ?? 0);
        var propertiesByColor = new Dictionary<string, TextRunProperties>(StringComparer.Ordinal);
        if (Spans is { } spans && text is not null)
            foreach (var span in spans)
            {
                // Text and span bindings can change separately when a row is recycled.
                if (span.Start < 0 || span.Length <= 0 || span.Length > text.Length - span.Start)
                    continue;
                if (!propertiesByColor.TryGetValue(span.Color, out var properties))
                {
                    var background = Color.Parse(span.Color);
                    properties = new GenericTextRunProperties(
                        typeface,
                        FontSize,
                        TextDecorations,
                        new SolidColorBrush(ReadableHighlightColor(background)),
                        new SolidColorBrush(background),
                        fontFeatures: FontFeatures
                    );
                    propertiesByColor.Add(span.Color, properties);
                }
                styles.Add(new ValueSpan<TextRunProperties>(span.Start, span.Length, properties));
            }
        var maxSize = GetMaxSizeFromConstraint();
        return new TextLayout(
            text,
            typeface,
            FontSize,
            Foreground,
            textAlignment: IsMeasureValid ? TextAlignment : TextAlignment.Left,
            textWrapping: TextWrapping,
            textTrimming: TextTrimming,
            textDecorations: TextDecorations,
            flowDirection: FlowDirection,
            maxWidth: maxSize.Width,
            maxHeight: maxSize.Height,
            lineHeight: LineHeight,
            letterSpacing: LetterSpacing,
            maxLines: MaxLines,
            fontFeatures: FontFeatures,
            textStyleOverrides: styles,
            textRunCache: _runCache
        );
    }

    internal static Color ReadableHighlightColor(Color background)
    {
        static double Channel(byte value)
        {
            var normalized = value / 255d;
            return normalized <= 0.03928
                ? normalized / 12.92
                : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        var luminance =
            0.2126 * Channel(background.R)
            + 0.7152 * Channel(background.G)
            + 0.0722 * Channel(background.B);
        return luminance > 0.179 ? Colors.Black : Colors.White;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using HexTail.Application;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class HighlightedTextBlockTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpanLayoutMatchesInlineSizingAndColors(bool wrap)
    {
        var wrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        var trimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis;
        const string text = "ready ERROR request completed with more text";
        var span = new HighlightSpan(6, 5, "#F59E0B");
        var actual = new HighlightedTextBlock
        {
            Text = text,
            Spans = [span],
            FontSize = 14,
            Foreground = Brushes.White,
            TextWrapping = wrapping,
            TextTrimming = trimming,
            Padding = new Thickness(3),
        };
        var expected = new TextBlock
        {
            FontSize = 14,
            Foreground = Brushes.White,
            TextWrapping = wrapping,
            TextTrimming = trimming,
            Padding = new Thickness(3),
            Inlines = new InlineCollection
            {
                new Run("ready "),
                new Run("ERROR")
                {
                    Foreground = Brushes.Black,
                    Background = new SolidColorBrush(Color.Parse(span.Color)),
                },
                new Run(" request completed with more text"),
            },
        };
        foreach (var width in new[] { 130d, 260d })
        {
            actual.Measure(new Size(width, 500));
            expected.Measure(new Size(width, 500));
            actual.Arrange(new Rect(new Size(width, actual.DesiredSize.Height)));
            expected.Arrange(new Rect(new Size(width, expected.DesiredSize.Height)));
            Assert.Equal(expected.DesiredSize.Height, actual.DesiredSize.Height, 3);
            Assert.Equal(expected.TextLayout.Width, actual.TextLayout.Width, 3);
            Assert.Equal(expected.TextLayout.TextLines.Count, actual.TextLayout.TextLines.Count);
            var run = actual
                .TextLayout.TextLines.SelectMany(line => line.TextRuns)
                .First(run => run.Properties?.BackgroundBrush is not null);
            Assert.Equal(
                Colors.Black,
                Assert.IsAssignableFrom<ISolidColorBrush>(run.Properties!.ForegroundBrush).Color
            );
            Assert.Equal(
                Color.Parse(span.Color),
                Assert.IsAssignableFrom<ISolidColorBrush>(run.Properties.BackgroundBrush).Color
            );
        }
        Assert.Same(text, actual.Text);
        Assert.True(actual.Inlines is null || actual.Inlines.Count == 0);
    }

    [AvaloniaFact]
    public void DetachedControlCanRenderAgainAfterShapingCacheIsReleased()
    {
        var block = new HighlightedTextBlock
        {
            Text = "error",
            Spans = [new HighlightSpan(0, 5, "#F59E0B")],
        };
        var window = new Window
        {
            Content = block,
            Width = 300,
            Height = 100,
        };
        try
        {
            window.Show();
            var width = block.TextLayout.Width;
            window.Content = null;
            window.Content = block;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Equal(width, block.TextLayout.Width, 3);
            Assert.Contains(
                block.TextLayout.TextLines.SelectMany(line => line.TextRuns),
                run => run.Properties?.BackgroundBrush is not null
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void SpanRendererAllocatesLessThanInlineRenderer()
    {
        var text = string.Join(" ", Enumerable.Repeat("error ready", 40));
        var spans = Enumerable
            .Range(0, 40)
            .Select(i => new HighlightSpan(i * 12, 5, "#F59E0B"))
            .ToArray();
        TextBlock Create(bool useSpans)
        {
            if (useSpans)
                return new HighlightedTextBlock
                {
                    Text = text,
                    Spans = spans,
                    FontSize = 14,
                };
            var inlines = new InlineCollection();
            var cursor = 0;
            foreach (var span in spans)
            {
                if (cursor < span.Start)
                    inlines.Add(new Run(text[cursor..span.Start]));
                inlines.Add(
                    new Run(text.Substring(span.Start, span.Length))
                    {
                        Background = new SolidColorBrush(Color.Parse(span.Color)),
                        Foreground = Brushes.Black,
                    }
                );
                cursor = span.Start + span.Length;
            }
            inlines.Add(new Run(text[cursor..]));
            return new TextBlock { Inlines = inlines, FontSize = 14 };
        }
        (long Bytes, double Milliseconds) Measure(bool useSpans, int count)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < count; i++)
            {
                var block = Create(useSpans);
                block.Measure(new Size(1_000, 100));
                block.Arrange(new Rect(new Size(1_000, block.DesiredSize.Height)));
                block.TextLayout.Dispose();
            }
            return (
                GC.GetAllocatedBytesForCurrentThread() - before,
                timer.Elapsed.TotalMilliseconds
            );
        }
        Measure(false, 5);
        Measure(true, 5);
        var old = Measure(false, 100);
        var current = Measure(true, 100);
        TestContext.Current.TestOutputHelper?.WriteLine(
            $"100 layouts: inlines {old.Bytes:N0} bytes / {old.Milliseconds:N2} ms; spans {current.Bytes:N0} bytes / {current.Milliseconds:N2} ms"
        );
        Assert.True(
            current.Bytes < old.Bytes * 0.75,
            $"Span allocation {current.Bytes} versus inline allocation {old.Bytes}"
        );
    }

    [AvaloniaFact]
    public void RecycledTextAndChangedSpansInvalidateLayout()
    {
        var block = new HighlightedTextBlock
        {
            Text = "error",
            Spans = [new HighlightSpan(0, 5, "#F59E0B")],
        };
        block.Measure(new Size(200, 100));
        var original = block.TextLayout;
        block.Spans = [new HighlightSpan(0, 5, "#1E293B")];
        block.Measure(new Size(200, 100));
        Assert.NotSame(original, block.TextLayout);
        var run = block.TextLayout.TextLines.SelectMany(line => line.TextRuns).First();
        Assert.Equal(
            Colors.White,
            Assert.IsAssignableFrom<ISolidColorBrush>(run.Properties!.ForegroundBrush).Color
        );
        // A recycled control may receive shorter text before its new span binding.
        block.Text = "ok";
        block.Measure(new Size(200, 100));
        block.Spans = [];
        block.FontSize = 24;
        block.Measure(new Size(200, 100));
        Assert.All(
            block.TextLayout.TextLines.SelectMany(line => line.TextRuns),
            run => Assert.Null(run.Properties?.BackgroundBrush)
        );
        block.Text = null;
        block.Measure(new Size(200, 100));
        Assert.True(block.Inlines is null || block.Inlines.Count == 0);
    }
}

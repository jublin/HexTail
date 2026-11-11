using System.Collections.Concurrent;
using Avalonia.Media;

namespace HexTail.ViewModels;

/// <summary>Member colours as immutable brushes, one per colour string, so rows never allocate a brush.</summary>
internal static class StreamBrushes
{
    private static readonly ConcurrentDictionary<string, IBrush> Cache = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>The brush for a <c>#rrggbb</c> colour; an unreadable value gets a neutral grey.</summary>
    internal static IBrush For(string color) =>
        Cache.GetOrAdd(
            color,
            static value =>
                new SolidColorBrush(
                    Color.TryParse(value, out var parsed) ? parsed : Color.Parse("#94a3b8")
                ).ToImmutable()
        );
}

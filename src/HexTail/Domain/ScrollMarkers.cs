namespace HexTail.Domain;

/// <summary>One displayed row that the overview ruler marks: its rule colour (or null) and whether it is an error.</summary>
public readonly record struct ScrollMarker(int Row, string? RuleColor, bool Error);

public static class ScrollMarkers
{
    /// <summary>Markers for the rows that match a rule or are errors, in row order.</summary>
    public static IReadOnlyList<ScrollMarker> Build(
        IReadOnlyList<Line> rows,
        Func<Line, string?> ruleColor
    )
    {
        var markers = new List<ScrollMarker>();
        for (var index = 0; index < rows.Count; index++)
        {
            var color = ruleColor(rows[index]);
            var error = LevelDetector.Detect(rows[index]) == LogLevels.Error;
            if (color is not null || error)
                markers.Add(new ScrollMarker(index, color, error));
        }
        return markers;
    }
}

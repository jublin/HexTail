namespace HexTail.Domain;

/// <summary>
/// A single log line: its raw text plus an optional parsed representation
/// (e.g. logfmt key-value pairs). <see cref="ParsedFields"/> is <c>null</c>
/// when the line has no parsed form (plain text); logfmt lines that fail to
/// parse carry an empty map instead.
/// <para>
/// <see cref="Timestamp"/> is the line's own time when one could be detected, otherwise the time
/// of the nearest preceding timestamped line (then <see cref="TimestampInherited"/> is
/// <c>true</c>), otherwise <c>null</c>. Lines are compared by reference everywhere; the added
/// init properties only widen the generated value equality, which nothing relies on.
/// </para>
/// </summary>
public sealed record Line(string Raw, IReadOnlyDictionary<string, string>? ParsedFields = null)
{
    public DateTimeOffset? Timestamp { get; init; }

    public bool TimestampInherited { get; init; }
}

namespace HexTailSharp.Domain;

/// <summary>
/// A single log line: its raw text plus an optional parsed representation
/// (e.g. logfmt key-value pairs). <see cref="ParsedFields"/> is <c>null</c>
/// when the line has no parsed form (plain text); logfmt lines that fail to
/// parse carry an empty map instead.
/// </summary>
public sealed record Line(string Raw, IReadOnlyDictionary<string, string>? ParsedFields = null);

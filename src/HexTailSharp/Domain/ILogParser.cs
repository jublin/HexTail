using System.Text;

namespace HexTailSharp.Domain;

/// <summary>Converts raw text into a <see cref="Line"/>. Implementations never throw.</summary>
public interface ILogParser
{
    Line Parse(string rawText);
}

/// <summary>No transformation; each line has no parsed fields.</summary>
public sealed class PlainTextParser : ILogParser
{
    public Line Parse(string rawText) => new(rawText);
}

/// <summary>
/// Parses logfmt <c>key=value</c> pairs into a map. Supports quoted values
/// (<c>key="a b"</c>), escaped quotes and backslashes inside quotes
/// (<c>\"</c>, <c>\\</c>), and empty values (<c>key=</c>). Any malformed
/// token (missing '=', empty key, unterminated quote, garbage after a
/// closing quote) causes the whole line to be treated as plain text with an
/// empty parsed map.
/// </summary>
public sealed class LogfmtParser : ILogParser
{
    public Line Parse(string rawText)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        var n = rawText.Length;

        while (true)
        {
            while (i < n && rawText[i] == ' ')
                i++;
            if (i >= n)
                break;

            // Key: up to '=' or space; empty key or missing '=' is malformed.
            var keyStart = i;
            while (i < n && rawText[i] is not ('=' or ' '))
                i++;
            if (i >= n || rawText[i] == ' ' || i == keyStart)
                return Plain(rawText);
            var key = rawText[keyStart..i];
            i++; // consume '='

            string value;
            if (i < n && rawText[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                var closed = false;
                while (i < n)
                {
                    var c = rawText[i];
                    if (c == '\\' && i + 1 < n)
                    {
                        var next = rawText[i + 1];
                        // Recognized escapes collapse; anything else stays literal.
                        sb.Append(next is '"' or '\\' ? next : c);
                        if (next is not ('"' or '\\'))
                            sb.Append(next);
                        i += 2;
                    }
                    else if (c == '"')
                    {
                        closed = true;
                        i++;
                        break;
                    }
                    else
                    {
                        sb.Append(c);
                        i++;
                    }
                }

                if (!closed)
                    return Plain(rawText);
                // A closing quote must be followed by a space or end of line.
                if (i < n && rawText[i] != ' ')
                    return Plain(rawText);
                value = sb.ToString();
            }
            else
            {
                var valueStart = i;
                while (i < n && rawText[i] != ' ')
                    i++;
                value = rawText[valueStart..i];
            }

            fields[key] = value; // last duplicate key wins
        }

        return fields.Count == 0 ? Plain(rawText) : new Line(rawText, fields);
    }

    private static Line Plain(string raw) => new(raw, new Dictionary<string, string>());
}

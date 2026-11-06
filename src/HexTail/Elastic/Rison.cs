using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace HexTail.Elastic;

/// <summary>Minimal Rison reader (objects, arrays, strings, !t !f !n, numbers, bare identifiers).</summary>
public static partial class Rison
{
    public static bool TryParse(string text, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parser = new Parser(text.Trim());
        if (!parser.TryReadValue(out value) || !parser.AtEnd)
        {
            value = null;
            return false;
        }

        return true;
    }

    private const int MaxDepth = 64;

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex RisonNumber();

    private sealed class Parser(string text)
    {
        private int _index;
        private int _depth;

        public bool AtEnd => _index >= text.Length;

        public bool TryReadValue(out object? value)
        {
            value = null;
            if (AtEnd)
                return false;
            switch (text[_index])
            {
                case '(':
                    return TryReadObject(out value);
                case '!':
                    return TryReadBang(out value);
                case '\'':
                    if (!TryReadString(out var quoted))
                        return false;
                    value = quoted;
                    return true;
                default:
                    return TryReadBare(out value);
            }
        }

        private bool TryReadBang(out object? value)
        {
            value = null;
            if (_index + 1 >= text.Length)
                return false;
            switch (text[_index + 1])
            {
                case 't':
                    _index += 2;
                    value = true;
                    return true;
                case 'f':
                    _index += 2;
                    value = false;
                    return true;
                case 'n':
                    _index += 2;
                    return true;
                case '(':
                    _index++;
                    return TryReadArray(out value);
                default:
                    return false;
            }
        }

        private bool TryReadArray(out object? value)
        {
            value = null;
            _index++; // '('
            var list = new List<object?>();
            if (Peek() == ')')
            {
                _index++;
                value = list;
                return true;
            }

            if (++_depth > MaxDepth)
                return false;
            while (true)
            {
                if (!TryReadValue(out var item))
                    return false;
                list.Add(item);
                if (AtEnd)
                    return false;
                var next = text[_index++];
                if (next == ')')
                {
                    _depth--;
                    value = list;
                    return true;
                }

                if (next != ',')
                    return false;
            }
        }

        private bool TryReadObject(out object? value)
        {
            value = null;
            _index++; // '('
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (Peek() == ')')
            {
                _index++;
                value = map;
                return true;
            }

            if (++_depth > MaxDepth)
                return false;
            while (true)
            {
                string key;
                if (Peek() == '\'')
                {
                    if (!TryReadString(out key))
                        return false;
                }
                else if (!TryReadToken(out key))
                    return false;
                if (AtEnd || text[_index++] != ':')
                    return false;
                if (!TryReadValue(out var item))
                    return false;
                map[key] = item;
                if (AtEnd)
                    return false;
                var next = text[_index++];
                if (next == ')')
                {
                    _depth--;
                    value = map;
                    return true;
                }

                if (next != ',')
                    return false;
            }
        }

        private bool TryReadString(out string value)
        {
            value = string.Empty;
            _index++; // opening quote
            var builder = new StringBuilder();
            while (_index < text.Length)
            {
                var c = text[_index++];
                if (c == '\'')
                {
                    value = builder.ToString();
                    return true;
                }

                if (c != '!')
                {
                    builder.Append(c);
                    continue;
                }

                if (_index >= text.Length)
                    return false;
                var escaped = text[_index++];
                if (escaped is not ('!' or '\''))
                    return false;
                builder.Append(escaped);
            }

            return false;
        }

        private bool TryReadToken(out string token)
        {
            var start = _index;
            while (_index < text.Length && !IsStructural(text[_index]))
                _index++;
            token = text[start.._index];
            return token.Length > 0;
        }

        private bool TryReadBare(out object? value)
        {
            value = null;
            if (!TryReadToken(out var token))
                return false;
            value =
                IsRisonNumber(token)
                && double.TryParse(
                    token,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var number
                )
                && double.IsFinite(number)
                    ? number
                    : token;
            return true;
        }

        private static bool IsRisonNumber(string token) =>
            RisonNumber().IsMatch(token)
            // Plain integers beyond double's exact range would silently lose digits.
            && (
                token.Contains('.')
                || token.Contains('e')
                || token.Contains('E')
                || token.Length <= 15
            );

        private char Peek() => _index < text.Length ? text[_index] : '\0';

        private static bool IsStructural(char c) =>
            c is '(' or ')' or ',' or ':' or '!' or '\'' || char.IsWhiteSpace(c);
    }
}

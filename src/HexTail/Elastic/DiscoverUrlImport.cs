using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;

namespace HexTail.Elastic;

public sealed record DiscoverImport(
    string? KibanaOrigin,
    string? DataViewId,
    IReadOnlyList<string> Columns,
    string? FilterField,
    string? FilterValue,
    IReadOnlyList<string> Ignored
);

/// <summary>Reads the <c>_a</c>/<c>_g</c> Rison state of a Kibana Discover URL.</summary>
public static partial class DiscoverUrlImport
{
    [GeneratedRegex("""^\s*([A-Za-z0-9_.@\-]+)\s*:\s*(?:"([^"]*)"|([^\s()"]+))\s*$""")]
    private static partial Regex SingleClause();

    public static bool TryParse(string url, out DiscoverImport? import)
    {
        import = null;
        if (
            !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
        )
            return false;

        // Discover keeps its state in the fragment ("#/?_a=…&_g=…"); older links use the query string.
        var fragment = uri.Fragment.TrimStart('#');
        var questionMark = fragment.IndexOf('?');
        var fragmentPath = questionMark >= 0 ? fragment[..questionMark] : fragment;
        var fragmentQuery = HttpUtility.ParseQueryString(
            questionMark >= 0 ? fragment[(questionMark + 1)..] : ""
        );
        var uriQuery = HttpUtility.ParseQueryString(uri.Query.TrimStart('?'));
        string? Param(string name) =>
            fragmentQuery[name] is { Length: > 0 } fromFragment ? fromFragment
            : uriQuery[name] is { Length: > 0 } fromQuery ? fromQuery
            : null;

        if (
            Param("_a") is not { } appText
            || !Rison.TryParse(appText, out var parsedApp)
            || parsedApp is not Dictionary<string, object?> app
        )
            return false;

        var ignored = new List<string>();
        void Ignore(string what)
        {
            if (!ignored.Contains(what))
                ignored.Add(what);
        }

        if (
            Param("_g") is { } globalText
            && Rison.TryParse(globalText, out var parsedGlobal)
            && parsedGlobal is Dictionary<string, object?> global
        )
        {
            if (global.ContainsKey("time"))
                Ignore("time range");
            if (
                global.TryGetValue("filters", out var globalFilters)
                && globalFilters is List<object?> { Count: > 0 }
            )
                Ignore("filters");
            if (
                global.TryGetValue("refreshInterval", out var refresh)
                && refresh is Dictionary<string, object?> refreshMap
                && !(refreshMap.TryGetValue("pause", out var paused) && paused is true)
            )
                Ignore("refresh interval");
        }

        if (fragmentPath.StartsWith("/view/", StringComparison.Ordinal))
            Ignore("saved search");

        Dictionary<string, object?>? queryMap = app.TryGetValue("query", out var rawQuery)
            ? rawQuery as Dictionary<string, object?>
            : null;
        Dictionary<string, object?>? sourceMap = app.TryGetValue("dataSource", out var source)
            ? source as Dictionary<string, object?>
            : null;
        var esql =
            (
                sourceMap is not null
                && sourceMap.TryGetValue("type", out var sourceType)
                && sourceType is "esql"
            ) || (queryMap is not null && queryMap.ContainsKey("esql"));
        if (esql)
            Ignore("ES|QL");

        string? dataViewId = null;
        if (!esql)
        {
            if (sourceMap is not null && sourceMap.TryGetValue("dataViewId", out var id))
                dataViewId = AsString(id);
            if (dataViewId is null && app.TryGetValue("index", out var legacy))
                dataViewId = AsString(legacy);
        }

        var columns =
            app.TryGetValue("columns", out var rawColumns) && rawColumns is List<object?> list
                ? list.Select(AsString).OfType<string>().Where(name => name.Length > 0).ToList()
                : [];

        string? field = null;
        string? value = null;
        if (
            !esql
            && queryMap is not null
            && queryMap.TryGetValue("query", out var rawText)
            && AsString(rawText) is { Length: > 0 } kql
        )
        {
            var language = queryMap.TryGetValue("language", out var rawLanguage)
                ? rawLanguage
                : null;
            var match = SingleClause().Match(kql);
            if (
                language is (null or "kuery")
                && match.Success
                && IsPlainValue(match, out var plain)
            )
            {
                field = match.Groups[1].Value;
                value = plain;
            }
            else
                Ignore("query");
        }

        foreach (var name in new[] { "sort", "filters" })
            if (app.TryGetValue(name, out var raw) && raw is List<object?> { Count: > 0 })
                Ignore(name);

        if (app.Keys.Any(key => !KnownAppKeys.Contains(key)))
            Ignore("other state");

        var path = uri.AbsolutePath;
        var appIndex = path.LastIndexOf("/app/", StringComparison.Ordinal);
        var origin = $"{uri.Scheme}://{uri.Authority}" + (appIndex > 0 ? path[..appIndex] : "");
        import = new DiscoverImport(origin, dataViewId, columns, field, value, ignored);
        return true;
    }

    private static readonly HashSet<string> KnownAppKeys = new(StringComparer.Ordinal)
    {
        "columns",
        "dataSource",
        "index",
        "query",
        "sort",
        "filters",
        "interval",
        "hideChart",
        "grid",
        "hideAggregatedPreview",
        "viewMode",
        "rowHeight",
        "rowsPerPage",
        "breakdownField",
        "sampleSize",
        "density",
        "headerRowHeight",
    };

    /// <summary>
    /// A clause is importable only when its value is a double-quoted string or a bare token with no
    /// KQL wildcard/escape/range syntax and not a boolean keyword.
    /// </summary>
    private static bool IsPlainValue(Match match, out string value)
    {
        var quoted = match.Groups[2].Success;
        value = quoted ? match.Groups[2].Value : match.Groups[3].Value;
        if (value.Contains('\\'))
            return false;
        if (quoted)
            return true;
        return value.IndexOfAny(['*', ':', '~', '?', '^']) < 0
            && !value.Equals("and", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("or", StringComparison.OrdinalIgnoreCase)
            && !value.Equals("not", StringComparison.OrdinalIgnoreCase);
    }

    private static string? AsString(object? value) =>
        value switch
        {
            string text => text,
            double number => number.ToString("R", CultureInfo.InvariantCulture),
            _ => null,
        };
}

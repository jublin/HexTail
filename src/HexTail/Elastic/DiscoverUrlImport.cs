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
        var queryText =
            questionMark >= 0 ? fragment[(questionMark + 1)..] : uri.Query.TrimStart('?');
        var query = HttpUtility.ParseQueryString(queryText);
        if (
            query["_a"] is not { Length: > 0 } appText
            || !Rison.TryParse(appText, out var parsedApp)
            || parsedApp is not Dictionary<string, object?> app
        )
            return false;

        var ignored = new List<string>();
        if (
            query["_g"] is { Length: > 0 } globalText
            && Rison.TryParse(globalText, out var parsedGlobal)
            && parsedGlobal is Dictionary<string, object?> global
            && global.ContainsKey("time")
        )
            ignored.Add("time range");

        string? dataViewId = null;
        if (
            app.TryGetValue("dataSource", out var source)
            && source is Dictionary<string, object?> sourceMap
            && sourceMap.TryGetValue("dataViewId", out var id)
        )
            dataViewId = AsString(id);
        if (dataViewId is null && app.TryGetValue("index", out var legacy))
            dataViewId = AsString(legacy);

        var columns =
            app.TryGetValue("columns", out var rawColumns) && rawColumns is List<object?> list
                ? list.Select(AsString).OfType<string>().Where(name => name.Length > 0).ToList()
                : [];

        string? field = null;
        string? value = null;
        if (
            app.TryGetValue("query", out var rawQuery)
            && rawQuery is Dictionary<string, object?> queryMap
            && queryMap.TryGetValue("query", out var rawText)
            && AsString(rawText) is { Length: > 0 } kql
        )
        {
            var match = SingleClause().Match(kql);
            if (match.Success)
            {
                field = match.Groups[1].Value;
                value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            }
            else
                ignored.Add("query");
        }

        foreach (var name in new[] { "sort", "filters" })
            if (app.TryGetValue(name, out var raw) && raw is List<object?> { Count: > 0 })
                ignored.Add(name);

        var path = uri.AbsolutePath;
        var appIndex = path.IndexOf("/app/", StringComparison.Ordinal);
        var origin = uri.GetLeftPart(UriPartial.Authority) + (appIndex > 0 ? path[..appIndex] : "");
        import = new DiscoverImport(origin, dataViewId, columns, field, value, ignored);
        return true;
    }

    private static string? AsString(object? value) =>
        value switch
        {
            string text => text,
            double number => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
}

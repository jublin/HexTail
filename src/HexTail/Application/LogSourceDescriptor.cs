using HexTail.Persistence;

namespace HexTail.Application;

public enum LogSourceKind
{
    File,
    Elastic,
}

public sealed record LogSourceDescriptor(
    string Id,
    LogSourceKind Kind,
    string DisplayName,
    string ToolTip,
    string? LocalPath = null,
    string? ElasticSourceId = null
)
{
    internal static LogSourceDescriptor Elastic(
        ElasticConnectionSettings connection,
        ElasticViewSettings view,
        ElasticSourceSettings source
    )
    {
        var viewName = string.IsNullOrWhiteSpace(view.Name) ? view.DataViewTitle : view.Name;
        var details =
            $"{connection.Name} / {viewName}\nFilter: {view.ServerField} matches \"{source.ServerValue}\"";
        if (
            !string.IsNullOrWhiteSpace(view.NamespaceField)
            || !string.IsNullOrWhiteSpace(source.NamespaceValue)
        )
            details +=
                $"\nPreserved namespace metadata: {view.NamespaceField} = {source.NamespaceValue} (not used as a query constraint)";
        return new(
            source.Id,
            LogSourceKind.Elastic,
            $"{connection.Name} / {viewName} · {source.ServerValue}",
            details,
            ElasticSourceId: source.Id
        );
    }
}

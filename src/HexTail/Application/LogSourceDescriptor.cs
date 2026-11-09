using HexTail.Persistence;

namespace HexTail.Application;

public enum LogSourceKind
{
    File,
    Elastic,
    Stream,
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
    /// <summary>The descriptor of a stream tab. Its id (<c>stream:{id}</c>) cannot clash with file or Elastic ids.</summary>
    internal static LogSourceDescriptor Stream(StreamSettings stream)
    {
        var members = string.Join(
            "\n",
            stream.Members.Select(member =>
                $"• {(member.Key.StartsWith("file:", StringComparison.Ordinal) ? member.Key[5..] : member.Key.StartsWith("elastic:", StringComparison.Ordinal) ? member.Key[8..] : member.Key)}"
            )
        );
        return new(
            $"stream:{stream.Id}",
            LogSourceKind.Stream,
            stream.Name,
            members.Length == 0
                ? $"Stream {stream.Name}"
                : $"Stream {stream.Name}\nMerges:\n{members}"
        );
    }

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

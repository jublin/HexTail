namespace HexTailSharp.Application;

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
);

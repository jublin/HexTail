namespace HexTail.ViewModels;

internal sealed record RecentFileViewModel(string Path, bool Exists)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public string ToolTip => Exists ? Path : $"{Path} (missing)";
}

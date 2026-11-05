using System.Collections.ObjectModel;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed record PaletteItem(
    string Title,
    string Category,
    string? Gesture,
    Func<Task> Execute
);

internal sealed class CommandPaletteViewModel(Func<IEnumerable<PaletteItem>> source)
    : ReactiveObject
{
    private static readonly char[] WordSeparators = [' ', '/', '·', ':', '.', '-', '_'];
    private string _query = string.Empty;
    private bool _isOpen;
    private int _selectedIndex = -1;

    public ObservableCollection<PaletteItem> Results { get; } = [];

    public string Query
    {
        get => _query;
        set
        {
            if (_query == value)
                return;
            this.RaiseAndSetIfChanged(ref _query, value);
            Refresh();
        }
    }

    public bool IsOpen
    {
        get => _isOpen;
        private set => this.RaiseAndSetIfChanged(ref _isOpen, value);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set => this.RaiseAndSetIfChanged(ref _selectedIndex, value);
    }

    public void Open()
    {
        _query = string.Empty;
        this.RaisePropertyChanged(nameof(Query));
        Refresh();
        IsOpen = true;
    }

    public void Close() => IsOpen = false;

    public void Move(int delta) =>
        SelectedIndex =
            Results.Count == 0 ? -1 : (SelectedIndex + delta + Results.Count) % Results.Count;

    public async Task ExecuteSelectedAsync()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Results.Count)
            return;
        var item = Results[SelectedIndex];
        Close();
        await item.Execute();
    }

    internal static int Score(string title, string query)
    {
        if (query.Length == 0)
            return 1;
        if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 300;
        var words = title.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries);
        if (words.Any(word => word.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            return 200;
        if (title.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 100;
        var position = 0;
        foreach (var character in title)
            if (
                position < query.Length
                && char.ToLowerInvariant(character) == char.ToLowerInvariant(query[position])
            )
                position++;
        return position == query.Length ? 50 : 0;
    }

    private void Refresh()
    {
        var ranked = source()
            .Select(item => (Item: item, Score: Score(item.Title, Query)))
            .Where(entry => entry.Score > 0)
            .OrderByDescending(entry => entry.Score)
            .ThenBy(entry => entry.Item.Title, StringComparer.OrdinalIgnoreCase)
            .Take(50)
            .Select(entry => entry.Item)
            .ToArray();
        Results.Clear();
        foreach (var item in ranked)
            Results.Add(item);
        SelectedIndex = Results.Count > 0 ? 0 : -1;
    }
}

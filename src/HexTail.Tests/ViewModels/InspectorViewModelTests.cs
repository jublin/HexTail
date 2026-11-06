using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class InspectorViewModelTests
{
    private static async Task<(
        MainWindowViewModel Owner,
        FileTabViewModel File,
        string Path,
        Avalonia.Controls.Window Window
    )> OpenAsync(params Line[] lines)
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var owner);
        await owner.OpenPathsCommand.Execute([path]);
        var file = owner.SelectedFile!;
        file.Model.Buffer.Append(lines);
        file.SyncViews();
        return (owner, file, path, window);
    }

    [AvaloniaFact]
    public async Task WithNoSelectionTheInspectorHasNoLine()
    {
        var (owner, _, path, window) = await OpenAsync(new Line("a"));
        try
        {
            owner.Inspector.Sync();

            Assert.False(owner.Inspector.HasLine);
            Assert.Equal("No row selected", owner.Inspector.Title);
            Assert.Empty(owner.Inspector.Fields);
            Assert.Null(owner.Inspector.JsonText);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SelectingAStructuredRowListsItsFieldsInOrderAndBuildsJson()
    {
        var fields = new Dictionary<string, string>
        {
            ["level"] = "error",
            ["msg"] = "disk full",
            ["db.host"] = "a1",
        };
        var line = new Line("level=error msg=\"disk full\" db.host=a1", fields);
        var (owner, file, path, window) = await OpenAsync(new Line("first"), line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();

            Assert.True(owner.Inspector.HasLine);
            Assert.Equal("Line 2", owner.Inspector.Title);
            Assert.Equal(line.Raw, owner.Inspector.RawText);
            Assert.Equal(
                ["level", "msg", "db.host"],
                owner.Inspector.Fields.Select(field => field.Key)
            );
            Assert.Equal("disk full", owner.Inspector.Fields[1].Value);
            Assert.True(owner.Inspector.CanCopyJson);
            using var json = System.Text.Json.JsonDocument.Parse(owner.Inspector.JsonText!);
            Assert.Equal("a1", json.RootElement.GetProperty("db.host").GetString());
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task APlainTextRowShowsRawTextAndNoFields()
    {
        var line = new Line("just text");
        var (owner, file, path, window) = await OpenAsync(line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();

            Assert.Equal("just text", owner.Inspector.RawText);
            Assert.False(owner.Inspector.HasFields);
            Assert.False(owner.Inspector.CanCopyJson);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ToggleFlipsOpenAndIsShownNeedsASource()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.Inspector.Toggle();
            Assert.True(owner.Inspector.IsOpen);
            Assert.False(owner.Inspector.IsShown); // no source selected

            var path = Path.GetTempFileName();
            await owner.OpenPathsCommand.Execute([path]);
            owner.Inspector.Sync();
            Assert.True(owner.Inspector.IsShown);

            owner.Inspector.Toggle();
            Assert.False(owner.Inspector.IsOpen);
            File.Delete(path);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public void WidthIsClampedToItsRange()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.Inspector.Width = 10;
            Assert.Equal(InspectorViewModel.MinWidth, owner.Inspector.Width);
            owner.Inspector.Width = 5000;
            Assert.Equal(InspectorViewModel.MaxWidth, owner.Inspector.Width);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task FieldActionsAddLiteralCaseSensitiveSourceRules()
    {
        var line = new Line(
            "level=error msg=boom",
            new Dictionary<string, string> { ["level"] = "error", ["msg"] = "boom" }
        );
        var (owner, file, path, window) = await OpenAsync(line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();
            var level = owner.Inspector.Fields[0];

            await owner.Inspector.ApplyFieldRuleAsync(level, RuleAction.ShowOnly);
            await owner.Inspector.ApplyFieldRuleAsync(owner.Inspector.Fields[1], RuleAction.Hide);
            await owner.Inspector.ApplyFieldRuleAsync(level, RuleAction.Highlight);

            Assert.Collection(
                file.Model.Rules,
                rule =>
                {
                    Assert.Equal("error", rule.Pattern);
                    Assert.Equal(RuleAction.ShowOnly, rule.Action);
                    Assert.Equal(RuleScope.Source, rule.Scope);
                    Assert.Equal(MatchMode.Literal, rule.Mode);
                    Assert.True(rule.CaseSensitive);
                },
                rule =>
                {
                    Assert.Equal("boom", rule.Pattern);
                    Assert.Equal(RuleAction.Hide, rule.Action);
                },
                rule => Assert.Equal(RuleAction.Highlight, rule.Action)
            );
            Assert.Null(owner.Inspector.Error);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task AFieldWithoutAValueCannotBecomeARule()
    {
        var line = new Line("k=", new Dictionary<string, string> { ["k"] = "" });
        var (owner, file, path, window) = await OpenAsync(line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();

            await owner.Inspector.ApplyFieldRuleAsync(owner.Inspector.Fields[0], RuleAction.Hide);

            Assert.Empty(file.Model.Rules);
            Assert.Equal("This field has no value to match.", owner.Inspector.Error);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ContextListsTheTrueNeighboursOfTheSelectedRowAndClickingMovesTheSelection()
    {
        var lines = Enumerable.Range(0, 20).Select(i => new Line($"row {i}")).ToArray();
        var (owner, file, path, window) = await OpenAsync(lines);
        try
        {
            owner.State.SelectLine(file.Model, lines[10]);
            owner.Inspector.ContextOpen = true;
            owner.Inspector.Sync();

            // Defaults are 3 lines above and 10 below; the file ends at row 19.
            Assert.Equal(
                Enumerable.Range(7, 13).Select(i => $"row {i}"),
                owner.Inspector.ContextRows.Select(row => row.Text)
            );
            Assert.True(owner.Inspector.ContextRows.Single(row => row.IsSelected).Text == "row 10");

            owner.Inspector.SelectContextRow(owner.Inspector.ContextRows[0]);
            owner.Inspector.Sync();

            Assert.Equal(7, file.Model.SelectedLine);
            Assert.Equal("row 7", owner.Inspector.ContextRows.Single(r => r.IsSelected).Text);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ContextRowsAreEmptyWhileTheContextToggleIsOff()
    {
        var lines = Enumerable.Range(0, 5).Select(i => new Line($"row {i}")).ToArray();
        var (owner, file, path, window) = await OpenAsync(lines);
        try
        {
            owner.State.SelectLine(file.Model, lines[2]);
            owner.Inspector.Sync();

            Assert.False(owner.Inspector.ContextOpen);
            Assert.Empty(owner.Inspector.ContextRows);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ARolloverShiftingTheSelectedRowKeepsItsFieldsAndError()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.Settings.MaxLinesValue = 1_000;
            await UiWait.Until(() => owner.State.Settings.MaxLines == 1_000);
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var structured = new Line("k=v", new Dictionary<string, string> { ["k"] = "v" });
            var lines = Enumerable.Range(0, 998).Select(i => new Line($"row {i}")).ToList();
            lines.Add(structured);
            lines.Add(new Line("last"));
            file.Model.Buffer.Append(lines);
            file.SyncViews();
            owner.State.SelectLine(file.Model, structured);
            owner.Inspector.Sync();
            var field = owner.Inspector.Fields[0];
            owner.Inspector.Error = "copy failed";
            Assert.Equal("Line 999", owner.Inspector.Title);

            file.Model.Buffer.Append(Enumerable.Range(0, 5).Select(i => new Line($"new {i}")));
            owner.Inspector.Sync();

            Assert.Same(field, owner.Inspector.Fields[0]);
            Assert.Equal("Line 994", owner.Inspector.Title);
            Assert.Equal("copy failed", owner.Inspector.Error);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task TurningContextOffAgainClearsTheRows()
    {
        var lines = Enumerable.Range(0, 5).Select(i => new Line($"row {i}")).ToArray();
        var (owner, file, path, window) = await OpenAsync(lines);
        try
        {
            owner.State.SelectLine(file.Model, lines[2]);
            owner.Inspector.ContextOpen = true;
            owner.Inspector.Sync();
            Assert.Equal(5, owner.Inspector.ContextRows.Count);

            owner.Inspector.ContextOpen = false;
            owner.Inspector.Sync();

            Assert.False(owner.Inspector.ContextOpen);
            Assert.Empty(owner.Inspector.ContextRows);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ToggleBookmarkCommandFlipsIsBookmarkedForTheSelectedRow()
    {
        var line = new Line("k=v", new Dictionary<string, string> { ["k"] = "v" });
        var (owner, file, path, window) = await OpenAsync(new Line("first"), line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();
            Assert.False(owner.Inspector.IsBookmarked);

            await owner.Inspector.ToggleBookmarkCommand.Execute();
            owner.Inspector.Sync();

            Assert.True(owner.Inspector.IsBookmarked);
            Assert.Equal([1], file.Model.Bookmarks);
            Assert.Equal("Line 2", owner.Inspector.Title);
            Assert.Equal("k=v", owner.Inspector.RawText);

            await owner.Inspector.ToggleBookmarkCommand.Execute();
            owner.Inspector.Sync();

            Assert.False(owner.Inspector.IsBookmarked);
            Assert.Empty(file.Model.Bookmarks);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ToggleBookmarkWithNoSelectedRowDoesNothing()
    {
        var (owner, file, path, window) = await OpenAsync(new Line("a"));
        try
        {
            owner.Inspector.Sync();

            owner.Inspector.ToggleBookmarkOnSelection();

            Assert.Empty(file.Model.Bookmarks);
            Assert.False(owner.Inspector.IsBookmarked);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }
}

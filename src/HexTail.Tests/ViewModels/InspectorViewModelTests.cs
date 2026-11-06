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
}

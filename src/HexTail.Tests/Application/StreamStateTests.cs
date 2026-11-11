using HexTail.Application;
using HexTail.Domain;
using HexTail.Persistence;
using HexTail.Tailing;

namespace HexTail.Tests.Application;

public sealed class StreamStateTests
{
    private sealed class NullTailer : ILogTailer
    {
        public string SourceId => "t";
        public string DisplayName => "t";
        public Task Completion => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FileTabState Tab(string id, string path, ILogParser? parser = null) =>
        new(id, path, new FileBuffer(), parser ?? new PlainTextParser(), new NullTailer());

    private static StreamSettings Definition(params (string Key, string Color)[] members) =>
        new("s1", "Both", [.. members.Select(m => new StreamMember(m.Key, m.Color))]);

    private static StreamState NewState() =>
        new(Definition(), TimeSpan.FromSeconds(5), () => DateTimeOffset.UtcNow);

    [Fact]
    public void SetMembers_MapsOpenMembersByOrderAndListsTheRestAsMissing()
    {
        var a = Tab("file-1", "/logs/a.log");
        var c = Tab("file-3", "/logs/c.log");
        var definition = Definition(
            ("file:/logs/a.log", "#111111"),
            ("file:/logs/b.log", "#222222"),
            ("file:/logs/c.log", "#333333")
        );
        var state = NewState();

        state.SetMembers(
            definition,
            new Dictionary<string, FileTabState>
            {
                ["file:/logs/c.log"] = c,
                ["file:/logs/a.log"] = a,
            }
        );

        Assert.Equal(["file-1", "file-3"], state.MemberIds);
        Assert.Equal(["file:/logs/b.log"], state.MissingMembers);
        Assert.Equal("#111111", state.ColorFor("file-1"));
        Assert.Equal("#333333", state.ColorFor("file-3"));
        Assert.Equal("a.log", state.DisplayNameFor("file-1"));
        Assert.True(state.NeedsRebuild);
    }

    [Fact]
    public void SetMembers_KeysThatMapToTheSameTab_ListTheMemberOnce()
    {
        var a = Tab("file-1", "/logs/a.log");
        var b = Tab("file-2", "/logs/b.log");
        var definition = Definition(
            ("file:/logs/a.log", "#111111"),
            ("file:/LOGS/A.log", "#222222"),
            ("file:/logs/b.log", "#333333")
        );
        var state = NewState();

        state.SetMembers(
            definition,
            new Dictionary<string, FileTabState>(StringComparer.OrdinalIgnoreCase)
            {
                ["file:/logs/a.log"] = a,
                ["file:/logs/b.log"] = b,
            }
        );

        Assert.Equal(["file-1", "file-2"], state.MemberIds);
        Assert.Empty(state.MissingMembers);
    }

    [Fact]
    public void UnknownSources_FallBackToANeutralColourAndTheirId()
    {
        var state = NewState();

        Assert.Equal(StreamState.Fallback, state.ColorFor("nope"));
        Assert.Equal(StreamState.Fallback, state.ColorFor(null));
        Assert.Equal("nope", state.DisplayNameFor("nope"));
    }

    [Fact]
    public void DetachMember_MovesItToMissing_ButRowsKeepTheirColour()
    {
        var a = Tab("file-1", "/logs/a.log");
        var b = Tab("file-2", "/logs/b.log");
        var definition = Definition(
            ("file:/logs/a.log", "#111111"),
            ("file:/logs/b.log", "#222222")
        );
        var state = NewState();
        state.SetMembers(
            definition,
            new Dictionary<string, FileTabState>
            {
                ["file:/logs/a.log"] = a,
                ["file:/logs/b.log"] = b,
            }
        );

        state.DetachMember(a);

        Assert.Equal(["file-2"], state.MemberIds);
        Assert.Equal(["file:/logs/a.log"], state.MissingMembers);
        Assert.Equal("#111111", state.ColorFor("file-1"));
        Assert.Equal("a.log", state.DisplayNameFor("file-1"));
    }

    [Fact]
    public void HiddenKeys_SurfaceAsHiddenTabIds_AndDropWhenTheMemberLeavesTheDefinition()
    {
        var a = Tab("file-1", "/logs/a.log");
        var b = Tab("file-2", "/logs/b.log");
        var definition = Definition(("file:/logs/a.log", "#1"), ("file:/logs/b.log", "#2"));
        var open = new Dictionary<string, FileTabState>
        {
            ["file:/logs/a.log"] = a,
            ["file:/logs/b.log"] = b,
        };
        var state = NewState();
        state.SetMembers(definition, open);

        state.SetHiddenKeys(["file:/logs/b.log", "file:/logs/unknown.log"]);

        Assert.Equal(["file-2"], state.HiddenSources);
        Assert.Equal(["file:/logs/b.log"], state.HiddenKeys);
        state.SetMembers(Definition(("file:/logs/a.log", "#1"), ("file:/logs/c.log", "#3")), open);
        Assert.Empty(state.HiddenSources);
        Assert.Empty(state.HiddenKeys);
    }

    [Fact]
    public void AnyMemberStructured_FollowsTheOpenMembers()
    {
        var plain = Tab("file-1", "/logs/a.log");
        var jsonl = Tab("file-2", "/logs/b.jsonl", new JsonlParser());
        var definition = Definition(("file:/logs/a.log", "#1"), ("file:/logs/b.jsonl", "#2"));
        var state = NewState();
        var streamTab = new FileTabState(
            LogSourceDescriptor.Stream(definition),
            new FileBuffer(),
            new PlainTextParser(),
            new NullTailer()
        )
        {
            Stream = state,
        };

        state.SetMembers(
            definition,
            new Dictionary<string, FileTabState> { ["file:/logs/a.log"] = plain }
        );
        Assert.False(state.AnyMemberStructured);
        Assert.True(streamTab.IsStructured);

        state.SetMembers(
            definition,
            new Dictionary<string, FileTabState>
            {
                ["file:/logs/a.log"] = plain,
                ["file:/logs/b.jsonl"] = jsonl,
            }
        );
        Assert.True(state.AnyMemberStructured);
        Assert.True(streamTab.IsStructured);
    }

    [Fact]
    public void StreamDescriptor_HasItsOwnKindAndListsTheMembers()
    {
        var descriptor = LogSourceDescriptor.Stream(
            Definition(("file:/logs/a.log", "#1"), ("elastic:src-9", "#2"))
        );

        Assert.Equal("stream:s1", descriptor.Id);
        Assert.Equal(LogSourceKind.Stream, descriptor.Kind);
        Assert.Equal("Both", descriptor.DisplayName);
        Assert.Contains("/logs/a.log", descriptor.ToolTip);
        Assert.Contains("src-9", descriptor.ToolTip);
        Assert.Null(descriptor.LocalPath);
    }

    [Fact]
    public void MemberKey_IsThePathForFilesAndTheSourceIdForElastic()
    {
        var file = Tab("file-1", "/logs/a.log");
        var elastic = new FileTabState(
            new LogSourceDescriptor(
                "src-9",
                LogSourceKind.Elastic,
                "Ops",
                "tip",
                ElasticSourceId: "src-9"
            ),
            new FileBuffer(),
            new PlainTextParser(),
            new NullTailer()
        );

        Assert.Equal("file:/logs/a.log", AppState.MemberKey(file));
        Assert.Equal("elastic:src-9", AppState.MemberKey(elastic));
    }

    [Fact]
    public void Normalize_FixesNamesDuplicatesAndColoursWithoutFailing()
    {
        var normalized = StreamSettings.Normalize([
            new StreamSettings(
                "a",
                "  ",
                [
                    new StreamMember("file:/x", "#abc"),
                    new StreamMember("file:/x", "#def"),
                    new StreamMember("", "#000"),
                    new StreamMember("elastic:q", ""),
                ]
            ),
            new StreamSettings("a", "Duplicate id", []),
            new StreamSettings("", "No id", []),
            null,
            new StreamSettings("b", "Only one", [new StreamMember("file:/y", "#123")]),
        ]);

        Assert.Equal(["a", "b"], normalized.Select(s => s.Id));
        Assert.Equal("Stream", normalized[0].Name);
        Assert.Equal(["file:/x", "elastic:q"], normalized[0].Members.Select(m => m.Key));
        Assert.Equal("#abc", normalized[0].Members[0].Color);
        Assert.Equal(StreamPalette.ColorAt(1), normalized[0].Members[1].Color);
        Assert.Single(normalized[1].Members);
    }

    [Fact]
    public void Palette_HasEightDistinctColoursAndWraps()
    {
        Assert.Equal(8, StreamPalette.Colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(StreamPalette.ColorAt(0), StreamPalette.ColorAt(8));
    }
}

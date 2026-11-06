using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class LevelDetectorTests
{
    private static Line Fields(params (string Key, string Value)[] fields) =>
        new("raw", fields.ToDictionary(field => field.Key, field => field.Value));

    [Theory]
    [InlineData("level", "debug", LogLevels.Debug)]
    [InlineData("LEVEL", "WARNING", LogLevels.Warn)]
    [InlineData("severity", "critical", LogLevels.Error)]
    [InlineData("log.level", "Information", LogLevels.Info)]
    [InlineData("lvl", "trc", LogLevels.Trace)]
    [InlineData("level", "fatal", LogLevels.Error)]
    [InlineData("level", "whatever", LogLevels.None)]
    public void ParsedFields_AreMappedCaseInsensitively(
        string key,
        string value,
        LogLevels expected
    ) => Assert.Equal(expected, LevelDetector.Detect(Fields((key, value))));

    [Theory]
    [InlineData("2026-11-06 10:00:01 ERROR payment failed", LogLevels.Error)]
    [InlineData("2026-11-06 10:00:01 [WARN] slow query", LogLevels.Warn)]
    [InlineData("10:00:01 INFO started", LogLevels.Info)]
    [InlineData("[10:00:01 DBG] cache hit", LogLevels.Debug)]
    [InlineData("[10:00:01 WRN] retrying", LogLevels.Warn)]
    [InlineData("{\"x\":1} FATAL out of memory", LogLevels.Error)]
    [InlineData("TRACE entering handler", LogLevels.Trace)]
    public void PlainText_RecognizesUppercaseLevelTokens(string raw, LogLevels expected) =>
        Assert.Equal(expected, LevelDetector.Detect(new Line(raw)));

    [Theory]
    [InlineData("no error found")]
    [InlineData("info about the warning label")]
    [InlineData("   at Foo.Bar() in Foo.cs:line 12")]
    [InlineData("")]
    public void PlainText_DoesNotGuessFromLowercaseWords(string raw) =>
        Assert.Equal(LogLevels.None, LevelDetector.Detect(new Line(raw)));

    [Fact]
    public void ParsedLevel_WinsOverTokensInTheMessage() =>
        Assert.Equal(
            LogLevels.Info,
            LevelDetector.Detect(
                new Line("ERROR in text", new Dictionary<string, string> { ["level"] = "info" })
            )
        );

    [Fact]
    public void Allows_NeverHidesUnknownLevels_AndHonorsTheMask()
    {
        var error = new Line("ERROR boom");
        var info = new Line("INFO ok");
        var unknown = new Line("   at Foo.Bar()");
        var onlyErrors = LogLevels.Error;

        Assert.True(LevelDetector.Allows(LogLevels.All, info));
        Assert.True(LevelDetector.Allows(onlyErrors, error));
        Assert.False(LevelDetector.Allows(onlyErrors, info));
        Assert.True(LevelDetector.Allows(onlyErrors, unknown));
    }

    [Fact]
    public void Detect_IsStableAcrossRepeatedCalls()
    {
        var line = new Line("WARN once");
        Assert.Equal(LevelDetector.Detect(line), LevelDetector.Detect(line));
    }
}

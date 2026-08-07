using HexTailSharp.Domain;

namespace HexTailSharp.Tests.Domain;

public class ParserTests
{
    private readonly PlainTextParser _plain = new();
    private readonly LogfmtParser _logfmt = new();

    [Fact]
    public void PlainText_PreservesRawText_WithNoParsedFields()
    {
        var line = _plain.Parse("level=info msg=\"hello\"");

        Assert.Equal("level=info msg=\"hello\"", line.Raw);
        Assert.Null(line.ParsedFields);
    }

    [Fact]
    public void PlainText_EmptyString_IsNoOp()
    {
        var line = _plain.Parse(string.Empty);

        Assert.Equal(string.Empty, line.Raw);
        Assert.Null(line.ParsedFields);
    }

    [Fact]
    public void Logfmt_ParsesSimplePairs()
    {
        var line = _logfmt.Parse("level=info msg=hello count=42");

        Assert.Equal("level=info msg=hello count=42", line.Raw);
        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            line.ParsedFields
        );
        Assert.Equal(3, fields.Count);
        Assert.Equal("info", fields["level"]);
        Assert.Equal("hello", fields["msg"]);
        Assert.Equal("42", fields["count"]);
    }

    [Fact]
    public void Logfmt_ParsesEmptyValue()
    {
        var line = _logfmt.Parse("level=info msg=");

        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            line.ParsedFields
        );
        Assert.Equal(string.Empty, fields["msg"]);
    }

    [Fact]
    public void Logfmt_ParsesQuotedValueWithSpaces()
    {
        var line = _logfmt.Parse("msg=\"hello world\" level=info");

        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            line.ParsedFields
        );
        Assert.Equal("hello world", fields["msg"]);
        Assert.Equal("info", fields["level"]);
    }

    [Fact]
    public void Logfmt_ParsesEscapedQuotesAndBackslashes()
    {
        var line = _logfmt.Parse("msg=\"say \\\"hi\\\"\" path=\"c:\\\\tmp\"");

        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            line.ParsedFields
        );
        Assert.Equal("say \"hi\"", fields["msg"]);
        Assert.Equal("c:\\tmp", fields["path"]);
    }

    [Fact]
    public void Logfmt_ParsesEmptyQuotedValue()
    {
        var line = _logfmt.Parse("msg=\"\" level=warn");

        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            line.ParsedFields
        );
        Assert.Equal(string.Empty, fields["msg"]);
    }

    [Fact]
    public void Logfmt_DuplicateKeys_LastWins()
    {
        var line = _logfmt.Parse("k=1 k=2");

        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            line.ParsedFields
        );
        Assert.Equal("2", fields["k"]);
    }

    [Theory]
    [InlineData("just a plain sentence")]
    [InlineData("level=info oops msg=hello")] // token without '='
    [InlineData("msg=\"unterminated")] // unterminated quote
    [InlineData("msg=\"quoted\"garbage")] // junk after closing quote
    [InlineData("=novalue")] // empty key
    [InlineData("   ")] // whitespace only
    [InlineData("")] // empty line
    public void Logfmt_MalformedLine_TreatedAsPlainTextWithEmptyMap(string raw)
    {
        var line = _logfmt.Parse(raw);

        Assert.Equal(raw, line.Raw);
        var fields = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(
            line.ParsedFields
        );
        Assert.Empty(fields);
    }

    [Fact]
    public void Logfmt_NeverThrows_OnAdversarialInput()
    {
        var inputs = new[] { "\\", "\"", "=", "a=\\", "a=\"\\", "a=\"\"\"", "  =  ", "a==b" };

        foreach (var input in inputs)
        {
            var line = _logfmt.Parse(input);
            Assert.Equal(input, line.Raw);
        }
    }
}

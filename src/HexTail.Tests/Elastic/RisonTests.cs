using HexTail.Elastic;

namespace HexTail.Tests.Elastic;

public sealed class RisonTests
{
    [Fact]
    public void ParsesObjectsArraysStringsAndLiterals()
    {
        Assert.True(Rison.TryParse("(a:1,b:!t,c:!f,d:!n,e:'x y',f:!(1,two,'3'))", out var value));

        var map = Assert.IsType<Dictionary<string, object?>>(value);
        Assert.Equal(1d, map["a"]);
        Assert.Equal(true, map["b"]);
        Assert.Equal(false, map["c"]);
        Assert.Null(map["d"]);
        Assert.Equal("x y", map["e"]);
        Assert.Equal(
            new object?[] { 1d, "two", "3" },
            Assert.IsType<List<object?>>(map["f"]).ToArray()
        );
    }

    [Fact]
    public void ParsesNestedObjectsAndEscapedQuotes()
    {
        Assert.True(Rison.TryParse("(q:(language:kuery,query:'name : !'Bob!''))", out var value));

        var query = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<Dictionary<string, object?>>(value)["q"]
        );
        Assert.Equal("name : 'Bob'", query["query"]);
    }

    [Fact]
    public void ParsesNestedArrays()
    {
        Assert.True(Rison.TryParse("!(!('@timestamp',desc),!())", out var value));

        var outer = Assert.IsType<List<object?>>(value);
        Assert.Equal(2, outer.Count);
        Assert.Equal(
            new object?[] { "@timestamp", "desc" },
            Assert.IsType<List<object?>>(outer[0]).ToArray()
        );
        Assert.Empty(Assert.IsType<List<object?>>(outer[1]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("(a:1)trailing")]
    [InlineData("(a:1,)")]
    [InlineData("!x")]
    [InlineData("(a:")]
    [InlineData("(a:1")]
    [InlineData("!(1,2")]
    [InlineData("'unterminated")]
    public void RejectsMalformedText(string text) => Assert.False(Rison.TryParse(text, out _));
}

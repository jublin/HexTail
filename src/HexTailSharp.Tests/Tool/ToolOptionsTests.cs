using HexTailSharp.Tool;

namespace HexTailSharp.Tests.Tool;

public sealed class ToolOptionsTests
{
    [Fact]
    public void TryParse_uses_defaults()
    {
        Assert.True(ToolOptions.TryParse([], out var options, out _));
        Assert.Equal(5178, options!.Port);
        Assert.False(options.NoBrowser);
        Assert.Equal("http://localhost:5178/", options.Url.ToString());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("invalid")]
    public void TryParse_rejects_invalid_ports(string port)
    {
        Assert.False(ToolOptions.TryParse(["--port", port], out _, out var error));
        Assert.NotNull(error);
    }
}

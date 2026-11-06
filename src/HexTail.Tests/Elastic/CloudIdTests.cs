using HexTail.Elastic;

namespace HexTail.Tests.Elastic;

public sealed class CloudIdTests
{
    private static string Encode(string payload) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

    [Fact]
    public void DecodesElasticsearchAndKibanaHosts()
    {
        var id = $"my-deployment:{Encode("us-east-1.aws.found.io$es123$kb456")}";

        Assert.True(CloudId.TryDecode(id, out var endpoints));
        Assert.Equal("https://es123.us-east-1.aws.found.io", endpoints!.ElasticsearchUrl);
        Assert.Equal("https://kb456.us-east-1.aws.found.io", endpoints.KibanaUrl);
    }

    [Fact]
    public void KeepsAnExplicitPortAndAllowsAMissingKibanaId()
    {
        var id = $"x:{Encode("host.example.com:9243$es1")}";

        Assert.True(CloudId.TryDecode(id, out var endpoints));
        Assert.Equal("https://es1.host.example.com:9243", endpoints!.ElasticsearchUrl);
        Assert.Null(endpoints.KibanaUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-colon")]
    [InlineData("name:!!!notbase64!!!")]
    [InlineData("name:aG9zdA==")] // "host": no $ separators
    public void RejectsMalformedIds(string value)
    {
        Assert.False(CloudId.TryDecode(value, out var endpoints));
        Assert.Null(endpoints);
    }

    [Fact]
    public void TrimsWhitespace()
    {
        var id = $"  n:{Encode("h.io$e$k")}  ";

        Assert.True(CloudId.TryDecode(id, out _));
    }
}

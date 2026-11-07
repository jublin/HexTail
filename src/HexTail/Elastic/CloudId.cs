using System.Text;
using System.Text.RegularExpressions;

namespace HexTail.Elastic;

public sealed record CloudIdEndpoints(string ElasticsearchUrl, string? KibanaUrl);

/// <summary>
/// Decodes an Elastic Cloud ID (<c>name:base64(host$elasticsearchId$kibanaId)</c>). Credentials are
/// later sent to the decoded URLs, so every segment is validated strictly.
/// </summary>
public static partial class CloudId
{
    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.\-]*[A-Za-z0-9])?(:\d{1,5})?$")]
    private static partial Regex HostSegment();

    [GeneratedRegex(@"^[A-Za-z0-9\-_]+$")]
    private static partial Regex IdSegment();

    public static bool TryDecode(string value, out CloudIdEndpoints? endpoints)
    {
        endpoints = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        var colon = text.LastIndexOf(':');
        if (colon < 0 || colon == text.Length - 1)
            return false;
        var base64 = text[(colon + 1)..];
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = decoded.Split('$');
        if (parts.Length < 2)
            return false;
        var host = parts[0];
        if (!HostSegment().IsMatch(host) || host.Contains(".."))
            return false;
        if (!IdSegment().IsMatch(parts[1]))
            return false;
        var elasticsearch = Build(parts[1], host);
        if (elasticsearch is null)
            return false;

        string? kibana = null;
        if (parts.Length > 2 && parts[2].Length > 0)
        {
            if (!IdSegment().IsMatch(parts[2]))
                return false;
            kibana = Build(parts[2], host);
            if (kibana is null)
                return false;
        }

        endpoints = new CloudIdEndpoints(elasticsearch, kibana);
        return true;
    }

    private static string? Build(string id, string host)
    {
        var url = $"https://{id}.{host}";
        var hostName = host.Contains(':') ? host[..host.IndexOf(':')] : host;
        return
            Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && uri.UserInfo.Length == 0
            && uri.AbsolutePath == "/"
            && uri.Query.Length == 0
            && uri.Fragment.Length == 0
            && uri.Host.EndsWith(hostName, StringComparison.OrdinalIgnoreCase)
            ? url
            : null;
    }
}

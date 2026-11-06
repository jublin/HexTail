using System.Text;

namespace HexTail.Elastic;

public sealed record CloudIdEndpoints(string ElasticsearchUrl, string? KibanaUrl);

/// <summary>Decodes an Elastic Cloud ID (<c>name:base64(host$elasticsearchId$kibanaId)</c>).</summary>
public static class CloudId
{
    public static bool TryDecode(string value, out CloudIdEndpoints? endpoints)
    {
        endpoints = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        var colon = text.LastIndexOf(':');
        if (colon < 0 || colon == text.Length - 1)
            return false;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text[(colon + 1)..]));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = decoded.Split('$');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            return false;
        var host = parts[0];
        var elasticsearch = $"https://{parts[1]}.{host}";
        var kibana = parts.Length > 2 && parts[2].Length > 0 ? $"https://{parts[2]}.{host}" : null;
        if (!Uri.TryCreate(elasticsearch, UriKind.Absolute, out _))
            return false;
        endpoints = new CloudIdEndpoints(elasticsearch, kibana);
        return true;
    }
}

namespace HexTail.Elastic;

/// <summary>What counts as a usable Kibana or Elasticsearch address, and when two addresses are the same server.</summary>
internal static class ElasticUrls
{
    /// <summary>An absolute http or https address. Credentials belong to the login method, not the address.</summary>
    internal static bool IsHttp(string? value) =>
        TryParse(value, out var uri) && uri.Scheme is "http" or "https";

    /// <summary>True when the address carries a user name or password in front of the host.</summary>
    internal static bool HasUserInfo(string? value) =>
        TryParse(value, out var uri) && uri.UserInfo.Length > 0;

    /// <summary>
    /// True when both addresses name the same scheme, host and port. The path, a trailing slash
    /// and letter case do not matter; an address that does not parse never matches.
    /// </summary>
    internal static bool SameOrigin(string? left, string? right) =>
        TryParse(left, out var a)
        && TryParse(right, out var b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    private static bool TryParse(string? value, out Uri uri)
    {
        if (value is not null && Uri.TryCreate(value.Trim(), UriKind.Absolute, out var parsed))
        {
            uri = parsed;
            return true;
        }
        uri = null!;
        return false;
    }
}

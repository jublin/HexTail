using HexTailSharp.Security;

namespace HexTailSharp.Tests.Support;

internal sealed class InMemoryCredentialVault : ICredentialVault
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

    public Exception? GetError { get; set; }
    public Exception? SetError { get; set; }
    public Exception? DeleteError { get; set; }

    public string? Get(string connectionId)
    {
        if (GetError is not null)
            throw GetError;
        return _secrets.GetValueOrDefault(connectionId);
    }

    public void Set(string connectionId, string secret)
    {
        if (SetError is not null)
            throw SetError;
        _secrets[connectionId] = secret;
    }

    public void Delete(string connectionId)
    {
        if (DeleteError is not null)
            throw DeleteError;
        _secrets.Remove(connectionId);
    }
}

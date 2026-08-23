namespace HexTailSharp.Security;

public interface ICredentialVault
{
    string? Get(string connectionId);
    void Set(string connectionId, string secret);
    void Delete(string connectionId);
}

public sealed class CredentialVaultUnavailableException(string message, Exception innerException)
    : Exception(message, innerException);

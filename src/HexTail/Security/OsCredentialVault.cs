using GnomeStack.Os.Secrets;

namespace HexTail.Security;

internal sealed class OsCredentialVault : ICredentialVault
{
    internal const string ServiceName = "HexTailSharp";

    internal static string Account(string connectionId) => connectionId;

    public string? Get(string connectionId) =>
        Execute(connectionId, () => OsSecretVault.GetSecret(ServiceName, Account(connectionId)));

    public void Set(string connectionId, string secret)
    {
        Validate(connectionId, secret);
        ExecuteVoid(
            connectionId,
            () =>
            {
                OsSecretVault.SetSecret(ServiceName, Account(connectionId), secret);
            }
        );
    }

    public void Delete(string connectionId) =>
        ExecuteVoid(
            connectionId,
            () =>
            {
                OsSecretVault.DeleteSecret(ServiceName, Account(connectionId));
            }
        );

    private static string? Execute(string connectionId, Func<string?> action)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A connection ID is required.", nameof(connectionId));
        try
        {
            return action();
        }
        catch (CredentialVaultUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not ArgumentException)
        {
            throw new CredentialVaultUnavailableException(
                "The operating-system credential vault is unavailable.",
                exception
            );
        }
    }

    private static void ExecuteVoid(string connectionId, Action action)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A connection ID is required.", nameof(connectionId));
        try
        {
            action();
        }
        catch (Exception exception) when (exception is not ArgumentException)
        {
            throw new CredentialVaultUnavailableException(
                "The operating-system credential vault is unavailable.",
                exception
            );
        }
    }

    private static void Validate(string connectionId, string secret)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            throw new ArgumentException("A connection ID is required.", nameof(connectionId));
        if (string.IsNullOrWhiteSpace(secret))
            throw new ArgumentException("A secret is required.", nameof(secret));
    }
}

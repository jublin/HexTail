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
                try
                {
                    OsSecretVault.DeleteSecret(ServiceName, Account(connectionId));
                }
                catch (InvalidOperationException exception) when (IsNotFound(exception))
                {
                    // Deleting a secret that was never stored is a no-op.
                }
            }
        );

    // Windows reports a missing credential as ERROR_NOT_FOUND (1168) from DeleteCredential.
    private static bool IsNotFound(InvalidOperationException exception) =>
        OperatingSystem.IsWindows()
        && exception.Message.Contains("error code 1168", StringComparison.Ordinal);

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

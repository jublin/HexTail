using HexTailSharp.Security;

namespace HexTailSharp.Tests.Security;

public sealed class OsCredentialVaultTests
{
    [Fact]
    public void ConnectionKey_UsesStableConnectionId()
    {
        Assert.Equal("HexTailSharp", OsCredentialVault.ServiceName);
        Assert.Equal("connection-42", OsCredentialVault.Account("connection-42"));
    }
}

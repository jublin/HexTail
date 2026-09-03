using HexTail.Persistence;

namespace HexTail.Tests.Support;

internal sealed class TestPersistence : IAppPersistence
{
    public AppConfig? Config { get; private set; } = new();
    public Exception? SaveError { get; set; }
    public int SaveCount { get; private set; }

    public ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(Config);

    public ValueTask SaveAsync(AppConfig config, CancellationToken cancellationToken = default)
    {
        if (SaveError is not null)
            throw SaveError;
        SaveCount++;
        Config = config;
        return ValueTask.CompletedTask;
    }
}

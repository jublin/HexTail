namespace HexTail.Persistence;

public interface IAppPersistence
{
    ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default);
    ValueTask SaveAsync(AppConfig config, CancellationToken cancellationToken = default);
}

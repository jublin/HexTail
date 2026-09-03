using System.Text.Json;

namespace HexTail.Persistence;

public sealed class JsonFileAppPersistence : IAppPersistence
{
    public const string FileName = "session.json";

    public JsonFileAppPersistence(string? path = null)
    {
        Path =
            path
            ?? System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "HexTailSharp",
                FileName
            );
    }

    public string Path { get; }

    public async ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(Path))
            return null;

        try
        {
            var json = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
            return AppConfigJson.Deserialize(json);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async ValueTask SaveAsync(
        AppConfig config,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(config);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (directory is null)
            throw new InvalidOperationException("The persistence path must include a directory.");

        Directory.CreateDirectory(directory);
        var temporaryPath = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                    temporaryPath,
                    AppConfigJson.Serialize(config),
                    cancellationToken
                )
                .ConfigureAwait(false);
            File.Move(temporaryPath, Path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}

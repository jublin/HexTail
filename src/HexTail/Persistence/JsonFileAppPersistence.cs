using System.Globalization;
using System.Text.Json;

namespace HexTail.Persistence;

public sealed class JsonFileAppPersistence : IAppPersistence
{
    public const string FileName = "session.json";
    private const string UnreadableBackup = "session.unreadable.bak";

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

        string? json = null;
        try
        {
            json = await File.ReadAllTextAsync(Path, cancellationToken).ConfigureAwait(false);
            if (RuleMigration.NeedsMigration(json))
                BackUp("session.v1.json.bak");
            else if (RuleMigration.IsFutureVersion(json))
                BackUp(UnreadableBackup);
            return AppConfigJson.Deserialize(json);
        }
        catch (Exception exception) when (RuleMigration.IsUnreadableTree(exception))
        {
            // Keep the unreadable file: the next save would otherwise overwrite it.
            if (json is not null)
                BackUp(UnreadableBackup);
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

    /// <summary>
    /// Copies the session file next to itself without ever overwriting a backup: the plain name when
    /// free, otherwise a timestamped name, and nothing when a backup with the same content exists.
    /// </summary>
    private void BackUp(string fileName)
    {
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path)!;
            var stem = System.IO.Path.GetFileNameWithoutExtension(fileName);
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            var content = File.ReadAllBytes(Path);
            // A timestamped name only collides within one second, so look at every earlier backup too.
            foreach (var existing in Directory.GetFiles(directory, $"{stem}*.bak"))
            {
                if (File.ReadAllBytes(existing).AsSpan().SequenceEqual(content))
                    return;
            }
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var name = attempt switch
                {
                    0 => fileName,
                    1 => $"{stem}.{stamp}.bak",
                    _ => $"{stem}.{stamp}-{attempt}.bak",
                };
                var candidate = System.IO.Path.Combine(directory, name);
                if (!File.Exists(candidate))
                {
                    File.Copy(Path, candidate);
                    return;
                }
                if (File.ReadAllBytes(candidate).AsSpan().SequenceEqual(content))
                    return;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

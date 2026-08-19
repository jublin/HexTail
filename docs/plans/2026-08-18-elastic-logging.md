# Elastic Logging Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add manually configured Elastic log sources that use Kibana for data-view metadata, Elasticsearch for documents, native credential storage for authenticated connections, and the existing HexTail tab/search/rendering workflow.

**Architecture:** Preserve one ingestion path by making the tailing event stream source-neutral and emitting `Line` objects from both local and Elastic tailers. Keep non-secret Elastic configuration in the existing JSON session, store only passwords/API keys in the OS vault, and put Elastic HTTP behavior behind one client boundary so deterministic handlers can test every request without a live cluster.

**Tech Stack:** .NET 10, C# 14, Avalonia 12.1.1, ReactiveUI, `System.Net.Http`, `System.Text.Json`, Polly 8.7.0, GnomeStack.Os.Secrets 0.1.3, xUnit v3, Avalonia.Headless.XUnit.

**Spec:** `docs/superpowers/specs/2026-08-18-elastic-logging-design.md`

## Global Constraints

- Keep the feature read-only. Do not add create/update/delete calls for Elastic documents or Kibana data views.
- Keep TLS validation enabled and never serialize a password or API key.
- Keep source discovery, custom queries, a grid renderer, merged tabs, and configurable timing out of this implementation.
- Use a five-minute initial lookback, two-second polling, thirty-second health checks, one-minute PIT lifetime, and 1,000-hit pages as code constants rather than user settings.
- Preserve the existing `OpenFiles` and `SelectedFilePath` JSON contract for old sessions.
- Reuse `FileBuffer`, `Search`, global labels/exclusions, context, expansion, follow state, and virtualized views.
- Add one focused failing check before each production change and commit once per task with the exact Conventional Commit message shown below.

---

### Task 1: Persist Elastic connections, sources, and remote-tab session state

**Files:**

- Create: `src/HexTailSharp/Persistence/ElasticSettings.cs`
- Modify: `src/HexTailSharp/Persistence/AppConfig.cs`
- Modify: `src/HexTailSharp.Tests/Persistence/JsonFileAppPersistenceTests.cs`
- Modify: `src/HexTailSharp.Tests/Application/AppStateTests.cs`

**Interfaces produced:** `ElasticAuthMode`, `ElasticConnectionSettings`, `ElasticSourceSettings`, `PersistedElasticTab`, and backward-compatible `AppConfig`/`AppSettings` properties consumed by later tasks.

- [ ] Add a failing JSON round-trip test that includes one Basic connection, one source, ordered output fields, one open remote tab, and `SelectedElasticSourceId`. Assert that the old-config test still produces empty Elastic collections.

```csharp
[Fact]
public void AppConfigJson_RoundTripsElasticSettingsWithoutSecretMaterial()
{
    var connection = new ElasticConnectionSettings
    {
        Id = "elastic-1",
        Name = "Production",
        KibanaUrl = "https://kibana.example/space/default/",
        ElasticsearchUrl = "https://elastic.example/",
        AuthMode = ElasticAuthMode.Basic,
        Username = "reader",
        DataViewId = "logs-view",
        DataViewTitle = "logs-*",
        TimeFieldName = "@timestamp",
        ServerField = "service.name.keyword",
        NamespaceField = "labels.namespace.keyword",
        OutputFields = ["@timestamp", "message"],
        Sources =
        [
            new ElasticSourceSettings
            {
                Id = "source-1",
                ServerValue = "Mystack1",
                NamespaceValue = "RhubarbPi",
            },
        ],
    };
    var json = AppConfigJson.Serialize(
        new AppConfig
        {
            Settings = new AppSettings { ElasticConnections = [connection] },
            OpenElasticTabs = [new PersistedElasticTab { SourceId = "source-1" }],
            SelectedElasticSourceId = "source-1",
        }
    );

    var restored = AppConfigJson.Deserialize(json);

    Assert.Equal(["@timestamp", "message"], restored.Settings.ElasticConnections[0].OutputFields);
    Assert.Equal("source-1", Assert.Single(restored.OpenElasticTabs).SourceId);
    Assert.Equal("source-1", restored.SelectedElasticSourceId);
    Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    Assert.DoesNotContain("api-key", json, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "FullyQualifiedName~JsonFileAppPersistenceTests|FullyQualifiedName~AppStateTests.AppConfigJson"`. Expected: failure because the Elastic persistence types and properties do not exist.

- [ ] Add the configuration types with required stable IDs and computed source names:

```csharp
public enum ElasticAuthMode { Anonymous, Basic, ApiKey }

public sealed record ElasticConnectionSettings
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string KibanaUrl { get; init; }
    public required string ElasticsearchUrl { get; init; }
    public ElasticAuthMode AuthMode { get; init; }
    public string? Username { get; init; }
    public string? DataViewId { get; init; }
    public string? DataViewTitle { get; init; }
    public string? TimeFieldName { get; init; }
    public string? ServerField { get; init; }
    public string? NamespaceField { get; init; }
    public List<string> OutputFields { get; init; } = [];
    public List<ElasticSourceSettings> Sources { get; init; } = [];
}

public sealed record ElasticSourceSettings
{
    public required string Id { get; init; }
    public required string ServerValue { get; init; }
    public required string NamespaceValue { get; init; }
    public string DisplayName => $"{ServerValue}-{NamespaceValue}";
}
```

- [ ] Add `List<ElasticConnectionSettings> ElasticConnections { get; init; } = [];` to `AppSettings`. Add `List<PersistedElasticTab> OpenElasticTabs { get; init; } = [];` and `string? SelectedElasticSourceId { get; init; }` to `AppConfig`. Give `PersistedElasticTab` the same searches/follow/context/selection properties and defaults as `PersistedFileTab`, plus required `SourceId`.

- [ ] Update `AppState.NormalizeSettings` to trim connection/source values, remove blank or duplicate IDs, preserve output-field order with `Distinct(StringComparer.Ordinal)`, and copy the normalized list into the returned `AppSettings`. Do not reject incomplete drafts here; the settings workflow validates before saving.

- [ ] Run the focused test command again. Expected: pass, including the legacy-session assertion.

- [ ] Commit with `rtk git add src/HexTailSharp/Persistence src/HexTailSharp.Tests/Persistence/JsonFileAppPersistenceTests.cs src/HexTailSharp.Tests/Application/AppStateTests.cs && rtk git commit -m "feat(elastic): persist connection and source settings"`.

---

### Task 2: Generalize the local tailer into the shared structured-line pipeline

**Files:**

- Rename: `src/HexTailSharp/Tailing/IFileTailer.cs` to `src/HexTailSharp/Tailing/ILogTailer.cs`
- Rename: `src/HexTailSharp/Tailing/TailerEvent.cs` to `src/HexTailSharp/Tailing/SourceEvent.cs`
- Rename: `src/HexTailSharp/Tailing/TailerService.cs` to `src/HexTailSharp/Tailing/LogSourceService.cs`
- Modify: `src/HexTailSharp/Tailing/FileTailer.cs`
- Modify: `src/HexTailSharp/Application/AppState.cs`
- Modify: `src/HexTailSharp/Application/FileTabState.cs`
- Modify: `src/HexTailSharp/MainWindow.axaml.cs`
- Modify: `src/HexTailSharp.Tests/Tailing/TailerServiceTests.cs`
- Modify: `src/HexTailSharp.Tests/Application/AppStateTests.cs`
- Modify: `src/HexTailSharp.Tests/Support/TestWindow.cs`

**Interfaces consumed:** `Line`, `ILogParser`, `FileBuffer`. **Interfaces produced:** `ILogTailer`, `SourceEvent`, and `LogSourceService`, which the Elastic tailer will use unchanged.

- [ ] Change the first tailer test to expect parsed `Line` objects directly from the service instead of strings:

```csharp
await using var tailer = service.StartFile("file-1", path, new LogfmtParser());
var initial = await ReadEventAsync<SourceLines>(service.Events);
Assert.Equal("info", Assert.Single(initial.Lines).ParsedFields!["level"]);
```

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "FullyQualifiedName~TailerServiceTests|FullyQualifiedName~AppStateTests.OpenAndDrain"`. Expected: compile failure because the source-neutral contracts do not exist.

- [ ] Replace the file-specific contracts with these source-neutral contracts:

```csharp
public interface ILogTailer : IAsyncDisposable
{
    string SourceId { get; }
    string DisplayName { get; }
    Task Completion { get; }
}

public abstract record SourceEvent(string SourceId);
public sealed record SourceLines(string SourceId, IReadOnlyList<Line> Lines) : SourceEvent(SourceId);
public sealed record SourceReset(string SourceId) : SourceEvent(SourceId);
public sealed record SourceError(string SourceId, string Message) : SourceEvent(SourceId);
public sealed record SourceRecovered(string SourceId) : SourceEvent(SourceId);
```

- [ ] Make `FileTailer` accept an `ILogParser`, parse each complete string before emitting, and map both rotation and truncation to `SourceReset`. Rename `FileId` to `SourceId`; use the full path as `DisplayName`.

```csharp
if (lines.Count > 0)
    Write(new SourceLines(SourceId, lines.Select(_parser.Parse).ToArray()));
```

- [ ] Rename `TailerService` to `LogSourceService`; store `List<ILogTailer>`, expose `ChannelReader<SourceEvent> Events`, and expose `StartFile(string sourceId, string path, ILogParser parser)`. Keep one unbounded channel and the existing disposal behavior.

- [ ] Update `AppState.DrainTailerEvents` to append `SourceLines.Lines` directly. Remove parsing from the consumer. Update `FileTabState` to hold `ILogTailer`. Update production/test composition roots and existing tests for the renamed service.

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "FullyQualifiedName~TailerServiceTests|FullyQualifiedName~AppStateTests"`. Expected: pass with local file append, partial-line, truncation, rotation, parsing, and disposal behavior unchanged.

- [ ] Commit with `rtk git add src/HexTailSharp src/HexTailSharp.Tests && rtk git commit -m "refactor(tailing): share structured log source events"`.

---

### Task 3: Add the native credential-vault boundary

**Files:**

- Modify: `Directory.Packages.props`
- Modify: `src/HexTailSharp/HexTailSharp.csproj`
- Create: `src/HexTailSharp/Security/ICredentialVault.cs`
- Create: `src/HexTailSharp/Security/OsCredentialVault.cs`
- Create: `src/HexTailSharp.Tests/Support/InMemoryCredentialVault.cs`
- Create: `src/HexTailSharp.Tests/Security/OsCredentialVaultTests.cs`

**Interfaces produced:** `ICredentialVault` is consumed by connection persistence, API calls, tailers, and health checks. `InMemoryCredentialVault` provides deterministic tests without touching the developer's real vault.

- [ ] Add a failing test for the stable service/account mapping and fake-vault overwrite/delete behavior:

```csharp
[Fact]
public void ConnectionKey_UsesStableConnectionId()
{
    Assert.Equal("HexTailSharp", OsCredentialVault.ServiceName);
    Assert.Equal("connection-42", OsCredentialVault.Account("connection-42"));
}
```

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter FullyQualifiedName~OsCredentialVaultTests`. Expected: compile failure because the vault types do not exist.

- [ ] Add central/package references for `GnomeStack.Os.Secrets` version `0.1.3`. This is the single justified dependency: it delegates to Windows Credential Manager, macOS Keychain, and Linux Secret Service without adding plaintext fallback code.

- [ ] Implement the narrow synchronous boundary, since all three native calls are synchronous:

```csharp
public interface ICredentialVault
{
    string? Get(string connectionId);
    void Set(string connectionId, string secret);
    void Delete(string connectionId);
}

internal sealed class OsCredentialVault : ICredentialVault
{
    internal const string ServiceName = "HexTailSharp";
    internal static string Account(string connectionId) => connectionId;

    public string? Get(string connectionId) =>
        GnomeStack.Os.Secrets.OsSecretVault.GetSecret(ServiceName, Account(connectionId));

    public void Set(string connectionId, string secret) =>
        GnomeStack.Os.Secrets.OsSecretVault.SetSecret(ServiceName, Account(connectionId), secret);

    public void Delete(string connectionId) =>
        GnomeStack.Os.Secrets.OsSecretVault.DeleteSecret(ServiceName, Account(connectionId));
}
```

- [ ] Validate blank IDs/secrets at the boundary and wrap native/library failures in `CredentialVaultUnavailableException` with the message `The operating-system credential vault is unavailable.` Preserve the original exception as `InnerException`; do not fall back to disk.

- [ ] Implement `InMemoryCredentialVault` with a dictionary plus injectable `GetError`, `SetError`, and `DeleteError` properties for later rollback/error tests.

- [ ] Run the focused vault test and `rtk dotnet build src/HexTailSharp/HexTailSharp.csproj`. Expected: both pass.

- [ ] Commit with `rtk git add Directory.Packages.props src/HexTailSharp/HexTailSharp.csproj src/HexTailSharp/Security src/HexTailSharp.Tests/Security src/HexTailSharp.Tests/Support/InMemoryCredentialVault.cs && rtk git commit -m "feat(elastic): store secrets in the native credential vault"`.

---

### Task 4: Implement Kibana metadata and Elasticsearch HTTP requests

**Files:**

- Create: `src/HexTailSharp/Elastic/ElasticModels.cs`
- Create: `src/HexTailSharp/Elastic/IElasticApiClient.cs`
- Create: `src/HexTailSharp/Elastic/ElasticApiClient.cs`
- Create: `src/HexTailSharp/Elastic/ElasticDocumentMapper.cs`
- Create: `src/HexTailSharp.Tests/Elastic/ElasticApiClientTests.cs`
- Create: `src/HexTailSharp.Tests/Elastic/ElasticDocumentMapperTests.cs`
- Create: `src/HexTailSharp.Tests/Support/RecordingHttpMessageHandler.cs`

**Interfaces consumed:** persisted connection/source settings and caller-supplied vault secret. **Interfaces produced:** metadata, page, hit, health, and exception models plus `IElasticApiClient` for polling and settings.

- [ ] Add failing handler-based tests covering: Spaces-aware Kibana paths, anonymous/no header, Basic/API-key headers, data-view parsing, PIT open/search/close, exact `term` filters, range bounds, ascending time plus `_shard_doc` sort, size 1,000, `track_total_hits: false`, `search_after`, full `_source`, and requested `fields`.

```csharp
Assert.Equal("/s/ops/api/data_views", handler.Requests[0].Uri.AbsolutePath);
Assert.Equal("Basic cmVhZGVyOnNlY3JldA==", handler.Requests[0].Authorization);
Assert.Equal("logs-*", dataView.Title);
Assert.Equal("@timestamp", dataView.TimeFieldName);
Assert.Contains(dataView.Fields, field => field.Name == "message");
```

- [ ] Add failing mapper tests for nested objects, arrays serialized as compact JSON, `fields` overriding `_source`, missing checked values, null values, output ordering, and complete flattened expansion data.

```csharp
var line = ElasticDocumentMapper.Map(
    JsonDocument.Parse("""{"message":"ready","service":{"name":"api"},"tags":["a","b"]}""").RootElement,
    JsonDocument.Parse("""{"service.name.keyword":["api"]}""").RootElement,
    ["service.name.keyword", "message", "missing"]
);
Assert.Equal("api ready", line.Raw);
Assert.Equal("api", line.ParsedFields!["service.name.keyword"]);
Assert.Equal("[\"a\",\"b\"]", line.ParsedFields["tags"]);
```

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "FullyQualifiedName~ElasticApiClientTests|FullyQualifiedName~ElasticDocumentMapperTests"`. Expected: compile failure because the Elastic client and mapper do not exist.

- [ ] Define concrete transport models: `ElasticDataViewSummary`, `ElasticDataView`, `ElasticDataViewField`, `ElasticSearchRequest`, `ElasticSearchPage`, `ElasticHit`, `ElasticHttpException`, and `ElasticUnauthorizedException`. Store cloned `JsonElement` sort values so their owning `JsonDocument` can be disposed safely.

- [ ] Implement one `ElasticApiClient(HttpClient)` using absolute request URIs. Append Kibana API paths to the configured base path, escape the data-view ID/title as path segments, and set authorization per request rather than on `HttpClient.DefaultRequestHeaders`.

```csharp
private static void AddAuthorization(
    HttpRequestMessage request,
    ElasticConnectionSettings connection,
    string? secret
)
{
    request.Headers.Authorization = connection.AuthMode switch
    {
        ElasticAuthMode.Anonymous => null,
        ElasticAuthMode.Basic => new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{connection.Username}:{secret}"))
        ),
        ElasticAuthMode.ApiKey => new AuthenticationHeaderValue("ApiKey", secret),
        _ => throw new ArgumentOutOfRangeException(nameof(connection.AuthMode)),
    };
}
```

- [ ] Implement `GET api/data_views`, `GET api/data_views/data_view/{id}`, `POST {title}/_pit?keep_alive=1m`, `POST _search`, `DELETE _pit`, and a `size: 0` filtered health search. Treat 401/403 as `ElasticUnauthorizedException`, network/408/429/5xx as `ElasticTransientException`, and other non-success responses as `ElasticHttpException`; include status/reason but not authorization values.

- [ ] Implement recursive dotted-path flattening. Merge `_source` first and response `fields` second. Compose `Line.Raw` by iterating configured output fields in order, omitting missing/null/blank values and joining the rest with one space.

- [ ] Run the focused client/mapper tests. Expected: pass with every recorded request body/header and mapped line assertion satisfied.

- [ ] Commit with `rtk git add src/HexTailSharp/Elastic src/HexTailSharp.Tests/Elastic src/HexTailSharp.Tests/Support/RecordingHttpMessageHandler.cs && rtk git commit -m "feat(elastic): add Kibana and Elasticsearch client"`.

---

### Task 5: Implement PIT pagination, cursor deduplication, and polling

**Files:**

- Create: `src/HexTailSharp/Elastic/ElasticTailer.cs`
- Modify: `src/HexTailSharp/Tailing/LogSourceService.cs`
- Create: `src/HexTailSharp.Tests/Elastic/ElasticTailerTests.cs`
- Create: `src/HexTailSharp.Tests/Support/FakeElasticApiClient.cs`

**Interfaces consumed:** `IElasticApiClient`, `ICredentialVault`, `ILogTailer`, and `SourceEvent`. **Interface produced:** an Elastic-backed `ILogTailer` registered through the existing `LogSourceService`.

- [ ] Add failing deterministic tests for the initial five-minute bound, 1,000-hit pagination, newest PIT ID propagation, full sort-array `search_after`, equal timestamps, duplicate IDs on the next cycle, PIT close in success/failure/cancellation, unauthorized errors, transient retry, recovery, and disposal.

```csharp
await tailer.PollOnceAsync(CancellationToken.None);
await tailer.PollOnceAsync(CancellationToken.None);

Assert.Equal(openTime - TimeSpan.FromMinutes(5), client.Searches[0].FromInclusive);
Assert.Equal(["a", "b", "c"], Lines(events).Select(line => line.Raw));
Assert.All(client.ClosedPitIds, id => Assert.StartsWith("pit-", id));
```

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter FullyQualifiedName~ElasticTailerTests`. Expected: compile failure because `ElasticTailer` does not exist.

- [ ] Implement constants and an internal test seam without exposing timing in settings:

```csharp
internal static readonly TimeSpan InitialLookback = TimeSpan.FromMinutes(5);
internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
internal static readonly TimeSpan UnauthorizedDelay = TimeSpan.FromSeconds(30);
internal const int PageSize = 1_000;
```

Use an internal constructor accepting `Func<DateTimeOffset> utcNow` and `Func<TimeSpan, CancellationToken, Task> delay`; the production constructor supplies `DateTimeOffset.UtcNow` and `Task.Delay`.

- [ ] In `PollOnceAsync`, capture `toInclusive` once, open a fresh PIT, request pages until fewer than 1,000 hits return, pass the full final sort array to the next page, accept the newest PIT ID from every response, and close that PIT in `finally`.

- [ ] Maintain `_cursorTimestamp` and `_idsAtCursor`. Query inclusively from the cursor; skip an ID only when its timestamp equals the cursor and it was already emitted. When a later timestamp arrives, clear the set, advance the cursor, and record IDs at the new boundary. Emit non-empty batches as `SourceLines`.

```csharp
if (_cursorTimestamp == hit.Timestamp && _idsAtCursor.Contains(hit.Id))
    continue;
if (_cursorTimestamp is null || hit.Timestamp > _cursorTimestamp)
{
    _cursorTimestamp = hit.Timestamp;
    _idsAtCursor.Clear();
}
_idsAtCursor.Add(hit.Id);
accepted.Add(hit.Line);
```

- [ ] Use the existing Polly dependency for bounded exponential retry of `ElasticTransientException`, capped at 30 seconds. Emit one `SourceError` while failing and `SourceRecovered` on the first succeeding cycle. Do not aggressively retry `ElasticUnauthorizedException`; emit the error and wait 30 seconds before the next cycle.

- [ ] Add `LogSourceService.StartElastic` to construct, own, start, and return the tailer on the shared channel. Closing the owning tab must cancel its loop via `DisposeAsync`.

- [ ] Run the focused tailer tests. Expected: pass without wall-clock sleeps because the fake delay completes immediately.

- [ ] Commit with `rtk git add src/HexTailSharp/Elastic/ElasticTailer.cs src/HexTailSharp/Tailing/LogSourceService.cs src/HexTailSharp.Tests/Elastic/ElasticTailerTests.cs src/HexTailSharp.Tests/Support/FakeElasticApiClient.cs && rtk git commit -m "feat(elastic): poll remote logs with stable pagination"`.

---

### Task 6: Save authenticated connections transactionally

**Files:**

- Modify: `src/HexTailSharp/Application/AppState.cs`
- Modify: `src/HexTailSharp/MainWindow.axaml.cs`
- Modify: `src/HexTailSharp.Tests/Application/AppStateTests.cs`
- Modify: `src/HexTailSharp.Tests/Support/TestWindow.cs`

**Interfaces consumed:** `ICredentialVault`, `IElasticApiClient`, persisted Elastic settings. **Interfaces produced:** AppState methods used by settings: `SaveElasticConnectionAsync`, `RemoveElasticConnectionAsync`, `GetElasticSecret`, `GetDataViewsAsync`, and `GetDataViewAsync`.

- [ ] Add failing tests for anonymous save, authenticated vault-first save, JSON failure restoring the old secret and old settings, delete persisting settings before vault deletion, and delete failure leaving the connection removed while reporting the error.

```csharp
vault.Set("elastic-1", "old-secret");
persistence.SaveError = new IOException("disk full");

await Assert.ThrowsAsync<IOException>(() =>
    state.SaveElasticConnectionAsync(updated, "new-secret").AsTask()
);

Assert.Equal("old-secret", vault.Get("elastic-1"));
Assert.Equal("Old name", Assert.Single(state.Settings.ElasticConnections).Name);
```

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "FullyQualifiedName~AppStateTests.SaveElastic|FullyQualifiedName~AppStateTests.RemoveElastic"`. Expected: compile failure because the AppState operations do not exist.

- [ ] Extend `AppState` construction with shared `ICredentialVault` and `IElasticApiClient` dependencies. Keep the two-argument constructor for local-only callers, but make `MainWindow` explicitly create `OsCredentialVault`, one `HttpClient`, one `ElasticApiClient`, and pass the shared instances.

- [ ] Validate connection IDs, absolute HTTPS/HTTP URLs, auth requirements, selected data view/time field, server/namespace mappings, and at least one output field before persistence. Basic requires username and secret; API key requires secret; Anonymous deletes any obsolete vault entry after JSON save.

- [ ] Implement authenticated save as: snapshot prior settings and secret; write new secret; replace/add normalized connection; save JSON; on failure restore settings and restore/delete the previous secret; rethrow. Implement removal as: snapshot settings; remove the connection; save JSON; restore settings if JSON fails; only then delete the vault secret. Surface vault-delete failure without re-adding the connection.

- [ ] Add thin metadata forwarding methods that resolve the vault secret and call the shared Elastic client. Never expose a secret property from `AppState` or a view model; `GetElasticSecret` should remain `internal` and only support editing an existing authenticated connection.

- [ ] Run the focused tests. Expected: pass, including assertions that serialized JSON never contains either old or new secrets.

- [ ] Commit with `rtk git add src/HexTailSharp/Application/AppState.cs src/HexTailSharp/MainWindow.axaml.cs src/HexTailSharp.Tests/Application/AppStateTests.cs src/HexTailSharp.Tests/Support/TestWindow.cs && rtk git commit -m "feat(elastic): save connections without exposing secrets"`.

---

### Task 7: Open, close, drain, save, and restore remote tabs

**Files:**

- Create: `src/HexTailSharp/Application/LogSourceDescriptor.cs`
- Modify: `src/HexTailSharp/Application/FileTabState.cs`
- Modify: `src/HexTailSharp/Application/AppState.cs`
- Modify: `src/HexTailSharp/ViewModels/FileTabViewModel.cs`
- Modify: `src/HexTailSharp/ViewModels/MainWindowViewModel.cs`
- Modify: `src/HexTailSharp.Tests/Application/AppStateTests.cs`
- Modify: `src/HexTailSharp.Tests/ViewModels/WorkspaceViewModelTests.cs`

**Interfaces consumed:** `ElasticTailer`, source-neutral events, persisted remote tabs. **Interfaces produced:** source-neutral tab identity and `OpenElasticSourceAsync`/`CloseFileAsync` behavior consumed by the picker UI.

- [ ] Add failing tests that open one local file and two remote sources, assert one tab per selected source, drain structured lines through the same `FileBuffer`, close a remote tab, restore remote searches/follow/context, ignore a removed source ID, and restore the selected local or remote tab correctly.

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "FullyQualifiedName~AppStateTests.Elastic|FullyQualifiedName~WorkspaceViewModelTests.Elastic"`. Expected: compile failure because remote-tab operations and source identity do not exist.

- [ ] Add a source-neutral descriptor and store it on `FileTabState`:

```csharp
public enum LogSourceKind { File, Elastic }

public sealed record LogSourceDescriptor(
    string Id,
    LogSourceKind Kind,
    string DisplayName,
    string ToolTip,
    string? LocalPath = null,
    string? ElasticSourceId = null
);
```

Keep `FileTabState` and `FileTabViewModel` names to avoid a repository-wide cosmetic rename, but derive tab display/tooltip from `Source`. Local descriptors use the full path as ID and `LocalPath`; remote descriptors use source ID and `<Server>-<Namespace>`, with connection name in the tooltip.

- [ ] Implement `OpenElasticSourceAsync(sourceId)` by resolving exactly one connection/source, returning the existing tab when already open, resolving the vault secret, and starting `LogSourceService.StartElastic`. Reject incomplete/removed/misconfigured sources with `ArgumentException` before creating a tab.

- [ ] Change event lookup from generated tab ID to stable `Source.Id`; append `SourceLines` for either source and keep reset/error/recovery handling identical. Closing any tab disposes its `ILogTailer`; expose `IsElasticSourceOpen(sourceId)` for picker synchronization.

- [ ] Extend `SaveAsync` to branch on `Source.Kind`: local tabs continue into unchanged `OpenFiles`; remote tabs go to `OpenElasticTabs`. Set only one of `SelectedFilePath` and `SelectedElasticSourceId`. Extend restore to ignore missing source IDs and rebuild the same searches/follow/context properties without restoring rows or cursors.

- [ ] Update `MainWindowViewModel` synchronization and error labels to say `SourceError` internally while keeping the local picker text `Open log files`. Confirm existing `FileTabViewModel` rendering and expanded `ParsedFields` require no Elastic-specific view.

- [ ] Run the focused AppState/view-model tests and `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter FullyQualifiedName~LogViewTests`. Expected: pass with local and remote tabs sharing the renderer.

- [ ] Commit with `rtk git add src/HexTailSharp/Application src/HexTailSharp/ViewModels src/HexTailSharp.Tests/Application/AppStateTests.cs src/HexTailSharp.Tests/ViewModels/WorkspaceViewModelTests.cs && rtk git commit -m "feat(elastic): integrate remote sources with tab state"`.

---

### Task 8: Monitor every configured source and publish aggregate health

**Files:**

- Create: `src/HexTailSharp/Elastic/ElasticHealthMonitor.cs`
- Modify: `src/HexTailSharp/Application/AppState.cs`
- Modify: `src/HexTailSharp/Elastic/ElasticModels.cs`
- Create: `src/HexTailSharp.Tests/Elastic/ElasticHealthMonitorTests.cs`
- Modify: `src/HexTailSharp.Tests/Application/AppStateTests.cs`

**Interfaces consumed:** client metadata/health operations, vault, configured connections/sources. **Interfaces produced:** per-source `ElasticSourceHealth` snapshots and aggregate `HasElasticWarning` state for the toolbar.

- [ ] Add failing tests for all five statuses (`Checking`, `Connected`, `Unreachable`, `Unauthorized`, `Misconfigured`), checking unselected sources, propagating a connection failure to its sources, a single source-query failure, recovery, and cancellation.

```csharp
await monitor.CheckOnceAsync(CancellationToken.None);

Assert.Equal(ElasticConnectionStatus.Connected, monitor.Statuses["source-1"].Status);
Assert.Equal(ElasticConnectionStatus.Unauthorized, monitor.Statuses["source-2"].Status);
Assert.True(monitor.HasWarning);
```

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter FullyQualifiedName~ElasticHealthMonitorTests`. Expected: compile failure because the monitor and statuses do not exist.

- [ ] Implement the status enum and immutable snapshot record with a user-safe message and timestamp. Keep the status dictionary keyed by source ID and return snapshots rather than the mutable dictionary.

- [ ] Implement `CheckOnceAsync`: mark every configured source `Checking`; validate cached configuration; resolve the vault secret; call Kibana data-view metadata once per connection; run the client's `size: 0` exact-filter health query once per source; map auth failures, transient failures, and validation failures to the required statuses. Do not batch source checks in v1.

- [ ] Add a background loop using the internal delay seam from Task 5 and a fixed 30-second interval. `AppState.RestoreAsync` starts it after configuration load; settings changes signal an immediate recheck; `DisposeAsync` cancels it. Publish `Changed` only when a status actually changes.

- [ ] Expose `ElasticSourceStatuses` and `HasElasticWarning` from `AppState`. Ensure an open tailer's `SourceError` remains the detailed workspace error while health state drives picker indicators.

- [ ] Run the focused monitor/AppState tests. Expected: pass without real time or a live server.

- [ ] Commit with `rtk git add src/HexTailSharp/Elastic src/HexTailSharp/Application/AppState.cs src/HexTailSharp.Tests/Elastic/ElasticHealthMonitorTests.cs src/HexTailSharp.Tests/Application/AppStateTests.cs && rtk git commit -m "feat(elastic): monitor configured source health"`.

---

### Task 9: Add settings and source-picker view models

**Files:**

- Create: `src/HexTailSharp/ViewModels/ElasticConnectionEditorViewModel.cs`
- Create: `src/HexTailSharp/ViewModels/ElasticFieldOptionViewModel.cs`
- Create: `src/HexTailSharp/ViewModels/ElasticSourceSettingViewModel.cs`
- Create: `src/HexTailSharp/ViewModels/ElasticSourceOptionViewModel.cs`
- Modify: `src/HexTailSharp/ViewModels/SettingsViewModel.cs`
- Modify: `src/HexTailSharp/ViewModels/MainWindowViewModel.cs`
- Create: `src/HexTailSharp.Tests/ViewModels/ElasticSettingsViewModelTests.cs`
- Modify: `src/HexTailSharp.Tests/ViewModels/WorkspaceViewModelTests.cs`

**Interfaces consumed:** AppState connection/metadata/tab/health operations. **Interfaces produced:** bindable connection drafts, field checkboxes, manual sources, and multi-select source options.

- [ ] Add failing view-model tests for: add/remove connection; auth-field visibility; successful test loading data views; selected data view loading fields/time field; generic server/namespace selectors; ordered checked output fields; source add/remove; invalid save; vault error display; picker absence with zero sources; checkbox opens/closes exactly one tab; closing a tab unchecks; and aggregate warning.

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "FullyQualifiedName~ElasticSettingsViewModelTests|FullyQualifiedName~WorkspaceViewModelTests.Elastic"`. Expected: compile failure because the Elastic view models do not exist.

- [ ] Extend `SettingsViewModel.SectionIndex` to `0..4`, mapping index 4 to `elastic`. Add `ObservableCollection<ElasticConnectionEditorViewModel> ElasticConnections`, selected editor state, and commands to add, save, and remove connections.

- [ ] Implement a staged `ElasticConnectionEditorViewModel`: URL/auth/username/secret fields are drafts; `TestConnectionCommand` calls both endpoints and populates `DataViews`; selecting a data view fetches its `timeFieldName` and `Fields`; field rows expose `IsOutput`, while exact-match candidates populate server/namespace selectors. `SaveCommand` is enabled only after successful endpoint/data-view validation and at least one checked output field.

- [ ] Use `TextBox`-bound `Secret` only as an editor draft. Clear it immediately after successful vault save and when switching connections. Never expose it through persisted settings, source options, error text, or `ToString()`.

- [ ] Implement manual source editors with trimmed values and GUID `N` IDs generated at add time. Reject blank pairs and duplicate `(ServerValue, NamespaceValue)` pairs within a connection. Commit all sources through `SaveElasticConnectionAsync`.

- [ ] Add `ObservableCollection<ElasticSourceOptionViewModel> ElasticSources` to `MainWindowViewModel`. Each row exposes `DisplayName`, connection tooltip, `IsOpen`, status/glyph, and a guarded async setter that calls `OpenElasticSourceAsync` or closes its tab. `SyncFromState` creates/removes rows from configuration and updates check state from actual open tabs, so tab close and checkbox state cannot diverge.

- [ ] Expose `HasElasticSources`, `HasElasticWarning`, and `ElasticSourceIcon` (`mdi-cloud-check` or `mdi-cloud-alert`) for the collapsed toolbar button. All configured sources remain in the collection even when unchecked.

- [ ] Run the focused view-model tests. Expected: pass with no Avalonia window and no live Elastic/vault access.

- [ ] Commit with `rtk git add src/HexTailSharp/ViewModels src/HexTailSharp.Tests/ViewModels && rtk git commit -m "feat(settings): configure Elastic log sources"`.

---

### Task 10: Add the wider Elastic settings UI and native multi-select flyout

**Files:**

- Modify: `src/HexTailSharp/MainWindow.axaml`
- Modify: `src/HexTailSharp/MainWindow.axaml.cs`
- Modify: `src/HexTailSharp/Views/SettingsPanel.axaml`
- Modify: `src/HexTailSharp/Views/FileStrip.axaml`
- Modify: `src/HexTailSharp/Views/WorkspaceError.axaml`
- Modify: `src/HexTailSharp.Tests/Ui/MainWindowInteractionTests.cs`

**Interfaces consumed:** Task 9 view models. **Interfaces produced:** the user-visible Elastic configuration and toolbar selection flow.

- [ ] Add failing headless UI tests that assert: 760px target pane; clamping below narrow window width; Elastic tab; connection/data-view/field/source controls; no toolbar source button when unconfigured; source button/flyout when configured; more than one checked source opens independent tabs; tab close unchecks; and warning/status automation text.

```csharp
Assert.Equal(760, window.FindControl<SplitView>("SettingsSplitView")!.OpenPaneLength);
var sourceButton = window.FindControl<CommandBarButton>("ElasticSourcesButton")!;
Assert.False(viewModel.HasElasticSources);
Assert.False(sourceButton.IsVisible);
Assert.Equal(2, viewModel.Files.Count(file => file.Model.Source.Kind == LogSourceKind.Elastic));
```

When implementing the conditional-button assertion, locate visual descendants because an invisible named control can still be present in the namescope; assert `IsVisible == false` rather than relying on `FindControl` returning null.

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter FullyQualifiedName~MainWindowInteractionTests.Elastic`. Expected: failure because the controls are absent.

- [ ] Set `OpenPaneLength="760"`. In `UpdateResponsiveLayout`, set `SettingsSplitView.OpenPaneLength = Math.Min(760, Math.Max(320, width - 48));` and retain Overlay below 960px so the pane remains usable on small windows.

- [ ] Add a `CommandBarButton` named `ElasticSourcesButton` beside Settings/Save/Open, bind its visibility to `HasElasticSources`, icon to `ElasticSourceIcon`, and automation name to the aggregate status. Use a native `Flyout` containing an `ItemsControl` of `CheckBox` rows bound two-way to `ElasticSourceOptionViewModel.IsOpen`, with source text, status glyph, tooltip, and automation text. This is the requested multi-select; add no control package.

- [ ] Add the fifth **Elastic** settings tab with connection list/add/remove, both URL boxes, auth selector, conditional username/secret controls, Test connection, data-view selector, generic server/namespace field selectors, output-field checklist, and manual source rows. Bind errors and busy state to the staged editor. Use existing brushes, spacing, buttons, and icons.

- [ ] Bind file-tab tooltip/accessibility text to the source-neutral tooltip so duplicate remote display names remain distinguishable by connection. Keep visible tab text exactly `<ServerValue>-<NamespaceValue>`.

- [ ] Keep open-poller errors in `WorkspaceError` and ensure remote paths are not formatted as filesystem paths in the message.

- [ ] Run the focused headless tests, then `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj`. Expected: all tests pass.

- [ ] Commit with `rtk git add src/HexTailSharp/MainWindow.axaml src/HexTailSharp/MainWindow.axaml.cs src/HexTailSharp/Views src/HexTailSharp.Tests/Ui/MainWindowInteractionTests.cs && rtk git commit -m "feat(ui): add Elastic source controls"`.

---

### Task 11: Document platform prerequisites and run release verification

**Files:**

- Modify: `README.md`
- Modify: `docs/architecture.md`
- Modify: `docs/user-guide.md`

**Interfaces consumed:** the completed feature and its fixed operational limits. **Interfaces produced:** operator/developer instructions for configuring and validating the feature.

- [ ] Add documentation describing dual URLs, shared auth, manual source values, generic mappings, checked output-field line composition, five-minute lookback, source statuses, and the fact that Kibana metadata and Elasticsearch search are separate calls.

- [ ] Document vault behavior and Linux prerequisite: an active Secret Service session plus `libsecret` (`libsecret-1-dev` on Debian/Ubuntu, `libsecret-devel` on Red Hat, `libsecret` on Arch). State explicitly that authenticated configuration fails closed when the native vault is unavailable and that secrets never enter `session.json`.

- [ ] Document a manual native-vault smoke check for Windows, macOS, and Linux: create a temporary authenticated connection, save, restart, test, remove, restart, and verify the credential no longer exists. Do not print the credential during the check.

- [ ] Run formatting verification: `rtk dotnet csharpier check .`. Expected: exit 0. If it fails, run `rtk dotnet csharpier format .`, inspect the diff, and rerun the check.

- [ ] Run `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --configuration Release`. Expected: all tests pass with no live Elastic dependency.

- [ ] Run `rtk dotnet build HexTailSharp.slnx --configuration Release --no-restore`. Expected: build succeeds with zero errors.

- [ ] Inspect the serialized-config tests and repository diff with `rtk rg -n -i "password|api.?key" src/HexTailSharp.Tests/Persistence src/HexTailSharp/Persistence` and `rtk git diff --check`. Expected: only model/auth labels and negative assertions mention secret terms; no secret-valued persistence property exists; diff check is clean.

- [ ] Commit with `rtk git add README.md docs/architecture.md docs/user-guide.md && rtk git commit -m "docs(elastic): document remote log sources"`.

- [ ] Perform the real credential save/read/delete smoke check on the current OS only. Record Windows/macOS/Linux coverage still outstanding in the PR or handoff; do not fake results for platforms that were not run.

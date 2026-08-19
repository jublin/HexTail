# Elastic Logging Integration Design

**Status:** Approved for implementation planning  
**Date:** 2026-08-18

## Purpose

Add read-only Elastic log sources to HexTail without creating a second log viewer. A configured remote source behaves like an open file: it owns a tab, a bounded buffer, searches, follow state, inline context, highlights, and expanded structured fields.

Kibana supplies supported data-view metadata. Elasticsearch supplies log documents through its supported search API. The application does not use Kibana internal query endpoints.

## Goals

- Configure multiple logical Elastic connections, each with Kibana and Elasticsearch URLs.
- Support anonymous, Basic, and API-key authentication with one credential set shared by both endpoints.
- Store secrets in the operating system credential vault, never in `session.json`.
- Configure server/namespace field mappings without assuming Kubernetes or ECS field names.
- Add server-namespace sources manually and open any number as independent tabs.
- Discover available fields from the selected Kibana data view and share the checked output fields across that connection.
- Load five minutes of history when a remote source opens, then continue polling.
- Show source and aggregate connection status even for unselected sources.
- Reuse the existing buffer, search, highlighting, context, follow, and virtualized rendering behavior.

## Non-goals

- Automatic discovery of server/namespace values.
- A table/grid log renderer.
- Merging multiple remote sources into one tab.
- Per-source output-field selection.
- User-configurable lookback, poll, health-check, or retry intervals.
- KQL, ES|QL, or arbitrary custom-query editing.
- Writing to Elastic, managing data views, or changing cluster configuration.
- Bypassing TLS certificate validation or falling back to plaintext credential storage.

## Terminology

- **Connection:** One Kibana URL, one Elasticsearch URL, one authentication configuration, one Kibana data view, shared field mappings, and shared output fields.
- **Source:** One manually configured pair of values for the connection's server and namespace fields.
- **Remote tab:** An open source backed by an Elastic poller rather than a local file tailer.
- **Display name:** `<ServerValue>-<NamespaceValue>`, for example `Mystack1-RhubarbPi`.

## Configuration Model

Persist non-secret configuration in `AppSettings`:

```text
ElasticConnection
  Id                    stable GUID/string used as the credential-vault key
  Name                  user-visible connection name
  KibanaUrl
  ElasticsearchUrl
  AuthMode              Anonymous | Basic | ApiKey
  Username              Basic only; not secret
  DataViewId
  DataViewTitle         cached index/data-stream pattern for offline display
  TimeFieldName         cached from the selected Kibana data view
  ServerField
  NamespaceField
  OutputFields[]        ordered shared field names
  Sources[]

ElasticSource
  Id                    stable GUID/string
  ServerValue
  NamespaceValue
```

The data view's `timeFieldName` is read from Kibana and cached with the connection after a successful test. It is not a normal user setting. A data view without a time field cannot be saved for live tailing.

Field selectors are populated only after a successful Kibana connection. Server and namespace selectors accept arbitrary string-valued data-view fields suitable for exact matching, including keyword multi-fields. No field name is hard-coded.

`OutputFields` preserves the order shown in the checklist. Each Elastic document becomes:

- `Line.Raw`: non-null selected field values joined with one space in configured order. Values come from each hit's `fields` response first and fall back to flattened `_source` values.
- `Line.ParsedFields`: flattened `_source` merged with returned `fields`, using dotted paths for nested objects and compact JSON for arrays/objects.

Missing selected fields contribute no text. Existing search, labels, and exclusions operate on `Line.Raw`; double-click expansion shows `Line.ParsedFields`.

## Credential Storage

An `ICredentialVault` boundary is justified because production uses three platform implementations and tests use an in-memory fake. Implementations target:

- Windows Credential Manager.
- macOS Keychain.
- Linux Secret Service/libsecret.

The service namespace is `HexTailSharp`, and the stable connection ID is the account key. Anonymous connections have no vault entry. `session.json` stores auth mode and Basic username but never a password or API key.

There is no plaintext fallback. If the platform vault is missing, locked, or unavailable, authenticated connection save/read fails with an actionable settings error. Linux therefore requires an available desktop Secret Service session.

Saving an authenticated connection writes the vault entry and then the JSON configuration. If JSON persistence fails, the previous vault entry is restored. Removing a connection saves the configuration first and then removes its vault entry; a vault-delete failure is reported but does not resurrect the connection.

## Runtime Architecture

Generalize the current file-specific ingestion boundary rather than adding a parallel Elastic pipeline:

```text
Local file -> FileTailer ----+
                             +-> source event channel -> AppState -> FileBuffer -> existing views
Elastic ----> ElasticTailer -+
```

The source-neutral layer contains:

- `ILogTailer`: source ID, display name, completion, and async disposal.
- `LogSourceService`: owns local and Elastic tailers and the shared event channel.
- `SourceEvent`: source-neutral lines, reset, error, and recovery events.
- `ElasticApiClient`: Kibana metadata calls, Elasticsearch health/search calls, authentication headers, and JSON conversion.
- `ElasticTailer`: initial query, cursor/deduplication state, polling, retry, and cancellation.

Both producers emit `IReadOnlyList<Line>`. `FileTailer` receives the selected `ILogParser` so plaintext and logfmt parsing occurs before emission. `ElasticTailer` emits composed raw text plus flattened fields. `AppState` therefore appends the same domain type regardless of source.

`FileTabState` becomes source-neutral internally while retaining the existing view-model behavior. It stores a source identity/type and an `ILogTailer` instead of assuming a filesystem path and `IFileTailer`. Public labels may continue to say “file” where they refer specifically to local picker actions; tabs themselves represent either source type.

## Kibana and Elasticsearch API Use

For Kibana metadata, use only documented public endpoints:

- `GET /api/data_views` to list available data views.
- `GET /api/data_views/data_view/{viewId}` to load the selected view, fields, title, and time field.

The configured Kibana base URL may include a Spaces prefix; API paths are appended relative to that base. No data view is created or modified.

For each paged read cycle, open a one-minute point in time with `POST /{dataViewTitle}/_pit?keep_alive=1m`, then send searches to `POST /_search` with that PIT. Requests use:

- Exact `term` filters for configured server and namespace fields/values.
- A range filter on the data-view time field.
- Ascending time-field sort plus the PIT's implicit `_shard_doc` tie-breaker.
- `search_after` pagination using every sort value returned by the previous page.
- A fixed page size of 1,000 with total-hit tracking disabled.
- Full `_source` plus the configured output fields so expansion requires no secondary request and keyword multi-fields remain available.

Use the newest PIT ID returned by each page and close the PIT when the cycle completes or is cancelled. A fresh PIT is opened for the next two-second poll so newly indexed documents become visible.

Anonymous mode sends no authorization header. Basic sends the shared username/password. API-key mode sends the shared API key. Normal `HttpClient` TLS validation remains enabled.

## Open and Poll Flow

Opening a remote source creates one normal tab and one `ElasticTailer`:

1. Resolve the connection, vault secret, data-view title, and cached time field.
2. Capture the open time once and query from `openTime - 5 minutes` through the current time.
3. Apply the source's two exact filters.
4. Open a PIT, read all pages in stable timestamp/`_shard_doc` order, close the PIT, and emit structured line batches.
5. Poll every two seconds.
6. Use an inclusive timestamp cursor and document-ID deduplication at the cursor boundary so documents sharing a timestamp are neither duplicated nor dropped.

The cursor is in memory only. Closing/unchecking and reopening a source starts a fresh five-minute window. Closing a tab cancels and disposes its poller immediately.

Remote tabs use the existing maximum buffer size. Buffer rollover, searches, follow mode, context, selection, and expanded rows require no Elastic-specific variants.

## Toolbar and Tab Behavior

Add a native button/flyout beside Settings, Save Session, and Open. Do not add a third-party multi-select control.

- The button is absent when no Elastic sources are configured.
- The flyout contains every configured source with a checkbox and status icon.
- Checking a source opens/selects its independent remote tab.
- Unchecking a source closes that tab.
- Closing a remote tab also unchecks the source.
- Local file tabs remain independent of the source picker.
- The collapsed button warns when any configured source is not healthy.

Each remote tab title is its computed display name. Duplicate display names from different connections include the connection name in their tooltip and accessibility name.

## Settings Layout

Increase the settings pane target width from 600px to 760px and add an **Elastic** tab to the existing settings panel. Clamp the pane to available window width so smaller screens remain usable.

The Elastic tab contains:

1. Connection list with add/remove.
2. Kibana URL, Elasticsearch URL, auth mode, username/secret controls, and **Test connection**.
3. Data-view selector populated after successful testing.
4. Generic server and namespace field selectors.
5. Shared output-field checklist.
6. Manual source list with server value, namespace value, add, and remove.

Changing connection URLs, authentication, data view, or field mappings revalidates affected sources. A connection cannot be saved until both endpoints, the data view, time field, mappings, and at least one output field validate. Sources may be added after the connection itself is valid.

## Health and Error States

Statuses are `Checking`, `Connected`, `Unreachable`, `Unauthorized`, and `Misconfigured`.

- Every connection is checked every 30 seconds, including when none of its sources are open.
- Every configured source receives an effective status. Connection failures propagate to its sources; source mapping/query validation can fail independently.
- Unselected sources use a lightweight zero-result filtered query during health validation.
- Open pollers publish detailed errors into the existing workspace error area while retaining buffered rows.
- Network and 5xx failures retry with bounded exponential backoff, capped at 30 seconds.
- `401` and `403` set `Unauthorized` and stop aggressive retries until credentials change or the next health cycle.
- A successful request emits recovery and clears the error without rebuilding the tab.

The initial implementation performs simple per-source health requests. Batching is deferred until measurements show that configured source counts make this material.

## Session Persistence and Compatibility

Keep the existing local-file persistence schema intact for backward compatibility. Add:

- `OpenElasticTabs`: source ID plus the same searches, follow, context, and selected-line state persisted for a local tab.
- `SelectedElasticSourceId`: set only when the selected tab is remote.

When a local tab is selected, existing `SelectedFilePath` remains authoritative and `SelectedElasticSourceId` is null. Unknown or removed source IDs are ignored during restore. Existing session files deserialize with empty Elastic collections and continue unchanged.

Connection/source configuration persists independently of whether its tabs are open. Remote cursors and fetched rows are never persisted.

## Verification

Automated tests cover:

- Kibana and Elasticsearch request paths, auth headers, metadata parsing, filters, five-minute range, sorting, and pagination.
- Nested-field flattening, checked-field row composition, missing/null values, arrays, and full expansion data.
- Equal timestamps, PIT/search-after pagination, PIT cleanup, duplicate IDs across poll cycles, cancellation, retry, unauthorized, recovery, and buffer rollover.
- Connection/source JSON round trips and proof that serialized JSON contains no password or API key.
- Vault behavior through an in-memory fake, including rollback and delete failures.
- Mixed local/remote state, restore, source selection, close, and aggregate health.
- Headless UI behavior for conditional picker visibility, checkbox/tab synchronization, status indicators, field selection, and the wider settings pane.
- All existing file-tailing, search, persistence, and UI regressions.

Windows, macOS, and Linux smoke checks verify real credential save/read/delete behavior. Elastic HTTP tests use deterministic fake `HttpMessageHandler` implementations; CI does not require a live Elastic cluster.

## Deferred Work

Add only when required by measured use:

- Automatic source discovery.
- Batched health checks for large source lists.
- Per-source output fields.
- Custom queries or server-side search integration.
- User-configurable timing and lookback.
- Late-event overlap based on a separate ingest timestamp.

## References

- [Kibana API documentation](https://www.elastic.co/docs/api/doc/kibana)
- [Get all Kibana data views](https://www.elastic.co/docs/api/doc/kibana/v8/operation/operation-getalldataviewsdefault)
- [Get a Kibana data view](https://www.elastic.co/docs/api/doc/kibana/operation/operation-getdataviewdefault)
- [Elastic query-language endpoints](https://www.elastic.co/docs/explore-analyze/query-filter/languages)
- [Paginate Elasticsearch results with PIT and search_after](https://www.elastic.co/docs/reference/elasticsearch/rest-apis/paginate-search-results)

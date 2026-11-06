# UX Phase 3 — Elastic Setup as a Stepped Flow Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the Elastic tab of the Settings modal with a stepped setup page (Connection → Data view → Filter → Columns → Name & open) that accepts a Cloud ID, tests endpoints automatically, shows live per-value document counts and a real-document preview, autosaves per step, and optionally prefills from a pasted Kibana Discover URL.

**Architecture:** A new `ElasticSetupViewModel` (owned by `MainWindowViewModel.ElasticSetup`) drives a page hosted in the workspace area. It reuses the existing `ElasticConnectionEditorViewModel` / `ElasticViewEditorViewModel` (validation, metadata loading, persistence mapping) and `AppState.SaveElasticConnectionAsync`, so the persisted shape does not change and there is no config migration. New pure helpers (Cloud ID decoder, Rison parser, Discover URL importer) and two new read-only API calls (`CountAsync`, `PreviewAsync`) feed the steps. The Settings modal loses its Elastic tab and the "Unsaved Elastic changes" interstitial.

**Tech Stack:** .NET 10, Avalonia 12.1.1 (headless tests via `[AvaloniaFact]`), ReactiveUI, xUnit v3, System.Text.Json, `HttpClient` against Kibana/Elasticsearch.

This is Phase 3 of `docs/ux/2026-11-04-ux-proposals.md` (item 5; item 10's last step). Phases 1 and 2 (parts A–C) are merged. Terminology: "Phase 3".

## Global Constraints

- Branch `audit-ui-phase-3`, cut from `origin/main` at `663fb7d` (PR #13 merged). Never work on `main`. **Never `git push` unless the user explicitly says so.**
- Conventional Commits, one commit per task. End each commit message with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- A CSharpier pre-commit hook reformats staged C# on commit. `csharpier check` already fails on `src/HexTail/HexTail.csproj`; leave that file alone.
- Prefix shell commands with `rtk`. Explore with `sb` (`sb show <file> <Symbol>`, `sb callers <Symbol>`) before opening whole files.
- Build/test commands (run from the repo root):
  - `rtk dotnet build src/HexTail.Tests`
  - `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~<Name>"`
  - full suite: `rtk dotnet test src/HexTail.Tests --no-build`. Baseline at the start: **566 passing, 53 build warnings** (do not add warnings).
- Tests must never touch the OS credential vault: every `new AppState(...)` in a test passes `credentials: new InMemoryCredentialVault()` (`TestWindow.Create*` already does). Tests never call a real Kibana/Elasticsearch: use `FakeElasticApiClient` or `RecordingHttpMessageHandler`.
- UI tests wait through `UiWait.Until(...)`; never assert immediately after an async operation. Debounce timers in view models take the owner's `Scheduler` so tests control time.
- ReactiveUI commands keep the positional scheduler pattern (`ReactiveCommand.Create*(func, scheduler)`). Every command whose body can throw catches everything except `OperationCanceledException` and shows it on the view model's `Error` (lesson of the 2B/2C final reviews: no unobserved `ThrownExceptions`).
- Reflection bindings are used in XAML (compiled bindings are off). Every interactive control gets an `AutomationProperties.Name`. Secrets stay in the OS keyring via `AppState`; they are never written to the session JSON, never logged, and never shown in an error message.
- Old sessions must still load: the persisted shapes (`ElasticConnectionSettings`, `ElasticViewSettings`, `ElasticSourceSettings`) do not change. `AppConfig.Version` stays 2.
- Known flaky test: `AccessibilityTests.SettingsFocus…` (fixed in #11; if it flakes again, report it, do not paper over it).

### Decisions taken where the proposal left room (veto points)

1. **The flow is a page in the workspace area** (the grid cell of `LogWorkspace`; sidebar and status bar stay visible), not the Settings modal and not a side sheet. This avoids the Settings focus pull-back machinery entirely. It closes with a Close button and `Esc` and never prompts.
2. **The persisted model is unchanged.** One flow edits exactly one connection and one *view* (a view = data view + filter field + 1..n filter values + columns; each filter value is one source). "Add source" creates a new view on a connection. *One merged stream* for several values is phase 4 and is **not** built; several values give several sources.
3. **Steps:** 1 Connection, 2 Data view, 3 Filter, 4 Columns, 5 Name & open. Entry points: *new* (sidebar `+`, empty-state button, palette) starts at 1; *Edit connection* starts at 1; *Edit source* starts at 3 (Filter); *Add source* to an existing connection starts at 2. Back/Next move between steps; the step list on the left lets the user jump to any step already reached.
4. **Autosave per step instead of the interstitial.** Leaving step 1 with *Next* saves the connection (and its secret) **without the in-progress view** (`SaveConnectionOnlyAsync`). Steps 2–4 hold state in memory. Step 5 saves the view and opens it. Closing mid-flow keeps what was saved at step 1 and drops the unfinished view silently. "Unsaved Elastic changes / Save / Discard / Keep editing" is deleted.
5. **Connection test is automatic and per endpoint.** Editing any connection field (URLs, Cloud ID, auth mode, username, secret) invalidates the last result and, after a 700 ms debounce, runs the test if the fields are valid. The result lists three rows: Kibana (`✓ N data views`), Elasticsearch, Credentials (`stored in keyring` once saved, otherwise `will be stored in keyring on Next`). A failing endpoint does not hide the other one. *Next* is enabled when both endpoints passed for the current values. The old `Status`/`Error` strings keep their meaning for existing tests.
6. **Cloud ID** is an optional field above the two URLs. Decoding fills both URLs (still editable); an invalid Cloud ID shows an error and changes nothing.
7. **Data view step** is a searchable list (substring match on title) with the selected item's timestamp field shown after its metadata loads; *Next* stays disabled until the metadata resolves (`View.IsSelectionResolved`).
8. **Filter step:** a searchable-fields-only combo (non-searchable fields are not offered), values entered as chips (Enter or comma adds one), and per-value `~N docs · last 15 min` from `CountAsync` (`0 docs` is shown as a warning). Legacy namespace field/value inputs move behind an *Advanced* expander; the "(not queried)" prose is removed.
9. **Columns step:** an available list (suggested fields first: timestamp-like and `message`/`level`/`service`-like names) and a selected list reordered by dragging (`ListReorderBehavior`) or by Up/Down buttons; below them a preview of up to five recent real documents rendered through `ElasticDocumentMapper.Map` exactly as rows will appear (`PreviewAsync`).
10. **Name & open:** the name is prefilled from the data view title (and first filter value); *Open now* is checked by default and opens every source of the view after saving.
11. **Discover URL import is optional** and last: a paste box on step 1 ("Paste a Kibana Discover URL to prefill"). It reads the `_g`/`_a` Rison state and prefills only: the Kibana origin (only when the Kibana URL is still blank), the data view id, the columns, and — if the KQL query is a single `field : value` clause — the filter field and value. Everything else is listed as "ignored". It never overwrites a field the user already filled in step 3/4 without the user pressing *Apply*.
12. **Settings sections become Appearance (0) and Limits (1).** `SectionIndex` clamps to 0..1; `SetUpElasticCommand` opens the setup page, not Settings.

### Not in Phase 3

Merged streams, a time-range prefill from `_g.time`, the Kibana/Elasticsearch version in the test result (the API calls do not return it), importing Discover filters/pills, multi-select of existing sources, a real "draft store" for abandoned setups, and the deferred items in `.superpowers/sdd/progress.md`.

## File Structure

| Path | Status | Responsibility |
| --- | --- | --- |
| `src/HexTail/Elastic/CloudId.cs` | create | `CloudId.TryDecode(string, out CloudIdEndpoints?)` |
| `src/HexTail/Elastic/Rison.cs` | create | minimal Rison parser used by the Discover import |
| `src/HexTail/Elastic/DiscoverUrlImport.cs` | create | `DiscoverUrlImport.TryParse(string)` → `DiscoverImport` |
| `src/HexTail/Elastic/IElasticApiClient.cs`, `ElasticApiClient.cs`, `ElasticModels.cs` | modify | `CountAsync`, `PreviewAsync`, shared hit mapper |
| `src/HexTail/ViewModels/ElasticConnectionEditorViewModel.cs` | modify | Cloud ID, per-endpoint results, debounced auto-test, `SaveConnectionOnlyAsync` |
| `src/HexTail/ViewModels/ElasticViewEditorViewModel.cs`, `ElasticSourceSettingViewModel.cs` | modify | reorderable output fields, filter chips + counts, preview rows, default name, suggested fields |
| `src/HexTail/ViewModels/ElasticSetupViewModel.cs` | create | steps, gating, entry points, autosave, finish & open, Discover state |
| `src/HexTail/Views/ElasticSetupPage.axaml(.cs)` | create | the page: step list + one panel per step |
| `src/HexTail/Views/LogWorkspace.axaml`, `Views/SourceSidebar.axaml`, `ViewModels/MainWindowViewModel.cs`, `MainWindow.axaml.cs` | modify | host the page; entry points; `Esc` |
| `src/HexTail/Views/SettingsPanel.axaml`, `ViewModels/SettingsViewModel.cs` | modify | delete the Elastic tab, drafts interstitial, delete confirmation UI that no longer applies |
| `src/HexTail.Tests/Support/FakeElasticApiClient.cs` | modify | `CountHandler`, `PreviewHandler` |
| `README.md`, `docs/user-guide.md`, `docs/architecture.md` | modify | document the flow |

---

### Task 1: Cloud ID, Rison and Discover URL parsers (pure logic)

**Files:**
- Create: `src/HexTail/Elastic/CloudId.cs`, `src/HexTail/Elastic/Rison.cs`, `src/HexTail/Elastic/DiscoverUrlImport.cs`
- Test: `src/HexTail.Tests/Elastic/CloudIdTests.cs`, `RisonTests.cs`, `DiscoverUrlImportTests.cs` (create)

**Interfaces:**
- Produces (later tasks rely on these exact names):

```csharp
namespace HexTail.Elastic;
public sealed record CloudIdEndpoints(string ElasticsearchUrl, string? KibanaUrl);
public static class CloudId { public static bool TryDecode(string value, out CloudIdEndpoints? endpoints); }
public static class Rison { public static bool TryParse(string text, out object? value); }   // Dictionary<string,object?>, List<object?>, string, double, bool, null
public sealed record DiscoverImport(
    string? KibanaOrigin, string? DataViewId, IReadOnlyList<string> Columns,
    string? FilterField, string? FilterValue, IReadOnlyList<string> Ignored);
public static class DiscoverUrlImport { public static bool TryParse(string url, out DiscoverImport? import); }
```

- [ ] **Step 1: Write the failing tests**

`CloudIdTests.cs`:

```csharp
using HexTail.Elastic;

namespace HexTail.Tests.Elastic;

public sealed class CloudIdTests
{
    private static string Encode(string payload) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));

    [Fact]
    public void DecodesElasticsearchAndKibanaHosts()
    {
        var id = $"my-deployment:{Encode("us-east-1.aws.found.io$es123$kb456")}";

        Assert.True(CloudId.TryDecode(id, out var endpoints));
        Assert.Equal("https://es123.us-east-1.aws.found.io", endpoints!.ElasticsearchUrl);
        Assert.Equal("https://kb456.us-east-1.aws.found.io", endpoints.KibanaUrl);
    }

    [Fact]
    public void KeepsAnExplicitPortAndAllowsAMissingKibanaId()
    {
        var id = $"x:{Encode("host.example.com:9243$es1")}";

        Assert.True(CloudId.TryDecode(id, out var endpoints));
        Assert.Equal("https://es1.host.example.com:9243", endpoints!.ElasticsearchUrl);
        Assert.Null(endpoints.KibanaUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-colon")]
    [InlineData("name:!!!notbase64!!!")]
    [InlineData("name:aG9zdA==")] // "host": no $ separators
    public void RejectsMalformedIds(string value)
    {
        Assert.False(CloudId.TryDecode(value, out var endpoints));
        Assert.Null(endpoints);
    }

    [Fact]
    public void TrimsWhitespace()
    {
        var id = $"  n:{Encode("h.io$e$k")}  ";

        Assert.True(CloudId.TryDecode(id, out _));
    }
}
```

`RisonTests.cs`:

```csharp
using HexTail.Elastic;

namespace HexTail.Tests.Elastic;

public sealed class RisonTests
{
    [Fact]
    public void ParsesObjectsArraysStringsAndLiterals()
    {
        Assert.True(Rison.TryParse("(a:1,b:!t,c:!f,d:!n,e:'x y',f:!(1,two,'3'))", out var value));

        var map = Assert.IsType<Dictionary<string, object?>>(value);
        Assert.Equal(1d, map["a"]);
        Assert.Equal(true, map["b"]);
        Assert.Equal(false, map["c"]);
        Assert.Null(map["d"]);
        Assert.Equal("x y", map["e"]);
        Assert.Equal(new object?[] { 1d, "two", "3" }, Assert.IsType<List<object?>>(map["f"]).ToArray());
    }

    [Fact]
    public void ParsesNestedObjectsAndEscapedQuotes()
    {
        Assert.True(Rison.TryParse("(q:(language:kuery,query:'name : !'Bob!''))", out var value));

        var query = Assert.IsType<Dictionary<string, object?>>(
            Assert.IsType<Dictionary<string, object?>>(value)["q"]
        );
        Assert.Equal("name : 'Bob'", query["query"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(a:")]
    [InlineData("(a:1")]
    [InlineData("!(1,2")]
    [InlineData("'unterminated")]
    public void RejectsMalformedText(string text) => Assert.False(Rison.TryParse(text, out _));
}
```

`DiscoverUrlImportTests.cs`:

```csharp
using HexTail.Elastic;

namespace HexTail.Tests.Elastic;

public sealed class DiscoverUrlImportTests
{
    private const string Modern =
        "https://kibana.example.com:5601/app/discover#/?_g=(filters:!(),time:(from:now-15m,to:now))"
        + "&_a=(columns:!(service,message),dataSource:(dataViewId:abc-123,type:dataView),"
        + "query:(language:kuery,query:'service : checkout'),sort:!(!('@timestamp',desc)))";

    [Fact]
    public void ReadsDataViewColumnsAndASimpleQuery()
    {
        Assert.True(DiscoverUrlImport.TryParse(Modern, out var import));

        Assert.Equal("https://kibana.example.com:5601", import!.KibanaOrigin);
        Assert.Equal("abc-123", import.DataViewId);
        Assert.Equal(["service", "message"], import.Columns);
        Assert.Equal("service", import.FilterField);
        Assert.Equal("checkout", import.FilterValue);
        Assert.Contains("sort", import.Ignored);
        Assert.Contains("time range", import.Ignored);
    }

    [Fact]
    public void ReadsTheLegacyIndexParameterAndQuotedValues()
    {
        var url =
            "https://k.example.com/app/discover#/?_a=(columns:!(a),index:'legacy-id',"
            + "query:(language:kuery,query:'host.name : \"web 1\"'))";

        Assert.True(DiscoverUrlImport.TryParse(url, out var import));

        Assert.Equal("legacy-id", import!.DataViewId);
        Assert.Equal("host.name", import.FilterField);
        Assert.Equal("web 1", import.FilterValue);
    }

    [Fact]
    public void ComplexQueriesAreIgnoredNotGuessed()
    {
        var url =
            "https://k/app/discover#/?_a=(query:(language:kuery,query:'a : 1 and b : 2'))";

        Assert.True(DiscoverUrlImport.TryParse(url, out var import));

        Assert.Null(import!.FilterField);
        Assert.Null(import.FilterValue);
        Assert.Contains("query", import.Ignored);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://example.com/no-fragment")]
    [InlineData("https://k/app/discover#/?nothing=here")]
    public void RejectsUrlsWithoutDiscoverState(string url) =>
        Assert.False(DiscoverUrlImport.TryParse(url, out _));
}
```

- [ ] **Step 2: Run, expect compile errors** (`rtk dotnet build src/HexTail.Tests`).

- [ ] **Step 3: Implement**

`CloudId.cs`:

```csharp
using System.Text;

namespace HexTail.Elastic;

public sealed record CloudIdEndpoints(string ElasticsearchUrl, string? KibanaUrl);

/// <summary>Decodes an Elastic Cloud ID (<c>name:base64(host$elasticsearchId$kibanaId)</c>).</summary>
public static class CloudId
{
    public static bool TryDecode(string value, out CloudIdEndpoints? endpoints)
    {
        endpoints = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        var colon = text.LastIndexOf(':');
        if (colon < 0 || colon == text.Length - 1)
            return false;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(Convert.FromBase64String(text[(colon + 1)..]));
        }
        catch (FormatException)
        {
            return false;
        }

        var parts = decoded.Split('$');
        if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
            return false;
        var host = parts[0];
        var elasticsearch = $"https://{parts[1]}.{host}";
        var kibana = parts.Length > 2 && parts[2].Length > 0 ? $"https://{parts[2]}.{host}" : null;
        if (!Uri.TryCreate(elasticsearch, UriKind.Absolute, out _))
            return false;
        endpoints = new CloudIdEndpoints(elasticsearch, kibana);
        return true;
    }
}
```

`Rison.cs`:

```csharp
using System.Globalization;
using System.Text;

namespace HexTail.Elastic;

/// <summary>Minimal Rison reader (objects, arrays, strings, !t !f !n, numbers, bare identifiers).</summary>
public static class Rison
{
    public static bool TryParse(string text, out object? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var parser = new Parser(text.Trim());
        if (!parser.TryReadValue(out value) || !parser.AtEnd)
        {
            value = null;
            return false;
        }

        return true;
    }

    private sealed class Parser(string text)
    {
        private int _index;

        public bool AtEnd => _index >= text.Length;

        public bool TryReadValue(out object? value)
        {
            value = null;
            if (AtEnd)
                return false;
            switch (text[_index])
            {
                case '(':
                    return TryReadObject(out value);
                case '!':
                    return TryReadBang(out value);
                case '\'':
                    if (!TryReadString(out var quoted))
                        return false;
                    value = quoted;
                    return true;
                default:
                    return TryReadBare(out value);
            }
        }

        private bool TryReadBang(out object? value)
        {
            value = null;
            if (_index + 1 >= text.Length)
                return false;
            switch (text[_index + 1])
            {
                case 't':
                    _index += 2;
                    value = true;
                    return true;
                case 'f':
                    _index += 2;
                    value = false;
                    return true;
                case 'n':
                    _index += 2;
                    return true;
                case '(':
                    _index++;
                    return TryReadArray(out value);
                default:
                    return false;
            }
        }

        private bool TryReadArray(out object? value)
        {
            value = null;
            _index++; // '('
            var list = new List<object?>();
            if (Peek() == ')')
            {
                _index++;
                value = list;
                return true;
            }

            while (true)
            {
                if (!TryReadValue(out var item))
                    return false;
                list.Add(item);
                if (AtEnd)
                    return false;
                var next = text[_index++];
                if (next == ')')
                {
                    value = list;
                    return true;
                }

                if (next != ',')
                    return false;
            }
        }

        private bool TryReadObject(out object? value)
        {
            value = null;
            _index++; // '('
            var map = new Dictionary<string, object?>(StringComparer.Ordinal);
            if (Peek() == ')')
            {
                _index++;
                value = map;
                return true;
            }

            while (true)
            {
                string key;
                if (Peek() == '\'')
                {
                    if (!TryReadString(out key))
                        return false;
                }
                else if (!TryReadToken(out key))
                    return false;
                if (AtEnd || text[_index++] != ':')
                    return false;
                if (!TryReadValue(out var item))
                    return false;
                map[key] = item;
                if (AtEnd)
                    return false;
                var next = text[_index++];
                if (next == ')')
                {
                    value = map;
                    return true;
                }

                if (next != ',')
                    return false;
            }
        }

        private bool TryReadString(out string value)
        {
            value = string.Empty;
            _index++; // opening quote
            var builder = new StringBuilder();
            while (_index < text.Length)
            {
                var c = text[_index++];
                if (c == '\'')
                {
                    value = builder.ToString();
                    return true;
                }

                if (c != '!')
                {
                    builder.Append(c);
                    continue;
                }

                if (_index >= text.Length)
                    return false;
                var escaped = text[_index++];
                if (escaped is not ('!' or '\''))
                    return false;
                builder.Append(escaped);
            }

            return false;
        }

        private bool TryReadToken(out string token)
        {
            var start = _index;
            while (_index < text.Length && !IsStructural(text[_index]))
                _index++;
            token = text[start.._index];
            return token.Length > 0;
        }

        private bool TryReadBare(out object? value)
        {
            value = null;
            if (!TryReadToken(out var token))
                return false;
            value = double.TryParse(
                token,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number
            )
                ? number
                : token;
            return true;
        }

        private char Peek() => _index < text.Length ? text[_index] : '\0';

        private static bool IsStructural(char c) =>
            c is '(' or ')' or ',' or ':' or '!' or '\'' || char.IsWhiteSpace(c);
    }
}
```

`DiscoverUrlImport.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;
using System.Web;

namespace HexTail.Elastic;

public sealed record DiscoverImport(
    string? KibanaOrigin,
    string? DataViewId,
    IReadOnlyList<string> Columns,
    string? FilterField,
    string? FilterValue,
    IReadOnlyList<string> Ignored
);

/// <summary>Reads the <c>_a</c>/<c>_g</c> Rison state of a Kibana Discover URL.</summary>
public static partial class DiscoverUrlImport
{
    [GeneratedRegex("""^\s*([A-Za-z0-9_.@\-]+)\s*:\s*(?:"([^"]*)"|([^\s()"]+))\s*$""")]
    private static partial Regex SingleClause();

    public static bool TryParse(string url, out DiscoverImport? import)
    {
        import = null;
        if (
            !Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
        )
            return false;

        // Discover keeps its state in the fragment ("#/?_a=…&_g=…"); older links use the query string.
        var fragment = uri.Fragment.TrimStart('#');
        var questionMark = fragment.IndexOf('?');
        var queryText =
            questionMark >= 0 ? fragment[(questionMark + 1)..] : uri.Query.TrimStart('?');
        var query = HttpUtility.ParseQueryString(queryText);
        if (
            query["_a"] is not { Length: > 0 } appText
            || !Rison.TryParse(appText, out var parsedApp)
            || parsedApp is not Dictionary<string, object?> app
        )
            return false;

        var ignored = new List<string>();
        if (
            query["_g"] is { Length: > 0 } globalText
            && Rison.TryParse(globalText, out var parsedGlobal)
            && parsedGlobal is Dictionary<string, object?> global
            && global.ContainsKey("time")
        )
            ignored.Add("time range");

        string? dataViewId = null;
        if (
            app.TryGetValue("dataSource", out var source)
            && source is Dictionary<string, object?> sourceMap
            && sourceMap.TryGetValue("dataViewId", out var id)
        )
            dataViewId = AsString(id);
        if (dataViewId is null && app.TryGetValue("index", out var legacy))
            dataViewId = AsString(legacy);

        var columns =
            app.TryGetValue("columns", out var rawColumns) && rawColumns is List<object?> list
                ? list.Select(AsString).OfType<string>().Where(name => name.Length > 0).ToList()
                : [];

        string? field = null;
        string? value = null;
        if (
            app.TryGetValue("query", out var rawQuery)
            && rawQuery is Dictionary<string, object?> queryMap
            && queryMap.TryGetValue("query", out var rawText)
            && AsString(rawText) is { Length: > 0 } kql
        )
        {
            var match = SingleClause().Match(kql);
            if (match.Success)
            {
                field = match.Groups[1].Value;
                value = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value;
            }
            else
                ignored.Add("query");
        }

        foreach (var name in new[] { "sort", "filters" })
            if (app.TryGetValue(name, out var raw) && raw is List<object?> { Count: > 0 })
                ignored.Add(name);

        var path = uri.AbsolutePath;
        var appIndex = path.IndexOf("/app/", StringComparison.Ordinal);
        var origin = uri.GetLeftPart(UriPartial.Authority) + (appIndex > 0 ? path[..appIndex] : "");
        import = new DiscoverImport(origin, dataViewId, columns, field, value, ignored);
        return true;
    }

    private static string? AsString(object? value) =>
        value switch
        {
            string text => text,
            double number => number.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
}
```

- [ ] **Step 4: Run** `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~CloudId|FullyQualifiedName~Rison|FullyQualifiedName~DiscoverUrlImport"` — Expected: PASS. Then the full suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): decode Cloud IDs and read Kibana Discover URLs"
```

---

### Task 2: `CountAsync` and `PreviewAsync` API calls

**Files:**
- Modify: `src/HexTail/Elastic/IElasticApiClient.cs`, `src/HexTail/Elastic/ElasticApiClient.cs`, `src/HexTail.Tests/Support/FakeElasticApiClient.cs`, `src/HexTail/ViewModels/SettingsViewModel.cs` (wrapper methods next to `GetDataViewAsync`)
- Test: `src/HexTail.Tests/Elastic/ElasticApiClientTests.cs`

**Interfaces:**
- Consumes: the existing `SendAsync(baseUrl, connection, secret, method, path, body, ct)`, `ElasticDocumentMapper.Map`, `ElasticHit`.
- Produces:

```csharp
// IElasticApiClient
Task<long> CountAsync(
    ElasticConnectionSettings connection, string? secret, string dataViewTitle, string timeFieldName,
    string filterField, string filterValue, DateTimeOffset from, DateTimeOffset to,
    CancellationToken cancellationToken = default);
Task<IReadOnlyList<ElasticHit>> PreviewAsync(
    ElasticConnectionSettings connection, string? secret, string dataViewTitle, string timeFieldName,
    string? filterField, string? filterValue, IReadOnlyList<string> outputFields, int size,
    CancellationToken cancellationToken = default);
// FakeElasticApiClient
public Func<(string Field, string Value), Task<long>>? CountHandler { get; set; }          // default: returns 0
public Func<IReadOnlyList<string>, Task<IReadOnlyList<ElasticHit>>>? PreviewHandler { get; set; }   // default: empty
public List<(string Field, string Value)> Counts { get; }
// SettingsViewModel
internal Task<long> CountAsync(ElasticConnectionSettings connection, string title, string timeField, string field, string value, string? secret = null);
internal Task<IReadOnlyList<ElasticHit>> PreviewAsync(ElasticConnectionSettings connection, string title, string timeField, string? field, string? value, IReadOnlyList<string> outputFields, string? secret = null);
```

- [ ] **Step 1: Failing tests** — in `ElasticApiClientTests` (copy the arrange style of the existing `SearchAsync` test with `RecordingHttpMessageHandler`):
  1. `CountAsync` sends `POST {elasticsearch}/{escaped title}/_count` with a `bool.filter` of `match_phrase {field: value}` and a `range` on the time field (`gte`/`lte` equal to the arguments), and returns the response's `count` (use `{"count": 3200}`).
  2. `CountAsync` surfaces a 401 as `ElasticUnauthorizedException` (reuse the existing assertion style).
  3. `PreviewAsync` sends `POST …/{title}/_search` with `size` = the argument, `sort: [{timeField: "desc"}]`, `fields` = the output fields, **no** `pit`, and a `match_phrase` filter only when `filterField`/`filterValue` are non-blank; it returns `ElasticHit`s mapped with `ElasticDocumentMapper.Map` (assert `Line.Raw` joins the output field values with a space and `Line.ParsedFields` contains the flattened source).
  4. `PreviewAsync` with no filter sends `match_all` (or an empty `bool.filter`; assert on whichever the implementation chooses, but the body must not contain `match_phrase`).

- [ ] **Step 2: Run, expect compile errors.**

- [ ] **Step 3: Implement.** In `ElasticApiClient`: extract the hit-mapping lambda from `SearchAsync` into `private static ElasticHit MapHit(JsonElement hit, string timeFieldName, IReadOnlyList<string> outputFields)` and use it from both `SearchAsync` and `PreviewAsync` (behaviour of `SearchAsync` must not change; its tests stay green). `CountAsync` body: `{ "query": { "bool": { "filter": [ match_phrase, range ] } } }`, path `$"{Uri.EscapeDataString(dataViewTitle)}/_count"`; read `GetProperty("count").GetInt64()`. `PreviewAsync` body: `size`, `track_total_hits=false`, `sort`, `_source=true`, `fields`, `query` (a `bool.filter` list containing the optional `match_phrase`; an empty list is valid). Check how `SendAsync` joins `baseUrl` and `path` (trailing slashes, escaping) before relying on `EscapeDataString` for patterns like `logs-*,metrics-*`; adjust and say so in your report.

`FakeElasticApiClient`: implement both members with the handlers above (record every count call in `Counts`). `SettingsViewModel`: add the two wrapper methods following `GetDataViewAsync`'s pattern (it forwards to the `IElasticApiClient` held by `State`/`Owner`).

- [ ] **Step 4: Run** `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~ElasticApiClient"`, then the full suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): count documents per filter value and preview recent documents"
```

---

### Task 3: Connection editor — Cloud ID, per-endpoint results, debounced auto-test, connection-only save

**Files:**
- Modify: `src/HexTail/ViewModels/ElasticConnectionEditorViewModel.cs`, `src/HexTail/ViewModels/SettingsViewModel.cs`, `src/HexTail/Application/AppState.cs` (only if a helper is needed)
- Create: `src/HexTail/ViewModels/ElasticEndpointResultViewModel.cs`
- Test: `src/HexTail.Tests/ViewModels/ElasticConnectionEditorTests.cs` (create); existing `ElasticSettingsViewModelTests` must stay green

**Interfaces:**
- Consumes: `CloudId.TryDecode`, existing `TestConnectionCommand`, `InvalidateConnectionTest`, `ToSettings(includeViews)`, `SettingsViewModel.SaveElasticConnectionAsync(ElasticConnectionSettings, string)`.
- Produces:

```csharp
internal enum EndpointState { NotTested, Checking, Ok, Failed }
internal sealed class ElasticEndpointResultViewModel : ReactiveObject
{
    public string Label { get; }                 // "Kibana" | "Elasticsearch" | "Credentials"
    public EndpointState State { get; }
    public string Message { get; }               // "✓ 12 data views", "✗ <error>", "Not tested", "Checking…"
    public bool IsOk { get; }
    public bool IsFailed { get; }
}
// ElasticConnectionEditorViewModel
public string CloudId { get; set; }              // setting a decodable value fills KibanaUrl/ElasticsearchUrl
public string? CloudIdError { get; }
public IReadOnlyList<ElasticEndpointResultViewModel> EndpointResults { get; }   // Kibana, Elasticsearch, Credentials
public bool EndpointsOk { get; }                 // Kibana.IsOk && Elasticsearch.IsOk for the current values
public bool CanTestConnection { get; }           // both URLs valid, auth fields complete
public bool IsSaved { get; }                     // a connection with this Id exists in AppState settings
public event EventHandler? ConnectionEdited;     // raised from InvalidateConnectionTest()
internal Task<bool> SaveConnectionOnlyAsync();   // saves ToSettings(includeViews:false) + the saved views, returns success
```

- [ ] **Step 1: Failing tests** (`ElasticConnectionEditorTests`, building editors from `TestWindow`/`ElasticFixtures.Workspace(...)` and a `FakeElasticApiClient`; model the arrange code on `ElasticSettingsViewModelTests`):
  1. Setting `CloudId` to a valid id fills both URLs and clears `CloudIdError`; an invalid id sets `CloudIdError` and leaves the URLs untouched; clearing it leaves the URLs as they are.
  2. `TestConnectionCommand` with `DataViewsHandler` returning two views and a healthy Elasticsearch → `EndpointResults[0]` (Kibana) is `Ok` with message `"✓ 2 data views"`, `[1]` Ok, `EndpointsOk` true, `Status` still `"Connected (2 data views)"` (old contract).
  3. With `ElasticsearchError` set on the fake: Kibana row is `Ok`, Elasticsearch row `Failed` containing the exception message, `EndpointsOk` false, and `Error`/`Status == "Connection failed"` as before.
  4. With `DataViewError` set: the reverse.
  5. Editing the secret/URL/auth after a successful test resets all rows to `NotTested` and `EndpointsOk` to false, and raises `ConnectionEdited`.
  6. `CanTestConnection` is false for an invalid URL, for Basic auth without username/secret, and for ApiKey without secret.
  7. `SaveConnectionOnlyAsync` on a new editor persists the connection with **zero views**; on an editor for a saved connection that has a saved view and an in-progress (incomplete) second view, the saved view is kept and the incomplete one is not written. The secret goes to the credential vault (assert via `InMemoryCredentialVault.Get(id)`), not into `persistence.Config`.

- [ ] **Step 2: Run, expect compile failures.**

- [ ] **Step 3: Implement.**
  - `CloudId`: a string property; in the setter, if non-blank and `HexTail.Elastic.CloudId.TryDecode` succeeds set `ElasticsearchUrl` (and `KibanaUrl` when the id carries one), clear the error; if non-blank and decoding fails set `CloudIdError = "Enter a valid Cloud ID (name:base64…)."`. Not persisted and not part of `DraftSnapshot`.
  - Split `TestConnectionAsync` so the two checks run independently: run `GetDataViewsAsync()` and `CheckElasticsearchAsync(...)` as two tasks, await both with individual `try/catch`, set each `EndpointResults` row, keep the existing combined `Status`/`Error` semantics (`Connected (N data views)` only when both passed; on any failure `Status = "Connection failed"` and `Error` = the first failing message) and keep the existing refresh of `DataViews` and the per-view `RefreshDataViewAsync` calls when the Kibana call succeeded. The "Credentials" row: `Ok` with `"stored in keyring"` when `IsSaved` and the auth mode needs a secret, `"will be stored in keyring on Next"` (State `NotTested`) when not yet saved, `"no credentials (anonymous)"` for anonymous.
  - `InvalidateConnectionTest()` also resets the three rows to `NotTested` and raises `ConnectionEdited`; make sure every connection-field setter (`KibanaUrl`, `ElasticsearchUrl`, `AuthMode`, `Username`, `Secret`, and Cloud ID decode) reaches it exactly once per change (check the existing setters at lines ~52-118).
  - `CanTestConnection` and `EndpointsOk` as specified; raise them from the same places as `CanSave`.
  - `SaveConnectionOnlyAsync`: compute `saved = _owner.SavedViews(Id)` (a new `SettingsViewModel` helper returning the persisted `ElasticViewSettings` list for the connection from `State.Settings`), call `_owner.SaveElasticConnectionAsync(ToSettings(includeViews:false) with { Views = saved }, Secret)`, then clear `Secret` if unchanged, update `_savedSnapshot`'s connection fields only (the draft snapshot still contains the in-progress view), set `IsSaved`; catch everything except cancellation into `Error`/return false (same shape as `SaveDraftAsync`).

- [ ] **Step 4: Run** the new tests, `ElasticSettingsViewModel*`, `ElasticDraft*`, `ElasticSetup*`, then the full suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): decode Cloud IDs and report each endpoint of a connection test"
```

---

### Task 4: View editor — reorderable columns, filter chips with counts, preview, suggestions

**Files:**
- Modify: `src/HexTail/ViewModels/ElasticViewEditorViewModel.cs`, `src/HexTail/ViewModels/ElasticSourceSettingViewModel.cs`, `src/HexTail/ViewModels/ElasticFieldOptionViewModel.cs`
- Create: `src/HexTail/ViewModels/ElasticPreviewRowViewModel.cs`
- Test: `src/HexTail.Tests/ViewModels/ElasticViewEditorTests.cs` (create); existing `ElasticSettingsViewModelTests`, `FieldDiscoveryTests` stay green

**Interfaces:**
- Consumes: `FakeElasticApiClient.CountHandler/PreviewHandler`, `SettingsViewModel.CountAsync/PreviewAsync` (Task 2), the existing `_outputFieldOrder`, `Sources`, `Fields`, `RefreshVisibleFields()`.
- Produces:

```csharp
// ElasticViewEditorViewModel
public ObservableCollection<string> SelectedOutputFields { get; }        // replaces the private List; same order semantics
public IReadOnlyList<ElasticFieldOptionViewModel> SuggestedFields { get; } // unselected fields ranked: timestamp, message, level, service, host…
public void MoveOutputField(int from, int to);
public bool AddFilterValue(string value);        // trims; false when blank or duplicate; adds a source and starts a count
public void RemoveFilterValue(ElasticSourceSettingViewModel source);
public ReactiveCommand<Unit, Unit> RefreshCountsCommand { get; }
public ObservableCollection<ElasticPreviewRowViewModel> PreviewRows { get; }
public string? PreviewMessage { get; }           // "Loading preview…", "No documents in the last 15 minutes", error text, or null
public ReactiveCommand<Unit, Unit> RefreshPreviewCommand { get; }
public IEnumerable<ElasticFieldOptionViewModel> SearchableFields { get; }  // for the filter combo
public string SuggestedName { get; }             // "{DataViewTitle}" or "{DataViewTitle} · {first value}"
// ElasticSourceSettingViewModel
public string CountText { get; }                 // "", "counting…", "~3.2k docs · 15 min", "0 docs · 15 min", "count failed"
public bool IsZeroCount { get; }
// ElasticPreviewRowViewModel
public string Text { get; }                      // ElasticHit.Line.Raw
public string Time { get; }
```

- [ ] **Step 1: Failing tests** (`ElasticViewEditorTests`; build a resolved editor as `ElasticSettingsViewModelTests` does, with a fake client whose `DataViewFields` contains `@timestamp`, `message`, `service` (searchable keyword), `bytes` (not searchable)):
  1. `MoveOutputField(0, 2)` reorders `SelectedOutputFields` and `ToSettings().OutputFields`; out-of-range indices are ignored; `RowPreview` updates.
  2. Toggling `IsOutput` on a field keeps `SelectedOutputFields` in sync (add at the end, remove) exactly as before (`SelectedFieldsSummary` unchanged for the old tests).
  3. `SearchableFields` excludes `bytes`; `SuggestedFields` lists `@timestamp` and `message` ahead of other names and never includes already-selected fields.
  4. `AddFilterValue("checkout")` adds a source, returns true, then `CountText` goes `counting…` → `~3.2k docs · 15 min` (use `CountHandler` returning 3200; formatting: `<1000` exact, else one decimal with `k`, `1_000_000+` with `M`); a duplicate or blank value returns false and adds nothing; `CountHandler` returning 0 sets `IsZeroCount`; a throwing handler sets `CountText = "count failed"` and does not throw to the caller.
  5. `RemoveFilterValue` removes the source; removing the last leaves one empty source (existing invariant).
  6. Changing `ServerField` recounts every value (one `Counts` entry per value with the new field).
  7. `RefreshPreviewCommand` with a `PreviewHandler` returning two hits fills `PreviewRows` (`Text == hit.Line.Raw`) and clears `PreviewMessage`; zero hits sets the "No documents…" message; a throwing handler sets the error text and does not throw.
  8. Stale results are dropped: start a count, change `ServerField`, complete the first count afterwards — the first result must not overwrite `CountText` (use `TaskCompletionSource` in the handler).
  9. `SuggestedName` is `"logs-*"` with no value and `"logs-* · checkout"` with a value.

- [ ] **Step 2: Run, expect compile failures.**

- [ ] **Step 3: Implement.** Turn `_outputFieldOrder` into the `ObservableCollection<string> SelectedOutputFields` (keep `OutputFieldNames` returning it; subscribe `CollectionChanged` → `NotifyPrerequisitesChanged()`); `MoveOutputField` uses `Move`. Counting and preview use `_owner` (connection editor) → `SettingsViewModel.CountAsync/PreviewAsync` with `ToSettings(includeViews:false)` and the editor's secret, over the last 15 minutes (`DateTimeOffset.UtcNow`; take the clock from the owner `Scheduler.Now` if it is already injected, otherwise `DateTimeOffset.UtcNow`). Every count/preview request captures a version number (like `_metadataVersion`) and ignores results from older versions. Counting runs only when the data view is resolved, `ServerField` is set and the source value is non-blank; it is triggered by `AddFilterValue`, by `ServerField` changes, and by `RefreshCountsCommand`. All async bodies catch everything except cancellation. `SuggestedFields` ranks by a fixed priority list (`@timestamp`, `timestamp`, `message`, `log.level`, `level`, `service.name`, `service`, `host.name`, `host`), then the rest alphabetically, capped at 12.

- [ ] **Step 4: Run** the new tests, `ElasticSettingsViewModel*`, `FieldDiscovery*`, `ElasticSetup*`, `ElasticDraft*`, then the full suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): reorder columns, count documents per filter value and preview rows"
```

---

### Task 5: `ElasticSetupViewModel` — steps, gating, autosave, entry points

**Files:**
- Create: `src/HexTail/ViewModels/ElasticSetupViewModel.cs`, `src/HexTail/ViewModels/ElasticSetupStepViewModel.cs`
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs` (property `ElasticSetup`, `SetUpElasticCommand` target, palette entries, `Sync`), `src/HexTail/ViewModels/SettingsViewModel.cs` (helpers used by the wizard: `SavedViews`, `EditorFor(connectionId)`, `AddElasticConnection` returning the editor), `src/HexTail/Application/AppState.cs` (only if opening needs a new method)
- Test: `src/HexTail.Tests/ViewModels/ElasticSetupViewModelTests.cs` (create)

**Interfaces:**
- Consumes: Tasks 3–4 types; `SettingsViewModel.ElasticConnections`, `AddElasticConnectionCommand`, `AppState.OpenElasticSourceAsync(string sourceId, bool save)`.
- Produces:

```csharp
internal enum ElasticSetupStep { Connection, DataView, Filter, Columns, Finish }
internal sealed class ElasticSetupStepViewModel : ReactiveObject
{
    public ElasticSetupStep Step { get; }
    public string Title { get; }                 // "Connection", "Data view", "Filter", "Columns", "Name & open"
    public int Number { get; }                   // 1..5
    public bool IsCurrent { get; }
    public bool IsReachable { get; }             // already visited or the next one
    public bool IsDone { get; }
}
internal sealed class ElasticSetupViewModel : ReactiveObject
{
    internal ElasticSetupViewModel(MainWindowViewModel owner);
    public bool IsOpen { get; }
    public ElasticSetupStep Step { get; }
    public IReadOnlyList<ElasticSetupStepViewModel> Steps { get; }
    public ElasticConnectionEditorViewModel? Connection { get; }
    public ElasticViewEditorViewModel? View { get; }
    public bool CanGoBack { get; }
    public bool CanGoNext { get; }               // per-step gating (below)
    public string NextLabel { get; }             // "Next" | "Save & open" | "Save" when OpenNow is off
    public bool OpenNow { get; set; }            // default true
    public string? Error { get; }
    public bool IsBusy { get; }
    public ReactiveCommand<Unit, Unit> NextCommand { get; }
    public ReactiveCommand<Unit, Unit> BackCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ReactiveCommand<ElasticSetupStepViewModel, Unit> GoToStepCommand { get; }
    internal void StartNew();
    internal void EditConnection(string connectionId);
    internal void EditView(string connectionId, string viewId);   // starts at Filter
    internal void AddSource(string connectionId);                 // new view, starts at DataView
    internal void Close();
}
```

Gating (`CanGoNext`): Connection → `Connection.EndpointsOk && !Connection.IsTesting`; DataView → `View.IsSelectionResolved && !View.IsLoading && !string.IsNullOrWhiteSpace(View.SelectedDataViewId)`; Filter → `View.FilterFieldError is null && View.FilterValueError is null`; Columns → `View.OutputFieldsError is null`; Finish → `View.CanSave`. `NextCommand`'s `canExecute` observes these (re-evaluate on every relevant `PropertyChanged` from `Connection`/`View`).

- [ ] **Step 1: Failing tests** (`ElasticSetupViewModelTests`; fixture: `ElasticFixtures.Workspace(...)` plus `FakeElasticApiClient` with `DataViews`, `DataViewFields`, `CountHandler`, `PreviewHandler`; state from `TestPersistence`):
  1. `StartNew()` opens at `Connection`, creates a new editor in `Settings.ElasticConnections`, `Steps[0].IsCurrent`, others not reachable, `CanGoNext` false until the connection test passes.
  2. Fill URLs, `await Connection.TestConnectionCommand.Execute()` → `CanGoNext` true; `NextCommand` saves the connection **with no views** (`persistence.Config.Settings.ElasticConnections.Single().Views` empty), `Step == DataView`, a new view exists on the editor.
  3. Selecting a data view (`View.SelectedDataViewId = "logs"`) and waiting for `IsSelectionResolved` enables Next; `Next` → `Filter`. Gating on Filter and Columns follows the table above (assert `CanGoNext` flips with `ServerField`, `AddFilterValue`, and `IsOutput`).
  4. At `Finish`, `NextCommand` saves the full connection (one view, sources = chips, `OutputFields` in the chosen order), and with `OpenNow` true the view's sources are opened (`owner.State.Files` gains one tab per source); with `OpenNow` false they are not. The wizard then closes (`IsOpen` false).
  5. `BackCommand` returns one step and keeps all entered state; `GoToStepCommand` to an unreachable step does nothing, to a reachable one jumps.
  6. `Close()` mid-flow after step 1: the saved connection remains, the unfinished view is gone from the editor, no confirmation state exists (`Settings.CloseConfirmationVisible` is false), `IsOpen` false.
  7. `EditView(connectionId, viewId)` on a saved connection starts at `Filter` with `View` bound to the saved view; `EditConnection` starts at `Connection` with `EndpointsOk` false until re-tested; `AddSource` starts at `DataView` with a fresh view and keeps the other views untouched in storage.
  8. Errors from `SaveElasticConnectionAsync` (make `TestPersistence` throw on save) land in `Error`, keep the step, and do not throw out of the command.
  9. `NextLabel` is `"Next"` before `Finish`, `"Save & open"` at `Finish` with `OpenNow`, `"Save"` otherwise.

- [ ] **Step 2: Run, expect compile failures.**

- [ ] **Step 3: Implement.** `ElasticSetupViewModel` holds `Connection` and `View` (taken from `Settings.ElasticConnections`/`Views`); `StartNew` uses the same code as `AddElasticConnection` (make it return the editor). A **new view** is created with `Connection.AddViewCommand`'s code path (`Views.Add(new ElasticViewEditorViewModel(...) { Name = string.Empty })`); `Name` is filled from `View.SuggestedName` on entering `Finish` if still blank. `NextCommand` is `CreateFromTask` whose body: `IsBusy = true; Error = null; try { switch (Step) { Connection: await Connection.SaveConnectionOnlyAsync() (fail → Error) then ensure a view exists and advance; Finish: await Connection.SaveDraftAsync(), then if OpenNow open every source of this view; Close(); default: advance } } catch (Exception ex) when (ex is not OperationCanceledException) { Error = ex.Message; } finally { IsBusy = false; }`. Opening a view's sources: after `SaveDraftAsync` the persisted view has source ids equal to `View.Sources[i].Id`; open them with the existing open-Elastic-source path (`owner.State.OpenElasticSourceAsync(id, save: true)`; look at how `ElasticSourceOptionViewModel.ActivateCommand` does it and reuse it so selection/health bookkeeping stay consistent). `Close()`: remove an unsaved new connection editor (never saved) and any view editor whose id is not in the saved settings, set `IsOpen=false`, clear `Error`. `MainWindowViewModel`: add `public ElasticSetupViewModel ElasticSetup { get; }`; `SetUpElasticCommand` now calls `ElasticSetup.StartNew()` (not Settings); add palette entries "Set up Elastic" (already exists — retarget) and "Add Elastic source" (only when a connection exists). Do not remove the Settings Elastic tab yet (Task 8).

- [ ] **Step 4: Run** `ElasticSetupViewModel*`, `ElasticSettingsViewModel*`, `Onboarding*`, `Sidebar*`, then the full suite. Existing tests that assert `SetUpElasticCommand` opens Settings (`SidebarTests.cs:275`, `MainWindowInteractionTests`) are updated in Task 8 together with their UI; for now change them to assert `ElasticSetup.IsOpen`.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): add the stepped setup flow view model with per-step autosave"
```

---

### Task 6: Setup page — shell, Connection step, Data view step

**Files:**
- Create: `src/HexTail/Views/ElasticSetupPage.axaml`, `src/HexTail/Views/ElasticSetupPage.axaml.cs`
- Modify: `src/HexTail/Views/LogWorkspace.axaml`, `src/HexTail/MainWindow.axaml.cs` (`Esc` closes the page when it is open and no popup/Settings/palette is), a styles file for step-list classes (reuse existing classes first; `rg -l "sidebar-heading" src/HexTail/Styles`)
- Test: `src/HexTail.Tests/Ui/ElasticSetupPageTests.cs` (create)

**Interfaces:**
- Consumes: Task 5 types. `DataContext` of the page is `ElasticSetupViewModel`; `LogWorkspace` places it in the same grid cell as the log content and shows it while `ElasticSetup.IsOpen`, hiding the log content and the empty state.
- Produces: named controls for tests: `ElasticSetupPage`, `SetupStepList`, `SetupNextButton`, `SetupBackButton`, `SetupCloseButton`, `SetupError`, `CloudIdBox`, `KibanaUrlBox`, `ElasticsearchUrlBox`, `AuthModeBox`, `UsernameBox`, `SecretBox`, `TestConnectionButton`, `EndpointResultsList`, `DataViewSearchBox`, `DataViewList`.

- [ ] **Step 1: Failing UI tests** (`ElasticSetupPageTests`, `[AvaloniaFact]`, window from `TestWindow`/`new MainWindow(owner, registerNativePicker: false)` as `ElasticSetupTests` does):
  1. `owner.ElasticSetup.StartNew()` shows the page (`ElasticSetupPage` effectively visible) and hides the log content; `Close` button closes it; `Esc` closes it (focus not in a text box with an open popup).
  2. The step list shows five steps; the first is current; `SetupNextButton` is disabled until the connection test passes, then enabled (drive via the view model).
  3. Typing a valid Cloud ID into `CloudIdBox` fills `KibanaUrlBox`/`ElasticsearchUrlBox`; an invalid one shows the error text.
  4. `EndpointResultsList` shows three rows with the labels Kibana / Elasticsearch / Credentials and the ✓/✗ messages after a (fake) test; editing a URL resets them to "Not tested".
  5. On the Data view step, typing in `DataViewSearchBox` filters `DataViewList` by title (case-insensitive substring); selecting an item loads metadata and enables Next only afterwards; the timestamp field text appears next to the selected item.
  6. Every interactive control in the page has a non-empty `AutomationProperties.Name` (walk the visual tree like `AccessibilityTests` does).

- [ ] **Step 2: Run, expect failures.**

- [ ] **Step 3: Implement.** Layout: `Grid ColumnDefinitions="220,*"`; left = title "Set up Elastic", `ItemsControl x:Name="SetupStepList"` of `ElasticSetupStepViewModel` buttons (number badge, title, ✓ when done; disabled when not reachable; command `GoToStepCommand`); right = `ScrollViewer` containing one `StackPanel` per step, shown by `IsVisible` converters on `Step` (add three tiny read-only bool properties on the view model, `IsConnectionStep`, `IsDataViewStep`, … so XAML needs no converters); bottom bar with Back, Next (`NextLabel`), Close and the error text. Connection step: Cloud ID `TextBox` (placeholder "Elastic Cloud ID (optional)"), the two URL boxes, `ComboBox` for auth, username/secret boxes (secret = password char `●`), a "Test connection" button (`TestConnectionCommand`), the results list. The debounce: in `ElasticSetupViewModel`, subscribe to `Connection.ConnectionEdited` with `Observable.FromEventPattern(...).Throttle(TimeSpan.FromMilliseconds(700), owner.Scheduler)` and execute the test when `CanTestConnection` (dispose/resubscribe when `Connection` changes); add a test with `TestScheduler`/the headless scheduler that the test runs once after a burst of edits (Task 5 test file). Data view step: `TextBox DataViewSearchBox` bound to `DataViewSearch` (new property on the setup VM exposing a filtered `ObservableCollection<ElasticDataViewChoiceViewModel> FilteredDataViews`), `ListBox DataViewList` with `SelectedItem` bound to `View.SelectedDataView`, a status line with `View.MetadataStatus` / `View.TimestampStatus` / `View.Error`. Do **not** reuse the Settings tab's `Expander` markup; keep the old Settings XAML as-is until Task 8.

- [ ] **Step 4: Run** `ElasticSetupPage*`, `ElasticSetup*`, `Accessibility*`, then the full suite. The Settings focus tests must stay green (the page is not part of Settings).

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): add the setup page with connection and data view steps"
```

---

### Task 7: Setup page — Filter, Columns and Name & open steps

**Files:**
- Modify: `src/HexTail/Views/ElasticSetupPage.axaml`, `ElasticSetupPage.axaml.cs`, `ViewModels/ElasticSetupViewModel.cs` (step visibility flags already exist; add `NewFilterValue` + `AddFilterValueCommand`, `MoveUpCommand`/`MoveDownCommand` parameters, `AdvancedOpen`)
- Test: `src/HexTail.Tests/Ui/ElasticSetupPageTests.cs`

**Interfaces:**
- Consumes: Task 4 view-editor API. Named controls: `FilterFieldBox`, `FilterValueInput`, `FilterChips`, `AdvancedExpander`, `NamespaceFieldBox`, `AvailableFieldsList`, `SelectedFieldsList`, `MoveFieldUpButton`, `MoveFieldDownButton`, `PreviewList`, `RefreshPreviewButton`, `ViewNameBox`, `OpenNowCheck`.

- [ ] **Step 1: Failing UI tests** (drive a resolved view through the fake client as in Task 6):
  1. Filter step: `FilterFieldBox` offers only searchable fields; pressing Enter in `FilterValueInput` after typing `checkout` adds a chip in `FilterChips` showing the value and `~3.2k docs · 15 min` (fake `CountHandler`); typing `a,b` + comma adds two chips; a zero count renders with the `zero-count` class; the remove button on a chip removes it; legacy namespace inputs are inside `AdvancedExpander` and the string "(not queried)" appears nowhere on the page.
  2. Columns step: ticking a field in `AvailableFieldsList` appends it to `SelectedFieldsList`; `MoveFieldUpButton`/`MoveFieldDownButton` with a selected item reorder it (and `ToSettings().OutputFields`); a real drag-reorder through `ListReorderBehavior` is attached to `SelectedFieldsList` (assert the behavior is present; the VM-level reorder is covered in Task 4); the preview shows up to five rows from the fake `PreviewHandler`, and shows "No documents in the last 15 minutes" when empty.
  3. Name & open step: `ViewNameBox` is prefilled with `SuggestedName`; `OpenNowCheck` is checked by default; the Next button reads "Save & open" and clicking it saves, opens a tab per chip, and closes the page.

- [ ] **Step 2: Run, expect failures.**

- [ ] **Step 3: Implement** the three step panels. Filter: `ComboBox` over `View.SearchableFields` (display `Name · Type`), `SelectedItem` ↔ `View.ServerField` through a small `SelectedSearchableField` property on the setup VM (string ↔ option mapping), chip `ItemsControl` over `View.Sources` (value, count text, remove button), the Advanced `Expander` holding the namespace field box and per-chip namespace value. Columns: two `ListBox`es side by side (available = `View.SuggestedFields` then a searchable full list via the existing `OutputFieldQuery`/`VisibleFields`; selected = `View.SelectedOutputFields` with `i:Interaction.Behaviors` → `behaviors:ListReorderBehavior` exactly as the sidebar attaches it — look at `SourceSidebar.axaml`), Up/Down buttons, preview `ItemsControl` of `PreviewRows` in the monospace font with the `PreviewMessage` line. Finish: name box, `OpenNow` check, summary text (connection name, data view, `N sources`, `columns: a → b → c`). Entering the Columns step triggers `View.RefreshPreviewCommand`; entering Filter triggers `RefreshCountsCommand` when values exist. Keyboard: Enter in the value box adds; comma splits; Esc closes the page only if no flyout is open.

- [ ] **Step 4: Run** `ElasticSetupPage*`, `ElasticSetup*`, `FieldDiscovery*`, `Accessibility*`, then the full suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): add filter chips with counts, column ordering and preview, and the finish step"
```

---

### Task 8: Entry points, and remove Elastic from Settings

**Files:**
- Modify: `src/HexTail/Views/SourceSidebar.axaml`, `src/HexTail/ViewModels/ElasticConnectionGroupViewModel.cs`, `src/HexTail/ViewModels/ElasticSourceOptionViewModel.cs`, `src/HexTail/Views/LogWorkspace.axaml` (empty-state button already targets `SetUpElasticCommand`), `src/HexTail/Views/SettingsPanel.axaml`, `src/HexTail/ViewModels/SettingsViewModel.cs`, `src/HexTail/MainWindow.axaml.cs`, `src/HexTail/ViewModels/MainWindowViewModel.cs`
- Test: port or delete every test touching the removed Settings Elastic UI: `rg -l "SectionIndex = 1|CloseConfirmation|SaveDraftsAndClose|DiscardDraftsAndClose|KeepEditing|DeleteConfirmation|HasElasticDrafts|AddElasticServerButton|ElasticSettingsScrollViewer" src/HexTail.Tests` (expect `ElasticDraftTests`, `ElasticSetupTests`, `ElasticSettingsViewModelTests`, `MainWindowInteractionTests`, `SidebarTests`, `SettingsReflowTests`, `SettingsSectionTests`, `OnboardingTests`, `ElasticStatusTests`)

**Interfaces:**
- Produces: `ElasticConnectionGroupViewModel.EditCommand` (opens `ElasticSetup.EditConnection(Id)`), `AddSourceCommand` (`ElasticSetup.AddSource(Id)`); `ElasticSourceOptionViewModel.EditCommand` (`ElasticSetup.EditView(ConnectionId, viewId)`); the sidebar shows an edit (pencil) button per connection group and per source row, plus "Add source" in the group header. `SettingsViewModel.SectionIndex` clamps to `0..1` with `Section` `"appearance"`/`"limits"`.

- [ ] **Step 1: Failing tests / ports.**
  - New: sidebar buttons `SidebarEditConnection…` / `SidebarAddSource…` / per-source edit exist with automation names (`Edit Ops`, `Add source to Ops`, `Edit <source>`), and invoking them opens the setup page at the right step (`Connection`, `DataView`, `Filter`).
  - New: Settings has exactly two sections (Appearance, Limits); `SectionIndex = 5` clamps to 1; the Elastic tab, the "Add server" button and the close-confirmation / delete-confirmation panels are gone from `SettingsPanel` (assert by name that none of the removed control names exists).
  - Delete as obsolete: tests that only covered the Settings interstitial and its commands (`ElasticDraftTests` cases about Save/Discard/Keep editing and dirty-close). For each deleted test write one line in the task report saying what replaced its coverage (usually a Task 5/6/7 test). Tests that exercised editor view-model behaviour through `owner.Settings.SectionIndex = 1; SettingsOpen = true` are rewritten to open the setup page (`owner.ElasticSetup.EditConnection(id)`) or, if they only need the editor, to drop the window setup.
  - Removing a connection: the delete action moves to the setup page's Connection step ("Delete connection…" with an inline confirm using the existing `DeleteConfirmationMessage` text); keep one test that deleting closes its source tabs (port from the Settings delete test).

- [ ] **Step 2: Run, expect failures.**

- [ ] **Step 3: Implement.** Remove the Elastic `TabItem` (lines ~96-535 of `SettingsPanel.axaml`), the draft/delete confirmation overlays at the bottom of the panel, and in `SettingsViewModel`: `CloseConfirmationVisible`, `DeleteConfirmationVisible`, `DeleteConfirmationMessage` (move the message builder to the setup VM), `SaveDraftsAndCloseCommand`, `DiscardDraftsAndCloseCommand`, `KeepEditingCommand`, `ConfirmDeleteCommand`/`CancelDeleteCommand` (re-home delete in the setup VM), `HasElasticDrafts`/`CanSaveDrafts`/`CanDiscardDrafts`/`NotifyElasticDraftsChanged` usages in the UI, and make `TryClose()` always `true`; `MainWindow.axaml.cs` no longer needs `OnSettingsDialogClosing` to veto (keep the focus pull-back code untouched). The `ElasticConnections` editor list and the editors' `IsDirty`/`CanSave` stay (the wizard uses them). `SectionIndex` clamp and `Section` mapping per the interface. Update the ledger-listed tests to the new section indexes (Limits = 1).

- [ ] **Step 4: Run** the whole `Ui` and `ViewModels` families, `Accessibility*`, then the full suite twice.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): open the setup flow from the sidebar and remove Elastic from Settings"
```

---

### Task 9: Optional Discover URL import

**Files:**
- Modify: `src/HexTail/ViewModels/ElasticSetupViewModel.cs`, `src/HexTail/Views/ElasticSetupPage.axaml`
- Test: `src/HexTail.Tests/ViewModels/ElasticSetupViewModelTests.cs`, `src/HexTail.Tests/Ui/ElasticSetupPageTests.cs`

**Interfaces:**
- Consumes: `DiscoverUrlImport.TryParse` (Task 1).
- Produces:

```csharp
// ElasticSetupViewModel
public string DiscoverUrl { get; set; }
public string? DiscoverMessage { get; }                 // "Prefilled: Kibana URL, data view, 2 columns, filter service = checkout. Ignored: sort, time range."
public ReactiveCommand<Unit, Unit> ApplyDiscoverUrlCommand { get; }
public bool HasPendingDiscoverImport { get; }
```

- [ ] **Step 1: Failing tests:**
  1. A valid Discover URL on a **new** setup: `ApplyDiscoverUrlCommand` fills `Connection.KibanaUrl` with the origin when it was blank, leaves `ElasticsearchUrl` untouched, sets `DiscoverMessage` listing what was applied and what was ignored, and stores the data view / columns / filter as a *pending import*.
  2. After the connection test passes and the wizard reaches the Data view step, the pending data view id is selected automatically (waits for `DataViews` to contain it; if absent, a message "The data view from the URL was not found in this Kibana." appears and nothing is selected).
  3. Entering Filter / Columns with a pending import applies the filter field/value (only when the field is searchable in the loaded metadata; otherwise ignored with a message) and the columns (only names present in the metadata, in URL order), **only if the user has not yet changed those steps** (`View.ServerField` empty / `SelectedOutputFields` empty); otherwise nothing is overwritten.
  4. With a saved/non-blank Kibana URL the origin is not applied and the message says it was kept.
  5. An invalid URL sets `DiscoverMessage = "That does not look like a Kibana Discover link."` and changes nothing; clearing the box clears the pending import.
  6. UI: the Connection step shows the optional `DiscoverUrlBox` (placeholder "Paste a Kibana Discover URL to prefill (optional)") with an Apply button (`ApplyDiscoverUrlButton`) and the message line; pasting + clicking Apply reflects in `KibanaUrlBox`.

- [ ] **Step 2: Run, expect failures.**

- [ ] **Step 3: Implement** as specified (veto point 11). Keep all import logic in the setup view model (no new service); the import is applied lazily as the user reaches each step so it works with the existing metadata gates.

- [ ] **Step 4: Run** the new tests, `ElasticSetup*`, then the full suite.

- [ ] **Step 5: Commit**

```bash
git add -A
git commit -m "feat(elastic): prefill the setup flow from a pasted Kibana Discover URL"
```

---

### Task 10: Documentation

**Files:**
- Modify: `README.md`, `docs/user-guide.md`, `docs/architecture.md`

- [ ] **Step 1: Update the docs.** Replace every description of setting up Elastic in Settings (`rg -n -i "Add server|Settings.*Elastic|Elastic.*Settings|Test connection|Save server|Unsaved Elastic|namespace|Fields to display|Source filter" README.md docs/user-guide.md docs/architecture.md`) with the stepped flow: entry points (sidebar `+`, the pencil buttons, empty-state button, palette), the five steps (Cloud ID, automatic per-endpoint test and what each row means, searchable data views, filter chips with live counts and that several values give several sources, ordered columns with a real preview, name and *Open now*), autosave semantics (the connection is saved when you leave step 1; closing mid-flow keeps it and drops the unfinished source), the optional Discover URL box (what it prefills and what it ignores), and that Settings now has Appearance and Limits only. Update the architecture component map (`ElasticSetupViewModel`, the setup page, the new parsers and API calls). Do not edit historical plans or `docs/superpowers`.
- [ ] **Step 2:** `rg -n -i "Add server|Unsaved Elastic|Source filter values|Fields to display" README.md docs/user-guide.md docs/architecture.md` — every remaining hit must be intentional.
- [ ] **Step 3: Commit**

```bash
git add -A
git commit -m "docs: describe the stepped Elastic setup flow"
```

---

### Task 11: Final verification

- [ ] **Step 1:** Run the full suite twice: `rtk dotnet test src/HexTail.Tests --no-build` (all green; build shows 53 warnings).
- [ ] **Step 2:** Load a copy of the real `session.json` (never modify the original) through `JsonFileAppPersistence`: confirm the saved Elastic connections, views and sources load unchanged and that opening the setup page on one (`EditConnection`) shows its saved values without prompting for the secret. No network calls.
- [ ] **Step 3:** Report what could not be checked by eye (page layout, step list states, chip layout, drag reorder, the 700 ms debounce feel) and that no real Kibana/Elasticsearch was contacted.

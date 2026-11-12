# UX Phase 4 part C: Timeline Histogram and Scroll Markers Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a collapsible 40 px timeline histogram above every log view (row counts per time bucket, stacked by level; drag to set the range, click to jump, double-click to reset) and an overview ruler beside the log list that marks rule matches and errors, as in VS Code.

**Architecture:** Two pure builders in `Domain` (`TimeHistogram`, `ScrollMarkers`) turn a view's displayed rows into buckets and marker rows. A per-view `LogOverviewViewModel` owns both builds (with a per-line rule-colour cache), the jump commands and the range routing. Two custom `Control.Render` controls (`TimelineHistogram`, `OverviewRuler`) draw them and coalesce redraws. A drag sets a range by source kind: Elastic tabs re-query, streams set the stream range, and file tabs get a new session-only *time window* that filters rows by timestamp.

**Tech Stack:** .NET 10, Avalonia 12.1.1 (custom `Control.Render` with `DrawingContext`, headless tests with `[AvaloniaFact]`), ReactiveUI, xUnit v3.

## Global Constraints

- Histogram height: **40 px** when expanded (a 12 px label strip on top, 28 px of bars). Collapsed: an **18 px** strip with a "Timeline" expand button. Hidden entirely when no displayed row has a timestamp.
- Collapsed/expanded is one app-wide choice, saved in `AppWindowState.HistogramCollapsed` (default `false`) and toggled by the strip's buttons and a palette command, **"Toggle timeline"** (category "View", no gesture).
- Buckets: at least **4 px** wide (`maxBuckets = max(1, floor(width / 4))`), on "nice" intervals from this exact list: 1 s, 2 s, 5 s, 10 s, 15 s, 30 s, 1 m, 2 m, 5 m, 10 m, 15 m, 30 m, 1 h, 2 h, 3 h, 6 h, 12 h, 1 d, 2 d, 7 d; past 7 d, whole multiples of 7 d. Bucket boundaries align in the display time zone (`AppSettings.TimeZoneMode`).
- Stack order bottom → top: Error, Warn, Info, Debug (Trace counts as Debug), Other (no level). Brushes: `ErrorBrush`, `WarnBrush` (new palette key, `#F5B942` in every theme), `SuccessBrush`, `MutedBrush`, `FaintTextBrush`.
- Gestures on the bars: a drag of **4 px or more** sets the range from the start of the first bucket to the end of the last one it covers. A shorter press-release is a click: it jumps to the first displayed row in that bucket. A double-click resets.
- Range routing: **Elastic** tab → `AppState.SetElasticTimeRange` (re-query). **Stream** → `AppState.SetStreamTimeRange(..., live: false)`. **File** tab → the new `AppState.SetFileTimeWindow`. Bounds are sent as round-trip ISO strings (`ToString("O", CultureInfo.InvariantCulture)`).
- Reset (double-click) restores the range in place before the first histogram drag on that tab (`FileTabState.HistogramPrior`). Any range applied from the time pill (or "Show all rows" on a stream) clears `HistogramPrior` and becomes the new baseline. With no prior: a file tab clears its time window, a stream shows all rows (`live: true`), an Elastic tab does nothing.
- File-tab time window: **session-only** (not persisted). Rows without a timestamp always pass. The filter bar shows it as a chip, `Time {TimeRangeLabel.Describe(from, to, zone)}`, with a clear button named **"Clear time window"**.
- Overview ruler: a **10 px** column to the right of the log list, full list height. Two lanes: the left 5 px shows rule matches in the matching search's or rule's colour, and the right 5 px shows errors in `ErrorBrush`. Clicking the ruler selects the row at that height and scrolls it into view. It is hidden when the view has no rows.
- Rule matches = rows matched by any of the tab's `Searches` (pinned rules and user searches; a search view skips its own search) or by an enabled Highlight rule of `EffectiveRules`. The first match wins: searches in list order, then rules in rule order.
- Redraws coalesce: each control rebuilds at most every **250 ms** after the view syncs, and only while it is visible.
- Copy: accessible names **"Timeline histogram"**, **"Collapse timeline"**, **"Expand timeline"**, **"Overview ruler"**, **"Clear time window"**. The bucket tooltip format is fixed in Task 3.
- Tests never touch the OS credential vault or a real Kibana/Elasticsearch (use `InMemoryCredentialVault` and fakes). UI tests are `[AvaloniaFact]`; any plain `[Fact]` that constructs a ReactiveUI object first runs `RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();`.
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Never push.
- Verify compile errors with `dotnet build src/HexTail.Tests 2>&1 | grep " error "` (`rtk dotnet build` can report 0 errors while a test file fails to compile).

---

## File structure

| File | Responsibility |
| --- | --- |
| `src/HexTail/Domain/TimeHistogram.cs` (new) | Pure bucket builder: nice intervals, zone-aligned boundaries, per-level counts, first row per bucket. |
| `src/HexTail/Domain/ScrollMarkers.cs` (new) | Pure marker builder: one entry per displayed row that has a rule colour or an error. |
| `src/HexTail/Domain/RuleSet.cs` | `FirstHighlightColor(Line)`: the first enabled Highlight rule matching the line. |
| `src/HexTail/Application/FileTabState.cs` | `TimeWindow`, `HistogramPrior`, `CreateTimeWindowPredicate()`. |
| `src/HexTail/Application/AppState.cs` | `SetFileTimeWindow`. |
| `src/HexTail/ViewModels/LogViewViewModel.cs` | Apply the file time window in `ApplyFilters`; own `Overview`; `SelectRowAt`. |
| `src/HexTail/ViewModels/LogOverviewViewModel.cs` (new) | Builds, caches, bucket descriptions, jump, range apply/reset routing through the owner. |
| `src/HexTail/ViewModels/MainWindowViewModel.cs` | `ApplyHistogramRange`, `ResetHistogramRange`, prior clearing, `TimelineOpen`, palette command, window-state restore. |
| `src/HexTail/ViewModels/FilterBarViewModel.cs`, `Views/FilterBar.axaml` | Time-window chip. |
| `src/HexTail/Views/TimelineHistogram.cs` (new) | Histogram control: render, hover tooltip, drag/click/double-click. |
| `src/HexTail/Views/OverviewRuler.cs` (new) | Ruler control: render lanes, click to jump. |
| `src/HexTail/Views/LogView.axaml(.cs)` | Layout: histogram row, list + ruler columns. |
| `src/HexTail/Persistence/AppConfig.cs`, `MainWindow.axaml.cs` | `AppWindowState.HistogramCollapsed`. |
| `src/HexTail/ThemeManager.cs`, `Styles/CyberTail.axaml` | `WarnBrush`; the cell `.wrn` style uses it. |
| Tests: `Domain/TimeHistogramTests.cs`, `Domain/ScrollMarkersTests.cs`, `Application/FileTimeWindowTests.cs`, `ViewModels/LogOverviewTests.cs`, `Ui/TimelineHistogramTests.cs`, `Ui/OverviewRulerTests.cs` | |

---

### Task 1: Pure builders — `TimeHistogram`, `ScrollMarkers`, `RuleSet.FirstHighlightColor`

**Files:**
- Create: `src/HexTail/Domain/TimeHistogram.cs`, `src/HexTail/Domain/ScrollMarkers.cs`
- Modify: `src/HexTail/Domain/RuleSet.cs`
- Test: `src/HexTail.Tests/Domain/TimeHistogramTests.cs`, `src/HexTail.Tests/Domain/ScrollMarkersTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record HistogramBucket(DateTimeOffset Start, DateTimeOffset End, int Error, int Warn, int Info, int Debug, int Other, int FirstRow) { public int Total { get; } }`
  - `public sealed class TimeHistogram { public static IReadOnlyList<TimeSpan> Intervals; public static TimeHistogram? Build(IReadOnlyList<Line> rows, int maxBuckets, Func<DateTimeOffset, TimeSpan> zoneOffset); public DateTimeOffset Start; public DateTimeOffset End; public TimeSpan Interval; public IReadOnlyList<HistogramBucket> Buckets; public int MaxTotal; public int BucketIndexAt(double fraction); }`
  - `public readonly record struct ScrollMarker(int Row, string? RuleColor, bool Error)`; `public static class ScrollMarkers { public static IReadOnlyList<ScrollMarker> Build(IReadOnlyList<Line> rows, Func<Line, string?> ruleColor); }`
  - `RuleSet.FirstHighlightColor(Line line) : string?`

- [ ] **Step 1: Write the failing tests**

`src/HexTail.Tests/Domain/TimeHistogramTests.cs`:

```csharp
using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class TimeHistogramTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly Func<DateTimeOffset, TimeSpan> Utc = _ => TimeSpan.Zero;

    private static Line At(DateTimeOffset? time, string raw = "INFO x") =>
        new(raw, null) { Timestamp = time };

    [Fact]
    public void NoTimestamps_GivesNull()
    {
        Assert.Null(TimeHistogram.Build([At(null), At(null)], 100, Utc));
        Assert.Null(TimeHistogram.Build([], 100, Utc));
    }

    [Fact]
    public void PicksTheSmallestNiceIntervalThatFits()
    {
        // 10 minutes of data into at most 20 buckets: 30 s (21 buckets) is too many, 1 m fits (11).
        var rows = new[] { At(T0), At(T0.AddMinutes(10)) };
        var histogram = TimeHistogram.Build(rows, 20, Utc)!;
        Assert.Equal(TimeSpan.FromMinutes(1), histogram.Interval);
        Assert.Equal(T0, histogram.Start);
        Assert.Equal(11, histogram.Buckets.Count);
        Assert.Equal(T0.AddMinutes(11), histogram.End);
    }

    [Fact]
    public void BoundariesAlignInTheDisplayZone()
    {
        // 10:07:30 UTC is 11:07:30 at +01:00; 5-minute buckets start at 11:05 local = 10:05 UTC.
        var rows = new[] { At(T0.AddMinutes(7.5)), At(T0.AddMinutes(30)) };
        var histogram = TimeHistogram.Build(rows, 6, _ => TimeSpan.FromHours(1))!;
        Assert.Equal(TimeSpan.FromMinutes(5), histogram.Interval);
        Assert.Equal(T0.AddMinutes(5), histogram.Start);
    }

    [Fact]
    public void CountsByLevel_AndRemembersTheFirstRowOfEachBucket()
    {
        var rows = new[]
        {
            At(T0.AddSeconds(1), "INFO a"),
            At(T0.AddSeconds(2), "ERROR b"),
            At(null, "untimed"),
            At(T0.AddSeconds(3), "WARN c"),
            At(T0.AddSeconds(4), "DEBUG d"),
            At(T0.AddSeconds(5), "TRACE e"),
            At(T0.AddSeconds(61), "plain f"),
        };
        var histogram = TimeHistogram.Build(rows, 2, Utc)!;
        Assert.Equal(TimeSpan.FromMinutes(1), histogram.Interval);
        var first = histogram.Buckets[0];
        Assert.Equal((1, 1, 1, 2, 0), (first.Error, first.Warn, first.Info, first.Debug, first.Other));
        Assert.Equal(5, first.Total);
        Assert.Equal(0, first.FirstRow);
        var second = histogram.Buckets[1];
        Assert.Equal(1, second.Other);
        Assert.Equal(6, second.FirstRow);
        Assert.Equal(5, histogram.MaxTotal);
    }

    [Fact]
    public void EmptyBucketsHaveNoFirstRow()
    {
        var histogram = TimeHistogram.Build([At(T0), At(T0.AddMinutes(2))], 3, Utc)!;
        Assert.Equal(-1, histogram.Buckets[1].FirstRow);
        Assert.Equal(0, histogram.Buckets[1].Total);
    }

    [Fact]
    public void BeyondSevenDays_UsesWholeWeeks()
    {
        var histogram = TimeHistogram.Build([At(T0), At(T0.AddDays(40))], 3, Utc)!;
        Assert.Equal(TimeSpan.FromDays(21), histogram.Interval);
    }

    [Fact]
    public void SingleInstant_GivesOneOneSecondBucket()
    {
        var histogram = TimeHistogram.Build([At(T0), At(T0)], 50, Utc)!;
        Assert.Equal(TimeSpan.FromSeconds(1), histogram.Interval);
        Assert.Single(histogram.Buckets);
        Assert.Equal(2, histogram.Buckets[0].Total);
    }

    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(0.49, 0)]
    [InlineData(0.5, 1)]
    [InlineData(1.0, 1)]
    [InlineData(-3.0, 0)]
    [InlineData(7.0, 1)]
    public void BucketIndexAt_ClampsToTheBuckets(double fraction, int expected)
    {
        var histogram = TimeHistogram.Build([At(T0), At(T0.AddMinutes(1))], 2, Utc)!;
        Assert.Equal(expected, histogram.BucketIndexAt(fraction));
    }
}
```

`src/HexTail.Tests/Domain/ScrollMarkersTests.cs`:

```csharp
using HexTail.Domain;

namespace HexTail.Tests.Domain;

public sealed class ScrollMarkersTests
{
    private static Line L(string raw) => new(raw, null);

    [Fact]
    public void MarksRuleMatchesAndErrors_InRowOrder()
    {
        var rows = new[] { L("INFO quiet"), L("ERROR boom"), L("INFO token here"), L("ERROR token too") };
        var markers = ScrollMarkers.Build(
            rows,
            line => line.Raw.Contains("token", StringComparison.Ordinal) ? "#123456" : null
        );
        Assert.Equal(
            [
                new ScrollMarker(1, null, true),
                new ScrollMarker(2, "#123456", false),
                new ScrollMarker(3, "#123456", true),
            ],
            markers
        );
    }

    [Fact]
    public void NoMatches_GivesNoMarkers() =>
        Assert.Empty(ScrollMarkers.Build([L("INFO a"), L("WARN b")], _ => null));

    [Fact]
    public void FirstHighlightColor_IsTheFirstMatchingHighlightRule()
    {
        var rules = new RuleSet(
            [
                new ViewRule { Pattern = "zzz", Color = "#000001" },
                new ViewRule { Pattern = "boom", Color = "#000002" },
                new ViewRule { Pattern = "bo", Color = "#000003" },
                new ViewRule { Pattern = "boom", Action = RuleAction.Hide, Color = "#000004" },
                new ViewRule { Pattern = "boom", Enabled = false, Color = "#000005" },
            ]
        );
        Assert.Equal("#000002", rules.FirstHighlightColor(L("ERROR boom")));
        Assert.Null(rules.FirstHighlightColor(L("INFO calm")));
    }
}
```

Check `ViewRule` for the colour property name before running (`sb show src/HexTail/Domain/ViewRule.cs ViewRule`). If it isn't `Color`, use the real name in both tests.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet build src/HexTail.Tests 2>&1 | grep " error "`
Expected: errors naming `TimeHistogram`, `ScrollMarkers`, `ScrollMarker`, `FirstHighlightColor`.

- [ ] **Step 3: Implement**

`src/HexTail/Domain/TimeHistogram.cs`:

```csharp
namespace HexTail.Domain;

/// <summary>One time bucket of a <see cref="TimeHistogram"/>: row counts by level and the first row in it (-1 when empty).</summary>
public sealed record HistogramBucket(
    DateTimeOffset Start,
    DateTimeOffset End,
    int Error,
    int Warn,
    int Info,
    int Debug,
    int Other,
    int FirstRow
)
{
    public int Total => Error + Warn + Info + Debug + Other;
}

/// <summary>
/// Row counts per time bucket for the rows a view displays. Buckets use a "nice" interval, the smallest
/// from <see cref="Intervals"/> that fits in the bucket budget (whole weeks past seven days), with
/// boundaries aligned in the display zone. Rows without a timestamp are not counted.
/// </summary>
public sealed class TimeHistogram
{
    public static IReadOnlyList<TimeSpan> Intervals { get; } =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(3),
        TimeSpan.FromHours(6),
        TimeSpan.FromHours(12),
        TimeSpan.FromDays(1),
        TimeSpan.FromDays(2),
        TimeSpan.FromDays(7),
    ];

    private TimeHistogram(DateTimeOffset start, TimeSpan interval, HistogramBucket[] buckets)
    {
        Start = start;
        Interval = interval;
        Buckets = buckets;
        End = start + interval * buckets.Length;
        MaxTotal = buckets.Max(bucket => bucket.Total);
    }

    public DateTimeOffset Start { get; }
    public DateTimeOffset End { get; }
    public TimeSpan Interval { get; }
    public IReadOnlyList<HistogramBucket> Buckets { get; }
    public int MaxTotal { get; }

    /// <summary>The bucket under a horizontal position (0 = left edge, 1 = right edge), clamped.</summary>
    public int BucketIndexAt(double fraction) =>
        Math.Clamp((int)Math.Floor(fraction * Buckets.Count), 0, Buckets.Count - 1);

    /// <param name="zoneOffset">The display zone's UTC offset at a given instant.</param>
    public static TimeHistogram? Build(
        IReadOnlyList<Line> rows,
        int maxBuckets,
        Func<DateTimeOffset, TimeSpan> zoneOffset
    )
    {
        maxBuckets = Math.Max(1, maxBuckets);
        DateTimeOffset? min = null;
        DateTimeOffset? max = null;
        foreach (var row in rows)
        {
            if (row.Timestamp is not { } time)
                continue;
            if (min is null || time < min)
                min = time;
            if (max is null || time > max)
                max = time;
        }
        if (min is not { } first || max is not { } last)
            return null;

        var offset = zoneOffset(first);
        var interval = ChooseInterval(first, last, offset, maxBuckets);
        var start = AlignDown(first, interval, offset);
        var count = (int)((last - start).Ticks / interval.Ticks) + 1;
        var error = new int[count];
        var warn = new int[count];
        var info = new int[count];
        var debug = new int[count];
        var other = new int[count];
        var firstRow = new int[count];
        Array.Fill(firstRow, -1);
        for (var index = 0; index < rows.Count; index++)
        {
            if (rows[index].Timestamp is not { } time)
                continue;
            var bucket = (int)((time - start).Ticks / interval.Ticks);
            if (firstRow[bucket] < 0)
                firstRow[bucket] = index;
            switch (LevelDetector.Detect(rows[index]))
            {
                case LogLevels.Error:
                    error[bucket]++;
                    break;
                case LogLevels.Warn:
                    warn[bucket]++;
                    break;
                case LogLevels.Info:
                    info[bucket]++;
                    break;
                case LogLevels.Debug or LogLevels.Trace:
                    debug[bucket]++;
                    break;
                default:
                    other[bucket]++;
                    break;
            }
        }
        var buckets = new HistogramBucket[count];
        for (var bucket = 0; bucket < count; bucket++)
            buckets[bucket] = new HistogramBucket(
                start + interval * bucket,
                start + interval * (bucket + 1),
                error[bucket],
                warn[bucket],
                info[bucket],
                debug[bucket],
                other[bucket],
                firstRow[bucket]
            );
        return new TimeHistogram(start, interval, buckets);
    }

    private static TimeSpan ChooseInterval(
        DateTimeOffset first,
        DateTimeOffset last,
        TimeSpan offset,
        int maxBuckets
    )
    {
        foreach (var interval in Intervals)
            if (Fits(interval))
                return interval;
        var week = TimeSpan.FromDays(7);
        for (var weeks = 2L; ; weeks++)
            if (Fits(week * weeks))
                return week * weeks;

        bool Fits(TimeSpan interval)
        {
            var start = AlignDown(first, interval, offset);
            return (last - start).Ticks / interval.Ticks + 1 <= maxBuckets;
        }
    }

    // Floors the instant to a whole interval on the display-zone clock.
    private static DateTimeOffset AlignDown(DateTimeOffset time, TimeSpan interval, TimeSpan offset)
    {
        var local = time.UtcTicks + offset.Ticks;
        var floored = local - local % interval.Ticks;
        return new DateTimeOffset(floored - offset.Ticks, TimeSpan.Zero);
    }
}
```

`src/HexTail/Domain/ScrollMarkers.cs`:

```csharp
namespace HexTail.Domain;

/// <summary>One displayed row that the overview ruler marks: its rule colour (or null) and whether it is an error.</summary>
public readonly record struct ScrollMarker(int Row, string? RuleColor, bool Error);

public static class ScrollMarkers
{
    /// <summary>Markers for the rows that match a rule or are errors, in row order.</summary>
    public static IReadOnlyList<ScrollMarker> Build(
        IReadOnlyList<Line> rows,
        Func<Line, string?> ruleColor
    )
    {
        var markers = new List<ScrollMarker>();
        for (var index = 0; index < rows.Count; index++)
        {
            var color = ruleColor(rows[index]);
            var error = LevelDetector.Detect(rows[index]) == LogLevels.Error;
            if (color is not null || error)
                markers.Add(new ScrollMarker(index, color, error));
        }
        return markers;
    }
}
```

`RuleSet.cs`: add after `Highlights(string text)`:

```csharp
    /// <summary>The colour of the first Highlight rule that matches the line, or null.</summary>
    public string? FirstHighlightColor(Line line)
    {
        foreach (var matcher in _highlight)
            if (matcher.Query.IsMatch(line.Raw))
                return matcher.Color;
        return null;
    }
```

Check `Line`'s constructor and the `Timestamp` init accessor (`sb show src/HexTail/Domain/Line.cs Line`) and fix the test helper if they differ.

- [ ] **Step 4: Run them and watch them pass**

Run: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj --filter "FullyQualifiedName~TimeHistogramTests|FullyQualifiedName~ScrollMarkersTests"`
Expected: all pass.

- [ ] **Step 5: Commit**

```bash
rtk git add src/HexTail/Domain/TimeHistogram.cs src/HexTail/Domain/ScrollMarkers.cs src/HexTail/Domain/RuleSet.cs src/HexTail.Tests/Domain/TimeHistogramTests.cs src/HexTail.Tests/Domain/ScrollMarkersTests.cs
rtk git commit -m "feat(timeline): bucket rows by time and find overview markers"
```

---

### Task 2: Range model — file time window, histogram prior, routing

**Files:**
- Modify: `src/HexTail/Application/FileTabState.cs`, `src/HexTail/Application/AppState.cs`, `src/HexTail/ViewModels/LogViewViewModel.cs` (`ApplyFilters`), `src/HexTail/ViewModels/MainWindowViewModel.cs`, `src/HexTail/ViewModels/FilterBarViewModel.cs`, `src/HexTail/Views/FilterBar.axaml`
- Test: `src/HexTail.Tests/Application/FileTimeWindowTests.cs`

**Interfaces:**
- Consumes: `AppState.SetElasticTimeRange(FileTabState, string, string)`, `AppState.SetStreamTimeRange(FileTabState, string, string, bool, AppTimeZoneMode?)`, `StreamState.RangeFrom/RangeTo/Live`, `MainWindowViewModel.LoadRangeInputs(FileTabState)`.
- Produces:
  - `public readonly record struct TimeWindow(DateTimeOffset From, DateTimeOffset To)` (in `FileTabState.cs`)
  - `public sealed record HistogramPrior(string From, string To, bool Live)` (in `FileTabState.cs`)
  - `FileTabState.TimeWindow : TimeWindow?` (setter internal), `FileTabState.HistogramPrior : HistogramPrior?` (internal get/set), `FileTabState.CreateTimeWindowPredicate() : Func<Line, bool>?`
  - `AppState.SetFileTimeWindow(FileTabState tab, TimeWindow? window)` (throws `ArgumentException` for Elastic or stream tabs and for From after To)
  - `MainWindowViewModel.ApplyHistogramRange(DateTimeOffset from, DateTimeOffset to)` and `MainWindowViewModel.ResetHistogramRange()` (both `internal`, act on `SelectedFile`)
  - `FilterBarViewModel.TimeWindowLabel : string`, `HasTimeWindow : bool`, `ClearTimeWindowCommand : ReactiveCommand<Unit, Unit>`

**Behaviour:**
- `CreateTimeWindowPredicate()`: null when `TimeWindow` is null. Otherwise rows without a timestamp pass and timestamped rows pass when `From <= t <= To`.
- `LogViewViewModel.ApplyFilters`: for a tab without `Stream`, AND the time-window predicate with the existing filters (alongside `inRange`). Include it in the early-return check.
- `ApplyHistogramRange(from, to)` on the selected tab:
  1. If `HistogramPrior` is null, capture it: a stream → `new(stream.RangeFrom, stream.RangeTo, stream.Live)`; Elastic → `new(tab.ElasticFrom, tab.ElasticTo, false)`; a file tab → `new(windowFromIso or "", windowToIso or "", window is null)`, where `Live = true` means "no window".
  2. Route: stream → `SetStreamTimeRange(tab, iso(from), iso(to), live: false, DisplayZone)`; Elastic → `SetElasticTimeRange(tab, iso(from), iso(to))` plus `tab.ElasticInputZone = DisplayZone`; file → `SetFileTimeWindow(tab, new TimeWindow(from, to))`.
  3. `LoadRangeInputs(tab)` for streams and Elastic, so the pill and editor show the new range. On `ArgumentException`, set `RangeError` and leave the prior as it was before step 1.
- `ResetHistogramRange()`: with a prior, restore it (stream → `SetStreamTimeRange(tab, prior.From, prior.To, prior.Live)`; Elastic → `SetElasticTimeRange(tab, prior.From, prior.To)`; file → `SetFileTimeWindow(tab, prior.Live ? null : new TimeWindow(Parse(prior.From), Parse(prior.To)))`), then clear the prior and reload the range inputs. Without a prior: a file tab clears its window, a stream calls `SetStreamTimeRange(tab, stream.RangeFrom, stream.RangeTo, live: true)`, and an Elastic tab does nothing.
- `ApplyElasticTimeRange()` (the pill's Apply, for both streams and Elastic) and `ClearStreamRange()` set `SelectedFile.Model.HistogramPrior = null` after they succeed.
- The filter bar chip is shown for a file tab with a window: `TimeWindowLabel = $"Time {TimeRangeLabel.Describe(iso(from), iso(to), zone)}"`. `ClearTimeWindowCommand` calls `SetFileTimeWindow(tab, null)` and clears `HistogramPrior`. `FilterBarViewModel.Sync` raises both properties. In `FilterBar.axaml`, put the chip before `SourceChipList`: a `Border` with the existing chip look containing the label and a `Button` (`mdi-close` icon, `AutomationProperties.Name="Clear time window"`).

- [ ] **Step 1: Write the failing tests** — `src/HexTail.Tests/Application/FileTimeWindowTests.cs`. Use the `TestWindow`/`MainWindowViewModel` setup from `src/HexTail.Tests/Ui/StreamRangeTests.cs` (its `Harness` builds a view model with `startPolling: false`, opens files and drains), and copy only what you need. The tests:

```csharp
[AvaloniaFact]
public async Task FileWindow_FiltersTimestampedRows_AndKeepsUntimedOnes()
// A file with lines at 10:00:00, 10:00:05, 10:00:10 and one untimestamped line (a continuation)
// after 10:00:05. ApplyHistogramRange(10:00:04, 10:00:06) leaves the 10:00:05 line plus its
// continuation (inherited timestamp, so it is filtered with its parent) in the All view.
// The filter bar shows "Time …"; ClearTimeWindowCommand brings all four rows back.

[AvaloniaFact]
public async Task FileWindow_Reset_RestoresThePreviousWindow_ThenClears()
// ApplyHistogramRange(a) then ApplyHistogramRange(b): ResetHistogramRange returns to no window
// (the prior captured before the first drag), and a second reset does nothing.

[AvaloniaFact]
public async Task Stream_HistogramRange_SetsTheStreamRange_AndResetRestoresAllRows()
// A stream over two files: ApplyHistogramRange sets stream.Live == false with ISO bounds and
// filters rows; ResetHistogramRange restores Live == true.

[AvaloniaFact]
public async Task PillApply_ClearsThePrior()
// Stream: ApplyHistogramRange, then set the pill inputs and run ApplyElasticTimeRangeCommand
// (or the command that ApplyElasticTimeRange backs). HistogramPrior is null, and
// ResetHistogramRange now shows all rows.

[Fact]
public void SetFileTimeWindow_RejectsReversedBounds_AndElasticOrStreamTabs()
// Plain AppState with an opened file tab: From after To throws ArgumentException and changes
// nothing. Pass a stream tab or a FileTabState built with LogSourceDescriptor.Elastic(...) and a
// NullTailer: both throw ArgumentException.
```

Write each test body in full, following the comments. Use `UiWait.Until` for loads and `Dispatcher.UIThread.RunJobs()` after commands. Timestamps in hand-made lines carry an explicit `Z` so the display zone cannot shift them.

The Elastic routing (`SetElasticTimeRange` with ISO bounds and capture of the prior Elastic range) is covered in Task 3's `LogOverviewTests` with a fake `IElasticApiClient` (see `src/HexTail.Tests/Ui/StreamRangeTests.cs` for the existing fake).

- [ ] **Step 2: Run them and watch them fail** — `dotnet build src/HexTail.Tests 2>&1 | grep " error "` lists the missing members.

- [ ] **Step 3: Implement** — `FileTabState.cs` additions:

```csharp
/// <summary>A file tab's histogram time window: rows outside it are hidden (session only, never saved).</summary>
public readonly record struct TimeWindow(DateTimeOffset From, DateTimeOffset To);

/// <summary>The range in place before the first histogram drag, restored by a double-click.</summary>
public sealed record HistogramPrior(string From, string To, bool Live);
```

```csharp
    public TimeWindow? TimeWindow { get; internal set; }
    internal HistogramPrior? HistogramPrior { get; set; }

    /// <summary>The time-window test for one filter pass, or null when no window is set.</summary>
    internal Func<Line, bool>? CreateTimeWindowPredicate()
    {
        if (TimeWindow is not { } window)
            return null;
        var (from, to) = (window.From, window.To);
        return line => line.Timestamp is not { } time || (time >= from && time <= to);
    }
```

`AppState.cs`:

```csharp
    /// <summary>
    /// Sets or clears a file tab's time window (from the timeline histogram). Elastic tabs and streams
    /// have real ranges instead; they throw, as does a window whose From is after its To.
    /// </summary>
    internal void SetFileTimeWindow(FileTabState tab, TimeWindow? window)
    {
        ArgumentNullException.ThrowIfNull(tab);
        if (tab.Stream is not null || tab.Source.Kind == LogSourceKind.Elastic)
            throw new ArgumentException("Only file tabs take a time window.", nameof(tab));
        if (window is { } value && value.From > value.To)
            throw new ArgumentException("From must be at or before To.", nameof(window));
        tab.TimeWindow = window;
        NotifyChanged();
    }
```

In `LogViewViewModel.ApplyFilters`, next to the existing `inRange`:

```csharp
        var inWindow = _file.Model.Stream is null ? _file.Model.CreateTimeWindowPredicate() : null;
        if (!rules.HasVisibilityRules && !filterLevels && !filterSources && inRange is null && inWindow is null)
            return lines as IReadOnlyList<Line> ?? lines.ToList();
        var result = lines;
        if (inRange is not null)
            result = result.Where(inRange);
        if (inWindow is not null)
            result = result.Where(inWindow);
```

Write `ApplyHistogramRange`/`ResetHistogramRange` in `MainWindowViewModel` per the Behaviour list, next to `ApplyElasticTimeRange`. Use `static string Iso(DateTimeOffset t) => t.ToString("O", CultureInfo.InvariantCulture);` and parse the prior back with `DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)`. Wrap the state calls in `try { … } catch (ArgumentException ex) { RangeError = ex.Message; }`, as `ApplyElasticTimeRange` does.

- [ ] **Step 4: Run them and watch them pass** — `dotnet test src/HexTail.Tests/HexTail.Tests.csproj --filter "FullyQualifiedName~FileTimeWindowTests|FullyQualifiedName~StreamRangeTests"`. Expected: all pass.

- [ ] **Step 5: Commit** — `feat(timeline): give file tabs a time window and route histogram ranges by source`.

---

### Task 3: `LogOverviewViewModel` — builds, cache, descriptions, jumps

**Files:**
- Create: `src/HexTail/ViewModels/LogOverviewViewModel.cs`
- Modify: `src/HexTail/ViewModels/LogViewViewModel.cs` (own `Overview`; call `Overview.Invalidate()` at the end of every `Sync`; add `internal void SelectRowAt(int index)` that bounds-checks and calls `SelectRow(Lines[index])`)
- Test: `src/HexTail.Tests/ViewModels/LogOverviewTests.cs`

**Interfaces:**
- Consumes: Task 1 builders; Task 2 `ApplyHistogramRange`/`ResetHistogramRange`; `FileTabState.Searches`, `FileTabState.EffectiveRules(AppSettings)`, `LogViewViewModel.Search` (the view's own search, null for All).
- Produces:

```csharp
internal sealed class LogOverviewViewModel : ReactiveObject
{
    public int Version { get; }                 // raised on Invalidate(); controls watch it
    public bool HasRows { get; }                // view has at least one row (ruler visibility)
    public bool HasTimeline { get; }            // last BuildHistogram found timestamps
    internal void Invalidate();                 // bump Version, raise HasRows
    internal TimeHistogram? BuildHistogram(int maxBuckets);   // sets HasTimeline
    internal IReadOnlyList<ScrollMarker> BuildMarkers();
    internal string DescribeBucket(TimeHistogram histogram, int index);
    internal void JumpToBucket(TimeHistogram histogram, int index);  // select FirstRow if >= 0
    internal void JumpToFraction(double fraction);                   // ruler click: row = floor(f * count), clamped
    internal void ApplyRange(DateTimeOffset from, DateTimeOffset to); // owner.ApplyHistogramRange
    internal void ResetRange();                                       // owner.ResetHistogramRange
}
```

**Details:**
- `BuildHistogram` uses the view's `Lines` (`row.Line`) and a zone offset from `Settings.TimeZoneMode`: UTC → `TimeSpan.Zero`, Local → `TimeZoneInfo.Local.GetUtcOffset(t)`. `HasTimeline` becomes `histogram is not null` (raise on change).
- The `BuildMarkers` colour function, in this order:
  1. Rows matched by the tab's `Searches`. Skip the view's own `Search`, because in a search view every row would match. Build a `Dictionary<Line, string>(ReferenceEqualityComparer.Instance)` once per build from each search's `Results` (buffer indices → `File.Buffer[i]`, bounds-checked), with the first search winning.
  2. Otherwise, `EffectiveRules(Settings).FirstHighlightColor(line)`, cached in a `Dictionary<Line, string?>` keyed by reference. Drop the cache when the `RuleSet` instance changes. Each build creates a new dictionary holding only the current rows' entries, so it never grows past the view.
- `DescribeBucket` format (exact): `"{start} – {end} · {total:N0} rows"`, then, when total > 0, `" ({error:N0} error, {warn:N0} warn, {info:N0} info, {debug:N0} debug, {other:N0} other)"`, leaving out the zero parts and the parentheses when all are zero. Times use `HH:mm:ss` in the display zone when the histogram spans under 24 h, otherwise `MM-dd HH:mm`. Example: `"10:00:00 – 10:01:00 · 5 rows (1 error, 1 warn, 1 info, 2 debug)"`.

- [ ] **Step 1: Write the failing tests** (`[AvaloniaFact]`; build the view model like `StreamRenderingTests`' `Harness`):

```csharp
// BuildHistogram_CountsTheDisplayedRows_AndHidesWhenNoneHaveTimestamps
// BuildMarkers_UsesSearchColourFirst_ThenHighlightRules_AndMarksErrors
// BuildMarkers_InASearchView_IgnoresThatViewsOwnSearch
// BuildMarkers_RecomputesAfterARuleChange   (cache dropped when the RuleSet instance changes)
// DescribeBucket_UsesTheExactFormat         (assert the example string above with Settings in UTC)
// JumpToBucket_SelectsTheFirstRowInTheBucket_AndRaisesSelectionMoved
// JumpToBucket_OnAnEmptyBucket_DoesNothing
// JumpToFraction_SelectsTheRowAtThatHeight  (0.0 → first, 0.999 → last, 0.5 of 4 rows → row 2)
// ApplyRange_OnAnElasticTab_RequeriesWithIsoBounds_AndCapturesThePriorRange
//     (fake IElasticApiClient as in StreamRangeTests; tab.ElasticFrom == ISO from; HistogramPrior has the old range)
// Invalidate_BumpsVersion
```

Write each test in full. For the Elastic test, reuse the fake client and source setup in `src/HexTail.Tests/Ui/StreamRangeTests.cs` (search it for `IElasticApiClient`).

- [ ] **Step 2: Run them and watch them fail**, then **Step 3: implement** `LogOverviewViewModel` as specified (constructor `(MainWindowViewModel owner, LogViewViewModel view)`; created in `LogViewViewModel`'s constructor as `Overview = new LogOverviewViewModel(owner, this);`).

- [ ] **Step 4: Run them and watch them pass** — `dotnet test … --filter "FullyQualifiedName~LogOverviewTests"`, then the full suite once: `dotnet test src/HexTail.Tests/HexTail.Tests.csproj`.

- [ ] **Step 5: Commit** — `feat(timeline): build the histogram and overview markers for each view`.

---

### Task 4: `TimelineHistogram` control, collapse state, `WarnBrush`

**Files:**
- Create: `src/HexTail/Views/TimelineHistogram.cs`
- Modify: `src/HexTail/Views/LogView.axaml`, `src/HexTail/ThemeManager.cs` (add `["WarnBrush"] = "#F5B942"` to all three palettes), `src/HexTail/Styles/CyberTail.axaml` (`.cell.wrn` → `{DynamicResource WarnBrush}`), `src/HexTail/Persistence/AppConfig.cs` (`AppWindowState.HistogramCollapsed`), `src/HexTail/MainWindow.axaml.cs` (save it in `OnClosed`), `src/HexTail/ViewModels/MainWindowViewModel.cs` (`TimelineOpen` property restored from `_state.Window.HistogramCollapsed` next to `Inspector.IsOpen`, `ToggleTimelineCommand`, palette entry `"Toggle timeline"`, `"View"`, no gesture)
- Test: `src/HexTail.Tests/Ui/TimelineHistogramTests.cs`

**Layout (`LogView.axaml`):** `RowDefinitions="Auto,Auto,*"`. Row 0 holds a `Panel` that is visible when `Overview.HasTimeline`, containing:
- the expanded view (`IsVisible="{Binding Workspace.TimelineOpen}"`, `Height="40"`): a `views:TimelineHistogram` (`x:Name="Histogram"`, `View="{Binding}"`, `AutomationProperties.Name="Timeline histogram"`) and, top-right over its label strip, a 16×12 transparent `Button` with `mdi-chevron-up` (`AutomationProperties.Name="Collapse timeline"`, `Command="{Binding Workspace.ToggleTimelineCommand}"`);
- the collapsed strip (`IsVisible="{Binding !Workspace.TimelineOpen}"`, `Height="18"`): a left-aligned transparent `Button` with `mdi-chevron-down` and the text "Timeline" (`AutomationProperties.Name="Expand timeline"`, same command).

The column header moves to row 1 and the list grid to row 2. The jump-to-latest button moves to `Grid.Row="2"`.

**Control (`TimelineHistogram : Control`):**
- `public static readonly StyledProperty<LogViewViewModel?> ViewProperty`. When it changes, subscribe to `View.Overview.PropertyChanged` (`Version`) and `View.Settings` theme changes through `ActualThemeVariantChanged`/resource changes, and schedule a rebuild.
- Rebuild coalescing: a `DispatcherTimer` with `Interval = 250 ms`, started on a change and stopped after one tick. Only rebuild when `IsEffectivelyVisible && Bounds.Width > 0`, otherwise leave the build dirty and rebuild once it becomes visible (watch `IsEffectivelyVisibleProperty`/`BoundsProperty`). A rebuild calls `View.Overview.BuildHistogram(Math.Max(1, (int)(Bounds.Width / 4)))`, stores the result and calls `InvalidateVisual()`. `OnDetachedFromVisualTree` stops the timer and unsubscribes.
- `Render(DrawingContext context)`:
  - Background `SurfaceAltBrush`.
  - For bucket `i` of `N`: `x = i * W / N`, `w = max(1, W / N - (W / N >= 3 ? 1 : 0))`. Stack the segments bottom-up in the constraint order inside the bars area (`y` from 12 to 40), each with height `count / MaxTotal * 28`.
  - Resolve brushes with `this.TryFindResource(key, ActualThemeVariant, out var value)`, falling back to `Brushes.Gray`.
  - Labels: `FormattedText` at 10 px in `FaintTextBrush`, the start label at (4, 0) and the end label right-aligned at `W - 4 - 16` (clear of the collapse button). Use the same formats as `DescribeBucket` (HH:mm:ss under 24 h, else MM-dd HH:mm).
  - While dragging: an `AccentSoftBrush` rectangle across the bars area from the drag start bucket to the current bucket.
- Pointer handling:
  - `OnPointerPressed` (left button): if `e.ClickCount == 2`, call `View.Overview.ResetRange()`, mark handled and return. Otherwise record the start x, capture the pointer and set `_dragging = true`.
  - `OnPointerMoved`: while dragging, update the current x and `InvalidateVisual()`. When not dragging, set `ToolTip.SetTip(this, View.Overview.DescribeBucket(histogram, index))` for the hovered bucket.
  - `OnPointerReleased`: if `|dx| >= 4`, call `ApplyRange(Buckets[min].Start, Buckets[max].End)`; otherwise `JumpToBucket(histogram, index)`. Release the capture and clear the drag.
  - Expose `internal TimelineHistogramTestHooks` methods `PressAt(double x, int clickCount)`, `MoveTo(double x)`, `ReleaseAt(double x)` (internal methods on the control that the pointer overrides delegate to), so headless tests can drive gestures without synthesising pointer events.

- [ ] **Step 1: Write the failing tests** (`[AvaloniaFact]`, using `TestWindow.Create`, opening a temp file with timestamped lines and showing the window at 1000×700):

```csharp
// Histogram_IsShownAboveTheList_WithTheExpectedHeight     (Bounds.Height == 40 after layout; visible)
// Histogram_HiddenWhenNoRowHasATimestamp                  (plain-text file → panel not visible)
// Collapse_ShowsTheStrip_AndPersistsInTheWindowState       (click "Collapse timeline" → strip visible 18 px;
//                                                           TimelineOpen false; close window → saved HistogramCollapsed true;
//                                                           a new window from the same persistence opens collapsed)
// PaletteToggleTimeline_FlipsTheState
// Drag_AppliesTheBucketRangeToTheFileTab                   (PressAt/MoveTo/ReleaseAt across ≥ 4 px → tab.TimeWindow
//                                                           equals Buckets[min].Start..Buckets[max].End)
// Click_JumpsToTheFirstRowOfTheBucket                      (release within 4 px → the selected line is that bucket's FirstRow)
// DoubleClick_ResetsTheWindow                              (after a drag, PressAt(x, 2) → TimeWindow null)
// Hover_ShowsTheBucketDescription                          (MoveTo without a press → ToolTip.GetTip == DescribeBucket(...))
// WarnBrush_IsDefinedInEveryTheme                          (for each ThemeCatalog name: apply the theme, resource resolves)
```

Write each in full. Wait for rebuilds with `UiWait.Until(() => histogram.CurrentHistogram is not null)`; expose `internal TimeHistogram? CurrentHistogram` on the control.

- [ ] **Step 2: Run them and watch them fail**, **Step 3: implement**, **Step 4: run them and watch them pass**, then run the full suite once (`dotnet test src/HexTail.Tests/HexTail.Tests.csproj`). Expected: all pass. Run it twice, because UI tests are order-sensitive.

- [ ] **Step 5: Commit** — `feat(timeline): draw a collapsible timeline histogram above each log view`.

---

### Task 5: `OverviewRuler` control

**Files:**
- Create: `src/HexTail/Views/OverviewRuler.cs`
- Modify: `src/HexTail/Views/LogView.axaml` (row 2 becomes a `Grid ColumnDefinitions="*,10"` with `LogList` in column 0 and `views:OverviewRuler` in column 1: `x:Name="Ruler"`, `View="{Binding}"`, `IsVisible="{Binding Overview.HasRows}"`, `AutomationProperties.Name="Overview ruler"`; the jump button stays spanning column 0)
- Test: `src/HexTail.Tests/Ui/OverviewRulerTests.cs`

**Control (`OverviewRuler : Control`):**
- Same `View` property, subscription and 250 ms coalesced rebuild pattern as `TimelineHistogram`. Factor the shared timer/visibility logic into a small `internal sealed class CoalescedRebuild` in `src/HexTail/Views/CoalescedRebuild.cs`, used by both controls. Task 4 may already have added it; if not, extract it here and switch `TimelineHistogram` to it. A rebuild calls `View.Overview.BuildMarkers()` and stores `RowCount = View.Lines.Count`.
- `Render`:
  - Background `SurfaceAltBrush`.
  - Map each marker to `y = floor(row / RowCount * H)` and draw a 2 px-high rectangle: rule colour in `x 0..5` (parse with `Color.TryParse`, cache `ImmutableSolidColorBrush` per colour string, with `AccentBrush` as the fallback), error in `x 5..10` with `ErrorBrush`.
  - Skip a lane's rectangle when that lane already drew at the same `y`, so 100k markers cost at most `H` rectangles per lane.
- `OnPointerPressed` (left): `View.Overview.JumpToFraction(e.GetPosition(this).Y / Bounds.Height)`, then mark handled. Add an internal `ClickAt(double y)` hook for tests.
- `internal IReadOnlyList<ScrollMarker> CurrentMarkers` for tests.

- [ ] **Step 1: Write the failing tests** (`[AvaloniaFact]`):

```csharp
// Ruler_IsTenPixelsWide_BesideTheList                (Bounds.Width == 10; list's right edge == ruler's left edge)
// Ruler_MarksHighlightRuleMatchesAndErrors           (file with ERROR lines and a Highlight rule → CurrentMarkers rows/colours)
// Ruler_UpdatesWhenARuleIsAdded                      (add a Highlight rule via the filter bar → markers include the new matches)
// Ruler_ClickSelectsTheRowAtThatHeight_AndScrollsToIt (ClickAt(H * 0.75) on 400 rows → selected row 300 and realized)
// Ruler_HiddenWhenTheViewHasNoRows                   (a show-only rule matching nothing → ruler not visible)
```

- [ ] **Step 2: Run them and watch them fail**, **Step 3: implement**, **Step 4: run them and watch them pass**, then run the full suite twice.

- [ ] **Step 5: Commit** — `feat(timeline): mark rule matches and errors in an overview ruler`.

---

### Task 6: Docs

**Files:** `README.md` (feature list), `docs/user-guide.md` (a new "Timeline and overview ruler" section: what the bars show, drag, click, double-click, collapse, the file-tab time window chip and that it isn't saved, how each source kind applies a dragged range, the ruler's lanes and click), `docs/architecture.md` (the pure builders, `LogOverviewViewModel`, the coalesced redraw and the range routing).

- [ ] **Step 1:** Write the sections. Use the exact UI names from the Global Constraints.
- [ ] **Step 2:** Commit — `docs: describe the timeline histogram and overview ruler`.

---

## Self-review notes

- Spec coverage: 40 px histogram, stacked by level (Tasks 1, 4); drag sets range — Elastic re-query, files filter, streams' range (Task 2); click jumps (Tasks 3, 4); double-click resets (Tasks 2, 4); collapsible (Task 4); scrollbar markers for rule matches and errors (Tasks 1, 3, 5).
- Deliberate choices: the histogram counts the rows the view displays (filters applied), so after a drag it shows only the chosen range, and double-click is the way out. The file time window is session-only. The ruler is a separate 10 px column, not drawn over the theme's auto-hiding scrollbar, so it stays visible and clickable.

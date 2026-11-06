# UX Phase 2 part C — Row Inspector, Bookmarks, Small Settings Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace inline row expansion and the bottom context pane with a right-hand row inspector (`Ctrl+I`), add in-session row bookmarks, and shrink Settings to a compact modal with left-hand navigation and a new Limits section.

**Architecture:** An `InspectorViewModel` (owned by `MainWindowViewModel.Inspector`, synced from `SyncFromState` like `FilterBar`) projects the selected row of the selected source: raw text, a field table, per-field rule actions, a ±N context list, and row actions (copy, bookmark). It lives in a workspace-level right column of `LogWorkspace`. Selection stays in `FileTabState.SelectedLine`; bookmarks are a shifting `SortedSet<int>` beside it. Settings gains a `Limits` section (max lines, context before/after, initial Elastic fetch) and a vertical section list.

**Tech Stack:** .NET 10, Avalonia 12.1.1 (headless tests via `[AvaloniaFact]`), ReactiveUI, xUnit v3, System.Text.Json.

This is part C of phase 2 of `docs/ux/2026-11-04-ux-proposals.md` (items 4 inspector part, 10). Parts A (`2026-11-06-ux-phase-2a-rule-engine.md`) and B (`2026-11-07-ux-phase-2b-filter-bar.md`) are merged. Terminology: "Phase 2 part C".

## Global Constraints

- Branch `audit-ui-phase-2c`, cut from `origin/main` at `9ddbcad` (PR #12 merged). Never work on `main`. **Never `git push` unless the user explicitly says so.**
- Conventional Commits, one commit per task. End each commit message with `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`.
- A CSharpier pre-commit hook reformats staged C# on commit. `csharpier check` already fails on `src/HexTail/HexTail.csproj`; leave that file alone.
- Prefix shell commands with `rtk`. Explore with `sb` (`sb show <file> <Symbol>`, `sb callers <Symbol>`) before opening whole files.
- Build/test commands (run from the repo root):
  - `rtk dotnet build src/HexTail.Tests`
  - `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~<Name>"`
  - full suite: `rtk dotnet test src/HexTail.Tests --no-build`. Baseline at the start of this part: **530 passing, 53 build warnings** (do not add warnings).
- Tests must never touch the OS credential vault: every `new AppState(...)` in a test passes `credentials: new InMemoryCredentialVault()` (`TestWindow.Create*` already does).
- UI tests that append to a temp file use `TestWindow.CreatePolling(out var viewModel)`; waits go through `UiWait.Until(...)`; never assert immediately after an append. Tests that append with `file.Model.Buffer.Append([...])` follow with `file.SyncViews()` (see `LogLineInteractionTests`).
- ReactiveUI commands keep the positional scheduler pattern (`ReactiveCommand.Create*(func, scheduler)`).
- Reflection bindings are used in XAML (compiled bindings are off). Every interactive control gets an `AutomationProperties.Name`. Any control that changes focus must keep the Settings focus rules in `MainWindow.axaml.cs` (`ApplySettingsFocus`, `WatchSettingsCloseButton`, `OnFocusChangedWhileSettingsOpen`).
- Old sessions must still load: the v2 JSON shape only gains optional fields; removed fields (`showContext`, `contextAbove`, `contextBelow`, `contextPaneSize`) are ignored on load (`System.Text.Json` skips unknown members; Task 3 proves it with a test). `AppConfig.Version` stays 2.
- Known flaky test: `AccessibilityTests.SettingsFocus…` (fixed in #11; if it flakes again, report it, do not paper over it).

### Decisions taken where the proposal left room (veto points)

1. **The inspector is one workspace-level panel**, not per view: selection lives on the source (`FileTabState.SelectedLine`), so the panel follows the selected source's selected row. It is a right column of `LogWorkspace` with a drag handle (width persisted, clamped 260..640, default 360) and is open/closed with `Ctrl+I`; the open flag and width are persisted in `AppWindowState`.
2. **Field actions create Source rules**, Literal, case-sensitive, with an automatic color, through the existing `AppState.AddRuleAsync`: *Show only this value*, *Hide value*, *Highlight*. The pattern is the field's value as-is. *Copy* copies the value. *Add column* is **not** built (columns are phase 4).
3. **Inline context becomes a row action.** The bottom context pane, the `Context` toolbar toggle, `ShowContext` and the per-tab `ContextAbove/Below` are removed. The inspector's **Show context** toggle lists the true neighbours of the selected row (the raw buffer, ignoring filters) using `AppSettings.ContextAbove/ContextBelow` (defaults 3 before, 10 after; edited in Settings → Limits). Clicking a context row selects it. The toggle is in-session UI state, not persisted.
4. **Inline expansion is removed.** Enter, double-click and the row menu's **Inspect** open the inspector on that row. *Copy raw line* / *Copy fields* stay on the row menu.
5. **Bookmarks are in-session only** (a `SortedSet<int>` of buffer indices that shifts on rollover and clears with the buffer, like `SelectedLine`). A bookmarked row shows a bookmark glyph. Keys (no text input focused): `m` toggles, `]` / `[` jump to the next / previous bookmarked visible row. They are never written to the session file.
6. **Copy JSON** is the flat map of parsed fields (dotted keys, string values) as indented JSON; it is disabled for plain-text rows. *Show ±N context* uses the two Settings numbers, not one symmetric N.
7. **Settings sections are Appearance (0), Elastic (1), Limits (2)** so existing `SectionIndex = 1` for Elastic keeps working. There is no "Advanced" section: nothing needs one (`SettingsMenuAlignment` is already forced to Right and has no UI). Elastic stays until phase 3 replaces it. Navigation moves to the left (`TabStripPlacement="Left"`), and the modal shrinks from 1600×920 to 960×640 (still clamped to the window).
8. **Limits** are: *Maximum lines per source* (default 100,000, 1,000..1,000,000, applies to sources opened afterwards), *Context lines before / after* (0..50), *Initial Elastic fetch* (new `AppSettings.ElasticInitialLines`, default 10,000, 100..100,000, applies to Elastic views opened afterwards).

### Not in part C

Relative time / delta display, multi-select and export of a filtered view, an *Add column* field action (phase 4 with columns), wrap-default and timestamp-format settings (no such features exist yet), persisting bookmarks, a bookmarks list, and everything listed as deferred in `.superpowers/sdd/progress.md` for part B.

## File Structure

| Path | Status | Responsibility |
| --- | --- | --- |
| `src/HexTail/ViewModels/InspectorViewModel.cs` | create | open/width state, selected-row projection, fields, JSON, context list, bookmark flag, actions |
| `src/HexTail/ViewModels/InspectorFieldViewModel.cs` | create | one field row + its three rule commands |
| `src/HexTail/ViewModels/InspectorContextRowViewModel.cs` | create | one context row |
| `src/HexTail/Views/InspectorPanel.axaml(.cs)` | create | the right-hand panel; clipboard copies live in the code-behind |
| `src/HexTail/Views/LogWorkspace.axaml(.cs)` | modify | right column + resize thumb; drop the toolbar `Context` toggle |
| `src/HexTail/Views/LogView.axaml(.cs)` | modify | drop the context rows/splitter/list |
| `src/HexTail/Views/LogLineView.axaml(.cs)` | modify | drop expansion; Inspect menu item; bookmark glyph |
| `src/HexTail/ViewModels/LogLineViewModel.cs`, `LogViewViewModel.cs`, `FileTabViewModel.cs`, `MainWindowViewModel.cs` | modify | remove expansion/context; add inspector, bookmark plumbing |
| `src/HexTail/Application/AppState.cs`, `FileTabState.cs` | modify | remove `ExpandedLine`/`ShowContext`/context sizes; add bookmarks; Elastic initial lines |
| `src/HexTail/Persistence/AppConfig.cs` | modify | window inspector fields; drop removed fields; `AppSettings.ElasticInitialLines` |
| `src/HexTail/Input/ShellShortcuts.cs`, `src/HexTail/MainWindow.axaml.cs` | modify | `Ctrl+I`, `m`, `[`, `]`; window-state save |
| `src/HexTail/Tailing/LogSourceService.cs` | modify | per-call initial-lines override for Elastic |
| `src/HexTail/ViewModels/SettingsViewModel.cs`, `src/HexTail/Views/SettingsPanel.axaml` | modify | Limits section, left navigation, compact modal |
| `README.md`, `docs/user-guide.md`, `docs/architecture.md` | modify | document the above |

---

### Task 1: Inspector panel, `Ctrl+I`, persisted open state

**Files:**
- Create: `src/HexTail/ViewModels/InspectorViewModel.cs`, `src/HexTail/ViewModels/InspectorFieldViewModel.cs`, `src/HexTail/Views/InspectorPanel.axaml`, `src/HexTail/Views/InspectorPanel.axaml.cs`
- Modify: `src/HexTail/ViewModels/MainWindowViewModel.cs`, `src/HexTail/Input/ShellShortcuts.cs`, `src/HexTail/MainWindow.axaml.cs`, `src/HexTail/Persistence/AppConfig.cs`, `src/HexTail/Views/LogWorkspace.axaml`, `src/HexTail/Views/LogWorkspace.axaml.cs`, `src/HexTail/Themes/CyberTail.axaml` (locate with `rg -l "sidebar-heading" src/HexTail`)
- Test: `src/HexTail.Tests/ViewModels/InspectorViewModelTests.cs` (create), `src/HexTail.Tests/Input/ShellShortcutsTests.cs`, `src/HexTail.Tests/Ui/InspectorPanelTests.cs` (create)

**Interfaces:**
- Consumes: `MainWindowViewModel.SelectedFile` (`FileTabViewModel?`, `.Model` is `FileTabState` with `SelectedLine`, `Buffer`), `MainWindowViewModel.Scheduler`, `AppState.Window` (`AppWindowState`), `ShellShortcuts.Resolve`.
- Produces (later tasks rely on these exact names):

```csharp
internal sealed class InspectorViewModel : ReactiveObject
{
    internal const double MinWidth = 260, MaxWidth = 640, DefaultWidth = 360;
    internal InspectorViewModel(MainWindowViewModel owner);
    public bool IsOpen { get; set; }                       // persisted
    public bool IsShown { get; }                           // IsOpen && a source is selected
    public double Width { get; set; }                      // clamped, persisted
    public bool HasLine { get; }
    public string Title { get; }                           // "Line 42" | "No row selected"
    public string RawText { get; }
    public ObservableCollection<InspectorFieldViewModel> Fields { get; }
    public bool HasFields { get; }
    public string? JsonText { get; }                       // null when the row has no parsed fields
    public bool CanCopyJson { get; }
    public string? Error { get; internal set; }
    public bool HasError { get; }
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    internal MainWindowViewModel Owner { get; }
    internal void Open();
    internal void Toggle();
    internal void Sync();                                   // called from MainWindowViewModel.SyncFromState
}
internal sealed class InspectorFieldViewModel : ReactiveObject
{
    internal InspectorFieldViewModel(InspectorViewModel owner, string key, string value);
    public string Key { get; }
    public string Value { get; }
}
// AppWindowState: public bool InspectorOpen { get; init; }  public double InspectorWidth { get; init; } = 360;
// ShellAction.ToggleInspector (Ctrl+I)
```

- [ ] **Step 1: Write the failing view-model tests**

Create `src/HexTail.Tests/ViewModels/InspectorViewModelTests.cs`:

```csharp
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.ViewModels;

public sealed class InspectorViewModelTests
{
    private static async Task<(MainWindowViewModel Owner, FileTabViewModel File, string Path, Avalonia.Controls.Window Window)> OpenAsync(
        params Line[] lines
    )
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var owner);
        await owner.OpenPathsCommand.Execute([path]);
        var file = owner.SelectedFile!;
        file.Model.Buffer.Append(lines);
        file.SyncViews();
        return (owner, file, path, window);
    }

    [AvaloniaFact]
    public async Task WithNoSelectionTheInspectorHasNoLine()
    {
        var (owner, _, path, window) = await OpenAsync(new Line("a"));
        try
        {
            owner.Inspector.Sync();

            Assert.False(owner.Inspector.HasLine);
            Assert.Equal("No row selected", owner.Inspector.Title);
            Assert.Empty(owner.Inspector.Fields);
            Assert.Null(owner.Inspector.JsonText);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task SelectingAStructuredRowListsItsFieldsInOrderAndBuildsJson()
    {
        var fields = new Dictionary<string, string>
        {
            ["level"] = "error",
            ["msg"] = "disk full",
            ["db.host"] = "a1",
        };
        var line = new Line("level=error msg=\"disk full\" db.host=a1", fields);
        var (owner, file, path, window) = await OpenAsync(new Line("first"), line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();

            Assert.True(owner.Inspector.HasLine);
            Assert.Equal("Line 2", owner.Inspector.Title);
            Assert.Equal(line.Raw, owner.Inspector.RawText);
            Assert.Equal(
                ["level", "msg", "db.host"],
                owner.Inspector.Fields.Select(field => field.Key)
            );
            Assert.Equal("disk full", owner.Inspector.Fields[1].Value);
            Assert.True(owner.Inspector.CanCopyJson);
            using var json = System.Text.Json.JsonDocument.Parse(owner.Inspector.JsonText!);
            Assert.Equal("a1", json.RootElement.GetProperty("db.host").GetString());
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task APlainTextRowShowsRawTextAndNoFields()
    {
        var line = new Line("just text");
        var (owner, file, path, window) = await OpenAsync(line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();

            Assert.Equal("just text", owner.Inspector.RawText);
            Assert.False(owner.Inspector.HasFields);
            Assert.False(owner.Inspector.CanCopyJson);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ToggleFlipsOpenAndIsShownNeedsASource()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.Inspector.Toggle();
            Assert.True(owner.Inspector.IsOpen);
            Assert.False(owner.Inspector.IsShown); // no source selected

            var path = Path.GetTempFileName();
            await owner.OpenPathsCommand.Execute([path]);
            owner.Inspector.Sync();
            Assert.True(owner.Inspector.IsShown);

            owner.Inspector.Toggle();
            Assert.False(owner.Inspector.IsOpen);
            File.Delete(path);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public void WidthIsClampedToItsRange()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.Inspector.Width = 10;
            Assert.Equal(InspectorViewModel.MinWidth, owner.Inspector.Width);
            owner.Inspector.Width = 5000;
            Assert.Equal(InspectorViewModel.MaxWidth, owner.Inspector.Width);
        }
        finally
        {
            window.Close();
        }
    }
}
```

Add to `src/HexTail.Tests/Input/ShellShortcutsTests.cs` (match the file's existing style):

```csharp
    [Fact]
    public void CtrlITogglesTheInspectorEvenWhileTyping()
    {
        Assert.Equal(
            ShellAction.ToggleInspector,
            ShellShortcuts.Resolve(Key.I, KeyModifiers.Control, textInputFocused: true).Action
        );
        Assert.Equal(
            ShellAction.None,
            ShellShortcuts.Resolve(Key.I, KeyModifiers.None, textInputFocused: false).Action
        );
    }
```

- [ ] **Step 2: Run to verify they fail**

Run: `rtk dotnet build src/HexTail.Tests` — Expected: compile errors (`Inspector`, `ToggleInspector`, `InspectorViewModel` do not exist).

- [ ] **Step 3: Implement the view models**

`src/HexTail/ViewModels/InspectorFieldViewModel.cs`:

```csharp
using ReactiveUI;

namespace HexTail.ViewModels;

internal sealed class InspectorFieldViewModel : ReactiveObject
{
    internal InspectorFieldViewModel(InspectorViewModel owner, string key, string value)
    {
        Owner = owner;
        Key = key;
        Value = value;
    }

    internal InspectorViewModel Owner { get; }
    public string Key { get; }
    public string Value { get; }
}
```

`src/HexTail/ViewModels/InspectorViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Encodings.Web;
using System.Text.Json;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>Projects the selected row of the selected source into the right-hand inspector panel.</summary>
internal sealed class InspectorViewModel : ReactiveObject
{
    internal const double MinWidth = 260;
    internal const double MaxWidth = 640;
    internal const double DefaultWidth = 360;
    private const string NoRowTitle = "No row selected";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly MainWindowViewModel _owner;
    private bool _isOpen;
    private double _width = DefaultWidth;
    private Line? _line;
    private int _index = -1;
    private string _title = NoRowTitle;
    private string _rawText = string.Empty;
    private string? _jsonText;
    private string? _error;

    internal InspectorViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        CloseCommand = ReactiveCommand.Create(
            () =>
            {
                IsOpen = false;
            },
            owner.Scheduler
        );
    }

    internal MainWindowViewModel Owner => _owner;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ObservableCollection<InspectorFieldViewModel> Fields { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            this.RaiseAndSetIfChanged(ref _isOpen, value);
            this.RaisePropertyChanged(nameof(IsShown));
        }
    }

    public bool IsShown => IsOpen && _owner.SelectedFile is not null;

    public double Width
    {
        get => _width;
        set => this.RaiseAndSetIfChanged(ref _width, Math.Clamp(value, MinWidth, MaxWidth));
    }

    public bool HasLine => _line is not null;
    public string Title => _title;
    public string RawText => _rawText;
    public bool HasFields => Fields.Count > 0;
    public string? JsonText => _jsonText;
    public bool CanCopyJson => _jsonText is not null;

    public string? Error
    {
        get => _error;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    internal void Open() => IsOpen = true;

    internal void Toggle() => IsOpen = !IsOpen;

    internal void Sync()
    {
        this.RaisePropertyChanged(nameof(IsShown));
        var file = _owner.SelectedFile?.Model;
        Line? line = null;
        var index = -1;
        if (file?.SelectedLine is int selected && selected >= 0 && selected < file.Buffer.Count)
        {
            index = selected;
            line = file.Buffer[selected];
        }

        if (ReferenceEquals(line, _line) && index == _index)
            return;
        _line = line;
        _index = index;
        Error = null;
        _title = line is null ? NoRowTitle : $"Line {index + 1:N0}";
        _rawText = line?.Raw ?? string.Empty;

        Fields.Clear();
        _jsonText = null;
        if (line?.ParsedFields is { Count: > 0 } fields)
        {
            foreach (var pair in fields)
                Fields.Add(new InspectorFieldViewModel(this, pair.Key, pair.Value));
            _jsonText = JsonSerializer.Serialize(fields, JsonOptions);
        }

        this.RaisePropertyChanged(nameof(HasLine));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(RawText));
        this.RaisePropertyChanged(nameof(HasFields));
        this.RaisePropertyChanged(nameof(JsonText));
        this.RaisePropertyChanged(nameof(CanCopyJson));
    }
}
```

- [ ] **Step 4: Wire it up**

1. `MainWindowViewModel.cs`: add `public InspectorViewModel Inspector { get; }` next to `FilterBar` (line ~177), create it in the constructor right after `FilterBar = new FilterBarViewModel(this);` (line ~75), and call `Inspector.Sync();` right after `FilterBar.Sync();` in `SyncFromState` (line ~905). Next to the sidebar-width restore (line ~501) add:
   ```csharp
   Inspector.IsOpen = _state.Window.InspectorOpen;
   Inspector.Width =
       _state.Window.InspectorWidth > 0
           ? _state.Window.InspectorWidth
           : InspectorViewModel.DefaultWidth;
   ```
   Add a `ToggleInspectorCommand` (`ReactiveCommand.Create(() => Inspector.Toggle(), Scheduler)`, declared like `ToggleSidebarCommand`) and a palette entry after "Toggle sidebar" (line ~746): `yield return new("Toggle inspector", "View", "Ctrl+I", async () => await ToggleInspectorCommand.Execute());`.
2. `AppConfig.cs` `AppWindowState`: add `public bool InspectorOpen { get; init; }` and `public double InspectorWidth { get; init; } = 360;`. (`ContextPaneSize` stays until Task 3.)
3. `MainWindow.axaml.cs` `OnClosed`: add `InspectorOpen = ViewModel.Inspector.IsOpen, InspectorWidth = ViewModel.Inspector.Width,` to the `AppWindowState` initialiser; add `case ShellAction.ToggleInspector: ViewModel.Inspector.Toggle(); break;` to the dispatch switch (next to `ToggleSidebar`, line ~351).
4. `ShellShortcuts.cs`: add `ToggleInspector` to `ShellAction`; in the `primary` switch add `Key.I => new(ShellAction.ToggleInspector),`.

- [ ] **Step 5: The panel**

`src/HexTail/Views/InspectorPanel.axaml`:

```xml
<UserControl
  xmlns="https://github.com/avaloniaui"
  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
  xmlns:vm="using:HexTail.ViewModels"
  xmlns:ic="https://github.com/projektanker/icons.avalonia"
  x:Class="HexTail.Views.InspectorPanel"
  x:DataType="vm:InspectorViewModel"
  AutomationProperties.Name="Row inspector"
>
  <Border
    Background="{DynamicResource RaisedBrush}"
    BorderBrush="{DynamicResource BorderBrush}"
    BorderThickness="1,0,0,0"
  >
    <Grid RowDefinitions="Auto,Auto,*,Auto">
      <Grid ColumnDefinitions="*,Auto" Margin="12,8,8,4">
        <TextBlock
          x:Name="InspectorTitle"
          Text="{Binding Title}"
          FontWeight="SemiBold"
          VerticalAlignment="Center"
        />
        <Button
          x:Name="InspectorCloseButton"
          Grid.Column="1"
          Classes="icon"
          Command="{Binding CloseCommand}"
          ToolTip.Tip="Close inspector (Ctrl+I)"
          AutomationProperties.Name="Close inspector"
        >
          <ic:Icon Value="mdi-close" />
        </Button>
      </Grid>
      <TextBlock
        x:Name="InspectorError"
        Grid.Row="1"
        Text="{Binding Error}"
        IsVisible="{Binding HasError}"
        Classes="error"
        TextWrapping="Wrap"
        Margin="12,0,12,4"
      />
      <ScrollViewer Grid.Row="2" HorizontalScrollBarVisibility="Disabled">
        <StackPanel Margin="12,0,12,12" Spacing="10">
          <TextBlock
            Text="Select a row to inspect it."
            Classes="hint"
            IsVisible="{Binding !HasLine}"
          />
          <StackPanel Spacing="4" IsVisible="{Binding HasLine}">
            <TextBlock Text="RAW" Classes="sidebar-heading" />
            <SelectableTextBlock
              x:Name="InspectorRaw"
              Text="{Binding RawText}"
              TextWrapping="Wrap"
              FontFamily="Cascadia Mono,Consolas,Menlo,monospace"
              AutomationProperties.Name="Raw log text"
            />
          </StackPanel>
          <StackPanel Spacing="4" IsVisible="{Binding HasFields}">
            <TextBlock Text="FIELDS" Classes="sidebar-heading" />
            <ItemsControl x:Name="InspectorFields" ItemsSource="{Binding Fields}">
              <ItemsControl.ItemTemplate>
                <DataTemplate x:DataType="vm:InspectorFieldViewModel">
                  <Grid ColumnDefinitions="Auto,*" ColumnSpacing="8" Margin="0,2">
                    <TextBlock
                      Text="{Binding Key}"
                      Foreground="{DynamicResource MutedBrush}"
                      FontFamily="Cascadia Mono,Consolas,Menlo,monospace"
                      VerticalAlignment="Top"
                    />
                    <SelectableTextBlock
                      Grid.Column="1"
                      Text="{Binding Value}"
                      TextWrapping="Wrap"
                      FontFamily="Cascadia Mono,Consolas,Menlo,monospace"
                    />
                  </Grid>
                </DataTemplate>
              </ItemsControl.ItemTemplate>
            </ItemsControl>
          </StackPanel>
        </StackPanel>
      </ScrollViewer>
      <WrapPanel Grid.Row="3" Margin="8,4,8,8">
        <Button
          x:Name="InspectorCopyRaw"
          Content="Copy raw"
          Margin="2"
          IsEnabled="{Binding HasLine}"
          Click="OnCopyRaw"
          AutomationProperties.Name="Copy raw line"
        />
        <Button
          x:Name="InspectorCopyJson"
          Content="Copy JSON"
          Margin="2"
          IsEnabled="{Binding CanCopyJson}"
          Click="OnCopyJson"
          AutomationProperties.Name="Copy fields as JSON"
        />
      </WrapPanel>
    </Grid>
  </Border>
</UserControl>
```

`src/HexTail/Views/InspectorPanel.axaml.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Interactivity;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class InspectorPanel : UserControl
{
    public InspectorPanel() => InitializeComponent();

    private async void OnCopyRaw(object? sender, RoutedEventArgs e) =>
        await CopyAsync(viewModel => viewModel.RawText);

    private async void OnCopyJson(object? sender, RoutedEventArgs e) =>
        await CopyAsync(viewModel => viewModel.JsonText);

    private async Task CopyAsync(Func<InspectorViewModel, string?> text)
    {
        if (
            DataContext is not InspectorViewModel viewModel
            || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard
            || text(viewModel) is not { } value
        )
            return;
        await clipboard.SetTextAsync(value);
    }
}
```

`LogWorkspace.axaml`: change the root `<Grid Background=… HorizontalAlignment="Stretch">` into a two-column host. Wrap the existing two children (the empty-state `ScrollViewer` and the `HasFile` `ContentControl`) in `<Grid Grid.Column="0">…</Grid>` (keep their content unchanged) and give the root `ColumnDefinitions="*,Auto"`. Add, as `Grid.Column="1"`:

```xml
<Grid Grid.Column="1" ColumnDefinitions="4,Auto" IsVisible="{Binding Inspector.IsShown}">
  <Thumb
    x:Name="InspectorResizeThumb"
    Cursor="SizeWestEast"
    Background="{DynamicResource BorderBrush}"
    DragDelta="OnInspectorResize"
    AutomationProperties.Name="Resize inspector"
  />
  <views:InspectorPanel
    Grid.Column="1"
    Width="{Binding Inspector.Width}"
    DataContext="{Binding Inspector}"
  />
</Grid>
```

`LogWorkspace.axaml.cs` gets:

```csharp
private void OnInspectorResize(object? sender, Avalonia.Input.VectorEventArgs e)
{
    if (DataContext is ViewModels.MainWindowViewModel owner)
        owner.Inspector.Width -= e.Vector.X;
}
```

(Dragging left grows the panel.) Check how the sidebar `SidebarResizeThumb` is styled in `MainWindow.axaml` (lines 57-67) and reuse its look.

- [ ] **Step 6: UI test**

Create `src/HexTail.Tests/Ui/InspectorPanelTests.cs`:

```csharp
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Domain;
using HexTail.Tests.Support;
using HexTail.Views;

namespace HexTail.Tests.Ui;

public sealed class InspectorPanelTests
{
    [AvaloniaFact]
    public async Task CtrlITogglesThePanelAndItShowsTheSelectedRow()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var owner);
        try
        {
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var line = new Line("k=v", new Dictionary<string, string> { ["k"] = "v" });
            file.Model.Buffer.Append([line]);
            file.SyncViews();
            owner.State.SelectLine(file.Model, line);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(window.GetVisualDescendants().OfType<InspectorPanel>().Where(p => p.IsEffectivelyVisible));

            window.KeyPress(Key.I, RawInputModifiers.Control, PhysicalKey.I, "i");
            await UiWait.Until(() => owner.Inspector.IsOpen);
            Dispatcher.UIThread.RunJobs();

            var panel = window.GetVisualDescendants().OfType<InspectorPanel>().Single();
            Assert.True(panel.IsEffectivelyVisible);
            Assert.Equal("k=v", panel.FindControl<SelectableTextBlock>("InspectorRaw")!.Text);

            window.KeyPress(Key.I, RawInputModifiers.Control, PhysicalKey.I, "i");
            await UiWait.Until(() => !owner.Inspector.IsOpen);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }
}
```

Also add a persistence test next to the existing window-state tests in `src/HexTail.Tests/Persistence/JsonFileAppPersistenceTests.cs`: save an `AppConfig` whose `Window` has `InspectorOpen = true, InspectorWidth = 400`, load it, assert both survive. (Locate the existing window-state assertion with `rg -n "SidebarWidth" src/HexTail.Tests`.)

- [ ] **Step 7: Run tests, commit**

Run: `rtk dotnet build src/HexTail.Tests` then `rtk dotnet test src/HexTail.Tests --no-build --filter "FullyQualifiedName~Inspector|FullyQualifiedName~ShellShortcuts|FullyQualifiedName~JsonFileAppPersistence"` — Expected: PASS. Then the full suite (530 + the new tests, 53 warnings).

```bash
git add -A
git commit -m "feat(inspector): add the row inspector panel toggled with Ctrl+I"
```

---

### Task 2: Field actions (Show only / Hide / Highlight / Copy)

**Files:**
- Modify: `src/HexTail/ViewModels/FilterBarViewModel.cs`, `src/HexTail/ViewModels/InspectorViewModel.cs`, `src/HexTail/ViewModels/InspectorFieldViewModel.cs`, `src/HexTail/Views/InspectorPanel.axaml`, `src/HexTail/Views/InspectorPanel.axaml.cs`
- Test: `src/HexTail.Tests/ViewModels/InspectorViewModelTests.cs`, `src/HexTail.Tests/Ui/InspectorPanelTests.cs`

**Interfaces:**
- Consumes: Task 1 types; `AppState.AddRuleAsync(FileTabState? tab, ViewRule rule, CancellationToken)`; `MainWindowViewModel.NextSearchColor(IEnumerable<string> activeSearchColors, IEnumerable<string> globalColors)`.
- Produces:

```csharp
// FilterBarViewModel
internal ViewRule NewSourceRule(FileTabState file, string pattern, MatchMode mode, bool caseSensitive, RuleAction action);
// InspectorViewModel
internal Task ApplyFieldRuleAsync(InspectorFieldViewModel field, RuleAction action);
// InspectorFieldViewModel
public ReactiveCommand<Unit, Unit> ShowOnlyCommand { get; }
public ReactiveCommand<Unit, Unit> HideCommand { get; }
public ReactiveCommand<Unit, Unit> HighlightCommand { get; }
```

- [ ] **Step 1: Failing tests**

Append to `InspectorViewModelTests`:

```csharp
    [AvaloniaFact]
    public async Task FieldActionsAddLiteralCaseSensitiveSourceRules()
    {
        var line = new Line(
            "level=error msg=boom",
            new Dictionary<string, string> { ["level"] = "error", ["msg"] = "boom" }
        );
        var (owner, file, path, window) = await OpenAsync(line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();
            var level = owner.Inspector.Fields[0];

            await owner.Inspector.ApplyFieldRuleAsync(level, RuleAction.ShowOnly);
            await owner.Inspector.ApplyFieldRuleAsync(owner.Inspector.Fields[1], RuleAction.Hide);
            await owner.Inspector.ApplyFieldRuleAsync(level, RuleAction.Highlight);

            Assert.Collection(
                file.Model.Rules,
                rule =>
                {
                    Assert.Equal("error", rule.Pattern);
                    Assert.Equal(RuleAction.ShowOnly, rule.Action);
                    Assert.Equal(RuleScope.Source, rule.Scope);
                    Assert.Equal(MatchMode.Literal, rule.Mode);
                    Assert.True(rule.CaseSensitive);
                },
                rule =>
                {
                    Assert.Equal("boom", rule.Pattern);
                    Assert.Equal(RuleAction.Hide, rule.Action);
                },
                rule => Assert.Equal(RuleAction.Highlight, rule.Action)
            );
            Assert.Null(owner.Inspector.Error);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task AFieldWithoutAValueCannotBecomeARule()
    {
        var line = new Line("k=", new Dictionary<string, string> { ["k"] = "" });
        var (owner, file, path, window) = await OpenAsync(line);
        try
        {
            owner.State.SelectLine(file.Model, line);
            owner.Inspector.Sync();

            await owner.Inspector.ApplyFieldRuleAsync(owner.Inspector.Fields[0], RuleAction.Hide);

            Assert.Empty(file.Model.Rules);
            Assert.Equal("This field has no value to match.", owner.Inspector.Error);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }
```

- [ ] **Step 2: Run, expect compile failure** (`ApplyFieldRuleAsync` missing).

- [ ] **Step 3: Extract the rule factory**

In `FilterBarViewModel.cs` the block at lines ~258-285 builds the color and the rule inline. Extract it:

```csharp
internal ViewRule NewSourceRule(
    FileTabState file,
    string pattern,
    MatchMode mode,
    bool caseSensitive,
    RuleAction action
)
{
    var color = MainWindowViewModel.NextSearchColor(
        file.Searches.Select(search => search.Color)
            .Concat(
                file.Rules.Where(rule => rule.Action != RuleAction.Hide).Select(rule => rule.Color)
            ),
        _owner
            .State.Settings.Rules.Where(rule => rule.Action != RuleAction.Hide)
            .Select(rule => rule.Color)
    );
    return new ViewRule
    {
        Pattern = pattern,
        Mode = mode,
        CaseSensitive = caseSensitive,
        Color = color,
        Action = action,
        Scope = RuleScope.Source,
    };
}
```

and have the existing submit path call it (the `PinAsTab` branch still needs only the color: use `NewSourceRule(...).Color` there, or keep the color computation by calling the helper once and reusing `rule.Color`). Behaviour must not change; `FilterBarTests` must stay green.

- [ ] **Step 4: Implement**

`InspectorViewModel`:

```csharp
internal async Task ApplyFieldRuleAsync(InspectorFieldViewModel field, RuleAction action)
{
    var file = _owner.SelectedFile?.Model;
    if (file is null)
        return;
    Error = null;
    if (string.IsNullOrWhiteSpace(field.Value))
    {
        Error = "This field has no value to match.";
        return;
    }

    try
    {
        var rule = _owner.FilterBar.NewSourceRule(
            file,
            field.Value,
            MatchMode.Literal,
            caseSensitive: true,
            action
        );
        await _owner.State.AddRuleAsync(file, rule);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        Error = ex.Message;
    }
}
```

`InspectorFieldViewModel` gets three commands built in its constructor (needs `using System.Reactive; using HexTail.Domain; using ReactiveUI.Reactive;`):

```csharp
ShowOnlyCommand = ReactiveCommand.CreateFromTask(
    () => owner.ApplyFieldRuleAsync(this, RuleAction.ShowOnly), owner.Owner.Scheduler);
HideCommand = ReactiveCommand.CreateFromTask(
    () => owner.ApplyFieldRuleAsync(this, RuleAction.Hide), owner.Owner.Scheduler);
HighlightCommand = ReactiveCommand.CreateFromTask(
    () => owner.ApplyFieldRuleAsync(this, RuleAction.Highlight), owner.Owner.Scheduler);
```

A failed command must not crash: `ApplyFieldRuleAsync` already catches everything except cancellation, so `ThrownExceptions` stays quiet (this is the lesson of the 2B final review).

- [ ] **Step 5: Panel UI**

In the field `DataTemplate` add a third column with a menu button:

```xml
<Grid ColumnDefinitions="Auto,*,Auto" ColumnSpacing="8" Margin="0,2">
  ... (key and value as before) ...
  <Button
    Grid.Column="2"
    Classes="icon"
    VerticalAlignment="Top"
    ToolTip.Tip="Field actions"
    AutomationProperties.Name="{Binding Key, StringFormat='Actions for {0}'}"
  >
    <ic:Icon Value="mdi-dots-horizontal" />
    <Button.Flyout>
      <MenuFlyout Placement="BottomEdgeAlignedRight">
        <MenuItem Header="Show only this value" Command="{Binding ShowOnlyCommand}" />
        <MenuItem Header="Hide value" Command="{Binding HideCommand}" />
        <MenuItem Header="Highlight" Command="{Binding HighlightCommand}" />
        <MenuItem Header="Copy value" Click="OnCopyFieldValue" />
      </MenuFlyout>
    </Button.Flyout>
  </Button>
</Grid>
```

`OnCopyFieldValue` in `InspectorPanel.axaml.cs`: `if (sender is MenuItem { DataContext: InspectorFieldViewModel field } && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(field.Value);` (a `MenuItem` inside a `MenuFlyout` inherits the field row's `DataContext`; if it does not in headless, set `Tag="{Binding}"` on the button and read it from `Click`'s sender instead — verify in the test below).

- [ ] **Step 6: UI test** — in `InspectorPanelTests`, select a structured row, open the inspector, assert the panel has an item in `InspectorFields` per field, then execute `owner.Inspector.Fields[0].HideCommand` (`await …Execute()`) and assert `file.Model.Rules` contains the Hide rule and the row is hidden from `owner.SelectedFile.SelectedView.Lines` after `Sync()`.

- [ ] **Step 7: Run tests (inspector + `FilterBar`), full suite, commit**

```bash
git add -A
git commit -m "feat(inspector): turn a field into a show-only, hide or highlight rule"
```

---

### Task 3: Context becomes an inspector action; the bottom pane is removed

**Files:**
- Create: `src/HexTail/ViewModels/InspectorContextRowViewModel.cs`
- Modify: `InspectorViewModel.cs`, `InspectorPanel.axaml`, `Views/LogView.axaml(.cs)`, `Views/LogWorkspace.axaml`, `ViewModels/LogViewViewModel.cs`, `ViewModels/FileTabViewModel.cs`, `ViewModels/MainWindowViewModel.cs`, `ViewModels/LogLineViewModel.cs`, `Application/AppState.cs`, `Application/FileTabState.cs`, `Persistence/AppConfig.cs`, `MainWindow.axaml.cs`
- Test: `InspectorViewModelTests.cs`, `JsonFileAppPersistenceTests.cs`; port or delete every test that references the removed symbols (`rg -l "ShowContext|ContextVisible|ContextLines|ContextRowHeight|ContextPaneSize|ContextAbove|InlineContextToggle" src/HexTail.Tests`).

**Interfaces:**
- Consumes: `FileBuffer.GetContextWindow(int index, int above, int below)`, `AppSettings.ContextAbove/ContextBelow`, `AppState.SelectLine(FileTabState, Line?)`.
- Produces:

```csharp
internal sealed class InspectorContextRowViewModel : ReactiveObject
{
    internal InspectorContextRowViewModel(Line line, bool isSelected);
    public Line Line { get; }
    public string Text => Line.Raw;
    public bool IsSelected { get; }
}
// InspectorViewModel
public bool ContextOpen { get; set; }                                   // in-session
public ObservableCollection<InspectorContextRowViewModel> ContextRows { get; }
public ReactiveCommand<Unit, Unit> ToggleContextCommand { get; }
internal void SelectContextRow(InspectorContextRowViewModel row);
```

- [ ] **Step 1: Failing tests**

Append to `InspectorViewModelTests` (uses 20 numbered lines, settings defaults 3 before / 10 after):

```csharp
    [AvaloniaFact]
    public async Task ContextListsTheTrueNeighboursOfTheSelectedRowAndClickingMovesTheSelection()
    {
        var lines = Enumerable.Range(0, 20).Select(i => new Line($"row {i}")).ToArray();
        var (owner, file, path, window) = await OpenAsync(lines);
        try
        {
            owner.State.SelectLine(file.Model, lines[10]);
            owner.Inspector.ContextOpen = true;
            owner.Inspector.Sync();

            Assert.Equal(
                Enumerable.Range(7, 14).Select(i => $"row {i}"),
                owner.Inspector.ContextRows.Select(row => row.Text)
            );
            Assert.True(owner.Inspector.ContextRows.Single(row => row.IsSelected).Text == "row 10");

            owner.Inspector.SelectContextRow(owner.Inspector.ContextRows[0]);
            owner.Inspector.Sync();

            Assert.Equal(7, file.Model.SelectedLine);
            Assert.Equal("row 7", owner.Inspector.ContextRows.Single(r => r.IsSelected).Text);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }

    [AvaloniaFact]
    public async Task ContextRowsAreEmptyWhileTheContextToggleIsOff()
    {
        var lines = Enumerable.Range(0, 5).Select(i => new Line($"row {i}")).ToArray();
        var (owner, file, path, window) = await OpenAsync(lines);
        try
        {
            owner.State.SelectLine(file.Model, lines[2]);
            owner.Inspector.Sync();

            Assert.False(owner.Inspector.ContextOpen);
            Assert.Empty(owner.Inspector.ContextRows);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
            File.Delete(path);
        }
    }
```

Add to `JsonFileAppPersistenceTests` a test that loads a session JSON containing the removed members (`"showContext": true`, `"contextAbove": 5`, `"contextBelow": 5` on an open-file entry and `"contextPaneSize": 300` under `window`) and asserts it loads with the file still open and no exception. Copy the JSON-literal style used by the neighbouring tests.

- [ ] **Step 2: Run, expect compile failures.**

- [ ] **Step 3: Add the inspector context**

`InspectorContextRowViewModel.cs`:

```csharp
using HexTail.Domain;
using ReactiveUI;

namespace HexTail.ViewModels;

internal sealed class InspectorContextRowViewModel : ReactiveObject
{
    internal InspectorContextRowViewModel(Line line, bool isSelected)
    {
        Line = line;
        IsSelected = isSelected;
    }

    public Line Line { get; }
    public string Text => Line.Raw;
    public bool IsSelected { get; }
}
```

In `InspectorViewModel`: a `_contextOpen` field; property `ContextOpen` (`RaiseAndSetIfChanged`, then `RebuildContext()`); `ContextRows`; `ToggleContextCommand = ReactiveCommand.Create(() => { ContextOpen = !ContextOpen; }, owner.Scheduler);` in the constructor; `SelectContextRow` calls `_owner.State.SelectLine(file, row.Line)` for the selected file. `Sync()` must also call `RebuildContext()` — and because context depends on the settings and neighbouring lines, `Sync` must rebuild even when the selected line is unchanged: move the early-return so that only the *field* rebuild is skipped, then always call `RebuildContext()`:

```csharp
private void RebuildContext()
{
    ContextRows.Clear();
    var file = _owner.SelectedFile?.Model;
    if (!ContextOpen || file is null || _index < 0 || _index >= file.Buffer.Count)
        return;
    var settings = _owner.State.Settings;
    var window = file.Buffer.GetContextWindow(_index, settings.ContextAbove, settings.ContextBelow);
    foreach (var line in window)
        ContextRows.Add(new InspectorContextRowViewModel(line, ReferenceEquals(line, _line)));
}
```

(Rebuilding a ≤ 63-row list per sync is fine; it only runs while the toggle is on. Guard against churn by skipping the rebuild when `ContextOpen` is false and `ContextRows.Count == 0`.)

Panel: a toggle button in the action strip (`<ToggleButton x:Name="InspectorContextToggle" Content="Show context" IsChecked="{Binding ContextOpen, Mode=TwoWay}" IsEnabled="{Binding HasLine}" AutomationProperties.Name="Show surrounding lines" />`) and, inside the scroll area after FIELDS, a `CONTEXT` heading plus an `ItemsControl x:Name="InspectorContext"` over `ContextRows`; each row is a `Button Classes="source-select"` whose `Command` is bound through `$parent[UserControl].DataContext`… simplest: handle `Click` in the code-behind (`OnContextRowClick` reads `((Button)sender).DataContext` and calls `viewModel.SelectContextRow`). Selected rows get `FontWeight="SemiBold"` via a `Classes.selected="{Binding IsSelected}"` style with `Background="{DynamicResource AccentSoftBrush}"` — reuse an existing selected-row brush (`rg -n "Brush Key" src/HexTail/Themes`).

- [ ] **Step 4: Remove the old pane (compile-driven)**

Remove, in this order, using `sb callers <Symbol>` and the compiler:
1. `LogView.axaml`: the two extra `RowDefinition`s, the `GridSplitter`, `ContextList`, and the "No lines to show." `TextBlock`. `LogView.axaml.cs`: `ContextList` wiring, `OnContextLinesChanged`, `OnContextSelectionChanged`, `ScrollContextToSelected`, and the `ContextVisible` property-changed branch. `LogWorkspace.axaml`: the `InlineContextToggle` `ToggleButton`.
2. `LogViewViewModel`: `ContextLines`, `ShowContext`, `ContextVisible`, `ContextRowHeight`, `ContextSplitterHeight`, `ContextEmpty`, `ContextEmptyVisible`, `ContextLinesFor`, the `context` sync block, the matching `ViewSnapshot` fields and their `RaisePropertyChanged` blocks, and the `isContext` argument of `SyncRows`/`LogLineViewModel` (rows are never context rows now: delete `LogLineViewModel.IsContext` and its brush branches).
3. `FileTabViewModel.ShowContext` + snapshot field; `MainWindowViewModel.SetShowContextAsync`; `AppState.SetShowContextAsync`; `FileTabState.ShowContext`, `ContextLines`, `ContextAbove`, `ContextBelow`; `AppState` lines that copy them (`rg -n "ContextAbove|ContextBelow|ShowContext" src/HexTail/Application/AppState.cs`) — keep `AppSettings.ContextAbove/ContextBelow` and their normalisation (`NormalizeSettings` should now clamp them to 0..50: `Math.Clamp(settings.ContextAbove, 0, 50)`).
4. `AppConfig.cs`: `PersistedFileTab.ShowContext/ContextAbove/ContextBelow`, the same on `PersistedElasticTab`, and `AppWindowState.ContextPaneSize`; `MainWindow.axaml.cs` `OnClosed` loses its `ContextPaneSize` line.
5. Tests: delete tests whose only purpose was the context pane (`ContextVisible`, `ContextRowHeight`, splitter, `InlineContextToggle`); rewrite any that merely used `ShowContext` as a stand-in for "a persisted per-tab setting" to use another property (`Levels`/`FollowAll`). Do not leave a test asserting nothing.

- [ ] **Step 5: Run tests** — Inspector, Persistence, `LogView*`, `WorkspaceViewModel*`, `AppState*`, `MainWindowInteraction*`, `StatusBar*`; then the full suite and the warning count (53).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "feat(inspector): show a row's neighbours in the inspector and drop the context pane"
```

---

### Task 4: Replace inline expansion with the inspector

**Files:**
- Modify: `Views/LogLineView.axaml(.cs)`, `ViewModels/LogLineViewModel.cs`, `ViewModels/LogViewViewModel.cs`, `ViewModels/FileTabViewModel.cs`, `ViewModels/MainWindowViewModel.cs`, `Application/AppState.cs`, `Application/FileTabState.cs`
- Test: `src/HexTail.Tests/Ui/LogLineInteractionTests.cs`, `src/HexTail.Tests/Application/AppStateTests.cs`

**Interfaces:**
- Consumes: `InspectorViewModel.Open()`, `MainWindowViewModel.Inspector`.
- Produces: `LogLineViewModel.Inspect()` (select the row, open the inspector); the row menu item **Inspect** (gesture `Enter`).

- [ ] **Step 1: Failing test**

Replace `KeyboardExpansionShowsTheEntireSelectedRawLine` with:

```csharp
    [AvaloniaFact]
    public async Task EnterOnARowOpensTheInspectorOnThatRow()
    {
        var path = Path.GetTempFileName();
        var window = TestWindow.Create(out var workspace);
        try
        {
            await workspace.OpenPathsCommand.Execute([path]);
            var file = workspace.SelectedFile!;
            var raw = "  " + new string('x', 2_000) + "\tfinal text  ";
            file.Model.Buffer.Append([new Line("other row"), new Line(raw)]);
            file.SyncViews();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = window
                .GetVisualDescendants()
                .OfType<LogView>()
                .Single()
                .FindControl<ListBox>("LogList")!;
            list.SelectedIndex = 1;
            var item = Assert.IsType<ListBoxItem>(list.ContainerFromIndex(1));
            item.Focus();

            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            await UiWait.Until(() => workspace.Inspector.IsOpen);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(1, file.Model.SelectedLine);
            Assert.Equal(raw, workspace.Inspector.RawText);
        }
        finally
        {
            window.Close();
            await workspace.DisposeAsync();
            File.Delete(path);
        }
    }
```

Add a second test `DoubleTapOpensTheInspector` using the same setup and `Tapped`/`DoubleTapped` raised on the row's inner `Border` (see how `RowMenuAndKeyboardCopyTheAddressedLineAndItsFields` and neighbours drive pointer events; if there is no existing double-tap test, use `row.FindControl`-free `RaiseEvent(new TappedEventArgs(DoubleTappedEvent, …))` on the `Border`). Update the copy test if it referenced the removed `Toggle full row details` item.

- [ ] **Step 2: Run, expect failure.**

- [ ] **Step 3: Implement**

`LogLineViewModel`: add `internal void Inspect() { Select(); _owner.Workspace.Inspector.Open(); }`; delete `IsExpanded`, `FieldsVisible`, `SyncExpansion`, `ToggleExpanded`, the `ExpandedLine` lookup in `Refresh`, and their `RaisePropertyChanged` calls. Keep `ParsedFieldsText`/`HasParsedFields` (the *Copy fields* menu item uses them) — `ParsedFieldsText` is still rendered lazily in `Render()`, but its on-screen `SelectableTextBlock` goes away.

`LogLineView.axaml`: delete `RawLineDetails` and the parsed-fields `SelectableTextBlock` (leave the single `HighlightedTextBlock`; `StackPanel` can stay); replace the first menu item with `<MenuItem Header="Inspect" InputGesture="Enter" Click="OnInspect" />`.

`LogLineView.axaml.cs`: `OnDoubleTapped` calls `row.Inspect()`; `OnInspect` replaces `OnToggleDetails`; the Enter branch of `OnRowKeyDown` calls `row.Inspect()`.

State cleanup: remove `FileTabState.ExpandedLine` and its shifting block in `OnBufferChanged`, `AppState.ToggleExpanded`, `MainWindowViewModel.ToggleExpanded`, `FileTabViewModel.ToggleExpanded`, `LogViewViewModel.ToggleExpandedCommand`, `_lastExpandedLine`, `expandedChanged`, `SyncExpansion`. Delete or port the tests that covered `ExpandedLine` (`AppStateTests`, `WorkspaceViewModelTests`).

- [ ] **Step 4: Tests and commit**

Run `LogLineInteraction`, `AppState`, `WorkspaceViewModel`, `LogView`, `RowKey`, then the full suite.

```bash
git add -A
git commit -m "feat(inspector): open the inspector from a row instead of expanding it inline"
```

---

### Task 5: Bookmarks

**Files:**
- Modify: `Application/FileTabState.cs`, `Application/AppState.cs`, `ViewModels/LogLineViewModel.cs`, `ViewModels/LogViewViewModel.cs`, `ViewModels/InspectorViewModel.cs`, `Views/InspectorPanel.axaml`, `Views/LogLineView.axaml`, `Input/ShellShortcuts.cs`, `MainWindow.axaml.cs`, `ViewModels/MainWindowViewModel.cs` (palette entry)
- Test: `BookmarkTests.cs` (create), `RowKeyTests.cs`, `InspectorViewModelTests.cs`, `ShellShortcutsTests.cs`

**Interfaces:**
- Produces:

```csharp
// FileTabState
public SortedSet<int> Bookmarks { get; }        // buffer indices
public int BookmarksVersion { get; }            // bumped on every change
public bool IsBookmarked(Line line);
// AppState
public void ToggleBookmark(FileTabState tab, Line line);
// LogLineViewModel
public bool IsBookmarked { get; }
internal void ToggleBookmark();
// LogViewViewModel
internal void MoveToBookmark(int direction);
// InspectorViewModel
public bool IsBookmarked { get; }
public ReactiveCommand<Unit, Unit> ToggleBookmarkCommand { get; }
// ShellAction.ToggleBookmark ('m'), NextBookmark (']'), PreviousBookmark ('[')
```

- [ ] **Step 1: Failing tests**

Create `src/HexTail.Tests/Application/BookmarkTests.cs` (a direct `FileTabState` test; the tab needs a tailer, so the file defines a tiny fake):

```csharp
using HexTail.Application;
using HexTail.Domain;
using HexTail.Tailing;

namespace HexTail.Tests.Application;

public sealed class BookmarkTests
{
    private sealed class NullTailer : ILogTailer
    {
        public string SourceId => "t";
        public string DisplayName => "t";
        public Task Completion => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static FileTabState Tab(int maxLines) =>
        new("t", "t.log", new FileBuffer(maxLines), new PlainTextParser(), new NullTailer());

    [Fact]
    public void ToggleAddsAndRemovesAndBumpsTheVersion()
    {
        var tab = Tab(10);
        tab.Buffer.Append([new Line("a"), new Line("b"), new Line("c")]);
        var version = tab.BookmarksVersion;

        tab.ToggleBookmarkAt(2);
        Assert.Equal([2], tab.Bookmarks);
        Assert.True(tab.IsBookmarked(tab.Buffer[2]));
        Assert.False(tab.IsBookmarked(tab.Buffer[0]));
        Assert.True(tab.BookmarksVersion > version);

        tab.ToggleBookmarkAt(2);
        Assert.Empty(tab.Bookmarks);
    }

    [Fact]
    public void BookmarksShiftWhenOldLinesRollOutAndDropWhenTheirLineDoes()
    {
        var tab = Tab(3);
        tab.Buffer.Append([new Line("a"), new Line("b"), new Line("c")]);
        tab.ToggleBookmarkAt(0);
        tab.ToggleBookmarkAt(2);

        tab.Buffer.Append([new Line("d"), new Line("e")]); // "a" and "b" roll out

        Assert.Equal([0], tab.Bookmarks); // "c" moved from index 2 to 0; the bookmark on "a" is gone
        Assert.True(tab.IsBookmarked(tab.Buffer[0]));
        Assert.Equal("c", tab.Buffer[0].Raw);
    }

    [Fact]
    public void ClearingTheBufferClearsTheBookmarks()
    {
        var tab = Tab(10);
        tab.Buffer.Append([new Line("a")]);
        tab.ToggleBookmarkAt(0);
        var version = tab.BookmarksVersion;

        tab.Buffer.Clear();

        Assert.Empty(tab.Bookmarks);
        Assert.True(tab.BookmarksVersion > version);
    }
}
```

Add one `AppState.ToggleBookmark` test to `AppStateTests` only if the file has a cheap local-file fixture (`rg -n "OpenFileAsync|OpenPathsAsync" src/HexTail.Tests/Application/AppStateTests.cs`); otherwise the `RowKeyTests` test below already drives `AppState.ToggleBookmark` end to end.

`RowKeyTests`:

```csharp
    [AvaloniaFact]
    public async Task MoveToBookmarkJumpsBetweenBookmarkedVisibleRows()
    {
        var path = await WriteLinesAsync("a", "b", "c", "d", "e");
        try
        {
            var window = TestWindow.Create(out var owner);
            await owner.OpenPathsCommand.Execute([path]);
            var file = owner.SelectedFile!;
            var view = await LoadedView(owner, 5);
            owner.State.ToggleBookmark(file.Model, file.Model.Buffer[1]);
            owner.State.ToggleBookmark(file.Model, file.Model.Buffer[3]);
            view.Sync();

            view.MoveToBookmark(1);
            Assert.Equal(1, file.Model.SelectedLine);
            view.MoveToBookmark(1);
            Assert.Equal(3, file.Model.SelectedLine);
            view.MoveToBookmark(1);
            Assert.Equal(3, file.Model.SelectedLine);
            view.MoveToBookmark(-1);
            Assert.Equal(1, file.Model.SelectedLine);
            window.Close();
        }
        finally
        {
            File.Delete(path);
        }
    }
```

`ShellShortcutsTests`: `m` → `ToggleBookmark`, `Key.OemCloseBrackets` → `NextBookmark`, `Key.OemOpenBrackets` → `PreviousBookmark` (all `textInputFocused: false`); with `textInputFocused: true` → `None`. `InspectorViewModelTests`: toggling via `ToggleBookmarkCommand` flips `IsBookmarked` after `Sync()`, and the title/raw stay.

- [ ] **Step 2: Run, expect compile failures.**

- [ ] **Step 3: State**

`FileTabState`:

```csharp
public SortedSet<int> Bookmarks { get; } = [];
public int BookmarksVersion { get; private set; }

public bool IsBookmarked(Line line)
{
    foreach (var index in Bookmarks)
        if (index >= 0 && index < Buffer.Count && ReferenceEquals(Buffer[index], line))
            return true;
    return false;
}

internal void ToggleBookmarkAt(int index)
{
    if (!Bookmarks.Remove(index))
        Bookmarks.Add(index);
    BookmarksVersion++;
}
```

In `OnBufferChanged` (replacing the removed `ExpandedLine` block): when `change.Cleared` → if `Bookmarks.Count > 0` clear and bump the version; else if `change.RolledOutCount > 0 && Bookmarks.Count > 0` → rebuild with `Bookmarks.Select(i => i - change.RolledOutCount).Where(i => i >= 0)` into a new set, replace contents, bump the version.

`AppState.ToggleBookmark` follows `SelectLine`'s locking shape: lock `_gate`, require `_files.Contains(tab)`, `FindLineIndex(tab, line)` (≥ 0), `tab.ToggleBookmarkAt(index)`, then `NotifyChanged()` outside the lock. Bookmarks are not persisted, so no `SaveAsync`.

- [ ] **Step 4: Rows, view model, keys**

`LogLineViewModel`: `public bool IsBookmarked { get; private set; }`; compute in `Refresh` (`IsBookmarked = _file.Model.IsBookmarked(Line)`), raise in the `notify` block, and add `internal void SyncBookmark()` that recomputes and raises `IsBookmarked` only when it changed; `internal void ToggleBookmark() => _owner.Workspace.State.ToggleBookmark(_file.Model, Line);`.

`LogViewViewModel`: field `_lastBookmarksVersion`; in `Sync`, `if (_lastBookmarksVersion != _file.Model.BookmarksVersion) { _lastBookmarksVersion = …; foreach (var row in Lines) row.SyncBookmark(); }` (same shape as the old expansion sync). `MoveToBookmark(int direction)` mirrors `MoveToMatch`: start index from `CurrentRowIndex()` (or the edge), scan `Lines` in `direction` for `File.IsBookmarked(Lines[i].Line)`, then `SelectRow`.

`ShellShortcuts.cs`: add `ToggleBookmark`, `NextBookmark`, `PreviousBookmark` to `ShellAction`; in the non-primary switch: `Key.M => new(ShellAction.ToggleBookmark), Key.OemCloseBrackets => new(ShellAction.NextBookmark), Key.OemOpenBrackets => new(ShellAction.PreviousBookmark),` (use the exact `Avalonia.Input.Key` member names: `OemOpenBrackets` and `OemCloseBrackets`). `MainWindow.axaml.cs` dispatch:

```csharp
case ShellAction.ToggleBookmark when file is not null:
    ViewModel.Inspector.ToggleBookmarkOnSelection();
    break;
case ShellAction.NextBookmark when file is not null:
    file.SelectedView.MoveToBookmark(1);
    break;
case ShellAction.PreviousBookmark when file is not null:
    file.SelectedView.MoveToBookmark(-1);
    break;
```

`InspectorViewModel`: `IsBookmarked` (computed in `Sync()` as `file.IsBookmarked(_line)`, also re-evaluated when `BookmarksVersion` changes — store `_lastBookmarksVersion` and recompute on every `Sync`), `ToggleBookmarkCommand = ReactiveCommand.Create(ToggleBookmarkOnSelection, owner.Scheduler)`, and `internal void ToggleBookmarkOnSelection()` that does nothing without a selected row and otherwise calls `_owner.State.ToggleBookmark(file, line)`.

UI: `LogLineView.axaml` — a small glyph at the row start, e.g. wrap the existing `HighlightedTextBlock` in `Grid ColumnDefinitions="Auto,*"` with `<ic:Icon Value="mdi-bookmark" Foreground="{DynamicResource AccentBrush}" IsVisible="{Binding IsBookmarked}" Margin="0,0,6,0" />` in column 0 (add the `ic` namespace); add `<MenuItem Header="Toggle bookmark" InputGesture="M" Click="OnToggleBookmark" />` (code-behind: `row.Select(); row.ToggleBookmark();`). `InspectorPanel.axaml`: a `ToggleButton x:Name="InspectorBookmark"` bound `IsChecked="{Binding IsBookmarked, Mode=OneWay}"` with `Command="{Binding ToggleBookmarkCommand}"` and content "Bookmark". Palette: "Toggle bookmark on selected row" `"M"`.

- [ ] **Step 5: Tests and commit**

Run `AppState`, `RowKey`, `ShellShortcuts`, `Inspector`, `LogView`, then the full suite.

```bash
git add -A
git commit -m "feat(inspector): bookmark rows and jump between bookmarks with m, [ and ]"
```

---

### Task 6: Settings — Limits, left navigation, compact modal

**Files:**
- Modify: `Persistence/AppConfig.cs`, `Application/AppState.cs`, `Tailing/LogSourceService.cs`, `ViewModels/SettingsViewModel.cs`, `Views/SettingsPanel.axaml`
- Test: `ViewModels/SettingsSectionTests.cs`, `ViewModels/SettingsLimitsTests.cs` (create), `Ui/SettingsReflowTests.cs`, `Ui/MainWindowInteractionTests.cs`, `Application/AppStateTests.cs`

**Interfaces:**
- Produces:

```csharp
// AppSettings
public int ElasticInitialLines { get; init; } = 10_000;
// LogSourceService
internal Elastic.ElasticTailer CreateElastic(..., Func<DateTimeOffset>? now = null, int? maxInitialLines = null);
// SettingsViewModel (decimal? because NumericUpDown.Value is decimal?)
public decimal? MaxLinesValue { get; set; }
public decimal? ContextAboveValue { get; set; }
public decimal? ContextBelowValue { get; set; }
public decimal? ElasticInitialLinesValue { get; set; }
// SectionIndex now clamps 0..2; Section is "appearance" | "elastic" | "limits"
```

- [ ] **Step 1: Failing tests**

Update `SettingsSectionTests` for the new range (`SectionIndex = 3` clamps to 2, `Section` is `"limits"` at 2), and create `SettingsLimitsTests`:

```csharp
using Avalonia.Headless.XUnit;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.ViewModels;

public sealed class SettingsLimitsTests
{
    [AvaloniaFact]
    public async Task EditingALimitPersistsItClamped()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.Settings.MaxLinesValue = 5_000;
            await UiWait.Until(() => owner.State.Settings.MaxLines == 5_000);

            owner.Settings.MaxLinesValue = 10; // below the minimum
            await UiWait.Until(() => owner.State.Settings.MaxLines == 1_000);
            Assert.Equal(1_000, owner.Settings.MaxLinesValue);

            owner.Settings.ContextBelowValue = 500; // above the maximum
            await UiWait.Until(() => owner.State.Settings.ContextBelow == 50);

            owner.Settings.ElasticInitialLinesValue = 25_000;
            await UiWait.Until(() => owner.State.Settings.ElasticInitialLines == 25_000);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ClearingTheFieldKeepsTheCurrentValue()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            var before = owner.State.Settings.MaxLines;
            owner.Settings.MaxLinesValue = null;
            Assert.Equal(before, owner.State.Settings.MaxLines);
            Assert.Equal(before, owner.Settings.MaxLinesValue);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
        }
    }
}
```

In `AppStateTests` add: a new settings value `ElasticInitialLines = 50` normalises to 100 and `3_000_000` to 100_000, `MaxLines = 0` to 1_000. In a `LogSourceService` test (find an existing one with `rg -l "CreateElastic" src/HexTail.Tests`), assert that passing `maxInitialLines: 7` leads the tailer to stop its first fetch at 7 lines (copy the fixture style of the existing Elastic tailer initial-limit test).

- [ ] **Step 2: Run, expect compile failures.**

- [ ] **Step 3: Settings model and plumbing**

`AppSettings`: add `ElasticInitialLines`. `AppState.NormalizeSettings`:

```csharp
MaxLines = Math.Clamp(settings.MaxLines, 1_000, 1_000_000),
ContextAbove = Math.Clamp(settings.ContextAbove, 0, 50),
ContextBelow = Math.Clamp(settings.ContextBelow, 0, 50),
ElasticInitialLines = Math.Clamp(settings.ElasticInitialLines, 100, 100_000),
```

`LogSourceService.CreateElastic`: new trailing parameter `int? maxInitialLines = null`; pass `options: maxInitialLines is { } lines ? _options with { MaxInitialLines = lines } : _options`. Both call sites in `AppState` (lines ~169 and ~532) pass `maxInitialLines: _settings.ElasticInitialLines`.

`SettingsViewModel`: four `decimal?` properties in the style of `Density` (private backing fields, `_syncing` guard, commit through `CommitAsync(_owner.State.Settings with { … })`). Setter rule: a null value restores the current setting (no commit); otherwise `(int)Math.Clamp(value, min, max)` is committed and the property re-synced to the clamped value. `Sync(AppSettings)` loads all four inside the existing `_syncing` block. `SectionIndex` clamps `0..2` and `_section = index switch { 1 => "elastic", 2 => "limits", _ => "appearance" }`; `Section`'s setter (see lines ~153-157) must accept `"limits"`.

- [ ] **Step 4: View**

`SettingsPanel.axaml`:
- `Border Classes="settings-modal"`: `Width="960" Height="640"`.
- `TabControl`: `TabStripPlacement="Left"`; the tab header `StackPanel` is already icon+text. The tab-strip style in the theme may assume a top strip (`rg -n "TabItem" src/HexTail/Themes/*.axaml`): add a style so left-placed headers are left-aligned with a fixed minimum width of 140 and no bottom-border accent (use the accent as a left border instead). The Elastic `ScrollViewer` content is unchanged.
- Third `TabItem` (header icon `mdi-tune-variant`, text "Limits") after Elastic:

```xml
<TabItem>
  <TabItem.Header>
    <StackPanel Orientation="Horizontal" Spacing="8">
      <ic:Icon Value="mdi-tune-variant" />
      <TextBlock Text="Limits" />
    </StackPanel>
  </TabItem.Header>
  <Border Classes="settings-section" Margin="16,0,0,0">
    <StackPanel Spacing="6" MaxWidth="420" HorizontalAlignment="Left">
      <TextBlock Text="Maximum lines per source" />
      <NumericUpDown
        x:Name="MaxLinesBox"
        Minimum="1000"
        Maximum="1000000"
        Increment="1000"
        FormatString="N0"
        Value="{Binding Settings.MaxLinesValue, Mode=TwoWay}"
        AutomationProperties.Name="Maximum lines per source"
      />
      <TextBlock Text="Applies to sources opened after the change." Classes="hint" />
      <TextBlock Text="Context lines before the selected row" Margin="0,8,0,0" />
      <NumericUpDown
        x:Name="ContextAboveBox"
        Minimum="0"
        Maximum="50"
        Value="{Binding Settings.ContextAboveValue, Mode=TwoWay}"
        AutomationProperties.Name="Context lines before"
      />
      <TextBlock Text="Context lines after the selected row" />
      <NumericUpDown
        x:Name="ContextBelowBox"
        Minimum="0"
        Maximum="50"
        Value="{Binding Settings.ContextBelowValue, Mode=TwoWay}"
        AutomationProperties.Name="Context lines after"
      />
      <TextBlock Text="Initial Elastic fetch (newest lines)" Margin="0,8,0,0" />
      <NumericUpDown
        x:Name="ElasticInitialLinesBox"
        Minimum="100"
        Maximum="100000"
        Increment="1000"
        FormatString="N0"
        Value="{Binding Settings.ElasticInitialLinesValue, Mode=TwoWay}"
        AutomationProperties.Name="Initial Elastic fetch"
      />
      <TextBlock Text="Applies to Elastic views opened after the change." Classes="hint" />
    </StackPanel>
  </Border>
</TabItem>
```

If `NumericUpDown` is not available under the default theme in Avalonia 12.1.1 (it ships in `Avalonia.Controls` and is styled by Fluent), the build will tell you; fall back to a `TextBox` with a numeric parse in the setters only in that case.

Fix `SettingsReflowTests` / `MainWindowInteractionTests` where they assumed the old 1600×920 size or a top tab strip (they set `SectionIndex` and measure; update the assumed widths, do not delete the reflow assertions). The focus pull-back code in `MainWindow.axaml.cs` must keep working: run `AccessibilityTests` and `MainWindowInteraction` in full.

- [ ] **Step 5: Tests and commit**

Run `SettingsSection`, `SettingsLimits`, `SettingsReflow`, `ElasticSettings`, `MainWindowInteraction`, `Accessibility`, `AppState`, the Elastic tailer tests, then the full suite.

```bash
git add -A
git commit -m "feat(settings): add Limits, navigate sections on the left and shrink the modal"
```

---

### Task 7: Documentation

**Files:**
- Modify: `README.md`, `docs/user-guide.md`, `docs/architecture.md`

- [ ] **Step 1: Update the docs**

- `README.md`: replace the "Optional inline context view" bullet (line ~29) and the Follow/Context sentence (lines ~99-101) with the inspector (`Ctrl+I`; raw text, fields with show-only / hide / highlight / copy, copy raw and JSON, show context, bookmark); mention bookmarks and Settings → Limits; remove any mention of the context-pane size being saved (line ~34, ~151) and say the inspector's open state and width are.
- `docs/user-guide.md`: remove line ~97's expansion paragraph and replace it with an **Inspector** section: opening it (`Ctrl+I`, Enter, double-click, row menu *Inspect*), the field menu, *Copy raw* / *Copy JSON* (disabled for plain text), *Show context* (uses the before/after numbers from Settings → Limits; clicking a context row selects it), *Bookmark* with `m`, `]`, `[` (bookmarks are not saved), and the resize handle. Update the keyboard list near line 101-105 (`Ctrl+I`, `m`, `[`, `]`). Describe **Settings → Limits** and the left-hand section list.
- `docs/architecture.md`: line 49 (shell commands no longer include a context toggle), line 51 (`FileTabState` no longer owns a context selection beyond `SelectedLine`; it owns `Bookmarks`), line 54 ("context view" → "inspector panel"), and line 75 (the "inline context view" bullet becomes: the inspector's context list is a window over the selected buffer line using `AppSettings.ContextAbove/ContextBelow`, and its toggle is in-session UI state). Add `InspectorViewModel` to the view-model table.
- Search the three files for leftovers: `rg -n -i "inline context|context pane|ContextPaneSize|expand" README.md docs` — every hit must be intentional.

- [ ] **Step 2: Commit**

```bash
git add -A
git commit -m "docs: describe the inspector, bookmarks and the Limits settings"
```

---

### Task 8: Final verification

- [ ] **Step 1:** Run the full suite twice: `rtk dotnet test src/HexTail.Tests --no-build` (all green, no new warnings: build shows 53).
- [ ] **Step 2:** Load a copy of the real `session.json` (`~/.config/HexTail/session.json` or the path the app uses; never modify the original) through `JsonFileAppPersistence` in a scratch test or `dotnet script`, confirm it loads with its 7 global rules, that `showContext`/`contextAbove`/`contextPaneSize` (if present) are ignored, and that a save/load round trip keeps rules, saved filters and window state.
- [ ] **Step 3:** Report what could not be checked by eye: panel layout and resize handle, field menu flyout, context list styling, bookmark glyph, left-hand tab strip, `NumericUpDown` look.

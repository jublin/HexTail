# Cyber Tail Avalonia UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace AtomUI with a polished, dark-only native Avalonia workspace whose controls work on Windows, Linux, and macOS while preserving the existing virtualized live-tail path.

**Architecture:** Keep the current explicit `AppState`/`TailerService`/persistence composition and ReactiveUI view-model boundary. Use Avalonia Fluent controls and one small Cyber Tail resource dictionary; retain code-behind only for native picker/drop, responsive window geometry, and virtualized row mechanics. The existing bounded `ObservableCollection<Line>` remains the UI source until the separately approved whole-file engine phase.

**Tech Stack:** .NET 10, Avalonia 12.1.1, Avalonia Fluent, ReactiveUI.Avalonia.Reactive 12.1.1, System.Reactive 6.1.0, Xaml.Behaviors 12.0.5 event packages, FluentIcons.Avalonia, xUnit v3, Avalonia.Headless.XUnit 12.1.1.

## Global Constraints

- Preserve the user's current uncommitted edits in `Directory.Packages.props`, `src/HexTailSharp/HexTailSharp.csproj`, and `src/HexTailSharp/MainWindow.axaml`; treat them as migration input rather than reverting them.
- Remove every AtomUI namespace, bootstrap call, type, resource, and package reference.
- Use only `FluentIcons.Avalonia`; remove Material Icons and both draggable packages.
- Use `ReactiveUI.Avalonia.Reactive`, not `ReactiveUI.Avalonia`. The former is the Avalonia 12.1.1 distribution that preserves the existing `Unit` and `IScheduler` System.Reactive API. Rewriting the application to `RxVoid` and `ISequencer` provides no product value.
- Use only `Xaml.Behaviors.Interactivity`, `Xaml.Behaviors.Interactions`, and `Xaml.Behaviors.Interactions.Events`. Native `Command` binding remains the default; keep file-drop disposal and virtualized scroll attachment in code-behind.
- Do not replace `Lines` collections during ordinary appends, recreate file tabs for settings changes, add a DI container, add speculative paging abstractions, or add pixel-baseline tests.
- Each task ends in one Conventional Commit. Do not push.
- Run all shell commands through `rtk` per repository policy.

---

## Task 1: Restore a buildable ReactiveUI baseline

**Files:**

- Modify: `Directory.Packages.props`
- Modify: `src/HexTailSharp/HexTailSharp.csproj`

**Interfaces:**

- Consumes: existing `ReactiveCommand<TInput, Unit>`, `Interaction<Unit, TOutput>`, and `IScheduler` signatures.
- Produces: the same public/internal C# signatures with an Avalonia 12.1.1-compatible ReactiveUI package graph.

- [ ] **Step 1: Capture the current package/API failure**

Run:

```bash
rtk dotnet build src/HexTailSharp.slnx --no-restore
```

Expected: FAIL with `Unit`/`RxVoid` and `IScheduler`/`ISequencer` conversion errors.

- [ ] **Step 2: Select the System.Reactive distribution**

In `Directory.Packages.props`, replace the package version entry:

```xml
<PackageVersion Include="ReactiveUI.Avalonia.Reactive" Version="12.1.1" />
```

In `src/HexTailSharp/HexTailSharp.csproj`, replace the reference:

```xml
<PackageReference Include="ReactiveUI.Avalonia.Reactive" />
```

Do not change existing C# command or scheduler types.

- [ ] **Step 3: Restore and verify the compatibility fix**

Run:

```bash
rtk dotnet restore src/HexTailSharp.slnx
rtk dotnet build src/HexTailSharp.slnx --no-restore
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --no-build
```

Expected: PASS.

- [ ] **Step 4: Commit the isolated build fix**

```bash
rtk git add Directory.Packages.props src/HexTailSharp/HexTailSharp.csproj
rtk git commit -m "fix(ui): use reactiveui system reactive distribution"
```

---

## Task 2: Normalize persisted appearance to Cyber Tail

**Files:**

- Modify: `src/HexTailSharp.Tests/Application/AppStateTests.cs`
- Modify: `src/HexTailSharp/Application/AppState.cs`
- Modify: `src/HexTailSharp/Persistence/AppConfig.cs`

**Interfaces:**

- Consumes: `ValueTask AppState.UpdateSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)` and legacy serialized `AppSettings.Theme`/`SettingsMenuAlignment` values.
- Produces: `ThemeCatalog.Names == ["dark"]`; `ThemeCatalog.Normalize(string?) == "dark"`; every normalized save uses `SettingsMenuAlignment.Right`.

- [ ] **Step 1: Write the failing migration assertions**

Change the settings assertions in `AppStateTests` to:

```csharp
Assert.Equal("dark", settings.Theme);
Assert.Equal(SettingsMenuAlignment.Right, settings.SettingsMenuAlignment);
```

Replace `ThemeCatalog_UsesNativeThemeVariants` with:

```csharp
[Theory]
[InlineData(null)]
[InlineData("system")]
[InlineData("light")]
[InlineData("material-wcag")]
[InlineData("dark")]
public void ThemeCatalog_NormalizesEveryLegacyValueToDark(string? value)
{
    Assert.Equal(["dark"], ThemeCatalog.Names);
    Assert.Equal("dark", ThemeCatalog.Normalize(value));
}
```

- [ ] **Step 2: Confirm the old behavior fails**

Run:

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "UpdateSettings_PersistsNormalizedGlobalRules|ThemeCatalog_NormalizesEveryLegacyValueToDark"
```

Expected: FAIL because left placement and light/system themes are still retained.

- [ ] **Step 3: Implement the one-way compatibility migration**

Replace `ThemeCatalog` with:

```csharp
public static class ThemeCatalog
{
    public static readonly string[] Names = ["dark"];

    public static bool Contains(string? theme) => string.Equals(theme, "dark", StringComparison.Ordinal);

    public static string Normalize(string? theme) => "dark";
}
```

In `AppState.NormalizeSettings`, make the appearance assignments unconditional:

```csharp
Theme = "dark",
Density = Enum.IsDefined(settings.Density) ? settings.Density : UiDensity.Comfortable,
LogFontSize = Enum.IsDefined(settings.LogFontSize)
    ? settings.LogFontSize
    : LogFontSize.Medium,
SettingsMenuAlignment = SettingsMenuAlignment.Right,
```

Keep the serialized properties and enums so old configuration files remain readable.

- [ ] **Step 4: Run the focused and full suites**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "UpdateSettings_PersistsNormalizedGlobalRules|ThemeCatalog_NormalizesEveryLegacyValueToDark"
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj
```

Expected: PASS.

- [ ] **Step 5: Commit the migration fix**

```bash
rtk git add src/HexTailSharp.Tests/Application/AppStateTests.cs src/HexTailSharp/Application/AppState.cs src/HexTailSharp/Persistence/AppConfig.cs
rtk git commit -m "fix(settings): normalize cyber tail appearance"
```

---

## Task 3: Split the workspace view models without changing identity semantics

**Files:**

- Modify: `src/HexTailSharp.Tests/ViewModels/WorkspaceViewModelTests.cs`
- Create: `src/HexTailSharp/ViewModels/MainWindowViewModel.cs`
- Create: `src/HexTailSharp/ViewModels/FileTabViewModel.cs`
- Create: `src/HexTailSharp/ViewModels/LogViewViewModel.cs`
- Create: `src/HexTailSharp/ViewModels/SettingsViewModel.cs`
- Delete: `src/HexTailSharp/ViewModels/WorkspaceViewModels.cs`

**Interfaces:**

- Consumes and preserves exactly:

```csharp
internal sealed class MainWindowViewModel : ReactiveObject, IAsyncDisposable
internal sealed class FileTabViewModel : ReactiveObject
internal sealed class LogViewViewModel : ReactiveObject
internal sealed class SettingsViewModel : ReactiveObject
internal sealed class LabelSettingViewModel : ReactiveObject
internal sealed class ExclusionSettingViewModel : ReactiveObject
```

- Produces: unchanged bindings and command/property names; `FileTabViewModel` instances remain keyed by `FileTabState` reference, and `LogViewViewModel.Lines` stays one stable `ObservableCollection<Line>`.

- [ ] **Step 1: Add identity characterization tests**

Add tests using the existing `AppState` test setup:

```csharp
[Fact]
public void SyncCollection_AppendRetainsCollectionIdentity()
{
    var rows = new ObservableCollection<Line>();
    var original = rows;

    LogViewViewModel.SyncCollection(rows, [new Line("one"), new Line("two")]);

    Assert.Same(original, rows);
    Assert.Equal(2, rows.Count);
}

[Fact]
public void SyncCollection_UnchangedTailDoesNotRaiseReset()
{
    var line = new Line("one");
    var rows = new ObservableCollection<Line> { line };
    var changes = 0;
    rows.CollectionChanged += (_, _) => changes++;

    LogViewViewModel.SyncCollection(rows, [line]);

    Assert.Equal(0, changes);
}
```

- [ ] **Step 2: Run the characterization tests**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter WorkspaceViewModelTests
```

Expected: PASS before the move.

- [ ] **Step 3: Move types mechanically into focused files**

Use the following ownership map and keep method bodies/signatures unchanged:

```text
MainWindowViewModel.cs  -> MainWindowViewModel
FileTabViewModel.cs     -> FileTabViewModel
LogViewViewModel.cs     -> LogViewViewModel
SettingsViewModel.cs    -> SettingsViewModel, LabelSettingViewModel, ExclusionSettingViewModel
```

Every file uses the same file-scoped namespace:

```csharp
namespace HexTailSharp.ViewModels;
```

Delete `WorkspaceViewModels.cs` only after all six types compile from their new files.

- [ ] **Step 4: Verify no behavior changed**

```bash
rtk dotnet build src/HexTailSharp.slnx
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj
```

Expected: PASS.

- [ ] **Step 5: Commit the mechanical refactor**

```bash
rtk git add src/HexTailSharp/ViewModels src/HexTailSharp.Tests/ViewModels/WorkspaceViewModelTests.cs
rtk git commit -m "refactor(ui): split workspace view models"
```

---

## Task 4: Add the Fluent dark foundation

**Files:**

- Modify: `Directory.Packages.props`
- Modify: `src/HexTailSharp/HexTailSharp.csproj`
- Modify: `src/HexTailSharp/App.axaml`
- Modify: `src/HexTailSharp/App.axaml.cs`
- Create: `src/HexTailSharp/Styles/CyberTail.axaml`

**Interfaces:**

- Consumes: Avalonia application resources and compiled XAML.
- Produces: a dark `FluentTheme` and stable resource keys (`CyberSurfaceBrush`, `CyberRaisedBrush`, `CyberBorderBrush`, `CyberTextBrush`, `CyberMutedBrush`, `CyberCyanBrush`, `CyberGreenBrush`, `CyberMagentaBrush`, `CyberErrorBrush`).

- [ ] **Step 1: Reference the new theme before creating it**

Add the central version and app reference:

```xml
<PackageVersion Include="Avalonia.Themes.Fluent" Version="12.1.1" />
<PackageReference Include="Avalonia.Themes.Fluent" />
```

In `App.axaml`, put Fluent before the not-yet-created app style include:

```xml
<Application.Styles>
  <FluentTheme DensityStyle="Compact" />
  <StyleInclude Source="avares://HexTailSharp/Styles/CyberTail.axaml" />
</Application.Styles>
```

- [ ] **Step 2: Confirm compiled XAML rejects the missing resource**

```bash
rtk dotnet build src/HexTailSharp/HexTailSharp.csproj
```

Expected: FAIL because `Styles/CyberTail.axaml` does not exist.

- [ ] **Step 3: Keep the native ColorPicker reference**

Add to the app project:

```xml
<PackageReference Include="Avalonia.Controls.ColorPicker" />
```

Retain AtomUI until Task 5 so this commit stays buildable.

- [ ] **Step 4: Define the small Cyber Tail palette**

Create `Styles/CyberTail.axaml` with this resource core:

```xml
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
  <Styles.Resources>
    <SolidColorBrush x:Key="CyberSurfaceBrush" Color="#090D12" />
    <SolidColorBrush x:Key="CyberRaisedBrush" Color="#101722" />
    <SolidColorBrush x:Key="CyberToolbarBrush" Color="#0D141E" />
    <SolidColorBrush x:Key="CyberBorderBrush" Color="#263449" />
    <SolidColorBrush x:Key="CyberTextBrush" Color="#E6EDF7" />
    <SolidColorBrush x:Key="CyberMutedBrush" Color="#91A0B5" />
    <SolidColorBrush x:Key="CyberCyanBrush" Color="#28D7FE" />
    <SolidColorBrush x:Key="CyberGreenBrush" Color="#39E58C" />
    <SolidColorBrush x:Key="CyberMagentaBrush" Color="#FF4FD8" />
    <SolidColorBrush x:Key="CyberErrorBrush" Color="#FF667A" />
    <x:Double x:Key="CyberControlHeight">34</x:Double>
  </Styles.Resources>
</Styles>
```

Add only targeted styles for `.command-bar`, `.panel`, `.primary`, `.icon`, `.status-live`, `.error`, and `.log-list`; do not re-template Fluent controls.

- [ ] **Step 5: Request the single dark variant**

In `App.Initialize`, set the single supported variant before loading views:

```csharp
RequestedThemeVariant = ThemeVariant.Dark;
AvaloniaXamlLoader.Load(this);
```

- [ ] **Step 6: Verify the resources and existing suite**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj
rtk dotnet build src/HexTailSharp.slnx
```

Expected: PASS.

- [ ] **Step 7: Commit the foundation**

```bash
rtk git add Directory.Packages.props src/HexTailSharp/HexTailSharp.csproj src/HexTailSharp/App.axaml src/HexTailSharp/App.axaml.cs src/HexTailSharp/Styles
rtk git commit -m "style(ui): add cyber tail fluent foundation"
```

---

## Task 5: Replace the Atom shell with working native controls

**Files:**

- Modify: `Directory.Packages.props`
- Modify: `src/HexTailSharp/HexTailSharp.csproj`
- Modify: `src/HexTailSharp/App.axaml.cs`
- Modify: `src/HexTailSharp/Program.cs`
- Modify: `src/HexTailSharp/MainWindow.axaml`
- Modify: `src/HexTailSharp/MainWindow.axaml.cs`
- Modify: `src/HexTailSharp/ViewModels/MainWindowViewModel.cs`
- Modify: `src/HexTailSharp/ViewModels/SettingsViewModel.cs`
- Modify: `src/HexTailSharp/ViewModels/FileTabViewModel.cs`
- Modify: `src/HexTailSharp.Tests/HexTailSharp.Tests.csproj`
- Create: `src/HexTailSharp.Tests/Support/TestPersistence.cs`
- Create: `src/HexTailSharp.Tests/Support/TestWindow.cs`
- Create: `src/HexTailSharp.Tests/Ui/HeadlessApp.cs`
- Create: `src/HexTailSharp.Tests/Ui/AppThemeTests.cs`
- Create: `src/HexTailSharp.Tests/Ui/MainWindowInteractionTests.cs`

**Interfaces:**

- Consumes: `MainWindowViewModel` commands/properties, native storage provider, persisted settings, `ReactiveCommand<TInput, Unit>`.
- Produces: native `Window`, a right `SplitView` named `SettingsSplitView`, functional `MatchModeBox`, `DensityBox`, and `FontSizeBox`, search creation by Enter/button, settings save/error feedback, and cross-platform keyboard shortcuts.
- Removes: `SettingsPlacement`, `SettingsViewModel.Theme`, `ThemeOptions`, `MenuAlignment`, `MenuAlignmentOptions`, and Atom theme application.

- [ ] **Step 1: Add the headless harness and test helpers**

Add central versions:

```xml
<PackageVersion Include="Avalonia.Headless.XUnit" Version="12.1.1" />
<PackageVersion Include="xunit.v3" Version="3.2.2" />
```

Replace the test project's `xunit` reference with:

```xml
<PackageReference Include="Avalonia.Headless.XUnit" />
<PackageReference Include="xunit.v3" />
```

Create `HeadlessApp.cs`:

```csharp
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ReactiveUI.Avalonia;

[assembly: AvaloniaTestApplication(typeof(HexTailSharp.Tests.Ui.HeadlessApp))]

namespace HexTailSharp.Tests.Ui;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseReactiveUI(_ => { })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}
```

Create `AppThemeTests.cs`:

```csharp
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;

namespace HexTailSharp.Tests.Ui;

public sealed class AppThemeTests
{
    [AvaloniaFact]
    public void AppLoadsCyberTailDarkResources()
    {
        var app = Assert.IsType<App>(Application.Current);
        Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
        Assert.True(app.TryGetResource("CyberCyanBrush", ThemeVariant.Dark, out _));
        Assert.True(app.TryGetResource("CyberSurfaceBrush", ThemeVariant.Dark, out _));
    }
}
```

Create `TestPersistence.cs`:

```csharp
using HexTailSharp.Persistence;

namespace HexTailSharp.Tests.Support;

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
```

Add `TestWindow.cs`:

```csharp
using HexTailSharp.Application;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;
using HexTailSharp.ViewModels;

namespace HexTailSharp.Tests.Support;

internal static class TestWindow
{
    public static MainWindow Create(out MainWindowViewModel viewModel) =>
        Create(new TestPersistence(), out viewModel);

    public static MainWindow Create(
        IAppPersistence persistence,
        out MainWindowViewModel viewModel)
    {
        viewModel = new MainWindowViewModel(new AppState(new TailerService(), persistence));
        return new MainWindow(viewModel);
    }
}
```

- [ ] **Step 2: Write failing headless interaction tests**

Add tests that construct `AppState`, `MainWindowViewModel`, and the internal injected-window constructor. The controls must be exercised, not merely found:

```csharp
[AvaloniaFact]
public void SettingsInspectorAndEveryComboBoxOpen()
{
    using var window = TestWindow.Create(out var viewModel);
    var pane = window.FindControl<SplitView>("SettingsSplitView")!;
    viewModel.SettingsOpen = true;

    Assert.True(pane.IsPaneOpen);
    foreach (var name in new[] { "MatchModeBox", "DensityBox", "FontSizeBox" })
    {
        var combo = window.FindControl<ComboBox>(name)!;
        combo.IsDropDownOpen = true;
        Assert.True(combo.IsDropDownOpen);
        combo.IsDropDownOpen = false;
    }
}

[AvaloniaFact]
public async Task SettingsFailureStaysInsideInspector()
{
    var persistence = new TestPersistence { SaveError = new IOException("disk full") };
    using var window = TestWindow.Create(persistence, out var viewModel);

    await viewModel.Settings.CommitAsync(viewModel.State.Settings with { Density = UiDensity.Compact });

    Assert.True(viewModel.Settings.HasSaveError);
    Assert.Contains("disk full", viewModel.Settings.SaveError);
    Assert.True(window.FindControl<TextBlock>("SettingsSaveError")!.IsVisible);
}
```

The same file must also cover:

```text
- settings open/close and Escape
- label add/edit/remove
- exclusion add/edit/remove
- search by Add button and Enter behavior
- Ctrl/Meta+O, Ctrl/Meta+F, Ctrl/Meta+S
- file-tab selection and close commands
- invalid regex remains visible and preserves Query
- narrow width selects Overlay; wide width selects Inline
```

- [ ] **Step 3: Confirm the native interaction tests fail**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "AppThemeTests|MainWindowInteractionTests"
```

Expected: FAIL because Atom initialization and the native named controls/injected constructor have not been replaced.

- [ ] **Step 4: Replace the bootstrap and window base**

`Program.BuildAvaloniaApp` becomes:

```csharp
public static AppBuilder BuildAvaloniaApp() =>
    AppBuilder.Configure<App>().UsePlatformDetect().UseReactiveUI(_ => { });
```

Remove all Atom initialization from `App.Initialize`; keep only dark variant and XAML loading. Change:

```csharp
public partial class MainWindow : Window
```

Add a testable constructor without introducing a container:

```csharp
public MainWindow(string[]? startupPaths = null)
    : this(new MainWindowViewModel(
        new AppState(new TailerService(), new JsonFileAppPersistence()),
        startupPaths)) { }

internal MainWindow(MainWindowViewModel viewModel)
{
    InitializeComponent();
    ViewModel = viewModel;
    DataContext = viewModel;
    ViewModel.PickFiles.RegisterHandler(context => _ = HandlePickFilesAsync(context));
    AddHandler(DragDrop.DragOverEvent, OnDragOver);
    AddHandler(DragDrop.DropEvent, OnDrop);
    Opened += OnOpened;
    Closed += OnClosed;
    SizeChanged += (_, args) => UpdateResponsiveLayout(args.NewSize.Width);
}
```

- [ ] **Step 5: Build the native workspace shell**

Use native controls and stable names. The root layout is:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="using:HexTailSharp.ViewModels"
        xmlns:views="using:HexTailSharp.Views"
        xmlns:icons="using:FluentIcons.Avalonia"
        xmlns:i="using:Avalonia.Xaml.Interactivity"
        xmlns:core="using:Avalonia.Xaml.Interactions.Core"
        xmlns:events="using:Avalonia.Xaml.Interactions.Events"
        x:Class="HexTailSharp.MainWindow"
        x:DataType="vm:MainWindowViewModel"
        Background="{DynamicResource CyberSurfaceBrush}"
        MinWidth="720" MinHeight="480">
  <SplitView x:Name="SettingsSplitView"
             PanePlacement="Right"
             OpenPaneLength="380"
             IsPaneOpen="{Binding SettingsOpen}"
             DisplayMode="Inline">
    <SplitView.Pane>
      <Grid RowDefinitions="Auto,*" Margin="16">
        <TextBlock Text="Settings" Classes="section-title" />
        <TabControl Grid.Row="1" SelectedIndex="{Binding Settings.SectionIndex}">
          <TabItem Header="Labels">
            <ScrollViewer><ItemsControl ItemsSource="{Binding Settings.Labels}" /></ScrollViewer>
          </TabItem>
          <TabItem Header="Exclusions">
            <ScrollViewer><ItemsControl ItemsSource="{Binding Settings.Exclusions}" /></ScrollViewer>
          </TabItem>
          <TabItem Header="Display">
            <StackPanel Spacing="12">
              <TextBlock Text="Density" />
              <ComboBox x:Name="DensityBox"
                        ItemsSource="{Binding Settings.DensityOptions}"
                        SelectedItem="{Binding Settings.Density}" />
              <TextBlock Text="Log font size" />
              <ComboBox x:Name="FontSizeBox"
                        ItemsSource="{Binding Settings.FontSizeOptions}"
                        SelectedItem="{Binding Settings.FontSize}" />
              <TextBlock x:Name="SettingsSaveError"
                         Classes="error"
                         IsVisible="{Binding Settings.HasSaveError}"
                         Text="{Binding Settings.SaveError}" />
            </StackPanel>
          </TabItem>
        </TabControl>
      </Grid>
    </SplitView.Pane>
    <Grid RowDefinitions="Auto,Auto,*">
      <Border Classes="command-bar">
        <StackPanel Orientation="Horizontal" Spacing="8">
          <Button Command="{Binding OpenCommand}" Content="Open Files" />
          <Button Command="{Binding SaveCommand}" Content="Save Session" />
          <Button Command="{Binding ToggleSettingsCommand}" Content="Settings" />
        </StackPanel>
      </Border>
      <Grid Grid.Row="1" ColumnDefinitions="*,Auto,Auto,Auto" IsVisible="{Binding HasFile}">
        <TextBox x:Name="QueryBox" Text="{Binding Query}" />
        <ComboBox x:Name="MatchModeBox" Grid.Column="1"
                  ItemsSource="{Binding MatchModes}" SelectedItem="{Binding MatchMode}" />
        <CheckBox Grid.Column="2" Content="Case" IsChecked="{Binding CaseSensitive}" />
        <Button Grid.Column="3" Content="Add Search" Command="{Binding AddSearchCommand}" />
      </Grid>
      <ContentControl Grid.Row="2" Content="{Binding SelectedFile}" />
    </Grid>
  </SplitView>
</Window>
```

The concrete settings pane must contain `Labels`, `Exclusions`, and `Display` sections, named `DensityBox` and `FontSizeBox`, editable rows, add/remove buttons, and `SettingsSaveError`. The active search bar must contain named `QueryBox` and `MatchModeBox`, a case toggle, `ColorPicker`, and Add Search.

Use `FluentIcon` only as decorative content inside controls whose accessible label/tool tip remains text:

```xml
<Button Command="{Binding OpenCommand}" ToolTip.Tip="Open log files">
  <StackPanel Orientation="Horizontal" Spacing="8">
    <icons:FluentIcon Icon="FolderOpen" />
    <TextBlock Text="Open Files" />
  </StackPanel>
</Button>
```

- [ ] **Step 6: Use a behavior only for Enter-to-add-search**

Attach the narrow behavior to `QueryBox`:

```xml
<TextBox x:Name="QueryBox" Text="{Binding Query}">
  <i:Interaction.Behaviors>
    <events:KeyDownEventTrigger Key="Enter">
      <core:InvokeCommandAction Command="{Binding AddSearchCommand}" />
    </events:KeyDownEventTrigger>
  </i:Interaction.Behaviors>
</TextBox>
```

Keep ordinary buttons, tabs, check boxes, and combo boxes on native bindings.

- [ ] **Step 7: Make responsive settings and shortcuts deterministic**

In code-behind, change display mode from actual width only:

```csharp
private void UpdateResponsiveLayout(double width) =>
    SettingsSplitView.DisplayMode = width < 960
        ? SplitViewDisplayMode.Overlay
        : SplitViewDisplayMode.Inline;
```

Handle `Control` on Windows/Linux and `Meta` on macOS for O/F/S, focus `QueryBox` for F, and close the inspector on Escape. Do not use platform-name checks; accept either modifier.

- [ ] **Step 8: Give settings persistence its own status**

Remove theme and pane-placement properties from `SettingsViewModel`. Add:

```csharp
private string? _saveError;
private bool _isSaving;

public string? SaveError
{
    get => _saveError;
    private set
    {
        this.RaiseAndSetIfChanged(ref _saveError, value);
        this.RaisePropertyChanged(nameof(HasSaveError));
    }
}

public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);
public bool IsSaving
{
    get => _isSaving;
    private set => this.RaiseAndSetIfChanged(ref _isSaving, value);
}

internal async Task CommitAsync(AppSettings settings)
{
    IsSaving = true;
    SaveError = null;
    try
    {
        await _owner.UpdateSettingsAsync(settings);
    }
    catch (Exception ex)
    {
        SaveError = ex.Message;
    }
    finally
    {
        IsSaving = false;
    }
}
```

`MainWindowViewModel.UpdateSettingsAsync` must let persistence exceptions reach this method. Remove duplicate file-level close commands when the window command with a parameter already exists. Use one explicit merged subscription for every remaining asynchronous command error:

```csharp
_subscriptions.Add(
    Observable.Merge(
        OpenCommand.ThrownExceptions,
        OpenPathsCommand.ThrownExceptions,
        SaveCommand.ThrownExceptions,
        CloseFileCommand.ThrownExceptions,
        AddSearchCommand.ThrownExceptions)
    .Subscribe(ex => SetFileError(ex.Message))
);
```

- [ ] **Step 9: Remove Atom and unused packages**

The final app package set for this task is:

```xml
<PackageReference Include="Avalonia" />
<PackageReference Include="Avalonia.Controls.ColorPicker" />
<PackageReference Include="Avalonia.Desktop" />
<PackageReference Include="Avalonia.Themes.Fluent" />
<PackageReference Include="FluentIcons.Avalonia" />
<PackageReference Include="Polly" />
<PackageReference Include="ReactiveUI.Avalonia.Reactive" />
<PackageReference Include="System.Reactive" />
<PackageReference Include="Xaml.Behaviors.Interactivity" />
<PackageReference Include="Xaml.Behaviors.Interactions" />
<PackageReference Include="Xaml.Behaviors.Interactions.Events" />
```

The behavior package versions in `Directory.Packages.props` are exactly:

```xml
<PackageVersion Include="Xaml.Behaviors.Interactivity" Version="12.0.5" />
<PackageVersion Include="Xaml.Behaviors.Interactions" Version="12.0.5" />
<PackageVersion Include="Xaml.Behaviors.Interactions.Events" Version="12.0.5" />
```

Delete central entries for all AtomUI packages, `Avalonia.Xaml.Interactions.Draggable`, `Material.Icons.Avalonia`, `Xaml.Behaviors.Avalonia`, `Xaml.Behaviors.Interactions.Draggable`, and `Xaml.Behaviors.Interactions.ReactiveUI`.

- [ ] **Step 10: Verify native interactions and Atom removal**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter "AppThemeTests|MainWindowInteractionTests"
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj
rtk dotnet build src/HexTailSharp.slnx
rtk rg -n "AtomUI|AtomUI\.|Material\.Icons|Xaml\.Behaviors\.Avalonia|Interactions\.Draggable|Interactions\.ReactiveUI" Directory.Packages.props src
```

Expected: tests/build PASS; `rg` returns no matches.

- [ ] **Step 11: Commit the native cutover**

```bash
rtk git add Directory.Packages.props src/HexTailSharp src/HexTailSharp.Tests
rtk git commit -m "refactor(ui): replace atom shell with native avalonia"
```

---

## Task 6: Fix duplicate context persistence

**Files:**

- Modify: `src/HexTailSharp.Tests/ViewModels/WorkspaceViewModelTests.cs`
- Modify: `src/HexTailSharp/ViewModels/MainWindowViewModel.cs`

**Interfaces:**

- Consumes: `Task MainWindowViewModel.SetShowContextAsync(FileTabViewModel file, bool value)`.
- Produces: exactly one `AppState.SetShowContextAsync` call and one persistence save per user toggle.

- [ ] **Step 1: Add a failing save-count regression test**

Use `TestPersistence` and the immediate scheduler:

```csharp
[Fact]
public async Task SetShowContext_PersistsOnce()
{
    var persistence = new TestPersistence();
    var state = new AppState(new TailerService(), persistence);
    await using var viewModel = new MainWindowViewModel(
        state,
        scheduler: ImmediateScheduler.Instance);
    var path = Path.GetTempFileName();
    try
    {
        await state.OpenFileAsync(path);
        var file = Assert.Single(viewModel.Files);
        var savesBeforeToggle = persistence.SaveCount;

        await viewModel.SetShowContextAsync(file, true);

        Assert.Equal(savesBeforeToggle + 1, persistence.SaveCount);
    }
    finally
    {
        File.Delete(path);
    }
}
```

- [ ] **Step 2: Confirm the duplicate call fails the test**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter SetShowContext_PersistsOnce
```

Expected: FAIL with two saves after the toggle.

- [ ] **Step 3: Remove the duplicate state call**

`MainWindowViewModel.SetShowContextAsync` must contain one call:

```csharp
await _state.SetShowContextAsync(file.Model, value);
```

- [ ] **Step 4: Verify and commit the bug fix**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter SetShowContext_PersistsOnce
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj
rtk git add src/HexTailSharp.Tests/ViewModels/WorkspaceViewModelTests.cs src/HexTailSharp/ViewModels/MainWindowViewModel.cs
rtk git commit -m "fix(context): persist visibility once"
```

---

## Task 7: Polish the virtualized log view without disturbing streaming

**Files:**

- Modify: `src/HexTailSharp/Views/LogView.axaml`
- Modify: `src/HexTailSharp/Views/LogView.axaml.cs`
- Create: `src/HexTailSharp.Tests/Ui/LogViewTests.cs`

**Interfaces:**

- Consumes: stable `LogViewViewModel.Lines` and `ContextLines`, nullable `FuncDataTemplate<Line>` recycle input, `IsFollowing`, selected line/context.
- Produces: virtualized 100,000-row presentation, resource-driven dark rows, follow disabled after scrolling away, and row selection/double-tap expansion.

- [ ] **Step 1: Add headless regression coverage**

Add a recycling guard test and virtualization assertion:

```csharp
[AvaloniaFact]
public void HundredThousandRowsRemainVirtualized()
{
    var list = new ListBox
    {
        Height = 400,
        ItemsSource = Enumerable.Range(0, 100_000).Select(index => new Line(index.ToString())).ToArray(),
        ItemsPanel = new FuncTemplate<Panel>(() => new VirtualizingStackPanel()),
    };
    using var window = new Window { Width = 900, Height = 500, Content = list };
    window.Show();

    Assert.True(list.GetVisualDescendants().OfType<ListBoxItem>().Count() < 200);
}
```

- [ ] **Step 2: Run the log regression tests**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter LogViewTests
```

Expected: PASS only if the list uses a virtualizing panel; otherwise FAIL with too many realized containers.

- [ ] **Step 3: Keep nullable recycling safe and switch colors to resources**

Preserve both row builder guards exactly:

```csharp
if (_viewModel is null || line is null)
    return new Border();
```

Replace theme-dependent light/dark conversion with two boring helpers:

```csharp
private IBrush ResourceBrush(string key, IBrush fallback) =>
    this.TryFindResource(key, out var value) && value is IBrush brush ? brush : fallback;

private static IBrush ColorBrush(string value) => new SolidColorBrush(Color.Parse(value));
```

Use resource keys for surfaces, borders, and text; use `ColorBrush` only for persisted search/label highlight hex values. Delete `IsLightTheme` and `LightColor`.

- [ ] **Step 4: Preserve native virtualization and streaming behavior**

Keep the XAML list panels explicit:

```xml
<ListBox.ItemsPanel>
  <ItemsPanelTemplate>
    <VirtualizingStackPanel />
  </ItemsPanelTemplate>
</ListBox.ItemsPanel>
```

Keep incremental `CollectionChanged` follow scrolling, the post-template `ScrollViewer` attachment, and the 8-pixel scroll-away threshold. Keep row height stable unless a structured row is expanded. Do not replace `Lines` or `ContextLines`.

- [ ] **Step 5: Verify performance invariants and the full suite**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj --filter LogViewTests
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj
rtk dotnet build src/HexTailSharp.slnx -c Release
```

Expected: PASS with no timing assertion and fewer than 200 realized containers for 100,000 rows.

- [ ] **Step 6: Commit the streaming-safe polish**

```bash
rtk git add src/HexTailSharp/Views/LogView.axaml src/HexTailSharp/Views/LogView.axaml.cs src/HexTailSharp.Tests/Ui/LogViewTests.cs
rtk git commit -m "style(ui): polish virtualized log rows"
```

---

## Task 8: Verify all supported desktop targets and document the boundary

**Files:**

- Create: `.github/workflows/desktop.yml`
- Modify: `docs/native-build.md`
- Modify: `src/HexTailSharp.slnx`

**Interfaces:**

- Consumes: .NET 10 solution, test project, desktop app project.
- Produces: Windows/Linux/macOS build-and-test jobs and Release publish smoke checks for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`.

- [ ] **Step 1: Add the desktop CI matrix**

Create `.github/workflows/desktop.yml`:

```yaml
name: desktop

on:
  pull_request:
  push:
    branches: [main]

jobs:
  verify:
    strategy:
      fail-fast: false
      matrix:
        include:
          - os: ubuntu-latest
            rid: linux-x64
          - os: windows-latest
            rid: win-x64
          - os: macos-latest
            rid: osx-x64
          - os: macos-latest
            rid: osx-arm64
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet restore src/HexTailSharp.slnx
      - run: dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj -c Release --no-restore
      - run: dotnet publish src/HexTailSharp/HexTailSharp.csproj -c Release -r ${{ matrix.rid }} --self-contained false --no-restore
```

- [ ] **Step 2: Document the native-only UI and manual smoke pass**

Update `docs/native-build.md` with these exact operational facts:

```text
- Avalonia Fluent, dark-only Cyber Tail theme
- ReactiveUI.Avalonia.Reactive for System.Reactive-compatible commands/schedulers
- native picker and drop, right responsive settings inspector
- build/test commands and four RID publish commands
- manual smoke: picker, drop, CLI path, tabs, every combo box, search button/Enter,
  regex error, settings add/edit/remove, settings error, follow/scroll-away, truncate,
  rotation, session restore, keyboard shortcuts
- whole-file random access/global search is deliberately the next engine phase
```

Add this plan and its approved spec to the solution's docs folders in `src/HexTailSharp.slnx`.

- [ ] **Step 3: Run final local verification**

```bash
rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj -c Release
rtk dotnet build src/HexTailSharp.slnx -c Release --no-restore
rtk dotnet publish src/HexTailSharp/HexTailSharp.csproj -c Release -r linux-x64 --self-contained false
rtk git diff --check
rtk rg -n "AtomUI|Material\.Icons|ThemeOptions|MenuAlignmentOptions" Directory.Packages.props src
```

Expected: tests/build/publish PASS; diff check clean; no obsolete UI matches.

- [ ] **Step 4: Perform the manual desktop smoke pass**

Run:

```bash
rtk dotnet run --project src/HexTailSharp/HexTailSharp.csproj -- /absolute/path/to/sample.log
```

Verify every item in the documented smoke list on the development OS. CI supplies compiled/headless coverage on the other two operating systems; do not claim native manual coverage that was not performed.

- [ ] **Step 5: Commit CI and documentation**

```bash
rtk git add .github/workflows/desktop.yml docs/native-build.md src/HexTailSharp.slnx
rtk git commit -m "ci(ui): verify cyber tail desktop targets"
```

---

## Completion Gate

- [ ] `rtk dotnet test src/HexTailSharp.Tests/HexTailSharp.Tests.csproj -c Release` passes.
- [ ] `rtk dotnet build src/HexTailSharp.slnx -c Release` passes.
- [ ] Current-platform Release publish passes.
- [ ] AtomUI and unused icon/behavior packages have zero source/package matches.
- [ ] Every visible combo box opens and changes its bound value in headless tests.
- [ ] Settings edits persist or present an inspector-local error.
- [ ] A 100,000-line test source realizes fewer than 200 row containers.
- [ ] File and line collection identities survive ordinary appends and unrelated settings changes.
- [ ] Whole-file random access/search remains explicitly deferred to its own engine plan.

## Deliberately Skipped

- Custom controls: add one only after a native Fluent control fails a concrete interaction or accessibility test.
- Whole-file paging/indexing/global historical search: required, but it changes the data engine and gets its own design/plan after this UI ships.
- Screenshot baselines, installer/signing/update infrastructure, navigation framework, and DI container: none are needed to make this one-window desktop tool polished and functional.

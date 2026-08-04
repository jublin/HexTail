using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTailSharp.Application;
using HexTailSharp.Domain;
using HexTailSharp.Persistence;
using HexTailSharp.Tailing;

namespace HexTailSharp;

public partial class MainWindow : Window
{
    private static readonly MatchMode[] MatchModes = [MatchMode.Literal, MatchMode.Regex];
    private static readonly UiDensity[] Densities = [UiDensity.Comfortable, UiDensity.Cozy, UiDensity.Compact];
    private static readonly LogFontSize[] FontSizes = [LogFontSize.Small, LogFontSize.Medium, LogFontSize.Large, LogFontSize.ExtraLarge];
    private static readonly SettingsMenuAlignment[] MenuAlignments = [SettingsMenuAlignment.Left, SettingsMenuAlignment.Right];

    private readonly string[] _startupPaths;
    private readonly TailerService _tailers = new();
    private readonly AppState _state;
    private readonly DispatcherTimer _tailerTimer;
    private readonly List<ViewEntry> _views = [];
    private bool _started;
    private bool _closed;
    private bool _drainingTailer;
    private bool _updatingUi;
    private int _refreshQueued;
    private int _activeViewIndex;
    private string _settingsSection = "labels";
    private FileTabState? _viewFile;
    private int _viewSearchCount;
    private bool _viewShowContext;

    public MainWindow() : this(null)
    {
    }

    public MainWindow(string[]? startupPaths = null)
    {
        InitializeComponent();
        _startupPaths = startupPaths ?? [];
        _state = new AppState(_tailers, new JsonFileAppPersistence());
        _state.Changed += OnStateChanged;

        ModeBox.ItemsSource = MatchModes;
        ModeBox.SelectedItem = MatchMode.Literal;
        SearchColorPicker.Color = Color.Parse("#F59E0B");
        SettingsSections.SelectedIndex = 0;
        _tailerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _tailerTimer.Tick += DrainTailerEvents;
        _tailerTimer.Start();

        Opened += OnOpened;
        Closed += OnClosed;
    }

    public AppState State => _state;

    private async void OnOpened(object? sender, EventArgs e)
    {
        if (_started)
            return;

        _started = true;
        await _state.RestoreAsync();
        foreach (var path in _startupPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
        {
            try
            {
                await _state.OpenFileAsync(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowFileError($"Could not open {path}: {ex.Message}");
            }
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            ApplyWindowState();
            RefreshSettingsEditor();
            RefreshUi();
        });
    }

    private void ApplyWindowState()
    {
        if (_state.Window.Width > 0)
            Width = _state.Window.Width;
        if (_state.Window.Height > 0)
            Height = _state.Window.Height;
        if (_state.Window.X is int x && _state.Window.Y is int y)
            Position = new PixelPoint(x, y);
    }

    private void OnStateChanged()
    {
        if (_drainingTailer || _closed || Interlocked.Exchange(ref _refreshQueued, 1) != 0)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            RefreshUi();
        }, DispatcherPriority.Background);
    }

    private void DrainTailerEvents(object? sender, EventArgs e)
    {
        _drainingTailer = true;
        var changed = _state.DrainTailerEvents();
        _drainingTailer = false;
        if (changed)
            RefreshVisibleViews(followTail: true);
    }

    private void RefreshUi()
    {
        if (_closed)
            return;

        _updatingUi = true;
        try
        {
            ApplyTheme();
            FileCountText.Text = $"{_state.Files.Count} file(s)";
            RefreshFileTabs();

            var file = _state.SelectedFile;
            var hasFile = file is not null;
            EmptyState.IsVisible = !hasFile;
            FileWorkspace.IsVisible = hasFile;
            if (!hasFile)
            {
                ClearViewTabs();
                return;
            }

            FileErrorBorder.IsVisible = file!.Error is not null;
            FileErrorText.Text = file.Error;
            FollowAllBox.IsChecked = file.FollowAll;
            ShowContextBox.IsChecked = file.ShowContext;
            LineCountText.Text = $"{file.Buffer.Count:N0} lines";
            SearchErrorBorder.IsVisible = false;
            if (ViewStructureChanged(file))
                BuildViewTabs(file);
            else
                RefreshVisibleViews(resetItems: true);
        }
        finally
        {
            _updatingUi = false;
        }
    }

    private void RefreshFileTabs()
    {
        FileTabsPanel.Children.Clear();
        foreach (var file in _state.Files)
        {
            var tab = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var select = new Button
            {
                Content = file.Error is null ? file.DisplayName : $"! {file.DisplayName}",
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = ReferenceEquals(file, _state.SelectedFile) ? Brush("#334155") : Brushes.Transparent,
                Foreground = ReferenceEquals(file, _state.SelectedFile) ? Brush("#F8FAFC") : Brush("#94A3B8"),
                Padding = new Thickness(12, 8),
                Tag = file,
            };
            select.Click += SelectFile;
            tab.Children.Add(select);

            var close = new Button
            {
                Content = "×",
                Background = Brushes.Transparent,
                Foreground = Brush("#94A3B8"),
                Padding = new Thickness(8, 8),
                Tag = file,
            };
            ToolTip.SetTip(close, file.Error ?? "Close file");
            close.Click += CloseFile;
            Grid.SetColumn(close, 1);
            tab.Children.Add(close);

            FileTabsPanel.Children.Add(new Border
            {
                Child = tab,
                BorderBrush = ReferenceEquals(file, _state.SelectedFile) ? Brush("#F59E0B") : Brushes.Transparent,
                BorderThickness = new Thickness(0, 0, 0, 2),
            });
        }
    }

    private void BuildViewTabs(FileTabState file)
    {
        _views.Clear();
        ViewTabs.Items.Clear();
        _viewFile = file;
        _viewSearchCount = file.Searches.Count;
        _viewShowContext = file.ShowContext;

        AddViewTab(file, null, "All");
        for (var index = 0; index < file.Searches.Count; index++)
            AddViewTab(file, file.Searches[index], Truncate(file.Searches[index].Query.Query));

        _activeViewIndex = Math.Clamp(_activeViewIndex, 0, Math.Max(0, ViewTabs.Items.Count - 1));
        ViewTabs.SelectedIndex = _activeViewIndex;
    }

    private void ClearViewTabs()
    {
        _views.Clear();
        ViewTabs.Items.Clear();
        _viewFile = null;
        _viewSearchCount = 0;
        _viewShowContext = false;
    }

    private bool ViewStructureChanged(FileTabState file) =>
        !ReferenceEquals(_viewFile, file) ||
        _viewSearchCount != file.Searches.Count ||
        _viewShowContext != file.ShowContext;

    private void AddViewTab(FileTabState file, Search? search, string header)
    {
        var view = BuildView(file, search);
        _views.Add(view);
        ViewTabs.Items.Add(new TabItem { Header = header, Content = view.Root });
    }

    private ViewEntry BuildView(FileTabState file, Search? search)
    {
        var lineItems = new ObservableCollection<Line>(LinesFor(file, search));
        var list = new ListBox
        {
            Background = Brush("#111827"),
            BorderThickness = new Thickness(0),
            ItemsSource = lineItems,
            ItemTemplate = new FuncDataTemplate<Line>((line, _) => BuildLogRow(file, line), supportsRecycling: false),
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is Line line)
            {
                SelectLine(file, line);
                UpdateContextViews(file);
            }
        };
        WireScrollHandler(file, search, list);

        var body = new Grid { RowDefinitions = new RowDefinitions("*") };
        ListBox? contextList = null;
        TextBlock? contextEmpty = null;
        ObservableCollection<Line>? contextItems = null;
        if (file.ShowContext)
        {
            body.RowDefinitions = new RowDefinitions($"*,4,{Math.Max(120, _state.Window.ContextPaneSize)}");
            var splitter = new GridSplitter
            {
                Background = Brush("#334155"),
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
            };
            splitter.DragCompleted += (_, _) => SaveContextPaneSize(body);
            Grid.SetRow(splitter, 1);
            body.Children.Add(splitter);

            contextItems = new ObservableCollection<Line>(ContextLines(file));
            contextList = new ListBox
            {
                Background = Brush("#172033"),
                BorderThickness = new Thickness(0),
                ItemsSource = contextItems,
                ItemTemplate = new FuncDataTemplate<Line>((line, _) => new TextBlock
                {
                    Text = line.Raw,
                    FontFamily = LogFont,
                    FontSize = ToLogFontSize(_state.Settings.LogFontSize),
                    Foreground = Brush("#CBD5E1"),
                    Padding = new Thickness(12, 4),
                    TextWrapping = TextWrapping.NoWrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                }, supportsRecycling: false),
                ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel()),
            };
            Grid.SetRow(contextList, 2);
            body.Children.Add(contextList);

            contextEmpty = new TextBlock
            {
                Text = "Select a match to see context.",
                Foreground = Brush("#94A3B8"),
                FontFamily = LogFont,
                FontSize = ToLogFontSize(_state.Settings.LogFontSize),
                Margin = new Thickness(12, 8),
                IsVisible = !ContextLines(file).Any(),
            };
            Grid.SetRow(contextEmpty, 2);
            body.Children.Add(contextEmpty);
        }

        Grid.SetRow(list, 0);
        body.Children.Insert(0, list);
        var root = new Grid { RowDefinitions = search is null ? new RowDefinitions("*") : new RowDefinitions("Auto,*") };
        if (search is not null)
        {
            var follow = new CheckBox
            {
                Content = "Follow",
                IsChecked = IsSearchFollow(file, search),
                VerticalAlignment = VerticalAlignment.Center,
            };
            follow.IsCheckedChanged += (_, _) => SetSearchFollow(file, search, follow.IsChecked == true);
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children =
                {
                    follow,
                    new TextBlock
                    {
                        Text = $"{search.Results.Count:N0} matches",
                        Foreground = Brush("#94A3B8"),
                        FontSize = 12,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                },
            };
            root.Children.Add(toolbar);
            Grid.SetRow(body, 1);
        }
        root.Children.Add(body);

        return new ViewEntry(file, search, root, list, contextList, contextEmpty,
            lineItems,
            contextItems);
    }

    private Control BuildLogRow(FileTabState file, Line? line)
    {
        if (line is null)
            return new Border();

        var lineIndex = FindLineIndex(file, line);
        var text = new TextBlock
        {
            FontFamily = LogFont,
            FontSize = ToLogFontSize(_state.Settings.LogFontSize),
            Foreground = Brush("#E2E8F0"),
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        AddHighlightedRuns(text, file, line);

        Control content = text;
        if (line.ParsedFields is { Count: > 0 } && file.ExpandedLine == lineIndex)
        {
            content = new StackPanel
            {
                Spacing = 3,
                Children =
                {
                    text,
                    new TextBlock
                    {
                        Text = string.Join("  ", line.ParsedFields.Select(field => $"{field.Key}={field.Value}")),
                        FontFamily = LogFont,
                        FontSize = ToLogFontSize(_state.Settings.LogFontSize),
                        Foreground = Brush("#CBD5E1"),
                        TextWrapping = TextWrapping.Wrap,
                        Padding = new Thickness(16, 2, 0, 6),
                    },
                },
            };
        }

        var row = new Border
        {
            Child = content,
            Padding = LogRowPadding(),
            BorderBrush = Brush("#263449"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Background = Brushes.Transparent,
            Focusable = true,
        };
        row.Tapped += (_, e) =>
        {
            if (e.Handled)
                return;
            SelectLine(file, line);
            e.Handled = true;
        };
        row.DoubleTapped += (_, e) =>
        {
            ToggleExpanded(file, line);
            e.Handled = true;
        };
        return row;
    }

    private void AddHighlightedRuns(TextBlock target, FileTabState file, Line line)
    {
        var ranges = file.Searches
            .SelectMany(search => search.GetHighlights(line).Select(range => (range.Start, range.Length, search.Color)))
            .Concat(_state.Settings.GetLabelHighlights(line.Raw).Select(range => (range.Start, range.Length, range.Color)))
            .Where(range => range.Start >= 0 && range.Length > 0 && range.Start + range.Length <= line.Raw.Length)
            .OrderBy(range => range.Start)
            .ThenByDescending(range => range.Length)
            .ToList();

        if (ranges.Count == 0)
        {
            target.Text = line.Raw;
            return;
        }

        var inlines = new InlineCollection();
        var cursor = 0;
        foreach (var range in ranges)
        {
            if (range.Start < cursor)
                continue;
            if (range.Start > cursor)
                inlines.Add(new Run { Text = line.Raw[cursor..range.Start] });
            inlines.Add(new Run { Text = line.Raw.Substring(range.Start, range.Length),
                Background = Brush(range.Color),
                Foreground = Brush("#111827"),
            });
            cursor = range.Start + range.Length;
        }

        if (cursor < line.Raw.Length)
            inlines.Add(new Run { Text = line.Raw[cursor..] });
        target.Inlines = inlines;
    }

    private void WireScrollHandler(FileTabState file, Search? search, ListBox list)
    {
        var attached = false;
        void TryAttach()
        {
            if (attached)
                return;

            var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            if (viewer is null)
                return;

            attached = true;
            viewer.ScrollChanged += (_, _) =>
            {
                if (viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y > 8)
                    SetFollow(file, search, false);
            };
        }

        list.TemplateApplied += (_, _) => TryAttach();
        list.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(TryAttach, DispatcherPriority.Background);
        TryAttach();
    }

    private void RefreshVisibleViews(bool followTail = false, bool resetItems = false)
    {
        foreach (var view in _views)
        {
            var selected = view.List.SelectedItem as Line;
            var lines = LinesFor(view.File, view.Search);
            if (resetItems)
            {
                view.Lines = new ObservableCollection<Line>(lines);
                view.List.ItemsSource = view.Lines;
            }
            else
                SyncLines(view.Lines, lines);
            if (selected is not null && view.Lines.Contains(selected))
                view.List.SelectedItem = selected;
            UpdateContext(view, resetItems);
            if (followTail && ShouldFollow(view))
                Dispatcher.UIThread.Post(() =>
                {
                    if (ShouldFollow(view) && view.List.ItemCount > 0)
                        view.List.ScrollIntoView(view.List.ItemCount - 1);
                }, DispatcherPriority.Background);
        }

        if (_state.SelectedFile is { } selectedFile)
            LineCountText.Text = $"{selectedFile.Buffer.Count:N0} lines";
    }

    private void ApplyTheme()
    {
        if (Avalonia.Application.Current is not { } application)
            return;

        application.RequestedThemeVariant = _state.Settings.Theme switch
        {
            "light" => ThemeVariant.Light,
            "system" => ThemeVariant.Default,
            _ => ThemeVariant.Dark,
        };

        var light = _state.Settings.Theme == "light";
        application.Resources["SurfaceBrush"] = new SolidColorBrush(Color.Parse(light ? "#F8FAFC" : "#111827"));
        application.Resources["RaisedSurfaceBrush"] = new SolidColorBrush(Color.Parse(light ? "#F1F5F9" : "#172033"));
        application.Resources["ToolbarBrush"] = new SolidColorBrush(Color.Parse(light ? "#FFFFFF" : "#1F2937"));
        application.Resources["BorderBrush"] = new SolidColorBrush(Color.Parse(light ? "#CBD5E1" : "#334155"));
        application.Resources["TextBrush"] = new SolidColorBrush(Color.Parse(light ? "#1E293B" : "#E2E8F0"));
        application.Resources["MutedTextBrush"] = new SolidColorBrush(Color.Parse(light ? "#475569" : "#94A3B8"));
        application.Resources["ErrorBackgroundBrush"] = new SolidColorBrush(Color.Parse(light ? "#FEE2E2" : "#451A1A"));
        application.Resources["ErrorBorderBrush"] = new SolidColorBrush(Color.Parse(light ? "#B91C1C" : "#EF4444"));
        application.Resources["ErrorTextBrush"] = new SolidColorBrush(Color.Parse(light ? "#991B1B" : "#FECACA"));
    }

    private void UpdateContextViews(FileTabState file)
    {
        foreach (var view in _views.Where(view => ReferenceEquals(view.File, file)))
            UpdateContext(view);
    }

    private void UpdateContext(ViewEntry view, bool resetItems = false)
    {
        if (view.ContextList is null || view.ContextEmpty is null)
            return;

        var lines = ContextLines(view.File);
        if (view.ContextLines is not null)
        {
            if (resetItems)
            {
                view.ContextLines = new ObservableCollection<Line>(lines);
                view.ContextList.ItemsSource = view.ContextLines;
            }
            else
                SyncLines(view.ContextLines, lines);
        }
        view.ContextEmpty.IsVisible = lines.Count == 0;
    }

    private static void SyncLines(ObservableCollection<Line> current, IReadOnlyList<Line> desired)
    {
        if (current.Count == desired.Count &&
            (current.Count == 0 || ReferenceEquals(current[^1], desired[^1])))
            return;

        if (current.Count < desired.Count &&
            (current.Count == 0 || ReferenceEquals(current[^1], desired[current.Count - 1])))
        {
            for (var index = current.Count; index < desired.Count; index++)
                current.Add(desired[index]);
            return;
        }

        var common = 0;
        while (common < current.Count && common < desired.Count && ReferenceEquals(current[common], desired[common]))
            common++;

        if (common < current.Count / 2)
        {
            current.Clear();
            common = 0;
        }
        else
        {
            while (current.Count > common)
                current.RemoveAt(current.Count - 1);
        }

        for (var index = common; index < desired.Count; index++)
            current.Add(desired[index]);
    }

    private IReadOnlyList<Line> LinesFor(FileTabState file, Search? search)
    {
        IEnumerable<Line> lines = search is null
            ? file.Buffer.Lines
            : search.Results.Where(index => index >= 0 && index < file.Buffer.Count).Select(index => file.Buffer[index]);
        return _state.Settings.GlobalExcludeLabels.Count == 0
            ? lines as IReadOnlyList<Line> ?? lines.ToList()
            : lines.Where(line => !_state.Settings.Excludes(line.Raw)).ToList();
    }

    private IReadOnlyList<Line> ContextLines(FileTabState file) =>
        file.ContextLines.Where(line => !_state.Settings.Excludes(line.Raw)).ToList();

    private void RefreshSettingsEditor()
    {
        SettingsEditor.Children.Clear();
        switch (_settingsSection)
        {
            case "labels":
                BuildLabelsEditor();
                break;
            case "exclusions":
                BuildExclusionsEditor();
                break;
            case "theme":
                BuildChoiceEditor("Theme", ThemeCatalog.Names, _state.Settings.Theme,
                    value => UpdateSettings(_state.Settings with { Theme = (string)value }));
                break;
            case "density":
                BuildChoiceEditor("UI spacing", Densities, _state.Settings.Density,
                    value => UpdateSettings(_state.Settings with { Density = (UiDensity)value }));
                break;
            case "font-size":
                BuildChoiceEditor("Log font size", FontSizes, _state.Settings.LogFontSize,
                    value => UpdateSettings(_state.Settings with { LogFontSize = (LogFontSize)value }));
                break;
            case "menu-alignment":
                BuildChoiceEditor("Menu alignment", MenuAlignments, _state.Settings.SettingsMenuAlignment,
                    value => UpdateSettings(_state.Settings with { SettingsMenuAlignment = (SettingsMenuAlignment)value }));
                break;
        }
    }

    private void BuildLabelsEditor()
    {
        SettingsEditor.Children.Add(HelpText("Highlight case-insensitive text across every log view."));
        for (var index = 0; index < _state.Settings.GlobalLabels.Count; index++)
        {
            var labelIndex = index;
            var label = _state.Settings.GlobalLabels[index];
            var text = new TextBox { Text = label.Text, PlaceholderText = "Text to highlight", HorizontalAlignment = HorizontalAlignment.Stretch };
            var color = ColorPicker(label.Color);
            text.LostFocus += (_, _) =>
            {
                var labels = _state.Settings.GlobalLabels.Select((item, current) => current == labelIndex
                    ? new GlobalLabel { Text = text.Text ?? string.Empty, Color = ColorToHex(color.Color) }
                    : item).ToList();
                _ = UpdateSettings(_state.Settings with { GlobalLabels = labels }, refreshEditor: true);
            };
            color.ColorChanged += (_, args) =>
            {
                var labels = _state.Settings.GlobalLabels.Select((item, current) => current == labelIndex
                    ? new GlobalLabel { Text = item.Text, Color = ColorToHex(args.NewColor) }
                    : item).ToList();
                _ = UpdateSettings(_state.Settings with { GlobalLabels = labels });
            };
            var remove = new Button { Content = "×" };
            ToolTip.SetTip(remove, "Remove label");
            remove.Click += (_, _) => _ = UpdateSettings(_state.Settings with
            {
                GlobalLabels = _state.Settings.GlobalLabels.Where((_, current) => current != labelIndex).ToList(),
            }, refreshEditor: true);
            SettingsEditor.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                Children = { text, color, remove },
            });
            Grid.SetColumn(color, 1);
            Grid.SetColumn(remove, 2);
        }

        var newText = new TextBox { PlaceholderText = "Text to highlight" };
        var newColor = ColorPicker("#F59E0B");
        var add = new Button { Content = "+" };
        ToolTip.SetTip(add, "Add label");
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(newText.Text))
                return;
            _ = UpdateSettings(_state.Settings with
            {
                GlobalLabels = [.. _state.Settings.GlobalLabels, new GlobalLabel { Text = newText.Text, Color = ColorToHex(newColor.Color) }],
            }, refreshEditor: true);
        };
        SettingsEditor.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
            Children = { newText, newColor, add },
        });
        Grid.SetColumn(newColor, 1);
        Grid.SetColumn(add, 2);
    }

    private void BuildExclusionsEditor()
    {
        SettingsEditor.Children.Add(HelpText("Matching lines stay buffered but are hidden from log and context views."));
        for (var index = 0; index < _state.Settings.GlobalExcludeLabels.Count; index++)
        {
            var exclusionIndex = index;
            var text = new TextBox { Text = _state.Settings.GlobalExcludeLabels[index], PlaceholderText = "Text to hide" };
            text.LostFocus += (_, _) =>
            {
                var values = _state.Settings.GlobalExcludeLabels.Select((item, current) => current == exclusionIndex ? text.Text ?? string.Empty : item).ToList();
                _ = UpdateSettings(_state.Settings with { GlobalExcludeLabels = values }, refreshEditor: true);
            };
            var remove = new Button { Content = "×" };
            ToolTip.SetTip(remove, "Remove exclusion");
            remove.Click += (_, _) => _ = UpdateSettings(_state.Settings with
            {
                GlobalExcludeLabels = _state.Settings.GlobalExcludeLabels.Where((_, current) => current != exclusionIndex).ToList(),
            }, refreshEditor: true);
            SettingsEditor.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Children = { text, remove },
            });
            Grid.SetColumn(remove, 1);
        }

        var newText = new TextBox { PlaceholderText = "Text to hide" };
        var add = new Button { Content = "+" };
        ToolTip.SetTip(add, "Add exclusion");
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(newText.Text))
                return;
            _ = UpdateSettings(_state.Settings with
            {
                GlobalExcludeLabels = [.. _state.Settings.GlobalExcludeLabels, newText.Text],
            }, refreshEditor: true);
        };
        SettingsEditor.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { newText, add },
        });
        Grid.SetColumn(add, 1);
    }

    private void BuildChoiceEditor<T>(string label, IEnumerable<T> values, T selected, Func<object, Task> changed)
    {
        SettingsEditor.Children.Add(new TextBlock { Text = label, Foreground = Brush("#CBD5E1") });
        var combo = new ComboBox { ItemsSource = values.ToList(), SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectionChanged += (_, _) =>
        {
            if (!_updatingUi && combo.SelectedItem is not null)
                _ = changed(combo.SelectedItem);
        };
        SettingsEditor.Children.Add(combo);
    }

    private async Task UpdateSettings(AppSettings settings, bool refreshEditor = false)
    {
        await _state.UpdateSettingsAsync(settings);
        if (refreshEditor && !_closed)
            await Dispatcher.UIThread.InvokeAsync(RefreshSettingsEditor);
    }

    private void SettingsSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SettingsSections.SelectedItem is ListBoxItem item && item.Tag is string section)
        {
            if (string.Equals(_settingsSection, section, StringComparison.Ordinal) && SettingsEditor.Children.Count > 0)
                return;
            _settingsSection = section;
            RefreshSettingsEditor();
        }
    }

    private void ToggleSettings(object? sender, RoutedEventArgs e) => SettingsSplitView.IsPaneOpen = !SettingsSplitView.IsPaneOpen;

    private void SelectFile(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileTabState file })
        {
            _state.SelectFile(file);
            _activeViewIndex = 0;
        }
    }

    private async void CloseFile(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: FileTabState file })
            await _state.CloseFileAsync(file);
    }

    private void ViewTabChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_updatingUi && ViewTabs.SelectedIndex >= 0)
            _activeViewIndex = ViewTabs.SelectedIndex;
    }

    private async void AddSearch(object? sender, RoutedEventArgs e)
    {
        if (_state.SelectedFile is not { } file || string.IsNullOrWhiteSpace(QueryBox.Text))
            return;

        try
        {
            _state.AddSearch(file, QueryBox.Text, ModeBox.SelectedItem is MatchMode mode ? mode : MatchMode.Literal,
                CaseSensitiveBox.IsChecked == true, ColorToHex(SearchColorPicker.Color));
            QueryBox.Text = string.Empty;
            SearchErrorBorder.IsVisible = false;
            await _state.SaveAsync();
        }
        catch (ArgumentException ex)
        {
            SearchErrorText.Text = ex.Message;
            SearchErrorBorder.IsVisible = true;
        }
    }

    private void QueryKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            AddSearch(sender, new RoutedEventArgs());
    }

    private async void SaveSession(object? sender, RoutedEventArgs e) => await _state.SaveAsync();

    private async void FollowAllChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingUi || _state.SelectedFile is not { } file)
            return;
        file.FollowAll = FollowAllBox.IsChecked == true;
        await _state.SaveAsync();
    }

    private async void ShowContextChanged(object? sender, RoutedEventArgs e)
    {
        if (_updatingUi || _state.SelectedFile is not { } file)
            return;
        file.ShowContext = ShowContextBox.IsChecked == true;
        await _state.SaveAsync();
        RefreshUi();
    }

    private void SetSearchFollow(FileTabState file, Search search, bool value)
    {
        var index = file.Searches.IndexOf(search);
        if (index < 0 || index >= file.FollowSearches.Count)
            return;
        file.FollowSearches[index] = value;
        _ = _state.SaveAsync();
    }

    private void SetFollow(FileTabState file, Search? search, bool value)
    {
        var changed = false;
        if (search is null)
        {
            changed = file.FollowAll != value;
            file.FollowAll = value;
            if (changed && ReferenceEquals(file, _state.SelectedFile))
            {
                _updatingUi = true;
                FollowAllBox.IsChecked = value;
                _updatingUi = false;
            }
        }
        else
        {
            var index = file.Searches.IndexOf(search);
            if (index >= 0 && index < file.FollowSearches.Count)
            {
                changed = file.FollowSearches[index] != value;
                file.FollowSearches[index] = value;
            }
        }

        if (changed)
            _ = _state.SaveAsync();
    }

    private static bool IsSearchFollow(FileTabState file, Search search)
    {
        var index = file.Searches.IndexOf(search);
        return index >= 0 && index < file.FollowSearches.Count && file.FollowSearches[index];
    }

    private static bool ShouldFollow(ViewEntry view) =>
        view.Search is null
            ? view.File.FollowAll
            : IsSearchFollow(view.File, view.Search);

    private async void PickFiles(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = true,
                Title = "Open log files",
            });
            await OpenStorageFilesAsync(files);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException)
        {
            ShowFileError($"Could not pick files: {ex.Message}");
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Formats.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is not { } files)
            return;
        await OpenStorageFilesAsync(files);
    }

    private async Task OpenStorageFilesAsync(IEnumerable<IStorageItem> files)
    {
        foreach (var file in files)
        {
            try
            {
                if (file.Path.IsFile)
                    await _state.OpenFileAsync(file.Path.LocalPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                ShowFileError($"Could not open {file.Name}: {ex.Message}");
            }
            finally
            {
                file.Dispose();
            }
        }
    }

    private void SaveContextPaneSize(Grid body)
    {
        if (body.RowDefinitions.Count < 3)
            return;

        var size = Math.Max(120, (int)Math.Round(body.RowDefinitions[2].ActualHeight));
        _state.SetWindowState(new AppWindowState
        {
            Width = _state.Window.Width,
            Height = _state.Window.Height,
            X = _state.Window.X,
            Y = _state.Window.Y,
            ContextPaneSize = size,
            VerticalFileTabs = _state.Window.VerticalFileTabs,
        });
        _ = _state.SaveAsync();
    }

    private void ShowFileError(string message)
    {
        FileErrorText.Text = message;
        FileErrorBorder.IsVisible = true;
    }

    private void SelectLine(FileTabState file, Line line) => file.SelectedLine = FindLineIndex(file, line);

    private void ToggleExpanded(FileTabState file, Line line)
    {
        var index = FindLineIndex(file, line);
        file.ExpandedLine = file.ExpandedLine == index ? null : index;
        RefreshVisibleViews(resetItems: true);
    }

    private static int FindLineIndex(FileTabState file, Line line)
    {
        for (var index = 0; index < file.Buffer.Count; index++)
            if (ReferenceEquals(file.Buffer[index], line))
                return index;
        return -1;
    }

    private static string Truncate(string value) => value.Length > 24 ? $"{value[..21]}..." : value;

    private static string ColorToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private ColorPicker ColorPicker(string value) => new()
    {
        Color = Color.Parse(value),
        IsAlphaEnabled = false,
        Width = 46,
        Height = 36,
    };

    private TextBlock HelpText(string text) => new()
    {
        Text = text,
        Foreground = Brush("#94A3B8"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    private static FontFamily LogFont => new("Cascadia Mono,Consolas,Menlo,monospace");

    private Thickness LogRowPadding() => _state.Settings.Density switch
    {
        UiDensity.Compact => new Thickness(12, 1),
        UiDensity.Cozy => new Thickness(12, 3),
        _ => new Thickness(12, 4),
    };

    private static double ToLogFontSize(LogFontSize size) => size switch
    {
        LogFontSize.Small => 12,
        LogFontSize.Large => 15,
        LogFontSize.ExtraLarge => 17,
        _ => 13,
    };

    private IBrush Brush(string value) => new SolidColorBrush(Color.Parse(_state.Settings.Theme == "light" ? LightColor(value) : value));

    private static string LightColor(string value) => value switch
    {
        "#111827" => "#F8FAFC",
        "#172033" => "#F1F5F9",
        "#1F2937" => "#FFFFFF",
        "#334155" => "#CBD5E1",
        "#263449" => "#E2E8F0",
        "#94A3B8" => "#475569",
        "#F8FAFC" => "#0F172A",
        "#E2E8F0" => "#1E293B",
        "#CBD5E1" => "#334155",
        "#451A1A" => "#FEE2E2",
        "#EF4444" => "#B91C1C",
        "#FECACA" => "#991B1B",
        _ => value,
    };

    private async void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
            return;
        _closed = true;
        _tailerTimer.Stop();
        _state.SetWindowState(new AppWindowState
        {
            Width = Width,
            Height = Height,
            X = Position.X,
            Y = Position.Y,
            ContextPaneSize = _state.Window.ContextPaneSize,
            VerticalFileTabs = _state.Window.VerticalFileTabs,
        });
        await _state.DisposeAsync();
    }

    private sealed class ViewEntry(
        FileTabState file,
        Search? search,
        Grid root,
        ListBox list,
        ListBox? contextList,
        TextBlock? contextEmpty,
        ObservableCollection<Line> lines,
        ObservableCollection<Line>? contextLines)
    {
        public FileTabState File { get; } = file;
        public Search? Search { get; } = search;
        public Grid Root { get; } = root;
        public ListBox List { get; } = list;
        public ListBox? ContextList { get; } = contextList;
        public TextBlock? ContextEmpty { get; } = contextEmpty;
        public ObservableCollection<Line> Lines { get; set; } = lines;
        public ObservableCollection<Line>? ContextLines { get; set; } = contextLines;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
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
    private int _activeViewIndex;
    private string _settingsSection = "labels";

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
        ApplyWindowState();
        foreach (var path in _startupPaths.Where(path => !string.IsNullOrWhiteSpace(path)))
            await _state.OpenFileAsync(path);
        RefreshUi();
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
        if (_drainingTailer)
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RefreshUi();
        else
            Dispatcher.UIThread.Post(RefreshUi);
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
            FileCountText.Text = $"{_state.Files.Count} file(s)";
            RefreshFileTabs();
            RefreshSettingsEditor();

            var file = _state.SelectedFile;
            var hasFile = file is not null;
            EmptyState.IsVisible = !hasFile;
            FileWorkspace.IsVisible = hasFile;
            if (!hasFile)
                return;

            FileErrorBorder.IsVisible = file!.Error is not null;
            FileErrorText.Text = file.Error;
            FollowAllBox.IsChecked = file.FollowAll;
            ShowContextBox.IsChecked = file.ShowContext;
            LineCountText.Text = $"{file.Buffer.Count:N0} lines";
            SearchErrorBorder.IsVisible = false;
            BuildViewTabs(file);
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

        AddViewTab(file, null, "All");
        for (var index = 0; index < file.Searches.Count; index++)
            AddViewTab(file, file.Searches[index], Truncate(file.Searches[index].Query.Query));

        _activeViewIndex = Math.Clamp(_activeViewIndex, 0, Math.Max(0, ViewTabs.Items.Count - 1));
        ViewTabs.SelectedIndex = _activeViewIndex;
    }

    private void AddViewTab(FileTabState file, Search? search, string header)
    {
        var view = BuildView(file, search);
        _views.Add(view);
        ViewTabs.Items.Add(new TabItem { Header = header, Content = view.Root });
    }

    private ViewEntry BuildView(FileTabState file, Search? search)
    {
        var list = new ListBox
        {
            Background = Brush("#111827"),
            BorderThickness = new Thickness(0),
            ItemsSource = LinesFor(file, search),
            ItemTemplate = new FuncDataTemplate<Line>((line, _) => BuildLogRow(file, line), supportsRecycling: false),
            ItemsPanel = new FuncTemplate<Panel>(() => new VirtualizingStackPanel()),
        };
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is Line line)
            {
                SelectLine(file, line);
                UpdateContextViews(file);
            }
        };
        list.AttachedToVisualTree += (_, _) => AttachScrollHandler(file, search, list);

        var body = new Grid { RowDefinitions = new RowDefinitions("*") };
        ListBox? contextList = null;
        TextBlock? contextEmpty = null;
        if (file.ShowContext)
        {
            body.RowDefinitions = new RowDefinitions("*,4,180");
            var splitter = new GridSplitter
            {
                Background = Brush("#334155"),
                ResizeDirection = GridResizeDirection.Rows,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
            };
            Grid.SetRow(splitter, 1);
            body.Children.Add(splitter);

            contextList = new ListBox
            {
                Background = Brush("#172033"),
                BorderThickness = new Thickness(0),
                ItemsSource = ContextLines(file),
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
                ItemsPanel = new FuncTemplate<Panel>(() => new VirtualizingStackPanel()),
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

        return new ViewEntry(file, search, root, list, contextList, contextEmpty);
    }

    private Control BuildLogRow(FileTabState file, Line line)
    {
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
            Padding = new Thickness(12, 4),
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
            target.Inlines!.Add(new Run { Text = line.Raw });
            return;
        }

        var cursor = 0;
        foreach (var range in ranges)
        {
            if (range.Start < cursor)
                continue;
            if (range.Start > cursor)
                target.Inlines!.Add(new Run { Text = line.Raw[cursor..range.Start] });
            target.Inlines!.Add(new Run { Text = line.Raw.Substring(range.Start, range.Length),
                Background = Brush(range.Color),
                Foreground = Brush("#111827"),
            });
            cursor = range.Start + range.Length;
        }

        if (cursor < line.Raw.Length)
            target.Inlines!.Add(new Run { Text = line.Raw[cursor..] });
    }

    private void AttachScrollHandler(FileTabState file, Search? search, ListBox list)
    {
        var viewer = list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
        if (viewer is null)
            return;

        viewer.ScrollChanged += (_, _) =>
        {
            if (viewer.Extent.Height - viewer.Viewport.Height - viewer.Offset.Y > 8)
                SetFollow(file, search, false);
        };
    }

    private void RefreshVisibleViews(bool followTail = false)
    {
        foreach (var view in _views)
        {
            var selected = view.List.SelectedItem as Line;
            view.List.ItemsSource = LinesFor(view.File, view.Search);
            if (selected is not null && view.List.ItemsSource is IEnumerable<Line> lines && lines.Contains(selected))
                view.List.SelectedItem = selected;
            UpdateContext(view);
            if (followTail && ShouldFollow(view) && view.List.ItemCount > 0)
                Dispatcher.UIThread.Post(() => view.List.ScrollIntoView(view.List.ItemCount - 1), DispatcherPriority.Background);
        }

        if (_state.SelectedFile is { } selectedFile)
            LineCountText.Text = $"{selectedFile.Buffer.Count:N0} lines";
    }

    private void UpdateContextViews(FileTabState file)
    {
        foreach (var view in _views.Where(view => ReferenceEquals(view.File, file)))
            UpdateContext(view);
    }

    private void UpdateContext(ViewEntry view)
    {
        if (view.ContextList is null || view.ContextEmpty is null)
            return;

        var lines = ContextLines(view.File).ToList();
        view.ContextList.ItemsSource = lines;
        view.ContextEmpty.IsVisible = lines.Count == 0;
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
            text.TextChanged += (_, _) =>
            {
                var labels = _state.Settings.GlobalLabels.Select((item, current) => current == labelIndex
                    ? new GlobalLabel { Text = text.Text ?? string.Empty, Color = ColorToHex(color.Color) }
                    : item).ToList();
                _ = UpdateSettings(_state.Settings with { GlobalLabels = labels });
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
            });
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
            });
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
            text.TextChanged += (_, _) =>
            {
                var values = _state.Settings.GlobalExcludeLabels.Select((item, current) => current == exclusionIndex ? text.Text ?? string.Empty : item).ToList();
                _ = UpdateSettings(_state.Settings with { GlobalExcludeLabels = values });
            };
            var remove = new Button { Content = "×" };
            ToolTip.SetTip(remove, "Remove exclusion");
            remove.Click += (_, _) => _ = UpdateSettings(_state.Settings with
            {
                GlobalExcludeLabels = _state.Settings.GlobalExcludeLabels.Where((_, current) => current != exclusionIndex).ToList(),
            });
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
            });
        };
        SettingsEditor.Children.Add(new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { newText, add },
        });
        Grid.SetColumn(add, 1);
    }

    private void BuildChoiceEditor<T>(string label, IEnumerable<T> values, T selected, Action<object> changed)
    {
        SettingsEditor.Children.Add(new TextBlock { Text = label, Foreground = Brush("#CBD5E1") });
        var combo = new ComboBox { ItemsSource = values.ToList(), SelectedItem = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
        combo.SelectionChanged += (_, _) =>
        {
            if (!_updatingUi && combo.SelectedItem is not null)
                changed(combo.SelectedItem);
        };
        SettingsEditor.Children.Add(combo);
    }

    private async Task UpdateSettings(AppSettings settings) => await _state.UpdateSettingsAsync(settings);

    private void SettingsSectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SettingsSections.SelectedItem is ListBoxItem item && item.Tag is string section)
        {
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
        if (search is null)
            file.FollowAll = value;
        else
        {
            var index = file.Searches.IndexOf(search);
            if (index >= 0 && index < file.FollowSearches.Count)
                file.FollowSearches[index] = value;
        }
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
            foreach (var file in files)
            {
                if (file.Path.IsFile)
                    await _state.OpenFileAsync(file.Path.LocalPath);
                file.Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
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
        foreach (var file in files)
        {
            if (file.Path.IsFile)
                await _state.OpenFileAsync(file.Path.LocalPath);
            file.Dispose();
        }
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
        RefreshVisibleViews();
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

    private static ColorPicker ColorPicker(string value) => new()
    {
        Color = Color.Parse(value),
        IsAlphaEnabled = false,
        Width = 46,
        Height = 36,
    };

    private static TextBlock HelpText(string text) => new()
    {
        Text = text,
        Foreground = Brush("#94A3B8"),
        FontSize = 12,
        TextWrapping = TextWrapping.Wrap,
    };

    private static FontFamily LogFont => new("Cascadia Mono,Consolas,Menlo,monospace");

    private static double ToLogFontSize(LogFontSize size) => size switch
    {
        LogFontSize.Small => 12,
        LogFontSize.Large => 15,
        LogFontSize.ExtraLarge => 17,
        _ => 13,
    };

    private static IBrush Brush(string value) => new SolidColorBrush(Color.Parse(value));

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
        TextBlock? contextEmpty)
    {
        public FileTabState File { get; } = file;
        public Search? Search { get; } = search;
        public Grid Root { get; } = root;
        public ListBox List { get; } = list;
        public ListBox? ContextList { get; } = contextList;
        public TextBlock? ContextEmpty { get; } = contextEmpty;
    }
}

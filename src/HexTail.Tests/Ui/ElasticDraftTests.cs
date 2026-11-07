using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using DialogHostAvalonia;
using HexTail.Application;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class ElasticDraftTests
{
    [AvaloniaFact]
    public async Task NestedDraftEditsAndCredentialsUpdateTheCardState()
    {
        var persistence = new TestPersistence();
        await persistence.SaveAsync(new AppConfig { Settings = Settings() });
        var state = new AppState(
            new LogSourceService(),
            persistence,
            Settings(),
            elastic: new FakeElasticApiClient(),
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(state, startPolling: false);
        await owner.InitializeAsync();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        var view = Assert.Single(editor.Views);
        var output = Assert.Single(view.Fields);
        var changes = 0;
        editor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == "SaveStatus")
                changes++;
        };
        (Action Edit, Action Undo)[] edits =
        [
            (() => editor.Username = "operator", () => editor.Username = ""),
            (() => editor.Secret = "test-secret", () => editor.Secret = ""),
            (() => view.NamespaceField = "service.name", () => view.NamespaceField = null),
            (() => view.FilterValue = "worker", () => view.FilterValue = "api"),
            (() => output.IsOutput = false, () => output.IsOutput = true),
        ];
        foreach (var (edit, undo) in edits)
        {
            var before = changes;
            edit();
            Assert.True(editor.IsDirty);
            Assert.Equal("Unsaved", editor.SaveStatus);
            Assert.True(changes > before);
            undo();
            Assert.False(editor.IsDirty);
        }
    }

    [AvaloniaFact]
    public async Task SaveAcknowledgesProgressAndFailurePreservesTheDraft()
    {
        var persistence = new DelayedPersistence();
        var state = new AppState(
            new LogSourceService(),
            persistence,
            Settings(),
            elastic: new FakeElasticApiClient(),
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(state, startPolling: false);
        await owner.InitializeAsync();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        Assert.False(editor.IsDirty);
        Assert.Equal("Saved", editor.SaveStatus);
        editor.Name = "Updated Ops";
        Assert.True(editor.IsDirty);
        Assert.Equal("Unsaved", editor.SaveStatus);

        persistence.Pending = new TaskCompletionSource();
        var saving = editor.SaveConnectionOnlyAsync();
        Assert.True(editor.IsSaving);
        Assert.Equal("Saving…", editor.SaveStatus);
        persistence.Pending.SetResult();
        await saving;
        Dispatcher.UIThread.RunJobs();
        Assert.False(editor.IsSaving);
        Assert.False(editor.IsDirty);
        Assert.Equal("Saved", editor.SaveStatus);
        Assert.Equal(
            "Updated Ops",
            Assert.Single(persistence.Config!.Settings.ElasticConnections).Name
        );

        editor.Name = "Submitted name";
        persistence.Pending = new TaskCompletionSource();
        saving = editor.SaveConnectionOnlyAsync();
        editor.Name = "Newer draft";
        persistence.Pending.SetResult();
        await saving;
        Assert.True(editor.IsDirty);
        Assert.Equal("Newer draft", editor.Name);
        Assert.Equal("Submitted name", Assert.Single(state.Settings.ElasticConnections).Name);

        persistence.Pending = null;
        try
        {
            persistence.SaveError = new InvalidOperationException("storage unavailable");
            editor.Name = "Draft kept";
            await editor.SaveConnectionOnlyAsync();
            Assert.True(editor.IsDirty);
            Assert.Equal("Save failed", editor.SaveStatus);
            Assert.Equal("Draft kept", editor.Name);
            Assert.Equal("Submitted name", Assert.Single(state.Settings.ElasticConnections).Name);
        }
        finally
        {
            persistence.SaveError = null;
        }
    }

    [AvaloniaTheory]
    [InlineData("escape")]
    [InlineData("backdrop")]
    [InlineData("button")]
    [InlineData("binding")]
    public async Task EverySettingsClosePathClosesWithoutAskingEvenWithAnEditedConnection(
        string path
    )
    {
        var persistence = new TestPersistence();
        await persistence.SaveAsync(new AppConfig { Settings = Settings() });
        var state = new AppState(
            new LogSourceService(),
            persistence,
            Settings(),
            elastic: new FakeElasticApiClient(),
            credentials: new InMemoryCredentialVault()
        );
        await using var owner = new MainWindowViewModel(state, startPolling: false);
        await owner.InitializeAsync();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.Name = "Unsaved change";
        owner.SettingsOpen = true;
        var window = new MainWindow(owner, registerNativePicker: false);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.True(window.FindControl<DialogHost>("SettingsDialogHost")!.IsOpen);
        if (path == "escape")
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.None, null);
        else if (path == "backdrop")
        {
            window.MouseDown(new Point(8, 8), MouseButton.Left, RawInputModifiers.None);
            window.MouseUp(new Point(8, 8), MouseButton.Left, RawInputModifiers.None);
        }
        else if (path == "button")
            await owner.ToggleSettingsCommand.Execute().FirstAsync();
        else
            owner.SettingsOpen = false;
        Dispatcher.UIThread.RunJobs();

        Assert.False(owner.SettingsOpen);
        Assert.False(window.FindControl<DialogHost>("SettingsDialogHost")!.IsOpen);
        // Nothing about Settings saves or discards the connection's edits.
        Assert.Equal("Unsaved change", editor.Name);
        Assert.Equal("Ops", Assert.Single(state.Settings.ElasticConnections).Name);
        await owner.DisposeAsync();
        window.Close();
    }

    private static AppSettings Settings() =>
        new()
        {
            ElasticConnections =
            [
                new ElasticConnectionSettings
                {
                    Id = "connection",
                    Name = "Ops",
                    KibanaUrl = "https://kibana/",
                    ElasticsearchUrl = "https://elastic/",
                    Views =
                    [
                        new ElasticViewSettings
                        {
                            Id = "view",
                            Name = "Logs",
                            DataViewId = "logs",
                            DataViewTitle = "logs-*",
                            TimeFieldName = "@timestamp",
                            ServerField = "server",
                            OutputFields = ["message"],
                            Sources =
                            [
                                new ElasticSourceSettings { Id = "source", ServerValue = "api" },
                            ],
                        },
                    ],
                },
            ],
        };

    private sealed class DelayedPersistence : IAppPersistence
    {
        public AppConfig? Config { get; private set; } = new() { Settings = Settings() };
        public TaskCompletionSource? Pending { get; set; }
        public Exception? SaveError { get; set; }

        public ValueTask<AppConfig?> LoadAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Config);

        public async ValueTask SaveAsync(
            AppConfig config,
            CancellationToken cancellationToken = default
        )
        {
            if (Pending is not null)
                await Pending.Task;
            if (SaveError is not null)
                throw SaveError;
            Config = config;
        }
    }
}

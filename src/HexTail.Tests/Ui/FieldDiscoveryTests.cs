using System.Reactive.Concurrency;
using System.Reactive.Linq;
using Avalonia.Headless.XUnit;
using HexTail.Application;
using HexTail.Elastic;
using HexTail.Persistence;
using HexTail.Tailing;
using HexTail.Tests.Support;
using HexTail.ViewModels;

namespace HexTail.Tests.Ui;

public sealed class FieldDiscoveryTests
{
    [Fact]
    public void BlankQueryShowsBoundedCandidatesAndEverySelectedField()
    {
        var fields = Enumerable
            .Range(0, 100)
            .Select(index => new ElasticFieldOptionViewModel($"field.{index:000}")
            {
                IsOutput = index == 99,
            })
            .Select(field => (field, field.Name, field.IsOutput))
            .ToArray();

        var visible = ElasticViewEditorViewModel.FilterFields(fields, string.Empty);

        Assert.Equal(51, visible.Count);
        Assert.Contains(fields[0].field, visible);
        Assert.Contains(fields[99].field, visible);
        Assert.DoesNotContain(fields[75].field, visible);
        Assert.Equal(
            fields[75].field,
            Assert.Single(ElasticViewEditorViewModel.FilterFields(fields, "field.075"))
        );
    }

    [AvaloniaFact]
    public async Task MetadataRefreshPreservesSavedCompositionAndNewSelectionOrder()
    {
        var client = new FakeElasticApiClient
        {
            DataViewHandler = id =>
                Task.FromResult(
                    new ElasticDataView(
                        id,
                        "logs-*",
                        "@timestamp",
                        [
                            new("alpha", "keyword", true),
                            new("beta", "text", false),
                            new("zeta", "long", true),
                        ]
                    )
                ),
        };
        var state = new AppState(new LogSourceService(), new TestPersistence(), elastic: client);
        await using var owner = new MainWindowViewModel(
            state,
            scheduler: ImmediateScheduler.Instance,
            startPolling: false
        );
        owner.Settings.AddElasticConnectionCommand.Execute().Subscribe();
        var editor = Assert.Single(owner.Settings.ElasticConnections);
        editor.AddViewCommand.Execute().Subscribe();
        var view = Assert.Single(editor.Views);
        view.Sync(
            new ElasticViewSettings
            {
                Id = view.Id,
                DataViewId = "saved",
                OutputFields = ["zeta", "alpha"],
            }
        );

        await view.RefreshFieldsCommand.Execute().FirstAsync();
        Assert.Equal("saved", view.SelectedDataViewId);
        Assert.Equal(2, view.SelectedFieldCount);
        Assert.Equal("zeta → alpha", view.SelectedFieldsSummary);
        Assert.Equal("<zeta> <alpha>", view.RowPreview);
        var beta = view.Fields.Single(field => field.Name == "beta");
        Assert.Equal("text", beta.Type);
        Assert.False(beta.Searchable);
        Assert.Equal("text · not searchable", beta.MetadataDescription);
        view.ServerField = "beta";
        Assert.Contains("not searchable", view.FilterFieldError);
        view.ServerField = "alpha";
        Assert.Equal("keyword · searchable", view.FilterFieldStatus);
        Assert.Null(view.FilterFieldError);

        view.SelectedDataViewId = "refreshed";

        Assert.Equal(["zeta", "alpha"], view.ToSettings().OutputFields);
        Assert.Contains(view.VisibleFields, field => field.Name == "beta");
        view.Fields.Single(field => field.Name == "beta").IsOutput = true;
        view.Fields.Single(field => field.Name == "alpha").IsOutput = false;
        view.Fields.Single(field => field.Name == "alpha").IsOutput = true;
        Assert.Equal(["zeta", "beta", "alpha"], view.ToSettings().OutputFields);

        view.SelectedDataViewId = "refreshed-again";
        Assert.Equal(["zeta", "beta", "alpha"], view.ToSettings().OutputFields);
    }
}

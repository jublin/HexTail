using System.Collections.ObjectModel;
using HexTailSharp.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTailSharp.ViewModels;

internal sealed class ElasticConnectionEditorViewModel : ReactiveObject
{
    private readonly SettingsViewModel _owner;

    public ElasticConnectionEditorViewModel(SettingsViewModel owner, string id)
    {
        _owner = owner;
        Id = id;
    }

    public string Id { get; }
    public string Name { get; set; } = string.Empty;
    public string KibanaUrl { get; set; } = string.Empty;
    public string ElasticsearchUrl { get; set; } = string.Empty;
    public ElasticAuthMode AuthMode { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Secret { get; set; } = string.Empty;
    public string? DataViewId { get; set; }
    public string? DataViewTitle { get; set; }
    public string? TimeFieldName { get; set; }
    public string? ServerField { get; set; }
    public string? NamespaceField { get; set; }
    public ObservableCollection<ElasticFieldOptionViewModel> Fields { get; } = [];
    public ObservableCollection<ElasticSourceSettingViewModel> Sources { get; } = [];

    public ElasticConnectionSettings ToSettings() =>
        new()
        {
            Id = Id,
            Name = Name,
            KibanaUrl = KibanaUrl,
            ElasticsearchUrl = ElasticsearchUrl,
            AuthMode = AuthMode,
            Username = Username,
            DataViewId = DataViewId,
            DataViewTitle = DataViewTitle,
            TimeFieldName = TimeFieldName,
            ServerField = ServerField,
            NamespaceField = NamespaceField,
            OutputFields = Fields
                .Where(field => field.IsOutput)
                .Select(field => field.Name)
                .ToList(),
            Sources = Sources.Select(source => source.ToSettings()).ToList(),
        };

    internal async Task SaveAsync()
    {
        await _owner.CommitAsync(_owner.ConnectionSettingsWith(ToSettings(), Secret));
        Secret = string.Empty;
    }
}

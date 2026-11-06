using System.Globalization;
using HexTail.Elastic;

namespace HexTail.ViewModels;

/// <summary>One real document shown in the view editor's preview.</summary>
internal sealed class ElasticPreviewRowViewModel(ElasticHit hit)
{
    public string Text { get; } = hit.Line.Raw;
    public string Time { get; } =
        hit.Timestamp.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
}

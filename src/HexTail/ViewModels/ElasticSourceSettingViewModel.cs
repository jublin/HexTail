using System.Globalization;
using HexTail.Persistence;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal sealed class ElasticSourceSettingViewModel : ReactiveObject
{
    internal const string CountingText = "counting…";
    internal const string CountFailedText = "count failed";

    private string _serverValue = string.Empty;
    private string _namespaceValue = string.Empty;
    private string _countText = string.Empty;
    private bool _isZeroCount;
    private int _countVersion;

    public ElasticSourceSettingViewModel(string id) => Id = id;

    public string Id { get; }
    public string ServerValue
    {
        get => _serverValue;
        set => this.RaiseAndSetIfChanged(ref _serverValue, value);
    }
    public string NamespaceValue
    {
        get => _namespaceValue;
        set => this.RaiseAndSetIfChanged(ref _namespaceValue, value);
    }

    /// <summary>"", "counting…", "~3.2k docs · 15 min", "0 docs · 15 min" or "count failed".</summary>
    public string CountText
    {
        get => _countText;
        private set => this.RaiseAndSetIfChanged(ref _countText, value);
    }

    public bool IsZeroCount
    {
        get => _isZeroCount;
        private set => this.RaiseAndSetIfChanged(ref _isZeroCount, value);
    }

    /// <summary>Starts a new count; results from older counts are dropped.</summary>
    internal int BeginCount()
    {
        var version = ++_countVersion;
        IsZeroCount = false;
        CountText = CountingText;
        return version;
    }

    /// <summary>Drops any in-flight count and clears the displayed count.</summary>
    internal void InvalidateCount()
    {
        ++_countVersion;
        IsZeroCount = false;
        CountText = string.Empty;
    }

    internal bool CompleteCount(int version, long count)
    {
        if (version != _countVersion)
            return false;
        IsZeroCount = count <= 0;
        CountText = count <= 0 ? "0 docs · 15 min" : $"~{FormatCount(count)} docs · 15 min";
        return true;
    }

    internal bool FailCount(int version)
    {
        if (version != _countVersion)
            return false;
        IsZeroCount = false;
        CountText = CountFailedText;
        return true;
    }

    /// <summary>Exact below 1000, then one decimal with a k or M suffix.</summary>
    internal static string FormatCount(long count)
    {
        if (count < 1000)
            return Math.Max(count, 0).ToString(CultureInfo.InvariantCulture);
        var thousands = Math.Round(count / 1000.0, 1, MidpointRounding.AwayFromZero);
        if (thousands < 1000)
            return thousands.ToString("0.0", CultureInfo.InvariantCulture) + "k";
        var millions = Math.Round(count / 1_000_000.0, 1, MidpointRounding.AwayFromZero);
        return millions.ToString("0.0", CultureInfo.InvariantCulture) + "M";
    }

    public ElasticSourceSettings ToSettings() =>
        new()
        {
            Id = Id,
            ServerValue = ServerValue.Trim(),
            NamespaceValue = NamespaceValue.Trim(),
        };
}

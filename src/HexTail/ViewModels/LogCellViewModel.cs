using HexTail.Application;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>
/// One cell of a column-mode row: the text shown under one column, its highlight spans and the few
/// presentation flags the row template binds. Immutable; a row builds new cells when it re-renders.
/// </summary>
internal sealed class LogCellViewModel : ReactiveObject
{
    internal LogCellViewModel(
        string key,
        string text,
        IReadOnlyList<HighlightSpan> spans,
        double width,
        bool isLast,
        string? toolTip = null,
        bool isDimmed = false,
        string? levelClass = null
    )
    {
        Key = key;
        Text = text;
        Spans = spans;
        Width = width;
        IsLast = isLast;
        ToolTip = toolTip;
        IsDimmed = isDimmed;
        LevelClass = levelClass;
    }

    public string Key { get; }
    public string Text { get; }
    public IReadOnlyList<HighlightSpan> Spans { get; }

    /// <summary>The column's width in pixels; ignored for the last cell, which fills the row.</summary>
    public double Width { get; }
    public bool IsLast { get; }

    /// <summary>The width to apply to the text block: NaN (auto, fills) for the last cell.</summary>
    public double RenderWidth => IsLast ? double.NaN : Width;

    /// <summary>Time cells: full ISO-8601 time and the delta; other cells: the full text when it is long.</summary>
    public string? ToolTip { get; }

    /// <summary>True for a timestamp inherited from an earlier line.</summary>
    public bool IsDimmed { get; }

    /// <summary><c>trc</c>, <c>dbg</c>, <c>inf</c>, <c>wrn</c>, <c>err</c> on level cells with a known level.</summary>
    public string? LevelClass { get; }

    public bool IsTraceLevel => LevelClass == "trc";
    public bool IsDebugLevel => LevelClass == "dbg";
    public bool IsInfoLevel => LevelClass == "inf";
    public bool IsWarnLevel => LevelClass == "wrn";
    public bool IsErrorLevel => LevelClass == "err";
}

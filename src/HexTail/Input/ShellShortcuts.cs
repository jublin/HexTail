using Avalonia.Input;

namespace HexTail.Input;

internal enum ShellAction
{
    None,
    OpenFiles,
    CommandPalette,
    FocusSearch,
    SaveSession,
    ToggleSidebar,
    CloseView,
    NextView,
    PreviousView,
    SelectView,
    ToggleFollow,
    TogglePause,
    ScrollToTop,
    ScrollToBottom,
}

internal readonly record struct ShellCommand(ShellAction Action, int Index = 0)
{
    public static readonly ShellCommand None = new(ShellAction.None);
}

internal static class ShellShortcuts
{
    public static ShellCommand Resolve(Key key, KeyModifiers modifiers, bool textInputFocused)
    {
        var primary = (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shift = (modifiers & KeyModifiers.Shift) != 0;
        var alt = (modifiers & KeyModifiers.Alt) != 0;
        if (alt)
            return ShellCommand.None;
        if (primary)
            return key switch
            {
                Key.K => new(ShellAction.CommandPalette),
                Key.P when shift => new(ShellAction.CommandPalette),
                Key.O => new(ShellAction.OpenFiles),
                Key.F => new(ShellAction.FocusSearch),
                Key.S => new(ShellAction.SaveSession),
                Key.B => new(ShellAction.ToggleSidebar),
                Key.W => new(ShellAction.CloseView),
                Key.Tab => new(shift ? ShellAction.PreviousView : ShellAction.NextView),
                >= Key.D1 and <= Key.D9 => new(ShellAction.SelectView, key - Key.D1),
                _ => ShellCommand.None,
            };
        if (textInputFocused)
            return ShellCommand.None;
        return key switch
        {
            Key.OemQuestion or Key.Divide when !shift => new(ShellAction.FocusSearch),
            Key.F when !shift => new(ShellAction.ToggleFollow),
            Key.Space => new(ShellAction.TogglePause),
            Key.G => new(shift ? ShellAction.ScrollToBottom : ShellAction.ScrollToTop),
            _ => ShellCommand.None,
        };
    }
}

using Avalonia.Input;
using HexTail.Input;

namespace HexTail.Tests.Input;

public sealed class ShellShortcutsTests
{
    [Theory]
    [InlineData(Key.K, KeyModifiers.Control, nameof(ShellAction.CommandPalette))]
    [InlineData(
        Key.P,
        KeyModifiers.Control | KeyModifiers.Shift,
        nameof(ShellAction.CommandPalette)
    )]
    [InlineData(Key.O, KeyModifiers.Control, nameof(ShellAction.OpenFiles))]
    [InlineData(Key.O, KeyModifiers.Meta, nameof(ShellAction.OpenFiles))]
    [InlineData(Key.F, KeyModifiers.Control, nameof(ShellAction.FocusSearch))]
    [InlineData(Key.S, KeyModifiers.Control, nameof(ShellAction.SaveSession))]
    [InlineData(Key.B, KeyModifiers.Control, nameof(ShellAction.ToggleSidebar))]
    [InlineData(Key.W, KeyModifiers.Control, nameof(ShellAction.CloseView))]
    [InlineData(Key.Tab, KeyModifiers.Control, nameof(ShellAction.NextView))]
    [InlineData(
        Key.Tab,
        KeyModifiers.Control | KeyModifiers.Shift,
        nameof(ShellAction.PreviousView)
    )]
    public void PrimaryShortcutsWorkEvenInTextBoxes(
        Key key,
        KeyModifiers modifiers,
        string expected
    ) =>
        Assert.Equal(
            Enum.Parse<ShellAction>(expected),
            ShellShortcuts.Resolve(key, modifiers, textInputFocused: true).Action
        );

    [Theory]
    [InlineData(Key.D1, 0)]
    [InlineData(Key.D9, 8)]
    public void ControlDigitSelectsView(Key key, int index) =>
        Assert.Equal(
            new ShellCommand(ShellAction.SelectView, index),
            ShellShortcuts.Resolve(key, KeyModifiers.Control, textInputFocused: false)
        );

    [Theory]
    [InlineData(Key.OemQuestion, KeyModifiers.None, nameof(ShellAction.FocusSearch))]
    [InlineData(Key.F, KeyModifiers.None, nameof(ShellAction.ToggleFollow))]
    [InlineData(Key.Space, KeyModifiers.None, nameof(ShellAction.TogglePause))]
    [InlineData(Key.G, KeyModifiers.None, nameof(ShellAction.ScrollToTop))]
    [InlineData(Key.G, KeyModifiers.Shift, nameof(ShellAction.ScrollToBottom))]
    public void SingleKeysWorkOutsideTextInput(Key key, KeyModifiers modifiers, string expected) =>
        Assert.Equal(
            Enum.Parse<ShellAction>(expected),
            ShellShortcuts.Resolve(key, modifiers, textInputFocused: false).Action
        );

    [Theory]
    [InlineData(Key.F)]
    [InlineData(Key.Space)]
    [InlineData(Key.G)]
    [InlineData(Key.OemQuestion)]
    public void SingleKeysAreIgnoredWhileTyping(Key key) =>
        Assert.Equal(
            ShellCommand.None,
            ShellShortcuts.Resolve(key, KeyModifiers.None, textInputFocused: true)
        );

    [Fact]
    public void AltCombinationsAreLeftForReordering() =>
        Assert.Equal(ShellCommand.None, ShellShortcuts.Resolve(Key.Down, KeyModifiers.Alt, false));
}

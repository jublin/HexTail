using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.ViewModels;

namespace HexTail.Views;

public partial class SourceSidebar : UserControl
{
    private MainWindowViewModel? _owner;
    private StreamEditorViewModel? _editor;

    public SourceSidebar()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Attach(DataContext as MainWindowViewModel);
        // Escape anywhere in the inline editor cancels it (or only its delete confirmation).
        StreamEditorPanel.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Bubble);
    }

    private void Attach(MainWindowViewModel? owner)
    {
        if (_owner is not null)
            _owner.PropertyChanged -= OnOwnerChanged;
        _owner = owner;
        if (_owner is not null)
            _owner.PropertyChanged += OnOwnerChanged;
        TrackEditor(_owner?.StreamEditor);
    }

    private void OnOwnerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.StreamEditor))
            TrackEditor(_owner?.StreamEditor);
    }

    /// <summary>Focuses the name box when an editor opens and gives focus back to its opener when it closes.</summary>
    private void TrackEditor(StreamEditorViewModel? editor)
    {
        if (ReferenceEquals(editor, _editor))
            return;
        var previous = _editor;
        if (previous is not null)
            previous.FocusRequested -= FocusNameBox;
        _editor = editor;
        if (editor is not null)
        {
            editor.FocusRequested += FocusNameBox;
            FocusNameBox();
        }
        else if (previous is not null)
            ReturnFocus(previous);
    }

    private void FocusNameBox() =>
        Dispatcher.UIThread.Post(() =>
        {
            var box = StreamNameBox;
            if (_editor is null || !box.IsEffectivelyVisible)
                return;
            box.Focus();
            box.SelectAll();
        });

    /// <summary>
    /// After the editor closed: focus the control that opened it, the stream's Edit button or the + button,
    /// unless the user has already moved focus somewhere outside the editor.
    /// </summary>
    private void ReturnFocus(StreamEditorViewModel closed)
    {
        var focus = TopLevel.GetTopLevel(this)?.FocusManager;
        var current = focus?.GetFocusedElement() as Visual;
        if (
            current is not null
            && current.IsEffectivelyVisible
            && !StreamEditorPanel.IsVisualAncestorOf(current)
            && !ReferenceEquals(current, StreamEditorPanel)
        )
            return;
        Dispatcher.UIThread.Post(() =>
        {
            if (_editor is not null)
                return;
            Button? target = null;
            // A deleted stream has no Edit button left to return to.
            if (closed.StreamId is { } id && !closed.WasDeleted)
                target = this.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button =>
                        button.IsEffectivelyVisible
                        && ReferenceEquals(button.Command, _owner?.EditStreamCommand)
                        && Equals(button.CommandParameter, id)
                    );
            (target ?? SidebarNewStreamButton).Focus();
        });
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.Handled || _editor is not { } editor)
            return;
        e.Handled = editor.HandleEscape();
    }
}

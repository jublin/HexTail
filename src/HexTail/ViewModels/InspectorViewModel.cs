using System.Collections.ObjectModel;
using System.Reactive;
using System.Text.Encodings.Web;
using System.Text.Json;
using HexTail.Domain;
using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

/// <summary>Projects the selected row of the selected source into the right-hand inspector panel.</summary>
internal sealed class InspectorViewModel : ReactiveObject
{
    internal const double MinWidth = 260;
    internal const double MaxWidth = 640;
    internal const double DefaultWidth = 360;
    private const string NoRowTitle = "No row selected";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly MainWindowViewModel _owner;
    private bool _isOpen;
    private double _width = DefaultWidth;
    private Line? _line;
    private int _index = -1;
    private string _title = NoRowTitle;
    private string _rawText = string.Empty;
    private string? _jsonText;
    private string? _error;

    internal InspectorViewModel(MainWindowViewModel owner)
    {
        _owner = owner;
        CloseCommand = ReactiveCommand.Create(
            () =>
            {
                IsOpen = false;
            },
            owner.Scheduler
        );
    }

    internal MainWindowViewModel Owner => _owner;
    public ReactiveCommand<Unit, Unit> CloseCommand { get; }
    public ObservableCollection<InspectorFieldViewModel> Fields { get; } = [];

    public bool IsOpen
    {
        get => _isOpen;
        set
        {
            this.RaiseAndSetIfChanged(ref _isOpen, value);
            this.RaisePropertyChanged(nameof(IsShown));
        }
    }

    public bool IsShown => IsOpen && _owner.SelectedFile is not null;

    public double Width
    {
        get => _width;
        set => this.RaiseAndSetIfChanged(ref _width, Math.Clamp(value, MinWidth, MaxWidth));
    }

    public bool HasLine => _line is not null;
    public string Title => _title;
    public string RawText => _rawText;
    public bool HasFields => Fields.Count > 0;
    public string? JsonText => _jsonText;
    public bool CanCopyJson => _jsonText is not null;

    public string? Error
    {
        get => _error;
        internal set
        {
            this.RaiseAndSetIfChanged(ref _error, value);
            this.RaisePropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    internal void Open() => IsOpen = true;

    internal void Toggle() => IsOpen = !IsOpen;

    internal void Sync()
    {
        this.RaisePropertyChanged(nameof(IsShown));
        var file = _owner.SelectedFile?.Model;
        Line? line = null;
        var index = -1;
        if (file?.SelectedLine is int selected && selected >= 0 && selected < file.Buffer.Count)
        {
            index = selected;
            line = file.Buffer[selected];
        }

        if (ReferenceEquals(line, _line) && index == _index)
            return;
        _line = line;
        _index = index;
        Error = null;
        _title = line is null ? NoRowTitle : $"Line {index + 1:N0}";
        _rawText = line?.Raw ?? string.Empty;

        Fields.Clear();
        _jsonText = null;
        if (line?.ParsedFields is { Count: > 0 } fields)
        {
            foreach (var pair in fields)
                Fields.Add(new InspectorFieldViewModel(this, pair.Key, pair.Value));
            _jsonText = JsonSerializer.Serialize(fields, JsonOptions);
        }

        this.RaisePropertyChanged(nameof(HasLine));
        this.RaisePropertyChanged(nameof(Title));
        this.RaisePropertyChanged(nameof(RawText));
        this.RaisePropertyChanged(nameof(HasFields));
        this.RaisePropertyChanged(nameof(JsonText));
        this.RaisePropertyChanged(nameof(CanCopyJson));
    }
}

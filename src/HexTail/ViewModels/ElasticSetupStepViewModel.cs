using ReactiveUI;
using ReactiveUI.Reactive;

namespace HexTail.ViewModels;

internal enum ElasticSetupStep
{
    Connection,
    DataView,
    Filter,
    Columns,
    Finish,
}

/// <summary>One entry of the stepper at the top of the setup flow.</summary>
internal sealed class ElasticSetupStepViewModel : ReactiveObject
{
    private bool _isCurrent;
    private bool _isReachable;
    private bool _isDone;

    internal ElasticSetupStepViewModel(ElasticSetupStep step, string title)
    {
        Step = step;
        Title = title;
    }

    public ElasticSetupStep Step { get; }
    public string Title { get; }
    public int Number => (int)Step + 1;

    public bool IsCurrent
    {
        get => _isCurrent;
        private set => this.RaiseAndSetIfChanged(ref _isCurrent, value);
    }

    /// <summary>Already visited, or the next step once the current one is complete.</summary>
    public bool IsReachable
    {
        get => _isReachable;
        private set => this.RaiseAndSetIfChanged(ref _isReachable, value);
    }

    public bool IsDone
    {
        get => _isDone;
        private set => this.RaiseAndSetIfChanged(ref _isDone, value);
    }

    internal void Update(bool isCurrent, bool isReachable, bool isDone)
    {
        IsCurrent = isCurrent;
        IsReachable = isReachable;
        IsDone = isDone;
    }
}

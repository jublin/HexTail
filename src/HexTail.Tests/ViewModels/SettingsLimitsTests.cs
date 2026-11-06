using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using HexTail.Persistence;
using HexTail.Tests.Support;

namespace HexTail.Tests.ViewModels;

public sealed class SettingsLimitsTests
{
    [AvaloniaFact]
    public async Task EditingALimitPersistsItClamped()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.Settings.MaxLinesValue = 5_000;
            await UiWait.Until(() => owner.State.Settings.MaxLines == 5_000);

            owner.Settings.MaxLinesValue = 10; // below the minimum
            await UiWait.Until(() => owner.State.Settings.MaxLines == 1_000);
            Assert.Equal(1_000, owner.Settings.MaxLinesValue);

            owner.Settings.ContextBelowValue = 500; // above the maximum
            await UiWait.Until(() => owner.State.Settings.ContextBelow == 50);

            owner.Settings.ElasticInitialLinesValue = 25_000;
            await UiWait.Until(() => owner.State.Settings.ElasticInitialLines == 25_000);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task ClearingTheFieldKeepsTheCurrentValue()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            var before = owner.State.Settings.MaxLines;
            owner.Settings.MaxLinesValue = null;
            Assert.Equal(before, owner.State.Settings.MaxLines);
            Assert.Equal(before, owner.Settings.MaxLinesValue);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
        }
    }

    [AvaloniaFact]
    public async Task NumericFieldsBindTwoWayAndRestoreWhenCleared()
    {
        var window = TestWindow.Create(out var owner);
        try
        {
            owner.SettingsOpen = true;
            owner.Settings.SectionIndex = 1;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var box = window
                .GetVisualDescendants()
                .OfType<NumericUpDown>()
                .Single(control => control.Name == "MaxLinesBox");
            Assert.Equal(owner.State.Settings.MaxLines, (int)box.Value!.Value);

            box.Value = 7_000;
            await UiWait.Until(() => owner.State.Settings.MaxLines == 7_000);

            box.Value = null;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(7_000, owner.State.Settings.MaxLines);
            await UiWait.Until(() => box.Value == 7_000);
        }
        finally
        {
            window.Close();
            await owner.DisposeAsync();
        }
    }
}

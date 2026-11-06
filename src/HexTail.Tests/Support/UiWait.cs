using Avalonia.Threading;

namespace HexTail.Tests.Support;

internal static class UiWait
{
    public static async Task Until(Func<bool> condition, int timeoutMs = 10000)
    {
        var timeout = DateTime.UtcNow + TimeSpan.FromMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < timeout)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
        Assert.True(condition());
    }
}

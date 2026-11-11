using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace HexTail.Tests.Support;

/// <summary>
/// Reports a stack trace, as an xUnit diagnostic message, when a UI object is touched from the wrong thread. Those exceptions
/// usually escape a finished test (a headless test gets a fresh UI thread each time), and xUnit then
/// reports only "Catastrophic failure" with the message, which does not say where the call came from.
/// </summary>
internal static class CrashDiagnostics
{
    private const string WrongThread = "different thread owns it";
    private const int MaxReports = 5;
    private const int MaxFrames = 30;
    private static int _reports;

    [ModuleInitializer]
    internal static void Install()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (
                e.Exception is InvalidOperationException ex
                && ex.Message.Contains(WrongThread, StringComparison.Ordinal)
            )
                Report(ex);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Write("Unhandled exception", e.ExceptionObject.ToString());
    }

    private static void Report(Exception ex)
    {
        if (Interlocked.Increment(ref _reports) > MaxReports)
            return;
        // A first-chance exception has no stack trace of its own yet; the current stack holds the throw site.
        // Skip this method and the event handler, and keep the frames nearest the throw.
        var frames = new StackTrace(2, true).ToString().Split(Environment.NewLine).Take(MaxFrames);
        Write($"{ex.GetType().Name}: {ex.Message}", string.Join(Environment.NewLine, frames));
    }

    private static void Write(string what, string? stack)
    {
        try
        {
            var test = TestContext.Current.Test?.TestDisplayName ?? "(no running test)";
            TestContext.Current.SendDiagnosticMessage(
                $"[crash-diagnostics] {what}{Environment.NewLine}"
                    + $"  test: {test}; thread {Environment.CurrentManagedThreadId}{Environment.NewLine}"
                    + stack
            );
        }
        catch
        {
            // Diagnostics must never become the failure.
        }
    }
}

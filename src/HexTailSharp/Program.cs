using AtomUI;
using Avalonia;
using HexTailSharp;
using ReactiveUI.Avalonia;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseReactiveUI(_ => { })
            .UseAtomUIPlatformDetect()
            .WithAtomUIDefaultOptions();
}

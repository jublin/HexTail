using Avalonia;
using HexTail;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;
using ReactiveUI.Avalonia.Reactive;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) =>
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
    {
        IconProvider.Current.Register<MaterialDesignIconProvider>();

        return AppBuilder.Configure<App>().UsePlatformDetect().UseReactiveUI(_ => { });
    }
}

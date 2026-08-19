using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;
using ReactiveUI.Avalonia.Reactive;

[assembly: AvaloniaTestApplication(typeof(HexTailSharp.Tests.Ui.HeadlessApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HexTailSharp.Tests.Ui;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp()
    {
        IconProvider.Current.Register<MaterialDesignIconProvider>();
        return AppBuilder
            .Configure<App>()
            .UseReactiveUI(_ => { })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}

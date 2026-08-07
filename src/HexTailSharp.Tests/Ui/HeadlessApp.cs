using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using ReactiveUI.Avalonia.Reactive;

[assembly: AvaloniaTestApplication(typeof(HexTailSharp.Tests.Ui.HeadlessApp))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace HexTailSharp.Tests.Ui;

public static class HeadlessApp
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseReactiveUI(_ => { })
            .UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

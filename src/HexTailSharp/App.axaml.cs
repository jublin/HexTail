using System.Globalization;
using AtomUI;
using AtomUI.Desktop.Controls;
using AtomUI.Theme;
using AtomUI.Theme.Configuration;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace HexTailSharp;

public partial class App : Avalonia.Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        AvaloniaXamlLoader.Load(this);
        this.UseAtomUI(builder =>
        {
            builder.WithApplicationId("HexTailSharp");
            builder.WithDefaultCultureInfo(CultureInfo.CurrentUICulture);
            builder.WithInitialTheme(
                IThemeManager.DEFAULT_THEME_ID,
                new ThemeConfigBuilder().WithAlgorithms(["Default"]).Build()
            );
            builder.UseAlibabaSansFont();
            builder.UseDesktopControls();
            builder.UseDesktopColorPicker();
        });
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new MainWindow(desktop.Args);

        base.OnFrameworkInitializationCompleted();
    }
}

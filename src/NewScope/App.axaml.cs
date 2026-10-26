using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using NewScope.Services;
using NewScope.ViewModels;
using NewScope.Views;

namespace NewScope;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Localization.Initialize();
            RequestedThemeVariant = string.Equals(
                AppSettingsStore.Current.Theme,
                "Light",
                StringComparison.OrdinalIgnoreCase)
                ? ThemeVariant.Light
                : ThemeVariant.Dark;

            var demo = desktop.Args?.Any(arg =>
                string.Equals(arg, "--demo", StringComparison.OrdinalIgnoreCase)) == true;
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainViewModel(demo),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
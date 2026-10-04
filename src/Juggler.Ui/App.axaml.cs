using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Juggler.Ui.Views;

namespace Juggler.Ui;

public sealed partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // The variant must be set before any window exists, not in Window.Opened.
        //
        // Resources declared inside ThemeDictionaries resolve against the *effective* theme
        // variant of the element that uses them. If this assignment happens after the first
        // window is realized, that pass has already resolved against Default, DynamicResource
        // lookups miss, and the affected brushes silently render in the wrong palette. That is
        // how a dark window ended up with a near-white status pill.
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = Avalonia.Controls.ShutdownMode.OnMainWindowClose;
            desktop.MainWindow = new MainWindow();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Switches between the light and dark palettes. Both are first-class.</summary>
    public static void ApplyTheme(ThemeVariant variant) => Current!.RequestedThemeVariant = variant;
}

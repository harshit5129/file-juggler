using Avalonia;
using System;
using System.Runtime.InteropServices;

namespace Juggler.Ui;

class Program
{
    // Must run before Avalonia creates any window.
    //
    // PerMonitorV2 is also declared in app.manifest, but a manifest only covers what the loader
    // applies at startup. If awareness is established after the first window exists, Avalonia
    // computes its client size against 1 DIP == 1 physical pixel while still reporting
    // RenderScaling 1.25, so it lays out 1180 DIP into a window that can only show ~950 of them.
    // The visible result is the right-hand ~280 px of every row and toolbar being clipped.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [STAThread]
    public static void Main(string[] args)
    {
        // Best effort: on an OS that rejects it the manifest still applies, so a false return
        // here is not fatal and must not stop startup.
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SetProcessDpiAwarenessContext(PerMonitorAwareV2);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
            {
                // Pre-10 Windows: app.manifest is the only mechanism available.
                // Non-Windows dev machines: user32.dll does not exist.
            }
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();
}

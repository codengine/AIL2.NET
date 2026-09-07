using Avalonia;

namespace AIL2.Player;

/// <summary>Starts the standalone Avalonia player.</summary>
public static class Program
{
    /// <summary>Runs the desktop application until its window closes.</summary>
    /// <param name="args">Arguments passed to the Avalonia desktop lifetime.</param>
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Configures platform services, bundled fonts and trace logging.</summary>
    /// <returns>The builder used for desktop startup.</returns>
    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
    }
}

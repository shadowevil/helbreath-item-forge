using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace ItemForge.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var splash = new SplashWindow();
            splash.Show();
            // Posted, so the splash actually paints before the workspace load and the MCP server start hold
            // the UI thread. The lifetime has already made its own MainWindow.Show() call, so Start shows it.
            Dispatcher.UIThread.Post(() => Start(desktop, splash), DispatcherPriority.Background);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void Start(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        var args = desktop.Args ?? Array.Empty<string>();
        var window = new MainWindow();
        desktop.MainWindow = window;
        // The main window comes up behind the splash, so an agent driving the app over MCP is never blocked
        // waiting for the splash to go away.
        window.Show();

        // Opens card files passed on the command line; non-file args (like --mcp) are ignored there.
        window.OpenFromArgs(args);
        // --mcp launches with the MCP server on (Observe) for this run.
        if (args.Any(a => string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase)))
        {
            window.EnableIntegrationFromCli();
        }

        splash.HandOverTo(window);
    }
}

using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace ItemForge.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;

            var args = desktop.Args ?? Array.Empty<string>();
            // Opens card files passed on the command line; non-file args (like --mcp) are ignored there.
            window.OpenFromArgs(args);
            // `--mcp` launches with the MCP server on (Observe) for this run.
            if (args.Any(a => string.Equals(a, "--mcp", StringComparison.OrdinalIgnoreCase)))
            {
                window.EnableIntegrationFromCli();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}

using Avalonia;

namespace ItemForge.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Single-instance: if the forge is already running, forward our args to it (opening a card in the
        // one window) and print an agent-readable report - including the running instance's MCP connection
        // details - instead of launching a duplicate that would collide on the MCP port.
        if (!SingleInstance.ClaimPrimary())
        {
            try
            {
                Console.Out.WriteLine(SingleInstance.ForwardToPrimary(args));
                Console.Out.Flush();
            }
            catch
            {
                // No console attached (e.g. double-clicked) - nothing to report to.
            }
            return;
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}

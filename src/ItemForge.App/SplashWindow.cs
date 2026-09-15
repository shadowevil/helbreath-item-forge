using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

namespace ItemForge.App;

// The loading splash. The logo is glow-on-transparency artwork - no pixel in it is fully opaque - so the
// window is transparent and lets the art shape itself instead of sitting in a box. The status line gets its
// own translucent pill, because text over bare transparency is at the mercy of whatever is behind it.
//
// If the platform refuses per-pixel transparency, OnOpened paints a dark panel instead, so the splash is
// never a black rectangle.
public sealed class SplashWindow : Window
{
    // The app window is held back until this has passed, so the splash is the whole of the start-up, not a
    // banner over a half-drawn tool (user direction, 2026-09-15).
    private static readonly TimeSpan MinimumDisplay = TimeSpan.FromSeconds(2);

    // The main window appears while the splash fades, so the two overlap rather than blink.
    private static readonly TimeSpan FadeOut = TimeSpan.FromMilliseconds(220);

    private const int LogoWidth = 512;

    private readonly Stopwatch _shown = Stopwatch.StartNew();
    private readonly Border _root;
    private readonly TextBlock _status = new()
    {
        Text = "Loading...",
        FontSize = 11.5,
        Foreground = new SolidColorBrush(Color.Parse("#C8B48A")),
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    public SplashWindow()
    {
        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        Width = 560;
        Height = 430;

        var logo = new Image
        {
            Source = new Bitmap(AssetLoader.Open(new Uri("avares://helbreath_item_forge/Assets/logo.png"))),
            Width = LogoWidth,
            Stretch = Stretch.Uniform,
        };

        var version = new TextBlock
        {
            Text = "version " + AppVersion,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.Parse("#7A6B54")),
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var pill = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#B0121214")),
            BorderBrush = new SolidColorBrush(Color.Parse("#662E2A24")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 5, 16, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = new StackPanel { Spacing = 2, Children = { _status, version } },
        };

        _root = new Border
        {
            Background = Brushes.Transparent,
            Child = new StackPanel
            {
                Spacing = 6,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { logo, pill },
            },
        };
        Content = _root;
    }

    public void SetStatus(string text) => _status.Text = text;

    // Keeps the splash up for its minimum time, then hands the screen over: the main window is shown as the
    // splash fades out. Until then the app is loaded but deliberately off screen.
    public void HandOverTo(Window main)
    {
        SetStatus("Ready");
        var remaining = MinimumDisplay - _shown.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            Finish(main);
            return;
        }
        DispatcherTimer.RunOnce(() => Finish(main), remaining);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (ActualTransparencyLevel == WindowTransparencyLevel.None)
        {
            // No per-pixel alpha here: fall back to a panel in the app's own dark, rather than a black box.
            _root.Background = new SolidColorBrush(Color.Parse("#0C0C0E"));
            _root.BorderBrush = new SolidColorBrush(Color.Parse("#2E2A24"));
            _root.BorderThickness = new Thickness(1);
        }
    }

    private void Finish(Window main)
    {
        main.Show();
        main.Activate();

        // Fade rather than vanish, so the hand-over reads as one motion. The splash is topmost, so the main
        // window comes up underneath it and is uncovered as this runs.
        var fade = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        var started = Stopwatch.StartNew();
        fade.Tick += (_, _) =>
        {
            double progress = started.Elapsed.TotalMilliseconds / FadeOut.TotalMilliseconds;
            if (progress >= 1)
            {
                fade.Stop();
                Close();
                main.Activate();
                return;
            }
            Opacity = 1 - progress;
        };
        fade.Start();
    }

    private static string AppVersion =>
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";
}

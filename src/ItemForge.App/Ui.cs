using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace ItemForge.App;

// Small helpers for views built in code, so every colour comes from the theme tokens (DynamicResource) and
// restyles with System / Light / Dark like the rest of the app.
internal static class Ui
{
    public static T Res<T>(this T target, AvaloniaProperty property, string key) where T : AvaloniaObject
    {
        target[!property] = new DynamicResourceExtension(key);
        return target;
    }

    public static TextBlock Text(string text, string? cssClass = null, double? size = null)
    {
        var block = new TextBlock { Text = text };
        if (cssClass is not null)
        {
            block.Classes.Add(cssClass);
        }
        else
        {
            block.Res(TextBlock.ForegroundProperty, "HbaTextBrush");
        }
        if (size is double s)
        {
            block.FontSize = s;
        }
        return block;
    }

    public static Button DialogButton(string text, EventHandler<Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var button = new Button { Content = text, MinWidth = 84 };
        button.Classes.Add("dialog");
        button.Click += click;
        return button;
    }

    // An X glyph for tab close buttons.
    public static Avalonia.Controls.Shapes.Path CloseGlyph() =>
        new Avalonia.Controls.Shapes.Path
        {
            StrokeThickness = 1,
            Data = Avalonia.Media.Geometry.Parse("M0,0 L8,8 M0,8 L8,0"),
        }.Res(Avalonia.Controls.Shapes.Shape.StrokeProperty, "HbaMutedBrush");
}

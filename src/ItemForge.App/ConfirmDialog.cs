using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;

namespace ItemForge.App;

public enum ConfirmResult { Save, Discard, Cancel }

// Themed confirmations (Workbench ConfirmDialog): Save / Don't Save / Cancel for leaving a dirty card, and a
// destructive yes/no whose confirm button reads red.
public sealed class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("HbaEditorBrush");

        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 96 };
        save.Classes.Add("dialog");
        save.Click += (_, _) => Close(ConfirmResult.Save);

        var discard = new Button { Content = "Don't Save", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        discard.Classes.Add("dialog");
        discard.Click += (_, _) => Close(ConfirmResult.Discard);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Classes.Add("dialog");
        cancel.Click += (_, _) => Close(ConfirmResult.Cancel);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { save, discard, cancel },
        };

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("HbaTextBrush");

        Content = new StackPanel { Margin = new Thickness(18), Children = { text, buttons } };
    }

    public static Task<ConfirmResult> Show(Window owner, string title, string message) =>
        new ConfirmDialog(title, message).ShowDialog<ConfirmResult>(owner);

    private ConfirmDialog(string title, string message, string confirmText)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("HbaEditorBrush");

        var confirm = new Button { Content = confirmText, IsDefault = true, MinWidth = 96, Foreground = new SolidColorBrush(Color.Parse("#F14C4C")) };
        confirm.Classes.Add("dialog");
        confirm.Click += (_, _) => Close(true);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Classes.Add("dialog");
        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { confirm, cancel },
        };

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("HbaTextBrush");

        Content = new StackPanel { Margin = new Thickness(18), Children = { text, buttons } };
    }

    public static Task<bool> Ask(Window owner, string title, string message, string confirmText) =>
        new ConfirmDialog(title, message, confirmText).ShowDialog<bool>(owner);
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;

namespace ItemForge.App;

// A minimal themed modal text-input dialog (new card name, duplicate name...). Returns the entered string,
// or null if cancelled. Uses DynamicResource so it matches the active theme. (Workbench InputDialog.)
public sealed class InputDialog : Window
{
    private readonly TextBox _box;

    public InputDialog(string title, string prompt, string initial)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        this[!BackgroundProperty] = new DynamicResourceExtension("HbaEditorBrush");

        var label = new TextBlock { Text = prompt, FontSize = 12.5, Margin = new Thickness(0, 0, 0, 6) };
        label[!TextBlock.ForegroundProperty] = new DynamicResourceExtension("HbaTextBrush");

        _box = new TextBox { Text = initial, FontSize = 13 };

        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 84 };
        ok.Classes.Add("dialog");
        ok.Click += (_, _) => Close(_box.Text ?? "");

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 84, Margin = new Thickness(8, 0, 0, 0) };
        cancel.Classes.Add("dialog");
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
            Children = { ok, cancel },
        };

        Content = new StackPanel { Margin = new Thickness(18), Children = { label, _box, buttons } };
        Opened += (_, _) => { _box.SelectAll(); _box.Focus(); };
    }

    public static Task<string?> Show(Window owner, string title, string prompt, string initial) =>
        new InputDialog(title, prompt, initial).ShowDialog<string?>(owner);
}

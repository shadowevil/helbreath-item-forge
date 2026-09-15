using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ItemForge.Core;
using ItemForge.Core.Cards;

namespace ItemForge.App.Views;

// Edits one card's working copy. Phase 1 has the Details tab (identity, item binding, model file, notes);
// the presentation tabs are shown locked with the phase that brings them, so the shape of the editor is
// visible from the start. Every edit writes straight into the working copy and raises Edited; saving is
// the window's job.
public sealed class CardEditorView : UserControl
{
    private static readonly (string Name, string Phase)[] PresentationTabs =
    {
        ("Worn", "phase 5"), ("Equip", "phase 4"), ("Inventory", "phase 6"), ("Ground", "phase 6"), ("Model", "phase 2"),
    };

    private readonly Card _card;
    private readonly Workspace _workspace;
    private readonly TextBox _name = new() { FontSize = 13 };
    private readonly TextBox _itemModel = new() { FontSize = 13, Watermark = "e.g. longsword" };
    private readonly TextBox _itemIds = new() { FontSize = 13, Watermark = "e.g. 17, 18, 19, 22" };
    private readonly ComboBox _weaponClass = new() { FontSize = 13, MinWidth = 220 };
    private readonly TextBox _modelPath = new() { FontSize = 13, IsReadOnly = true, Watermark = "no model bound" };
    private readonly TextBox _notes = new() { FontSize = 13, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120 };
    private readonly TextBlock _idsError = Ui.Text("", "hint");
    private readonly StackPanel _modelStatus = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _modelDetail = Ui.Text("", "hint");
    private readonly TextBlock _fileInfo = Ui.Text("", "hint");
    private readonly List<string> _classItems = new(WeaponClasses.All);
    private bool _loading;

    public event Action? Edited;

    public CardEditorView(Card card, Workspace workspace)
    {
        _card = card;
        _workspace = workspace;
        _idsError.Res(TextBlock.ForegroundProperty, "HbaErrorBrush");

        _name.TextChanged += (_, _) => Apply(() => _card.Name = _name.Text ?? "");
        _itemModel.TextChanged += (_, _) => Apply(() => _card.Item.Model = (_itemModel.Text ?? "").Trim());
        _itemIds.TextChanged += (_, _) => Apply(ApplyIds);
        _notes.TextChanged += (_, _) => Apply(() => _card.Notes = _notes.Text ?? "");
        _weaponClass.ItemsSource = _classItems;
        _weaponClass.SelectionChanged += (_, _) => Apply(() => _card.Item.WeaponClass = _weaponClass.SelectedItem as string ?? "");

        var browse = Ui.DialogButton("Browse...", async (_, _) => await BrowseModelAsync());
        var rehash = Ui.DialogButton("Rehash", (_, _) => Rebind());
        ToolTip.SetTip(rehash, "Accept the model file as it is now: re-record its SHA-256.");
        var clear = Ui.DialogButton("Clear", (_, _) => Apply(() => ModelBinding.Clear(_card), refresh: true));

        var modelRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto") };
        modelRow.Children.Add(_modelPath);
        AddAt(modelRow, browse, 1, new Thickness(8, 0, 0, 0));
        AddAt(modelRow, rehash, 2, new Thickness(6, 0, 0, 0));
        AddAt(modelRow, clear, 3, new Thickness(6, 0, 0, 0));

        var form = new StackPanel { Spacing = 10, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(24, 18, 24, 24) };
        form.Children.Add(Ui.Text("CARD", "section"));
        form.Children.Add(Field("Name", _name));
        form.Children.Add(Ui.Text("ITEM", "section").WithTopMargin(10));
        form.Children.Add(Field("Item model", _itemModel, "The items.hba model group this art is for: sprites/items/<model>."));
        form.Children.Add(Field("Item ids", _itemIds, "Items that display this model (informational; several items can share one model)."));
        form.Children.Add(_idsError);
        form.Children.Add(Field("Weapon class", _weaponClass, "Picks the pose baseline the Worn presentation starts from."));
        form.Children.Add(Ui.Text("MODEL", "section").WithTopMargin(10));
        form.Children.Add(Field("Model file", modelRow));
        form.Children.Add(new StackPanel { Spacing = 3, Margin = new Thickness(140, 0, 0, 0), Children = { _modelStatus, _modelDetail } });
        form.Children.Add(Ui.Text("NOTES", "section").WithTopMargin(10));
        form.Children.Add(_notes);
        form.Children.Add(_fileInfo.WithTopMargin(6));

        var root = new DockPanel();
        var tabs = BuildPresentationTabs();
        DockPanel.SetDock(tabs, Dock.Top);
        root.Children.Add(tabs);
        root.Children.Add(new ScrollViewer { Content = form });
        Content = root;

        LoadFromCard();
    }

    public string CardId => _card.Id;

    // Re-reads every field from the working copy (after an MCP edit or a save).
    public void LoadFromCard()
    {
        _loading = true;
        try
        {
            _name.Text = _card.Name;
            _itemModel.Text = _card.Item.Model;
            _itemIds.Text = string.Join(", ", _card.Item.Ids);
            _notes.Text = _card.Notes;
            if (!string.IsNullOrEmpty(_card.Item.WeaponClass) && !_classItems.Contains(_card.Item.WeaponClass))
            {
                _classItems.Add(_card.Item.WeaponClass);
                _weaponClass.ItemsSource = null;
                _weaponClass.ItemsSource = _classItems;
            }
            _weaponClass.SelectedItem = string.IsNullOrEmpty(_card.Item.WeaponClass) ? null : _card.Item.WeaponClass;
            _modelPath.Text = _card.Model.Path;
            _idsError.Text = "";
            _idsError.IsVisible = false;
        }
        finally
        {
            _loading = false;
        }
        UpdateModelInfo();
        UpdateFileInfo();
    }

    public void UpdateFileInfo()
    {
        string created = _card.Created == default ? "not saved yet" : _card.Created.ToString("yyyy-MM-dd HH:mm") + " UTC";
        string modified = _card.Modified == default ? "-" : _card.Modified.ToString("yyyy-MM-dd HH:mm") + " UTC";
        _fileInfo.Text = $"cards/{_card.Id}.json    created {created}    modified {modified}";
    }

    private void Apply(Action change, bool refresh = false)
    {
        if (_loading)
        {
            return;
        }
        change();
        if (refresh)
        {
            LoadFromCard();
        }
        UpdateModelInfo();
        Edited?.Invoke();
    }

    private void ApplyIds()
    {
        var ids = new List<int>();
        foreach (string part in (_itemIds.Text ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out int id) || id < 0)
            {
                _idsError.Text = $"'{part}' is not an item id - the card keeps its previous ids until this is fixed.";
                _idsError.IsVisible = true;
                return;
            }
            ids.Add(id);
        }
        _idsError.IsVisible = false;
        _card.Item.Ids = ids;
    }

    private async Task BrowseModelAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }
        var options = new FilePickerOpenOptions
        {
            Title = "Choose the 3D model for this card",
            AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("glTF model (.glb, .gltf)") { Patterns = new[] { "*.glb", "*.gltf" } } },
        };
        string models = Path.Combine(_workspace.Root, "models");
        if (Directory.Exists(models))
        {
            options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(new Uri(models));
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(options);
        string? path = files.Count > 0 ? files[0].TryGetLocalPath() : null;
        if (path is null)
        {
            return;
        }
        BindModel(path);
    }

    private void Rebind()
    {
        if (!string.IsNullOrEmpty(_card.Model.Path))
        {
            BindModel(_card.Model.Path);
        }
    }

    private void BindModel(string path)
    {
        string? error = ModelBinding.Bind(_card, _workspace, path);
        if (error is not null)
        {
            _modelDetail.Text = error;
            return;
        }
        _modelPath.Text = _card.Model.Path;
        UpdateModelInfo();
        Edited?.Invoke();
    }

    private void UpdateModelInfo()
    {
        var status = ModelBinding.Status(_card.Model, _workspace);
        var (text, brush) = CardGalleryView.StatusLabel(status);
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center }
            .Res(Avalonia.Controls.Shapes.Shape.FillProperty, brush);
        _modelStatus.Children.Clear();
        _modelStatus.Children.Add(dot);
        _modelStatus.Children.Add(Ui.Text(text, "hint"));

        if (status == ModelStatus.None)
        {
            _modelDetail.Text = "Pick a .glb or .gltf. Files inside the workspace (models/) are stored relative, so the card works on any machine.";
            return;
        }
        string full = _workspace.ToFullPath(_card.Model.Path);
        var lines = new List<string>();
        if (File.Exists(full))
        {
            lines.Add($"{new FileInfo(full).Length / 1024.0 / 1024.0:0.0} MB    sha256 {Short(_card.Model.Sha256)}");
        }
        if (!_workspace.IsInside(_card.Model.Path))
        {
            lines.Add("Outside the workspace - the path is absolute, so this card only works on this machine. Copy the file into models/ and re-bind it.");
        }
        if (status == ModelStatus.Changed)
        {
            lines.Add("The file no longer matches the hash recorded when it was bound. Rehash once the change is intended.");
        }
        _modelDetail.Text = string.Join("\n", lines);
    }

    private static string Short(string hash) => hash.Length > 16 ? hash[..16] + "..." : hash;

    private Border BuildPresentationTabs()
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(Tab("Details", null, active: true));
        foreach (var (name, phase) in PresentationTabs)
        {
            strip.Children.Add(Tab(name, phase, active: false));
        }
        return new Border
        {
            Height = 32,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 0, 0, 0),
            Child = strip,
        }.Res(Border.BorderBrushProperty, "HbaBorderBrush");
    }

    private static Border Tab(string text, string? lockedPhase, bool active)
    {
        var label = Ui.Text(text, size: 12);
        label.VerticalAlignment = VerticalAlignment.Center;
        var tab = new Border { Child = label };
        tab.Classes.Add("tab");
        if (active)
        {
            tab.Classes.Add("active");
        }
        if (lockedPhase is not null)
        {
            tab.Classes.Add("locked");
            ToolTip.SetTip(tab, $"{text} arrives in {lockedPhase} of the Item Forge plan.");
        }
        return tab;
    }

    private static Control Field(string label, Control editor, string? hint = null)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*") };
        var text = Ui.Text(label, "label");
        grid.Children.Add(text);
        var right = new StackPanel { Spacing = 3, Children = { editor } };
        if (hint is not null)
        {
            right.Children.Add(Ui.Text(hint, "hint"));
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        text.VerticalAlignment = VerticalAlignment.Top;
        text.Margin = new Thickness(0, 7, 0, 0);
        return grid;
    }

    private static void AddAt(Grid grid, Control child, int column, Thickness margin)
    {
        child.Margin = margin;
        Grid.SetColumn(child, column);
        grid.Children.Add(child);
    }
}

internal static class LayoutExtensions
{
    public static T WithTopMargin<T>(this T control, double top) where T : Control
    {
        control.Margin = new Thickness(control.Margin.Left, top, control.Margin.Right, control.Margin.Bottom);
        return control;
    }
}

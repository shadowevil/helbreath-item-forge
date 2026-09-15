using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ItemForge.Core;
using ItemForge.Core.Cards;

namespace ItemForge.App.Views;

// Edits one card's working copy. Details holds what describes the item (name, type, model file, notes);
// Model is the live 3D view and the base fix-up. The presentation tabs are shown locked with the phase that
// brings them. Every edit writes straight into the working copy and raises Edited; saving is the window's job.
public sealed class CardEditorView : UserControl
{
    public const string DetailsTab = "Details";
    public const string ModelTab = "Model";

    private static readonly (string Name, string Phase)[] LockedTabs =
    {
        ("Worn", "phase 5"), ("Equip", "phase 4"), ("Inventory", "phase 6"), ("Ground", "phase 6"),
    };

    private readonly Card _card;
    private readonly Workspace _workspace;
    private readonly TextBox _name = new() { FontSize = 13 };
    private readonly ComboBox _itemType = new() { FontSize = 13, MinWidth = 220 };
    private readonly TextBox _modelPath = new() { FontSize = 13, IsReadOnly = true, Watermark = "no model bound" };
    private readonly TextBox _notes = new() { FontSize = 13, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 120 };
    private readonly StackPanel _modelStatus = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly TextBlock _modelDetail = Ui.Text("", "hint");
    private readonly TextBlock _fileInfo = Ui.Text("", "hint");
    private readonly List<string> _typeItems = new(ItemTypes.All);
    private readonly Dictionary<string, Border> _tabs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ContentControl _host = new();
    private readonly Control _details;
    private ModelView? _modelView;
    private string _currentTab = DetailsTab;
    private bool _loading;

    public event Action? Edited;

    public CardEditorView(Card card, Workspace workspace)
    {
        _card = card;
        _workspace = workspace;

        _name.TextChanged += (_, _) => Apply(() => _card.Name = _name.Text ?? "");
        _notes.TextChanged += (_, _) => Apply(() => _card.Notes = _notes.Text ?? "");
        _itemType.ItemsSource = _typeItems;
        _itemType.SelectionChanged += (_, _) => Apply(() => _card.ItemType = _itemType.SelectedItem as string ?? "");

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
        form.Children.Add(Field("Item type", _itemType, "Picks the pose baseline the Worn presentation starts from."));
        form.Children.Add(Ui.Text("MODEL", "section").WithTopMargin(10));
        form.Children.Add(Field("Model file", modelRow));
        form.Children.Add(new StackPanel { Spacing = 3, Margin = new Thickness(140, 0, 0, 0), Children = { _modelStatus, _modelDetail } });
        form.Children.Add(Ui.Text("NOTES", "section").WithTopMargin(10));
        form.Children.Add(_notes);
        form.Children.Add(_fileInfo.WithTopMargin(6));
        _details = new ScrollViewer { Content = form };

        var root = new DockPanel();
        var tabs = BuildTabStrip();
        DockPanel.SetDock(tabs, Dock.Top);
        root.Children.Add(tabs);
        root.Children.Add(_host);
        Content = root;

        LoadFromCard();
        ShowTab(DetailsTab);
    }

    public string CardId => _card.Id;

    public string CurrentTab => _currentTab;

    public ModelView? Model => _modelView;

    // Switches tabs; the Model tab is built on first use so opening a card never pays for loading a GLB.
    public bool ShowTab(string tab)
    {
        if (!_tabs.ContainsKey(tab))
        {
            return false;
        }
        _currentTab = _tabs.Keys.First(k => string.Equals(k, tab, StringComparison.OrdinalIgnoreCase));
        foreach (var (name, border) in _tabs)
        {
            border.Classes.Set("active", string.Equals(name, _currentTab, StringComparison.OrdinalIgnoreCase));
        }
        if (string.Equals(_currentTab, ModelTab, StringComparison.OrdinalIgnoreCase))
        {
            _host.Content = EnsureModelView();
        }
        else
        {
            _host.Content = _details;
        }
        return true;
    }

    public ModelView EnsureModelView()
    {
        if (_modelView is null)
        {
            _modelView = new ModelView(_card, _workspace);
            _modelView.Edited += () => Edited?.Invoke();
        }
        return _modelView;
    }

    // Re-reads every field from the working copy (after an MCP edit or a save).
    public void LoadFromCard()
    {
        _loading = true;
        try
        {
            _name.Text = _card.Name;
            _notes.Text = _card.Notes;
            if (!string.IsNullOrEmpty(_card.ItemType) && !_typeItems.Contains(_card.ItemType))
            {
                _typeItems.Add(_card.ItemType);
                _itemType.ItemsSource = null;
                _itemType.ItemsSource = _typeItems;
            }
            _itemType.SelectedItem = string.IsNullOrEmpty(_card.ItemType) ? null : _card.ItemType;
            _modelPath.Text = _card.Model.Path;
        }
        finally
        {
            _loading = false;
        }
        UpdateModelInfo();
        UpdateFileInfo();
        _modelView?.Reload();
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
        _modelView?.Reload();
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

    private Border BuildTabStrip()
    {
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        strip.Children.Add(Tab(DetailsTab, null));
        strip.Children.Add(Tab(ModelTab, null));
        foreach (var (name, phase) in LockedTabs)
        {
            strip.Children.Add(Tab(name, phase));
        }
        return new Border
        {
            Height = 32,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(12, 0, 0, 0),
            Child = strip,
        }.Res(Border.BorderBrushProperty, "HbaBorderBrush");
    }

    private Border Tab(string text, string? lockedPhase)
    {
        var label = Ui.Text(text, size: 12);
        label.VerticalAlignment = VerticalAlignment.Center;
        var tab = new Border { Child = label };
        tab.Classes.Add("tab");
        if (lockedPhase is not null)
        {
            tab.Classes.Add("locked");
            ToolTip.SetTip(tab, $"{text} arrives in {lockedPhase} of the Item Forge plan.");
            return tab;
        }
        _tabs[text] = tab;
        tab.PointerPressed += (_, _) => ShowTab(text);
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

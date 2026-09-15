using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ItemForge.Core;
using ItemForge.Core.Cards;
using ItemForge.Core.Model;
using ItemForge.Core.Render;

namespace ItemForge.App.Views;

// The Model tab: the card's model in the game camera, with the base fix-up (scale, orientation) and the
// light that every presentation inherits. The live view and the bake go through the same renderer, so the
// only difference here is zoom - no supersampling and no pixel-art pass.
public sealed class ModelView : UserControl
{
    private static readonly string[] DirectionNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    // The frame the sprite is judged in: big enough for a two-handed weapon at game scale.
    public const int FrameSize = 160;

    // Scale that makes a model fill about 80% of that frame - the starting point for a newly bound model.
    public static float FitScale(ModelGeometry model) =>
        model.Extent > 0 ? FrameSize * 0.8f / (GameCamera.DefaultPixelsPerUnit * model.Extent) : 1f;

    private readonly Card _card;
    private readonly Workspace _workspace;
    private readonly ModelSurface _surface = new();
    private readonly TextBlock _cost = Ui.Text("", "hint");
    private readonly TextBlock _geometry = Ui.Text("", "hint");
    private readonly TextBlock _error = Ui.Text("", "hint");
    private readonly StackPanel _directionButtons = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<Border> _directionTabs = new();
    private ModelGeometry? _model;
    private int _direction;
    private bool _loading;

    public event Action? Edited;

    public ModelView(Card card, Workspace workspace)
    {
        _card = card;
        _workspace = workspace;

        var inspector = new StackPanel { Spacing = 9, Width = 268, Margin = new Thickness(14, 14, 14, 14) };
        inspector.Children.Add(Ui.Text("CAMERA", "section"));
        inspector.Children.Add(BuildDirections());
        inspector.Children.Add(Row("Zoom", ViewNumeric(_surface.Zoom, 1, 8, 1, v =>
        {
            _surface.Zoom = (int)v;
            Rerender();
        })));

        inspector.Children.Add(Ui.Text("FIT", "section"));
        var scale = CardNumeric(() => (decimal)Setup.Scale, 0.001m, 1000m, 0.05m, v => Setup.Scale = (float)v, "0.###");
        var fit = Ui.DialogButton("Fit to frame", (_, _) =>
        {
            if (_model is not null)
            {
                Apply(() => Setup.Scale = FitScale(_model));
                Reload();
            }
        });
        ToolTip.SetTip(fit, "Set the scale so the model fills about 80% of the frame.");
        fit.HorizontalAlignment = HorizontalAlignment.Left;
        inspector.Children.Add(Row("Scale", scale));
        inspector.Children.Add(Row("", fit));

        inspector.Children.Add(Ui.Text("ORIENTATION", "section"));
        inspector.Children.Add(Row("Rotate X", CardNumeric(() => (decimal)Setup.Rotation.X, -360, 360, 5, v => Setup.Rotation.X = (float)v)));
        inspector.Children.Add(Row("Rotate Y", CardNumeric(() => (decimal)Setup.Rotation.Y, -360, 360, 5, v => Setup.Rotation.Y = (float)v)));
        inspector.Children.Add(Row("Rotate Z", CardNumeric(() => (decimal)Setup.Rotation.Z, -360, 360, 5, v => Setup.Rotation.Z = (float)v)));
        inspector.Children.Add(Ui.Text("Degrees, applied X then Y then Z. This is the base fix-up every presentation starts from.", "hint"));

        inspector.Children.Add(Ui.Text("LIGHT", "section"));
        inspector.Children.Add(Row("Yaw", CardNumeric(() => (decimal)Setup.LightYaw, -180, 180, 5, v => Setup.LightYaw = (float)v)));
        inspector.Children.Add(Row("Pitch", CardNumeric(() => (decimal)Setup.LightPitch, -90, 90, 5, v => Setup.LightPitch = (float)v)));
        inspector.Children.Add(Row("Ambient", CardNumeric(() => (decimal)Setup.Ambient, 0, 1, 0.05m, v => Setup.Ambient = (float)v, "0.00")));
        inspector.Children.Add(Ui.Text("The light is fixed to the screen, so turning the character never swings the highlight.", "hint"));

        inspector.Children.Add(Ui.Text("MODEL", "section"));
        inspector.Children.Add(_geometry);
        inspector.Children.Add(_cost);
        inspector.Children.Add(_error);

        var right = new Border
        {
            BorderThickness = new Thickness(1, 0, 0, 0),
            Child = new ScrollViewer { Content = inspector },
        }.Res(Border.BorderBrushProperty, "HbaBorderBrush");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(_surface);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        Content = grid;

        _surface.PropertyChanged += (_, e) =>
        {
            if (e.Property == BoundsProperty)
            {
                Rerender();
            }
        };

        Reload();
    }

    public int Direction => _direction;

    public int Zoom => _surface.Zoom;

    public RenderStats? LastStats { get; private set; }

    public ModelGeometry? Geometry => _model;

    public string? LoadError { get; private set; }

    private ModelSetup Setup => _card.Model.Setup;

    public void SetDirection(int direction)
    {
        _direction = ((direction % GameCamera.Directions) + GameCamera.Directions) % GameCamera.Directions;
        for (int i = 0; i < _directionTabs.Count; i++)
        {
            _directionTabs[i].Classes.Set("active", i == _direction);
        }
        Rerender();
    }

    public void SetZoom(int zoom)
    {
        _surface.Zoom = zoom;
        Rerender();
    }

    // Re-reads the card (after an MCP edit) and reloads the model file if the binding changed.
    public void Reload()
    {
        _loading = true;
        try
        {
            RebuildInspectorValues();
        }
        finally
        {
            _loading = false;
        }
        LoadModel();
        Rerender();
    }

    private void LoadModel()
    {
        LoadError = null;
        _model = null;
        if (string.IsNullOrEmpty(_card.Model.Path))
        {
            LoadError = "No model bound. Pick one on the Details tab.";
        }
        else
        {
            try
            {
                _model = ModelCache.Load(_workspace.ToFullPath(_card.Model.Path));
            }
            catch (Exception ex)
            {
                LoadError = ex.Message;
            }
        }

        _error.Text = LoadError ?? "";
        _geometry.Text = _model is null
            ? ""
            : $"{_model.TriangleCount} tris, {_model.VertexCount} verts, {_model.Primitives.Count} primitive(s)\n" +
              $"size {_model.Size.X:0.##} x {_model.Size.Y:0.##} x {_model.Size.Z:0.##}    loaded in {_model.LoadMs:0} ms\n" +
              TextureLine(_model);
    }

    private static string TextureLine(ModelGeometry model)
    {
        var texture = model.Primitives.Select(p => p.Material.BaseColorTexture).FirstOrDefault(t => t is not null);
        if (texture is null)
        {
            return "no base colour texture";
        }
        string reduced = texture.SourceWidth > texture.Width ? $" (from {texture.SourceWidth}x{texture.SourceHeight})" : "";
        return $"texture {texture.Width}x{texture.Height}{reduced}";
    }

    private void Rerender()
    {
        if (_model is null)
        {
            _surface.Clear();
            _cost.Text = "";
            LastStats = null;
            return;
        }

        var result = ModelRenderer.Render(BuildRequest(FrameSize, FrameSize, supersample: 1, _direction));
        LastStats = result.Stats;
        _surface.Show(result.Image);
        _cost.Text = result.Stats.CostLine + $"\npivot ({result.Stats.PivotX}, {result.Stats.PivotY}) from the anchor";
    }

    // The one place a card turns into a render request, so the live view and any MCP render agree.
    public RenderRequest BuildRequest(int width, int height, int supersample, int direction) => new()
    {
        Geometry = _model ?? throw new InvalidOperationException("no model loaded"),
        Camera = new GameCamera().WithDirection(direction),
        Width = width,
        Height = height,
        Supersample = supersample,
        Scale = Setup.Scale,
        RotationDegrees = new Vector3(Setup.Rotation.X, Setup.Rotation.Y, Setup.Rotation.Z),
        LightYawDegrees = Setup.LightYaw,
        LightPitchDegrees = Setup.LightPitch,
        Ambient = Setup.Ambient,
    };

    private Control BuildDirections()
    {
        _directionButtons.Children.Clear();
        _directionTabs.Clear();
        for (int i = 0; i < DirectionNames.Length; i++)
        {
            int index = i;
            var label = Ui.Text(DirectionNames[i], size: 10.5);
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            var tab = new Border { Width = 31, Height = 24, Padding = new Thickness(0), Child = label };
            tab.Classes.Add("tab");
            tab.Classes.Set("active", i == _direction);
            tab.PointerPressed += (_, _) => SetDirection(index);
            ToolTip.SetTip(tab, $"Facing {index} ({DirectionNames[index]})");
            _directionTabs.Add(tab);
            _directionButtons.Children.Add(tab);
        }
        return _directionButtons;
    }

    private void RebuildInspectorValues()
    {
        // The numeric boxes are created from the card once; an MCP edit refreshes them through here.
        foreach (var (control, read) in _numerics)
        {
            control.Value = read();
        }
    }

    private readonly List<(NumericUpDown Control, Func<decimal> Read)> _numerics = new();

    // A box bound to a card value. It registers how to re-read that value, so an edit arriving over MCP
    // refreshes the inspector instead of leaving stale numbers on screen.
    private NumericUpDown CardNumeric(Func<decimal> read, decimal min, decimal max, decimal step, Action<decimal> set, string format = "0.#")
    {
        var box = ViewNumeric(read(), min, max, step, v => Apply(() => set(v)), format);
        _numerics.Add((box, read));
        return box;
    }

    private NumericUpDown ViewNumeric(decimal value, decimal min, decimal max, decimal step, Action<decimal> set, string format = "0.#")
    {
        var box = new NumericUpDown
        {
            Value = value,
            Minimum = min,
            Maximum = max,
            Increment = step,
            FormatString = format,
            FontSize = 12,
            Width = 118,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        box.ValueChanged += (_, e) =>
        {
            if (!_loading && e.NewValue is { } v)
            {
                set(v);
            }
        };
        return box;
    }

    private void Apply(Action change)
    {
        if (_loading)
        {
            return;
        }
        change();
        Rerender();
        Edited?.Invoke();
    }

    private static Control Row(string label, params Control[] editors)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("66,*") };
        var text = Ui.Text(label, "label");
        text.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(text);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var editor in editors)
        {
            row.Children.Add(editor);
        }
        Grid.SetColumn(row, 1);
        grid.Children.Add(row);
        return grid;
    }
}

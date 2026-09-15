using System.Numerics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ItemForge.Core;
using ItemForge.Core.Cards;
using ItemForge.Core.Model;
using ItemForge.Core.Render;

namespace ItemForge.App.Views;

// The Model tab: the card's model in the game camera, with the base fix-up (scale, orientation, offset) and
// the light that every presentation inherits.
//
// Two cameras, and the difference matters. GAME is the bake camera - fixed elevation, eight facings - and in
// it the viewport is pixel-exact: the live view renders at game scale and magnifies by whole pixels, so what
// is on screen is what a bake writes. FREE orbits for inspection only and never reaches an output pixel.
//
// Manipulation follows Blender: middle-drag orbits, shift-middle pans, the wheel zooms, and G / R / S start
// a modal move, rotate or scale with X / Y / Z to constrain, Ctrl to snap, Shift for fine, Esc to cancel.
public sealed class ModelView : UserControl
{
    private static readonly string[] DirectionNames = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

    // The sprite frame the item is judged in: big enough for a two-handed weapon at game scale. The viewport
    // is no longer limited to it - it is drawn as a guide.
    public const int FrameSize = 160;

    // Scale that makes a model fill about 80% of that frame - the starting point for a newly bound model.
    public static float FitScale(ModelGeometry model) =>
        model.Extent > 0 ? FrameSize * 0.8f / (GameCamera.DefaultPixelsPerUnit * model.Extent) : 1f;

    // Two deliberate lines: camera on top, object below, so it never wraps into a ragged tail.
    private const string Hint =
        "MMB orbit  -  Shift+MMB pan  -  wheel zoom  -  arrows turn  -  Home reset view\n" +
        "G move  -  R rotate  -  S scale  -  X/Y/Z axis  -  Ctrl snap  -  Ctrl+wheel scale  -  Esc cancel";

    private enum CameraMode { Game, Free }

    private enum OpKind { None, Move, Rotate, Scale }

    private readonly Card _card;
    private readonly Workspace _workspace;
    private readonly ModelSurface _surface = new();
    private readonly TextBlock _cost = Ui.Text("", "hint");
    private readonly TextBlock _geometry = Ui.Text("", "hint");
    private readonly TextBlock _error = Ui.Text("", "hint");
    private readonly TextBlock _opStatus = Ui.Text("", size: 12);
    private readonly TextBlock _cameraBadge = Ui.Text("", "hint");
    private readonly StackPanel _directionButtons = new() { Orientation = Orientation.Horizontal, Spacing = 2 };
    private readonly List<Border> _directionTabs = new();
    private readonly Dictionary<CameraMode, Border> _modeTabs = new();
    private readonly List<(NumericUpDown Control, Func<decimal> Read)> _numerics = new();

    private ModelGeometry? _model;
    private int _direction;
    private bool _loading;

    private CameraMode _mode = CameraMode.Game;
    private float _freeYaw;
    private float _freePitch = GameCamera.DefaultElevationDegrees;
    private float _freeZoom = 1f;
    private double _panX;
    private double _panY;

    private OpKind _op = OpKind.None;
    private Point _opStart;
    private Point _lastPointer;
    private char _opAxis;
    private (float Scale, float RotX, float RotY, float RotZ, float OffX, float OffY) _opSnapshot;
    private Point _dragLast;
    private bool _orbiting;
    private bool _panning;

    public event Action? Edited;

    public ModelView(Card card, Workspace workspace)
    {
        _card = card;
        _workspace = workspace;
        Focusable = true;
        _surface.FrameGuide = FrameSize;

        var inspector = new StackPanel { Spacing = 9, Width = 268, Margin = new Thickness(14, 14, 14, 14) };
        inspector.Children.Add(Ui.Text("CAMERA", "section"));
        inspector.Children.Add(BuildCameraModes());
        inspector.Children.Add(BuildDirections());
        inspector.Children.Add(Row("Zoom", ViewNumeric(_surface.Zoom, 1, 16, 1, v =>
        {
            _surface.Zoom = (int)v;
            Rerender();
        })));
        var reset = Ui.DialogButton("Reset view", (_, _) => ResetView());
        ToolTip.SetTip(reset, "Back to the game camera, centred, at zoom 2.");
        reset.HorizontalAlignment = HorizontalAlignment.Left;
        inspector.Children.Add(Row("", reset));

        inspector.Children.Add(Ui.Text("PLACEMENT", "section"));
        inspector.Children.Add(Row("Scale", CardNumeric(() => (decimal)Setup.Scale, 0.001m, 1000m, 0.05m, v => Setup.Scale = (float)v, "0.###")));
        var fit = Ui.DialogButton("Fit to frame", (_, _) =>
        {
            if (_model is not null)
            {
                Apply(() => Setup.Scale = FitScale(_model));
                SyncInspector();
            }
        });
        ToolTip.SetTip(fit, "Set the scale so the model fills about 80% of the sprite frame.");
        fit.HorizontalAlignment = HorizontalAlignment.Left;
        inspector.Children.Add(Row("", fit));
        inspector.Children.Add(Row("Offset X", CardNumeric(() => (decimal)Setup.Offset.X, -2000, 2000, 1, v => Setup.Offset.X = (float)v, "0.##")));
        inspector.Children.Add(Row("Offset Y", CardNumeric(() => (decimal)Setup.Offset.Y, -2000, 2000, 1, v => Setup.Offset.Y = (float)v, "0.##")));
        inspector.Children.Add(Ui.Text("Sprite pixels from the anchor. Moving the model moves its derived pivot by the same amount.", "hint"));

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

        _opStatus.IsVisible = false;
        _opStatus.Margin = new Thickness(12, 10, 0, 0);
        _opStatus.HorizontalAlignment = HorizontalAlignment.Left;
        _opStatus.VerticalAlignment = VerticalAlignment.Top;
        _opStatus.Res(TextBlock.ForegroundProperty, "HbaAccentBrush");

        _cameraBadge.IsVisible = false;
        _cameraBadge.Margin = new Thickness(0, 10, 14, 0);
        _cameraBadge.HorizontalAlignment = HorizontalAlignment.Right;
        _cameraBadge.VerticalAlignment = VerticalAlignment.Top;
        _cameraBadge.TextWrapping = TextWrapping.Wrap;
        _cameraBadge.MaxWidth = 220;
        _cameraBadge.TextAlignment = TextAlignment.Right;

        var hint = Ui.Text(Hint, "hint");
        hint.Margin = new Thickness(12, 0, 12, 8);
        hint.HorizontalAlignment = HorizontalAlignment.Left;
        hint.VerticalAlignment = VerticalAlignment.Bottom;

        var viewport = new Grid();
        viewport.Children.Add(_surface);
        viewport.Children.Add(_opStatus);
        viewport.Children.Add(_cameraBadge);
        viewport.Children.Add(hint);

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(viewport);
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
        _surface.PointerPressed += OnPointerPressed;
        _surface.PointerMoved += OnPointerMoved;
        _surface.PointerReleased += OnPointerReleased;
        _surface.PointerWheelChanged += OnWheel;
        KeyDown += OnKeyDown;

        Reload();
    }

    public int Direction => _direction;

    public int Zoom => _surface.Zoom;

    public string CameraModeName => _mode == CameraMode.Free ? "free" : "game";

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

    public bool SetCameraMode(string mode)
    {
        if (string.Equals(mode, "game", StringComparison.OrdinalIgnoreCase))
        {
            SetMode(CameraMode.Game);
            return true;
        }
        if (string.Equals(mode, "free", StringComparison.OrdinalIgnoreCase))
        {
            SetMode(CameraMode.Free);
            return true;
        }
        return false;
    }

    // Back to the bake camera, centred, at a sane zoom - the way out of any view you have orbited into.
    public void ResetView()
    {
        _freeYaw = 0;
        _freePitch = GameCamera.DefaultElevationDegrees;
        _freeZoom = 1f;
        _panX = _panY = 0;
        _surface.Zoom = 2;
        SetMode(CameraMode.Game);
    }

    // Re-reads the card (after an MCP edit) and reloads the model file if the binding changed.
    public void Reload()
    {
        SyncInspector();
        LoadModel();
        Rerender();
    }

    // --- rendering ---------------------------------------------------------------------

    private void Rerender()
    {
        if (_model is null)
        {
            _surface.Clear();
            _cost.Text = "";
            LastStats = null;
            return;
        }

        // The buffer covers the whole viewport at the current magnification, so nothing clips at the edge of
        // a fixed frame the way it did when the preview was locked to the sprite size.
        int zoom = _surface.Zoom;
        int width = Math.Clamp((int)Math.Ceiling(Math.Max(_surface.Bounds.Width, 64) / zoom), 32, 2048);
        int height = Math.Clamp((int)Math.Ceiling(Math.Max(_surface.Bounds.Height, 64) / zoom), 32, 2048);

        var result = ModelRenderer.Render(BuildPreviewRequest(width, height));
        LastStats = result.Stats;
        _surface.FrameGuide = _mode == CameraMode.Game ? FrameSize : 0;
        _surface.Show(result.Image);
        _cost.Text = result.Stats.CostLine +
                     (_mode == CameraMode.Game
                         ? $"\npivot ({result.Stats.PivotX}, {result.Stats.PivotY}) from the anchor"
                         : "\nfree camera - not what a bake would write");
    }

    private RenderRequest BuildPreviewRequest(int width, int height)
    {
        int zoom = _surface.Zoom;
        var anchor = (X: width / 2 + (int)Math.Round(_panX / zoom), Y: height / 2 + (int)Math.Round(_panY / zoom));
        var request = BuildRequest(width, height, supersample: 1, _direction) with { Anchor = anchor };
        return _mode == CameraMode.Free
            ? request with
            {
                Camera = request.Camera with
                {
                    ElevationDegrees = _freePitch,
                    ExtraYawDegrees = _freeYaw,
                    PixelsPerUnit = GameCamera.DefaultPixelsPerUnit * _freeZoom,
                },
            }
            : request;
    }

    // The one place a card turns into a render request, so the live view and any MCP render agree. It is
    // always the GAME camera; only the live preview layers a free look on top.
    public RenderRequest BuildRequest(int width, int height, int supersample, int direction) => new()
    {
        Geometry = _model ?? throw new InvalidOperationException("no model loaded"),
        Camera = new GameCamera().WithDirection(direction),
        Width = width,
        Height = height,
        Supersample = supersample,
        Scale = Setup.Scale,
        RotationDegrees = new Vector3(Setup.Rotation.X, Setup.Rotation.Y, Setup.Rotation.Z),
        PixelOffset = new Vector2(Setup.Offset.X, Setup.Offset.Y),
        LightYawDegrees = Setup.LightYaw,
        LightPitchDegrees = Setup.LightPitch,
        Ambient = Setup.Ambient,
    };

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

    // --- camera ------------------------------------------------------------------------

    private void SetMode(CameraMode mode)
    {
        _mode = mode;
        foreach (var (key, tab) in _modeTabs)
        {
            tab.Classes.Set("active", key == mode);
        }
        _cameraBadge.IsVisible = mode == CameraMode.Free;
        _cameraBadge.Text = "free camera - inspection only; renders and bakes use the game camera";
        Rerender();
    }

    private void Orbit(Point position)
    {
        if (_mode != CameraMode.Free)
        {
            // Orbiting is what asks for the free camera, so asking for it switches.
            SetMode(CameraMode.Free);
        }
        var delta = position - _dragLast;
        _dragLast = position;
        _freeYaw = Wrap180(_freeYaw + (float)delta.X * 0.4f);
        _freePitch = Math.Clamp(_freePitch + (float)delta.Y * 0.4f, -89f, 89f);
        Rerender();
    }

    private void Pan(Point position)
    {
        var delta = position - _dragLast;
        _dragLast = position;
        _panX += delta.X;
        _panY += delta.Y;
        Rerender();
    }

    // --- modal move / rotate / scale ----------------------------------------------------

    private void BeginOp(OpKind kind)
    {
        if (_model is null)
        {
            return;
        }
        _op = kind;
        _opAxis = '\0';
        _opStart = _lastPointer;
        _opSnapshot = (Setup.Scale, Setup.Rotation.X, Setup.Rotation.Y, Setup.Rotation.Z, Setup.Offset.X, Setup.Offset.Y);
        _opStatus.IsVisible = true;
        UpdateOpStatus();
    }

    private void UpdateOp(Point position, KeyModifiers modifiers)
    {
        var delta = position - _opStart;
        bool snap = modifiers.HasFlag(KeyModifiers.Control);
        double gain = modifiers.HasFlag(KeyModifiers.Shift) ? 0.2 : 1.0;

        switch (_op)
        {
            case OpKind.Move:
            {
                double dx = _opAxis == 'y' ? 0 : delta.X / _surface.Zoom * gain;
                double dy = _opAxis == 'x' ? 0 : delta.Y / _surface.Zoom * gain;
                float x = _opSnapshot.OffX + (float)dx;
                float y = _opSnapshot.OffY + (float)dy;
                Setup.Offset.X = snap ? MathF.Round(x) : x;
                Setup.Offset.Y = snap ? MathF.Round(y) : y;
                break;
            }
            case OpKind.Rotate:
            {
                const double degreesPerPixel = 0.5;
                double amount = (delta.X - delta.Y) * degreesPerPixel * gain;
                float x = _opSnapshot.RotX, y = _opSnapshot.RotY, z = _opSnapshot.RotZ;
                switch (_opAxis)
                {
                    case 'x': x += (float)amount; break;
                    case 'y': y += (float)amount; break;
                    case 'z': z += (float)amount; break;
                    default:
                        // Unconstrained: horizontal turns the model, vertical tips it.
                        y += (float)(delta.X * degreesPerPixel * gain);
                        x += (float)(delta.Y * degreesPerPixel * gain);
                        break;
                }
                Setup.Rotation.X = Snap(Wrap180(x), snap, 5f);
                Setup.Rotation.Y = Snap(Wrap180(y), snap, 5f);
                Setup.Rotation.Z = Snap(Wrap180(z), snap, 5f);
                break;
            }
            case OpKind.Scale:
            {
                double factor = Math.Exp((delta.X - delta.Y) * 0.006 * gain);
                float value = Math.Max(0.001f, _opSnapshot.Scale * (float)factor);
                Setup.Scale = snap ? MathF.Max(0.001f, MathF.Round(value * 20f) / 20f) : value;
                break;
            }
            default:
                return;
        }

        Rerender();
        UpdateOpStatus();
    }

    private void EndOp(bool cancel)
    {
        if (_op == OpKind.None)
        {
            return;
        }
        if (cancel)
        {
            Setup.Scale = _opSnapshot.Scale;
            Setup.Rotation.X = _opSnapshot.RotX;
            Setup.Rotation.Y = _opSnapshot.RotY;
            Setup.Rotation.Z = _opSnapshot.RotZ;
            Setup.Offset.X = _opSnapshot.OffX;
            Setup.Offset.Y = _opSnapshot.OffY;
            Rerender();
        }
        _op = OpKind.None;
        _opAxis = '\0';
        _opStatus.IsVisible = false;
        SyncInspector();
        Edited?.Invoke();
    }

    private void UpdateOpStatus()
    {
        string axis = _opAxis == '\0' ? "" : $" [{char.ToUpperInvariant(_opAxis)}]";
        _opStatus.Text = _op switch
        {
            OpKind.Move => $"Move{axis}   X {Setup.Offset.X:0.##}  Y {Setup.Offset.Y:0.##}   -   X/Y axis, Ctrl snap, Esc cancel",
            OpKind.Rotate => $"Rotate{axis}   X {Setup.Rotation.X:0.#}  Y {Setup.Rotation.Y:0.#}  Z {Setup.Rotation.Z:0.#}   -   X/Y/Z axis, Ctrl 5 deg, Esc cancel",
            OpKind.Scale => $"Scale   {Setup.Scale:0.###}   -   Ctrl snap, Esc cancel",
            _ => "",
        };
    }

    // --- input -------------------------------------------------------------------------

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Focus();
        var point = e.GetCurrentPoint(_surface);
        _lastPointer = point.Position;

        if (_op != OpKind.None)
        {
            // Blender: left click confirms the modal op, right click throws it away.
            EndOp(cancel: point.Properties.IsRightButtonPressed);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsMiddleButtonPressed)
        {
            _dragLast = point.Position;
            _panning = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            _orbiting = !_panning;
            e.Pointer.Capture(_surface);
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(_surface);
        _lastPointer = position;
        if (_op != OpKind.None)
        {
            UpdateOp(position, e.KeyModifiers);
            return;
        }
        if (_orbiting)
        {
            Orbit(position);
        }
        else if (_panning)
        {
            Pan(position);
        }
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_orbiting || _panning)
        {
            _orbiting = _panning = false;
            e.Pointer.Capture(null);
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        double steps = e.Delta.Y;
        if (steps == 0)
        {
            return;
        }
        e.Handled = true;

        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Apply(() => Setup.Scale = Math.Max(0.001f, Setup.Scale * (float)Math.Pow(1.1, steps)));
            SyncInspector();
            return;
        }
        if (_mode == CameraMode.Free)
        {
            _freeZoom = Math.Clamp(_freeZoom * (float)Math.Pow(1.15, steps), 0.05f, 40f);
            Rerender();
            return;
        }
        SetZoom(_surface.Zoom + (steps > 0 ? 1 : -1));
        SyncInspector();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_op != OpKind.None)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    EndOp(cancel: true);
                    e.Handled = true;
                    return;
                case Key.Enter:
                    EndOp(cancel: false);
                    e.Handled = true;
                    return;
                case Key.X:
                case Key.Y:
                case Key.Z:
                {
                    char axis = e.Key == Key.X ? 'x' : e.Key == Key.Y ? 'y' : 'z';
                    _opAxis = _opAxis == axis ? '\0' : axis;
                    UpdateOp(_lastPointer, e.KeyModifiers);
                    e.Handled = true;
                    return;
                }
            }
            return;
        }

        switch (e.Key)
        {
            case Key.G:
                BeginOp(OpKind.Move);
                e.Handled = true;
                break;
            case Key.R:
                BeginOp(OpKind.Rotate);
                e.Handled = true;
                break;
            case Key.S when !e.KeyModifiers.HasFlag(KeyModifiers.Control):
                BeginOp(OpKind.Scale);
                e.Handled = true;
                break;
            case Key.Home:
                ResetView();
                SyncInspector();
                e.Handled = true;
                break;
            case Key.Left:
                SetDirection(_direction - 1);
                e.Handled = true;
                break;
            case Key.Right:
                SetDirection(_direction + 1);
                e.Handled = true;
                break;
        }
    }

    // --- inspector ---------------------------------------------------------------------

    private Control BuildCameraModes()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (mode, label) in new[] { (CameraMode.Game, "Game"), (CameraMode.Free, "Free") })
        {
            var captured = mode;
            var text = Ui.Text(label, size: 11.5);
            text.HorizontalAlignment = HorizontalAlignment.Center;
            text.VerticalAlignment = VerticalAlignment.Center;
            var tab = new Border { Width = 62, Height = 24, Padding = new Thickness(0), Child = text };
            tab.Classes.Add("tab");
            tab.Classes.Set("active", mode == _mode);
            tab.PointerPressed += (_, _) => SetMode(captured);
            ToolTip.SetTip(tab, mode == CameraMode.Game
                ? "The bake camera: fixed elevation, eight facings. The preview is pixel-exact."
                : "Orbit freely to inspect the model. Never used by a render or a bake.");
            _modeTabs[mode] = tab;
            row.Children.Add(tab);
        }
        return Row("Camera", row);
    }

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

    private void SyncInspector()
    {
        _loading = true;
        try
        {
            foreach (var (control, read) in _numerics)
            {
                control.Value = read();
            }
        }
        finally
        {
            _loading = false;
        }
    }

    // A box bound to a card value. It registers how to re-read that value, so an edit arriving over MCP - or
    // a drag in the viewport - refreshes the inspector instead of leaving stale numbers on screen.
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

    private static float Snap(float value, bool snap, float step) =>
        snap ? MathF.Round(value / step) * step : value;

    private static float Wrap180(float degrees)
    {
        degrees %= 360f;
        if (degrees > 180f)
        {
            degrees -= 360f;
        }
        if (degrees < -180f)
        {
            degrees += 360f;
        }
        return degrees;
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

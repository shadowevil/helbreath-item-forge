using ItemForge.App.Views;
using ItemForge.Core.Cards;
using ItemForge.Core.Render;

namespace ItemForge.App;

// The Model tab over MCP: read the loaded geometry, change the base fix-up, drive the live view, and render
// a frame off screen. Renders go through the same ModelView request builder the live view uses, so an agent
// and a person are looking at the same pixels.
public partial class MainWindow
{
    public Task<OpResult> GetModelInfoAsync(string? id) => OnUi(() =>
    {
        var (view, error) = RequireModelView(id, needsGeometry: false);
        if (view is null)
        {
            return OpResult.Fail(error!);
        }
        var setup = _openCard!.Model.Setup;
        var model = view.Geometry;
        var texture = model?.Primitives.Select(p => p.Material.BaseColorTexture).FirstOrDefault(t => t is not null);
        return OpResult.Success(new
        {
            id = _openCard.Id,
            modelPath = _openCard.Model.Path,
            modelStatus = ModelBinding.Status(_openCard.Model, _store!.Workspace).ToString(),
            loaded = model is not null,
            error = view.LoadError,
            triangles = model?.TriangleCount ?? 0,
            vertices = model?.VertexCount ?? 0,
            primitives = model?.Primitives.Count ?? 0,
            loadMs = model?.LoadMs ?? 0,
            bounds = model is null ? null : new
            {
                min = new { x = model.Min.X, y = model.Min.Y, z = model.Min.Z },
                max = new { x = model.Max.X, y = model.Max.Y, z = model.Max.Z },
                size = new { x = model.Size.X, y = model.Size.Y, z = model.Size.Z },
                extent = model.Extent,
            },
            texture = texture is null ? null : new
            {
                width = texture.Width,
                height = texture.Height,
                sourceWidth = texture.SourceWidth,
                sourceHeight = texture.SourceHeight,
            },
            setup = Describe(setup),
            view = new { tab = _editor!.CurrentTab, direction = view.Direction, zoom = view.Zoom, camera = view.CameraModeName },
            lastRender = view.LastStats is null ? null : Describe(view.LastStats),
        });
    });

    public Task<OpResult> UpdateModelSetupAsync(ModelSetupUpdate update) => OnUi(() =>
    {
        var (view, error) = RequireModelView(update.Id, needsGeometry: update.Fit);
        if (view is null)
        {
            return OpResult.Fail(error!);
        }

        // Validate everything before changing anything, as update_card does.
        if (update.Scale is { } scale && (scale <= 0f || !float.IsFinite(scale)))
        {
            return OpResult.Fail("scale must be greater than 0");
        }
        if (update.Ambient is { } ambient && (ambient < 0f || ambient > 1f))
        {
            return OpResult.Fail("ambient must be between 0 and 1");
        }
        if (update.LightPitch is { } pitch && (pitch < -90f || pitch > 90f))
        {
            return OpResult.Fail("lightPitch must be between -90 and 90");
        }
        foreach (var (name, value) in new[]
                 {
                     ("rotationX", update.RotationX), ("rotationY", update.RotationY), ("rotationZ", update.RotationZ),
                     ("lightYaw", update.LightYaw), ("offsetX", update.OffsetX), ("offsetY", update.OffsetY),
                 })
        {
            if (value is { } v && !float.IsFinite(v))
            {
                return OpResult.Fail($"{name} must be a finite number");
            }
        }
        if (update.Fit && view.Geometry is null)
        {
            return OpResult.Fail(view.LoadError ?? "no model is loaded");
        }

        var setup = _openCard!.Model.Setup;
        if (update.Fit) setup.Scale = ModelView.FitScale(view.Geometry!);
        if (update.Scale is { } s) setup.Scale = s;
        if (update.OffsetX is { } ox) setup.Offset.X = ox;
        if (update.OffsetY is { } oy) setup.Offset.Y = oy;
        if (update.RotationX is { } rx) setup.Rotation.X = rx;
        if (update.RotationY is { } ry) setup.Rotation.Y = ry;
        if (update.RotationZ is { } rz) setup.Rotation.Z = rz;
        if (update.LightYaw is { } ly) setup.LightYaw = ly;
        if (update.LightPitch is { } lp) setup.LightPitch = lp;
        if (update.Ambient is { } a) setup.Ambient = a;

        view.Reload();
        UpdateChrome();
        return OpResult.Success(new { id = _openCard.Id, dirty = IsDirty, setup = Describe(setup) });
    });

    public Task<OpResult> SetCardTabAsync(string tab) => OnUi(() =>
    {
        if (_editor is null)
        {
            return OpResult.Fail("no card is open");
        }
        if (!_editor.ShowTab(tab))
        {
            return OpResult.Fail($"unknown or locked tab '{tab}' (open tabs: {CardEditorView.DetailsTab}, {CardEditorView.ModelTab})");
        }
        ShowOpenCard();
        return OpResult.Success(new { id = _openCard!.Id, tab = _editor.CurrentTab });
    });

    public Task<OpResult> SetModelViewAsync(ModelViewUpdate update) => OnUi(() =>
    {
        var (view, error) = RequireModelView(null, needsGeometry: false);
        if (view is null)
        {
            return OpResult.Fail(error!);
        }
        if (update.Direction is { } d && (d < 0 || d >= GameCamera.Directions))
        {
            return OpResult.Fail($"direction must be 0..{GameCamera.Directions - 1} (0 = north, clockwise)");
        }
        if (update.Zoom is { } z && (z < 1 || z > 16))
        {
            return OpResult.Fail("zoom must be 1..16");
        }
        if (update.Mode is { Length: > 0 } mode && !mode.Equals("game", StringComparison.OrdinalIgnoreCase) && !mode.Equals("free", StringComparison.OrdinalIgnoreCase))
        {
            return OpResult.Fail("mode must be game or free");
        }

        // Reset first, so a call can reset and then point the view in one go.
        if (update.Reset) view.ResetView();
        if (update.Mode is { Length: > 0 } wanted) view.SetCameraMode(wanted);
        if (update.Direction is { } dir) view.SetDirection(dir);
        if (update.Zoom is { } factor) view.SetZoom(factor);
        return OpResult.Success(new { direction = view.Direction, zoom = view.Zoom, camera = view.CameraModeName });
    });

    public Task<OpResult> RenderModelAsync(RenderModelArgs args) => OnUi(() =>
    {
        var (view, error) = RequireModelView(args.Id, needsGeometry: true);
        if (view is null)
        {
            return OpResult.Fail(error!);
        }
        int size = Math.Clamp(args.Size ?? ModelView.FrameSize, 16, 1024);
        int supersample = Math.Clamp(args.Supersample ?? 4, 1, 8);
        int direction = args.Direction ?? view.Direction;
        if (direction < 0 || direction >= GameCamera.Directions)
        {
            return OpResult.Fail($"direction must be 0..{GameCamera.Directions - 1} (0 = north, clockwise)");
        }

        var result = ModelRenderer.Render(view.BuildRequest(size, size, supersample, direction));
        return OpResult.Success(new RenderPayload(
            Describe(result.Stats),
            Convert.ToBase64String(result.Image.EncodePng()),
            result.Image.Width,
            result.Image.Height));
    });

    // Opens the card if asked, then hands back its Model view (built on demand, exactly as clicking the tab
    // would). Returns the reason instead when that is not possible.
    private (ModelView? View, string? Error) RequireModelView(string? id, bool needsGeometry)
    {
        if (_store is null)
        {
            return (null, "no workspace is open");
        }
        if (id is not null && _openCard?.Id != id)
        {
            if (IsDirty)
            {
                return (null, $"card '{_openCard!.Id}' has unsaved changes - save or close it before working on '{id}'");
            }
            string? openError = OpenCardCore(id);
            if (openError is not null)
            {
                return (null, openError);
            }
        }
        if (_openCard is null || _editor is null)
        {
            return (null, "no card is open - pass 'id'");
        }
        var view = _editor.EnsureModelView();
        if (needsGeometry && view.Geometry is null)
        {
            return (null, view.LoadError ?? "no model is loaded");
        }
        return (view, null);
    }

    private static object Describe(ModelSetup setup) => new
    {
        scale = setup.Scale,
        offsetX = setup.Offset.X,
        offsetY = setup.Offset.Y,
        rotationX = setup.Rotation.X,
        rotationY = setup.Rotation.Y,
        rotationZ = setup.Rotation.Z,
        lightYaw = setup.LightYaw,
        lightPitch = setup.LightPitch,
        ambient = setup.Ambient,
    };

    private static RenderStatsInfo Describe(RenderStats s) => new(
        s.Triangles, s.TrianglesDrawn, s.Supersample, Math.Round(s.ElapsedMs, 2), s.OpaquePixels,
        s.X, s.Y, s.W, s.H, s.PivotX, s.PivotY, s.CostLine);
}

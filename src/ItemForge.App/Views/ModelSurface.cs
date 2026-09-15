using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ItemForge.Core.Render;

namespace ItemForge.App.Views;

// Shows a rendered frame at an integer zoom with NO smoothing, so what is on screen is exactly the pixels
// the bake would write - a placement judged here is the placement that ships.
public sealed class ModelSurface : Control
{
    private WriteableBitmap? _bitmap;
    private Framebuffer? _frame;
    private int _zoom = 2;

    public ModelSurface()
    {
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
    }

    public int Zoom
    {
        get => _zoom;
        set
        {
            int clamped = Math.Clamp(value, 1, 8);
            if (clamped != _zoom)
            {
                _zoom = clamped;
                InvalidateVisual();
            }
        }
    }

    // Draw a crosshair where the model origin sits; later phases put the character's foot anchor there.
    public bool ShowAnchor { get; set; } = true;

    public Framebuffer? Frame => _frame;

    public void Show(Framebuffer frame)
    {
        if (_bitmap is null || _bitmap.PixelSize.Width != frame.Width || _bitmap.PixelSize.Height != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(
                new PixelSize(frame.Width, frame.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        }
        using (var locked = _bitmap.Lock())
        {
            System.Runtime.InteropServices.Marshal.Copy(frame.Rgba, 0, locked.Address, frame.Rgba.Length);
        }
        _frame = frame;
        InvalidateVisual();
    }

    public void Clear()
    {
        _frame = null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (this.FindResource("HbaEditorBrush") is IBrush background)
        {
            context.FillRectangle(background, bounds);
        }
        if (_bitmap is null || _frame is null)
        {
            return;
        }

        double width = _frame.Width * _zoom, height = _frame.Height * _zoom;
        double left = Math.Round((bounds.Width - width) / 2);
        double top = Math.Round((bounds.Height - height) / 2);
        var dest = new Rect(left, top, width, height);
        context.DrawImage(_bitmap, new Rect(0, 0, _frame.Width, _frame.Height), dest);

        if (!ShowAnchor)
        {
            return;
        }
        var pen = new Pen(this.FindResource("HbaAccentBrush") as IBrush ?? Brushes.Goldenrod, 1);
        double ax = left + (_frame.AnchorX + 0.5) * _zoom;
        double ay = top + (_frame.AnchorY + 0.5) * _zoom;
        const double arm = 7;
        context.DrawLine(pen, new Point(ax - arm, ay), new Point(ax + arm, ay));
        context.DrawLine(pen, new Point(ax, ay - arm), new Point(ax, ay + arm));
    }
}

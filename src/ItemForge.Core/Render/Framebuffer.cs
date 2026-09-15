using SkiaSharp;

namespace ItemForge.Core.Render;

// Straight-alpha RGBA8 pixels, the one image type the renderer produces. The anchor is the pixel the model
// origin projects to, so a pivot can be derived without knowing how the buffer was sized.
public sealed class Framebuffer
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public int AnchorX { get; set; }
    public int AnchorY { get; set; }

    public Framebuffer(int width, int height)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Rgba = new byte[Width * Height * 4];
    }

    public int IndexOf(int x, int y) => (y * Width + x) * 4;

    // Tight box of the opaque pixels: x, y, w, h. Empty when nothing was drawn.
    public (int X, int Y, int W, int H) OpaqueBounds(byte alphaThreshold = 1)
    {
        int minX = Width, minY = Height, maxX = -1, maxY = -1;
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                if (Rgba[IndexOf(x, y) + 3] >= alphaThreshold)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
            }
        }
        return maxX < 0 ? (0, 0, 0, 0) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    public int OpaquePixelCount(byte alphaThreshold = 1)
    {
        int count = 0;
        for (int i = 3; i < Rgba.Length; i += 4)
        {
            if (Rgba[i] >= alphaThreshold)
            {
                count++;
            }
        }
        return count;
    }

    public Framebuffer Crop(int x, int y, int w, int h)
    {
        var crop = new Framebuffer(Math.Max(1, w), Math.Max(1, h)) { AnchorX = AnchorX - x, AnchorY = AnchorY - y };
        for (int row = 0; row < crop.Height; row++)
        {
            int sy = y + row;
            if (sy < 0 || sy >= Height)
            {
                continue;
            }
            for (int col = 0; col < crop.Width; col++)
            {
                int sx = x + col;
                if (sx < 0 || sx >= Width)
                {
                    continue;
                }
                Array.Copy(Rgba, IndexOf(sx, sy), crop.Rgba, crop.IndexOf(col, row), 4);
            }
        }
        return crop;
    }

    public byte[] EncodePng()
    {
        var info = new SKImageInfo(Width, Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bitmap = new SKBitmap(info);
        System.Runtime.InteropServices.Marshal.Copy(Rgba, 0, bitmap.GetPixels(), Rgba.Length);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    public void WritePng(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllBytes(path, EncodePng());
    }
}

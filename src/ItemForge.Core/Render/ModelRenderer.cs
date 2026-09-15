using System.Diagnostics;
using System.Numerics;
using ItemForge.Core.Model;

namespace ItemForge.Core.Render;

public sealed record RenderRequest
{
    public required ModelGeometry Geometry { get; init; }
    public GameCamera Camera { get; init; } = new();

    public int Width { get; init; } = 256;
    public int Height { get; init; } = 256;

    // 1 for the live view, 4 for a bake. The image is rendered this many times larger and boxed back down.
    public int Supersample { get; init; } = 1;

    // Card setup.
    public float Scale { get; init; } = 1f;
    public Vector3 RotationDegrees { get; init; }
    public Vector2 PixelOffset { get; init; }
    public float LightYawDegrees { get; init; } = -35f;
    public float LightPitchDegrees { get; init; } = 45f;
    public float Ambient { get; init; } = 0.35f;

    // Where the model origin lands in the image. Null centres it; later phases pass the foot anchor.
    public (int X, int Y)? Anchor { get; init; }
}

public sealed record RenderStats(
    int Triangles,
    int TrianglesDrawn,
    int Supersample,
    double ElapsedMs,
    int OpaquePixels,
    int X,
    int Y,
    int W,
    int H,
    int PivotX,
    int PivotY)
{
    // Every derived render states what it cost (project rule), in the same shape the bake will report.
    public string CostLine =>
        $"{TrianglesDrawn}/{Triangles} tris  x{Supersample}  {ElapsedMs:0.0} ms  {W}x{H} px";
}

public sealed record RenderResult(Framebuffer Image, RenderStats Stats);

// Software rasterizer, shared by the live view and (from phase 7) the bake. Software, not the GPU, because
// output must not vary by driver; the sprites are tiny, so speed is a non-issue.
public static class ModelRenderer
{
    public static RenderResult Render(RenderRequest request)
    {
        var sw = Stopwatch.StartNew();
        int ss = Math.Clamp(request.Supersample, 1, 8);
        int width = Math.Max(1, request.Width) * ss;
        int height = Math.Max(1, request.Height) * ss;

        var anchor = request.Anchor ?? (request.Width / 2, request.Height / 2);
        float anchorX = (anchor.X + request.PixelOffset.X) * ss;
        float anchorY = (anchor.Y + request.PixelOffset.Y) * ss;

        var geometry = request.Geometry;
        // The model is placed by its bounding-box centre until a card picks a grip point (phase 4).
        var model =
            Matrix4x4.CreateTranslation(-geometry.Center) *
            Matrix4x4.CreateScale(request.Scale) *
            Matrix4x4.CreateRotationX(Deg(request.RotationDegrees.X)) *
            Matrix4x4.CreateRotationY(Deg(request.RotationDegrees.Y)) *
            Matrix4x4.CreateRotationZ(Deg(request.RotationDegrees.Z));
        var toCamera = model * request.Camera.ViewMatrix;

        // The light is fixed to the SCREEN, not the world, so turning the character does not swing the
        // highlight around - that is how the 1999 sprites are lit.
        var light = LightDirection(request.LightYawDegrees, request.LightPitchDegrees);
        float ambient = Math.Clamp(request.Ambient, 0f, 1f);

        var color = new float[width * height * 4];
        var depth = new float[width * height];
        Array.Fill(depth, float.NegativeInfinity);

        int triangles = 0, drawn = 0;
        foreach (var prim in geometry.Primitives)
        {
            var material = prim.Material;
            var verts = prim.Vertices;
            for (int i = 0; i + 2 < prim.Indices.Length; i += 3)
            {
                triangles++;
                var a = Project(verts[prim.Indices[i]], toCamera, request.Camera, anchorX, anchorY, ss);
                var b = Project(verts[prim.Indices[i + 1]], toCamera, request.Camera, anchorX, anchorY, ss);
                var c = Project(verts[prim.Indices[i + 2]], toCamera, request.Camera, anchorX, anchorY, ss);
                if (RasterizeTriangle(a, b, c, material, light, ambient, color, depth, width, height))
                {
                    drawn++;
                }
            }
        }

        var full = new Framebuffer(width, height);
        WritePixels(color, full.Rgba);
        var image = ss == 1 ? full : Downsample(full, ss);
        image.AnchorX = anchor.X;
        image.AnchorY = anchor.Y;

        sw.Stop();
        var (bx, by, bw, bh) = image.OpaqueBounds();
        var stats = new RenderStats(
            triangles, drawn, ss, sw.Elapsed.TotalMilliseconds, image.OpaquePixelCount(),
            bx, by, bw, bh,
            bx - image.AnchorX, by - image.AnchorY);
        return new RenderResult(image, stats);
    }

    private readonly record struct Projected(float X, float Y, float Z, Vector3 Normal, Vector2 Uv);

    private static Projected Project(ModelVertex vertex, Matrix4x4 toCamera, GameCamera camera, float anchorX, float anchorY, int ss)
    {
        var cam = Vector3.Transform(vertex.Position, toCamera);
        var normal = Vector3.TransformNormal(vertex.Normal, toCamera);
        var pixels = camera.ToPixels(cam) * ss;
        return new Projected(pixels.X + anchorX, pixels.Y + anchorY, cam.Z, normal, vertex.Uv);
    }

    private static bool RasterizeTriangle(
        Projected a, Projected b, Projected c,
        ModelMaterial material, Vector3 light, float ambient,
        float[] color, float[] depth, int width, int height)
    {
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-9f)
        {
            return false;
        }

        int minX = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))));
        int maxX = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int minY = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))));
        int maxY = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (minX > maxX || minY > maxY)
        {
            return false;
        }

        float invArea = 1f / area;
        bool touched = false;
        for (int y = minY; y <= maxY; y++)
        {
            float py = y + 0.5f;
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f;
                float w0 = ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) * invArea;
                float w1 = ((px - a.X) * (c.Y - a.Y) - (py - a.Y) * (c.X - a.X)) * invArea;
                float u = w1, v = w0, w = 1f - u - v;
                if (u < 0f || v < 0f || w < 0f)
                {
                    continue;
                }

                // Orthographic projection is affine, so plain barycentric interpolation is exact here.
                float z = w * a.Z + u * b.Z + v * c.Z;
                int pixel = y * width + x;
                if (z <= depth[pixel])
                {
                    continue;
                }

                var normal = w * a.Normal + u * b.Normal + v * c.Normal;
                var uv = w * a.Uv + u * b.Uv + v * c.Uv;
                var rgba = material.BaseColor;
                if (material.BaseColorTexture is not null)
                {
                    rgba *= material.BaseColorTexture.Sample(uv.X, uv.Y);
                }
                if (rgba.W <= 0f)
                {
                    continue;
                }

                if (normal != Vector3.Zero)
                {
                    normal = Vector3.Normalize(normal);
                    // Two-sided lighting: a face turned away from the camera is still lit, never black.
                    if (normal.Z < 0f)
                    {
                        normal = -normal;
                    }
                }
                float lambert = normal == Vector3.Zero ? 1f : MathF.Max(0f, Vector3.Dot(normal, light));
                float shade = ambient + (1f - ambient) * lambert;

                depth[pixel] = z;
                int o = pixel * 4;
                color[o] = rgba.X * shade;
                color[o + 1] = rgba.Y * shade;
                color[o + 2] = rgba.Z * shade;
                color[o + 3] = rgba.W;
                touched = true;
            }
        }
        return touched;
    }

    private static Vector3 LightDirection(float yawDegrees, float pitchDegrees)
    {
        float yaw = Deg(yawDegrees), pitch = Deg(pitchDegrees);
        var dir = new Vector3(
            MathF.Sin(yaw) * MathF.Cos(pitch),
            MathF.Sin(pitch),
            MathF.Cos(yaw) * MathF.Cos(pitch));
        return dir == Vector3.Zero ? Vector3.UnitZ : Vector3.Normalize(dir);
    }

    private static void WritePixels(float[] color, byte[] rgba)
    {
        for (int i = 0; i < rgba.Length; i++)
        {
            rgba[i] = (byte)Math.Clamp((int)MathF.Round(color[i] * 255f), 0, 255);
        }
    }

    // Box filter back to 1:1. Colour is weighted by alpha so transparent pixels cannot bleed black into the
    // edges, which is what gives a clean silhouette to cut against later.
    private static Framebuffer Downsample(Framebuffer source, int factor)
    {
        var target = new Framebuffer(source.Width / factor, source.Height / factor);
        int samples = factor * factor;
        for (int y = 0; y < target.Height; y++)
        {
            for (int x = 0; x < target.Width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int sy = 0; sy < factor; sy++)
                {
                    for (int sx = 0; sx < factor; sx++)
                    {
                        int i = source.IndexOf(x * factor + sx, y * factor + sy);
                        float alpha = source.Rgba[i + 3] / 255f;
                        r += source.Rgba[i] * alpha;
                        g += source.Rgba[i + 1] * alpha;
                        b += source.Rgba[i + 2] * alpha;
                        a += alpha;
                    }
                }
                int o = target.IndexOf(x, y);
                if (a > 0f)
                {
                    target.Rgba[o] = (byte)Math.Clamp((int)MathF.Round(r / a), 0, 255);
                    target.Rgba[o + 1] = (byte)Math.Clamp((int)MathF.Round(g / a), 0, 255);
                    target.Rgba[o + 2] = (byte)Math.Clamp((int)MathF.Round(b / a), 0, 255);
                    target.Rgba[o + 3] = (byte)Math.Clamp((int)MathF.Round(a / samples * 255f), 0, 255);
                }
            }
        }
        return target;
    }

    private static float Deg(float degrees) => degrees * MathF.PI / 180f;
}

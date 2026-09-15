using System.Numerics;

namespace ItemForge.Core.Model;

// A loaded model, flattened to world-space triangles. Nothing here knows about cards or cameras: the loader
// bakes every node transform in, so the renderer only ever sees triangles and materials.
public sealed class ModelGeometry
{
    public required IReadOnlyList<ModelPrimitive> Primitives { get; init; }
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }
    public required string SourcePath { get; init; }
    public required double LoadMs { get; init; }

    public int TriangleCount => Primitives.Sum(p => p.Indices.Length / 3);
    public int VertexCount => Primitives.Sum(p => p.Vertices.Length);
    public Vector3 Size => Max - Min;
    public Vector3 Center => (Min + Max) * 0.5f;

    // The largest dimension, used to frame the model when a card has no scale of its own yet.
    public float Extent => MathF.Max(Size.X, MathF.Max(Size.Y, Size.Z));
}

public sealed class ModelPrimitive
{
    public required ModelVertex[] Vertices { get; init; }
    public required int[] Indices { get; init; }
    public required ModelMaterial Material { get; init; }
}

public readonly record struct ModelVertex(Vector3 Position, Vector3 Normal, Vector2 Uv);

public sealed class ModelMaterial
{
    public string Name { get; init; } = "";
    public Vector4 BaseColor { get; init; } = Vector4.One;
    public TextureImage? BaseColorTexture { get; init; }
    public bool DoubleSided { get; init; }

    // 1999 item art is flat-lit and never glows, and the first model's emissive map is a copy of its base
    // colour, so emissive is dropped on load rather than switched off per card.
    public static readonly ModelMaterial Default = new() { Name = "default" };
}

// Straight-alpha RGBA8 image, sampled bilinearly by the rasterizer.
public sealed class TextureImage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }

    // Source size before any downsample, so the cost report can say a 4096 texture was reduced.
    public required int SourceWidth { get; init; }
    public required int SourceHeight { get; init; }

    public Vector4 Sample(float u, float v)
    {
        // glTF UVs repeat; sprites are tiny so bilinear is cheap and kills most of the sampling noise.
        float x = Wrap(u) * Width - 0.5f;
        float y = Wrap(v) * Height - 0.5f;
        int x0 = (int)MathF.Floor(x), y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        var c00 = Texel(x0, y0);
        var c10 = Texel(x0 + 1, y0);
        var c01 = Texel(x0, y0 + 1);
        var c11 = Texel(x0 + 1, y0 + 1);
        return Vector4.Lerp(Vector4.Lerp(c00, c10, fx), Vector4.Lerp(c01, c11, fx), fy);
    }

    private static float Wrap(float t)
    {
        t -= MathF.Floor(t);
        return t < 0 ? t + 1 : t;
    }

    private Vector4 Texel(int x, int y)
    {
        x = ((x % Width) + Width) % Width;
        y = ((y % Height) + Height) % Height;
        int i = (y * Width + x) * 4;
        const float inv = 1f / 255f;
        return new Vector4(Rgba[i] * inv, Rgba[i + 1] * inv, Rgba[i + 2] * inv, Rgba[i + 3] * inv);
    }
}

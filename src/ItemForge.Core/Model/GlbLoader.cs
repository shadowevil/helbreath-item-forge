using System.Diagnostics;
using System.Numerics;
using SkiaSharp;

namespace ItemForge.Core.Model;

// Loads a .glb / .gltf into flat world-space triangles. Node transforms are baked in at load, so a model
// authored with a scaled or rotated node hierarchy renders the same as one authored at the origin.
public static class GlbLoader
{
    // Item sprites are a few dozen pixels, so a 4096 texture buys nothing but memory and cache misses.
    public const int MaxTextureSize = 1024;

    public static ModelGeometry Load(string path)
    {
        var sw = Stopwatch.StartNew();
        var root = SharpGLTF.Schema2.ModelRoot.Load(path);
        var scene = root.DefaultScene ?? root.LogicalScenes.FirstOrDefault()
            ?? throw new InvalidDataException("the model has no scene");

        var textures = new Dictionary<int, TextureImage?>();
        var materials = new Dictionary<int, ModelMaterial>();
        var primitives = new List<ModelPrimitive>();
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);

        foreach (var node in Flatten(scene.VisualChildren))
        {
            var mesh = node.Mesh;
            if (mesh is null)
            {
                continue;
            }
            var world = node.WorldMatrix;
            // A mirrored or non-uniform node scale needs the inverse-transpose for normals to stay normal.
            var normalMatrix = Matrix4x4.Invert(world, out var inverse)
                ? Matrix4x4.Transpose(inverse)
                : world;

            foreach (var prim in mesh.Primitives)
            {
                var positions = prim.GetVertexAccessor("POSITION")?.AsVector3Array();
                if (positions is null)
                {
                    continue;
                }
                var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                var uvs = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();

                var vertices = new ModelVertex[positions.Count];
                for (int i = 0; i < positions.Count; i++)
                {
                    var p = Vector3.Transform(positions[i], world);
                    var n = normals is null ? Vector3.Zero : Vector3.TransformNormal(normals[i], normalMatrix);
                    if (n != Vector3.Zero)
                    {
                        n = Vector3.Normalize(n);
                    }
                    vertices[i] = new ModelVertex(p, n, uvs is null ? Vector2.Zero : uvs[i]);
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }

                var indices = new List<int>();
                foreach (var (a, b, c) in prim.GetTriangleIndices())
                {
                    indices.Add(a);
                    indices.Add(b);
                    indices.Add(c);
                }
                if (indices.Count == 0)
                {
                    continue;
                }

                primitives.Add(new ModelPrimitive
                {
                    Vertices = vertices,
                    Indices = indices.ToArray(),
                    Material = ReadMaterial(prim.Material, materials, textures),
                });
            }
        }

        if (primitives.Count == 0)
        {
            throw new InvalidDataException("the model has no triangles");
        }

        // Flat-shade anything that arrived without normals, so a normal-less model still reads as a solid.
        foreach (var prim in primitives)
        {
            FillMissingNormals(prim);
        }

        sw.Stop();
        return new ModelGeometry
        {
            Primitives = primitives,
            Min = min,
            Max = max,
            SourcePath = path,
            LoadMs = sw.Elapsed.TotalMilliseconds,
        };
    }

    private static IEnumerable<SharpGLTF.Schema2.Node> Flatten(IEnumerable<SharpGLTF.Schema2.Node> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in Flatten(node.VisualChildren))
            {
                yield return child;
            }
        }
    }

    private static void FillMissingNormals(ModelPrimitive prim)
    {
        if (prim.Vertices.Any(v => v.Normal != Vector3.Zero))
        {
            return;
        }
        for (int i = 0; i + 2 < prim.Indices.Length; i += 3)
        {
            var a = prim.Vertices[prim.Indices[i]];
            var b = prim.Vertices[prim.Indices[i + 1]];
            var c = prim.Vertices[prim.Indices[i + 2]];
            var n = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            if (n != Vector3.Zero)
            {
                n = Vector3.Normalize(n);
            }
            prim.Vertices[prim.Indices[i]] = a with { Normal = n };
            prim.Vertices[prim.Indices[i + 1]] = b with { Normal = n };
            prim.Vertices[prim.Indices[i + 2]] = c with { Normal = n };
        }
    }

    private static ModelMaterial ReadMaterial(
        SharpGLTF.Schema2.Material? material,
        Dictionary<int, ModelMaterial> cache,
        Dictionary<int, TextureImage?> textures)
    {
        if (material is null)
        {
            return ModelMaterial.Default;
        }
        if (cache.TryGetValue(material.LogicalIndex, out var known))
        {
            return known;
        }

        var color = Vector4.One;
        TextureImage? texture = null;
        var channel = material.FindChannel("BaseColor");
        if (channel is not null)
        {
            var ch = channel.Value;
            color = ch.Color;
            var image = ch.Texture?.PrimaryImage;
            if (image is not null)
            {
                texture = ReadTexture(image, textures);
            }
        }

        var result = new ModelMaterial
        {
            Name = material.Name ?? "",
            BaseColor = color,
            BaseColorTexture = texture,
            DoubleSided = material.DoubleSided,
        };
        cache[material.LogicalIndex] = result;
        return result;
    }

    private static TextureImage? ReadTexture(SharpGLTF.Schema2.Image image, Dictionary<int, TextureImage?> cache)
    {
        if (cache.TryGetValue(image.LogicalIndex, out var known))
        {
            return known;
        }
        TextureImage? result = null;
        var content = image.Content;
        if (content.IsValid)
        {
            using var bitmap = SKBitmap.Decode(content.Content.ToArray());
            if (bitmap is not null)
            {
                result = ToTexture(bitmap);
            }
        }
        cache[image.LogicalIndex] = result;
        return result;
    }

    private static TextureImage ToTexture(SKBitmap source)
    {
        int width = source.Width, height = source.Height;
        int longest = Math.Max(width, height);
        var scaled = source;
        SKBitmap? resized = null;
        if (longest > MaxTextureSize)
        {
            double factor = (double)MaxTextureSize / longest;
            int w = Math.Max(1, (int)Math.Round(width * factor));
            int h = Math.Max(1, (int)Math.Round(height * factor));
            resized = source.Resize(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul), SKFilterQuality.High);
            if (resized is not null)
            {
                scaled = resized;
            }
        }

        var info = new SKImageInfo(scaled.Width, scaled.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[info.Width * info.Height * 4];
        using (var target = new SKBitmap(info))
        {
            scaled.CopyTo(target, SKColorType.Rgba8888);
            System.Runtime.InteropServices.Marshal.Copy(target.GetPixels(), rgba, 0, rgba.Length);
        }
        resized?.Dispose();
        return new TextureImage
        {
            Width = info.Width,
            Height = info.Height,
            Rgba = rgba,
            SourceWidth = width,
            SourceHeight = height,
        };
    }
}

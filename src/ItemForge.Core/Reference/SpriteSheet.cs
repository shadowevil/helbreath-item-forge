using System.Text.Json;
using System.Text.Json.Serialization;
using SkiaSharp;

namespace ItemForge.Core.Reference;

// One copied Helbreath sprite sheet: a PNG plus the frame table exported beside it. The forge never opens a
// .hba - character art is copied in as plain files and used as a BACKDROP only, never baked into output.
//
// The frame table is the Workbench's export_frames_json shape, so a sheet can be copied out and dropped in
// without a conversion step:
//   { "sheetW": 512, "sheetH": 512, "frames": [ { "index": 0, "x": 0, "y": 0, "w": 24, "h": 48,
//                                                 "pivotX": -12, "pivotY": -46 } ] }
public sealed class SpriteSheet
{
    public required string Path { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] Rgba { get; init; }
    public required IReadOnlyList<SpriteFrame> Frames { get; init; }

    public SpriteFrame? Frame(int index) => index >= 0 && index < Frames.Count ? Frames[index] : null;

    public static string FrameTablePathFor(string pngPath) =>
        System.IO.Path.ChangeExtension(pngPath, null) + ".frames.json";

    public static SpriteSheet Load(string pngPath)
    {
        if (!File.Exists(pngPath))
        {
            throw new FileNotFoundException($"sprite sheet not found: {pngPath}", pngPath);
        }

        using var bitmap = SKBitmap.Decode(pngPath)
                           ?? throw new InvalidDataException($"could not decode {pngPath}");
        var info = new SKImageInfo(bitmap.Width, bitmap.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var rgba = new byte[info.Width * info.Height * 4];
        using (var target = new SKBitmap(info))
        {
            bitmap.CopyTo(target, SKColorType.Rgba8888);
            System.Runtime.InteropServices.Marshal.Copy(target.GetPixels(), rgba, 0, rgba.Length);
        }

        return new SpriteSheet
        {
            Path = pngPath,
            Width = info.Width,
            Height = info.Height,
            Rgba = rgba,
            Frames = LoadFrames(FrameTablePathFor(pngPath), info.Width, info.Height),
        };
    }

    private static IReadOnlyList<SpriteFrame> LoadFrames(string tablePath, int width, int height)
    {
        if (!File.Exists(tablePath))
        {
            // No table: treat the whole image as one frame pivoted on its own centre, which is enough for a
            // single-figure backdrop like the equip doll.
            return new[] { new SpriteFrame { Index = 0, X = 0, Y = 0, W = width, H = height, PivotX = -width / 2, PivotY = -height / 2 } };
        }

        var table = JsonSerializer.Deserialize<FrameTable>(File.ReadAllText(tablePath), JsonOpts.Pretty)
                    ?? throw new InvalidDataException($"empty frame table: {tablePath}");
        var frames = table.Frames ?? new List<SpriteFrame>();
        foreach (var frame in frames)
        {
            if (frame.X < 0 || frame.Y < 0 || frame.X + frame.W > width || frame.Y + frame.H > height)
            {
                throw new InvalidDataException(
                    $"{tablePath}: frame {frame.Index} ({frame.X},{frame.Y} {frame.W}x{frame.H}) falls outside the {width}x{height} sheet");
            }
        }
        return frames;
    }

    private sealed class FrameTable
    {
        public int SheetW { get; set; }
        public int SheetH { get; set; }
        public List<SpriteFrame>? Frames { get; set; }
    }
}

// A frame's rect in the sheet, and where it goes relative to the anchor: dest = anchor + pivot, the client's
// own convention (above the anchor is negative).
public sealed class SpriteFrame
{
    public int Index { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }
    public int PivotX { get; set; }
    public int PivotY { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }

    public bool IsEmpty => W <= 0 || H <= 0;
}

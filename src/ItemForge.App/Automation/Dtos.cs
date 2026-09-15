using System.Text.Json.Nodes;
using ItemForge.Core.Cards;

namespace ItemForge.App;

public sealed record AppState(
    string App,
    string Version,
    string? Workspace,
    int CardCount,
    string View,
    OpenCardInfo? OpenCard,
    IntegrationInfo Integration,
    IReadOnlyList<WindowInfo> Windows,
    SettingsInfo Settings);

public sealed record OpenCardInfo(string Id, string Name, bool Dirty);

public sealed record IntegrationInfo(string Mode, int Port);

public sealed record WindowInfo(string Title, string Kind, double Width, double Height, bool IsActive, bool IsVisible);

public sealed record SettingsInfo(string Theme, string WorkspaceDir);

// A card as the agent sees it: the summary (status etc.), the full card JSON - the unsaved working copy when
// the card is open - and whether it is open / dirty.
public sealed record CardDetail(CardSummary Summary, JsonNode? Card, bool IsOpen, bool Dirty);

// Fields to change on a card; null = leave alone. ModelPath "" clears the model binding.
public sealed record CardUpdate(
    string? Id,
    string? Name,
    string? Notes,
    string? ItemType,
    string? ModelPath);

// Model tab edits; null = leave alone. Fit overrides Scale by sizing the model to the frame.
public sealed record ModelSetupUpdate(
    string? Id,
    bool Fit,
    float? Scale,
    float? OffsetX,
    float? OffsetY,
    float? RotationX,
    float? RotationY,
    float? RotationZ,
    float? LightYaw,
    float? LightPitch,
    float? Ambient);

// Live-view state: which way it looks and how hard it magnifies. None of it reaches an output pixel.
public sealed record ModelViewUpdate(int? Direction, int? Zoom, string? Mode, bool Reset);

// One off-screen render of a card's model through the game camera.
public sealed record RenderModelArgs(string? Id, int? Direction, int? Size, int? Supersample);

public sealed record RenderStatsInfo(
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
    int PivotY,
    string Cost);

// A render on its way back to the agent: the controller turns it into an inline image or a file.
public sealed record RenderPayload(RenderStatsInfo Stats, string Base64Png, int Width, int Height);

public sealed record OpResult(bool Ok, string? Error, object? Data)
{
    public static OpResult Success(object? data = null) => new(true, null, data);

    public static OpResult Fail(string error) => new(false, error, null);
}

// Screenshot results stay in-process (the controller unpacks them into MCP image content blocks).
public sealed record ScreenshotResult(string Target, IReadOnlyList<CapturedImage> Images);

public sealed record CapturedImage(string Title, string Base64Png, int Width, int Height);

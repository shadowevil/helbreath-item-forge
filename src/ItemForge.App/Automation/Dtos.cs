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
    string? ItemModel,
    int[]? ItemIds,
    string? WeaponClass,
    string? ModelPath);

public sealed record OpResult(bool Ok, string? Error, object? Data)
{
    public static OpResult Success(object? data = null) => new(true, null, data);

    public static OpResult Fail(string error) => new(false, error, null);
}

// Screenshot results stay in-process (the controller unpacks them into MCP image content blocks).
public sealed record ScreenshotResult(string Target, IReadOnlyList<CapturedImage> Images);

public sealed record CapturedImage(string Title, string Base64Png, int Width, int Height);

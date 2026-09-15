using ItemForge.Core.Cards;

namespace ItemForge.App;

// The app surface the MCP tools drive. Implemented by MainWindow (MainWindow.Automation.cs); every call
// marshals onto the UI thread. Mutations return an OpResult so a refusal carries its reason.
public interface IAutomationHost
{
    Task<AppState> GetStateAsync();

    Task<ScreenshotResult> ScreenshotAsync(string? target);

    Task<IReadOnlyList<CardSummary>> ListCardsAsync();

    Task<CardDetail?> GetCardAsync(string id);

    Task<OpResult> OpenCardAsync(string id);

    Task<OpResult> ShowGalleryAsync();

    Task<OpResult> CloseCardAsync(bool discard);

    Task<OpResult> CreateCardAsync(string name, bool open);

    Task<OpResult> UpdateCardAsync(CardUpdate update);

    Task<OpResult> SaveCardAsync();

    Task<OpResult> DuplicateCardAsync(string id, string name);

    Task<OpResult> DeleteCardAsync(string id);

    Task<OpResult> GetModelInfoAsync(string? id);

    Task<OpResult> UpdateModelSetupAsync(ModelSetupUpdate update);

    Task<OpResult> SetCardTabAsync(string tab);

    Task<OpResult> SetModelViewAsync(int? direction, int? zoom);

    Task<OpResult> RenderModelAsync(RenderModelArgs args);

    Task<OpResult> SetSettingAsync(string key, string value);

    Task<OpResult> QuitAsync(bool discard);
}

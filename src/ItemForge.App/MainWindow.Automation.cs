using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ItemForge.Core;
using ItemForge.Core.Cards;

namespace ItemForge.App;

// MainWindow's implementation of the automation surface the MCP integration drives. The transport runs on
// background threads; every method here hops onto the Avalonia UI thread before touching window state, and
// goes through the same *Core operations the menus and buttons use, so an agent and a person always get the
// same behaviour.
public partial class MainWindow : IAutomationHost
{
    public Task<AppState> GetStateAsync() => OnUi(BuildState);

    public Task<ScreenshotResult> ScreenshotAsync(string? target) => OnUi(() => CaptureWindows(target));

    public Task<IReadOnlyList<CardSummary>> ListCardsAsync() => OnUi(() =>
    {
        RefreshCards();
        return _summaries;
    });

    public Task<CardDetail?> GetCardAsync(string id) => OnUi<CardDetail?>(() =>
    {
        if (_store is null || !CardStore.IsValidId(id))
        {
            return null;
        }
        if (_openCard?.Id == id)
        {
            return new CardDetail(_store.Summarize(_openCard), JsonSerializer.SerializeToNode(_openCard, JsonOpts.Pretty), true, IsDirty);
        }
        if (!_store.Exists(id))
        {
            return null;
        }
        var card = _store.Load(id);
        return new CardDetail(_store.Summarize(card), JsonSerializer.SerializeToNode(card, JsonOpts.Pretty), false, false);
    });

    public Task<OpResult> OpenCardAsync(string id) => OnUi(() =>
    {
        if (_openCard?.Id == id)
        {
            ShowOpenCard();
            return OpResult.Success(new { id, dirty = IsDirty });
        }
        if (IsDirty)
        {
            return OpResult.Fail($"card '{_openCard!.Id}' has unsaved changes - save_card, or close_card with discard:true, first");
        }
        string? error = OpenCardCore(id);
        return error is null ? OpResult.Success(new { id, dirty = false }) : OpResult.Fail(error);
    });

    public Task<OpResult> ShowGalleryAsync() => OnUi(() =>
    {
        if (_store is null)
        {
            return OpResult.Fail("no workspace is open");
        }
        ShowGallery();
        return OpResult.Success(new { view = _view, cards = _summaries.Count });
    });

    public Task<OpResult> CloseCardAsync(bool discard) => OnUi(() =>
    {
        if (_openCard is null)
        {
            return OpResult.Success(new { closed = (string?)null });
        }
        if (IsDirty && !discard)
        {
            return OpResult.Fail($"card '{_openCard.Id}' has unsaved changes - save_card first, or pass discard:true");
        }
        string id = _openCard.Id;
        CloseCardCore();
        return OpResult.Success(new { closed = id });
    });

    public Task<OpResult> CreateCardAsync(string name, bool open) => OnUi(() =>
    {
        var (card, error) = CreateCardCore(name);
        if (card is null)
        {
            return OpResult.Fail(error ?? "could not create the card");
        }
        bool opened = false;
        if (open && !IsDirty)
        {
            opened = OpenCardCore(card.Id) is null;
        }
        return OpResult.Success(new { id = card.Id, file = _store!.PathFor(card.Id), opened });
    });

    public Task<OpResult> UpdateCardAsync(CardUpdate update) => OnUi(() =>
    {
        if (_store is null)
        {
            return OpResult.Fail("no workspace is open");
        }
        if (update.Id is not null && _openCard?.Id != update.Id)
        {
            if (IsDirty)
            {
                return OpResult.Fail($"card '{_openCard!.Id}' has unsaved changes - save or close it before editing '{update.Id}'");
            }
            string? openError = OpenCardCore(update.Id);
            if (openError is not null)
            {
                return OpResult.Fail(openError);
            }
        }
        if (_openCard is null)
        {
            return OpResult.Fail("no card is open - pass 'id'");
        }

        // Validate everything before changing anything, so a bad field never leaves a half-applied edit.
        if (update.Name is not null && string.IsNullOrWhiteSpace(update.Name))
        {
            return OpResult.Fail("name cannot be empty");
        }
        if (update.WeaponClass is not null && update.WeaponClass.Length > 0 && !WeaponClasses.All.Contains(update.WeaponClass))
        {
            return OpResult.Fail($"unknown weaponClass '{update.WeaponClass}' (one of: {string.Join(", ", WeaponClasses.All)})");
        }
        if (update.ItemIds is not null && update.ItemIds.Any(i => i < 0))
        {
            return OpResult.Fail("itemIds must be non-negative");
        }
        var scratch = _openCard.Clone();
        if (update.ModelPath is not null && update.ModelPath.Length > 0)
        {
            string? bindError = ModelBinding.Bind(scratch, _store.Workspace, update.ModelPath);
            if (bindError is not null)
            {
                return OpResult.Fail(bindError);
            }
        }

        if (update.Name is not null) _openCard.Name = update.Name.Trim();
        if (update.Notes is not null) _openCard.Notes = update.Notes;
        if (update.ItemModel is not null) _openCard.Item.Model = update.ItemModel.Trim();
        if (update.ItemIds is not null) _openCard.Item.Ids = update.ItemIds.ToList();
        if (update.WeaponClass is not null) _openCard.Item.WeaponClass = update.WeaponClass;
        if (update.ModelPath is not null)
        {
            if (update.ModelPath.Length == 0)
            {
                ModelBinding.Clear(_openCard);
            }
            else
            {
                _openCard.Model.Path = scratch.Model.Path;
                _openCard.Model.Sha256 = scratch.Model.Sha256;
            }
        }

        _editor?.LoadFromCard();
        UpdateChrome();
        return OpResult.Success(new
        {
            id = _openCard.Id,
            dirty = IsDirty,
            modelStatus = ModelBinding.Status(_openCard.Model, _store.Workspace),
        });
    });

    public Task<OpResult> SaveCardAsync() => OnUi(() =>
    {
        string? error = SaveCardCore();
        return error is null
            ? OpResult.Success(new { id = _openCard!.Id, file = _store!.PathFor(_openCard.Id), modified = _openCard.Modified })
            : OpResult.Fail(error);
    });

    public Task<OpResult> DuplicateCardAsync(string id, string name) => OnUi(() =>
    {
        var (copy, error) = DuplicateCardCore(id, name);
        return copy is null ? OpResult.Fail(error ?? "could not duplicate") : OpResult.Success(new { id = copy.Id, file = _store!.PathFor(copy.Id) });
    });

    public Task<OpResult> DeleteCardAsync(string id) => OnUi(() =>
    {
        var (trash, error) = DeleteCardCore(id);
        return trash is null ? OpResult.Fail(error ?? "could not delete") : OpResult.Success(new { id, movedTo = trash });
    });

    public Task<OpResult> SetSettingAsync(string key, string value) => OnUi(() =>
    {
        switch (key)
        {
            case "theme":
                if (value is not ("System" or "Light" or "Dark"))
                {
                    return OpResult.Fail("theme must be System, Light or Dark");
                }
                ApplyTheme(value, persist: true);
                return OpResult.Success(new { theme = _settings.Theme });

            case "workspaceDir":
                if (IsDirty)
                {
                    return OpResult.Fail($"card '{_openCard!.Id}' has unsaved changes - save or close it first");
                }
                if (!Workspace.IsWorkspace(value))
                {
                    return OpResult.Fail($"'{value}' has no {Workspace.MarkerFile}");
                }
                _settings.WorkspaceDir = value;
                _settings.Save();
                LoadWorkspace(Workspace.Open(value));
                return OpResult.Success(new { workspace = _store?.Workspace.Root });

            default:
                return OpResult.Fail($"unknown setting '{key}' (theme, workspaceDir)");
        }
    });

    public Task<OpResult> QuitAsync(bool discard) => OnUi(() =>
    {
        if (IsDirty && !discard)
        {
            return OpResult.Fail($"card '{_openCard!.Id}' has unsaved changes - save_card first, or pass discard:true");
        }
        _forceClose = true;
        // Close after the reply has gone out, so the caller sees the result instead of a dropped connection.
        DispatcherTimer.RunOnce(Close, TimeSpan.FromMilliseconds(250));
        return OpResult.Success(new { quitting = true });
    });

    // --- helpers -----------------------------------------------------------------------------

    private AppState BuildState()
    {
        var windows = new List<WindowInfo>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            foreach (var w in lifetime.Windows)
            {
                windows.Add(new WindowInfo(w.Title ?? "", w.GetType().Name, w.Bounds.Width, w.Bounds.Height, w.IsActive, w.IsVisible));
            }
        }
        return new AppState(
            "helbreath-item-forge",
            AppVersion,
            _store?.Workspace.Root,
            _summaries.Count,
            _view,
            _openCard is null ? null : new OpenCardInfo(_openCard.Id, _openCard.DisplayName, IsDirty),
            new IntegrationInfo((_integration?.Mode ?? IntegrationMode.Off).ToString(), _integration?.Port ?? _settings.IntegrationPort),
            windows,
            new SettingsInfo(_settings.Theme, _settings.WorkspaceDir));
    }

    private ScreenshotResult CaptureWindows(string? target)
    {
        var images = new List<CapturedImage>();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
        {
            IEnumerable<Window> selected = string.IsNullOrWhiteSpace(target) || target.Equals("main", StringComparison.OrdinalIgnoreCase)
                ? lifetime.Windows.Where(w => ReferenceEquals(w, lifetime.MainWindow))
                : target.Equals("all", StringComparison.OrdinalIgnoreCase)
                    ? lifetime.Windows.ToList()
                    : lifetime.Windows.Where(w => (w.Title ?? "").Contains(target, StringComparison.OrdinalIgnoreCase));
            foreach (var w in selected)
            {
                var shot = CaptureWindow(w);
                if (shot is not null)
                {
                    images.Add(shot);
                }
            }
        }
        return new ScreenshotResult(target ?? "main", images);
    }

    // Renders a window's live visual tree to a PNG via RenderTargetBitmap, sized by RenderScaling so the
    // capture is crisp on a HiDPI display.
    private static CapturedImage? CaptureWindow(Window w)
    {
        double scaling = w.RenderScaling <= 0 ? 1.0 : w.RenderScaling;
        var size = w.ClientSize;
        int pw = (int)Math.Ceiling(size.Width * scaling);
        int ph = (int)Math.Ceiling(size.Height * scaling);
        if (pw < 1 || ph < 1)
        {
            return null; // minimized or not yet laid out
        }
        using var rtb = new RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96 * scaling, 96 * scaling));
        rtb.Render(w);
        using var ms = new MemoryStream();
        rtb.Save(ms);
        return new CapturedImage(string.IsNullOrEmpty(w.Title) ? w.GetType().Name : w.Title!, Convert.ToBase64String(ms.ToArray()), pw, ph);
    }

    private static string AppVersion => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.1.0";

    private static async Task<T> OnUi<T>(Func<T> f) =>
        Dispatcher.UIThread.CheckAccess() ? f() : await Dispatcher.UIThread.InvokeAsync(f);
}

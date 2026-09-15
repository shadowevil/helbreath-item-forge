using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using ItemForge.App.Views;
using ItemForge.Core;
using ItemForge.Core.Cards;

namespace ItemForge.App;

// One row of the sidebar card list.
public sealed class CardListItem
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Subtitle { get; init; }
}

public partial class MainWindow : Window
{
    private const string ViewNone = "none";
    private const string ViewGallery = "gallery";
    private const string ViewCard = "card";

    private readonly Settings _settings = Settings.Load();
    private readonly Bitmap _appIcon;
    private readonly ObservableCollection<CardListItem> _cardItems = new();
    private IReadOnlyList<CardSummary> _summaries = Array.Empty<CardSummary>();

    private CardStore? _store;
    private Card? _openCard;           // the working copy being edited
    private string _openBaseline = "";  // its serialized form as last loaded / saved, for dirty tracking
    private CardEditorView? _editor;
    private CardGalleryView? _gallery;
    private string _view = ViewNone;
    private IntegrationController? _integration;
    private bool _forceClose;
    private bool _syncingList;

    public MainWindow()
    {
        InitializeComponent();

        _appIcon = new Bitmap(AssetLoader.Open(new Uri("avares://helbreath_item_forge/Assets/forge_icon.png")));
        Icon = new WindowIcon(_appIcon);
        AppIcon.Source = _appIcon;
        CardList.ItemsSource = _cardItems;
        ApplyTheme(_settings.Theme, persist: false);

        FilterBox.TextChanged += (_, _) => RebuildCardList();
        CardList.SelectionChanged += OnCardListSelectionChanged;
        KeyDown += OnGlobalKeyDown;

        LoadWorkspace(Workspace.Locate(_settings.WorkspaceDir, AppContext.BaseDirectory));

        InitIntegration();
        Closed += (_, _) => _integration?.Dispose();

        // Only the primary instance reaches here (secondaries forward + exit in Program.Main), so it hosts
        // the pipe server that later launches forward to.
        SingleInstance.StartServer(HandleForward);
    }

    private bool IsDirty => _openCard is not null && Serialize(_openCard) != _openBaseline;

    private static string Serialize(Card card) => JsonSerializer.Serialize(card, JsonOpts.Pretty);

    // --- workspace + card list --------------------------------------------------------

    private void LoadWorkspace(Workspace? workspace)
    {
        _openCard = null;
        _editor = null;
        _openBaseline = "";
        _store = workspace is null ? null : new CardStore(workspace);
        if (_store is null)
        {
            _summaries = Array.Empty<CardSummary>();
            RebuildCardList();
            ShowNoWorkspace();
            return;
        }
        RefreshCards();
        ShowGallery();
        StatusText.Text = $"Workspace: {_store.Workspace.Root}";
    }

    public void RefreshCards()
    {
        _summaries = _store?.List() ?? Array.Empty<CardSummary>();
        RebuildCardList();
        _gallery?.SetCards(_summaries, _openCard?.Id);
        UpdateChrome();
    }

    private void RebuildCardList()
    {
        string filter = (FilterBox.Text ?? "").Trim();
        _syncingList = true;
        try
        {
            _cardItems.Clear();
            foreach (var s in _summaries)
            {
                if (filter.Length > 0 &&
                    !s.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !s.Id.Contains(filter, StringComparison.OrdinalIgnoreCase) &&
                    !s.ItemModel.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                string subtitle = s.Error is not null
                    ? "unreadable card file"
                    : (string.IsNullOrEmpty(s.ItemModel) ? s.Id : s.ItemModel) + "  -  " + CardGalleryView.StatusLabel(s.ModelStatus).Text;
                _cardItems.Add(new CardListItem { Id = s.Id, Name = s.Name, Subtitle = subtitle });
            }
            CardList.SelectedItem = _cardItems.FirstOrDefault(i => i.Id == _openCard?.Id);
        }
        finally
        {
            _syncingList = false;
        }
        SideHeader.Text = _store is null ? "CARDS" : $"CARDS ({_summaries.Count})";
    }

    private async void OnCardListSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncingList || CardList.SelectedItem is not CardListItem item || item.Id == _openCard?.Id)
        {
            return;
        }
        await OpenCardInteractiveAsync(item.Id);
    }

    // --- views --------------------------------------------------------------------------

    private void ShowNoWorkspace()
    {
        _view = ViewNone;
        var text = new StackPanel
        {
            Spacing = 6,
            MaxWidth = 520,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Ui.Text("No workspace found.", size: 14),
                Ui.Text($"The forge works inside a folder holding {Workspace.MarkerFile} (the helbreath-item-forge repo). Use File > Open Workspace... to pick it.", "hint"),
            },
        };
        ViewerHost.Content = text;
        UpdateChrome();
    }

    public void ShowGallery()
    {
        if (_store is null)
        {
            ShowNoWorkspace();
            return;
        }
        _gallery ??= new CardGalleryView(this);
        _gallery.SetCards(_summaries, _openCard?.Id);
        ViewerHost.Content = _gallery;
        _view = ViewGallery;
        UpdateChrome();
    }

    private void ShowOpenCard()
    {
        if (_editor is null)
        {
            ShowGallery();
            return;
        }
        ViewerHost.Content = _editor;
        _view = ViewCard;
        UpdateChrome();
    }

    private void UpdateChrome()
    {
        bool dirty = IsDirty;
        if (_openCard is not null && _view == ViewCard)
        {
            TitleText.Text = $"{_openCard.DisplayName}{(dirty ? " *" : "")}  -  cards/{_openCard.Id}.json";
            Breadcrumb.IsVisible = true;
            BreadcrumbText.Text = $"cards  /  {_openCard.Id}.json{(dirty ? "   (unsaved)" : "")}";
        }
        else
        {
            TitleText.Text = _store is null ? "Helbreath Item Forge" : $"Helbreath Item Forge  -  {Path.GetFileName(_store.Workspace.Root)}";
            Breadcrumb.IsVisible = false;
        }
        StatusRight.Text = _store is null ? "no workspace" : _store.Workspace.Root;
        RebuildTabs(dirty);
    }

    private void RebuildTabs(bool dirty)
    {
        Tabs.Children.Clear();
        TabStrip.IsVisible = _store is not null;
        if (_store is null)
        {
            return;
        }
        Tabs.Children.Add(MakeTab("Cards", _view == ViewGallery, ShowGallery, null));
        if (_openCard is not null)
        {
            Tabs.Children.Add(MakeTab(_openCard.DisplayName + (dirty ? " *" : ""), _view == ViewCard, ShowOpenCard,
                async () => await CloseCardInteractiveAsync()));
        }
    }

    private Border MakeTab(string text, bool active, Action activate, Func<Task>? close)
    {
        var label = Ui.Text(text, size: 12);
        label.VerticalAlignment = VerticalAlignment.Center;
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Children = { label } };
        if (close is not null)
        {
            var closeButton = new Button { Classes = { "flat" }, Padding = new Thickness(4, 3), Margin = new Thickness(4, 0, 0, 0), Content = Ui.CloseGlyph() };
            ToolTip.SetTip(closeButton, "Close card");
            closeButton.Click += async (_, e) => { e.Handled = true; await close(); };
            content.Children.Add(closeButton);
        }
        var tab = new Border { Child = content };
        tab.Classes.Add("tab");
        if (active)
        {
            tab.Classes.Add("active");
        }
        tab.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                activate();
            }
        };
        return tab;
    }

    // --- card operations (shared by the UI and the MCP tools; each returns an error or null) -----------

    private string? OpenCardCore(string id)
    {
        if (_store is null)
        {
            return "no workspace is open";
        }
        if (!_store.Exists(id))
        {
            return $"no card '{id}'";
        }
        Card card;
        try
        {
            card = _store.Load(id);
        }
        catch (Exception ex)
        {
            return $"card '{id}' could not be read: {ex.Message}";
        }
        _openCard = card;
        _openBaseline = Serialize(card);
        _editor = new CardEditorView(card, _store.Workspace);
        _editor.Edited += UpdateChrome;
        _gallery?.SetOpen(id);
        RebuildCardList();
        ShowOpenCard();
        StatusText.Text = $"Opened card '{id}'.";
        return null;
    }

    private void CloseCardCore()
    {
        _openCard = null;
        _openBaseline = "";
        _editor = null;
        _gallery?.SetOpen(null);
        RebuildCardList();
        ShowGallery();
    }

    private string? SaveCardCore()
    {
        if (_store is null || _openCard is null)
        {
            return "no card is open";
        }
        try
        {
            _store.Save(_openCard);
        }
        catch (Exception ex)
        {
            return $"save failed: {ex.Message}";
        }
        _openBaseline = Serialize(_openCard);
        _editor?.UpdateFileInfo();
        RefreshCards();
        StatusText.Text = $"Saved cards/{_openCard.Id}.json";
        return null;
    }

    private (Card? Card, string? Error) CreateCardCore(string name)
    {
        if (_store is null)
        {
            return (null, "no workspace is open");
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "a card needs a name");
        }
        try
        {
            var card = _store.Create(name);
            RefreshCards();
            StatusText.Text = $"Created cards/{card.Id}.json";
            return (card, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private (Card? Card, string? Error) DuplicateCardCore(string id, string name)
    {
        if (_store is null)
        {
            return (null, "no workspace is open");
        }
        if (!_store.Exists(id))
        {
            return (null, $"no card '{id}'");
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            return (null, "the copy needs a name");
        }
        try
        {
            var copy = _store.Duplicate(id, name);
            RefreshCards();
            StatusText.Text = $"Duplicated '{id}' as cards/{copy.Id}.json";
            return (copy, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    private (string? TrashPath, string? Error) DeleteCardCore(string id)
    {
        if (_store is null)
        {
            return (null, "no workspace is open");
        }
        if (!_store.Exists(id))
        {
            return (null, $"no card '{id}'");
        }
        if (_openCard?.Id == id)
        {
            CloseCardCore();
        }
        try
        {
            string trash = _store.Delete(id);
            RefreshCards();
            StatusText.Text = $"Moved '{id}' to {Path.GetRelativePath(_store.Workspace.Root, trash)}";
            return (trash, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    // --- card operations, interactive ------------------------------------------------------

    // Asks what to do with unsaved edits before leaving the open card. False = stay.
    private async Task<bool> ConfirmLeaveOpenCardAsync()
    {
        if (!IsDirty)
        {
            return true;
        }
        var result = await ConfirmDialog.Show(this, "Unsaved card", $"Save changes to '{_openCard!.DisplayName}' first?");
        if (result == ConfirmResult.Cancel)
        {
            return false;
        }
        if (result == ConfirmResult.Save)
        {
            string? error = SaveCardCore();
            if (error is not null)
            {
                StatusText.Text = error;
                return false;
            }
        }
        return true;
    }

    public async Task OpenCardInteractiveAsync(string id)
    {
        if (_openCard?.Id == id)
        {
            ShowOpenCard();
            return;
        }
        if (!await ConfirmLeaveOpenCardAsync())
        {
            RebuildCardList();
            return;
        }
        string? error = OpenCardCore(id);
        if (error is not null)
        {
            StatusText.Text = error;
            RebuildCardList();
        }
    }

    public async Task CloseCardInteractiveAsync()
    {
        if (_openCard is null)
        {
            return;
        }
        if (await ConfirmLeaveOpenCardAsync())
        {
            CloseCardCore();
        }
    }

    public async Task NewCardInteractiveAsync()
    {
        if (_store is null)
        {
            StatusText.Text = "Open a workspace first.";
            return;
        }
        string? name = await InputDialog.Show(this, "New card", "Card name (the item it is for, e.g. Dark Angel Sword):", "");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var (card, error) = CreateCardCore(name);
        if (card is null)
        {
            StatusText.Text = error ?? "could not create the card";
            return;
        }
        await OpenCardInteractiveAsync(card.Id);
    }

    public async Task DuplicateCardInteractiveAsync(string id)
    {
        var source = _summaries.FirstOrDefault(s => s.Id == id);
        string? name = await InputDialog.Show(this, "Duplicate card", "Name for the copy:", (source?.Name ?? id) + " copy");
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        var (_, error) = DuplicateCardCore(id, name);
        if (error is not null)
        {
            StatusText.Text = error;
        }
    }

    public async Task DeleteCardInteractiveAsync(string id)
    {
        var source = _summaries.FirstOrDefault(s => s.Id == id);
        bool yes = await ConfirmDialog.Ask(this, "Delete card",
            $"Move '{source?.Name ?? id}' (cards/{id}.json) to cards/.trash? Any unsaved edits to it are lost. The model file is not touched.",
            "Delete");
        if (!yes)
        {
            return;
        }
        var (_, error) = DeleteCardCore(id);
        if (error is not null)
        {
            StatusText.Text = error;
        }
    }

    public void RevealCard(string id)
    {
        if (_store is null || !_store.Exists(id))
        {
            return;
        }
        RevealPath(_store.PathFor(id), select: true);
    }

    private static void RevealPath(string path, bool select)
    {
        try
        {
            string args = select ? $"/select,\"{path}\"" : $"\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch
        {
            // best effort - revealing a folder is a convenience
        }
    }

    // --- menu + keyboard ------------------------------------------------------------------

    private async void OnNewCard(object? sender, RoutedEventArgs e) => await NewCardInteractiveAsync();

    private void OnSave(object? sender, RoutedEventArgs e) => SaveInteractive();

    private void SaveInteractive()
    {
        string? error = SaveCardCore();
        if (error is not null)
        {
            StatusText.Text = error;
        }
    }

    private async void OnCloseCardMenu(object? sender, RoutedEventArgs e) => await CloseCardInteractiveAsync();

    private void OnShowGallery(object? sender, RoutedEventArgs e) => ShowGallery();

    private void OnRefresh(object? sender, RoutedEventArgs e) => RefreshCards();

    private async void OnOpenWorkspace(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeaveOpenCardAsync())
        {
            return;
        }
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the Item Forge workspace folder",
            AllowMultiple = false,
        });
        string? dir = folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
        if (dir is null)
        {
            return;
        }
        if (!Workspace.IsWorkspace(dir))
        {
            bool create = await ConfirmDialog.Ask(this, "Not a workspace",
                $"'{dir}' has no {Workspace.MarkerFile}. Make it an Item Forge workspace?", "Create");
            if (!create)
            {
                return;
            }
            Workspace.Create(dir);
        }
        _settings.WorkspaceDir = dir;
        _settings.Save();
        LoadWorkspace(Workspace.Open(dir));
    }

    private void OnRevealWorkspace(object? sender, RoutedEventArgs e)
    {
        if (_store is not null)
        {
            RevealPath(_store.Workspace.Root, select: false);
        }
    }

    private async void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.S)
        {
            e.Handled = true;
            SaveInteractive();
        }
        else if (ctrl && e.Key == Key.N)
        {
            e.Handled = true;
            await NewCardInteractiveAsync();
        }
        else if (e.Key == Key.F5)
        {
            e.Handled = true;
            RefreshCards();
        }
    }

    // Opens card files passed on the command line (e.g. `helbreath_item_forge.exe cards\foo.json`).
    public void OpenFromArgs(string[] args)
    {
        foreach (string arg in args)
        {
            if (arg.StartsWith("--", StringComparison.Ordinal) || !File.Exists(arg) || _store is null)
            {
                continue;
            }
            string? id = _store.IdForFile(arg);
            if (id is not null)
            {
                OpenCardCore(id);
            }
        }
    }

    // --- window chrome ----------------------------------------------------------------------

    private void OnTitleBarPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.ClickCount == 1)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e) => ToggleMaximize();

    private void OnMinimize(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object? sender, RoutedEventArgs e) => ToggleMaximize();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    // Activity-bar button cycles System -> Dark -> Light -> System.
    private void OnThemeToggle(object? sender, RoutedEventArgs e)
    {
        string next = _settings.Theme switch
        {
            "System" => "Dark",
            "Dark" => "Light",
            _ => "System",
        };
        ApplyTheme(next, persist: true);
    }

    private void OnThemeSystem(object? sender, RoutedEventArgs e) => ApplyTheme("System", true);
    private void OnThemeLight(object? sender, RoutedEventArgs e) => ApplyTheme("Light", true);
    private void OnThemeDark(object? sender, RoutedEventArgs e) => ApplyTheme("Dark", true);

    private void ApplyTheme(string mode, bool persist)
    {
        Application.Current!.RequestedThemeVariant = mode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
        _settings.Theme = mode is "Light" or "Dark" ? mode : "System";
        if (persist)
        {
            _settings.Save();
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (!_forceClose && IsDirty)
        {
            e.Cancel = true;
            if (await ConfirmLeaveOpenCardAsync())
            {
                _forceClose = true;
                Close();
            }
            return;
        }
        base.OnClosing(e);
    }

    // --- Claude Code integration -------------------------------------------------------------

    // Builds the integration controller (minting a token on first use) and restores the persisted mode.
    private void InitIntegration()
    {
        if (string.IsNullOrEmpty(_settings.IntegrationToken))
        {
            _settings.IntegrationToken = Convert
                .ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24))
                .Replace('+', '-').Replace('/', '_').TrimEnd('=');
            _settings.Save();
        }

        _integration = new IntegrationController(this, _settings.IntegrationPort, _settings.IntegrationToken);
        _integration.Changed += () => Dispatcher.UIThread.Post(UpdateIntegrationUi);
        _integration.Log += line => Dispatcher.UIThread.Post(() => StatusText.Text = $"Claude: {line}");

        IntegrationMode restore = ParseMode(_settings.IntegrationMode);
        if (restore != IntegrationMode.Off)
        {
            string? err = _integration.SetMode(restore);
            if (err is not null)
            {
                StatusText.Text = $"Claude integration failed to start: {err}";
            }
        }
        UpdateIntegrationUi();
    }

    private static IntegrationMode ParseMode(string? s) =>
        string.Equals(s, "Observe", StringComparison.OrdinalIgnoreCase) ? IntegrationMode.Observe
        : string.Equals(s, "Full", StringComparison.OrdinalIgnoreCase) ? IntegrationMode.Full
        : IntegrationMode.Off;

    private void OnIntegrationOff(object? sender, RoutedEventArgs e) => ApplyIntegrationMode(IntegrationMode.Off);
    private void OnIntegrationObserve(object? sender, RoutedEventArgs e) => ApplyIntegrationMode(IntegrationMode.Observe);
    private void OnIntegrationFull(object? sender, RoutedEventArgs e) => ApplyIntegrationMode(IntegrationMode.Full);

    private void ApplyIntegrationMode(IntegrationMode mode)
    {
        if (_integration is null)
        {
            return;
        }
        string? err = _integration.SetMode(mode);
        _settings.IntegrationMode = mode.ToString();
        _settings.Save();
        if (err is not null)
        {
            StatusText.Text = $"Claude integration failed: {err}";
        }
        else if (mode != IntegrationMode.Off)
        {
            StatusText.Text = $"Claude integration on ({mode}). Tools > Claude Code Integration > Copy connect command to hook up Claude Code.";
        }
        UpdateIntegrationUi();
    }

    private void UpdateIntegrationUi()
    {
        var mode = _integration is { IsRunning: true } ? _integration.Mode : IntegrationMode.Off;
        bool on = mode != IntegrationMode.Off;
        IntegrationStatus.Text = on ? $"● Claude: {mode}  :{_integration!.Port}" : "";
        IntegrationStatus.IsVisible = on;
        MarkModeItem(IntegrationOffItem, "Off", mode == IntegrationMode.Off);
        MarkModeItem(IntegrationObserveItem, "Observe (read-only)", mode == IntegrationMode.Observe);
        MarkModeItem(IntegrationFullItem, "Full (read/write)", mode == IntegrationMode.Full);
    }

    private static void MarkModeItem(MenuItem item, string label, bool active) =>
        item.Header = active ? "✓ " + label : label;

    // Called by App when launched with --mcp: turns on Observe for this run without flipping the persisted
    // toggle, and never downgrades a persisted Full.
    public void EnableIntegrationFromCli()
    {
        if (_integration is null)
        {
            return;
        }
        if (_integration.IsRunning)
        {
            UpdateIntegrationUi();
            return;
        }
        string? err = _integration.SetMode(IntegrationMode.Observe);
        StatusText.Text = err is null
            ? "Claude integration on (Observe) via --mcp."
            : $"Claude integration failed to start: {err}";
        UpdateIntegrationUi();
    }

    private async void OnCopyConnectCommand(object? sender, RoutedEventArgs e) =>
        await CopyToClipboard(_integration?.ConnectCommand ?? "",
            _integration is { IsRunning: true } ? "Connect command copied to clipboard." : "Copied - turn on Observe or Full so the server is listening.");

    private async void OnCopyServerUrl(object? sender, RoutedEventArgs e) =>
        await CopyToClipboard($"http://127.0.0.1:{_integration?.Port ?? _settings.IntegrationPort}/mcp", "Server URL copied.");

    private async void OnCopyToken(object? sender, RoutedEventArgs e) =>
        await CopyToClipboard(_integration?.Token ?? _settings.IntegrationToken, "Bearer token copied.");

    private async void OnCopyMcpJson(object? sender, RoutedEventArgs e) =>
        await CopyToClipboard(_integration?.McpJsonConfig ?? "",
            "Copied .mcp.json config - paste into your project's .mcp.json for auto-connect.");

    private async Task CopyToClipboard(string text, string status)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
        StatusText.Text = status;
    }

    // A second launch forwards here: open any card files it named, ensure MCP if asked, and report the
    // live connection details back.
    private ForwardResponse HandleForward(ForwardRequest request) => Dispatcher.UIThread.Invoke(() =>
    {
        var opened = new List<string>();
        foreach (string file in request.Files)
        {
            string? id = _store?.IdForFile(file);
            if (id is not null && (!IsDirty || _openCard?.Id == id) && (_openCard?.Id == id || OpenCardCore(id) is null))
            {
                opened.Add(id);
            }
        }
        if (request.EnableMcp)
        {
            EnableIntegrationFromCli();
        }
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }
        Activate();

        bool on = _integration is { IsRunning: true };
        return new ForwardResponse(
            true,
            opened.ToArray(),
            on,
            on ? $"http://127.0.0.1:{_integration!.Port}/mcp" : null,
            on ? _integration!.Token : null,
            on ? _integration!.ConnectCommand : null,
            on ? _integration!.McpJsonConfig : null,
            on ? "" : "The MCP server is OFF on the running instance. Launch with --mcp, or enable Tools > Claude Code Integration.");
    });
}

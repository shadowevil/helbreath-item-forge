using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ItemForge.Core.Cards;

namespace ItemForge.App.Views;

// The card gallery: one tile per model card. Clicking a tile opens the card; the context menu duplicates,
// reveals or deletes it. The thumbnail is a placeholder until the renderer exists (phase 2) - it will show
// the card's live render.
public sealed class CardGalleryView : UserControl
{
    private const double TileWidth = 212;
    private const double TileHeight = 236;
    private const double ThumbHeight = 138;

    private readonly MainWindow _host;
    private readonly WrapPanel _tiles = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock _count = Ui.Text("", "hint");
    private readonly StackPanel _empty;
    private readonly Dictionary<string, Border> _tileById = new(StringComparer.Ordinal);
    private string? _openId;

    public CardGalleryView(MainWindow host)
    {
        _host = host;

        var title = Ui.Text("Model cards", size: 18);
        title.FontWeight = FontWeight.SemiBold;

        var newButton = Ui.DialogButton("New Card...", async (_, _) => await _host.NewCardInteractiveAsync());

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 16) };
        var titles = new StackPanel { Spacing = 2, Children = { title, _count } };
        header.Children.Add(titles);
        newButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(newButton, 1);
        header.Children.Add(newButton);

        _empty = new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 24, 0, 0),
            IsVisible = false,
            Children =
            {
                Ui.Text("No cards yet.", size: 13),
                Ui.Text("A card binds one 3D model to one item. Create one with New Card..., then pick its model file on the Details tab.", "hint"),
            },
        };

        var body = new StackPanel { Margin = new Thickness(24, 18, 12, 18), Children = { header, _empty, _tiles } };
        Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    public void SetCards(IReadOnlyList<CardSummary> cards, string? openId)
    {
        _openId = openId;
        _tiles.Children.Clear();
        _tileById.Clear();
        foreach (var card in cards)
        {
            var tile = MakeTile(card);
            _tileById[card.Id] = tile;
            _tiles.Children.Add(tile);
        }
        _count.Text = cards.Count == 1 ? "1 card" : $"{cards.Count} cards";
        _empty.IsVisible = cards.Count == 0;
    }

    public void SetOpen(string? openId)
    {
        _openId = openId;
        foreach (var (id, tile) in _tileById)
        {
            tile.Classes.Set("open", string.Equals(id, openId, StringComparison.Ordinal));
        }
    }

    private Border MakeTile(CardSummary card)
    {
        var glyph = new Avalonia.Controls.Shapes.Path
        {
            Data = Geometry.Parse("M10,22 L27,5 M6,18 L14,26 M10,22 L5,27"),
            StrokeThickness = 2,
            Width = 56,
            Height = 56,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
        }.Res(Avalonia.Controls.Shapes.Shape.StrokeProperty, "HbaMutedBrush");

        var thumbCaption = Ui.Text("not rendered yet", "hint");
        thumbCaption.HorizontalAlignment = HorizontalAlignment.Center;

        var thumb = new Border
        {
            Height = ThumbHeight,
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Child = new StackPanel
            {
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { glyph, thumbCaption },
            },
        }.Res(Border.BackgroundProperty, "HbaEditorBrush");

        var name = Ui.Text(card.Name, size: 13);
        name.FontWeight = FontWeight.SemiBold;
        name.TextTrimming = TextTrimming.CharacterEllipsis;

        var sub = Ui.Text(string.IsNullOrEmpty(card.ItemType) ? "no item type" : card.ItemType, "hint");
        sub.TextWrapping = TextWrapping.NoWrap;
        sub.TextTrimming = TextTrimming.CharacterEllipsis;

        var (statusText, statusBrush) = card.Error is not null
            ? ("unreadable card file", "HbaErrorBrush")
            : StatusLabel(card.ModelStatus);
        var dot = new Avalonia.Controls.Shapes.Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center }
            .Res(Avalonia.Controls.Shapes.Shape.FillProperty, statusBrush);
        var status = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children = { dot, Ui.Text(statusText, "hint") },
        };

        var info = new StackPanel { Margin = new Thickness(12, 10, 12, 10), Spacing = 3, Children = { name, sub, status } };

        var tile = new Border
        {
            Width = TileWidth,
            Height = TileHeight,
            Margin = new Thickness(0, 0, 14, 14),
            Child = new DockPanel { Children = { thumb, info } },
        };
        DockPanel.SetDock(thumb, Dock.Top);
        tile.Classes.Add("tile");
        tile.Classes.Set("open", string.Equals(card.Id, _openId, StringComparison.Ordinal));
        ToolTip.SetTip(tile, card.Error is null ? $"cards/{card.Id}.json" : $"cards/{card.Id}.json\n{card.Error}");

        tile.PointerReleased += async (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                await _host.OpenCardInteractiveAsync(card.Id);
            }
        };

        var open = new MenuItem { Header = "Open" };
        open.Click += async (_, _) => await _host.OpenCardInteractiveAsync(card.Id);
        var duplicate = new MenuItem { Header = "Duplicate..." };
        duplicate.Click += async (_, _) => await _host.DuplicateCardInteractiveAsync(card.Id);
        var reveal = new MenuItem { Header = "Reveal in Folder" };
        reveal.Click += (_, _) => _host.RevealCard(card.Id);
        var delete = new MenuItem { Header = "Delete..." };
        delete.Classes.Add("danger");
        delete.Click += async (_, _) => await _host.DeleteCardInteractiveAsync(card.Id);
        var menu = new ContextMenu();
        menu.Items.Add(open);
        menu.Items.Add(duplicate);
        menu.Items.Add(reveal);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
        tile.ContextMenu = menu;

        return tile;
    }

    internal static (string Text, string BrushKey) StatusLabel(ModelStatus status) => status switch
    {
        ModelStatus.Ok => ("model ok", "HbaAccentBrush"),
        ModelStatus.Missing => ("model missing", "HbaErrorBrush"),
        ModelStatus.Changed => ("model changed", "HbaWarningBrush"),
        _ => ("no model", "HbaMutedBrush"),
    };
}

using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using FlashDesktop.Core;

namespace FlashDesktop.UI;

/// <summary>
/// One category card. Renders an icon grid, launches on double-click, accepts
/// external file drops (Copy/Link ONLY - never Move, red line 1) and in-panel
/// tile moves. Themed per plan §4, first-appear animation per §5.
/// </summary>
public sealed class CardView : Border
{
    private readonly CardContext _ctx;
    private readonly string _category;
    private readonly Palette _pal;
    private readonly double _tileW;
    private readonly double _iconSize;
    private ItemEntry? _dragItem;
    private Point _dragStart;
    private DateTime _lastClick = DateTime.MinValue;

    public CardView(CardContext ctx, string category)
    {
        _ctx = ctx;
        _category = category;
        _pal = Theme.Get(ctx.Settings);
        _tileW = ctx.Settings.TileSize;
        _iconSize = Math.Round(_tileW * 0.57);

        CornerRadius = new CornerRadius(ctx.Settings.CornerRadius);
        Background = _pal.CardBg;
        BorderBrush = _pal.Border;
        BorderThickness = new Thickness(1);
        Padding = new Thickness(16);
        AllowDrop = true;

        DragOver += OnDragOver;
        Drop += OnDrop;
        Loaded += OnLoaded;

        Build();
    }

    private void Build()
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(0, GridUnitType.Auto) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var title = new TextBlock
        {
            Text = _category,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = _pal.Title,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 10)
        };
        grid.Children.Add(title);
        Grid.SetRow(title, 0);

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        var wrap = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            ItemWidth = _tileW + 12,
            ItemHeight = _tileW + 16
        };
        foreach (var e in _ctx.ItemsIn(_category))
            wrap.Children.Add(MakeTile(e));
        scroll.Content = wrap;
        grid.Children.Add(scroll);
        Grid.SetRow(scroll, 1);

        Child = grid;
    }

    private UIElement MakeTile(ItemEntry entry)
    {
        var tile = new Border
        {
            Width = _tileW,
            Height = _tileW + 16,
            CornerRadius = new CornerRadius(Math.Max(2, _ctx.Settings.CornerRadius - 2)),
            Background = _pal.TileBg,
            Padding = new Thickness(6),
            Cursor = Cursors.Hand
        };

        var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };

        var icon = _ctx.Icons.LoadOrExtract(entry.OriginalPath);
        if (icon is null)
            sp.Children.Add(MakePlaceholder(entry));
        else
            sp.Children.Add(new Image
            {
                Width = _iconSize,
                Height = _iconSize,
                Stretch = Stretch.Uniform,
                Source = icon,
                HorizontalAlignment = HorizontalAlignment.Center
            });

        sp.Children.Add(new TextBlock
        {
            Text = entry.DisplayName,
            FontSize = 13,
            Foreground = _pal.Text,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 32,
            Margin = new Thickness(0, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center
        });

        var close = new Border
        {
            Width = 18,
            Height = 18,
            CornerRadius = new CornerRadius(9),
            Background = Theme.BrushFromHex("#000000", 0x70),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, -2, -2, 0),
            Cursor = Cursors.Hand,
            Visibility = Visibility.Collapsed,
            ToolTip = "从卡片移除（不会删除原文件）",
            Child = new TextBlock
            {
                Text = "✕",
                FontSize = 9,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            RemoveEntry(entry);
        };

        var grid = new Grid();
        grid.Children.Add(sp);
        grid.Children.Add(close);

        tile.Child = grid;

        tile.MouseEnter += (_, _) =>
        {
            tile.Background = _pal.TileHover;
            close.Visibility = Visibility.Visible;
        };
        tile.MouseLeave += (_, _) =>
        {
            tile.Background = _pal.TileBg;
            close.Visibility = Visibility.Collapsed;
        };

        // Build the menu on demand: 140 eager ContextMenus cost ~12MB of private
        // bytes for nothing. Closed menus are collectable.
        tile.MouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            var menu = BuildTileMenu(entry);
            menu.PlacementTarget = tile;
            menu.IsOpen = true;
        };
        // Border has no MouseDoubleClick (that's a Control event), so detect the
        // double-click manually via two MouseLeftButtonDown within 400ms.
        tile.MouseLeftButtonDown += (_, _) =>
        {
            var now = DateTime.Now;
            if ((now - _lastClick).TotalMilliseconds < 400)
            {
                _lastClick = DateTime.MinValue;
                Launcher.Launch(entry);
                _ctx.Store.TouchUsage(entry.Id);
            }
            else
            {
                _lastClick = now;
            }
        };

        // In-panel move (disabled while 常用置顶 is on - single rule owns the order).
        tile.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragItem = entry;
            _dragStart = e.GetPosition(null);
        };
        tile.PreviewMouseMove += (_, e) =>
        {
            if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed || _ctx.Settings.FreqSort)
                return;
            var d = e.GetPosition(null) - _dragStart;
            if (Math.Abs(d.X) > 4 || Math.Abs(d.Y) > 4)
            {
                var id = _dragItem.Id;
                _dragItem = null;
                DragDrop.DoDragDrop(tile, new DataObject("application/x-fd-item", id), DragDropEffects.Move);
            }
        };
        tile.MouseLeftButtonUp += (_, _) => _dragItem = null;

        return tile;
    }

    private void RemoveEntry(ItemEntry entry)
    {
        // Soft delete: only our own items.json changes. The real file stays put,
        // and the client can restore every removed tile in one click.
        _ctx.Store.Remove(entry.Id);
        var app = System.Windows.Application.Current;
        if (app is not null)
            app.Dispatcher.BeginInvoke(new Action(() => _ctx.NotifyChanged()));
        else
            _ctx.NotifyChanged();
    }

    private ContextMenu BuildTileMenu(ItemEntry entry)
    {
        var menu = new ContextMenu
        {
            Background = _pal.PanelBg,
            BorderBrush = _pal.Border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        menu.Items.Add(MenuAction("启动", () =>
        {
            Launcher.Launch(entry);
            _ctx.Store.TouchUsage(entry.Id);
        }));

        bool exists = entry.Kind is "file" or "folder" or "lnk" &&
                      (System.IO.File.Exists(entry.OriginalPath) ||
                       System.IO.Directory.Exists(entry.OriginalPath));
        if (exists)
            menu.Items.Add(MenuAction("打开所在位置", () => RevealInExplorer(entry)));

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuAction("从卡片移除", () => RemoveEntry(entry)));
        return menu;
    }

    private MenuItem MenuAction(string text, Action onClick)
    {
        var mi = new MenuItem
        {
            Header = text,
            Foreground = _pal.Text,
            Padding = new Thickness(10, 6, 10, 6)
        };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static void RevealInExplorer(ItemEntry entry)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + entry.OriginalPath + "\"",
                UseShellExecute = true
            });
        }
        catch { /* explorer unavailable: ignore */ }
    }

    private UIElement MakePlaceholder(ItemEntry entry)
    {
        var first = entry.DisplayName.Length > 0 ? entry.DisplayName[0].ToString() : "?";
        return new Border
        {
            Width = _iconSize,
            Height = _iconSize,
            CornerRadius = new CornerRadius(Math.Max(2, _ctx.Settings.CornerRadius)),
            Background = Theme.BrushFromHex(_ctx.Settings.Accent, 0x26),
            Child = new TextBlock
            {
                Text = first,
                FontSize = Math.Max(16, _iconSize * 0.44),
                FontWeight = FontWeights.Bold,
                Foreground = _pal.Accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
    }

    // ---- drop handling (red line 1: external files are Copy/Link only) -------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent("application/x-fd-item"))
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            // We advertise ONLY Copy. We never advertise Move, so the source
            // (Explorer) cannot move the original file off the desktop.
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        bool changed = false;
        if (e.Data.GetDataPresent("application/x-fd-item"))
        {
            var id = (string?)e.Data.GetData("application/x-fd-item");
            var entry = _ctx.Store.ById(id ?? "");
            if (entry is not null && entry.Category != _category)
            {
                _ctx.Store.MoveToCategory(entry, _category);
                changed = true;
            }
            e.Handled = true;
        }
        else if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var paths = (string[]?)e.Data.GetData(DataFormats.FileDrop);
            if (paths is not null)
            {
                foreach (var raw in paths)
                {
                    var p = raw.TrimEnd('\\');
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var name = Path.GetFileName(p);
                    bool isDir = Directory.Exists(p);
                    _ctx.Store.AddReference(p, name, _category, isDir ? "folder" : "file");
                    changed = true;
                }
            }
            e.Handled = true;
        }

        // Defer the rebuild until after this drop event has fully unwound, so we
        // never destroy the very window that is currently handling the drop.
        if (changed)
        {
            var app = System.Windows.Application.Current;
            if (app is not null)
                app.Dispatcher.BeginInvoke(new Action(() => _ctx.NotifyChanged()));
            else
                _ctx.NotifyChanged();
        }
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (!_ctx.Settings.Animation) return;
        var t = new TranslateTransform(0, 4);
        RenderTransform = t;
        Opacity = 0;

        var sb = new Storyboard();
        var oa = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150));
        Storyboard.SetTarget(oa, this);
        Storyboard.SetTargetProperty(oa, new PropertyPath(OpacityProperty));
        var ya = new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(150));
        Storyboard.SetTarget(ya, t);
        Storyboard.SetTargetProperty(ya, new PropertyPath(TranslateTransform.YProperty));
        sb.Children.Add(oa);
        sb.Children.Add(ya);
        sb.Completed += (_, _) => { RenderTransform = null; };
        sb.Freeze();
        sb.Begin();
    }
}

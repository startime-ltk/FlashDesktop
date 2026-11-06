using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using FlashDesktop.Core;

namespace FlashDesktop.UI;

/// <summary>
/// S5 client shell: 外观风格 / 常规设置 / 检查更新 / 关于.
/// Pure code-built WPF (no XAML, no extra assemblies). Opened on demand from the
/// tray, never resident while closed. All changes persist to settings.json and
/// rebuild the cards immediately - no timers, no polling, no registry writes.
/// </summary>
public sealed class ClientWindow : Window
{
    private readonly CardContext _ctx;
    private readonly WindowLayer _layer;
    private readonly DesktopState _state;

    private string _page;
    private Palette _pal;
    private Border _contentHost = null!;
    private TextBlock _updateStatus = null!;
    private StackPanel _updateResultHost = null!;

    private const double NavWidth = 212;

    public ClientWindow(CardContext ctx, WindowLayer layer, DesktopState state,
        string initialPage = "外观风格")
    {
        _ctx = ctx;
        _layer = layer;
        _state = state;
        _page = initialPage;
        _pal = Theme.Get(ctx.Settings);

        Title = "FlashDesktop 客户端";
        Width = 960;
        Height = 660;
        MinWidth = 820;
        MinHeight = 560;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Background = _pal.WindowBg;
        FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI");

        BuildShell();
    }

    public void NavigateTo(string page)
    {
        _page = page;
        RefreshTheme();
    }

    // ---------------------------------------------------------------- shell --

    private void BuildShell()
    {
        _pal = Theme.Get(_ctx.Settings);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(46) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        root.Children.Add(BuildTitleBar());

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(NavWidth) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var side = BuildSidebar();
        body.Children.Add(side);
        Grid.SetColumn(side, 0);

        _contentHost = new Border { Background = _pal.WindowBg };
        body.Children.Add(_contentHost);
        Grid.SetColumn(_contentHost, 1);

        root.Children.Add(body);
        Grid.SetRow(body, 1);

        Content = root;
        RenderPage();
    }

    private UIElement BuildTitleBar()
    {
        var bar = new Border
        {
            Background = _pal.SidebarBg,
            BorderBrush = _pal.Border,
            BorderThickness = new Thickness(0, 0, 0, 1)
        };

        var g = new Grid { Margin = new Thickness(18, 0, 8, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = _pal.Accent,
            Margin = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(Txt("FD 桌面", 13.5, _pal.Text, FontWeights.SemiBold));
        left.Children.Add(Txt("  客户端", 12, _pal.TextDim));
        g.Children.Add(left);

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(ChromeButton("—", () => WindowState = WindowState.Minimized, false));
        right.Children.Add(ChromeButton("✕", Close, true));
        g.Children.Add(right);
        Grid.SetColumn(right, 2);

        bar.Child = g;
        bar.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); } catch { /* not ready yet */ }
            }
        };
        return bar;
    }

    private Border ChromeButton(string glyph, Action onClick, bool danger)
    {
        var b = new Border
        {
            Width = 38,
            Height = 30,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Transparent,
            Child = new TextBlock
            {
                Text = glyph,
                FontSize = 12,
                Foreground = _pal.TextDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        b.MouseEnter += (_, _) => b.Background = danger
            ? new SolidColorBrush(Color.FromRgb(0xE8, 0x11, 0x23))
            : _pal.NavHover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonDown += (_, e) => e.Handled = true; // don't start a window drag
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private UIElement BuildSidebar()
    {
        var side = new Border
        {
            Background = _pal.SidebarBg,
            BorderBrush = _pal.Border,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };

        var g = new Grid();
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Margin = new Thickness(20, 22, 20, 14) };
        head.Children.Add(Txt("FlashDesktop", 17, _pal.Text, FontWeights.Bold));
        head.Children.Add(Txt("v" + Updater.CurrentVersion + " · 本地客户端", 11.5, _pal.TextDim));
        g.Children.Add(head);

        var nav = new StackPanel { Margin = new Thickness(12, 4, 12, 0) };
        nav.Children.Add(NavItem("外观风格", "调色 / 卡片 / 磁贴"));
        nav.Children.Add(NavItem("常规设置", "动画 / 排序 / 热键"));
        nav.Children.Add(NavItem("检查更新", "版本 / 下载"));
        nav.Children.Add(NavItem("关于", "信息 / 数据"));
        g.Children.Add(nav);
        Grid.SetRow(nav, 1);

        var foot = new StackPanel { Margin = new Thickness(20, 0, 20, 20) };
        var dataBtn = Pill("打开数据目录", OpenDataDir, false);
        dataBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        foot.Children.Add(dataBtn);
        foot.Children.Add(new TextBlock
        {
            Text = "数据全部留在本机，不上传、不改系统设置",
            FontSize = 10.5,
            Foreground = _pal.TextDim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });
        g.Children.Add(foot);
        Grid.SetRow(foot, 2);

        side.Child = g;
        return side;
    }

    private UIElement NavItem(string page, string hint)
    {
        bool active = _page == page;
        var b = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = active ? _pal.NavSelected : Brushes.Transparent,
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 2, 0, 2),
            Cursor = Cursors.Hand
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Border
        {
            Width = 3,
            Height = 18,
            CornerRadius = new CornerRadius(2),
            Background = active ? _pal.Accent : Brushes.Transparent,
            Margin = new Thickness(0, 0, 9, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        var texts = new StackPanel();
        texts.Children.Add(Txt(page, 13, active ? _pal.Text : _pal.TextDim,
            active ? FontWeights.SemiBold : FontWeights.Normal));
        texts.Children.Add(Txt(hint, 10.5, _pal.TextDim));
        row.Children.Add(texts);

        b.Child = row;
        if (!active)
        {
            b.MouseEnter += (_, _) => b.Background = _pal.NavHover;
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        }
        b.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_page == page) return;
            _page = page;
            Dispatcher.BeginInvoke(new Action(RefreshTheme));
        };
        return b;
    }

    private void RefreshTheme()
    {
        // Rebuild with the current palette (theme / accent / card style may have changed).
        BuildShell();
    }

    private void RenderPage()
    {
        UIElement page = _page switch
        {
            "常规设置" => BuildGeneralPage(),
            "检查更新" => BuildUpdatePage(),
            "关于" => BuildAboutPage(),
            _ => BuildAppearancePage()
        };

        _contentHost.Child = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(28, 24, 28, 28),
            Content = page
        };
    }

    // ---------------------------------------------------------------- pages --

    private UIElement BuildAppearancePage()
    {
        var s = _ctx.Settings;
        var stack = new StackPanel();
        stack.Children.Add(Header("外观风格", "改完立刻生效，卡片会原地重建，桌面图标不动。"));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("主题", "跟随系统时，Windows 切换深浅色卡片会跟着变。"),
                Segmented(
                    new[] { "跟随系统", "深色", "浅色" },
                    new[] { "auto", "dark", "light" },
                    s.Theme,
                    v => { s.Theme = v; Apply(AppearanceKind.Theme); })
            }
        }));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("主色调", "选中色用于高亮、占位图标和客户端强调色。"),
                Swatches()
            }
        }));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("卡片风格", "纯色最省；半透明会透出一点桌面；渐变保留原来的质感。"),
                Segmented(
                    new[] { "纯色", "半透明", "渐变" },
                    new[] { "solid", "translucent", "gradient" },
                    s.CardStyle,
                    v => { s.CardStyle = v; Apply(AppearanceKind.Theme); })
            }
        }));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("卡片圆角", "0 = 直角，16 = 很圆。"),
                SliderRow(0, 16, s.CornerRadius, v =>
                {
                    s.CornerRadius = (int)Math.Round(v);
                    Apply(AppearanceKind.LayoutOnly);
                }, "px")
            }
        }));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("磁贴大小", "影响每个图标的格子尺寸和图标显示大小。"),
                SliderRow(64, 110, s.TileSize, v =>
                {
                    s.TileSize = (int)Math.Round(v / 2) * 2;
                    Apply(AppearanceKind.LayoutOnly);
                }, "px")
            }
        }));

        return stack;
    }

    private UIElement BuildGeneralPage()
    {
        var s = _ctx.Settings;
        var stack = new StackPanel();
        stack.Children.Add(Header("常规设置", "这些开关只改本程序的行为，不写注册表。"));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                ToggleRow("入场动画", "卡片首次出现时 150ms 淡入上浮。", s.Animation,
                    v => { s.Animation = v; Apply(AppearanceKind.None); }),
                Divider(),
                ToggleRow("常用置顶", "按使用次数排序；开启时禁用磁贴手动拖拽。", s.FreqSort,
                    v => { s.FreqSort = v; Apply(AppearanceKind.None); }),
                Divider(),
                ToggleRow("Alt+Space 浮层", "按 Alt+Space 弹出快捷启动浮层（重启后生效）。",
                    s.ShowOverlayHotkey,
                    v => { s.ShowOverlayHotkey = v; Apply(AppearanceKind.None); })
            }
        }));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("面板控制", "隐藏后可从托盘图标一键恢复，桌面布局不受影响。"),
                Actions(
                    Pill(_state.CardsVisible ? "立即隐藏面板" : "立即显示面板", () =>
                    {
                        if (_state.CardsVisible) _layer.Hide(); else _layer.Show();
                        RefreshTheme();
                    }, true),
                    Pill("重建卡片", () => { _ctx.NotifyChanged(); RefreshTheme(); }, false))
            }
        }));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("维护", "缓存删掉后会自动重新提取，不影响桌面文件。"),
                Actions(
                    Pill("重建图标缓存", RebuildIconCache, false),
                    Pill("打开数据目录", OpenDataDir, false))
            }
        }));

        stack.Children.Add(Note("按定案，V1 不提供开机自启，也不写任何注册表项。需要开机使用时，把 exe 的快捷方式放进启动文件夹即可（由你手动放，程序不碰系统状态）。"));

        return stack;
    }

    private UIElement BuildUpdatePage()
    {
        var s = _ctx.Settings;
        var stack = new StackPanel();
        stack.Children.Add(Header("检查更新", "点按钮才会联网，程序不后台轮询。"));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("当前版本", "单文件、framework-dependent，依赖 .NET 8 Desktop Runtime。"),
                new TextBlock
                {
                    Text = "v" + Updater.CurrentVersion,
                    FontSize = 24,
                    FontWeight = FontWeights.Bold,
                    Foreground = _pal.Text,
                    Margin = new Thickness(0, 2, 0, 0)
                },
                Txt("构建时间：" + BuildTime(), 11.5, _pal.TextDim)
            }
        }));

        var urlBox = new TextBox
        {
            Text = s.UpdateUrl,
            FontSize = 12.5,
            Padding = new Thickness(10, 8, 10, 8),
            Background = _pal.TileBg,
            Foreground = _pal.Text,
            BorderBrush = _pal.Border,
            BorderThickness = new Thickness(1),
            CaretBrush = _pal.Text,
            Margin = new Thickness(0, 8, 0, 0)
        };
        urlBox.TextChanged += (_, _) =>
        {
            s.UpdateUrl = urlBox.Text.Trim();
            s.Save(_ctx.SettingsFile);
        };

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("更新地址", "填一个 version.json 的网址，例如 GitHub raw 链接。"),
                urlBox,
                new TextBlock
                {
                    Text = "清单格式：{ \"version\": \"0.6.0\", \"notes\": \"更新说明\", \"url\": \"下载页\" }",
                    FontSize = 10.5,
                    Foreground = _pal.TextDim,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 8, 0, 0)
                }
            }
        }));

        _updateStatus = Txt("还没检查。", 12, _pal.TextDim);
        _updateResultHost = new StackPanel { Margin = new Thickness(0, 6, 0, 0) };

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("手动检查", "10 秒超时；失败会告诉你原因，不会弹系统对话框。"),
                Actions(Pill("检查更新", RunUpdateCheck, true)),
                _updateStatus,
                _updateResultHost
            }
        }));

        return stack;
    }

    private UIElement BuildAboutPage()
    {
        var stack = new StackPanel();
        stack.Children.Add(Header("关于", "一个把桌面图标按用途分卡片的本地面板。"));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        new Border
                        {
                            Width = 52, Height = 52,
                            CornerRadius = new CornerRadius(12),
                            Background = Theme.BrushFromHex(_ctx.Settings.Accent, 0x2E),
                            Margin = new Thickness(0, 0, 14, 0),
                            Child = new TextBlock
                            {
                                Text = "FD",
                                FontSize = 19,
                                FontWeight = FontWeights.Bold,
                                Foreground = _pal.Accent,
                                HorizontalAlignment = HorizontalAlignment.Center,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        },
                        new StackPanel
                        {
                            VerticalAlignment = VerticalAlignment.Center,
                            Children =
                            {
                                Txt("FlashDesktop", 19, _pal.Text, FontWeights.Bold),
                                Txt("版本 v" + Updater.CurrentVersion + " · 单文件 · 绿色免安装", 11.5, _pal.TextDim)
                            }
                        }
                    }
                },
                Divider(),
                InfoLine("收录项目", _ctx.Store.Items.Count + " 项"),
                InfoLine("分类卡片", _ctx.Categories().Count + " 张"),
                InfoLine("图标缓存", FormatBytes(_ctx.Icons.CacheBytes())),
                InfoLine("数据目录", AppPaths.DataDir),
                InfoLine("运行环境", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription)
            }
        }));

        stack.Children.Add(Panel(new StackPanel
        {
            Children =
            {
                RowTitle("设计原则", "冲突时按 1 > 2 > 3 取舍。"),
                Bullet("1  原桌面布局任何情况下都能完整找回：不移动文件、不改系统设置。"),
                Bullet("2  流畅轻量化：缓存与内存有上限，空闲不耗 CPU。"),
                Bullet("3  省电：事件驱动，没有定时器和全局钩子。")
            }
        }));

        stack.Children.Add(Note("程序只在本机读写 Data 目录下的 JSON 与图标缓存；拖入文件只记路径，不动原文件。卸载就是删掉这个文件夹。"));

        return stack;
    }

    // ------------------------------------------------------------- controls --

    private enum AppearanceKind { None, LayoutOnly, Theme }

    private void Apply(AppearanceKind kind)
    {
        _ctx.Settings.Save(_ctx.SettingsFile);
        _ctx.NotifyChanged();
        if (kind == AppearanceKind.Theme || kind == AppearanceKind.None)
            Dispatcher.BeginInvoke(new Action(RefreshTheme));
    }

    private UIElement Header(string title, string subtitle)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
        sp.Children.Add(Txt(title, 24, _pal.Text, FontWeights.Bold));
        sp.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = 12,
            Foreground = _pal.TextDim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 0)
        });
        return sp;
    }

    private Border Panel(UIElement child) => new()
    {
        Background = _pal.PanelBg,
        BorderBrush = _pal.Border,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(18, 16, 18, 16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = child
    };

    private UIElement RowTitle(string title, string desc)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        sp.Children.Add(Txt(title, 13.5, _pal.Text, FontWeights.SemiBold));
        sp.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 11.5,
            Foreground = _pal.TextDim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        return sp;
    }

    private UIElement Row(string title, string desc, UIElement control)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Txt(title, 13, _pal.Text));
        text.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 11,
            Foreground = _pal.TextDim,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0)
        });
        g.Children.Add(text);

        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        g.Children.Add(control);
        Grid.SetColumn(control, 1);
        return g;
    }

    private UIElement ToggleRow(string title, string desc, bool value, Action<bool> onChange)
    {
        var sw = Switch(value, onChange);
        var row = Row(title, desc, sw);
        row.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 6, 0, 6));
        return row;
    }

    private Border Switch(bool value, Action<bool> onChange)
    {
        bool on = value;
        var track = new Border
        {
            Width = 46,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = on ? _pal.Accent : _pal.TileHover,
            Cursor = Cursors.Hand
        };
        var knob = new System.Windows.Shapes.Ellipse
        {
            Width = 20,
            Height = 20,
            Fill = Brushes.White,
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(3)
        };
        track.Child = knob;
        track.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            on = !on;
            track.Background = on ? _pal.Accent : _pal.TileHover;
            knob.HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            onChange(on);
        };
        return track;
    }

    private UIElement Segmented(string[] labels, string[] values, string selected, Action<string> onChange)
    {
        var wrap = new WrapPanel();
        for (int i = 0; i < labels.Length; i++)
        {
            var value = values[i];
            bool active = value == selected;
            var b = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = active ? _pal.Accent : _pal.TileBg,
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 6),
                Cursor = Cursors.Hand,
                Child = new TextBlock
                {
                    Text = labels[i],
                    FontSize = 12,
                    Foreground = active ? OnAccent(_pal.AccentColor) : _pal.Text
                }
            };
            b.MouseEnter += (_, _) => { if (!active) b.Opacity = 0.8; };
            b.MouseLeave += (_, _) => b.Opacity = 1;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onChange(value); };
            wrap.Children.Add(b);
        }
        return wrap;
    }

    private UIElement Swatches()
    {
        var wrap = new WrapPanel();
        foreach (var hex in Theme.AccentPresets)
        {
            bool active = string.Equals(_ctx.Settings.Accent, hex, StringComparison.OrdinalIgnoreCase);
            var color = Theme.BrushFromHex(hex);
            var ring = new Border
            {
                Width = 34,
                Height = 34,
                CornerRadius = new CornerRadius(17),
                BorderBrush = active ? _pal.Accent : Brushes.Transparent,
                BorderThickness = new Thickness(2),
                Margin = new Thickness(0, 0, 10, 6),
                Padding = new Thickness(3),
                Cursor = Cursors.Hand,
                Child = new System.Windows.Shapes.Ellipse { Fill = color }
            };
            ring.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _ctx.Settings.Accent = hex;
                _ctx.Settings.Save(_ctx.SettingsFile);
                _ctx.NotifyChanged();
                Dispatcher.BeginInvoke(new Action(RefreshTheme));
            };
            wrap.Children.Add(ring);
        }
        return wrap;
    }

    private UIElement SliderRow(double min, double max, double value, Action<double> onChange, string suffix)
    {
        var label = Txt(FormatValue(value, suffix), 12, _pal.TextDim);
        label.MinWidth = 56;
        label.TextAlignment = TextAlignment.Right;

        var slider = new Slider
        {
            Minimum = min,
            Maximum = max,
            Value = value,
            TickFrequency = 1,
            IsSnapToTickEnabled = true,
            Width = 320,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Only rebuild the cards when the drag ends / key / wheel settles.
        // Rebuilding on every tick would make the slider stutter.
        void Commit() => onChange(slider.Value);

        slider.ValueChanged += (_, _) => label.Text = FormatValue(slider.Value, suffix);
        slider.AddHandler(System.Windows.Controls.Primitives.Thumb.DragCompletedEvent,
            new System.Windows.Controls.Primitives.DragCompletedEventHandler((_, _) => Commit()));
        slider.PreviewMouseLeftButtonUp += (_, _) => Commit();
        slider.KeyUp += (_, _) => Commit();
        slider.MouseWheel += (_, _) => Commit();

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(slider);
        row.Children.Add(label);
        return row;
    }

    private static string FormatValue(double v, string suffix) => (int)Math.Round(v) + suffix;

    private UIElement Actions(params UIElement[] items)
    {
        var wrap = new WrapPanel();
        foreach (var it in items)
        {
            if (it is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 8, 6);
            wrap.Children.Add(it);
        }
        return wrap;
    }

    private Border Pill(string text, Action onClick, bool primary)
    {
        var b = new Border
        {
            Background = primary ? _pal.Accent : _pal.TileBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 8, 14, 8),
            Cursor = Cursors.Hand,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 12,
                Foreground = primary ? OnAccent(_pal.AccentColor) : _pal.Text
            }
        };
        b.MouseEnter += (_, _) => b.Opacity = 0.85;
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    private UIElement Divider() => new Border
    {
        Height = 1,
        Background = _pal.Border,
        Margin = new Thickness(0, 8, 0, 8)
    };

    private UIElement Note(string text) => new Border
    {
        Background = Theme.BrushFromHex(_ctx.Settings.Accent, 0x14),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(14, 12, 14, 12),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            Foreground = _pal.TextDim,
            TextWrapping = TextWrapping.Wrap
        }
    };

    private UIElement Bullet(string text) => new TextBlock
    {
        Text = text,
        FontSize = 12,
        Foreground = _pal.Text,
        TextWrapping = TextWrapping.Wrap,
        Margin = new Thickness(0, 3, 0, 3)
    };

    private UIElement InfoLine(string key, string value)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var k = Txt(key, 11.5, _pal.TextDim);
        g.Children.Add(k);

        var v = new TextBlock
        {
            Text = value,
            FontSize = 11.5,
            Foreground = _pal.Text,
            TextWrapping = TextWrapping.Wrap
        };
        g.Children.Add(v);
        Grid.SetColumn(v, 1);
        return g;
    }

    private TextBlock Txt(string text, double size, Brush brush, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        FontWeight = weight ?? FontWeights.Normal,
        TextWrapping = TextWrapping.Wrap
    };

    private static Brush OnAccent(Color c)
    {
        double lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return lum > 165 ? Brushes.Black : Brushes.White;
    }

    // --------------------------------------------------------------- actions --

    private void OpenDataDir()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "\"" + AppPaths.DataDir + "\"",
                UseShellExecute = true
            });
        }
        catch { /* explorer missing: ignore */ }
    }

    private void RebuildIconCache()
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDir, "icon-cache");
            if (Directory.Exists(dir))
                foreach (var f in Directory.GetFiles(dir, "*.png"))
                {
                    try { File.Delete(f); } catch { /* in use: skip */ }
                }
        }
        catch { /* cache hygiene must never break the UI */ }

        _ctx.NotifyChanged();
        RefreshTheme();
    }

    private async void RunUpdateCheck()
    {
        _updateStatus.Text = "正在检查…（最多 10 秒）";
        _updateStatus.Foreground = _pal.TextDim;
        _updateResultHost.Children.Clear();

        var result = await Updater.CheckAsync(_ctx.Settings.UpdateUrl, CancellationToken.None);

        if (!IsLoaded) return;

        if (!string.IsNullOrEmpty(result.Error))
        {
            _updateStatus.Text = result.Error;
            _updateStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x6C));
            return;
        }

        _updateStatus.Text = result.HasUpdate
            ? "发现新版本 v" + result.Latest + "。"
            : "已是最新版本（v" + Updater.CurrentVersion + "）。";
        _updateStatus.Foreground = result.HasUpdate ? _pal.Accent : _pal.TextDim;

        var box = new StackPanel();
        box.Children.Add(Txt(result.HasUpdate ? "新版本 v" + result.Latest : "当前已是最新",
            13, _pal.Text, FontWeights.SemiBold));

        if (!string.IsNullOrWhiteSpace(result.Notes))
            box.Children.Add(new TextBlock
            {
                Text = result.Notes,
                FontSize = 11.5,
                Foreground = _pal.TextDim,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 6, 0, 0)
            });

        if (result.HasUpdate && !string.IsNullOrWhiteSpace(result.Url))
        {
            var open = Pill("打开下载页", () =>
            {
                try { Process.Start(new ProcessStartInfo(result.Url) { UseShellExecute = true }); }
                catch { /* bad url: ignore */ }
            }, true);
            open.Margin = new Thickness(0, 10, 0, 0);
            box.Children.Add(open);
        }

        _updateResultHost.Children.Add(new Border
        {
            Background = _pal.TileBg,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 12, 14, 12),
            Margin = new Thickness(0, 10, 0, 0),
            Child = box
        });
    }

    private static string BuildTime()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe) && File.Exists(exe))
                return File.GetLastWriteTime(exe).ToString("yyyy-MM-dd HH:mm");
        }
        catch { /* ignore */ }
        return "未知";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.0") + " KB";
        return (bytes / 1024.0 / 1024.0).ToString("0.0") + " MB";
    }
}

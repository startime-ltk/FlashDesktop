using System.Windows;
using System.Windows.Controls;
using FlashDesktop.Core;

namespace FlashDesktop.UI;

/// <summary>
/// V2 overlay (§3.3): an independent quick-launch entry triggered by Alt+Space.
/// Event-driven via RegisterHotKey (not a hook, not a timer). Destroyed on close
/// or when it loses focus, so it costs nothing while idle.
/// </summary>
public sealed class HotkeyLauncher : Window
{
    private readonly CardContext _ctx;

    public HotkeyLauncher(CardContext ctx)
    {
        _ctx = ctx;
        Title = "FlashDesktop · 快速启动";
        Width = 380; Height = 440; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.ToolWindow; ShowInTaskbar = false; Topmost = true;

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel { Margin = new Thickness(10) };

        foreach (var cat in _ctx.Categories())
        {
            stack.Children.Add(new TextBlock
            {
                Text = cat,
                FontWeight = FontWeights.SemiBold,
                Foreground = System.Windows.Media.Brushes.Gray,
                Margin = new Thickness(0, 8, 0, 4)
            });
            var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var e in _ctx.ItemsIn(cat))
            {
                var btn = new Button
                {
                    Content = e.DisplayName,
                    Margin = new Thickness(3),
                    Padding = new Thickness(8, 4, 8, 4),
                    Tag = e
                };
                btn.Click += (_, _) =>
                {
                    Launcher.Launch(e);
                    _ctx.Store.TouchUsage(e.Id);
                    Close();
                };
                wrap.Children.Add(btn);
            }
            stack.Children.Add(wrap);
        }

        scroll.Content = stack;
        Content = scroll;

        // Close when it loses focus - it is a transient entry, not a resident window.
        Deactivated += (_, _) => Close();
    }
}

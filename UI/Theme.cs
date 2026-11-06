using System;
using System.Windows;
using System.Windows.Media;
using FlashDesktop.Core;

namespace FlashDesktop.UI;

/// <summary>Art palette per plan §4. All brushes are static (non-animated).</summary>
public struct Palette
{
    public Brush CardBg;
    public Brush Border;
    public Brush Title;
    public Brush TileBg;
    public Brush TileHover;
    public Brush Text;
    public Brush TextDim;
    public Brush Accent;
    public Brush OverlayBg;

    // client shell
    public Brush WindowBg;
    public Brush SidebarBg;
    public Brush PanelBg;
    public Brush NavSelected;
    public Brush NavHover;
    public Color AccentColor;
}

public static class Theme
{
    public static readonly string[] AccentPresets =
    {
        "#60CDFF", // 冰蓝
        "#A78BFA", // 靛紫
        "#34D399", // 青绿
        "#FBBF24", // 琥珀
        "#FB7185", // 珊瑚
        "#94A3B8"  // 石墨
    };

    public static bool IsDark(Settings s)
    {
        if (s.Theme == "dark") return true;
        if (s.Theme == "light") return false;
        try
        {
            var v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme", 1);
            return !(v is int i && i == 1); // 0 = dark
        }
        catch { return true; }
    }

    public static Color AccentColor(Settings s)
    {
        try { return (Color)ColorConverter.ConvertFromString(s.Accent)!; }
        catch { return Color.FromRgb(0x60, 0xCD, 0xFF); }
    }

    public static Palette Get(Settings s) =>
        Get(IsDark(s), AccentColor(s), s.CardStyle);

    public static Palette Get(bool dark) =>
        Get(dark, Color.FromRgb(0x60, 0xCD, 0xFF), "gradient");

    public static Palette Get(bool dark, Color accent, string cardStyle)
    {
        var accentBrush = Freeze(new SolidColorBrush(accent));

        if (dark)
        {
            var tile = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));
            var hover = Freeze(new SolidColorBrush(Color.FromRgb(0x3D, 0x3D, 0x3D)));
            return new Palette
            {
                CardBg = CardBackground(dark, cardStyle),
                Border = Freeze(new SolidColorBrush(Color.FromArgb(0x20, 0xFF, 0xFF, 0xFF))),
                Title = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF))),
                TileBg = tile,
                TileHover = hover,
                Text = Freeze(new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD))),
                TextDim = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0xBB, 0xBB, 0xBB))),
                Accent = accentBrush,
                OverlayBg = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x20, 0x20, 0x20))),
                WindowBg = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1B))),
                SidebarBg = Freeze(new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x23))),
                PanelBg = Freeze(new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28))),
                NavSelected = Freeze(new SolidColorBrush(Blend(accent, Color.FromRgb(0x1B, 0x1B, 0x1B), 0.22))),
                NavHover = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x2E))),
                AccentColor = accent
            };
        }

        return new Palette
        {
            CardBg = CardBackground(dark, cardStyle),
            Border = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0x00, 0x00, 0x00))),
            Title = Freeze(new SolidColorBrush(Color.FromArgb(0xA0, 0x00, 0x00, 0x00))),
            TileBg = Freeze(new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3))),
            TileHover = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8))),
            Text = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22))),
            TextDim = Freeze(new SolidColorBrush(Color.FromArgb(0xB0, 0x55, 0x55, 0x55))),
            Accent = accentBrush,
            OverlayBg = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0xF3, 0xF3, 0xF3))),
            WindowBg = Freeze(new SolidColorBrush(Color.FromRgb(0xF7, 0xF7, 0xF7))),
            SidebarBg = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))),
            PanelBg = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF))),
            NavSelected = Freeze(new SolidColorBrush(Blend(accent, Color.FromRgb(0xFF, 0xFF, 0xFF), 0.18))),
            NavHover = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0xEF, 0xEF))),
            AccentColor = accent
        };
    }

    private static Brush CardBackground(bool dark, string style)
    {
        // Cards live in their own transparent windows, so alpha lets the real
        // desktop show through (no blur, no acrylic API - red line 3 keeps it cheap).
        switch (style)
        {
            case "solid":
                return Freeze(new SolidColorBrush(dark
                    ? Color.FromRgb(0x2B, 0x2B, 0x2B)
                    : Color.FromRgb(0xFF, 0xFF, 0xFF)));
            case "translucent":
                return Freeze(new SolidColorBrush(dark
                    ? Color.FromArgb(0xD0, 0x2B, 0x2B, 0x2B)
                    : Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)));
            default:
                return MakeGradient(dark ? "#303030" : "#FFFFFF",
                                    dark ? "#2B2B2B" : "#F6F6F6");
        }
    }

    public static Brush BrushFromHex(string hex, byte alpha = 0xFF)
    {
        try
        {
            var c = (Color)ColorConverter.ConvertFromString(hex)!;
            return Freeze(new SolidColorBrush(Color.FromArgb(alpha, c.R, c.G, c.B)));
        }
        catch { return Brushes.Gray; }
    }

    private static Color Blend(Color fg, Color bg, double amount)
    {
        byte Mix(byte a, byte b) => (byte)Math.Round(a * amount + b * (1 - amount));
        return Color.FromRgb(Mix(fg.R, bg.R), Mix(fg.G, bg.G), Mix(fg.B, bg.B));
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    private static Brush MakeGradient(string top, string bottom)
    {
        var g = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(top)!, 0));
        g.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(bottom)!, 1));
        g.Freeze();
        return g;
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using System;

namespace EasyDesktopLyrics.Views;

public partial class ColorPicker : UserControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPicker, Color>(nameof(Color), Colors.White);

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    // ---------- 内部数据绑定属性 ----------
    public IBrush CurrentColorBrush => new SolidColorBrush(Color);

    public string HexText
    {
        get => $"#{Color.A:X2}{Color.R:X2}{Color.G:X2}{Color.B:X2}";
        set
        {
            if (Color.TryParse(value, out var newColor) && newColor != Color)
                Color = newColor;
        }
    }

    // RGB 属性
    public int R { get => Color.R; set => Color = Color.FromArgb(Color.A, (byte)Math.Clamp(value, 0, 255), Color.G, Color.B); }
    public int G { get => Color.G; set => Color = Color.FromArgb(Color.A, Color.R, (byte)Math.Clamp(value, 0, 255), Color.B); }
    public int B { get => Color.B; set => Color = Color.FromArgb(Color.A, Color.R, Color.G, (byte)Math.Clamp(value, 0, 255)); }

    // HSL 属性
    public int H { get => GetHsl().H; set => UpdateHsl(value, S, L); }
    public int S { get => GetHsl().S; set => UpdateHsl(H, value, L); }
    public int L { get => GetHsl().L; set => UpdateHsl(H, S, value); }

    public ColorPicker()
    {
        InitializeComponent();
    }

    static ColorPicker()
    {
        ColorProperty.Changed.AddClassHandler<ColorPicker>((x, e) => x.OnColorChanged());
    }

    private void OnColorChanged()
    {
        // 当颜色变化时，通知所有绑定的 UI 更新
        RaisePropertyChanged(CurrentColorBrushProperty, null, CurrentColorBrush);
        RaisePropertyChanged(HexTextProperty, null, HexText);
        
        RaisePropertyChanged(RProperty, 0, R);
        RaisePropertyChanged(GProperty, 0, G);
        RaisePropertyChanged(BProperty, 0, B);
        
        var hsl = GetHsl();
        RaisePropertyChanged(HProperty, 0, hsl.H);
        RaisePropertyChanged(SProperty, 0, hsl.S);
        RaisePropertyChanged(LProperty, 0, hsl.L);
    }

    // ---------- 事件处理：回车或失焦时解析 Hex ----------
    private void OnHexLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        OnColorChanged(); // 强制刷新，如果解析失败会恢复原色
    }

    private void OnHexKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnColorChanged();
            e.Handled = true;
        }
    }

    // ---------- HSL 与 RGB 的数学转换 ----------
    private void UpdateHsl(int h, int s, int l)
    {
        Color = HslToRgb(h, s, l, Color.A);
    }

    private (int H, int S, int L) GetHsl()
    {
        double r = Color.R / 255.0;
        double g = Color.G / 255.0;
        double b = Color.B / 255.0;

        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double h = 0;
        double s = 0;
        double l = (max + min) / 2.0;

        if (delta != 0)
        {
            s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
            if (max == r) h = (g - b) / delta + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / delta + 2;
            else if (max == b) h = (r - g) / delta + 4;
            h /= 6;
        }

        return ((int)Math.Round(h * 360), (int)Math.Round(s * 100), (int)Math.Round(l * 100));
    }

    private Color HslToRgb(double h, double s, double l, byte alpha)
    {
        h = Math.Clamp(h, 0, 360) / 360.0;
        s = Math.Clamp(s, 0, 100) / 100.0;
        l = Math.Clamp(l, 0, 100) / 100.0;

        double r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }

        return Color.FromArgb(alpha,
            (byte)Math.Round(r * 255),
            (byte)Math.Round(g * 255),
            (byte)Math.Round(b * 255));
    }

    private double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    // 注册绑定依赖项，确保底层数据变更时 UI 能实时刷新
    private static readonly DirectProperty<ColorPicker, IBrush> CurrentColorBrushProperty =
        AvaloniaProperty.RegisterDirect<ColorPicker, IBrush>(nameof(CurrentColorBrush), o => o.CurrentColorBrush);

    private static readonly DirectProperty<ColorPicker, string> HexTextProperty =
        AvaloniaProperty.RegisterDirect<ColorPicker, string>(nameof(HexText), o => o.HexText, (o, v) => o.HexText = v);

    private static readonly DirectProperty<ColorPicker, int> RProperty =
        AvaloniaProperty.RegisterDirect<ColorPicker, int>(nameof(R), o => o.R, (o, v) => o.R = v);
    private static readonly DirectProperty<ColorPicker, int> GProperty =
        AvaloniaProperty.RegisterDirect<ColorPicker, int>(nameof(G), o => o.G, (o, v) => o.G = v);
    private static readonly DirectProperty<ColorPicker, int> BProperty =
        AvaloniaProperty.RegisterDirect<ColorPicker, int>(nameof(B), o => o.B, (o, v) => o.B = v);
    private static readonly DirectProperty<ColorPicker, int> HProperty =
        AvaloniaProperty.RegisterDirect<ColorPicker, int>(nameof(H), o => o.H, (o, v) => o.H = v);
    private static readonly DirectProperty<ColorPicker, int> SProperty =
        AvaloniaProperty.RegisterDirect<ColorPicker, int>(nameof(S), o => o.S, (o, v) => o.S = v);
    private static readonly DirectProperty<ColorPicker, int> LProperty =
        AvaloniaProperty.RegisterDirect<ColorPicker, int>(nameof(L), o => o.L, (o, v) => o.L = v);
}

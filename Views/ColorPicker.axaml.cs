using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace EasyDesktopLyrics.Views;

public partial class ColorPicker : UserControl, INotifyPropertyChanged
{
    // 依赖属性：保持与原有代码的完美兼容
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPicker, Color>(nameof(Color), Colors.White);

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    // 内部同步锁
    private bool _isUpdating;

    public ColorPicker()
    {
        InitializeComponent();
        DataContext = this;
    }

    static ColorPicker()
    {
        ColorProperty.Changed.AddClassHandler<ColorPicker>((x, e) => x.OnColorChanged(e));
    }

    private void OnColorChanged(AvaloniaPropertyChangedEventArgs e)
    {
        if (_isUpdating) return;
        _isUpdating = true;

        var c = (Color)e.NewValue!;
        Raise(nameof(CurrentColorBrush));
        Raise(nameof(HexText));
        Raise(nameof(R));
        Raise(nameof(G));
        Raise(nameof(B));
        Raise(nameof(H));
        Raise(nameof(S));
        Raise(nameof(L));

        _isUpdating = false;
    }

    // ---------- 颜色显示与 Hex ----------
    public IBrush CurrentColorBrush => new SolidColorBrush(Color);

    public string HexText
    {
        get => $"#{Color.A:X2}{Color.R:X2}{Color.G:X2}{Color.B:X2}";
        set
        {
            if (_isUpdating) return;
            if (Color.TryParse(value, out var newColor))
            {
                Color = newColor;
            }
        }
    }

    // ---------- RGB 通道 ----------
    public int R
    {
        get => Color.R;
        set => UpdateColorFromRgb((byte)Math.Clamp(value, 0, 255), Color.G, Color.B, Color.A);
    }

    public int G
    {
        get => Color.G;
        set => UpdateColorFromRgb(Color.R, (byte)Math.Clamp(value, 0, 255), Color.B, Color.A);
    }

    public int B
    {
        get => Color.B;
        set => UpdateColorFromRgb(Color.R, Color.G, (byte)Math.Clamp(value, 0, 255), Color.A);
    }

    private void UpdateColorFromRgb(byte r, byte g, byte b, byte a)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        Color = Color.FromArgb(a, r, g, b);
        _isUpdating = false;
    }

    // ---------- HSL 通道 ----------
    public int H
    {
        get => GetHsl().H;
        set => UpdateColorFromHsl(value, GetHsl().S, GetHsl().L);
    }

    public int S
    {
        get => GetHsl().S;
        set => UpdateColorFromHsl(GetHsl().H, value, GetHsl().L);
    }

    public int L
    {
        get => GetHsl().L;
        set => UpdateColorFromHsl(GetHsl().H, GetHsl().S, value);
    }

    private void UpdateColorFromHsl(int h, int s, int l)
    {
        if (_isUpdating) return;
        _isUpdating = true;
        Color = HslToRgb(h, s, l, Color.A);
        _isUpdating = false;
    }

    // ---------- 核心数学转换（RGB 与 HSL 互转） ----------

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

            if (max == r)
                h = (g - b) / delta + (g < b ? 6 : 0);
            else if (max == g)
                h = (b - r) / delta + 2;
            else if (max == b)
                h = (r - g) / delta + 4;

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
            r = g = b = l; // 灰色
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

    // ---------- 属性通知 ----------
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

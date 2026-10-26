using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace EasyDesktopLyrics.Views;

public sealed partial class ColorPicker : UserControl
{
    public static readonly StyledProperty<Color> ColorProperty =
        AvaloniaProperty.Register<ColorPicker, Color>(nameof(Color), Colors.White,
            defaultBindingMode: Avalonia.Data.BindingMode.TwoWay);

    private double _h, _s, _v;
    private double _a = 1.0;
    private bool _suppress;
    private bool _dragging;
    private double _lastHue = double.NaN;
    private Color _lastPreview = default;

    private readonly LinearGradientBrush _hueBrush = BuildHueGradient();
    private readonly LinearGradientBrush _valueBrush = BuildValueGradient();
    private readonly LinearGradientBrush _shadeBrush = BuildShadeGradient();

    public event Action<Color>? ColorChanged;

    public Color Color
    {
        get => GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    public ColorPicker()
    {
        InitializeComponent();

        SvPanel.PointerPressed += OnSvPressed;
        SvPanel.PointerMoved += OnSvPointer;
        SvPanel.PointerReleased += OnReleased;

        HueBar.PointerPressed += OnHuePressed;
        HueBar.PointerMoved += OnHuePointer;
        HueBar.PointerReleased += OnReleased;

        ValueBar.PointerPressed += OnValuePressed;
        ValueBar.PointerMoved += OnValuePointer;
        ValueBar.PointerReleased += OnReleased;

        HexBox.TextChanged += OnHexChanged;
        HexBox.LostFocus += OnHexLostFocus;
        HexBox.KeyDown += OnBoxKeyDown;
        
        ColorProperty.Changed.AddClassHandler<ColorPicker>((x, _) => x.OnColorExternallyChanged());
        SizeChanged += (_, _) => UpdateIndicators();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnColorExternallyChanged();
    }

    // ================== 拖拽指针事件 ==================
    private void OnSvPressed(object? sender, PointerPressedEventArgs e) { _dragging = true; e.Pointer.Capture(SvPanel); OnSvPointer(sender, e); }
    private void OnHuePressed(object? sender, PointerPressedEventArgs e) { _dragging = true; e.Pointer.Capture(HueBar); OnHuePointer(sender, e); }
    private void OnValuePressed(object? sender, PointerPressedEventArgs e) { _dragging = true; e.Pointer.Capture(ValueBar); OnValuePointer(sender, e); }
    private void OnReleased(object? sender, PointerReleasedEventArgs e) { _dragging = false; e.Pointer.Capture(null); }

    private void OnSvPointer(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(SvPanel).Properties.IsLeftButtonPressed) return;
        var p = e.GetCurrentPoint(SvPanel).Position;
        _s = Math.Clamp(p.X / Math.Max(1, SvPanel.Bounds.Width), 0, 1);
        _v = 1 - Math.Clamp(p.Y / Math.Max(1, SvPanel.Bounds.Height), 0, 1);
        ApplyFromHsv();
    }

    private void OnHuePointer(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(HueBar).Properties.IsLeftButtonPressed) return;
        var p = e.GetCurrentPoint(HueBar).Position;
        _h = Math.Clamp(p.X / Math.Max(1, HueBar.Bounds.Width), 0, 1) * 360;
        ApplyFromHsv();
    }

    private void OnValuePointer(object? sender, PointerEventArgs e)
    {
        if (!e.GetCurrentPoint(ValueBar).Properties.IsLeftButtonPressed) return;
        var p = e.GetCurrentPoint(ValueBar).Position;
        _v = Math.Clamp(p.X / Math.Max(1, ValueBar.Bounds.Width), 0, 1);
        ApplyFromHsv();
    }

    // ================== 核心状态更新 ==================

    private void ApplyFromHsv()
    {
        _suppress = true;
        Color = HsvToRgb(_h, _s, _v, _a);
        _suppress = false;

        if (_lastHue != _h)
        {
            _lastHue = _h;
            SvPanel.Background = BuildPanelBrush(HsvToRgb(_h, 1, 1, 1));
        }
        
        var c = Color;
        if (_lastPreview != c)
        {
            _lastPreview = c;
            PreviewBlock.Background = new SolidColorBrush(c);
        }
        
        SetHexSilently(FormatHex(c));
        UpdateIndicators();
        
        // 拖拽时如果焦点在输入框，不要覆盖输入框内容
        if (!RBox.IsFocused && !HBox.IsFocused && !HexBox.IsFocused) 
        {
            UpdateRgbHslUi(c);
        }
        
        ColorChanged?.Invoke(c);
    }

    private void OnColorExternallyChanged()
    {
        if (_suppress || _dragging) return;
        SyncHsvFromColor(Color);
        RefreshUi();
        ColorChanged?.Invoke(Color);
    }

    private void SyncHsvFromColor(Color c)
    {
        var (h, s, v) = RgbToHsv(c);
        if (s > 0.001 && v > 0.001) _h = h;
        _s = s;
        _v = v;
        _a = c.A / 255.0;
    }

    private void RefreshUi()
    {
        var hue = HsvToRgb(_h, 1, 1, 1);
        _lastHue = _h;
        _lastPreview = Color;

        SvPanel.Background = BuildPanelBrush(hue);
        SvShade.Background = _shadeBrush;
        HueBar.Background = _hueBrush;
        ValueBar.Background = _valueBrush;
        PreviewBlock.Background = new SolidColorBrush(Color);
        
        SetHexSilently(FormatHex(Color));
        UpdateIndicators();
        UpdateRgbHslUi(Color);
    }

    private void UpdateVisualsOnly()
    {
        var hue = HsvToRgb(_h, 1, 1, 1);
        _lastHue = _h;
        _lastPreview = Color;

        SvPanel.Background = BuildPanelBrush(hue);
        SvShade.Background = _shadeBrush;
        HueBar.Background = _hueBrush;
        ValueBar.Background = _valueBrush;
        PreviewBlock.Background = new SolidColorBrush(Color);
        UpdateIndicators();
    }

    // ================== RGB 和 HSL UI 事件与同步 ==================

    private void UpdateRgbHslUi(Color c)
    {
        if (_suppress) return;
        _suppress = true;

        // 只在没有焦点的时候覆盖内容，防止用户输入时被强行打断
        if (!RBox.IsFocused) RBox.Text = c.R.ToString();
        if (!GBox.IsFocused) GBox.Text = c.G.ToString();
        if (!BBox.IsFocused) BBox.Text = c.B.ToString();

        var (h, s, l) = RgbToHsl(c);
        var hr = Math.Round(h); var sr = Math.Round(s); var lr = Math.Round(l);
        
        if (!HBox.IsFocused) HBox.Text = hr.ToString();
        if (!SBox.IsFocused) SBox.Text = sr.ToString();
        if (!LBox.IsFocused) LBox.Text = lr.ToString();

        _suppress = false;
    }

    private void OnRgbBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        // 必须三个框都能解析，才去更新颜色，防止打字打一半报错
        if (byte.TryParse(RBox.Text, out var r) && byte.TryParse(GBox.Text, out var g) && byte.TryParse(BBox.Text, out var b))
        {
            _suppress = true;
            // 直接用 RGB 赋值，确保输入 172 就是 172，不因为色彩转换变成 174
            Color = Color.FromArgb((byte)Math.Round(_a * 255), r, g, b);
            _suppress = false;
            
            SyncHsvFromColor(Color);
            UpdateVisualsOnly(); // 打字时只更色盘，不动 TextBox
            ColorChanged?.Invoke(Color);
        }
    }

    private void OnHslBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (double.TryParse(HBox.Text, out var h) && double.TryParse(SBox.Text, out var s) && double.TryParse(LBox.Text, out var l))
        {
            _suppress = true;
            Color = HslToRgb(h, s, l, _a);
            _suppress = false;
            
            SyncHsvFromColor(Color);
            UpdateVisualsOnly();
            ColorChanged?.Invoke(Color);
        }
    }

    private void ApplyFromRgb(byte r, byte g, byte b)
    {
        _suppress = true;
        Color = Color.FromArgb((byte)Math.Round(_a * 255), r, g, b);
        _suppress = false;
        
        SyncHsvFromColor(Color);
        RefreshUi();
        ColorChanged?.Invoke(Color);
    }

    private void ApplyFromHsl(double h, double s, double l)
    {
        _suppress = true;
        Color = HslToRgb(h, s, l, _a);
        _suppress = false;
        
        SyncHsvFromColor(Color);
        RefreshUi();
        ColorChanged?.Invoke(Color);
    }

    // ================== 焦点与键盘事件 ==================
    
    private void OnBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        
        bool valid = false;
        if (byte.TryParse(RBox.Text, out var r) && byte.TryParse(GBox.Text, out var g) && byte.TryParse(BBox.Text, out var b))
        {
            ApplyFromRgb(r, g, b);
            valid = true;
        }
        else if (double.TryParse(HBox.Text, out var h) && double.TryParse(SBox.Text, out var s) && double.TryParse(LBox.Text, out var l))
        {
            ApplyFromHsl(h, s, l);
            valid = true;
        }

        if (!valid)
        {
            RefreshUi(); // 如果乱填，失去焦点后强行恢复正确数值
        }
    }

    private void OnHexLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!Color.TryParse(HexBox.Text?.Trim(), out _))
        {
            RefreshUi();
        }
    }

    private void OnBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnBoxLostFocus(sender, new RoutedEventArgs());
            OnHexLostFocus(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // ================== Hex 与 辅助函数 ==================

    private void OnHexChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress || HexBox.Text is null) return;
        
        var input = HexBox.Text.Trim();
        
        // 支持 6 位或 8 位解析
        if (Color.TryParse(input, out var c))
        {
            _suppress = true;
            Color = c;
            _suppress = false;
            
            SyncHsvFromColor(c);
            UpdateRgbHslUi(c); 
            UpdateVisualsOnly();
            
            // 自动转换为标准大写格式，并保证光标不打断
            string standardHex = FormatHex(c);
            
            // 如果输入/粘贴的长度已经和标准一致，且内容不同（比如有小写），则强制替换
            if (input.Length == standardHex.Length && input != standardHex)
            {
                int caretIndex = HexBox.CaretIndex; // 记录光标位置
                _suppress = true;
                HexBox.Text = standardHex; // 覆盖为大写
                HexBox.CaretIndex = Math.Min(caretIndex, standardHex.Length); // 恢复光标
                _suppress = false;
            }

            ColorChanged?.Invoke(c);
        }
    }

    private void SetHexSilently(string hex)
    {
        if (HexBox.Text == hex || HexBox.IsFocused) return;
        _suppress = true;
        HexBox.Text = hex;
        _suppress = false;
    }

    private void UpdateIndicators()
    {
        var svw = SvPanel.Bounds.Width;
        var svh = SvPanel.Bounds.Height;
        Canvas.SetLeft(SvIndicator, Math.Clamp(_s * svw - 4, -2, Math.Max(0, svw - 8)));
        Canvas.SetTop(SvIndicator, Math.Clamp((1 - _v) * svh - 4, -2, Math.Max(0, svh - 8)));
        Canvas.SetLeft(HueIndicator, _h / 360.0 * Math.Max(0, HueBar.Bounds.Width - 4));
        Canvas.SetLeft(ValueIndicator, _v * Math.Max(0, ValueBar.Bounds.Width - 4));
    }

    private static string FormatHex(Color c) =>
        $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- 色彩空间转换 ----
    private static Color HsvToRgb(double h, double s, double v, double a = 1.0)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        double r, g, b;
        if (h < 60) { r = c; g = x; b = 0; }
        else if (h < 120) { r = x; g = c; b = 0; }
        else if (h < 180) { r = 0; g = c; b = x; }
        else if (h < 240) { r = 0; g = x; b = c; }
        else if (h < 300) { r = x; g = 0; b = c; }
        else { r = c; g = 0; b = x; }
        return Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private static (double H, double S, double V) RgbToHsv(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h;
        if (d == 0) h = 0;
        else if (max == r) h = 60 * (((g - b) / d) % 6);
        else if (max == g) h = 60 * ((b - r) / d + 2);
        else h = 60 * ((r - g) / d + 4);
        if (h < 0) h += 360;
        return (h, max == 0 ? 0 : d / max, max);
    }

    private static (double H, double S, double L) RgbToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double h = 0, s = 0, l = (max + min) / 2.0;
        double d = max - min;

        if (d != 0)
        {
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else if (max == b) h = (r - g) / d + 4;
            h /= 6;
        }
        return (h * 360, s * 100, l * 100);
    }

    private static Color HslToRgb(double h, double s, double l, double a = 1.0)
    {
        h = Math.Clamp(h, 0, 360) / 360.0;
        s = Math.Clamp(s, 0, 100) / 100.0;
        l = Math.Clamp(l, 0, 100) / 100.0;
        double r, g, b;
        if (s == 0) r = g = b = l;
        else
        {
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s;
            double p = 2 * l - q;
            r = HueToRgb(p, q, h + 1.0 / 3.0);
            g = HueToRgb(p, q, h);
            b = HueToRgb(p, q, h - 1.0 / 3.0);
        }
        return Color.FromArgb((byte)Math.Round(a * 255), (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static double HueToRgb(double p, double q, double t)
    {
        if (t < 0) t += 1;
        if (t > 1) t -= 1;
        if (t < 1.0 / 6.0) return p + (q - p) * 6 * t;
        if (t < 1.0 / 2.0) return q;
        if (t < 2.0 / 3.0) return p + (q - p) * (2.0 / 3.0 - t) * 6;
        return p;
    }

    // ---- 静态渐变画笔构建 ----
    private static LinearGradientBrush BuildPanelBrush(Color hue) => new()
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(hue, 1) },
    };

    private static LinearGradientBrush BuildShadeGradient() => new()
    {
        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) },
    };

    private static LinearGradientBrush BuildValueGradient() => new()
    {
        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
        GradientStops = { new GradientStop(Colors.Black, 0), new GradientStop(Colors.White, 1) },
    };

    private static LinearGradientBrush BuildHueGradient()
    {
        static Color C(double h) => HsvToRgb(h, 1, 1, 1);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(C(0), 0.00), new GradientStop(C(60), 0.17),
                new GradientStop(C(120), 0.33), new GradientStop(C(180), 0.50),
                new GradientStop(C(240), 0.67), new GradientStop(C(300), 0.83),
                new GradientStop(C(360), 1.00),
            },
        };
    }
}

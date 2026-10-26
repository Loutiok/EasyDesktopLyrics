using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
        
        if (!RBox.IsFocused && !HBox.IsFocused && !HexBox.IsFocused) 
        {
            UpdateRgbHsvUi(c);
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
        UpdateRgbHsvUi(Color);
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

    // ================== RGB 和 HSV UI 事件与同步 ==================

    private void UpdateRgbHsvUi(Color c)
    {
        if (_suppress) return;
        _suppress = true;

        if (!RBox.IsFocused) RBox.Text = c.R.ToString();
        if (!GBox.IsFocused) GBox.Text = c.G.ToString();
        if (!BBox.IsFocused) BBox.Text = c.B.ToString();

        var (h, s, v) = RgbToHsv(c);
        if (!HBox.IsFocused) HBox.Text = Math.Round(h, 0).ToString();
        if (!SBox.IsFocused) SBox.Text = Math.Round(s * 100, 0).ToString();
        if (!VBox.IsFocused) VBox.Text = Math.Round(v * 100, 0).ToString();

        _suppress = false;
    }

    private void OnRgbBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (byte.TryParse(RBox.Text, out var r) && byte.TryParse(GBox.Text, out var g) && byte.TryParse(BBox.Text, out var b))
        {
            // 防空转拦截器
            if (Color.R == r && Color.G == g && Color.B == b) return;

            _suppress = true;
            Color = Color.FromArgb((byte)Math.Round(_a * 255), r, g, b);
            _suppress = false;
            
            SyncHsvFromColor(Color);
            UpdateVisualsOnly();
            ColorChanged?.Invoke(Color);
        }
    }

    private void OnHsvBoxChanged(object? sender, TextChangedEventArgs e)
    {
        if (_suppress) return;
        if (double.TryParse(HBox.Text, out var h) && double.TryParse(SBox.Text, out var s) && double.TryParse(VBox.Text, out var v))
        {
            // 防空转拦截器：如果当前颜色的 HSV 取整后，跟输入框一模一样，说明这只是代码刷新UI触发的，直接忽略！
            var currentHsv = RgbToHsv(Color);
            if (Math.Round(currentHsv.H) == Math.Round(h) && 
                Math.Round(currentHsv.S * 100) == Math.Round(s) && 
                Math.Round(currentHsv.V * 100) == Math.Round(v))
            {
                return;
            }

            _suppress = true;
            _h = h;
            _s = Math.Clamp(s / 100.0, 0, 1);
            _v = Math.Clamp(v / 100.0, 0, 1);
            
            Color = HsvToRgb(_h, _s, _v, _a);
            _suppress = false;
            
            UpdateVisualsOnly();
            ColorChanged?.Invoke(Color);
        }
    }

    // ================== 焦点与键盘事件 ==================
    
    private void OnBoxLostFocus(object? sender, RoutedEventArgs e)
    {
        if (_suppress) return;
        RefreshUi();
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
        
        if (Color.TryParse(input, out var c))
        {
            // 防空转拦截器
            if (Color == c) return;

            _suppress = true;
            Color = c;
            _suppress = false;
            
            SyncHsvFromColor(c);
            UpdateVisualsOnly();
            UpdateRgbHsvUi(c);
            
            string standardHex = FormatHex(c);
            
            if (input.Length == standardHex.Length && input != standardHex)
            {
                int caretIndex = HexBox.CaretIndex; 
                _suppress = true;
                HexBox.Text = standardHex; 
                HexBox.CaretIndex = Math.Min(caretIndex, standardHex.Length); 
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
        Canvas.SetLeft(SvIndicator, Math.Clamp(_s * svw - 6, -2, Math.Max(0, svw - 10)));
        Canvas.SetTop(SvIndicator, Math.Clamp((1 - _v) * svh - 6, -2, Math.Max(0, svh - 10)));
        Canvas.SetLeft(HueIndicator, _h / 360.0 * Math.Max(0, HueBar.Bounds.Width - 6));
        Canvas.SetLeft(ValueIndicator, _v * Math.Max(0, ValueBar.Bounds.Width - 6));
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

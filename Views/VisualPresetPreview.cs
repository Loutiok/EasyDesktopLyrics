using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;

namespace EasyDesktopLyrics.Views;

/// <summary>
/// 设置页的轻量视觉预览。只在进入可视树后刷新，使用合成数据展示歌词辉光、
/// 动画幅度与背景层级，不启动 WASAPI，也不影响正式歌词时间轴。
/// </summary>
public sealed class VisualPresetPreview : Control
{
    private const string PreviewText = "让歌词随旋律流动";
    private static readonly IBrush SurfaceBrush = new SolidColorBrush(Color.FromRgb(13, 15, 22));
    private static readonly IBrush InactiveTextBrush = new SolidColorBrush(Color.FromArgb(105, 255, 255, 255));
    private static readonly IBrush ActiveTextBrush = Brushes.White;
    private static readonly Pen EdgePen = new(
        new SolidColorBrush(Color.FromArgb(42, 255, 255, 255)), 1);

    public static readonly StyledProperty<string> PresetIdProperty =
        AvaloniaProperty.Register<VisualPresetPreview, string>(nameof(PresetId), "custom");

    public static readonly StyledProperty<double> GlowStrengthProperty =
        AvaloniaProperty.Register<VisualPresetPreview, double>(nameof(GlowStrength), 1);

    public static readonly StyledProperty<double> AnimationAmplitudeProperty =
        AvaloniaProperty.Register<VisualPresetPreview, double>(nameof(AnimationAmplitude), 40);

    public static readonly StyledProperty<double> BackgroundOpacityProperty =
        AvaloniaProperty.Register<VisualPresetPreview, double>(nameof(BackgroundOpacity), 0.7);

    public static readonly StyledProperty<int> PerformanceLevelProperty =
        AvaloniaProperty.Register<VisualPresetPreview, int>(nameof(PerformanceLevel), 1);

    private readonly DispatcherTimer _timer = new();
    private readonly Dictionary<int, IBrush[]> _auroraBrushCache = [];
    private readonly BlurEffect _waveBlur = new() { Radius = 5 };
    private readonly DropShadowEffect _textGlow = new();
    private Pen _waveGlowPen = new(Brushes.Transparent, 3.2);
    private Pen _waveLinePen = new(Brushes.Transparent, 1.15);
    private TextLayout? _activeLayout;
    private TextLayout? _inactiveLayout;
    private double _phase;

    static VisualPresetPreview()
    {
        AffectsRender<VisualPresetPreview>(
            PresetIdProperty, GlowStrengthProperty, AnimationAmplitudeProperty,
            BackgroundOpacityProperty, PerformanceLevelProperty);
    }

    public VisualPresetPreview()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;
        RefreshDrawingResources();
        _timer.Tick += OnTick;
        AttachedToVisualTree += (_, _) =>
        {
            ConfigureTimer();
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) => _timer.Stop();
    }

    public string PresetId
    {
        get => GetValue(PresetIdProperty);
        set => SetValue(PresetIdProperty, value);
    }

    public double GlowStrength
    {
        get => GetValue(GlowStrengthProperty);
        set => SetValue(GlowStrengthProperty, value);
    }

    public double AnimationAmplitude
    {
        get => GetValue(AnimationAmplitudeProperty);
        set => SetValue(AnimationAmplitudeProperty, value);
    }

    public double BackgroundOpacity
    {
        get => GetValue(BackgroundOpacityProperty);
        set => SetValue(BackgroundOpacityProperty, value);
    }

    public int PerformanceLevel
    {
        get => GetValue(PerformanceLevelProperty);
        set => SetValue(PerformanceLevelProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PerformanceLevelProperty)
            ConfigureTimer();
        if (change.Property == GlowStrengthProperty ||
            change.Property == BackgroundOpacityProperty ||
            change.Property == PresetIdProperty)
        {
            RefreshDrawingResources();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var area = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (area.Width <= 0 || area.Height <= 0)
            return;

        using (context.PushClip(new RoundedRect(area, 9)))
        {
            context.DrawRectangle(SurfaceBrush, null, area);
            var mode = EffectivePresetId();
            if (mode == "immersive")
                DrawAurora(context, area);
            if (mode is "luminous" or "immersive")
                DrawWave(context, area, mode == "immersive");
            DrawLyrics(context, area, mode);
            DrawEdge(context, area);
        }
    }

    private void DrawAurora(DrawingContext context, Rect area)
    {
        var brushes = GetAuroraBrushes();
        var breathe = 1 + 0.07 * Math.Sin(_phase * 1.7);
        var width = area.Width;
        var height = area.Height;
        for (var i = 0; i < brushes.Length; i++)
        {
            var x = width * (0.48 + 0.42 * Math.Sin(_phase * (0.24 + i * 0.05) + i * 2.1));
            var y = height * (0.48 + 0.34 * Math.Sin(_phase * (0.18 + i * 0.04) + i * 1.6));
            context.DrawEllipse(
                brushes[i], null, new Point(x, y),
                width * (0.38 + i * 0.04) * breathe,
                height * (0.75 + i * 0.08) * breathe);
        }
    }

    private void DrawWave(DrawingContext context, Rect area, bool immersive)
    {
        const int pointCount = 19;
        var points = new Point[pointCount];
        var width = area.Width * (immersive ? 0.90 : 0.72);
        var startX = (area.Width - width) / 2;
        var baseline = area.Height * 0.72;
        var amplitude = 3 + Math.Clamp(AnimationAmplitude, 0, 100) * 0.055;
        for (var i = 0; i < pointCount; i++)
        {
            var normalized = i / (double)(pointCount - 1);
            var envelope = Math.Sin(Math.PI * normalized);
            var wave = Math.Sin(_phase * 2.2 + i * 0.82) * 0.62 +
                Math.Sin(_phase * 1.35 - i * 0.39) * 0.38;
            points[i] = new Point(startX + normalized * width, baseline - wave * amplitude * envelope);
        }

        var geometry = BuildSmoothCurve(points);
        using (context.PushEffect(_waveBlur, area))
            context.DrawGeometry(null, _waveGlowPen, geometry);
        context.DrawGeometry(null, _waveLinePen, geometry);
    }

    private void DrawLyrics(DrawingContext context, Rect area, string mode)
    {
        EnsureTextLayouts();
        if (_activeLayout == null || _inactiveLayout == null)
            return;

        var cycle = (_phase * 0.22) % 1;
        var progress = 0.12 + cycle * 0.82;
        var amplitude = Math.Clamp(AnimationAmplitude, 0, 100) / 100.0;
        var enterPulse = Math.Pow(Math.Max(0, Math.Sin(_phase * Math.PI * 0.44)), 5);
        var offsetX = mode == "immersive" ? (1 - enterPulse) * 7 * amplitude : 0;
        var x = (area.Width - _activeLayout.Width) / 2 + offsetX;
        var y = area.Height * 0.40 - _activeLayout.Height / 2;
        var textBounds = new Rect(x, y, _activeLayout.Width, _activeLayout.Height);

        _inactiveLayout.Draw(context, new Point(x, y));

        var strength = Math.Clamp(GlowStrength, 0.2, 2);
        if (mode is "luminous" or "immersive" || strength > 0.2)
        {
            var glowBounds = textBounds.Inflate(8 + strength * 12);
            _textGlow.Opacity = (mode == "classic" ? 0.22 : 0.32 + strength * 0.14) *
                (mode == "classic" ? 1 : 0.72 + 0.28 * Math.Sin(_phase * 1.8));
            using (context.PushEffect(_textGlow, glowBounds))
                _activeLayout.Draw(context, new Point(x, y));
        }

        using (context.PushClip(new Rect(x, y, _activeLayout.Width * progress, _activeLayout.Height)))
            _activeLayout.Draw(context, new Point(x, y));
    }

    private static void DrawEdge(DrawingContext context, Rect area)
    {
        context.DrawRectangle(null, EdgePen, new RoundedRect(area.Deflate(0.5), 9));
    }

    private IBrush[] GetAuroraBrushes()
    {
        var opacityStep = (int)Math.Round(Math.Clamp(BackgroundOpacity, 0, 1) * 20);
        if (_auroraBrushCache.TryGetValue(opacityStep, out var cached))
            return cached;

        var opacity = opacityStep / 20.0;
        var colors = new[]
        {
            Color.FromRgb(98, 82, 210),
            Color.FromRgb(46, 173, 208),
            Color.FromRgb(210, 72, 153),
        };
        var brushes = colors.Select(color => (IBrush)new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)Math.Round(105 * opacity), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb((byte)Math.Round(42 * opacity), color.R, color.G, color.B), 0.48),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        }).ToArray();
        _auroraBrushCache[opacityStep] = brushes;
        return brushes;
    }

    private void EnsureTextLayouts()
    {
        if (_activeLayout != null)
            return;
        var typeface = new Typeface(
            new FontFamily("Microsoft YaHei UI"), FontStyle.Normal, FontWeight.SemiBold, FontStretch.Normal);
        const double fontSize = 28;
        var lineHeight = Math.Ceiling(fontSize * 1.4 * 2) / 2;
        _activeLayout = new TextLayout(
            PreviewText, typeface, fontSize, ActiveTextBrush,
            textAlignment: TextAlignment.Left,
            textWrapping: TextWrapping.NoWrap,
            maxWidth: double.PositiveInfinity,
            maxHeight: double.PositiveInfinity,
            lineHeight: lineHeight);
        _inactiveLayout = new TextLayout(
            PreviewText, typeface, fontSize, InactiveTextBrush,
            textAlignment: TextAlignment.Left,
            textWrapping: TextWrapping.NoWrap,
            maxWidth: double.PositiveInfinity,
            maxHeight: double.PositiveInfinity,
            lineHeight: lineHeight);
    }

    private static Geometry BuildSmoothCurve(Point[] points)
    {
        var geometry = new StreamGeometry();
        using var stream = geometry.Open();
        stream.BeginFigure(points[0], false);
        for (var i = 0; i < points.Length - 1; i++)
        {
            var p1 = points[i];
            var p2 = points[i + 1];
            var p0 = i > 0 ? points[i - 1] : p1;
            var p3 = i + 2 < points.Length ? points[i + 2] : p2;
            stream.CubicBezierTo(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2);
        }
        stream.EndFigure(false);
        return geometry;
    }

    private string EffectivePresetId() => PresetId is "classic" or "luminous" or "immersive"
        ? PresetId
        : "luminous";

    private void ConfigureTimer()
    {
        var fps = Math.Clamp(PerformanceLevel, 0, 2) switch
        {
            0 => 30,
            2 => 90,
            _ => 60,
        };
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / fps);
    }

    private void RefreshDrawingResources()
    {
        var mode = EffectivePresetId();
        var strength = Math.Clamp(GlowStrength, 0.2, 2);
        _textGlow.Color = mode == "classic" ? Colors.White : Color.FromRgb(184, 125, 255);
        _textGlow.BlurRadius = (mode == "classic" ? 5 : 10) +
            strength * (mode == "classic" ? 6 : 11);

        var opacity = Math.Clamp(BackgroundOpacity, 0, 1);
        var glowColor = Color.FromArgb((byte)Math.Round(70 * opacity), 61, 213, 255);
        var lineColor = Color.FromArgb((byte)Math.Round(220 * opacity), 150, 238, 255);
        _waveGlowPen = new Pen(new SolidColorBrush(glowColor), 3.2);
        _waveLinePen = new Pen(new SolidColorBrush(lineColor), 1.15);
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _phase += _timer.Interval.TotalSeconds;
        InvalidateVisual();
    }
}

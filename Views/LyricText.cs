using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Utilities;

namespace EasyDesktopLyrics.Views;

/// <summary>
/// 自绘歌词文本：非逐字时整行单色 TextLayout 平涂；
/// 逐字（卡拉 OK）时整行一次绘制 + 线性渐变笔刷——每个字符按"已唱色 → 分界线 → 柔和过渡带 → 未唱色"
/// 平滑渐变，分界线在当前字内部随 HighlightFraction 平滑右移（真渐变填充，非硬裁剪）。
/// 描边 = 4向锐利偏移，视觉厚度减半，无发糊感。
/// </summary>
public sealed class LyricText : Control
{
    public LyricText()
    {
        // 禁用 ClearType 亚像素渲染，使用灰度抗锯齿。
        // 彻底解决透明窗口下文字边缘出现红、蓝、黄彩色毛边的问题。
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
    }

    /// <summary>层角色：整行、已唱段、未唱段或指定字符。</summary>
    public enum ClipSideMode
    {
        None = 0,
        Left = 1,
        Right = 2,
        Character = 3,
    }

    // 为偏移描边预留一点空间，防止被父容器或控件自身边界裁剪
    private const double TextPadding = 2.0;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<LyricText, string?>(nameof(Text));

    /// <summary>已唱字符数；-1 = 整行单色（无逐字数据）。</summary>
    public static readonly StyledProperty<int> HighlightLengthProperty =
        AvaloniaProperty.Register<LyricText, int>(nameof(HighlightLength), -1);

    /// <summary>正在唱字符的渐变进度 0~1（分界线在字内从左向右移动）。</summary>
    public static readonly StyledProperty<double> HighlightFractionProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(HighlightFraction), 0);

    /// <summary>层角色（固定，由宿主设置）：Left=已唱层、Right=未唱层、None=平涂。</summary>
    public static readonly StyledProperty<ClipSideMode> HighlightClipProperty =
        AvaloniaProperty.Register<LyricText, ClipSideMode>(nameof(HighlightClip), ClipSideMode.None);

    /// <summary>Character 遮罩模式绘制的字符索引；-1 = 不绘制。</summary>
    public static readonly StyledProperty<int> MaskCharacterIndexProperty =
        AvaloniaProperty.Register<LyricText, int>(nameof(MaskCharacterIndex), -1);

    /// <summary>逐字分段直接绘制字形，不通过矩形透明遮罩；用于会应用模糊/阴影的辉光层。</summary>
    public static readonly StyledProperty<bool> UseGlyphClippingProperty =
        AvaloniaProperty.Register<LyricText, bool>(nameof(UseGlyphClipping), false);

    /// <summary>在绘制命令内应用于字形内容的特效；与 Visual.Effect 不同，会按输出范围自动膨胀。</summary>
    public static readonly StyledProperty<IEffect?> RenderEffectProperty =
        AvaloniaProperty.Register<LyricText, IEffect?>(nameof(RenderEffect));

    /// <summary>直接写入字形画笔的透明度，避免 Visual.Opacity 再次创建按控件边界合成的图层。</summary>
    public static readonly StyledProperty<double> RenderOpacityProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(RenderOpacity), 1);

    /// <summary>行进入逐字显现进度 0~1（从左到右逐个字符点亮）；-1 = 禁用（正常渲染）。</summary>
    public static readonly StyledProperty<double> EnterProgressProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(EnterProgress), -1);

    /// <summary>扫光带位置进度 0~1（亮带沿行从左到右扫描）；-1 = 禁用。</summary>
    public static readonly StyledProperty<double> ShineProgressProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(ShineProgress), -1);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        AvaloniaProperty.Register<LyricText, FontFamily>(nameof(FontFamily), FontFamily.Default);

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        AvaloniaProperty.Register<LyricText, FontWeight>(nameof(FontWeight), FontWeight.Normal);

    public static readonly StyledProperty<double> FontSizeProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(FontSize), 12);

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<LyricText, IBrush?>(nameof(Fill));

    /// <summary>未唱段颜色（逐字模式）。</summary>
    public static readonly StyledProperty<IBrush?> InactiveFillProperty =
        AvaloniaProperty.Register<LyricText, IBrush?>(nameof(InactiveFill));

    public static readonly StyledProperty<IBrush?> StrokeBrushProperty =
        AvaloniaProperty.Register<LyricText, IBrush?>(nameof(StrokeBrush));

    /// <summary>描边总开关（false 时即使 StrokeThickness&gt;0 也不绘制）。</summary>
    public static readonly StyledProperty<bool> StrokeEnabledProperty =
        AvaloniaProperty.Register<LyricText, bool>(nameof(StrokeEnabled), false);

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(StrokeThickness), 0);

    /// <summary>未唱段描边开关（逐字模式；false 时未唱段描边透明，避免整行描边抹平明暗差）。</summary>
    public static readonly StyledProperty<bool> InactiveStrokeEnabledProperty =
        AvaloniaProperty.Register<LyricText, bool>(nameof(InactiveStrokeEnabled), false);

    /// <summary>未唱段描边画笔（逐字模式）。</summary>
    public static readonly StyledProperty<IBrush?> InactiveStrokeBrushProperty =
        AvaloniaProperty.Register<LyricText, IBrush?>(nameof(InactiveStrokeBrush));

    public static readonly StyledProperty<double> MaxTextWidthProperty =
        AvaloniaProperty.Register<LyricText, double>(nameof(MaxTextWidth), double.PositiveInfinity);

    public static readonly StyledProperty<TextAlignment> TextAlignmentProperty =
        AvaloniaProperty.Register<LyricText, TextAlignment>(nameof(TextAlignment), TextAlignment.Center);

    private TextLayout? _bodyLayout;
    private TextLayout? _strokeLayout;
    private bool _layoutDirty = true;
    private double[] _charLeft = [];
    private readonly Dictionary<(int Start, int Length, byte OpacityStep), TextLayout> _glyphLayouts = [];

    static LyricText()
    {
        AffectsMeasure<LyricText>(
            TextProperty, FontFamilyProperty, FontWeightProperty, FontSizeProperty,
            MaxTextWidthProperty);
        AffectsRender<LyricText>(
            TextProperty, FontFamilyProperty, FontWeightProperty, FontSizeProperty,
            FillProperty, InactiveFillProperty, StrokeBrushProperty, StrokeEnabledProperty,
            StrokeThicknessProperty, InactiveStrokeEnabledProperty, InactiveStrokeBrushProperty,
            TextAlignmentProperty, HighlightLengthProperty, HighlightFractionProperty,
            HighlightClipProperty, MaskCharacterIndexProperty, MaxTextWidthProperty,
            UseGlyphClippingProperty, RenderEffectProperty, RenderOpacityProperty,
            EnterProgressProperty, ShineProgressProperty);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public int HighlightLength
    {
        get => GetValue(HighlightLengthProperty);
        set => SetValue(HighlightLengthProperty, value);
    }

    public double HighlightFraction
    {
        get => GetValue(HighlightFractionProperty);
        set => SetValue(HighlightFractionProperty, value);
    }

    public ClipSideMode HighlightClip
    {
        get => GetValue(HighlightClipProperty);
        set => SetValue(HighlightClipProperty, value);
    }

    public int MaskCharacterIndex
    {
        get => GetValue(MaskCharacterIndexProperty);
        set => SetValue(MaskCharacterIndexProperty, value);
    }

    public bool UseGlyphClipping
    {
        get => GetValue(UseGlyphClippingProperty);
        set => SetValue(UseGlyphClippingProperty, value);
    }

    public IEffect? RenderEffect
    {
        get => GetValue(RenderEffectProperty);
        set => SetValue(RenderEffectProperty, value);
    }

    public double RenderOpacity
    {
        get => GetValue(RenderOpacityProperty);
        set => SetValue(RenderOpacityProperty, value);
    }

    public double EnterProgress
    {
        get => GetValue(EnterProgressProperty);
        set => SetValue(EnterProgressProperty, value);
    }

    public double ShineProgress
    {
        get => GetValue(ShineProgressProperty);
        set => SetValue(ShineProgressProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public IBrush? InactiveFill
    {
        get => GetValue(InactiveFillProperty);
        set => SetValue(InactiveFillProperty, value);
    }

    public IBrush? StrokeBrush
    {
        get => GetValue(StrokeBrushProperty);
        set => SetValue(StrokeBrushProperty, value);
    }

    public bool StrokeEnabled
    {
        get => GetValue(StrokeEnabledProperty);
        set => SetValue(StrokeEnabledProperty, value);
    }

    public double StrokeThickness
    {
        get => GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    public bool InactiveStrokeEnabled
    {
        get => GetValue(InactiveStrokeEnabledProperty);
        set => SetValue(InactiveStrokeEnabledProperty, value);
    }

    public IBrush? InactiveStrokeBrush
    {
        get => GetValue(InactiveStrokeBrushProperty);
        set => SetValue(InactiveStrokeBrushProperty, value);
    }

    public double MaxTextWidth
    {
        get => GetValue(MaxTextWidthProperty);
        set => SetValue(MaxTextWidthProperty, value);
    }

    public TextAlignment TextAlignment
    {
        get => GetValue(TextAlignmentProperty);
        set => SetValue(TextAlignmentProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == TextProperty || p == FontFamilyProperty || p == FontWeightProperty || p == FontSizeProperty
            || p == MaxTextWidthProperty || p == TextAlignmentProperty
            || p == FillProperty || p == InactiveFillProperty
            || p == StrokeBrushProperty || p == InactiveStrokeBrushProperty
            || p == StrokeThicknessProperty || p == StrokeEnabledProperty || p == InactiveStrokeEnabledProperty)
        {
            _layoutDirty = true;
        }
        else
        {
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        RebuildIfDirty();
        var body = _bodyLayout;
        if (body == null)
            return;

        // 全局平移一点，防止描边超出控件边界被裁剪
        using var paddingState = context.PushTransform(Matrix.CreateTranslation(TextPadding, TextPadding));

        using var effectState = RenderEffect != null
            ? (IDisposable)context.PushEffect(RenderEffect, new Rect(0, 0, body.Width, body.Height))
            : null;
        var thickness = StrokeEnabled ? StrokeThickness : 0;

        if (ShineProgress >= 0 && ShineProgress <= 1)
        {
            var band = BuildBand(ShineProgress, body.Width);
            if (band != null)
            {
                using (context.PushOpacityMask(band, new Rect(0, 0, body.Width, body.Height)))
                    body.Draw(context, new Point());
            }
            else
            {
                DrawBody(context, body, thickness);
            }
            return;
        }

        if (HighlightClip == ClipSideMode.Character)
        {
            DrawGlyphRange(context, MaskCharacterIndex, 1, RenderOpacity);
            return;
        }

        if (IsFlatMode || HighlightClip == ClipSideMode.None)
        {
            if (IsFlatMode && HighlightClip == ClipSideMode.Right)
                return;
            if (UseGlyphClipping)
            {
                DrawGlyphRange(context, 0, Text?.Length ?? 0, RenderOpacity);
                return;
            }
            DrawBody(context, body, thickness);
            return;
        }

        if (UseGlyphClipping)
        {
            DrawGlyphProgress(context, HighlightClip);
            return;
        }

        var mask = BuildMask(body, HighlightClip == ClipSideMode.Left);
        if (mask == null)
        {
            DrawBody(context, body, thickness);
            return;
        }

        using (context.PushOpacityMask(mask, new Rect(0, 0, body.Width, body.Height)))
        {
            DrawSharpStroke(context, thickness);
            body.Draw(context, new Point());
        }
    }

    private void DrawBody(DrawingContext context, TextLayout body, double thickness)
    {
        DrawSharpStroke(context, thickness);
        body.Draw(context, new Point());
    }

    /// <summary>
    /// 核心锐利描边法：使用 4 向偏移并强制物理厚度减半。
    /// 这种方法去除了斜角的重叠，使得边缘极为锐利，且在 1.0 的厚度设置下能呈现出 0.5 的细边。
    /// </summary>
    private void DrawSharpStroke(DrawingContext context, double thickness)
    {
        if (thickness <= 0 || _strokeLayout == null)
            return;

        // 强制视觉减半，并最少保留 0.1 的偏移
        var t = Math.Max(0.1, thickness * 0.5);
        
        // 只画上下左右，保证最锐利的边缘
        var sharpOffsets = new (double dx, double dy)[] 
        { 
            (0, -t), (0, t), (-t, 0), (t, 0) 
        };

        foreach (var (dx, dy) in sharpOffsets)
            _strokeLayout.Draw(context, new Point(dx, dy));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        RebuildIfDirty();
        var body = _bodyLayout;
        if (body == null)
            return new Size();
        
        // 加上四周的 Padding，防止描边被裁切
        return new Size(body.Width + TextPadding * 2, body.Height + TextPadding * 2);
    }

    private void RebuildIfDirty()
    {
        if (!_layoutDirty)
            return;
        _layoutDirty = false;
        Rebuild();
    }

    private void Rebuild()
    {
        _glyphLayouts.Clear();
        var text = Text;
        if (string.IsNullOrEmpty(text))
        {
            _bodyLayout = null;
            _strokeLayout = null;
            _charLeft = [];
            return;
        }

        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight, FontStretch.Normal);
        var alignment = TextAlignment;
        var wrapping = TextWrapping.NoWrap;
        var maxWidth = double.PositiveInfinity;
        var maxHeight = double.PositiveInfinity;

        _strokeLayout = StrokeEnabled && StrokeThickness > 0 && StrokeBrush != null
            ? CreateLayout(text, typeface, StrokeBrush, alignment, wrapping, maxWidth, maxHeight)
            : null;

        _bodyLayout = CreateLayout(text, typeface, Fill, alignment, wrapping, maxWidth, maxHeight);
        _charLeft = BuildCharLefts(_bodyLayout, text);
    }

    private bool IsFlatMode
    {
        get
        {
            var text = Text;
            if (string.IsNullOrEmpty(text))
                return true;
            var entering = EnterProgress >= 0 && EnterProgress <= 1;
            return HighlightLength < 0 && !entering;
        }
    }

    private (int Done, double Frac) EffectiveHighlight(int len)
    {
        if (EnterProgress >= 0 && EnterProgress <= 1)
        {
            var p = Math.Clamp(EnterProgress, 0, 1);
            var total = p * len;
            var done = (int)total;
            return (done, total - done);
        }
        return (Math.Clamp(HighlightLength, 0, len), Math.Clamp(HighlightFraction, 0, 1));
    }

    private static IBrush? BuildBand(double progress, double width)
    {
        if (width <= 0)
            return null;
        var pos = Math.Clamp(progress, 0, 1) * width;
        var bandW = Math.Max(24, width * 0.08);
        var stops = new List<GradientStop>(5);
        AddStop(stops, 0, Colors.Transparent);
        AddStop(stops, Math.Max(0, (pos - bandW) / width), Colors.Transparent);
        AddStop(stops, pos / width, Colors.Black);
        AddStop(stops, Math.Min(1, (pos + bandW) / width), Colors.Transparent);
        AddStop(stops, 1, Colors.Transparent);
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(width, 0, RelativeUnit.Absolute),
        };
        brush.GradientStops.AddRange(stops);
        return brush;
    }

    private static double[] BuildCharLefts(TextLayout layout, string text)
    {
        var left = new double[text.Length + 1];
        for (var i = 0; i < text.Length; i++)
            left[i] = layout.HitTestTextPosition(i).X;
        left[text.Length] = layout.Width;
        return left;
    }

    private IBrush? BuildMask(TextLayout layout, bool maskPlayed)
    {
        return maskPlayed
            ? BuildGradient(layout, Colors.Black, Colors.Transparent)
            : BuildGradient(layout, Colors.Transparent, Colors.Black);
    }

    private void DrawGlyphProgress(DrawingContext context, ClipSideMode clip)
    {
        var length = _charLeft.Length - 1;
        if (length <= 0)
            return;
        var (done, fraction) = EffectiveHighlight(length);

        if (clip == ClipSideMode.Left)
        {
            DrawGlyphRange(context, 0, done, RenderOpacity);
            if (done < length)
                DrawGlyphRange(context, done, 1, RenderOpacity * SmoothStep(fraction));
            return;
        }

        if (done < length)
        {
            DrawGlyphRange(context, done, 1, RenderOpacity * SmoothStep(1 - fraction));
            DrawGlyphRange(context, done + 1, length - done - 1, RenderOpacity);
        }
    }

    private void DrawGlyphRange(DrawingContext context, int start, int length, double opacity)
    {
        var text = Text;
        if (string.IsNullOrEmpty(text) || start < 0 || length <= 0 || start + length > text.Length || opacity <= 0)
            return;

        var opacityStep = (byte)Math.Clamp(Math.Round(opacity * 16), 1, 16);
        var key = (start, length, opacityStep);
        if (!_glyphLayouts.TryGetValue(key, out var layout))
        {
            var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight, FontStretch.Normal);
            layout = CreateLayout(text.Substring(start, length), typeface,
                BrushWithOpacity(Fill, opacityStep / 16.0), TextAlignment,
                TextWrapping.NoWrap, double.PositiveInfinity, double.PositiveInfinity);
            _glyphLayouts[key] = layout;
        }

        layout.Draw(context, new Point(_charLeft[start], 0));
    }

    private static IBrush? BrushWithOpacity(IBrush? brush, double opacity)
    {
        if (brush is not ISolidColorBrush solid)
            return brush;
        var color = solid.Color;
        return new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B));
    }

    private static double SmoothStep(double value)
    {
        var x = Math.Clamp(value, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private LinearGradientBrush? BuildGradient(TextLayout layout, Color played, Color unplayed)
    {
        var len = _charLeft.Length - 1;
        if (len <= 0)
            return null;

        var (done, frac) = EffectiveHighlight(len);
        var width = layout.Width;
        if (width <= 0)
            return null;

        var stops = new List<GradientStop>(len * 2 + 4);
        AddStop(stops, 0, played);

        for (var i = 0; i < len; i++)
        {
            var x0 = _charLeft[i];
            var x1 = _charLeft[i + 1];
            if (i < done)
            {
                AddStop(stops, x0 / width, played);
            }
            else if (i > done)
            {
                AddStop(stops, x0 / width, unplayed);
            }
            else
            {
                var charW = Math.Max(x1 - x0, 0.001);
                var boundary = x0 + charW * frac;
                var fadeW = Math.Min(charW * 0.35, 8);
                var fadeEnd = Math.Min(x1, boundary + fadeW);
                AddStop(stops, x0 / width, played);
                AddStop(stops, boundary / width, played);
                AddStop(stops, fadeEnd / width, unplayed);
            }
        }
        AddStop(stops, 1, done >= len ? played : unplayed);

        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(width, 0, RelativeUnit.Absolute),
        };
        brush.GradientStops.AddRange(stops);
        return brush;
    }

    private static void AddStop(List<GradientStop> stops, double offset, Color color)
    {
        if (stops.Count > 0)
        {
            var last = stops[^1];
            if (Math.Abs(last.Offset - offset) < 0.0005)
            {
                if (last.Color != color)
                    stops[^1] = new GradientStop(color, offset);
                return;
            }
        }
        stops.Add(new GradientStop(color, offset));
    }

    private TextLayout CreateLayout(
        string text, Typeface typeface, IBrush? foreground, TextAlignment alignment,
        TextWrapping wrapping, double maxWidth, double maxHeight)
    {
        var lineHeight = Math.Ceiling(FontSize * 1.4 * 2) / 2;
        return new TextLayout(text, typeface, FontSize, foreground,
            textAlignment: alignment,
            textWrapping: wrapping,
            maxWidth: maxWidth,
            maxHeight: maxHeight,
            lineHeight: lineHeight);
    }
}

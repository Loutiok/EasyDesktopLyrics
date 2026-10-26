using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace EasyDesktopLyrics.Views;

/// <summary>
/// 带描边的文本展示控件。经过像素级修正，完全对齐主歌词 LyricText 渲染逻辑。
/// </summary>
public sealed class StrokedTextPresenter : Control
{
    public StrokedTextPresenter()
    {
        // 强制禁用 ClearType 亚像素渲染，使用灰度抗锯齿。
        // 彻底解决透明窗口下文字边缘出现红、蓝、黄彩色毛边以及亚像素居中对齐发糊的问题。
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
    }

    // 为偏移描边预留完全相同的空间，防止文字居中对齐时因亚像素位移与原文错位
    private const double TextPadding = 2.0;

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, IBrush?>(nameof(Fill), Brushes.White);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, IBrush?>(nameof(Stroke), Brushes.Black);

    public static readonly StyledProperty<bool> StrokeEnabledProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, bool>(nameof(StrokeEnabled));

    public static readonly StyledProperty<double> StrokeThicknessProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, double>(nameof(StrokeThickness), 2.0);

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextBlock.FontFamilyProperty.AddOwner<StrokedTextPresenter>();

    public static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<StrokedTextPresenter>();

    public static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextBlock.FontWeightProperty.AddOwner<StrokedTextPresenter>();

    public static readonly new StyledProperty<IEffect?> EffectProperty =
        Visual.EffectProperty.AddOwner<StrokedTextPresenter>();

    public static readonly new StyledProperty<double> OpacityProperty =
        Visual.OpacityProperty.AddOwner<StrokedTextPresenter>();

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public bool StrokeEnabled { get => GetValue(StrokeEnabledProperty); set => SetValue(StrokeEnabledProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public new IEffect? Effect { get => GetValue(EffectProperty); set => SetValue(EffectProperty, value); }
    public new double Opacity { get => GetValue(OpacityProperty); set => SetValue(OpacityProperty, value); }

    private TextLayout? _bodyLayout;
    private TextLayout? _strokeLayout;
    private bool _layoutDirty = true;

    static StrokedTextPresenter()
    {
        AffectsMeasure<StrokedTextPresenter>(TextProperty, FontFamilyProperty, FontSizeProperty, FontWeightProperty);
        AffectsRender<StrokedTextPresenter>(
            TextProperty, FillProperty, StrokeProperty, StrokeEnabledProperty, StrokeThicknessProperty,
            FontFamilyProperty, FontSizeProperty, FontWeightProperty, OpacityProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == TextProperty || p == FontFamilyProperty || p == FontWeightProperty || p == FontSizeProperty
            || p == FillProperty || p == StrokeProperty || p == StrokeThicknessProperty || p == StrokeEnabledProperty)
        {
            _layoutDirty = true;
            // 必须调用此方法，否则属性更改（如切歌、换字体）时，界面不会立刻刷新
            InvalidateVisual(); 
        }
    }

    private void RebuildIfDirty()
    {
        if (!_layoutDirty && _bodyLayout != null) return;
        _layoutDirty = false;
        
        if (string.IsNullOrEmpty(Text))
        {
            _bodyLayout = null;
            _strokeLayout = null;
            return;
        }

        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight, FontStretch.Normal);
        // 强制使用和主歌词一致的固定行高逻辑，防止上下抖动
        var lineHeight = Math.Ceiling(FontSize * 1.4 * 2) / 2; 
        
        _strokeLayout = StrokeEnabled && StrokeThickness > 0 && Stroke != null
            ? new TextLayout(Text, typeface, FontSize, Stroke,
                textAlignment: TextAlignment.Center,
                textWrapping: TextWrapping.NoWrap,
                lineHeight: lineHeight)
            : null;

        _bodyLayout = new TextLayout(Text, typeface, FontSize, Fill,
            textAlignment: TextAlignment.Center,
            textWrapping: TextWrapping.NoWrap,
            lineHeight: lineHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        RebuildIfDirty();
        // 加上四周的 Padding，让翻译行的测量尺寸物理边界和原文保持绝对一致
        return _bodyLayout != null 
            ? new Size(_bodyLayout.Width + TextPadding * 2, _bodyLayout.Height + TextPadding * 2) 
            : new Size();
    }

    public override void Render(DrawingContext context)
    {
        RebuildIfDirty();
        if (_bodyLayout == null) return;

        // 全局平移物理像素，防止描边边缘超出控件边界被裁剪，且完美对齐主歌词图层
        using var paddingState = context.PushTransform(Matrix.CreateTranslation(TextPadding, TextPadding));

        using (context.PushOpacity(Opacity))
        {
            if (StrokeEnabled && StrokeThickness > 0 && _strokeLayout != null)
            {
                // 采用和 LyricText 完全一致的 4 向锐利偏移法（物理厚度减半）
                // 这种做法去除了对角线的重叠，边缘最锐利，且绝对不会“向内吃字”
                var t = Math.Max(0.1, StrokeThickness * 0.5);
                
                var sharpOffsets = new (double dx, double dy)[] 
                { 
                    (0, -t), (0, t), (-t, 0), (t, 0) 
                };

                foreach (var (dx, dy) in sharpOffsets)
                {
                    _strokeLayout.Draw(context, new Point(dx, dy));
                }
            }

            // 正文平涂覆盖在最上方
            _bodyLayout.Draw(context, new Point(0, 0));
        }
    }
}

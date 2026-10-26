using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using System.Globalization;

namespace EasyDesktopLyrics.Views;

/// <summary>
/// 带描边的文本展示控件。彻底抛弃多 TextBlock 叠加，采用 TextLayout 精准提取几何轮廓绘制极细描边。
/// </summary>
public sealed class StrokedTextPresenter : Control
{
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

    private TextLayout? _layout;
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
        if (p == TextProperty || p == FontFamilyProperty || p == FontWeightProperty || p == FontSizeProperty)
        {
            _layoutDirty = true;
        }
    }

    private void RebuildIfDirty()
    {
        if (!_layoutDirty && _layout != null) return;
        _layoutDirty = false;
        
        if (string.IsNullOrEmpty(Text))
        {
            _layout = null;
            return;
        }

        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight, FontStretch.Normal);
        // 强制使用和主歌词一致的固定行高逻辑，防止上下抖动
        var lineHeight = Math.Ceiling(FontSize * 1.4 * 2) / 2; 
        
        _layout = new TextLayout(Text, typeface, FontSize, Fill,
            textAlignment: TextAlignment.Center,
            textWrapping: TextWrapping.NoWrap,
            lineHeight: lineHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        RebuildIfDirty();
        return _layout != null ? new Size(_layout.Width, _layout.Height) : new Size();
    }

    public override void Render(DrawingContext context)
    {
        RebuildIfDirty();
        if (_layout == null) return;

        using (context.PushOpacity(Opacity))
        {
            if (StrokeEnabled && StrokeThickness > 0 && Stroke != null)
            {
                // 利用 FormattedText 提取完美的几何轮廓
                var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight, FontStretch.Normal);
                var formattedText = new FormattedText(Text ?? "", CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, FontSize, Stroke);
                
                var geometry = formattedText.BuildGeometry(new Point(0, 0));
                if (geometry != null)
                {
                    // 对齐处理：由于 TextLayout 默认有 1.4 的行高拉伸，这里需要精准找到正文的基线并居中
                    double offsetX = (_layout.Width - formattedText.Width) / 2;
                    // 垂直方向使用固定行高居中修正
                    double offsetY = (_layout.Height - formattedText.Height) / 2;
                    
                    using (context.PushTransform(Matrix.CreateTranslation(offsetX, offsetY)))
                    {
                        var pen = new Pen(Stroke, StrokeThickness, lineJoin: PenLineJoin.Round, lineCap: PenLineCap.Round);
                        context.DrawGeometry(null, pen, geometry);
                    }
                }
            }

            // 直接在正中间绘制正文（自带完美的渐变和抗锯齿）
            _layout.Draw(context, new Point(0, 0));
        }
    }
}

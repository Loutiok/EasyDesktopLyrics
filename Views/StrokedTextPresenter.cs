using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;

namespace EasyDesktopLyrics.Views;

/// <summary>
/// 带描边的文本展示控件。彻底抛弃多 TextBlock，采用 TextLayout 的 8 向锐利偏移法（厚度减半）。
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
        if (p == TextProperty || p == FontFamilyProperty || p == FontWeightProperty || p == FontSizeProperty)
        {
            _layoutDirty = true;
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
        
        _strokeLayout = new TextLayout(Text, typeface, FontSize, Stroke,
            textAlignment: TextAlignment.Center,
            textWrapping: TextWrapping.NoWrap,
            lineHeight: lineHeight);

        _bodyLayout = new TextLayout(Text, typeface, FontSize, Fill,
            textAlignment: TextAlignment.Center,
            textWrapping: TextWrapping.NoWrap,
            lineHeight: lineHeight);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        RebuildIfDirty();
        return _bodyLayout != null ? new Size(_bodyLayout.Width, _bodyLayout.Height) : new Size();
    }

    public override void Render(DrawingContext context)
    {
        RebuildIfDirty();
        if (_bodyLayout == null) return;

        using (context.PushOpacity(Opacity))
        {
            if (StrokeEnabled && StrokeThickness > 0 && _strokeLayout != null && Stroke != null)
            {
                // 8 向偏移法：厚度减半，边缘锐利
                var t = Math.Max(0.1, StrokeThickness * 0.5);
                var d = t * 0.707; // 对角线偏移

                var offsets = new (double dx, double dy)[] 
                { 
                    (0, -t), (0, t), (-t, 0), (t, 0),
                    (-d, -d), (d, d), (-d, d), (d, -d)
                };

                foreach (var (dx, dy) in offsets)
                {
                    _strokeLayout.Draw(context, new Point(dx, dy));
                }
            }

            // 正文覆盖在最上方
            _bodyLayout.Draw(context, new Point(0, 0));
        }
    }
}

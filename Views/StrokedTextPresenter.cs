using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace EasyDesktopLyrics.Views;

/// <summary>
/// 带描边的文本展示控件。采用单 TextBlock 渲染正文，底层通过矢量 Geometry 和 Pen 绘制极细描边。
/// </summary>
public sealed class StrokedTextPresenter : Decorator
{
    private readonly TextBlock _main = new();

    public StrokedTextPresenter()
    {
        Child = _main;
    }

    static StrokedTextPresenter()
    {
        AffectsRender<StrokedTextPresenter>(
            TextProperty, FillProperty, StrokeProperty, SEnabledProperty, SThicknessProperty,
            FontFamilyProperty, FontSizeProperty, FontWeightProperty, OpacityProperty);

        TextProperty.Changed.AddClassHandler<StrokedTextPresenter>((x, _) => x._main.Text = x.Text);
        FillProperty.Changed.AddClassHandler<StrokedTextPresenter>((x, _) => x._main.Foreground = x.Fill);
        FontFamilyProperty.Changed.AddClassHandler<StrokedTextPresenter>((x, _) => x._main.FontFamily = x.FontFamily);
        FontSizeProperty.Changed.AddClassHandler<StrokedTextPresenter>((x, _) => x._main.FontSize = x.FontSize);
        FontWeightProperty.Changed.AddClassHandler<StrokedTextPresenter>((x, _) => x._main.FontWeight = x.FontWeight);
        EffectProperty.Changed.AddClassHandler<StrokedTextPresenter>((x, _) => x._main.Effect = x.Effect);
        OpacityProperty.Changed.AddClassHandler<StrokedTextPresenter>((x, _) => x._main.Opacity = x.Opacity);
    }

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, string?>(nameof(Text));

    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, IBrush?>(nameof(Fill), Brushes.White);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, IBrush?>(nameof(Stroke), Brushes.Black);

    public static readonly StyledProperty<bool> SEnabledProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, bool>("StrokeEnabled");

    public static readonly StyledProperty<double> SThicknessProperty =
        AvaloniaProperty.Register<StrokedTextPresenter, double>("StrokeThickness", 2.0);

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
    public bool StrokeEnabled { get => GetValue(SEnabledProperty); set => SetValue(SEnabledProperty, value); }
    public double StrokeThickness { get => GetValue(SThicknessProperty); set => SetValue(SThicknessProperty, value); }
    public FontFamily FontFamily { get => GetValue(FontFamilyProperty); set => SetValue(FontFamilyProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }
    public FontWeight FontWeight { get => GetValue(FontWeightProperty); set => SetValue(FontWeightProperty, value); }
    public new IEffect? Effect { get => GetValue(EffectProperty); set => SetValue(EffectProperty, value); }
    public new double Opacity { get => GetValue(OpacityProperty); set => SetValue(OpacityProperty, value); }

    public override void Render(DrawingContext context)
    {
        // 底层先绘制矢量描边
        if (StrokeEnabled && StrokeThickness > 0 && Stroke != null && !string.IsNullOrEmpty(Text))
        {
            var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight, FontStretch.Normal);
            
            var formattedText = new FormattedText(
                Text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize,
                Stroke);

            var textGeometry = formattedText.BuildGeometry(new Point(0, 0));
            if (textGeometry != null)
            {
                // 计算对齐偏移（通常 TextBlock 在 Decorator 中居中或左上对齐，这里默认获取其实际 Bounds）
                double offsetX = (Bounds.Width - formattedText.Width) / 2;
                double offsetY = (Bounds.Height - formattedText.Height) / 2;

                using (context.PushTransform(Matrix.CreateTranslation(offsetX, offsetY)))
                using (context.PushOpacity(Opacity)) // 应用透明度，防止描边过亮
                {
                    // 配置画笔
                    var strokePen = new Pen(
                        Stroke,
                        StrokeThickness,
                        lineJoin: PenLineJoin.Round,
                        lineCap: PenLineCap.Round
                    );

                    context.DrawGeometry(null, strokePen, textGeometry);
                }
            }
        }

        // 让子 TextBlock 正常绘制正文覆盖在上面
        base.Render(context);
    }
}

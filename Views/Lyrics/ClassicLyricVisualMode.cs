using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using EasyDesktopLyrics.ViewModels;

namespace EasyDesktopLyrics.Views.Lyrics;

/// <summary>
/// 当前版本的经典歌词视觉：未唱层、已唱层、翻译描边以及可选辉光层。
/// 这里只组合渲染层，不管理换行时机、窗口位置或封面状态。
/// </summary>
internal sealed class ClassicLyricVisualMode : ILyricVisualMode
{
    private static readonly (double X, double Y)[] StrokeOffsets =
    {
        (0, -1), (0, 1), (-1, 0), (1, 0),
        (-0.7, -0.7), (0.7, 0.7), (-0.7, 0.7), (0.7, -0.7),
    };

    private readonly OverlayViewModel _viewModel;
    private readonly List<TextBlock> _strokeLayers = [];
    private readonly List<Control> _glowLayers = [];
    private TextAlignment _textAlignment;
    private HorizontalAlignment _horizontalAlignment;

    public ClassicLyricVisualMode(OverlayViewModel viewModel, Panel host)
    {
        _viewModel = viewModel;

        MainHost = new Grid();
        InactiveLyric = new LyricText();
        BindInactiveLyric(InactiveLyric);
        MainHost.Children.Add(InactiveLyric);

        ActiveLyric = new LyricText();
        BindActiveLyric(ActiveLyric);
        MainHost.Children.Add(ActiveLyric);

        TranslationHost = new Grid();
        TranslationText = CreateTranslationText();
        TranslationHost.Children.Add(TranslationText);

        host.Children.Clear();
        host.Children.Add(MainHost);
        host.Children.Add(TranslationHost);

        _textAlignment = viewModel.TextAlignment;
        _horizontalAlignment = HorizontalAlignment.Center;
        RefreshStroke();
        RefreshGlow();
    }

    public string Id => LyricVisualModeIds.Classic;

    public Grid MainHost { get; }
    public Grid TranslationHost { get; }
    public LyricText ActiveLyric { get; }
    public LyricText InactiveLyric { get; }
    public TextBlock TranslationText { get; }
    public LyricText? ActiveGlowLyric { get; private set; }
    public LyricText? InactiveGlowLyric { get; private set; }

    public void RefreshStroke()
    {
        foreach (var layer in _strokeLayers)
            TranslationHost.Children.Remove(layer);
        _strokeLayers.Clear();

        if (_viewModel.StrokeEnabled && _viewModel.StrokeThickness > 0)
        {
            var thickness = _viewModel.StrokeThickness;
            foreach (var (x, y) in StrokeOffsets)
            {
                var layer = CreateTranslationBase();
                layer.RenderTransform = new TranslateTransform(x * thickness, y * thickness);
                layer.Bind(TextBlock.ForegroundProperty, new Binding("StrokeBrush"));
                layer.Bind(TextBlock.EffectProperty, new Binding("TextEffect"));
                TranslationHost.Children.Insert(0, layer);
                _strokeLayers.Add(layer);
            }
        }

        ApplyAlignment(_textAlignment, _horizontalAlignment);
    }

    public void RefreshGlow()
    {
        ActiveGlowLyric = null;
        InactiveGlowLyric = null;
        foreach (var layer in _glowLayers)
        {
            MainHost.Children.Remove(layer);
            TranslationHost.Children.Remove(layer);
        }
        _glowLayers.Clear();

        if (_viewModel.InactiveGlowEnabled)
        {
            var glow = CreateMainGlowLyric();
            glow.Bind(LyricText.FillProperty, new Binding("InactiveFill"));
            glow.Bind(Visual.EffectProperty, new Binding("InactiveGlowEffect"));
            glow.HighlightClip = LyricText.ClipSideMode.Right;
            MainHost.Children.Insert(0, glow);
            _glowLayers.Add(glow);
            InactiveGlowLyric = glow;
        }

        if (_viewModel.GlowEnabled)
        {
            var glow = CreateMainGlowLyric();
            glow.Bind(LyricText.FillProperty, new Binding("Fill"));
            glow.Bind(Visual.EffectProperty, new Binding("GlowEffect"));
            glow.HighlightClip = LyricText.ClipSideMode.Left;
            MainHost.Children.Insert(0, glow);
            _glowLayers.Add(glow);
            ActiveGlowLyric = glow;

            var translationGlow = CreateTranslationBase();
            translationGlow.Bind(TextBlock.ForegroundProperty, new Binding("Fill"));
            translationGlow.Bind(TextBlock.EffectProperty, new Binding("GlowEffect"));
            TranslationHost.Children.Insert(0, translationGlow);
            _glowLayers.Add(translationGlow);
        }

        ApplyAlignment(_textAlignment, _horizontalAlignment);
    }

    public void ApplyAlignment(TextAlignment textAlignment, HorizontalAlignment horizontalAlignment)
    {
        _textAlignment = textAlignment;
        _horizontalAlignment = horizontalAlignment;

        ActiveLyric.TextAlignment = textAlignment;
        ActiveLyric.HorizontalAlignment = horizontalAlignment;
        InactiveLyric.TextAlignment = textAlignment;
        InactiveLyric.HorizontalAlignment = horizontalAlignment;
        TranslationText.TextAlignment = textAlignment;

        foreach (var layer in _strokeLayers)
            layer.TextAlignment = textAlignment;
        foreach (var layer in _glowLayers)
        {
            if (layer is TextBlock text)
                text.TextAlignment = textAlignment;
            else if (layer is LyricText lyric)
            {
                lyric.TextAlignment = textAlignment;
                lyric.HorizontalAlignment = horizontalAlignment;
            }
        }
    }

    private static void BindLyricBase(LyricText lyric)
    {
        lyric.Bind(LyricText.TextProperty, new Binding("MainText"));
        lyric.Bind(LyricText.FontFamilyProperty, new Binding("FontFamilyValue"));
        lyric.Bind(LyricText.FontWeightProperty, new Binding("WeightValue"));
        lyric.Bind(LyricText.FontSizeProperty, new Binding("MainFontSize"));
        lyric.Bind(LyricText.TextAlignmentProperty, new Binding("TextAlignment"));
        lyric.Bind(LyricText.OpacityProperty, new Binding("TextOpacity"));
        lyric.Bind(LyricText.HighlightFractionProperty, new Binding("WordHighlightFraction"));
    }

    private static void BindActiveLyric(LyricText lyric)
    {
        BindLyricBase(lyric);
        lyric.Bind(LyricText.HighlightLengthProperty, new Binding("WordHighlightLength"));
        lyric.Bind(LyricText.FillProperty, new Binding("Fill"));
        lyric.Bind(LyricText.StrokeEnabledProperty, new Binding("StrokeEnabled"));
        lyric.Bind(LyricText.StrokeBrushProperty, new Binding("StrokeBrush"));
        lyric.Bind(LyricText.StrokeThicknessProperty, new Binding("StrokeThickness"));
        lyric.Bind(Visual.EffectProperty, new Binding("TextEffect"));
        lyric.HighlightClip = LyricText.ClipSideMode.Left;
    }

    private static void BindInactiveLyric(LyricText lyric)
    {
        BindLyricBase(lyric);
        lyric.Bind(LyricText.HighlightLengthProperty, new Binding("WordHighlightLength"));
        lyric.Bind(LyricText.FillProperty, new Binding("InactiveFill"));
        lyric.Bind(LyricText.StrokeEnabledProperty, new Binding("InactiveStrokeEnabled"));
        lyric.Bind(LyricText.StrokeBrushProperty, new Binding("InactiveStrokeBrush"));
        lyric.Bind(LyricText.StrokeThicknessProperty, new Binding("InactiveStrokeThickness"));
        lyric.Bind(Visual.EffectProperty, new Binding("TextEffect"));
        lyric.HighlightClip = LyricText.ClipSideMode.Right;
    }

    private static LyricText CreateMainGlowLyric()
    {
        var lyric = new LyricText();
        BindLyricBase(lyric);
        lyric.Bind(LyricText.HighlightLengthProperty, new Binding("WordHighlightLength"));
        return lyric;
    }

    private static TextBlock CreateTranslationText()
    {
        var text = CreateTranslationBase();
        text.Bind(TextBlock.ForegroundProperty, new Binding("Fill"));
        text.Bind(TextBlock.EffectProperty, new Binding("TextEffect"));
        return text;
    }

    private static TextBlock CreateTranslationBase()
    {
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, new Binding("TransText"));
        text.Bind(TextBlock.FontFamilyProperty, new Binding("FontFamilyValue"));
        text.Bind(TextBlock.FontWeightProperty, new Binding("WeightValue"));
        text.Bind(TextBlock.FontSizeProperty, new Binding("EffectiveTransFontSize"));
        text.Bind(TextBlock.OpacityProperty, new Binding("TextOpacity"));
        text.Bind(TextBlock.IsVisibleProperty, new Binding("ShowTransLine"));
        return text;
    }
}

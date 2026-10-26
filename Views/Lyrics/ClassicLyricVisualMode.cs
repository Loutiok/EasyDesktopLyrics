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
    private readonly OverlayViewModel _viewModel;
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
    public LyricText TranslationText { get; }
    public LyricText? ActiveGlowLyric { get; private set; }
    public LyricText? InactiveGlowLyric { get; private set; }
    public IReadOnlyList<LyricText> ActiveGlowLyrics => _activeGlowLyrics;
    public IReadOnlyList<LyricText> InactiveGlowLyrics => _inactiveGlowLyrics;

    private readonly List<LyricText> _activeGlowLyrics = [];
    private readonly List<LyricText> _inactiveGlowLyrics = [];

    public void RefreshStroke()
    {
        // 翻译行现在使用 LyricText，描边由控件内部直接绘制，无需额外叠加 TextBlock。
        ApplyAlignment(_textAlignment, _horizontalAlignment);
    }

    public void RefreshGlow()
    {
        ActiveGlowLyric = null;
        InactiveGlowLyric = null;
        _activeGlowLyrics.Clear();
        _inactiveGlowLyrics.Clear();
        foreach (var layer in _glowLayers)
        {
            MainHost.Children.Remove(layer);
            TranslationHost.Children.Remove(layer);
        }
        _glowLayers.Clear();

        if (_viewModel.InactiveGlowEnabled)
        {
            var outer = CreateMainGlowLyric("InactiveFill", "InactiveGlowOuterEffect", LyricText.ClipSideMode.Right);
            var core = CreateMainGlowLyric("InactiveFill", "InactiveGlowEffect", LyricText.ClipSideMode.Right);
            AddMainGlow(outer);
            AddMainGlow(core);
            _inactiveGlowLyrics.Add(outer);
            _inactiveGlowLyrics.Add(core);
            InactiveGlowLyric = core;
        }

        if (_viewModel.GlowEnabled)
        {
            var outer = CreateMainGlowLyric("Fill", "GlowOuterEffect", LyricText.ClipSideMode.Left);
            var core = CreateMainGlowLyric("Fill", "GlowEffect", LyricText.ClipSideMode.Left);
            AddMainGlow(outer);
            AddMainGlow(core);
            _activeGlowLyrics.Add(outer);
            _activeGlowLyrics.Add(core);
            ActiveGlowLyric = core;

            var translationGlow = CreateTranslationGlow();
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
        TranslationText.HorizontalAlignment = horizontalAlignment;

        foreach (var layer in _glowLayers)
        {
            if (layer is LyricText lyric)
            {
                lyric.TextAlignment = textAlignment;
                lyric.HorizontalAlignment = horizontalAlignment;
            }
        }
    }

    private static void BindLyricBase(LyricText lyric, bool useRenderOpacity = false)
    {
        lyric.Bind(LyricText.TextProperty, new Binding("MainText"));
        lyric.Bind(LyricText.FontFamilyProperty, new Binding("FontFamilyValue"));
        lyric.Bind(LyricText.FontWeightProperty, new Binding("WeightValue"));
        lyric.Bind(LyricText.FontSizeProperty, new Binding("MainFontSize"));
        lyric.Bind(LyricText.TextAlignmentProperty, new Binding("TextAlignment"));
        if (useRenderOpacity)
            lyric.Bind(LyricText.RenderOpacityProperty, new Binding("TextOpacity"));
        else
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

    private static LyricText CreateMainGlowLyric(
        string fillProperty,
        string effectProperty,
        LyricText.ClipSideMode clip)
    {
        var lyric = new LyricText
        {
            HighlightClip = clip,
            UseGlyphClipping = true,
            IsHitTestVisible = false,
        };
        BindLyricBase(lyric, useRenderOpacity: true);
        lyric.Bind(LyricText.HighlightLengthProperty, new Binding("WordHighlightLength"));
        lyric.Bind(LyricText.FillProperty, new Binding(fillProperty));
        lyric.Bind(LyricText.RenderEffectProperty, new Binding(effectProperty));
        return lyric;
    }

    private void AddMainGlow(LyricText glow)
    {
        MainHost.Children.Insert(0, glow);
        _glowLayers.Add(glow);
    }

    private static LyricText CreateTranslationText()
    {
        var text = new LyricText
        {
            HighlightLength = -1,
            HighlightClip = LyricText.ClipSideMode.None
        };
        text.Bind(LyricText.TextProperty, new Binding("TransText"));
        text.Bind(LyricText.FontFamilyProperty, new Binding("FontFamilyValue"));
        text.Bind(LyricText.FontWeightProperty, new Binding("WeightValue"));
        text.Bind(LyricText.FontSizeProperty, new Binding("EffectiveTransFontSize"));
        text.Bind(LyricText.OpacityProperty, new Binding("TextOpacity"));
        text.Bind(LyricText.IsVisibleProperty, new Binding("ShowTransLine"));
        text.Bind(LyricText.FillProperty, new Binding("Fill"));
        text.Bind(LyricText.StrokeEnabledProperty, new Binding("StrokeEnabled"));
        text.Bind(LyricText.StrokeBrushProperty, new Binding("StrokeBrush"));
        text.Bind(LyricText.StrokeThicknessProperty, new Binding("StrokeThickness"));
        text.Bind(Visual.EffectProperty, new Binding("TextEffect"));
        return text;
    }

    private static LyricText CreateTranslationGlow()
    {
        var text = new LyricText
        {
            HighlightLength = -1,
            HighlightClip = LyricText.ClipSideMode.None,
            IsHitTestVisible = false,
        };
        text.Bind(LyricText.TextProperty, new Binding("TransText"));
        text.Bind(LyricText.FontFamilyProperty, new Binding("FontFamilyValue"));
        text.Bind(LyricText.FontWeightProperty, new Binding("WeightValue"));
        text.Bind(LyricText.FontSizeProperty, new Binding("EffectiveTransFontSize"));
        text.Bind(LyricText.OpacityProperty, new Binding("TextOpacity"));
        text.Bind(LyricText.IsVisibleProperty, new Binding("ShowTransLine"));
        text.Bind(LyricText.FillProperty, new Binding("Fill"));
        text.Bind(Visual.EffectProperty, new Binding("GlowEffect"));
        return text;
    }
}

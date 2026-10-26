using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using EasyDesktopLyrics.ViewModels;

namespace EasyDesktopLyrics.Views.Lyrics;

/// <summary>
/// 流光歌词模式：复用经典模式保证主体可读性，在文字下方叠加当前音节双层辉光与上一音节释放余辉。
/// </summary>
internal sealed class LuminousLyricVisualMode : ILyricVisualMode
{
    private readonly ClassicLyricVisualMode _classic;
    private readonly LyricText _afterglow;
    private readonly LyricText _activeOuterGlow;
    private readonly LyricText _activeCoreGlow;

    public LuminousLyricVisualMode(OverlayViewModel viewModel, Panel host)
    {
        _classic = new ClassicLyricVisualMode(viewModel, host);

        _afterglow = CreateCharacterGlow(
            "ReleasedCharacterIndex", "LuminousAfterglowOpacity", "LuminousOuterGlowEffect");
        _activeOuterGlow = CreateCharacterGlow(
            "ActiveCharacterIndex", "LuminousOuterGlowOpacity", "LuminousOuterGlowEffect");
        _activeCoreGlow = CreateCharacterGlow(
            "ActiveCharacterIndex", "LuminousCoreGlowOpacity", "LuminousCoreGlowEffect");

        // 全部位于经典文字主体和常规辉光之下，不改变排版测量或文字清晰度。
        MainHost.Children.Insert(0, _afterglow);
        MainHost.Children.Insert(0, _activeOuterGlow);
        MainHost.Children.Insert(0, _activeCoreGlow);
        ApplyAlignment(viewModel.TextAlignment, HorizontalAlignment.Center);
    }

    public string Id => LyricVisualModeIds.Luminous;

    public Grid MainHost => _classic.MainHost;
    public Grid TranslationHost => _classic.TranslationHost;
    public LyricText ActiveLyric => _classic.ActiveLyric;
    public LyricText InactiveLyric => _classic.InactiveLyric;
    public LyricText TranslationText => _classic.TranslationText;
    public LyricText? ActiveGlowLyric => _classic.ActiveGlowLyric;
    public LyricText? InactiveGlowLyric => _classic.InactiveGlowLyric;
    public IReadOnlyList<LyricText> ActiveGlowLyrics => _classic.ActiveGlowLyrics;
    public IReadOnlyList<LyricText> InactiveGlowLyrics => _classic.InactiveGlowLyrics;

    public void RefreshStroke() => _classic.RefreshStroke();

    public void RefreshGlow() => _classic.RefreshGlow();

    public void ApplyAlignment(TextAlignment textAlignment, HorizontalAlignment horizontalAlignment)
    {
        _classic.ApplyAlignment(textAlignment, horizontalAlignment);
        foreach (var glow in new[] { _afterglow, _activeOuterGlow, _activeCoreGlow })
        {
            glow.TextAlignment = textAlignment;
            glow.HorizontalAlignment = horizontalAlignment;
        }
    }

    private static LyricText CreateCharacterGlow(
        string characterIndexProperty, string opacityProperty, string effectProperty)
    {
        var glow = new LyricText
        {
            HighlightClip = LyricText.ClipSideMode.Character,
            UseGlyphClipping = true,
            IsHitTestVisible = false,
        };
        glow.Bind(LyricText.TextProperty, new Binding("MainText"));
        glow.Bind(LyricText.FontFamilyProperty, new Binding("FontFamilyValue"));
        glow.Bind(LyricText.FontWeightProperty, new Binding("WeightValue"));
        glow.Bind(LyricText.FontSizeProperty, new Binding("MainFontSize"));
        glow.Bind(LyricText.TextAlignmentProperty, new Binding("TextAlignment"));
        glow.Bind(LyricText.FillProperty, new Binding("LuminousGlowBrush"));
        glow.Bind(LyricText.MaskCharacterIndexProperty, new Binding(characterIndexProperty));
        glow.Bind(LyricText.RenderOpacityProperty, new Binding(opacityProperty));
        glow.Bind(LyricText.RenderEffectProperty, new Binding(effectProperty));
        return glow;
    }
}

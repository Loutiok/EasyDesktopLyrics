using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EasyDesktopLyrics.Views.Lyrics;

/// <summary>
/// 歌词视觉模式的稳定边界。窗口只负责生命周期与布局，具体文字层、描边和辉光组合由模式实现。
/// </summary>
internal interface ILyricVisualMode
{
    string Id { get; }

    Grid MainHost { get; }
    Grid TranslationHost { get; }
    LyricText ActiveLyric { get; }
    LyricText InactiveLyric { get; }
    LyricText TranslationText { get; }
    LyricText? ActiveGlowLyric { get; }
    LyricText? InactiveGlowLyric { get; }
    IReadOnlyList<LyricText> ActiveGlowLyrics { get; }
    IReadOnlyList<LyricText> InactiveGlowLyrics { get; }

    void RefreshStroke();
    void RefreshGlow();
    void ApplyAlignment(TextAlignment textAlignment, HorizontalAlignment horizontalAlignment);
}

namespace EasyDesktopLyrics.Models;

/// <summary>
/// 某一播放时刻的歌词时间轴快照。既保留传统逐字高亮进度，也提供当前音节与最近释放音节供视觉模式使用。
/// </summary>
public readonly record struct LyricTimingFrame(
    int CompletedCharacters,
    double HighlightFraction,
    int ActiveCharacterIndex,
    long ActiveCharacterDurationMs,
    int ReleasedCharacterIndex,
    long ReleasedCharacterDurationMs,
    long ReleasedElapsedMs)
{
    public static LyricTimingFrame None => new(-1, 0, -1, 0, -1, 0, 0);

    public bool IsVisuallyEquivalentTo(LyricTimingFrame other) =>
        CompletedCharacters == other.CompletedCharacters
        && Math.Abs(HighlightFraction - other.HighlightFraction) < 0.0005
        && ActiveCharacterIndex == other.ActiveCharacterIndex
        && ActiveCharacterDurationMs == other.ActiveCharacterDurationMs
        && ReleasedCharacterIndex == other.ReleasedCharacterIndex
        && ReleasedCharacterDurationMs == other.ReleasedCharacterDurationMs
        && ReleasedElapsedMs == other.ReleasedElapsedMs;
}

using EasyDesktopLyrics.Models;

namespace EasyDesktopLyrics.Services;

/// <summary>
/// 将歌词源提供的行/词时间转换成连续的逐字符时间轴。
/// 精确持续时间优先；缺失时依次使用下一词、行末、下一行或固定兜底值。
/// </summary>
internal static class LyricsTimelineBuilder
{
    private const long LastWordFallbackMs = 3000;
    private const long RecentReleaseWindowMs = 600;

    public static LyricDocument AssignCharacterTimings(LyricDocument document)
    {
        var lines = document.Lines.ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Words is not { Count: > 0 } || line.Text.Length == 0)
                continue;

            var nextLineStart = i + 1 < lines.Count ? lines[i + 1].TimeMs : (long?)null;
            var charTimes = BuildCharacterTimings(line, nextLineStart);
            if (charTimes.Count > 0)
                lines[i] = line with { CharTimes = charTimes };
        }

        return new LyricDocument(lines, document.IsInstrumental);
    }

    internal static IReadOnlyList<LyricCharTime> BuildCharacterTimings(LyricLine line, long? nextLineStart)
    {
        if (line.Words is not { Count: > 0 } words || line.Text.Length == 0)
            return [];

        var lineEnd = ResolveLineEnd(line, nextLineStart);
        var times = new List<LyricCharTime>(line.Text.Length);
        var offset = 0;

        for (var wordIndex = 0; wordIndex < words.Count; wordIndex++)
        {
            var word = words[wordIndex];
            var length = Math.Min(word.Text.Length, line.Text.Length - offset);
            if (length <= 0)
            {
                offset += word.Text.Length;
                continue;
            }

            var nextWordStart = wordIndex + 1 < words.Count ? words[wordIndex + 1].TimeMs : (long?)null;
            var wordEnd = ResolveWordEnd(word, nextWordStart, lineEnd);
            AddEvenCharacterTimings(times, offset, length, word.TimeMs, wordEnd);
            offset += length;
        }

        // 对齐差异留下的字符沿用当前行剩余时段，保证索引完整且永不越界。
        if (offset < line.Text.Length)
        {
            var tailStart = times.Count > 0 ? times[^1].EndMs : line.TimeMs;
            var tailEnd = lineEnd is > 0
                ? Math.Max(lineEnd.Value, tailStart + 1)
                : tailStart + LastWordFallbackMs;
            AddEvenCharacterTimings(times, offset, line.Text.Length - offset, tailStart, tailEnd);
        }

        return times;
    }

    /// <summary>计算指定播放位置的离散/连续歌词状态；最近释放字符只保留 600ms，避免无动画时持续刷新。</summary>
    public static LyricTimingFrame Evaluate(LyricLine line, long positionMs)
    {
        if (line.CharTimes is not { Count: > 0 } times)
            return LyricTimingFrame.None;

        var completed = 0;
        var releasedIndex = -1;
        var releasedDuration = 0L;
        var releasedElapsed = 0L;

        for (var index = 0; index < times.Count; index++)
        {
            var timing = times[index];
            var duration = Math.Max(timing.EndMs - timing.StartMs, 1);
            if (timing.EndMs <= positionMs)
            {
                completed = index + 1;
                releasedIndex = index;
                releasedDuration = duration;
                releasedElapsed = positionMs - timing.EndMs;
                continue;
            }

            var release = RecentRelease(releasedIndex, releasedDuration, releasedElapsed);
            if (positionMs < timing.StartMs)
            {
                return new LyricTimingFrame(
                    completed, 0, -1, 0,
                    release.Index, release.DurationMs, release.ElapsedMs);
            }

            var fraction = Math.Clamp((positionMs - timing.StartMs) / (double)duration, 0, 1);
            return new LyricTimingFrame(
                completed, fraction, index, duration,
                release.Index, release.DurationMs, release.ElapsedMs);
        }

        var finalRelease = RecentRelease(releasedIndex, releasedDuration, releasedElapsed);
        return new LyricTimingFrame(
            times.Count, 1, -1, 0,
            finalRelease.Index, finalRelease.DurationMs, finalRelease.ElapsedMs);
    }

    private static (int Index, long DurationMs, long ElapsedMs) RecentRelease(
        int index, long durationMs, long elapsedMs) =>
        index >= 0 && elapsedMs >= 0 && elapsedMs <= RecentReleaseWindowMs
            ? (index, durationMs, elapsedMs)
            : (-1, 0, 0);

    private static long? ResolveLineEnd(LyricLine line, long? nextLineStart)
    {
        var end = line.EndMs;
        if (nextLineStart is > 0 && nextLineStart > line.TimeMs)
            end = end.HasValue ? Math.Min(end.Value, nextLineStart.Value) : nextLineStart;
        return end;
    }

    private static long ResolveWordEnd(LyricWord word, long? nextWordStart, long? lineEnd)
    {
        var end = word.EndMs
                  ?? nextWordStart
                  ?? lineEnd
                  ?? word.TimeMs + LastWordFallbackMs;

        // 单行渲染器无法表达重叠片段；以后一片段/下一行起点为上限，保持时间轴单调。
        if (nextWordStart.HasValue && nextWordStart.Value > word.TimeMs)
            end = Math.Min(end, nextWordStart.Value);
        if (lineEnd.HasValue && lineEnd.Value > word.TimeMs)
            end = Math.Min(end, lineEnd.Value);

        return end > word.TimeMs ? end : word.TimeMs + 1;
    }

    private static void AddEvenCharacterTimings(
        ICollection<LyricCharTime> target,
        int offset,
        int length,
        long startMs,
        long endMs)
    {
        var duration = Math.Max(endMs - startMs, 1);
        for (var index = 0; index < length; index++)
        {
            var start = startMs + (long)(duration * (double)index / length);
            var end = startMs + (long)(duration * (double)(index + 1) / length);
            target.Add(new LyricCharTime(offset + index, start, Math.Max(end, start + 1)));
        }
    }
}

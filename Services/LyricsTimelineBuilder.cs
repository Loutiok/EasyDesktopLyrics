using EasyDesktopLyrics.Models;
using System.Text;

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
        var source = new List<SourceCharacterTiming>();

        for (var wordIndex = 0; wordIndex < words.Count; wordIndex++)
        {
            var word = words[wordIndex];
            var characters = word.Text.Where(c => !char.IsWhiteSpace(c)).ToArray();
            if (characters.Length == 0)
                continue;

            var nextWordStart = wordIndex + 1 < words.Count ? words[wordIndex + 1].TimeMs : (long?)null;
            var wordEnd = ResolveWordEnd(word, nextWordStart, lineEnd);
            AddSourceCharacterTimings(source, characters, word.TimeMs, wordEnd);
        }

        return AlignToDisplayedText(line, lineEnd, source);
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

    private static void AddSourceCharacterTimings(
        ICollection<SourceCharacterTiming> target,
        IReadOnlyList<char> characters,
        long startMs,
        long endMs)
    {
        var duration = Math.Max(endMs - startMs, 1);
        for (var index = 0; index < characters.Count; index++)
        {
            var start = startMs + (long)(duration * (double)index / characters.Count);
            var end = startMs + (long)(duration * (double)(index + 1) / characters.Count);
            target.Add(new SourceCharacterTiming(
                NormalizeCharacter(characters[index]), start, Math.Max(end, start + 1)));
        }
    }

    /// <summary>
    /// 将逐字源字符映射回实际显示文本。源通常会丢弃英文词间空格，且可能使用全角标点；
    /// 使用 LCS 保持字符顺序，再为未匹配的空格/标点填充相邻时段，避免索引逐步漂移。
    /// </summary>
    private static IReadOnlyList<LyricCharTime> AlignToDisplayedText(
        LyricLine line,
        long? lineEnd,
        IReadOnlyList<SourceCharacterTiming> source)
    {
        var target = line.Text
            .Select((character, index) => new TargetCharacter(index, NormalizeCharacter(character)))
            .Where(item => !char.IsWhiteSpace(item.Character))
            .ToList();
        var mapped = new LyricCharTime?[line.Text.Length];

        var lcs = new int[source.Count + 1, target.Count + 1];
        for (var sourceIndex = source.Count - 1; sourceIndex >= 0; sourceIndex--)
        {
            for (var targetIndex = target.Count - 1; targetIndex >= 0; targetIndex--)
            {
                lcs[sourceIndex, targetIndex] = source[sourceIndex].Character == target[targetIndex].Character
                    ? lcs[sourceIndex + 1, targetIndex + 1] + 1
                    : Math.Max(lcs[sourceIndex + 1, targetIndex], lcs[sourceIndex, targetIndex + 1]);
            }
        }

        var si = 0;
        var ti = 0;
        while (si < source.Count && ti < target.Count)
        {
            if (source[si].Character == target[ti].Character)
            {
                var timing = source[si];
                var displayIndex = target[ti].Index;
                mapped[displayIndex] = new LyricCharTime(displayIndex, timing.StartMs, timing.EndMs);
                si++;
                ti++;
            }
            else if (lcs[si + 1, ti] >= lcs[si, ti + 1])
            {
                si++;
            }
            else
            {
                ti++;
            }
        }

        FillUnmappedRanges(mapped, line.TimeMs, ResolveFallbackEnd(line, lineEnd, source));
        return mapped.Select(timing => timing!).ToList();
    }

    private static void FillUnmappedRanges(
        LyricCharTime?[] mapped,
        long lineStart,
        long lineEnd)
    {
        var index = 0;
        while (index < mapped.Length)
        {
            if (mapped[index] != null)
            {
                index++;
                continue;
            }

            var rangeStart = index;
            while (index < mapped.Length && mapped[index] == null)
                index++;
            var rangeEnd = index;
            var startMs = rangeStart > 0 && mapped[rangeStart - 1] != null
                ? mapped[rangeStart - 1]!.EndMs
                : lineStart;
            var endMs = rangeEnd < mapped.Length && mapped[rangeEnd] != null
                ? mapped[rangeEnd]!.StartMs
                : lineEnd;
            endMs = Math.Max(endMs, startMs + rangeEnd - rangeStart);

            for (var current = rangeStart; current < rangeEnd; current++)
            {
                var start = startMs + (long)((endMs - startMs) * (double)(current - rangeStart) / (rangeEnd - rangeStart));
                var end = startMs + (long)((endMs - startMs) * (double)(current - rangeStart + 1) / (rangeEnd - rangeStart));
                mapped[current] = new LyricCharTime(current, start, Math.Max(end, start + 1));
            }
        }
    }

    private static long ResolveFallbackEnd(
        LyricLine line,
        long? lineEnd,
        IReadOnlyList<SourceCharacterTiming> source)
    {
        var sourceEnd = source.Count > 0 ? source[^1].EndMs : line.TimeMs;
        return Math.Max(lineEnd ?? sourceEnd + LastWordFallbackMs, Math.Max(sourceEnd, line.TimeMs + 1));
    }

    private static char NormalizeCharacter(char character)
    {
        var normalized = character.ToString().Normalize(NormalizationForm.FormKC);
        return normalized.Length > 0 ? char.ToUpperInvariant(normalized[0]) : character;
    }

    private readonly record struct SourceCharacterTiming(char Character, long StartMs, long EndMs);

    private readonly record struct TargetCharacter(int Index, char Character);
}

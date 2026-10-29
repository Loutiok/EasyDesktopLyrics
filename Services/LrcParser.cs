using System.Text.RegularExpressions;
using EasyDesktopLyrics.Models;

namespace EasyDesktopLyrics.Services;

/// <summary>
/// LRC 解析：行级时间戳、一行多标签、[offset:] 标签；翻译按相同时间戳（±20ms）合并；
/// 逐字歌词支持：yrc、尖括号增强 LRC (<ts>字)、方括号逐字 LRC ([ts]字[ts]字)。
/// </summary>
public static partial class LrcParser
{
    /// <summary>
    /// 瞬时空行过滤阈值（ms）：空文本行距下一句非空歌词不足此值时视为源文件伪影并丢弃，
    /// 否则播放定位会短暂选中空行而闪现省略号。仅当空行占据足够久的时间（真实间奏）才保留。
    /// </summary>
    private const long TransientEmptyLineMs = 2000;

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    /// <summary>逐字标签（增强 LRC / 网易 klyric）：&lt;mm:ss.xx&gt;字。</summary>
    [GeneratedRegex(@"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")]
    private static partial Regex WordTag();

    /// <summary>yrc 逐字行：行头 [开始ms,持续ms] + (字开始ms,字持续ms,0)字 序列。</summary>
    [GeneratedRegex(@"\[(\d+),(\d+)\](.*)")]
    private static partial Regex YrcLineTag();

    [GeneratedRegex(@"\((\d+),(\d+),(\d+)\)")]
    private static partial Regex YrcWordTag();

    [GeneratedRegex(@"\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    /// <summary>
    /// 解析逐字歌词：支持三种格式（自动识别）——
    /// 1. yrc：行头 [开始ms,持续ms] + (字开始ms,持续ms,0)字 序列；
    /// 2. 方括号逐字 LRC：行内穿插 [mm:ss.xx]字[mm:ss.xx]字 序列；
    /// 3. 尖括号增强 LRC / klyric：行首 [mm:ss.xx] + &lt;ts&gt;字 序列。
    /// 返回按行时间升序的逐字行，并保留源中存在的行/字持续时间。
    /// </summary>
    public static IReadOnlyList<LyricWordLine> ParseWordLines(string? wordLrc)
    {
        var result = new List<LyricWordLine>();
        if (string.IsNullOrWhiteSpace(wordLrc))
            return result;

        foreach (var rawLine in wordLrc.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
                continue;

            // 1. yrc 格式（行头 [ms,ms]）
            var ym = YrcLineTag().Match(line);
            if (ym.Success && ym.Index == 0
                && long.TryParse(ym.Groups[1].Value, out var yStart)
                && long.TryParse(ym.Groups[2].Value, out var yDuration))
            {
                var yrcWords = ParseYrcWordsInLine(ym.Groups[3].Value);
                if (yrcWords.Count > 0)
                    result.Add(new LyricWordLine(yStart, yrcWords, PositiveDuration(yDuration)));
                continue;
            }

            var matches = TimeTag().Matches(line);
            if (matches.Count == 0)
                continue;

            // 2. 方括号逐字格式（如 [00:01.405]悪[00:01.933]戯[00:02.431]...[00:04.773]）
            // 条件：包含多个方括号标签，且不包含尖括号逐字标签
            if (matches.Count > 2 && !line.Contains('<'))
            {
                var bracketWords = ParseBracketWordsInLine(line, matches);
                if (bracketWords.Count > 0)
                {
                    var lineStart = ParseStamp(matches[0]);
                    var lineEnd = ParseStamp(matches[^1]);
                    var duration = lineEnd > lineStart ? (long?)(lineEnd - lineStart) : null;
                    result.Add(new LyricWordLine(lineStart, bracketWords, duration));
                    continue;
                }
            }

            // 3. 增强 LRC / klyric 格式（行首 [mm:ss.xx] 时间标签 + <ts>字）
            var end = 0;
            var stamps = new List<long>();
            foreach (Match m in matches)
            {
                if (m.Index != end)
                    break;
                stamps.Add(ParseStamp(m));
                end = m.Index + m.Length;
            }
            if (stamps.Count == 0)
                continue;

            var content = line[end..];
            var words = ParseWordsInLine(content, stamps[0]);
            if (words.Count > 0)
                result.Add(new LyricWordLine(stamps[0], words));
        }

        return result.OrderBy(x => x.TimeMs).ToList();
    }

    /// <summary>
    /// 解析方括号逐字行：[开始ts]字[下个ts]字...[结束ts]
    /// </summary>
    private static List<LyricWord> ParseBracketWordsInLine(string line, MatchCollection matches)
    {
        var words = new List<LyricWord>();

        for (var i = 0; i < matches.Count; i++)
        {
            var curMatch = matches[i];
            var curTime = ParseStamp(curMatch);

            var textStart = curMatch.Index + curMatch.Length;
            var textEnd = (i + 1 < matches.Count) ? matches[i + 1].Index : line.Length;
            var text = line[textStart..textEnd];

            // 依据下个标签计算当前字时长
            long? duration = null;
            if (i + 1 < matches.Count)
            {
                var nextTime = ParseStamp(matches[i + 1]);
                if (nextTime >= curTime)
                    duration = nextTime - curTime;
            }

            // 行尾用于标记闭合的时间戳后面往往为空字符串，予以跳过
            if (!string.IsNullOrEmpty(text))
            {
                words.Add(new LyricWord(curTime, text, duration));
            }
        }

        return words;
    }

    /// <summary>yrc 行内字序列：(开始ms,持续ms,未知)字 …。</summary>
    private static List<LyricWord> ParseYrcWordsInLine(string content)
    {
        var words = new List<LyricWord>();
        var index = 0;
        while (index < content.Length)
        {
            var tag = YrcWordTag().Match(content, index);
            if (!tag.Success)
                break;
            var start = long.Parse(tag.Groups[1].Value);
            var duration = long.Parse(tag.Groups[2].Value);
            var textEnd = index + tag.Length;
            var next = YrcWordTag().Match(content, textEnd);
            var textLen = next.Success ? next.Index - textEnd : content.Length - textEnd;
            var text = content[textEnd..(textEnd + textLen)].Trim();
            if (text.Length > 0)
                words.Add(new LyricWord(start, text, PositiveDuration(duration)));
            index = next.Success ? next.Index : content.Length;
        }
        return words;
    }

    private static long? PositiveDuration(long durationMs) => durationMs > 0 ? durationMs : null;

    /// <summary>把行内容切分为字序列；行首无标签的文本归属行起始时间。</summary>
    private static List<LyricWord> ParseWordsInLine(string content, long lineTime)
    {
        var words = new List<LyricWord>();
        var index = 0;
        while (index < content.Length && content[index] == ' ')
            index++;

        // 行首文本（第一个字标签之前）→ 从行时间开始
        var head = content[index..];
        var m = WordTag().Match(head);
        if (m.Index > 0)
        {
            var text = head[..m.Index].Trim();
            if (text.Length > 0)
                words.Add(new LyricWord(lineTime, text));
            index += m.Index;
        }

        // <ts>字 序列
        while (index < content.Length)
        {
            var rest = content[index..];
            var tag = WordTag().Match(rest);
            if (!tag.Success)
                break;
            var ts = ParseStamp(tag);
            var textEnd = index + tag.Index + tag.Length;
            var next = WordTag().Match(content[textEnd..]);
            var textLen = next.Success ? next.Index : content.Length - textEnd;
            var text = content[textEnd..(textEnd + textLen)].Trim();
            if (text.Length > 0)
                words.Add(new LyricWord(ts, text));
            index = textEnd;
        }

        return words;
    }

    private static long ParseStamp(Match m)
    {
        var min = long.Parse(m.Groups[1].Value);
        var sec = long.Parse(m.Groups[2].Value);
        long frac = 0;
        if (m.Groups[3].Success)
        {
            var f = m.Groups[3].Value;
            frac = f.Length switch
            {
                1 => int.Parse(f) * 100,
                2 => int.Parse(f) * 10,
                _ => int.Parse(f[..3]),
            };
        }
        return min * 60_000 + sec * 1000 + frac;
    }

    /// <summary>解析主歌词 + 可选翻译；无任何带时间戳的行时返回 null（视为无可用歌词）。</summary>
    public static LyricDocument? Parse(string? mainLrc, string? transLrc)
    {
        var main = ParseTimed(mainLrc);
        if (main.Count == 0)
            return null;

        // 过滤"瞬时空行"：空文本行仅在距下一句非空歌词足够久（真实间奏）才保留
        var filtered = new List<(long TimeMs, string Text)>(main.Count);
        for (var i = 0; i < main.Count; i++)
        {
            var (timeMs, text) = main[i];
            if (text.Length == 0)
            {
                var nextNonEmpty = -1L;
                for (var j = i + 1; j < main.Count; j++)
                {
                    if (main[j].Text.Length > 0)
                    {
                        nextNonEmpty = main[j].TimeMs;
                        break;
                    }
                }
                if (nextNonEmpty >= 0 && nextNonEmpty - timeMs < TransientEmptyLineMs)
                    continue;
            }
            filtered.Add((timeMs, text));
        }

        var lines = filtered.Select(m => new LyricLine(m.TimeMs, m.Text, null)).ToList();

        if (!string.IsNullOrWhiteSpace(transLrc))
        {
            var trans = ParseTimed(transLrc);
            if (trans.Count > 0)
            {
                // 时间戳取整到 10ms 作桶，容差 ±20ms
                var map = new Dictionary<long, string>();
                foreach (var t in trans)
                    if (t.Text.Length > 0)
                        map[t.TimeMs / 10] = t.Text;

                for (var i = 0; i < lines.Count; i++)
                {
                    var bucket = lines[i].TimeMs / 10;
                    string? tr = null;
                    for (long d = 0; d <= 2 && tr is null; d++)
                    {
                        if (map.TryGetValue(bucket + d, out var v) || map.TryGetValue(bucket - d, out v))
                            tr = v;
                    }
                    if (tr != null)
                        lines[i] = lines[i] with { Translation = tr };
                }
            }
        }

        return new LyricDocument(lines);
    }

    /// <summary>二分定位：返回最后一个 TimeMs &lt;= posMs 的行索引；前奏返回 -1。</summary>
    public static int FindIndex(IReadOnlyList<LyricLine> lines, long posMs)
    {
        int lo = 0, hi = lines.Count - 1, ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].TimeMs <= posMs)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return ans;
    }

    private static List<(long TimeMs, string Text)> ParseTimed(string? text)
    {
        var result = new List<(long TimeMs, string Text)>();
        if (string.IsNullOrWhiteSpace(text))
            return result;

        long offset = 0;
        var om = OffsetTag().Match(text);
        if (om.Success && long.TryParse(om.Groups[1].Value, out var o))
            offset = o;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var matches = TimeTag().Matches(line);
            if (matches.Count == 0)
                continue;

            // 时间标签必须是行首连续前缀
            var end = 0;
            var stamps = new List<long>();
            foreach (Match m in matches)
            {
                if (m.Index != end)
                    break;
                stamps.Add(ParseStamp(m));
                end = m.Index + m.Length;
            }
            if (stamps.Count == 0)
                continue;

            var rawContent = line[end..];

            // 清洗行内残余的逐字时间标签（去除 <ts> 以及残余的 [ts]），还原为纯净的行歌词
            var plainContent = TimeTag().Replace(rawContent, "");
            plainContent = WordTag().Replace(plainContent, "").Trim();

            foreach (var s in stamps)
            {
                // LRC 约定：offset 正值 = 歌词整体提前
                var effective = Math.Max(0, s - offset);
                result.Add((effective, plainContent));
            }
        }

        return result.OrderBy(x => x.TimeMs).ToList();
    }
}

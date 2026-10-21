using System.Text.RegularExpressions;
using EasyDesktopLyrics.Models;

namespace EasyDesktopLyrics.Services.Providers;

/// <summary>
/// 本地 LRC 文件源：扫描设置目录下的 *.lrc 文件（一层），
/// 文件名按 "艺术家 - 标题.lrc" 解析，匹配打分复用 LyricsMatcher。
/// 修改本地歌词文件后需清空缓存（清空歌词缓存按钮）。
/// </summary>
public sealed class LocalLrcProvider : ILyricsProvider
{
    private readonly SettingsService _settings;

    // 行首单个时间戳，如 [00:12.34] / [00:12.345] / [00:12:34]
    private static readonly Regex LeadingSingleTsRegex = new(
        @"^\s*(\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\])\s*",
        RegexOptions.Compiled);

    // 行首连续多个时间戳（副歌重复行），用于排除，不参与翻译识别
    private static readonly Regex LeadingTsSequenceRegex = new(
        @"^(?:\s*\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\])+",
        RegexOptions.Compiled);

    private static readonly Regex TsRegex = new(
        @"\[\d{1,2}:\d{2}(?:[.:]\d{1,3})?\]",
        RegexOptions.Compiled);

    public LocalLrcProvider(SettingsService settings) => _settings = settings;

    public string Id => "local";

    public string DisplayName => "本地歌词文件";

    public Task<IReadOnlyList<ProviderSong>> SearchAsync(string keyword, CancellationToken ct)
    {
        var list = new List<ProviderSong>();
        var folder = _settings.Current.LyricsFolder;
        if (string.IsNullOrWhiteSpace(folder))
            return Task.FromResult<IReadOnlyList<ProviderSong>>(list);

        foreach (var dir in folder.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.lrc", SearchOption.TopDirectoryOnly))
            {
                if (ct.IsCancellationRequested)
                    return Task.FromResult<IReadOnlyList<ProviderSong>>(list);
                var (artist, title) = SplitName(Path.GetFileNameWithoutExtension(file));
                if (title.Length == 0)
                    continue;
                list.Add(new ProviderSong(Id, file, title, artist, "", 0));
            }
        }
        return Task.FromResult<IReadOnlyList<ProviderSong>>(list);
    }

    public Task<RawLyric?> GetLyricAsync(string songId, CancellationToken ct)
    {
        try
        {
            var rawText = File.ReadAllText(songId);
            var (original, translation) = SplitTranslationLines(rawText);
            return Task.FromResult<RawLyric?>(new RawLyric(original, translation));
        }
        catch (Exception)
        {
            return Task.FromResult<RawLyric?>(null);
        }
    }

    /// <summary>
    /// 把 LRC 文本里「与上一行时间戳相同」的连续行识别为翻译行，
    /// 原文与翻译各自保留时间戳，返回 (原文 LRC, 翻译 LRC 或 null)。
    /// </summary>
    private static (string Original, string? Translation) SplitTranslationLines(string lrcText)
    {
        var normalized = lrcText.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n');

        var original = new List<string>(lines.Length);
        var translation = new List<string>();
        var hasTranslation = false;

        long lastTsMs = long.MinValue;

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.Length == 0)
                continue;

            // 行首多个时间戳 = 同一句歌词的重复时间点，属于原文，跳过翻译识别
            var seq = LeadingTsSequenceRegex.Match(line);
            if (seq.Success && TsRegex.Matches(seq.Value).Count > 1)
            {
                original.Add(line);
                lastTsMs = long.MinValue;
                continue;
            }

            var single = LeadingSingleTsRegex.Match(line);
            if (!single.Success)
            {
                // 元数据行（[ar:...]/[ti:...]/[offset:...]）或纯文本行
                original.Add(line);
                lastTsMs = long.MinValue;
                continue;
            }

            var ts = single.Groups[1].Value;
            var tsMs = ParseTimestampMs(ts);
            var content = line[single.Length..].Trim();

            if (tsMs >= 0 && tsMs == lastTsMs && content.Length > 0)
            {
                // 同时间戳的后续行 → 翻译
                translation.Add(ts + content);
                hasTranslation = true;
            }
            else
            {
                original.Add(line);
                lastTsMs = tsMs;
            }
        }

        return (
            string.Join('\n', original),
            hasTranslation ? string.Join('\n', translation) : null
        );
    }

    /// <summary>把 [mm:ss.xxx] 解析为毫秒；解析失败返回 -1。</summary>
    private static long ParseTimestampMs(string ts)
    {
        var inner = ts.Trim('[', ']');
        var colon = inner.IndexOf(':');
        if (colon <= 0) return -1;
        if (!int.TryParse(inner.AsSpan(0, colon), out var min)) return -1;

        var rest = inner[(colon + 1)..];
        var dot = rest.IndexOfAny(new[] { '.', ':' });
        var secStr = dot >= 0 ? rest[..dot] : rest;
        var fracStr = dot >= 0 ? rest[(dot + 1)..] : "0";

        if (!int.TryParse(secStr, out var sec)) return -1;

        fracStr = fracStr.PadRight(3, '0');
        fracStr = fracStr[..3];
        if (!int.TryParse(fracStr, out var ms)) return -1;

        return ((long)min * 60 + sec) * 1000 + ms;
    }

    /// <summary>解析 "艺术家 - 标题"（兼容全角连字符）。</summary>
    private static (string Artist, string Title) SplitName(string name)
    {
        var idx = name.IndexOf(" - ", StringComparison.Ordinal);
        if (idx <= 0 || idx >= name.Length - 3)
        {
            var full = name.IndexOf("－", StringComparison.Ordinal);
            if (full <= 0 || full >= name.Length - 1)
                return ("", name);
            return (name[..full].Trim(), name[(full + 1)..].Trim());
        }
        return (name[..idx].Trim(), name[(idx + 3)..].Trim());
    }
}

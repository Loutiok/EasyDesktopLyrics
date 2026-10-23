using System.Text.RegularExpressions;
using System.Text;
using Avalonia.Threading;
using EasyDesktopLyrics.Infrastructure;
using EasyDesktopLyrics.Models;
using EasyDesktopLyrics.Services.Providers;

namespace EasyDesktopLyrics.Services;

public enum LyricsPhase
{
    NoSession,
    Resolving,
    Ready,
    NoLyric,
}

/// <summary>
/// 协调层：曲目变化 → override/缓存/搜索匹配 → LyricDocument；
/// 100ms 定时器 → 二分定位当前行，仅行号变化时通知 UI。
/// 除 ResolveCoreAsync 内部外全部运行在 UI 线程。
/// </summary>
public sealed partial class LyricsOrchestrator
{
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromDays(3);

    /// <summary>纯音乐占位行：整首歌词仅含此类文本时视为无歌词（显示标题）。</summary>
    [GeneratedRegex("纯音乐|伴奏|演奏曲|instrumental|no lyrics|music only", RegexOptions.IgnoreCase)]
    private static partial Regex InstrumentalPattern();

    /// <summary>作词/作曲等元信息行（纯音乐中保留显示，显示完后切回标题）。</summary>
    [GeneratedRegex("^(作词|作曲|编曲|制作人?|OP|SP|原曲|演唱|和声|混音|母带|录音|发行|版权|企划|出品)", RegexOptions.IgnoreCase)]
    private static partial Regex MetaInfoPattern();

    private readonly SettingsService _settings;
    private readonly LyricsCache _cache;
    private readonly OverridesStore _overrides;
    private readonly IReadOnlyList<ILyricsProvider> _providers;
    private readonly PlaybackClock _clock = new();
    private readonly DispatcherTimer _timer;

    private CancellationTokenSource? _cts;
    private LyricDocument? _doc;
    private int _lineIndex = -1;
    private int _songOffsetMs;
    private bool _instrumentalEnded;
    private bool _hasWords;
    private LyricTimingFrame _timingFrame = LyricTimingFrame.None;
    private bool _suppressLineTransition;
    private bool _waitingForFreshPosition;   // 切歌后到新歌词就绪前，SMTC 位置不可信
    private DateTimeOffset _trackChangedAt;

    public LyricsOrchestrator(
        SmtcService smtc,
        SettingsService settings,
        LyricsCache cache,
        OverridesStore overrides,
        IReadOnlyList<ILyricsProvider> providers)
    {
        _settings = settings;
        _cache = cache;
        _overrides = overrides;
        _providers = providers;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += (_, _) => Tick(forceRaise: false);

        smtc.TrackChanged += OnTrackChanged;
        smtc.PlaybackChanged += OnPlaybackChanged;
    }

    public LyricsPhase Phase { get; private set; } = LyricsPhase.NoSession;

    public TrackInfo? Track { get; private set; }

    public bool IsPlaying => _clock.IsPlaying;

    public string CurrentMain { get; private set; } = "";

    public string CurrentTrans { get; private set; } = "";

    /// <summary>true = 当前歌词为纯音乐（仅元信息行）。</summary>
    public bool IsInstrumental => _doc?.IsInstrumental == true;

    /// <summary>当前歌词是否含逐字（卡拉 OK）数据。</summary>
    public bool HasWordData => _hasWords;

    /// <summary>纯音乐元信息行已显示完毕（时间超过最后一行 5 秒）→ 切回标题。</summary>
    public bool InstrumentalEnded => _instrumentalEnded;

    /// <summary>当前行已唱字符数；-1 = 非逐字模式（整行高亮）。</summary>
    public int CurrentWordDone => _timingFrame.CompletedCharacters;

    /// <summary>正在唱字符的渐变进度 0~1（字符内平滑点亮）；非逐字模式为 0。</summary>
    public double CurrentWordFraction => _timingFrame.HighlightFraction;

    /// <summary>当前播放时刻的精确歌词时间轴快照，供视觉模式驱动当前音节和释放余辉。</summary>
    public LyricTimingFrame CurrentTimingFrame => _timingFrame;

    /// <summary>任何展示相关状态变化（相位/当前行/播放状态）。UI 线程回调。</summary>
    public event Action? StateChanged;

    /// <summary>
    /// 当前行切换（歌词行号变化）。UI 线程回调；bool = 是否抑制行间动效
    /// （true：切歌/手动校正/seek 跳变等场景，应由宿主直接瞬切）。
    /// </summary>
    public event Action<bool>? LineChanged;

    /// <summary>手动校正后强制重新解析当前曲目（绕过磁盘缓存）。</summary>
    public void RefreshCurrent()
    {
        var t = Track;
        if (t == null)
            return;
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        _suppressLineTransition = true;
        Phase = LyricsPhase.Resolving;
        _doc = null;
        _hasWords = false;
        _lineIndex = -1;
        _timingFrame = LyricTimingFrame.None;
        CurrentMain = "";
        CurrentTrans = "";
        UpdateTimer();
        Raise();
        _ = ResolveAndApplyAsync(t, force: true, cts.Token);
    }

    /// <summary>单曲偏移变更后即时生效（不重新拉取歌词）。</summary>
    public void NotifySongOffsetChanged()
    {
        var t = Track;
        if (t == null)
            return;
        _songOffsetMs = _overrides.Get(LyricsMatcher.TrackKey(t))?.OffsetMs ?? 0;
        Tick(forceRaise: true);
    }

    private void OnTrackChanged(TrackInfo? t)
    {
        // 切歌后到新歌词就绪前，SMTC 可能推送旧曲位置（PositionAt 新鲜但位置属于旧曲）。
        // 置位后 OnPlaybackChanged 会让 PlaybackClock 只更新 IsPlaying，不采用位置。
        _waitingForFreshPosition = true;
        _trackChangedAt = DateTimeOffset.UtcNow;

        _cts?.Cancel();
        _cts = null;
        _suppressLineTransition = true;
        Track = t;
        _doc = null;
        _hasWords = false;
        _lineIndex = -1;
        _songOffsetMs = 0;
        _timingFrame = LyricTimingFrame.None;
        CurrentMain = "";
        CurrentTrans = "";
        _clock.Reset();

        if (t == null)
        {
            _waitingForFreshPosition = false; // 无会话，不再等待位置
            Phase = LyricsPhase.NoSession;
            UpdateTimer();
            Raise();
            return;
        }

        Phase = LyricsPhase.Resolving;
        UpdateTimer();
        Raise();

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = ResolveAndApplyAsync(t, force: false, cts.Token);
    }

    private void OnPlaybackChanged(PlaybackSnapshot snapshot)
    {
        // 切歌后 2 秒内，SMTC 可能连续推送多个"旧曲残留"事件：
        // Duration 已更新为新曲，但 Position 还是旧曲的，且可能推多个。
        // 只拦第一个不够（后续会被接受 → 跳到旧位置 → 闪过其他歌词）。
        // 因此窗口内的所有事件都不采用 Position，只更新 IsPlaying，
        // 位置由挂钟从 TrackChanged 时 Reset 的 0 稳步驱动。
        if (_waitingForFreshPosition)
        {
            var elapsed = DateTimeOffset.UtcNow - _trackChangedAt;
            if (elapsed < TimeSpan.FromSeconds(2))
            {
                Log.Info($"playback zeroed (settling after track change): " +
                         $"elapsed={elapsed.TotalMilliseconds:F0}ms " +
                         $"pos={snapshot.Position.TotalSeconds:F2}s " +
                         $"dur={snapshot.Duration.TotalSeconds:F2}s");
                // 只更新 IsPlaying（PlaybackClock 走降级分支），不采用 Position
                snapshot = snapshot with { PositionAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1) };
                // 若 PlaybackSnapshot 不是 record 导致 with 不可用，改为：
                // snapshot = new PlaybackSnapshot
                // {
                //     Position = snapshot.Position,
                //     PositionAt = DateTimeOffset.UtcNow - TimeSpan.FromHours(1),
                //     Duration = snapshot.Duration,
                //     Rate = snapshot.Rate,
                //     IsPlaying = snapshot.IsPlaying,
                // };
            }
            else
            {
                Log.Info($"playback accepted (settled): pos={snapshot.Position.TotalSeconds:F2}s " +
                         $"elapsed={elapsed.TotalSeconds:F2}s");
                _waitingForFreshPosition = false;
            }
        }

        var posBefore = _clock.Estimate();
        _clock.Sync(snapshot);
        UpdateTimer();
        var jump = _clock.Estimate() - posBefore;
        var seekJumped = jump > TimeSpan.FromSeconds(2) || jump < -TimeSpan.FromSeconds(2);
        if (seekJumped)
            _suppressLineTransition = true;
        Tick(forceRaise: seekJumped);
        Raise();
    }

    private async Task ResolveAndApplyAsync(TrackInfo t, bool force, CancellationToken ct)
    {
        LyricDocument? doc = null;
        var songOffset = 0;
        try
        {
            (doc, songOffset) = await Task.Run(() => ResolveCoreAsync(t, force, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"resolve failed: {t.Title}", ex);
        }

        if (ct.IsCancellationRequested)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(Track, t))
                return;
            _doc = doc;
            _hasWords = doc?.Lines.Any(l => l.Words is { Count: > 0 }) == true;
            _songOffsetMs = songOffset;
            _lineIndex = -1;
            _timingFrame = LyricTimingFrame.None;
            CurrentMain = "";
            CurrentTrans = "";

            // 兜底：若就绪时时钟仍残留旧曲位置且明显超出新曲时间轴末尾，重置。
            // 正常情况下 _waitingForFreshPosition 已保证切歌后位置归零，这里是极端路径保护。
            if (doc is { Lines.Count: > 0 })
            {
                var lastMs = doc.Lines[^1].TimeMs;
                var currentMs = _clock.Estimate().TotalMilliseconds;
                if (currentMs > lastMs + 30000)
                {
                    Log.Info($"clock reset after track change: pos={currentMs:F0}ms > docEnd={lastMs}ms");
                    _clock.Reset();
                }
            }


            Phase = doc != null ? LyricsPhase.Ready : LyricsPhase.NoLyric;
            Log.Info($"lyric {(doc != null ? $"ready ({doc.Lines.Count} lines, words={_hasWords})" : "not found")}: {t.Title}");
            UpdateTimer();
            Tick(forceRaise: true);
            Raise();
        });
    }

    private async Task<(LyricDocument? Doc, int SongOffsetMs)> ResolveCoreAsync(TrackInfo t, bool force, CancellationToken ct)
    {
        var key = LyricsMatcher.TrackKey(t);
        var ov = _overrides.Get(key);
        var songOffset = ov?.OffsetMs ?? 0;

        // 1. 磁盘缓存
        if (!force)
        {
            var cached = await _cache.GetAsync(key).ConfigureAwait(false);
            if (cached != null)
            {
                var ovMismatch = ov?.SongId is { Length: > 0 }
                                 && (cached.SongId != ov.SongId || cached.Source != ov.Provider);
                if (!ovMismatch)
                {
                    if (cached.NotFound)
                    {
                        if (ov?.SongId is null && DateTimeOffset.UtcNow - cached.FetchedAt < NegativeCacheTtl)
                            return (null, songOffset);
                    }
                    else
                    {
                        var cachedDoc = ParseLyric(cached.Lrc, cached.TransLrc, cached.WordLrc);
                        if (cachedDoc != null)
                        {
                            Log.Info($"cache hit: {key} [{cached.Source}] words={!string.IsNullOrEmpty(cached.WordLrc)}");                            // 缓存无逐字（上次 yrc 获取失败/无数据）→ 后台补拉 yrc，不阻塞歌词显示；
                            // 仅网易云源缓存可补拉（yrc 是网易专属字段；其他源的 SongId 不能用于网易云请求）
                            if (string.IsNullOrEmpty(cached.WordLrc)
                                && cached.Source == "netease"
                                && !string.IsNullOrEmpty(cached.SongId)
                                && _providers.FirstOrDefault(p => p.Id == "netease") is { } yrcProvider)
                            {
                                RefreshYrcAsync(key, t, yrcProvider, cached.SongId, ct);
                            }
                            return (cachedDoc, songOffset);
                        }
                    }
                }
            }
        }
        Log.Info($"cache miss: {key} ({t.Artist}|{t.Title} dur={t.DurationMs}ms)");

        // 2. 手动校正指定的歌词
        if (ov is { SongId.Length: > 0, Provider.Length: > 0 })
        {
            var provider = _providers.FirstOrDefault(p => p.Id == ov.Provider);
            if (provider != null)
            {
                var raw = await SafeGetLyricAsync(provider, ov.SongId, ct).ConfigureAwait(false);
                var doc = raw != null ? ParseLyric(raw.Lrc, raw.TranslationLrc, raw.WordLrc) : null;
                if (doc != null && raw != null)
                {
                    await _cache.SetAsync(key, CachedLyric.Positive(provider.Id, ov.SongId, raw)).ConfigureAwait(false);
                    return (doc, songOffset);
                }
            }
            // override 失效 → 继续走自动匹配
        }

        // 3. 自动搜索 + 打分
        var enabled = EnabledProviders();
        var candidates = new List<(ILyricsProvider Provider, ProviderSong Song, double Score)>();
        for (var i = 0; i < enabled.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var provider = enabled[i];
            IReadOnlyList<ProviderSong> found;
            try
            {
                found = await provider.SearchAsync(LyricsMatcher.BuildKeyword(t), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error($"search failed ({provider.Id})", ex);
                continue;
            }

            foreach (var song in found)
                candidates.Add((provider, song, LyricsMatcher.Score(t, song)));
            Log.Info($"search {provider.Id}: {found.Count} hits for '{LyricsMatcher.BuildKeyword(t)}'");

            // 第一源已有高置信度命中，省掉第二源的请求
            if (i == 0 && candidates.Any(c => c.Score >= LyricsMatcher.EarlyAcceptThreshold))
                break;
        }

        // 优先尝试网易云候选（唯一带逐字歌词的源）：同合格线内 netease 排最前，防其他源以更高 score 抢走；
        // 拉取失败仍会继续尝试后续源兜底
        foreach (var c in candidates
                     .Where(c => c.Score >= LyricsMatcher.AcceptThreshold)
                     .OrderByDescending(c => c.Provider.Id == "netease" ? 1 : 0)
                     .ThenByDescending(c => c.Score)
                     .Take(3))
        {
            ct.ThrowIfCancellationRequested();
            var raw = await SafeGetLyricAsync(c.Provider, c.Song.SongId, ct).ConfigureAwait(false);
            if (raw == null)
                continue;
            var doc = ParseLyric(raw.Lrc, raw.TranslationLrc, raw.WordLrc);
            if (doc == null)
                continue;

            await _cache.SetAsync(key, CachedLyric.Positive(c.Provider.Id, c.Song.SongId, raw)).ConfigureAwait(false);
            Log.Info($"matched [{c.Provider.Id}] {c.Song.Artist} - {c.Song.Title} score={c.Score:F2} id={c.Song.SongId}");
            return (doc, songOffset);
        }

        await _cache.SetAsync(key, CachedLyric.Negative()).ConfigureAwait(false);
        return (null, songOffset);
    }

    /// <summary>
    /// 解析 LRC（可选逐字 klyric 合并）。纯音乐判定：排除元信息行后，无内容或全部为纯音乐占位字样。
    /// 纯音乐且存在元信息行（作词/作曲等）→ 保留元信息行短暂显示，之后切回标题；
    /// 否则返回 null（直接显示标题）。
    /// </summary>
    private static LyricDocument? ParseLyric(string? lrc, string? transLrc, string? wordLrc)
    {
        var doc = LrcParser.Parse(lrc, transLrc);
        if (doc == null)
            return null;

        if (!string.IsNullOrWhiteSpace(wordLrc))
            doc = MergeWordLines(doc, LrcParser.ParseWordLines(wordLrc));

        var texts = doc.Lines.Select(l => l.Text).Where(t => t.Length > 0).ToList();
        if (texts.Count == 0)
            return null;

        var content = texts.Where(t => !MetaInfoPattern().IsMatch(t)).ToList();
        if (content.Count > 0 && !content.All(t => InstrumentalPattern().IsMatch(t)))
            return LyricsTimelineBuilder.AssignCharacterTimings(doc); // 正常歌词

        // 纯音乐：保留元信息行供短暂显示
        var meta = doc.Lines
            .Where(l => l.Text.Length > 0 && MetaInfoPattern().IsMatch(l.Text))
            .ToList();
        return meta.Count > 0
            ? LyricsTimelineBuilder.AssignCharacterTimings(new LyricDocument(meta, isInstrumental: true))
            : null;
    }

    /// <summary>
    /// 逐字补充层合并：优先按规范化文本挂到主歌词行，时间仅用于重复句消歧；
    /// 文本不可匹配时才回退到 ±800ms 最近行。
    /// 匹配率 ≥60% 才视为整首有效（否则整体回退整行高亮）。
    /// </summary>
    private static LyricDocument MergeWordLines(
        LyricDocument doc, IReadOnlyList<LyricWordLine> wordLines)
    {
        if (wordLines.Count == 0)
            return doc;

        var lines = doc.Lines.ToList();
        var matched = 0;
        var usedWordLines = new HashSet<int>();
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            long bestDelta = long.MaxValue;
            LyricWordLine? best = null;
            var bestIndex = -1;
            var normalizedText = NormalizeLyricText(l.Text);

            // YRC 的行起点可能包含数秒前导长音/静音，不能只依赖时间戳。
            // 同文重复句仍以时间最近者消歧，并保证一条逐字行最多消费一次。
            if (normalizedText.Length > 0)
            {
                for (var wordIndex = 0; wordIndex < wordLines.Count; wordIndex++)
                {
                    if (usedWordLines.Contains(wordIndex))
                        continue;
                    var wl = wordLines[wordIndex];
                    var wordText = string.Concat(wl.Words.Select(word => word.Text));
                    if (NormalizeLyricText(wordText) != normalizedText)
                        continue;
                    var delta = Math.Abs(wl.TimeMs - l.TimeMs);
                    if (delta >= bestDelta)
                        continue;
                    bestDelta = delta;
                    best = wl;
                    bestIndex = wordIndex;
                }
            }

            if (best == null)
            {
                for (var wordIndex = 0; wordIndex < wordLines.Count; wordIndex++)
                {
                    if (usedWordLines.Contains(wordIndex))
                        continue;
                    var wl = wordLines[wordIndex];
                    var delta = Math.Abs(wl.TimeMs - l.TimeMs);
                    if (delta >= bestDelta)
                        continue;
                    bestDelta = delta;
                    best = wl;
                    bestIndex = wordIndex;
                }
                if (bestDelta >= 800)
                    best = null;
            }

            if (best != null)
            {
                long? duration = best.EndMs.HasValue
                    ? Math.Max(best.EndMs.Value - l.TimeMs, 1)
                    : null;
                lines[i] = l with { Words = best.Words, DurationMs = duration };
                usedWordLines.Add(bestIndex);
                matched++;
            }
        }
        return matched * 10 >= lines.Count * 6
            ? new LyricDocument(lines, doc.IsInstrumental)
            : doc;
    }

    private static string NormalizeLyricText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC);
        return string.Concat(normalized.Where(char.IsLetterOrDigit)).ToUpperInvariant();
    }

    /// <summary>
    /// 后台补拉逐字歌词：缓存命中但无 yrc 时异步重试（网易云接口不稳定）；
    /// 成功后替换内存文档启用逐字高亮，并更新磁盘缓存。失败静默（保持整行高亮）。
    /// </summary>
    private async void RefreshYrcAsync(string key, TrackInfo t, ILyricsProvider provider, string songId, CancellationToken ct)
    {
        LyricDocument? withWords = null;
        RawLyric? raw = null;
        try
        {
            raw = await SafeGetLyricAsync(provider, songId, ct).ConfigureAwait(false);
            if (raw?.WordLrc != null)
                withWords = ParseLyric(raw.Lrc, raw.TranslationLrc, raw.WordLrc);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Log.Error($"yrc refresh failed: {t.Title}", ex);
            return;
        }

        if (ct.IsCancellationRequested || withWords == null || raw == null)
            return;

        var hasWords = withWords.Lines.Any(l => l.Words is { Count: > 0 });
        if (!hasWords)
            return;

        try { await _cache.SetAsync(key, CachedLyric.Positive(provider.Id, songId, raw)).ConfigureAwait(false); }
        catch (Exception ex) { Log.Error("cache update (yrc)", ex); }

        Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested || !ReferenceEquals(Track, t) || _hasWords)
                return;
            Log.Info($"yrc refreshed in background: {t.Title}");
            _doc = withWords;
            _hasWords = true;
            Tick(forceRaise: true);
            Raise();
        });
    }

    private static async Task<RawLyric?> SafeGetLyricAsync(ILyricsProvider provider, string songId, CancellationToken ct)
    {
        try
        {
            return await provider.GetLyricAsync(songId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"get lyric failed ({provider.Id}/{songId})", ex);
            return null;
        }
    }

    /// <summary>按设置的歌词源优先级顺序返回启用的 Provider。</summary>
    private List<ILyricsProvider> EnabledProviders()
    {
        var s = _settings.Current;
        var list = new List<ILyricsProvider>(s.LyricSources.Count);
        foreach (var r in s.LyricSources)
        {
            if (!r.Enabled)
                continue;
            var p = _providers.FirstOrDefault(x => x.Id == r.SourceId);
            if (p != null)
                list.Add(p);
        }
        return list;
    }

    /// <summary>逐字模式：设置开启且当前歌词含逐字数据。</summary>
    private bool WordMode => _settings.Current.WordByWord && _hasWords;

    private void UpdateTimer()
    {
        var shouldRun = Phase == LyricsPhase.Ready && _clock.IsPlaying;
        if (shouldRun)
        {
            _timer.Interval = WordMode ? TimeSpan.FromMilliseconds(16) : TimeSpan.FromMilliseconds(100);
            if (!_timer.IsEnabled)
                _timer.Start();
        }
        else if (_timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private void Tick(bool forceRaise, bool suppressTransition = false)
    {
        if (_doc == null)
            return;

        var pos = (long)_clock.Estimate().TotalMilliseconds + _settings.Current.GlobalOffsetMs + _songOffsetMs;
        var idx = LrcParser.FindIndex(_doc.Lines, pos);

        // 纯音乐：时间超过最后一行元信息 5 秒 → 结束显示（切回标题）
        var ended = _doc.IsInstrumental && _doc.Lines.Count > 0
                    && pos > _doc.Lines[^1].TimeMs + 5000;

        var timingFrame = WordMode && idx >= 0
            ? LyricsTimelineBuilder.Evaluate(_doc.Lines[idx], pos)
            : LyricTimingFrame.None;

        var lineChanged = idx != _lineIndex;
        var suppress = suppressTransition || _suppressLineTransition;
        _suppressLineTransition = false;

        if (!lineChanged && ended == _instrumentalEnded
            && timingFrame.IsVisuallyEquivalentTo(_timingFrame) && !forceRaise)
            return;

        _lineIndex = idx;
        _instrumentalEnded = ended;
        _timingFrame = timingFrame;
        CurrentMain = ResolveMainText(idx);
        CurrentTrans = idx >= 0 ? _doc.Lines[idx].Translation ?? "" : "";
        Raise();

        if (lineChanged)
            LineChanged?.Invoke(suppress);
    }

    /// <summary>伴奏/间奏/前奏一律显示 "···"，始终保持有内容。</summary>
    private string ResolveMainText(int idx)
    {
        if (idx < 0)
            return "\u00B7\u00B7\u00B7"; // 前奏
        var text = _doc!.Lines[idx].Text;
        return text.Length > 0 ? text : "\u00B7\u00B7\u00B7";
    }

    private void Raise() => StateChanged?.Invoke();
}

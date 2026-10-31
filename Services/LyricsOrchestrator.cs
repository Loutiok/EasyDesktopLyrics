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

public sealed partial class LyricsOrchestrator
{
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromDays(3);

    [GeneratedRegex("纯音乐|伴奏|演奏曲|instrumental|no lyrics|music only", RegexOptions.IgnoreCase)]
    private static partial Regex InstrumentalPattern();

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
    private int _trackEpoch;
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
    public bool IsInstrumental => _doc?.IsInstrumental == true;
    public bool HasWordData => _hasWords;
    public bool InstrumentalEnded => _instrumentalEnded;
    public int CurrentWordDone => _timingFrame.CompletedCharacters;
    public double CurrentWordFraction => _timingFrame.HighlightFraction;
    public LyricTimingFrame CurrentTimingFrame => _timingFrame;

    public event Action? StateChanged;
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
        _ = ResolveAndApplyAsync(t, ++_trackEpoch, force: true, cts.Token);
    }

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
        var currentEpoch = ++_trackEpoch;
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
        _clock.Reset(keepPlaying: false);

        if (t == null)
        {
            Phase = LyricsPhase.NoSession;
            UpdateTimer();
            Raise();
            return;
        }

        Phase = LyricsPhase.Resolving;
        UpdateTimer();
        Raise();
        LineChanged?.Invoke(true);

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = ResolveAndApplyAsync(t, currentEpoch, force: false, cts.Token);
    }

    private void OnPlaybackChanged(PlaybackSnapshot snapshot)
    {
        // 如果当前歌词根本还没就绪（还在占位符阶段），绝对不要让外部旧位置来污染时钟！
        if (Phase != LyricsPhase.Ready)
        {
            // 只记录播放/暂停状态，位置等歌词下好、真正就绪了再说
            return;
        }
    
        // 只有歌词真正就绪、曲目进入正常播放后，才采纳正常时钟
        _clock.Sync(snapshot);
        UpdateTimer();
        Tick(forceRaise: false);
        Raise();
    }

    private async Task ResolveAndApplyAsync(TrackInfo t, int epoch, bool force, CancellationToken ct)
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
            // 如果在这段解析期间用户又切歌了（epoch 已经过时），或者实例不一致，直接丢弃
            if (ct.IsCancellationRequested || epoch != _trackEpoch || !ReferenceEquals(Track, t))
                return;

            _doc = doc;
            _hasWords = doc?.Lines.Any(l => l.Words is { Count: > 0 }) == true;
            _songOffsetMs = songOffset;
            _lineIndex = -1;
            _timingFrame = LyricTimingFrame.None;
            CurrentMain = "";
            CurrentTrans = "";

            Phase = doc != null ? LyricsPhase.Ready : LyricsPhase.NoLyric;
            Log.Info($"lyric {(doc != null ? $"ready ({doc.Lines.Count} lines, words={_hasWords})" : "not found")}: {t.Title}");
            
            UpdateTimer();
            _suppressLineTransition = true;
            Tick(forceRaise: true, suppressTransition: true);
            Raise();
        });
    }

    private async Task<(LyricDocument? Doc, int SongOffsetMs)> ResolveCoreAsync(TrackInfo t, bool force, CancellationToken ct)
    {
        var key = LyricsMatcher.TrackKey(t);
        var ov = _overrides.Get(key);
        var songOffset = ov?.OffsetMs ?? 0;

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
                            Log.Info($"cache hit: {key} [{cached.Source}] words={!string.IsNullOrEmpty(cached.WordLrc)}");
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
        }

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

            if (i == 0 && candidates.Any(c => c.Score >= LyricsMatcher.EarlyAcceptThreshold))
                break;
        }

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

    private static LyricDocument? ParseLyric(string? lrc, string? transLrc, string? wordLrc)
    {
        var doc = LrcParser.Parse(lrc, transLrc);
        if (doc == null)
            return null;

        if (!string.IsNullOrWhiteSpace(wordLrc))
        {
            doc = MergeWordLines(doc, LrcParser.ParseWordLines(wordLrc));
        }
        else if (!string.IsNullOrWhiteSpace(lrc))
        {
            var localWordLines = LrcParser.ParseWordLines(lrc);
            if (localWordLines.Count > 0)
            {
                doc = MergeWordLines(doc, localWordLines);
            }
        }

        var texts = doc.Lines.Select(l => l.Text).Where(t => t.Length > 0).ToList();
        if (texts.Count == 0)
            return null;

        var content = texts.Where(t => !MetaInfoPattern().IsMatch(t)).ToList();
        if (content.Count > 0 && !content.All(t => InstrumentalPattern().IsMatch(t)))
            return LyricsTimelineBuilder.AssignCharacterTimings(doc);

        var meta = doc.Lines
            .Where(l => l.Text.Length > 0 && MetaInfoPattern().IsMatch(l.Text))
            .ToList();
        return meta.Count > 0
            ? LyricsTimelineBuilder.AssignCharacterTimings(new LyricDocument(meta, isInstrumental: true))
            : null;
    }

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
        if (_doc == null || Phase != LyricsPhase.Ready)
            return;

        var pos = (long)_clock.Estimate().TotalMilliseconds + _settings.Current.GlobalOffsetMs + _songOffsetMs;

        // 如果旧歌原本处于后半段，位置却瞬间跌回开头，判定为切歌残留包，直接丢弃
        if (_lineIndex > 2 && pos < 2000)
            return;

        var idx = LrcParser.FindIndex(_doc.Lines, pos);

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

    private string ResolveMainText(int idx)
    {
        if (idx < 0)
            return "\u00B7\u00B7\u00B7";
        var text = _doc!.Lines[idx].Text;
        return text.Length > 0 ? text : "\u00B7\u00B7\u00B7";
    }

    private void Raise() => StateChanged?.Invoke();
}

using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EasyDesktopLyrics.Models;
using EasyDesktopLyrics.Services;

namespace EasyDesktopLyrics.ViewModels;

/// <summary>
/// 悬浮窗只读视图模型：组合 设置 + 协调器状态 → 文本/样式/可见性。
/// 所有属性为计算属性，来源变化时整体广播。
/// </summary>
public sealed class OverlayViewModel : ObservableObject
{
    private static readonly string[] AllProps =
    [
        nameof(MainText), nameof(TransText), nameof(ShowTransLine),
        nameof(FontFamilyValue), nameof(MainFontSize), nameof(EffectiveTransFontSize), nameof(WeightValue),
        nameof(Fill), nameof(InactiveFill), nameof(ActiveLayerInactiveFill), nameof(TextOpacity), nameof(MaxTextWidth), nameof(TextEffect),
        nameof(GlowEffect), nameof(GlowOuterEffect), nameof(InactiveGlowOuterEffect),
        nameof(WindowVisible), nameof(IsPlaying), nameof(Phase),
        nameof(StrokeEnabled), nameof(StrokeBrush), nameof(StrokeThickness), nameof(LineSpacing),
        nameof(TextAlignment), nameof(GlowEnabled),
        nameof(InactiveStrokeEnabled), nameof(InactiveStrokeBrush), nameof(InactiveStrokeThickness), nameof(InactiveGlowEffect),
        nameof(LyricVisualMode), nameof(ActiveCharacterIndex), nameof(ReleasedCharacterIndex),
        nameof(LuminousGlowBrush), nameof(LuminousCoreGlowEffect), nameof(LuminousOuterGlowEffect),
        nameof(LuminousCoreGlowOpacity), nameof(LuminousOuterGlowOpacity), nameof(LuminousAfterglowOpacity),
        nameof(CoverImage), nameof(CoverEnabled), nameof(CoverCutAnimation), nameof(CoverSizePct),
        nameof(WordHighlightLength), nameof(WordHighlightFraction),
        nameof(CoverTitle), nameof(CoverArtist),
        nameof(CoverTitleEnabled), nameof(CoverTitleShowArtist), nameof(CoverTitleAnimDirection),
        nameof(CoverTitleFontFamily), nameof(CoverTitleColor), nameof(CoverTitleFontSize),
        nameof(CoverTitleOpacity), nameof(CoverTitleStrokeEnabled), nameof(CoverTitleStrokeBrush),
        nameof(CoverTitleStrokeThickness), nameof(CoverTitleShadowEffect),
        nameof(CoverArtistFontFamily), nameof(CoverArtistColor), nameof(CoverArtistFontSize),
        nameof(CoverArtistOpacity), nameof(CoverArtistStrokeEnabled), nameof(CoverArtistStrokeBrush),
        nameof(CoverArtistStrokeThickness), nameof(CoverArtistShadowEffect),
    ];

    private static readonly IBrush TransparentBrush = new SolidColorBrush(Colors.Transparent);

    private readonly SettingsService _settings;
    private readonly LyricsOrchestrator _orchestrator;
    private readonly SmtcService _smtc;

    private string _fillHexCache = "";
    private IBrush _fillCache = Brushes.White;
    private string _inactiveHexCache = "";
    private IBrush _inactiveFillCache = Brushes.White;
    private string _inactiveStrokeHexCache = "";
    private IBrush _inactiveStrokeBrushCache = Brushes.Black;
    private string _strokeHexCache = "";
    private IBrush _strokeBrushCache = Brushes.Black;
    private string _shadowKeyCache = "";
    private IEffect? _shadowCache;
    private string _glowKeyCache = "";
    private IEffect? _glowCache;
    private IEffect? _glowOuterCache;
    private string _inactiveGlowKeyCache = "";
    private IEffect? _inactiveGlowCache;
    private IEffect? _inactiveGlowOuterCache;
    private string _luminousGlowKeyCache = "";
    private IBrush _luminousGlowBrushCache = Brushes.White;
    private IEffect? _luminousCoreGlowCache;
    private IEffect? _luminousOuterGlowCache;

    public OverlayViewModel(SettingsService settings, LyricsOrchestrator orchestrator, SmtcService smtc)
    {
        _settings = settings;
        _orchestrator = orchestrator;
        _smtc = smtc;
        _settings.Changed += () => RaiseMany(AllProps);
        _orchestrator.StateChanged += () =>
        {
            RefreshCover();
            RefreshCoverTrackKey();
            RaiseMany(AllProps);
        };
        _orchestrator.LineChanged += suppress => LineChanged?.Invoke(suppress);

        PlayPauseCommand = new RelayCommand(() => _ = _smtc.TryTogglePlayPauseAsync());
        PrevCommand = new RelayCommand(() => _ = _smtc.TrySkipPreviousAsync());
        NextCommand = new RelayCommand(() => _ = _smtc.TrySkipNextAsync());
    }

    /// <summary>当前行切换（bool = 是否抑制行间动效，如切歌/seek）。UI 线程回调。</summary>
    public event Action<bool>? LineChanged;

    public RelayCommand PlayPauseCommand { get; }

    public RelayCommand PrevCommand { get; }

    public RelayCommand NextCommand { get; }

    public bool IsPlaying => _orchestrator.IsPlaying;

    /// <summary>歌词解析相位（切歌动画以此判定歌词加载完成）。</summary>
    public LyricsPhase Phase => _orchestrator.Phase;

    // ---------- 封面 ----------

    public bool CoverEnabled => S.CoverEnabled;

    public bool CoverCutAnimation => S.CoverCutAnimation;

    /// <summary>切歌封面最短显示时长（ms）。</summary>
    public int CoverAnimMinMs => Math.Clamp(S.CoverAnimMinMs, 300, 5000);

    /// <summary>切歌封面最长显示时长（ms）。</summary>
    public int CoverAnimMaxMs => Math.Clamp(S.CoverAnimMaxMs, 1000, 15000);

    /// <summary>封面占比：封面边长 = 歌词行高度 × 该百分比（40–120）。</summary>
    public double CoverSizePct => Math.Clamp(S.CoverSizePct, 40, 120);

    public IImage? CoverImage { get; private set; }

    /// <summary>当前曲目标识（Title|Artist）；仅真正切歌时变化。</summary>
    public string CoverTrackKey { get; private set; } = "";

    /// <summary>切歌动画歌名（单行文本）。</summary>
    public string CoverTitle { get; private set; } = "";

    /// <summary>切歌动画歌手名（歌名下方独立行）。</summary>
    public string CoverArtist { get; private set; } = "";

    private string _coverKey = "";
    private string _trackKeyCache = "";
    private bool _hadTrack;
    private bool _cutAnimationFlag;

    /// <summary>
    /// 消费切歌动画标记：任何来源（播放器内切歌 / 悬浮窗按钮）的曲目变化都会置位，
    /// 由悬浮窗消费后清空。
    /// </summary>
    public bool ConsumeCutAnimationFlag()
    {
        var v = _cutAnimationFlag;
        _cutAnimationFlag = false;
        return v;
    }

    /// <summary>曲目变化时解码封面（限制解码尺寸，UI 线程执行）。</summary>
    private void RefreshCover()
    {
        var track = _orchestrator.Track;
        var bytes = track?.Cover;
        var key = bytes == null ? "" : $"{track!.Title}|{track.Artist}|{bytes.Length}";
        if (key == _coverKey)
            return;
        _coverKey = key;

        IImage? image = null;
        if (bytes is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(bytes);
                image = Bitmap.DecodeToWidth(ms, 512);
            }
            catch
            {
                // 封面解码失败 → 无封面
            }
        }
        CoverImage = image;
    }

    /// <summary>真正的切歌（Title|Artist 变化）才更新 CoverTrackKey；并标记切歌动画。</summary>
    private void RefreshCoverTrackKey()
    {
        var t = _orchestrator.Track;
        var key = t == null ? "" : $"{t.Title}|{t.Artist}";
        if (key == _trackKeyCache)
            return;
        _trackKeyCache = key;
        // 切歌动画：任何来源的曲目变化都播；首次获得曲目（应用启动/绑定新会话）不播
        _cutAnimationFlag = t != null && _hadTrack;
        if (t != null)
            _hadTrack = true;
        CoverTrackKey = key;
        CoverTitle = t?.Title ?? "";
        CoverArtist = t?.Artist ?? "";
        Raise(nameof(CoverTrackKey));
        Raise(nameof(CoverTitle));
        Raise(nameof(CoverArtist));
    }

    // ---------- 切歌封面歌名样式（只读，供悬浮窗绑定） ----------

    public bool CoverTitleEnabled => S.CoverTitleEnabled;

    public bool CoverTitleShowArtist => S.CoverTitleShowArtist;

    /// <summary>歌名淡入淡出方向：0=无位移，1=上，2=下，3=左，4=右。</summary>
    public int CoverTitleAnimDirection => Math.Clamp(S.CoverTitleAnimDirection, 0, 4);

    public IEffect? CoverTitleShadowEffect => BuildShadow(
        S.CoverTitleShadowEnabled, S.CoverTitleShadowColorHex,
        S.CoverTitleShadowBlurRadius, S.CoverTitleShadowOffsetY, ref _coverTitleShadowCache, ref _coverTitleShadowKey);

    public IEffect? CoverArtistShadowEffect => BuildShadow(
        S.CoverArtistShadowEnabled, S.CoverArtistShadowColorHex,
        S.CoverArtistShadowBlurRadius, S.CoverArtistShadowOffsetY, ref _coverArtistShadowCache, ref _coverArtistShadowKey);

    private IEffect? _coverTitleShadowCache;
    private string _coverTitleShadowKey = "";
    private IEffect? _coverArtistShadowCache;
    private string _coverArtistShadowKey = "";

    private static IEffect? BuildShadow(
        bool enabled, string colorHex, double blur, double offsetY,
        ref IEffect? cache, ref string cacheKey)
    {
        if (!enabled)
            return null;
        var key = $"{colorHex}|{blur}|{offsetY}";
        if (cacheKey != key)
        {
            cacheKey = key;
            cache = new DropShadowEffect
            {
                OffsetX = 0,
                OffsetY = offsetY,
                BlurRadius = Math.Clamp(blur, 1, 60),
                Opacity = 0.9,
                Color = Color.TryParse(colorHex, out var c) ? c : Colors.Black,
            };
        }
        return cache;
    }

    public FontFamily CoverTitleFontFamily =>
        ParseFont(S.CoverTitleFont) ?? FontFamilyValue;

    public FontFamily CoverArtistFontFamily =>
        ParseFont(S.CoverArtistFont) ?? FontFamilyValue;

    public IBrush CoverTitleColor => ParseColor(S.CoverTitleColorHex, Colors.White);

    public IBrush CoverArtistColor => ParseColor(S.CoverArtistColorHex, Colors.White);

    public double CoverTitleFontSize => Math.Clamp(S.CoverTitleFontSize, 8, 120);

    public double CoverArtistFontSize => Math.Clamp(S.CoverArtistFontSize, 8, 120);

    public double CoverTitleOpacity => Math.Clamp(S.CoverTitleOpacity, 0.05, 1);

    public double CoverArtistOpacity => Math.Clamp(S.CoverArtistOpacity, 0.05, 1);

    public bool CoverTitleStrokeEnabled => S.CoverTitleStrokeEnabled;

    public bool CoverArtistStrokeEnabled => S.CoverArtistStrokeEnabled;

    public IBrush CoverTitleStrokeBrush => ParseColor(S.CoverTitleStrokeColorHex, Colors.Black);

    public IBrush CoverArtistStrokeBrush => ParseColor(S.CoverArtistStrokeColorHex, Colors.Black);

    public double CoverTitleStrokeThickness => Math.Clamp(S.CoverTitleStrokeThickness, 0.1, 4);

    public double CoverArtistStrokeThickness => Math.Clamp(S.CoverArtistStrokeThickness, 0.1, 4);

    private static FontFamily? ParseFont(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        try
        {
            return new FontFamily(name);
        }
        catch
        {
            return null;
        }
    }

    private static IBrush ParseColor(string hex, Color fallback) =>
        new SolidColorBrush(Color.TryParse(hex, out var c) ? c : fallback);

    private AppSettings S => _settings.Current;

    public string MainText
    {
        get
        {
            string text;
            if (_orchestrator.Phase == LyricsPhase.Ready)
            {
                // 纯音乐：作词/作曲元信息显示完后切回标题
                text = _orchestrator.IsInstrumental && _orchestrator.InstrumentalEnded
                    ? FallbackText()
                    : _orchestrator.CurrentMain;
            }
            else
            {
                text = FallbackText();
            }
            return text;
        }
    }

    public string TransText => _orchestrator.Phase == LyricsPhase.Ready ? _orchestrator.CurrentTrans : "";

    public bool ShowTransLine => S.ShowTranslation && TransText.Length > 0;

    public FontFamily FontFamilyValue
    {
        get
        {
            try
            {
                return new FontFamily(S.FontFamily);
            }
            catch
            {
                return FontFamily.Default;
            }
        }
    }

    public double MainFontSize => Math.Clamp(S.FontSize, 10, 200);

    public double EffectiveTransFontSize
    {
        get
        {
            var ts = S.TransFontSize > 0 ? S.TransFontSize : Math.Round(MainFontSize * 0.6);
            return Math.Max(10, ts);
        }
    }

    public double LineSpacing => Math.Clamp(S.LineSpacing, 0, 50);

    public TextAlignment TextAlignment
    {
        get
        {
            try { return (TextAlignment)Enum.Parse(typeof(TextAlignment), S.Alignment); }
            catch { return Avalonia.Media.TextAlignment.Center; }
        }
    }

    public FontWeight WeightValue => (FontWeight)Math.Clamp(S.FontWeight, 100, 950);

    public IBrush Fill
    {
        get
        {
            var topHex = S.GradientTopColorHex;
            var bottomHex = S.GradientBottomColorHex;
            var key = $"{topHex}|{bottomHex}";
            if (_fillHexCache != key)
            {
                _fillHexCache = key;
                _fillCache = BuildGradient(topHex, bottomHex);
            }
            return _fillCache;
        }
    }

    /// <summary>逐字未唱段颜色：独立设置时用指定色；否则主色。最终 alpha = 颜色 alpha × 未唱不透明度比例。</summary>
    public IBrush InactiveFill
    {
        get
        {
            var pct = Math.Clamp(S.InactiveOpacity, 0.05, 1.0);
            var key = $"{S.InactiveGradientTopColorHex}|{S.InactiveGradientBottomColorHex}|{pct}";
            if (_inactiveHexCache != key)
            {
                _inactiveHexCache = key;
                var top = Color.TryParse(S.InactiveGradientTopColorHex, out var c1) ? c1 : Colors.White;
                var bottom = Color.TryParse(S.InactiveGradientBottomColorHex, out var c2) ? c2 : Colors.White;
                // 未唱段统一乘上透明度
                _inactiveFillCache = BuildGradient(ApplyOpacity(top, pct), ApplyOpacity(bottom, pct));
            }
            return _inactiveFillCache;
        }
    }

    private static IBrush BuildGradient(string topHex, string bottomHex)
    {
        var top = Color.TryParse(topHex, out var t) ? t : Colors.White;
        var bottom = Color.TryParse(bottomHex, out var b) ? b : Colors.White;
        return BuildGradient(top, bottom);
    }
    
    private static IBrush BuildGradient(Color top, Color bottom)
    {
        return new LinearGradientBrush
        {
            // (0.5, 0) 到 (0.5, 1) 表示从上到下的垂直渐变
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(top, 0),
                new GradientStop(bottom, 1)
            }
        };
    }
    
    private static Color ApplyOpacity(Color c, double pct) =>
        Color.FromArgb((byte)Math.Round(c.A * pct), c.R, c.G, c.B);

    /// <summary>已唱层未唱段填充：完全透明（只让已唱段字形上色，未唱段由下层负责）。</summary>
    public IBrush ActiveLayerInactiveFill => TransparentBrush;

    /// <summary>未唱段描边开关（默认关：未唱段不描边，逐字明暗对比不被抹平）。</summary>
    public bool InactiveStrokeEnabled => S.InactiveStrokeEnabled;

    /// <summary>未唱段描边宽度（独立于已唱段）。</summary>
    public double InactiveStrokeThickness => Math.Clamp(S.InactiveStrokeThickness, 0.1, 4);

    public bool InactiveGlowEnabled => S.InactiveGlowEnabled;

    /// <summary>未唱段描边画笔（已唱段描边沿用 StrokeBrush）。</summary>
    public IBrush InactiveStrokeBrush
    {
        get
        {
            if (_inactiveStrokeHexCache != S.InactiveStrokeColorHex)
            {
                _inactiveStrokeHexCache = S.InactiveStrokeColorHex;
                _inactiveStrokeBrushCache = Color.TryParse(S.InactiveStrokeColorHex, out var c)
                    ? new SolidColorBrush(c)
                    : Brushes.Black;
            }
            return _inactiveStrokeBrushCache;
        }
    }

    /// <summary>未唱段辉光：独立于已唱段辉光（颜色/强度均可独立设置，默认关）。</summary>
    public IEffect? InactiveGlowEffect
    {
        get
        {
            if (!S.InactiveGlowEnabled)
                return null;
            var key = $"{S.InactiveGlowColorHex}|{S.InactiveGlowRadius}";
            if (_inactiveGlowKeyCache != key)
            {
                _inactiveGlowKeyCache = key;
                var color = Color.TryParse(S.InactiveGlowColorHex, out var c) ? c : Colors.White;
                _inactiveGlowCache = new DropShadowEffect
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    BlurRadius = Math.Clamp(S.InactiveGlowRadius * 0.75, 3, 30),
                    Opacity = 1,
                    Color = color,
                };
                _inactiveGlowOuterCache = new DropShadowEffect
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    BlurRadius = Math.Clamp(S.InactiveGlowRadius * 1.8, 10, 64),
                    Opacity = 0.72,
                    Color = color,
                };
            }
            return _inactiveGlowCache;
        }
    }

    /// <summary>当前行已唱字符数；-1 = 非逐字模式（整行高亮）。</summary>
    public int WordHighlightLength => _orchestrator.CurrentWordDone;

    /// <summary>正在唱字符的平滑渐变进度 0~1（与 WordHighlightLength 配合实现字符内点亮动画）。</summary>
    public double WordHighlightFraction => _orchestrator.CurrentWordFraction;

    public string LyricVisualMode => S.LyricVisualMode;

    public int ActiveCharacterIndex => _orchestrator.CurrentTimingFrame.ActiveCharacterIndex;

    public int ReleasedCharacterIndex => _orchestrator.CurrentTimingFrame.ReleasedCharacterIndex;

    public IBrush LuminousGlowBrush
    {
        get
        {
            EnsureLuminousGlowResources();
            return _luminousGlowBrushCache;
        }
    }

    public IEffect? InactiveGlowOuterEffect
    {
        get
        {
            _ = InactiveGlowEffect;
            return _inactiveGlowOuterCache;
        }
    }

    public IEffect? LuminousCoreGlowEffect
    {
        get
        {
            EnsureLuminousGlowResources();
            return _luminousCoreGlowCache;
        }
    }

    public IEffect? LuminousOuterGlowEffect
    {
        get
        {
            EnsureLuminousGlowResources();
            return _luminousOuterGlowCache;
        }
    }

    public double LuminousCoreGlowOpacity => LuminousActiveEnvelope();

    public double LuminousOuterGlowOpacity => LuminousActiveEnvelope() * 0.9;

    public double LuminousAfterglowOpacity
    {
        get
        {
            var frame = _orchestrator.CurrentTimingFrame;
            if (frame.ReleasedCharacterIndex < 0)
                return 0;

            var releaseMs = Math.Clamp(S.LuminousReleaseMs, 120, 600);
            if (frame.ReleasedElapsedMs >= releaseMs)
                return 0;

            var release = 1 - SmoothStep(frame.ReleasedElapsedMs / (double)releaseMs);
            var longNote = LongNoteFactor(frame.ReleasedCharacterDurationMs);
            var strength = Math.Clamp(S.LuminousGlowStrength, 0.2, 2.0);
            return Math.Clamp(TextOpacity * strength * release * (0.48 + longNote * 0.52), 0, 1);
        }
    }

    private double LuminousActiveEnvelope()
    {
        var frame = _orchestrator.CurrentTimingFrame;
        if (frame.ActiveCharacterIndex < 0 || frame.ActiveCharacterDurationMs <= 0)
            return 0;

        var duration = frame.ActiveCharacterDurationMs;
        var elapsed = Math.Clamp(frame.HighlightFraction, 0, 1) * duration;
        var attack = SmoothStep(elapsed / 120.0);
        var releaseWindow = Math.Clamp(duration * 0.22, 70, 180);
        var release = SmoothStep((duration - elapsed) / releaseWindow);
        var longNote = LongNoteFactor(duration);
        var pulse = 1 + Math.Sin(elapsed * Math.PI * 2 / 900.0) * longNote * 0.12;
        var strength = Math.Clamp(S.LuminousGlowStrength, 0.2, 2.0);
        return Math.Clamp(TextOpacity * strength * attack * release * (0.68 + longNote * 0.52) * pulse, 0, 1);
    }

    private double LongNoteFactor(long durationMs)
    {
        var threshold = Math.Clamp(S.LuminousLongNoteThresholdMs, 300, 2000);
        return SmoothStep((durationMs - threshold) / (double)Math.Max(threshold * 2, 1));
    }

    private static double SmoothStep(double value)
    {
        var x = Math.Clamp(value, 0, 1);
        return x * x * (3 - 2 * x);
    }

    private void EnsureLuminousGlowResources()
    {
        var coreRadius = Math.Clamp(S.FontSize * 0.38, 10, 24);
        var outerRadius = Math.Clamp(S.FontSize * 1.05, 28, 58);
        var key = $"{S.LuminousGlowColorHex}|{coreRadius:F1}|{outerRadius:F1}";
        if (_luminousGlowKeyCache == key)
            return;

        _luminousGlowKeyCache = key;
        var color = Color.TryParse(S.LuminousGlowColorHex, out var parsed) ? parsed : Colors.White;
        _luminousGlowBrushCache = new SolidColorBrush(color);
        _luminousCoreGlowCache = new DropShadowEffect
        {
            OffsetX = 0,
            OffsetY = 0,
            BlurRadius = coreRadius,
            Opacity = 1,
            Color = color,
        };
        _luminousOuterGlowCache = new DropShadowEffect
        {
            OffsetX = 0,
            OffsetY = 0,
            BlurRadius = outerRadius,
            Opacity = 0.88,
            Color = color,
        };
    }

    public double TextOpacity => Math.Clamp(S.Opacity, 0.05, 1.0);

    public double MaxTextWidth => Math.Clamp(S.MaxWidth, 200, 4000);

    /// <summary>投影：颜色/模糊半径/垂直偏移均可配置。</summary>
    public IEffect? TextEffect
    {
        get
        {
            if (!S.ShadowEnabled)
                return null;
            var key = $"{S.ShadowColorHex}|{S.ShadowBlurRadius}|{S.ShadowOffsetY}";
            if (_shadowKeyCache != key)
            {
                _shadowKeyCache = key;
                var color = Color.TryParse(S.ShadowColorHex, out var c) ? c : Colors.Black;
                _shadowCache = new DropShadowEffect
                {
                    OffsetX = 0,
                    OffsetY = S.ShadowOffsetY,
                    BlurRadius = Math.Clamp(S.ShadowBlurRadius, 1, 60),
                    Opacity = 0.9,
                    Color = color,
                };
            }
            return _shadowCache;
        }
    }

    /// <summary>辉光：大模糊半径的彩色光晕（叠加在文字下层）。</summary>
    public IEffect? GlowEffect
    {
        get
        {
            if (!S.GlowEnabled)
                return null;
            var key = $"{S.GlowColorHex}|{S.GlowRadius}";
            if (_glowKeyCache != key)
            {
                _glowKeyCache = key;
                var color = Color.TryParse(S.GlowColorHex, out var c) ? c : Colors.White;
                _glowCache = new DropShadowEffect
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    BlurRadius = Math.Clamp(S.GlowRadius * 0.75, 3, 30),
                    Opacity = 1,
                    Color = color,
                };
                _glowOuterCache = new DropShadowEffect
                {
                    OffsetX = 0,
                    OffsetY = 0,
                    BlurRadius = Math.Clamp(S.GlowRadius * 1.8, 10, 64),
                    Opacity = 0.82,
                    Color = color,
                };
            }
            return _glowCache;
        }
    }

    public IEffect? GlowOuterEffect
    {
        get
        {
            _ = GlowEffect;
            return _glowOuterCache;
        }
    }

    public bool StrokeEnabled => S.StrokeEnabled;

    public bool GlowEnabled => S.GlowEnabled;

    public IBrush StrokeBrush
    {
        get
        {
            if (_strokeHexCache != S.StrokeColorHex)
            {
                _strokeHexCache = S.StrokeColorHex;
                _strokeBrushCache = Color.TryParse(S.StrokeColorHex, out var c) ? new SolidColorBrush(c) : Brushes.Black;
            }
            return _strokeBrushCache;
        }
    }

    public double StrokeThickness => Math.Clamp(S.StrokeThickness, 0.1, 4);

    public bool WindowVisible
    {
        get
        {
            if (!S.LyricsVisible)
                return false;
            if (_orchestrator.Phase == LyricsPhase.NoSession)
                return false;
            if (S.HideWhenPaused && !_orchestrator.IsPlaying)
                return false;
            if (MainText.Length == 0 && !ShowTransLine)
                return false;
            return true;
        }
    }

    private string FallbackText()
    {
        var t = _orchestrator.Track;
        if (t == null || !S.ShowTitleWhenNoLyric)
            return "";
        return string.IsNullOrEmpty(t.Artist) ? t.Title : $"{t.Artist} - {t.Title}";
    }
}

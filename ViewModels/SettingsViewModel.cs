using Avalonia;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using EasyDesktopLyrics.Infrastructure;
using EasyDesktopLyrics.Models;
using EasyDesktopLyrics.Services;
using EasyDesktopLyrics.Services.Providers;

namespace EasyDesktopLyrics.ViewModels;

/// <summary>设置导航项。</summary>
public sealed record NavOption(string Icon, string Label);

/// <summary>播放器优先级列表项。</summary>
public sealed class PlayerRuleItem : ObservableObject
{
    private bool _isEnabled;

    public PlayerRuleItem(string aumid, string displayName, bool enabled)
    {
        Aumid = aumid;
        DisplayName = displayName;
        _isEnabled = enabled;
    }

    public string Aumid { get; }
    public string DisplayName { get; set; }
    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }
}

/// <summary>歌词源优先级列表项。</summary>
public sealed class LyricSourceItem : ObservableObject
{
    private bool _isEnabled;

    public LyricSourceItem(string sourceId, string displayName, bool enabled)
    {
        SourceId = sourceId;
        DisplayName = displayName;
        _isEnabled = enabled;
    }

    public string SourceId { get; }
    public string DisplayName { get; set; }
    public bool IsEnabled
    {
        get => _isEnabled;
        set => Set(ref _isEnabled, value);
    }
}

/// <summary>手动搜索结果条目。</summary>
public sealed record SearchResultItem(string ProviderId, string ProviderName, string SongId, string Title, string Artist, long DurationMs, double Score)
{
    public override string ToString()
    {
        var dur = TimeSpan.FromMilliseconds(Math.Max(0, DurationMs));
        var scorePart = Score > 0 ? $"　匹配度 {Score * 100:F0}%" : "";
        return $"[{ProviderName}] {Title} — {Artist}　({(int)dur.TotalMinutes}:{dur.Seconds:D2}){scorePart}";
    }
}

/// <summary>位置预设九宫格条目。</summary>
public sealed record PresetOption(PositionPreset Preset, string Label, int Col, int Row);

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private static readonly string[] SettingProps =
    [
        nameof(SelectedFont), nameof(FontSize), nameof(WeightIndex), nameof(ColorHex),
        nameof(ColorValue), nameof(StrokeColorValue), nameof(GlowColorValue), nameof(ShadowColorValue),
        nameof(ShadowEnabled), nameof(StrokeEnabled), nameof(StrokeColorHex), nameof(StrokeThicknessVal),
        nameof(GlowEnabled), nameof(GlowRadiusVal),
        nameof(InactiveColorIndependent), nameof(InactiveColorValue), nameof(InactiveOpacityVal),
        nameof(InactiveStrokeEnabled), nameof(InactiveStrokeColorValue), nameof(InactiveStrokeThicknessVal),
        nameof(InactiveGlowEnabled), nameof(InactiveGlowColorValue), nameof(InactiveGlowRadiusVal),
        nameof(ActiveVisualPresetId), nameof(ActiveVisualPresetName),
        nameof(IsClassicVisualPreset), nameof(IsLuminousVisualPreset), nameof(IsImmersiveVisualPreset),
        nameof(VisualGlowStrengthVal), nameof(VisualAnimationAmplitudeVal),
        nameof(VisualBackgroundOpacityVal), nameof(VisualPerformanceLevelIndex),
        nameof(LyricVisualModeIndex), nameof(ShowLuminousOptions), nameof(LuminousGlowColorValue),
        nameof(LuminousGlowStrengthVal), nameof(LuminousLongNoteThresholdVal), nameof(LuminousReleaseVal),
        nameof(ShadowBlurVal), nameof(ShadowOffsetYVal),
        nameof(TransFontSizeVal), nameof(LineSpacingVal), nameof(TextOpacity), nameof(MaxWidth),
        nameof(ShowTranslation), nameof(HideWhenPaused), nameof(ShowTitleWhenNoLyric), nameof(AutoStartEnabled), nameof(IsLocked), nameof(LogLevelIndex),
        nameof(LineTransitionEnabled), nameof(LineTransitionTypeIndex), nameof(LineTransitionSpeedVal),
        nameof(LineTransitionEasingIndex), nameof(LineTransitionDirectionIndex), nameof(LineTransitionDistanceVal),
        nameof(LineTransitionScaleVal), nameof(ShowSlideOptions), nameof(ShowScaleOptions),
        nameof(LineTransitionScaleExitGrow), nameof(LineTransitionSlideBlurEnabled), nameof(LineTransitionSlideBlurRadiusVal),
        nameof(LyricsFolder), nameof(GlobalOffsetMs),
        nameof(CoverEnabled), nameof(CoverCutAnimation), nameof(CoverSizePctVal),
        nameof(CoverAnimMinSec), nameof(CoverAnimMaxSec),
        nameof(CoverAnimEasingIndex), nameof(CoverAnimDurationVal),
        nameof(CoverAnimDirectionIndex), nameof(CoverAnimSlideDistanceVal),
        nameof(CoverTitleEnabled), nameof(CoverTitleShowArtist), nameof(CoverTitleAnimDirectionIndex),
        nameof(CoverTitleSlideDistanceVal),
        nameof(CoverTitleFont), nameof(CoverTitleColorValue), nameof(CoverTitleFontSizeVal),
        nameof(CoverTitleOpacityVal), nameof(CoverTitleStrokeEnabled),
        nameof(CoverTitleStrokeColorValue), nameof(CoverTitleStrokeThicknessVal),
        nameof(CoverTitleShadowEnabled), nameof(CoverTitleShadowColorValue),
        nameof(CoverTitleShadowBlurVal), nameof(CoverTitleShadowOffsetYVal),
        nameof(CoverArtistFont), nameof(CoverArtistColorValue), nameof(CoverArtistFontSizeVal),
        nameof(CoverArtistOpacityVal), nameof(CoverArtistStrokeEnabled),
        nameof(CoverArtistStrokeColorValue), nameof(CoverArtistStrokeThicknessVal),
        nameof(CoverArtistShadowEnabled), nameof(CoverArtistShadowColorValue),
        nameof(CoverArtistShadowBlurVal), nameof(CoverArtistShadowOffsetYVal),
        nameof(SelectedPresetIndex), nameof(PositionXPct), nameof(PositionYPct), nameof(AlignmentIndex), nameof(SelectedFontFamily),
        nameof(HeightGrowIndex),
        nameof(BgFps),
        nameof(AuroraEnabled), nameof(AuroraOverallOpacityVal), nameof(AuroraOpacityVal), nameof(AuroraCoverOpacityVal),
        nameof(AuroraBlurVal), nameof(AuroraDimVal), nameof(AuroraFlowVal),
        nameof(AuroraAudioReactiveEnabled), nameof(AuroraAudioResponseVal),
        nameof(SpectrumEnabled), nameof(SpectrumPositionIndex), nameof(SpectrumStyleIndex),
        nameof(SpectrumIntensityVal), nameof(SpectrumBandCountVal), nameof(SpectrumWidthVal),
        nameof(SpectrumOpacityVal), nameof(SpectrumColorValue),
        nameof(SpectrumGlowEnabled), nameof(SpectrumGlowStrengthVal),
        nameof(SpectrumSmoothingVal), nameof(SpectrumCenterBassEnabled),
        nameof(SpectrumBreathingVal),
        nameof(SnowEnabled), nameof(SnowIntensityVal), nameof(SnowWidthVal), nameof(SnowOpacityVal),
        nameof(SnowSizeVal), nameof(SnowSpeedVal), nameof(SnowColorValue),
        nameof(FogEnabled), nameof(FogOpacityVal), nameof(FogSoftnessVal), nameof(FogFlowVal),
        nameof(FogUseCoverEnabled), nameof(FogUseBackdropEnabled), nameof(FogBlendVal), nameof(FogAnimatedEnabled),
        nameof(GradientTopColorValue), nameof(GradientBottomColorValue),
        nameof(InactiveGradientTopColorValue), nameof(InactiveGradientBottomColorValue),
    ];

    private readonly SettingsService _settings;
    private readonly SmtcService _smtc;
    private readonly LyricsOrchestrator _orchestrator;
    private readonly OverridesStore _overrides;
    private readonly LyricsCache _cache;
    private readonly IReadOnlyList<ILyricsProvider> _providers;

    private TrackInfo? _lastTrack;

    // 🌟 新增：UI 初始化保护锁
    private bool _isInitializing = true;

    public Action<PositionPreset>? SnapToPreset { get; set; }
    public Action<double, double>? SetAnchor { get; set; }
    public PixelRect ScreenArea { get; set; }

    public SettingsViewModel(
        SettingsService settings,
        SmtcService smtc,
        LyricsOrchestrator orchestrator,
        OverridesStore overrides,
        LyricsCache cache,
        IReadOnlyList<ILyricsProvider> providers)
    {
        _settings = settings;
        _smtc = smtc;
        _orchestrator = orchestrator;
        _overrides = overrides;
        _cache = cache;
        _providers = providers;

        FontOptions = LoadFontOptions();

        SearchCommand = new RelayCommand(() => _ = RunSearchAsync(), () => !_isBusy);
        ApplyResultCommand = new RelayCommand(ApplySelectedResult, () => _selectedResult != null && _orchestrator.Track != null);
        ClearOverrideCommand = new RelayCommand(ClearOverride, () => _orchestrator.Track != null);
        ApplySongOffsetCommand = new RelayCommand(ApplySongOffset, () => _orchestrator.Track != null);
        ClearCacheCommand = new RelayCommand(ClearCache);
        RefreshSessionsCommand = new RelayCommand(RefreshPlayerRules);
        ApplyVisualPresetCommand = new RelayCommand(ApplyVisualPreset);
        SaveUserVisualPresetCommand = new RelayCommand(SaveUserVisualPreset);
        ApplyUserVisualPresetCommand = new RelayCommand(ApplyUserVisualPreset);
        UpdateUserVisualPresetCommand = new RelayCommand(UpdateUserVisualPreset);
        DeleteUserVisualPresetCommand = new RelayCommand(DeleteUserVisualPreset);

        SnapPresetCommand = new RelayCommand(p =>
        {
            if (p is string s && Enum.TryParse<PositionPreset>(s, out var preset))
                SnapToPreset?.Invoke(preset);
        });

        _settings.Changed += OnSettingsChanged;
        _orchestrator.StateChanged += OnOrchestratorChanged;
        _smtc.SessionsChanged += RefreshPlayerRules;

        RefreshPlayerRules();
        RefreshLyricSources();
        SyncFromTrack(force: true);
        RefreshUserVisualPresets();

        // 🌟 延迟一帧，等 UI 控件全部完成首次绑定并赋值，再解开保护锁
        Dispatcher.UIThread.Post(() =>
        {
            _isInitializing = false;
        }, DispatcherPriority.Loaded);
    }

    // ---------- 导航 ----------
    public IReadOnlyList<NavOption> NavOptions { get; } =
    [
        new("\uE771", "视觉预设"),
        new("\uE768", "播放"),
        new("\uE790", "外观"),
        new("\uE762", "文字特效"),
        new("\uE7B5", "歌词封面"),
        new("\uE7C4", "显示"),
        new("\uE8FD", "校正"),
        new("\uE768", "背景动效"),
    ];

    public bool VisualPresetNavSelected => _selectedNav == 0;
    public bool PlayNavSelected => _selectedNav == 1;
    public bool AppearanceNavSelected => _selectedNav == 2;
    public bool EffectsNavSelected => _selectedNav == 3;
    public bool CoverNavSelected => _selectedNav == 4;
    public bool DisplayNavSelected => _selectedNav == 5;
    public bool FixNavSelected => _selectedNav == 6;
    public bool BackgroundFxNavSelected => _selectedNav == 7;

    private int _selectedNav;

    public int SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (Set(ref _selectedNav, value))
            {
                RaiseMany([nameof(VisualPresetNavSelected), nameof(PlayNavSelected), nameof(AppearanceNavSelected),
                           nameof(EffectsNavSelected), nameof(CoverNavSelected),
                           nameof(DisplayNavSelected), nameof(FixNavSelected),
                           nameof(BackgroundFxNavSelected)]);

                // 切换到“校正”页面时，自动搜索
                if (value == 6)
                {
                    var t = _orchestrator.Track;
                    if (t != null && string.IsNullOrWhiteSpace(SearchKeyword))
                    {
                        _searchKeyword = LyricsMatcher.BuildKeyword(t);
                        _keywordDirty = false;
                        Raise(nameof(SearchKeyword));
                    }
                    _ = RunSearchAsync();
                }
            }
        }
    }

    // ---------- 公用命令与数据 ----------
    public RelayCommand SnapPresetCommand { get; }

    public IReadOnlyList<PresetOption> PositionPresets { get; } =
    [
        new(PositionPreset.TopLeft,     "↖", 0, 0), new(PositionPreset.TopCenter,    "↑", 1, 0), new(PositionPreset.TopRight,    "↗", 2, 0),
        new(PositionPreset.MiddleLeft,  "←", 0, 1), new(PositionPreset.Center,       "⊙", 1, 1), new(PositionPreset.MiddleRight, "→", 2, 1),
        new(PositionPreset.BottomLeft,  "↙", 0, 2), new(PositionPreset.BottomCenter, "↓", 1, 2), new(PositionPreset.BottomRight, "↘", 2, 2),
    ];

    // ---------- 位置 ----------
    private bool _updatingPosition;

    public IReadOnlyList<string> PresetOptionLabels { get; } =
        ["左上", "中上", "右上",
         "左中", "居中", "右中",
         "左下", "中下", "右下",
         "自定义"];

    private static readonly PositionPreset[] PresetValues =
        [PositionPreset.TopLeft, PositionPreset.TopCenter, PositionPreset.TopRight,
         PositionPreset.MiddleLeft, PositionPreset.Center, PositionPreset.MiddleRight,
         PositionPreset.BottomLeft, PositionPreset.BottomCenter, PositionPreset.BottomRight];

    public int SelectedPresetIndex
    {
        get
        {
            var ax = _settings.Current.AnchorX;
            var ay = _settings.Current.AnchorY;
            var a = ScreenArea;
            if (!ax.HasValue || !ay.HasValue || a.Width <= 0) return 4; // default = center

            for (int i = 0; i < 9; i++)
            {
                var (ex, ey) = PresetToAnchor(PresetValues[i]);
                if (Math.Abs(ex - ax.Value) < 3 && Math.Abs(ey - ay.Value) < 3)
                    return i;
            }
            return 9; // 自定义
        }
        set
        {
            if (_updatingPosition || value < 0 || value > 9) return;
            _updatingPosition = true;
            if (value < 9)
            {
                var preset = PresetValues[value];
                var (ax, ay) = PresetToAnchor(preset);
                _settings.Update(s => { s.AnchorX = ax; s.AnchorY = ay; });
                SnapToPreset?.Invoke(preset);
            }
            Raise(nameof(PositionXPct));
            Raise(nameof(PositionYPct));
            _updatingPosition = false;
        }
    }

    public double PositionXPct
    {
        get
        {
            var a = ScreenArea;
            if (a.Width <= 0 || !_settings.Current.AnchorX.HasValue) return 50;
            return Math.Clamp((_settings.Current.AnchorX.Value - a.X) / a.Width * 100, 5, 95);
        }
        set
        {
            // 🌟 增加保护锁
            if (_isInitializing) return;
            if (_updatingPosition) return;
            var a = ScreenArea;
            if (a.Width <= 0) return;
            _updatingPosition = true;
            var ax = a.X + a.Width * value / 100.0;
            var ay = _settings.Current.AnchorY ?? a.Y + a.Height * 0.85;
            _settings.Update(s => { s.AnchorX = ax; s.AnchorY = ay; });
            SetAnchor?.Invoke(ax, ay);
            Raise(nameof(SelectedPresetIndex));
            _updatingPosition = false;
        }
    }

    public double PositionYPct
    {
        get
        {
            var a = ScreenArea;
            if (a.Height <= 0 || !_settings.Current.AnchorY.HasValue) return 85;
            return Math.Clamp((_settings.Current.AnchorY.Value - a.Y) / a.Height * 100, 5, 95);
        }
        set
        {
            // 🌟 增加保护锁
            if (_isInitializing) return;
            if (_updatingPosition) return;
            var a = ScreenArea;
            if (a.Height <= 0) return;
            _updatingPosition = true;
            var ax = _settings.Current.AnchorX ?? a.X + a.Width * 0.5;
            var ay = a.Y + a.Height * value / 100.0;
            _settings.Update(s => { s.AnchorX = ax; s.AnchorY = ay; });
            SetAnchor?.Invoke(ax, ay);
            Raise(nameof(SelectedPresetIndex));
            _updatingPosition = false;
        }
    }

    private (double x, double y) PresetToAnchor(PositionPreset p)
    {
        var a = ScreenArea;
        if (a.Width <= 0) a = new PixelRect(0, 0, 1920, 1080);
        var margin = 80;
        int cx = a.X + a.Width / 2;
        int left = a.X + margin;
        int right = a.X + a.Width - margin;
        int cy = a.Y + a.Height / 2;
        int top = a.Y + margin;
        int bottom = a.Y + a.Height - margin;
        int x = p switch
        {
            PositionPreset.TopLeft or PositionPreset.MiddleLeft or PositionPreset.BottomLeft => left,
            PositionPreset.TopCenter or PositionPreset.Center or PositionPreset.BottomCenter => cx,
            _ => right,
        };
        int y = p switch
        {
            PositionPreset.TopLeft or PositionPreset.TopCenter or PositionPreset.TopRight => top,
            PositionPreset.MiddleLeft or PositionPreset.Center or PositionPreset.MiddleRight => cy,
            _ => bottom,
        };
        return (x, y);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _orchestrator.StateChanged -= OnOrchestratorChanged;
        _smtc.SessionsChanged -= RefreshPlayerRules;
    }

    private static IReadOnlyList<string> LoadFontOptions()
    {
        try
        {
            return FontManager.Current.SystemFonts
                .Select(f => f.Name)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct()
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Error("enum fonts", ex);
            return ["Microsoft YaHei UI", "Segoe UI"];
        }
    }

    private void OnSettingsChanged() => RaiseMany(SettingProps);
}

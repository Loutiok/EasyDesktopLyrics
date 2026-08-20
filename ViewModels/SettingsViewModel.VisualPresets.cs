using EasyDesktopLyrics.Models;

namespace EasyDesktopLyrics.ViewModels;

/// <summary>设置首页：三套稳定视觉预设与四项跨效果高级参数。</summary>
public sealed partial class SettingsViewModel
{
    public RelayCommand ApplyVisualPresetCommand { get; }

    public string ActiveVisualPresetId => _settings.Current.VisualPresetId;

    public string ActiveVisualPresetName => ActiveVisualPresetId switch
    {
        "classic" => "经典",
        "luminous" => "流光",
        "immersive" => "沉浸",
        _ => "自定义",
    };

    /// <summary>旧配置尚未应用预设时，按其真实歌词/背景状态选择最接近的预览。</summary>
    public string VisualPreviewModeId => ActiveVisualPresetId != "custom"
        ? ActiveVisualPresetId
        : _settings.Current.BackgroundFx.CoverAurora.Enabled
            ? "immersive"
            : _settings.Current.LyricVisualMode == "luminous" ? "luminous" : "classic";

    public bool IsClassicVisualPreset => ActiveVisualPresetId == "classic";
    public bool IsLuminousVisualPreset => ActiveVisualPresetId == "luminous";
    public bool IsImmersiveVisualPreset => ActiveVisualPresetId == "immersive";

    /// <summary>统一辉光强度：同时调整歌词流光、经典辉光半径和频谱辉光。</summary>
    public double VisualGlowStrengthVal
    {
        get => _settings.Current.LuminousGlowStrength;
        set
        {
            var strength = Math.Round(Math.Clamp(value, 0.2, 2), 1);
            if (Math.Abs(strength - _settings.Current.LuminousGlowStrength) < 0.05)
                return;
            _settings.Update(s => ApplyGlowStrength(s, strength));
        }
    }

    /// <summary>跨效果动画幅度（0–100）：位移、缩放、模糊和音频呼吸采用同一强弱语义。</summary>
    public double VisualAnimationAmplitudeVal
    {
        get => Math.Round(Math.Clamp(_settings.Current.LineTransitionDistance / 24.0 * 100, 0, 100));
        set
        {
            var amplitude = Math.Round(Math.Clamp(value, 0, 100));
            if (Math.Abs(amplitude - VisualAnimationAmplitudeVal) < 1)
                return;
            _settings.Update(s => ApplyAnimationAmplitude(s, amplitude));
        }
    }

    /// <summary>所有背景效果的统一不透明度。</summary>
    public double VisualBackgroundOpacityVal
    {
        get => _settings.Current.BackgroundFx.CoverAurora.OverallOpacity;
        set
        {
            var opacity = Math.Round(Math.Clamp(value, 0, 1), 2);
            if (Math.Abs(opacity - VisualBackgroundOpacityVal) < 0.01)
                return;
            _settings.Update(s => ApplyBackgroundOpacity(s, opacity));
        }
    }

    public IReadOnlyList<string> VisualPerformanceLevelOptions { get; } =
        ["节能 · 30 FPS", "均衡 · 60 FPS", "高质量 · 90 FPS"];

    public int VisualPerformanceLevelIndex
    {
        get
        {
            var fps = _settings.Current.BackgroundFx.Fps;
            return fps <= 30 ? 0 : fps <= 60 ? 1 : 2;
        }
        set
        {
            var level = Math.Clamp(value, 0, 2);
            var (targetFps, targetBands) = level switch
            {
                0 => (30, 32),
                2 => (90, 96),
                _ => (60, 64),
            };
            var background = _settings.Current.BackgroundFx;
            if (background.Fps == targetFps && background.Spectrum.BandCount == targetBands)
                return;
            _settings.Update(s => ApplyPerformanceLevel(s, level));
        }
    }

    private void ApplyVisualPreset(object? parameter)
    {
        if (parameter is not string id || id is not ("classic" or "luminous" or "immersive"))
            return;

        _settings.Update(s =>
        {
            s.VisualPresetId = id;
            s.ShadowEnabled = true;
            s.InactiveGlowEnabled = false;
            s.BackgroundFx.Snow.Enabled = false;
            s.BackgroundFx.Fog.Enabled = false;

            switch (id)
            {
                case "classic":
                    ApplyClassicPreset(s);
                    break;
                case "luminous":
                    ApplyLuminousPreset(s);
                    break;
                case "immersive":
                    ApplyImmersivePreset(s);
                    break;
            }
        });
    }

    private static void ApplyClassicPreset(AppSettings s)
    {
        s.LyricVisualMode = "classic";
        s.GlowEnabled = true;
        s.LineTransitionEnabled = true;
        s.LineTransitionType = "Fade";
        s.LineTransitionDurationMs = 300;
        s.LineTransitionEasing = "Quadratic";
        s.BackgroundFx.CoverAurora.Enabled = false;
        s.BackgroundFx.Spectrum.Enabled = false;
        ApplyGlowStrength(s, 0.7);
        ApplyAnimationAmplitude(s, 20);
        ApplyBackgroundOpacity(s, 0.65);
        ApplyPerformanceLevel(s, 0);
    }

    private static void ApplyLuminousPreset(AppSettings s)
    {
        s.LyricVisualMode = "luminous";
        s.GlowEnabled = false;
        s.LineTransitionEnabled = true;
        s.LineTransitionType = "Crossfade";
        s.LineTransitionDurationMs = 360;
        s.LineTransitionEasing = "Sine";
        s.BackgroundFx.CoverAurora.Enabled = false;
        ConfigureWaveBand(s.BackgroundFx.Spectrum, opacity: 0.72, width: 72);
        ApplyGlowStrength(s, 1.3);
        ApplyAnimationAmplitude(s, 38);
        ApplyBackgroundOpacity(s, 0.72);
        ApplyPerformanceLevel(s, 1);
    }

    private static void ApplyImmersivePreset(AppSettings s)
    {
        s.LyricVisualMode = "luminous";
        s.GlowEnabled = false;
        s.LineTransitionEnabled = true;
        s.LineTransitionType = "Slide";
        s.LineTransitionDurationMs = 420;
        s.LineTransitionEasing = "Sine";
        s.LineTransitionDirection = 2;
        s.LineTransitionSlideBlurEnabled = true;

        var aurora = s.BackgroundFx.CoverAurora;
        aurora.Enabled = true;
        aurora.Opacity = 0.58;
        aurora.CoverOpacity = 0.30;
        aurora.BlurRadius = 24;
        aurora.DimAmount = 0.30;
        aurora.FlowSpeed = 0.42;
        aurora.AudioReactive = true;

        ConfigureWaveBand(s.BackgroundFx.Spectrum, opacity: 0.58, width: 100);
        ApplyGlowStrength(s, 1.6);
        ApplyAnimationAmplitude(s, 58);
        ApplyBackgroundOpacity(s, 0.78);
        ApplyPerformanceLevel(s, 1);
    }

    private static void ConfigureWaveBand(SpectrumFxSettings spectrum, double opacity, double width)
    {
        spectrum.Enabled = true;
        spectrum.Style = "WaveBand";
        spectrum.Position = "Center";
        spectrum.Intensity = 90;
        spectrum.WidthPct = width;
        spectrum.CenterBass = true;
        spectrum.GlowEnabled = true;
        spectrum.Opacity = opacity;
    }

    private static void ApplyGlowStrength(AppSettings s, double strength)
    {
        strength = Math.Clamp(strength, 0.2, 2);
        s.LuminousGlowStrength = strength;
        s.GlowRadius = 4 + strength * 14;
        s.BackgroundFx.Spectrum.GlowStrength = strength;
    }

    private static void ApplyAnimationAmplitude(AppSettings s, double amplitude)
    {
        var normalized = Math.Clamp(amplitude, 0, 100) / 100.0;
        s.LineTransitionDistance = normalized * 24;
        s.LineTransitionScale = Math.Clamp(0.97 - normalized * 0.19, 0.3, 0.95);
        s.LineTransitionSlideBlurRadius = normalized * 8;
        s.CoverAnimSlideDistance = normalized * 20;
        s.CoverTitleSlideDistance = normalized * 20;
        s.BackgroundFx.CoverAurora.AudioResponse = normalized * 0.65;
        s.BackgroundFx.Spectrum.Breathing = (int)Math.Round(normalized * 55);
    }

    private static void ApplyBackgroundOpacity(AppSettings s, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        s.BackgroundFx.CoverAurora.OverallOpacity = opacity;
        s.BackgroundFx.Spectrum.Opacity = Math.Max(0.05, opacity);
        s.BackgroundFx.Fog.Opacity = opacity;
        s.BackgroundFx.Snow.Opacity = opacity;
    }

    private static void ApplyPerformanceLevel(AppSettings s, int level)
    {
        var (fps, bands, smoothing) = level switch
        {
            0 => (30, 32, 4),
            2 => (90, 96, 3),
            _ => (60, 64, 4),
        };
        s.BackgroundFx.Fps = fps;
        s.BackgroundFx.Spectrum.BandCount = bands;
        s.BackgroundFx.Spectrum.Smoothing = smoothing;
    }
}

using EasyDesktopLyrics.Models;

namespace EasyDesktopLyrics.Services;

/// <summary>内置预设与用户视觉快照的创建、应用和描述；不负责 UI 或持久化时机。</summary>
public static class VisualPresetService
{
    public static bool ApplyBuiltIn(AppSettings settings, string id)
    {
        if (id is not ("classic" or "luminous" or "immersive"))
            return false;

        settings.VisualPresetId = id;
        settings.ShadowEnabled = true;
        settings.InactiveGlowEnabled = false;
        settings.BackgroundFx.Snow.Enabled = false;
        settings.BackgroundFx.Fog.Enabled = false;

        switch (id)
        {
            case "classic":
                ApplyClassic(settings);
                break;
            case "luminous":
                ApplyLuminous(settings);
                break;
            case "immersive":
                ApplyImmersive(settings);
                break;
        }
        return true;
    }

    public static VisualPresetSnapshot Capture(AppSettings settings) => new()
    {
        ColorHex = settings.ColorHex,
        InactiveColorHex = settings.InactiveColorHex,
        InactiveOpacity = settings.InactiveOpacity,
        ShadowEnabled = settings.ShadowEnabled,
        ShadowColorHex = settings.ShadowColorHex,
        ShadowBlurRadius = settings.ShadowBlurRadius,
        ShadowOffsetY = settings.ShadowOffsetY,
        StrokeEnabled = settings.StrokeEnabled,
        StrokeColorHex = settings.StrokeColorHex,
        StrokeThickness = settings.StrokeThickness,
        InactiveStrokeEnabled = settings.InactiveStrokeEnabled,
        InactiveStrokeColorHex = settings.InactiveStrokeColorHex,
        InactiveStrokeThickness = settings.InactiveStrokeThickness,
        GlowEnabled = settings.GlowEnabled,
        GlowColorHex = settings.GlowColorHex,
        GlowRadius = settings.GlowRadius,
        InactiveGlowEnabled = settings.InactiveGlowEnabled,
        InactiveGlowColorHex = settings.InactiveGlowColorHex,
        InactiveGlowRadius = settings.InactiveGlowRadius,
        LyricVisualMode = settings.LyricVisualMode,
        LuminousGlowColorHex = settings.LuminousGlowColorHex,
        LuminousGlowStrength = settings.LuminousGlowStrength,
        LuminousLongNoteThresholdMs = settings.LuminousLongNoteThresholdMs,
        LuminousReleaseMs = settings.LuminousReleaseMs,
        LineTransitionEnabled = settings.LineTransitionEnabled,
        LineTransitionType = settings.LineTransitionType,
        LineTransitionDurationMs = settings.LineTransitionDurationMs,
        LineTransitionScale = settings.LineTransitionScale,
        LineTransitionScaleExitGrow = settings.LineTransitionScaleExitGrow,
        LineTransitionSlideBlurEnabled = settings.LineTransitionSlideBlurEnabled,
        LineTransitionSlideBlurRadius = settings.LineTransitionSlideBlurRadius,
        LineTransitionEasing = settings.LineTransitionEasing,
        LineTransitionDirection = settings.LineTransitionDirection,
        LineTransitionDistance = settings.LineTransitionDistance,
        CoverAnimSlideDistance = settings.CoverAnimSlideDistance,
        CoverTitleSlideDistance = settings.CoverTitleSlideDistance,
        BackgroundFx = CloneBackground(settings.BackgroundFx),
    };

    public static void ApplySnapshot(AppSettings settings, VisualPresetSnapshot snapshot)
    {
        settings.ColorHex = snapshot.ColorHex;
        settings.InactiveColorHex = snapshot.InactiveColorHex;
        settings.InactiveOpacity = snapshot.InactiveOpacity;
        settings.ShadowEnabled = snapshot.ShadowEnabled;
        settings.ShadowColorHex = snapshot.ShadowColorHex;
        settings.ShadowBlurRadius = snapshot.ShadowBlurRadius;
        settings.ShadowOffsetY = snapshot.ShadowOffsetY;
        settings.StrokeEnabled = snapshot.StrokeEnabled;
        settings.StrokeColorHex = snapshot.StrokeColorHex;
        settings.StrokeThickness = snapshot.StrokeThickness;
        settings.InactiveStrokeEnabled = snapshot.InactiveStrokeEnabled;
        settings.InactiveStrokeColorHex = snapshot.InactiveStrokeColorHex;
        settings.InactiveStrokeThickness = snapshot.InactiveStrokeThickness;
        settings.GlowEnabled = snapshot.GlowEnabled;
        settings.GlowColorHex = snapshot.GlowColorHex;
        settings.GlowRadius = snapshot.GlowRadius;
        settings.InactiveGlowEnabled = snapshot.InactiveGlowEnabled;
        settings.InactiveGlowColorHex = snapshot.InactiveGlowColorHex;
        settings.InactiveGlowRadius = snapshot.InactiveGlowRadius;
        settings.LyricVisualMode = snapshot.LyricVisualMode;
        settings.LuminousGlowColorHex = snapshot.LuminousGlowColorHex;
        settings.LuminousGlowStrength = snapshot.LuminousGlowStrength;
        settings.LuminousLongNoteThresholdMs = snapshot.LuminousLongNoteThresholdMs;
        settings.LuminousReleaseMs = snapshot.LuminousReleaseMs;
        settings.LineTransitionEnabled = snapshot.LineTransitionEnabled;
        settings.LineTransitionType = snapshot.LineTransitionType;
        settings.LineTransitionDurationMs = snapshot.LineTransitionDurationMs;
        settings.LineTransitionScale = snapshot.LineTransitionScale;
        settings.LineTransitionScaleExitGrow = snapshot.LineTransitionScaleExitGrow;
        settings.LineTransitionSlideBlurEnabled = snapshot.LineTransitionSlideBlurEnabled;
        settings.LineTransitionSlideBlurRadius = snapshot.LineTransitionSlideBlurRadius;
        settings.LineTransitionEasing = snapshot.LineTransitionEasing;
        settings.LineTransitionDirection = snapshot.LineTransitionDirection;
        settings.LineTransitionDistance = snapshot.LineTransitionDistance;
        settings.CoverAnimSlideDistance = snapshot.CoverAnimSlideDistance;
        settings.CoverTitleSlideDistance = snapshot.CoverTitleSlideDistance;
        settings.BackgroundFx = CloneBackground(snapshot.BackgroundFx);
    }

    public static string Describe(VisualPresetSnapshot snapshot)
    {
        var lyric = snapshot.LyricVisualMode == "luminous" ? "流光歌词" : "经典歌词";
        var backgrounds = new List<string>(2);
        if (snapshot.BackgroundFx.CoverAurora.Enabled)
            backgrounds.Add("封面极光");
        if (snapshot.BackgroundFx.Spectrum.Enabled)
            backgrounds.Add(snapshot.BackgroundFx.Spectrum.Style == "WaveBand" ? "极简曲线" : "频谱");
        return backgrounds.Count == 0 ? $"{lyric} · 无背景动效" : $"{lyric} · {string.Join(" + ", backgrounds)}";
    }

    public static void ApplyGlowStrength(AppSettings settings, double strength)
    {
        strength = Math.Clamp(strength, 0.2, 2);
        settings.LuminousGlowStrength = strength;
        settings.GlowRadius = 4 + strength * 14;
        settings.BackgroundFx.Spectrum.GlowStrength = strength;
    }

    public static void ApplyAnimationAmplitude(AppSettings settings, double amplitude)
    {
        var normalized = Math.Clamp(amplitude, 0, 100) / 100.0;
        settings.LineTransitionDistance = normalized * 24;
        settings.LineTransitionScale = Math.Clamp(0.97 - normalized * 0.19, 0.3, 0.95);
        settings.LineTransitionSlideBlurRadius = normalized * 8;
        settings.CoverAnimSlideDistance = normalized * 20;
        settings.CoverTitleSlideDistance = normalized * 20;
        settings.BackgroundFx.CoverAurora.AudioResponse = normalized * 0.65;
        settings.BackgroundFx.Spectrum.Breathing = (int)Math.Round(normalized * 55);
    }

    public static void ApplyBackgroundOpacity(AppSettings settings, double opacity)
    {
        opacity = Math.Clamp(opacity, 0, 1);
        settings.BackgroundFx.CoverAurora.OverallOpacity = opacity;
        settings.BackgroundFx.Spectrum.Opacity = Math.Max(0.05, opacity);
        settings.BackgroundFx.Fog.Opacity = opacity;
        settings.BackgroundFx.Snow.Opacity = opacity;
    }

    public static void ApplyPerformanceLevel(AppSettings settings, int level)
    {
        var (fps, bands, smoothing) = level switch
        {
            0 => (30, 32, 4),
            2 => (90, 96, 3),
            _ => (60, 64, 4),
        };
        settings.BackgroundFx.Fps = fps;
        settings.BackgroundFx.Spectrum.BandCount = bands;
        settings.BackgroundFx.Spectrum.Smoothing = smoothing;
    }

    private static void ApplyClassic(AppSettings settings)
    {
        settings.LyricVisualMode = "classic";
        settings.GlowEnabled = true;
        settings.LineTransitionEnabled = true;
        settings.LineTransitionType = "Fade";
        settings.LineTransitionDurationMs = 300;
        settings.LineTransitionEasing = "Quadratic";
        settings.BackgroundFx.CoverAurora.Enabled = false;
        settings.BackgroundFx.Spectrum.Enabled = false;
        ApplyGlowStrength(settings, 0.7);
        ApplyAnimationAmplitude(settings, 20);
        ApplyBackgroundOpacity(settings, 0.65);
        ApplyPerformanceLevel(settings, 0);
    }

    private static void ApplyLuminous(AppSettings settings)
    {
        settings.LyricVisualMode = "luminous";
        settings.GlowEnabled = false;
        settings.LineTransitionEnabled = true;
        settings.LineTransitionType = "Crossfade";
        settings.LineTransitionDurationMs = 360;
        settings.LineTransitionEasing = "Sine";
        settings.BackgroundFx.CoverAurora.Enabled = false;
        ConfigureWaveBand(settings.BackgroundFx.Spectrum, 72);
        ApplyGlowStrength(settings, 1.3);
        ApplyAnimationAmplitude(settings, 38);
        ApplyBackgroundOpacity(settings, 0.72);
        ApplyPerformanceLevel(settings, 1);
    }

    private static void ApplyImmersive(AppSettings settings)
    {
        settings.LyricVisualMode = "luminous";
        settings.GlowEnabled = false;
        settings.LineTransitionEnabled = true;
        settings.LineTransitionType = "Slide";
        settings.LineTransitionDurationMs = 420;
        settings.LineTransitionEasing = "Sine";
        settings.LineTransitionDirection = 2;
        settings.LineTransitionSlideBlurEnabled = true;

        var aurora = settings.BackgroundFx.CoverAurora;
        aurora.Enabled = true;
        aurora.Opacity = 0.58;
        aurora.CoverOpacity = 0.30;
        aurora.BlurRadius = 24;
        aurora.DimAmount = 0.30;
        aurora.FlowSpeed = 0.42;
        aurora.AudioReactive = true;

        ConfigureWaveBand(settings.BackgroundFx.Spectrum, 100);
        ApplyGlowStrength(settings, 1.6);
        ApplyAnimationAmplitude(settings, 58);
        ApplyBackgroundOpacity(settings, 0.78);
        ApplyPerformanceLevel(settings, 1);
    }

    private static void ConfigureWaveBand(SpectrumFxSettings spectrum, double width)
    {
        spectrum.Enabled = true;
        spectrum.Style = "WaveBand";
        spectrum.Position = "Center";
        spectrum.Intensity = 90;
        spectrum.WidthPct = width;
        spectrum.CenterBass = true;
        spectrum.GlowEnabled = true;
    }

    private static BackgroundFxSettings CloneBackground(BackgroundFxSettings source) => new()
    {
        Fps = source.Fps,
        CoverAurora = new CoverAuroraFxSettings
        {
            Enabled = source.CoverAurora.Enabled,
            OverallOpacity = source.CoverAurora.OverallOpacity,
            Opacity = source.CoverAurora.Opacity,
            CoverOpacity = source.CoverAurora.CoverOpacity,
            BlurRadius = source.CoverAurora.BlurRadius,
            DimAmount = source.CoverAurora.DimAmount,
            FlowSpeed = source.CoverAurora.FlowSpeed,
            AudioReactive = source.CoverAurora.AudioReactive,
            AudioResponse = source.CoverAurora.AudioResponse,
        },
        Spectrum = new SpectrumFxSettings
        {
            Enabled = source.Spectrum.Enabled,
            Position = source.Spectrum.Position,
            Style = source.Spectrum.Style,
            Intensity = source.Spectrum.Intensity,
            BandCount = source.Spectrum.BandCount,
            WidthPct = source.Spectrum.WidthPct,
            CenterBass = source.Spectrum.CenterBass,
            Breathing = source.Spectrum.Breathing,
            Opacity = source.Spectrum.Opacity,
            ColorHex = source.Spectrum.ColorHex,
            GlowEnabled = source.Spectrum.GlowEnabled,
            GlowStrength = source.Spectrum.GlowStrength,
            Smoothing = source.Spectrum.Smoothing,
        },
        Snow = new SnowFxSettings
        {
            Enabled = source.Snow.Enabled,
            Intensity = source.Snow.Intensity,
            WidthPct = source.Snow.WidthPct,
            Opacity = source.Snow.Opacity,
            Size = source.Snow.Size,
            Speed = source.Snow.Speed,
            ColorHex = source.Snow.ColorHex,
        },
        Fog = new FogFxSettings
        {
            Enabled = source.Fog.Enabled,
            Opacity = source.Fog.Opacity,
            Softness = source.Fog.Softness,
            FlowSpeed = source.Fog.FlowSpeed,
            UseCoverColor = source.Fog.UseCoverColor,
            UseBackdropColor = source.Fog.UseBackdropColor,
            Blend = source.Fog.Blend,
            Animated = source.Fog.Animated,
        },
    };
}

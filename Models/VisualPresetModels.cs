namespace EasyDesktopLyrics.Models;

/// <summary>用户命名的视觉预设。只保存视觉效果，不包含窗口位置、歌词源或播放器规则。</summary>
public sealed class UserVisualPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "我的预设";

    public VisualPresetSnapshot Snapshot { get; set; } = new();
}

/// <summary>可独立复制和应用的歌词、切行动画及背景效果快照。</summary>
public sealed class VisualPresetSnapshot
{
    public string ColorHex { get; set; } = "#FFFFFF";
    public string InactiveColorHex { get; set; } = "";
    public double InactiveOpacity { get; set; } = 0.45;

    public bool ShadowEnabled { get; set; }
    public string ShadowColorHex { get; set; } = "#000000";
    public double ShadowBlurRadius { get; set; } = 8;
    public double ShadowOffsetY { get; set; } = 2;

    public bool StrokeEnabled { get; set; }
    public string StrokeColorHex { get; set; } = "#000000";
    public double StrokeThickness { get; set; } = 2;
    public bool InactiveStrokeEnabled { get; set; }
    public string InactiveStrokeColorHex { get; set; } = "#000000";
    public double InactiveStrokeThickness { get; set; } = 2;

    public bool GlowEnabled { get; set; }
    public string GlowColorHex { get; set; } = "#FFFFFF";
    public double GlowRadius { get; set; } = 14;
    public bool InactiveGlowEnabled { get; set; }
    public string InactiveGlowColorHex { get; set; } = "#FFFFFF";
    public double InactiveGlowRadius { get; set; } = 14;

    public string LyricVisualMode { get; set; } = "classic";
    public string LuminousGlowColorHex { get; set; } = "#FFFFFF";
    public double LuminousGlowStrength { get; set; } = 1;
    public int LuminousLongNoteThresholdMs { get; set; } = 650;
    public int LuminousReleaseMs { get; set; } = 420;

    public bool LineTransitionEnabled { get; set; }
    public string LineTransitionType { get; set; } = "Fade";
    public int LineTransitionDurationMs { get; set; } = 400;
    public double LineTransitionScale { get; set; } = 0.85;
    public bool LineTransitionScaleExitGrow { get; set; } = true;
    public bool LineTransitionSlideBlurEnabled { get; set; } = true;
    public double LineTransitionSlideBlurRadius { get; set; } = 4;
    public string LineTransitionEasing { get; set; } = "Quadratic";
    public int LineTransitionDirection { get; set; } = 2;
    public double LineTransitionDistance { get; set; } = 10;
    public double CoverAnimSlideDistance { get; set; } = 12;
    public double CoverTitleSlideDistance { get; set; } = 12;

    public BackgroundFxSettings BackgroundFx { get; set; } = new();
}

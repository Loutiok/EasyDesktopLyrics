using System.Collections.ObjectModel;
using EasyDesktopLyrics.Models;
using EasyDesktopLyrics.Services;

namespace EasyDesktopLyrics.ViewModels;

public sealed record UserVisualPresetItem(string Id, string Name, string Description);

/// <summary>设置首页：内置预设、用户命名预设与四项跨效果高级参数。</summary>
public sealed partial class SettingsViewModel
{
    private string _newVisualPresetName = "";
    private string _visualPresetStatus = "";

    public RelayCommand ApplyVisualPresetCommand { get; }
    public RelayCommand SaveUserVisualPresetCommand { get; }
    public RelayCommand ApplyUserVisualPresetCommand { get; }
    public RelayCommand UpdateUserVisualPresetCommand { get; }
    public RelayCommand DeleteUserVisualPresetCommand { get; }

    public ObservableCollection<UserVisualPresetItem> UserVisualPresets { get; } = [];

    public string ActiveVisualPresetId => _settings.Current.VisualPresetId;

    public string ActiveVisualPresetName
    {
        get
        {
            var id = ActiveVisualPresetId;
            if (id.StartsWith("user:", StringComparison.OrdinalIgnoreCase))
            {
                var presetId = id[5..];
                return _settings.Current.UserVisualPresets
                    .FirstOrDefault(p => p.Id.Equals(presetId, StringComparison.OrdinalIgnoreCase))?.Name ?? "自定义";
            }
            return id switch
            {
                "classic" => "经典",
                "luminous" => "流光",
                "immersive" => "沉浸",
                _ => "自定义",
            };
        }
    }

    public bool IsClassicVisualPreset => ActiveVisualPresetId == "classic";
    public bool IsLuminousVisualPreset => ActiveVisualPresetId == "luminous";
    public bool IsImmersiveVisualPreset => ActiveVisualPresetId == "immersive";
    public bool HasNoUserVisualPresets => UserVisualPresets.Count == 0;

    public string NewVisualPresetName
    {
        get => _newVisualPresetName;
        set
        {
            var normalized = value.Length <= 24 ? value : value[..24];
            if (!Set(ref _newVisualPresetName, normalized))
                return;
            VisualPresetStatus = "";
        }
    }

    public string VisualPresetStatus
    {
        get => _visualPresetStatus;
        private set => Set(ref _visualPresetStatus, value);
    }

    /// <summary>统一辉光强度：同时调整歌词流光、经典辉光半径和频谱辉光。</summary>
    public double VisualGlowStrengthVal
    {
        get => _settings.Current.LuminousGlowStrength;
        set
        {
            var strength = Math.Round(Math.Clamp(value, 0.2, 2), 1);
            if (Math.Abs(strength - _settings.Current.LuminousGlowStrength) < 0.05)
                return;
            _settings.Update(s => VisualPresetService.ApplyGlowStrength(s, strength));
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
            _settings.Update(s => VisualPresetService.ApplyAnimationAmplitude(s, amplitude));
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
            _settings.Update(s => VisualPresetService.ApplyBackgroundOpacity(s, opacity));
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
            _settings.Update(s => VisualPresetService.ApplyPerformanceLevel(s, level));
        }
    }

    private void ApplyVisualPreset(object? parameter)
    {
        if (parameter is not string id)
            return;
        _settings.Update(s => VisualPresetService.ApplyBuiltIn(s, id));
        VisualPresetStatus = "";
    }

    private void SaveUserVisualPreset()
    {
        var name = NewVisualPresetName.Trim();
        if (name.Length == 0)
        {
            VisualPresetStatus = "请输入预设名称。";
            return;
        }

        var existing = _settings.Current.UserVisualPresets
            .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing == null && _settings.Current.UserVisualPresets.Count >= 12)
        {
            VisualPresetStatus = "最多保存 12 个用户预设。";
            return;
        }

        var snapshot = VisualPresetService.Capture(_settings.Current);
        var activeId = existing?.Id ?? Guid.NewGuid().ToString("N");
        _settings.Update(s =>
        {
            var target = s.UserVisualPresets
                .FirstOrDefault(p => p.Id.Equals(activeId, StringComparison.OrdinalIgnoreCase));
            if (target == null)
            {
                s.UserVisualPresets.Add(new UserVisualPreset
                {
                    Id = activeId,
                    Name = name,
                    Snapshot = snapshot,
                });
            }
            else
            {
                target.Name = name;
                target.Snapshot = snapshot;
            }
            s.VisualPresetId = $"user:{activeId}";
        });

        RefreshUserVisualPresets();
        NewVisualPresetName = "";
        VisualPresetStatus = existing == null ? $"已保存“{name}”。" : $"已覆盖同名预设“{name}”。";
    }

    private void ApplyUserVisualPreset(object? parameter)
    {
        var preset = FindUserPreset(parameter);
        if (preset == null)
            return;
        _settings.Update(s =>
        {
            VisualPresetService.ApplySnapshot(s, preset.Snapshot);
            s.VisualPresetId = $"user:{preset.Id}";
        });
        VisualPresetStatus = $"已应用“{preset.Name}”。";
    }

    private void UpdateUserVisualPreset(object? parameter)
    {
        var preset = FindUserPreset(parameter);
        if (preset == null)
            return;
        var snapshot = VisualPresetService.Capture(_settings.Current);
        _settings.Update(s =>
        {
            var target = s.UserVisualPresets.First(p => p.Id == preset.Id);
            target.Snapshot = snapshot;
            s.VisualPresetId = $"user:{target.Id}";
        });
        RefreshUserVisualPresets();
        VisualPresetStatus = $"已用当前效果更新“{preset.Name}”。";
    }

    private void DeleteUserVisualPreset(object? parameter)
    {
        var preset = FindUserPreset(parameter);
        if (preset == null)
            return;
        _settings.Update(s =>
        {
            s.UserVisualPresets.RemoveAll(p => p.Id == preset.Id);
            if (s.VisualPresetId == $"user:{preset.Id}")
                s.VisualPresetId = "custom";
        });
        RefreshUserVisualPresets();
        VisualPresetStatus = $"已删除“{preset.Name}”。";
    }

    private UserVisualPreset? FindUserPreset(object? parameter)
    {
        var id = parameter as string;
        return id == null
            ? null
            : _settings.Current.UserVisualPresets.FirstOrDefault(p => p.Id == id);
    }

    private void RefreshUserVisualPresets()
    {
        UserVisualPresets.Clear();
        foreach (var preset in _settings.Current.UserVisualPresets)
        {
            UserVisualPresets.Add(new UserVisualPresetItem(
                preset.Id,
                preset.Name,
                VisualPresetService.Describe(preset.Snapshot)));
        }
        RaiseMany(nameof(HasNoUserVisualPresets), nameof(ActiveVisualPresetName));
    }
}

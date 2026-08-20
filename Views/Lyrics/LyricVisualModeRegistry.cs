using Avalonia.Controls;
using EasyDesktopLyrics.ViewModels;

namespace EasyDesktopLyrics.Views.Lyrics;

internal static class LyricVisualModeIds
{
    public const string Classic = "classic";
}

/// <summary>
/// 歌词视觉模式注册表。新增模式只需注册工厂，不再向覆盖窗口追加一组构建分支。
/// </summary>
internal sealed class LyricVisualModeRegistry
{
    private readonly Dictionary<string, Func<OverlayViewModel, Panel, ILyricVisualMode>> _factories =
        new(StringComparer.OrdinalIgnoreCase);

    public static LyricVisualModeRegistry CreateDefault()
    {
        var registry = new LyricVisualModeRegistry();
        registry.Register(LyricVisualModeIds.Classic, static (vm, host) => new ClassicLyricVisualMode(vm, host));
        return registry;
    }

    public void Register(string id, Func<OverlayViewModel, Panel, ILyricVisualMode> factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(factory);
        _factories[id] = factory;
    }

    public ILyricVisualMode Create(string id, OverlayViewModel viewModel, Panel host)
    {
        if (!_factories.TryGetValue(id, out var factory))
            throw new InvalidOperationException($"Unknown lyric visual mode: {id}");
        return factory(viewModel, host);
    }
}

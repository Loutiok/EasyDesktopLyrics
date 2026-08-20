using System.Diagnostics;
using Avalonia.Threading;

namespace EasyDesktopLyrics.Rendering.Animation;

/// <summary>
/// UI 动画的单一进度驱动器。使用单调时钟计算真实进度，避免窗口为每种效果维护独立计时状态。
/// </summary>
internal sealed class AnimationProgressDriver : IDisposable
{
    private readonly DispatcherTimer _timer;
    private Action<double>? _progress;
    private Action? _completed;
    private long _startedAt;
    private TimeSpan _duration;
    private int _generation;
    private bool _disposed;

    public AnimationProgressDriver(TimeSpan frameInterval)
    {
        if (frameInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(frameInterval));

        _timer = new DispatcherTimer { Interval = frameInterval };
        _timer.Tick += OnTick;
    }

    public bool IsRunning => _timer.IsEnabled;

    public void Start(TimeSpan duration, Action<double> progress, Action completed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(completed);

        Cancel();
        _duration = duration > TimeSpan.Zero ? duration : TimeSpan.FromMilliseconds(1);
        _progress = progress;
        _completed = completed;
        _startedAt = Stopwatch.GetTimestamp();
        var generation = ++_generation;

        progress(0);
        if (generation == _generation && _progress != null)
            _timer.Start();
    }

    public void Cancel()
    {
        _generation++;
        _timer.Stop();
        _progress = null;
        _completed = null;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var generation = _generation;
        var elapsed = Stopwatch.GetElapsedTime(_startedAt);
        var value = Math.Clamp(elapsed.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
        _progress?.Invoke(value);
        if (value < 1 || generation != _generation)
            return;

        var completed = _completed;
        Cancel();
        completed?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Cancel();
        _timer.Tick -= OnTick;
    }
}

using EasyDesktopLyrics.Models;

namespace EasyDesktopLyrics.Services;

/// <summary>
/// 播放进度插值时钟。SMTC 的 Position 仅在 PositionAt（LastUpdatedTime）时刻准确，
/// 播放器上报间隔不定，本地用挂钟插值出连续位置。
/// </summary>
public sealed class PlaybackClock
{
    private TimeSpan _basePos;
    private DateTimeOffset _baseAt = DateTimeOffset.UtcNow;
    private double _rate = 1.0;
    private TimeSpan _duration;
    private DateTimeOffset _lastResetAt = DateTimeOffset.MinValue;

    public bool IsPlaying { get; private set; }

    public void Sync(PlaybackSnapshot s)
    {
        _duration = s.Duration;
        _rate = s.Rate <= 0 ? 1.0 : s.Rate;

        var age = DateTimeOffset.UtcNow - s.PositionAt;
        var timelineUsable = age > TimeSpan.Zero && age < TimeSpan.FromSeconds(30);

        if (timelineUsable)
        {
            var estimatedNow = Estimate();
            var incomingPositionNow = s.Position + age * _rate;
            var backwardDelta = estimatedNow - incomingPositionNow;

            // 缩短到 1.5 秒：足够消化切歌瞬间的旧事件倾倒，又不会占用正常交互时长
            var isWithinTrackChangeWindow = DateTimeOffset.UtcNow - _lastResetAt < TimeSpan.FromMilliseconds(1500);
            
            if (isWithinTrackChangeWindow 
                && IsPlaying 
                && s.IsPlaying 
                && backwardDelta > TimeSpan.FromMilliseconds(150) 
                && backwardDelta < TimeSpan.FromMilliseconds(1500))
            {
                // 仅在切歌刚发生的 1.5 秒内吃掉滞后小抖动
            }
            else
            {
                // 其余所有情况：包括 1.5 秒后、或者往回拖拽超过 1.5 秒的大跳转，全部秒级响应
                _basePos = s.Position;
                _baseAt = s.PositionAt;
            }
        }
        else if (IsPlaying != s.IsPlaying)
        {
            _basePos = Estimate();
            _baseAt = DateTimeOffset.UtcNow;
        }

        IsPlaying = s.IsPlaying;
    }

    public void Reset()
    {
        _basePos = TimeSpan.Zero;
        _baseAt = DateTimeOffset.UtcNow;
        _rate = 1.0;
        _duration = TimeSpan.Zero;
        IsPlaying = false;
        _lastResetAt = DateTimeOffset.UtcNow; // 标记切歌时刻
    }

    public TimeSpan Estimate()
    {
        var pos = IsPlaying
            ? _basePos + (DateTimeOffset.UtcNow - _baseAt) * _rate
            : _basePos;

        if (_duration > TimeSpan.Zero && pos > _duration)
            pos = _duration;
        return pos < TimeSpan.Zero ? TimeSpan.Zero : pos;
    }
}

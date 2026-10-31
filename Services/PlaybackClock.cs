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

            // 切歌后的 1.5 秒内，如果上报的位置比当前估算落后（100ms ~ 1500ms），
            // 视为播放器启动排队的迟到包，不回滚时钟，维持挂钟进度
            var isWithinTrackChangeWindow = DateTimeOffset.UtcNow - _lastResetAt < TimeSpan.FromMilliseconds(1500);

            if (isWithinTrackChangeWindow
                && IsPlaying
                && s.IsPlaying
                && backwardDelta > TimeSpan.FromMilliseconds(100)
                && backwardDelta < TimeSpan.FromMilliseconds(1500))
            {
                // 吃掉切歌初期的迟到小倒流
            }
            else
            {
                // 其余所有情况：正常采用
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

    /// <summary>
    /// 切歌时复位：基准时间归零，但保持 keepPlaying 状态，
    /// 确保切歌后哪怕暂无 SMTC 事件包，挂钟也能立即从 0 开始自然计时，防止歌词滞后
    /// </summary>
    public void Reset(bool keepPlaying = true)
    {
        _basePos = TimeSpan.Zero;
        _baseAt = DateTimeOffset.UtcNow;
        _rate = 1.0;
        _duration = TimeSpan.Zero;
        IsPlaying = keepPlaying;
        _lastResetAt = DateTimeOffset.UtcNow;
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

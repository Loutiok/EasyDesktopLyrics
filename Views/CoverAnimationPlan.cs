using Avalonia;

namespace EasyDesktopLyrics.Views;

/// <summary>封面视觉状态：相对横向居中布局槽的位移、中心缩放和透明度。</summary>
internal readonly record struct CoverVisualState(double X, double Y, double Scale, double Opacity);

/// <summary>
/// 一轮切歌封面的纯几何计划。封面由布局系统持续横向居中，布局尺寸全程保持 RestingSize；
/// 放大只通过以中心为原点的 Scale 完成，因此改变方向不会再混入尺寸向右下扩张的位移。
/// </summary>
internal readonly record struct CoverAnimationPlan(
    CoverVisualState PersistentStart,
    CoverVisualState TransientStart,
    CoverVisualState Center,
    CoverVisualState PersistentEnd,
    CoverVisualState TransientEnd)
{
    public static CoverAnimationPlan Create(
        double containerWidth,
        double restingSize,
        double animationSize,
        Point persistentSlot,
        int direction,
        double distance)
    {
        restingSize = Math.Max(1, restingSize);
        animationSize = Math.Max(restingSize, animationSize);
        var scale = animationSize / restingSize;
        var centerY = animationSize / 2;
        var baseY = centerY - restingSize / 2;
        var offset = DirectionOffset(direction, distance);
        var persistent = Resting(containerWidth, restingSize, persistentSlot, visible: true);

        var transientStart = new CoverVisualState(offset.X, baseY + offset.Y, scale, 0);
        var center = new CoverVisualState(0, baseY, scale, 1);
        var transientEnd = new CoverVisualState(offset.X, baseY + offset.Y, scale, 0);
        return new CoverAnimationPlan(persistent, transientStart, center, persistent, transientEnd);
    }

    public static CoverVisualState Resting(
        double containerWidth,
        double restingSize,
        Point slot,
        bool visible)
    {
        var centeredLayoutX = Math.Max(0, (containerWidth - restingSize) / 2);
        return new CoverVisualState(
            slot.X - centeredLayoutX,
            slot.Y,
            1,
            visible ? 1 : 0);
    }

    public static Vector DirectionOffset(int direction, double distance)
    {
        var d = Math.Clamp(distance, 0, 60);
        return Math.Clamp(direction, 0, 4) switch
        {
            1 => new Vector(0, -d),
            2 => new Vector(0, d),
            3 => new Vector(-d, 0),
            4 => new Vector(d, 0),
            _ => default,
        };
    }
}

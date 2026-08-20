using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using EasyDesktopLyrics.Models;
using EasyDesktopLyrics.Services;

namespace EasyDesktopLyrics.Views;

/// <summary>
/// Cover-driven background: a softly blurred cover base plus three slowly moving aurora lights.
/// The low-frequency feature only changes light radius and opacity; it never moves the window.
/// </summary>
public sealed class CoverAuroraFxControl : Control, IDisposable
{
    private const double CornerRadius = 6;
    private const int LightCount = 3;

    private readonly Dictionary<string, IBrush> _brushCache = new();
    private readonly double[] _phaseX = [0.4, 2.7, 4.8];
    private readonly double[] _phaseY = [1.2, 4.1, 5.6];
    private CoverPalette _palette = CoverPalette.Default;
    private IImage? _coverImage;
    private ImageBrush? _coverBrush;
    private BlurEffect? _coverBlur;
    private IBrush? _dimBrush;
    private bool _enabled;
    private bool _audioReactive = true;
    private double _opacity = 0.42;
    private double _coverOpacity = 0.24;
    private double _blurRadius = 18;
    private double _dimAmount = 0.30;
    private double _flowSpeed = 0.35;
    private double _audioResponse = 0.35;
    private double _time;
    private float _lowEnergy;

    public bool UsesAudio => _enabled && _audioReactive && _audioResponse > 0;

    public void SetCoverImage(IImage? image)
    {
        _coverImage = image;
        _palette = CoverPaletteExtractor.Extract(image);
        RebuildCoverBrush();
        _brushCache.Clear();
        InvalidateVisual();
    }

    public void ApplySettings(CoverAuroraFxSettings settings)
    {
        _enabled = settings.Enabled;
        _opacity = Math.Clamp(settings.Opacity, 0.05, 1);
        _coverOpacity = Math.Clamp(settings.CoverOpacity, 0, 0.8);
        _blurRadius = Math.Clamp(settings.BlurRadius, 0, 36);
        _dimAmount = Math.Clamp(settings.DimAmount, 0, 0.8);
        _flowSpeed = Math.Clamp(settings.FlowSpeed, 0, 2);
        _audioReactive = settings.AudioReactive;
        _audioResponse = Math.Clamp(settings.AudioResponse, 0, 1);
        _coverBlur = _blurRadius > 0 ? new BlurEffect { Radius = _blurRadius } : null;
        _dimBrush = _dimAmount > 0
            ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * _dimAmount), 7, 8, 14))
            : null;
        RebuildCoverBrush();
        _brushCache.Clear();
        InvalidateVisual();
    }

    public void TickAndInvalidate(double deltaSeconds, AudioFeatureFrame audioFrame)
    {
        _time += Math.Clamp(deltaSeconds, 0, 0.1) * _flowSpeed;
        _lowEnergy = _audioReactive ? audioFrame.LowEnergy : 0;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (!_enabled || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;

        using (context.PushClip(new RoundedRect(Bounds, CornerRadius)))
        {
            DrawBlurredCover(context);
            DrawAurora(context);
        }
    }

    private void DrawBlurredCover(DrawingContext context)
    {
        if (_coverBrush != null && _coverOpacity > 0)
        {
            if (_coverBlur != null)
            {
                var padding = _blurRadius * 1.5 + 2;
                var expanded = new Rect(
                    Bounds.X - padding,
                    Bounds.Y - padding,
                    Bounds.Width + padding * 2,
                    Bounds.Height + padding * 2);
                using (context.PushEffect(_coverBlur, expanded))
                    context.DrawRectangle(_coverBrush, null, expanded);
            }
            else
            {
                context.DrawRectangle(_coverBrush, null, Bounds);
            }
        }

        if (_dimBrush != null)
            context.DrawRectangle(_dimBrush, null, Bounds);
    }

    private void DrawAurora(DrawingContext context)
    {
        var width = Bounds.Width;
        var height = Bounds.Height;
        var bass = MathF.Sqrt(Math.Clamp(_lowEnergy, 0, 1));
        var audioScale = 1 + bass * _audioResponse * 0.22;
        var audioOpacity = 1 + bass * _audioResponse * 0.18;

        for (var i = 0; i < LightCount; i++)
        {
            var speed = 0.42 + i * 0.11;
            var centerX = width * (0.5 + 0.46 * Math.Sin(_time * speed + _phaseX[i]));
            var centerY = height * (0.48 + 0.36 * Math.Sin(_time * (speed * 0.73) + _phaseY[i]));
            var radiusX = Math.Max(width * (0.38 + i * 0.06), height * 2.2) * audioScale;
            var radiusY = Math.Max(height * (1.15 + i * 0.18), 24) * audioScale;
            var color = i switch
            {
                0 => _palette.Primary,
                1 => _palette.Secondary,
                _ => _palette.Accent,
            };
            var brush = GetAuroraBrush(color, audioOpacity);
            context.DrawEllipse(brush, null, new Point(centerX, centerY), radiusX, radiusY);
        }
    }

    private IBrush GetAuroraBrush(Color color, double audioOpacity)
    {
        var opacity = Math.Clamp(_opacity * audioOpacity, 0, 1);
        var opacityStep = (int)Math.Round(opacity * 40);
        var key = $"{color.R}|{color.G}|{color.B}|{opacityStep}";
        if (_brushCache.TryGetValue(key, out var cached))
            return cached;

        var alpha = (byte)Math.Round(255 * opacityStep / 40.0);
        var brush = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb((byte)(alpha * 0.82), color.R, color.G, color.B), 0),
                new GradientStop(Color.FromArgb((byte)(alpha * 0.38), color.R, color.G, color.B), 0.46),
                new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1),
            },
        };
        _brushCache[key] = brush;
        return brush;
    }

    private void RebuildCoverBrush()
    {
        _coverBrush = _coverImage is not IImageBrushSource brushSource || _coverOpacity <= 0
            ? null
            : new ImageBrush
            {
                Source = brushSource,
                Stretch = Stretch.UniformToFill,
                Opacity = _coverOpacity,
            };
    }

    public void Dispose()
    {
        _coverBrush = null;
        _coverImage = null;
        _coverBlur = null;
        _dimBrush = null;
        _brushCache.Clear();
    }
}

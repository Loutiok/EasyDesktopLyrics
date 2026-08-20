using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace EasyDesktopLyrics.Services;

/// <summary>Extracts and weakly caches a compact, display-ready palette for each decoded cover.</summary>
public static class CoverPaletteExtractor
{
    private const int QuantizationLevels = 8;
    private const int BucketCount = QuantizationLevels * QuantizationLevels * QuantizationLevels;
    private static readonly ConditionalWeakTable<Bitmap, PaletteBox> Cache = new();

    public static CoverPalette Extract(IImage? image)
    {
        if (image is not Bitmap bitmap)
            return CoverPalette.Default;
        return Cache.GetValue(bitmap, static source => new PaletteBox(ExtractCore(source))).Value;
    }

    private static CoverPalette ExtractCore(Bitmap bitmap)
    {
        try
        {
            var width = bitmap.PixelSize.Width;
            var height = bitmap.PixelSize.Height;
            if (width <= 0 || height <= 0)
                return CoverPalette.Default;

            var stride = width * 4;
            var pixels = new byte[stride * height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(
                    new PixelRect(0, 0, width, height),
                    handle.AddrOfPinnedObject(),
                    pixels.Length,
                    stride);
            }
            finally
            {
                handle.Free();
            }

            var weights = new double[BucketCount];
            var red = new double[BucketCount];
            var green = new double[BucketCount];
            var blue = new double[BucketCount];
            double averageRed = 0, averageGreen = 0, averageBlue = 0, averageWeight = 0;
            var sampleStep = Math.Max(1, Math.Max(width, height) / 72);

            for (var y = 0; y < height; y += sampleStep)
            {
                var row = y * stride;
                for (var x = 0; x < width; x += sampleStep)
                {
                    var offset = row + x * 4;
                    var alpha = pixels[offset + 3] / 255.0;
                    if (alpha < 0.15)
                        continue;

                    var b = pixels[offset];
                    var g = pixels[offset + 1];
                    var r = pixels[offset + 2];
                    averageRed += r * alpha;
                    averageGreen += g * alpha;
                    averageBlue += b * alpha;
                    averageWeight += alpha;

                    var max = Math.Max(r, Math.Max(g, b));
                    var min = Math.Min(r, Math.Min(g, b));
                    var saturation = max == 0 ? 0 : (max - min) / (double)max;
                    var brightness = max / 255.0;
                    if (brightness < 0.07 || brightness > 0.98)
                        continue;

                    // Saturated mid-tones carry the visual identity of most covers better than
                    // black borders, white text, or tiny highlights.
                    var weight = alpha * (0.35 + saturation * 1.65) * (0.55 + brightness * 0.45);
                    var index = ((r >> 5) * QuantizationLevels + (g >> 5)) * QuantizationLevels + (b >> 5);
                    weights[index] += weight;
                    red[index] += r * weight;
                    green[index] += g * weight;
                    blue[index] += b * weight;
                }
            }

            var average = averageWeight > 0
                ? Color.FromRgb(
                    (byte)Math.Round(averageRed / averageWeight),
                    (byte)Math.Round(averageGreen / averageWeight),
                    (byte)Math.Round(averageBlue / averageWeight))
                : CoverPalette.Default.Average;

            var candidates = Enumerable.Range(0, BucketCount)
                .Where(i => weights[i] > 0)
                .OrderByDescending(i => weights[i])
                .Select(i => Enhance(Color.FromRgb(
                    (byte)Math.Round(red[i] / weights[i]),
                    (byte)Math.Round(green[i] / weights[i]),
                    (byte)Math.Round(blue[i] / weights[i]))))
                .ToList();

            var selected = new List<Color>(3);
            foreach (var candidate in candidates)
            {
                if (selected.All(existing => ColorDistance(existing, candidate) >= 52))
                    selected.Add(candidate);
                if (selected.Count == 3)
                    break;
            }

            var enhancedAverage = Enhance(average);
            if (selected.Count == 0)
                selected.Add(enhancedAverage);
            if (selected.Count == 1)
                selected.Add(ShiftHue(selected[0], 42));
            if (selected.Count == 2)
                selected.Add(ShiftHue(selected[0], -48));

            return new CoverPalette(average, selected[0], selected[1], selected[2]);
        }
        catch
        {
            return CoverPalette.Default;
        }
    }

    private static Color Enhance(Color color)
    {
        var (hue, saturation, value) = ToHsv(color);
        saturation = Math.Clamp(Math.Max(saturation, 0.34) * 1.12, 0, 0.92);
        value = Math.Clamp(value, 0.30, 0.88);
        return FromHsv(hue, saturation, value);
    }

    private static Color ShiftHue(Color color, double degrees)
    {
        var (hue, saturation, value) = ToHsv(color);
        return FromHsv((hue + degrees + 360) % 360, saturation, value);
    }

    private static double ColorDistance(Color first, Color second)
    {
        var dr = first.R - second.R;
        var dg = first.G - second.G;
        var db = first.B - second.B;
        return Math.Sqrt(dr * dr + dg * dg + db * db);
    }

    private static (double Hue, double Saturation, double Value) ToHsv(Color color)
    {
        var r = color.R / 255.0;
        var g = color.G / 255.0;
        var b = color.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var delta = max - min;
        var hue = delta == 0
            ? 0
            : max == r
                ? 60 * (((g - b) / delta) % 6)
                : max == g
                    ? 60 * ((b - r) / delta + 2)
                    : 60 * ((r - g) / delta + 4);
        if (hue < 0)
            hue += 360;
        return (hue, max == 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double hue, double saturation, double value)
    {
        var chroma = value * saturation;
        var x = chroma * (1 - Math.Abs((hue / 60) % 2 - 1));
        var m = value - chroma;
        var (r, g, b) = hue switch
        {
            < 60 => (chroma, x, 0d),
            < 120 => (x, chroma, 0d),
            < 180 => (0d, chroma, x),
            < 240 => (0d, x, chroma),
            < 300 => (x, 0d, chroma),
            _ => (chroma, 0d, x),
        };
        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }

    private sealed class PaletteBox(CoverPalette value)
    {
        public CoverPalette Value { get; } = value;
    }
}

public readonly record struct CoverPalette(Color Average, Color Primary, Color Secondary, Color Accent)
{
    public static CoverPalette Default { get; } = new(
        Color.FromRgb(24, 25, 34),
        Color.FromRgb(72, 96, 164),
        Color.FromRgb(126, 66, 160),
        Color.FromRgb(42, 142, 150));
}

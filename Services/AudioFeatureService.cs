namespace EasyDesktopLyrics.Services;

/// <summary>
/// Owns the single audio capture/FFT pipeline and turns its spectrum into reusable,
/// frame-rate-independent audio features for every visual effect.
/// </summary>
public sealed class AudioFeatureService : IDisposable
{
    private SpectrumEngine? _engine;
    private int _engineGeneration;
    private int _smoothing = 3;
    private int _bandCount = 32;
    private bool _playing;
    private bool _disposed;
    private float _low;
    private float _mid;
    private float _high;
    private float _loudness;
    private float _transient;
    private float _previousRawLoudness;

    public bool IsRunning => _engine != null;

    public AudioFeatureFrame Current { get; private set; } = AudioFeatureFrame.Empty;

    public void Configure(int smoothing, int bandCount)
    {
        _smoothing = Math.Clamp(smoothing, 1, 10);
        _bandCount = Math.Clamp(bandCount, 16, 128);
        _engine?.SetSmoothing(_smoothing);
        _engine?.SetBandCount(_bandCount);
    }

    public void SetPlaying(bool playing) => _playing = playing;

    public async Task StartAsync()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_engine != null)
            return;

        var engine = new SpectrumEngine();
        engine.SetSmoothing(_smoothing);
        engine.SetBandCount(_bandCount);
        _engine = engine;
        var generation = ++_engineGeneration;
        await engine.StartAsync().ConfigureAwait(false);

        // Capture startup can finish after the effect was disabled. Never revive that old session.
        if (generation != _engineGeneration || !ReferenceEquals(_engine, engine))
            engine.Dispose();
    }

    /// <summary>Advances all audio features exactly once for the current visual frame.</summary>
    public AudioFeatureFrame Advance(double deltaSeconds)
    {
        var engine = _engine;
        if (engine == null)
            return Current = AudioFeatureFrame.Empty;

        engine.SetPlaying(_playing);
        var spectrum = engine.GetFrame(deltaSeconds);
        var count = Math.Min(spectrum.Left.Length, spectrum.Right.Length);
        if (count == 0)
            return Current = AudioFeatureFrame.Empty;

        // SpectrumEngine uses logarithmic bands from 40 Hz to 12 kHz. These boundaries place
        // low below ~250 Hz, mid below ~3 kHz, and leave the remaining detail to high.
        var lowEnd = Math.Clamp((int)Math.Round(count * 0.32), 1, count);
        var midEnd = Math.Clamp((int)Math.Round(count * 0.76), lowEnd, count);
        var rawLow = BandEnergy(spectrum, 0, lowEnd);
        var rawMid = BandEnergy(spectrum, lowEnd, midEnd);
        var rawHigh = BandEnergy(spectrum, midEnd, count);
        var rawLoudness = Math.Clamp(rawLow * 0.34f + rawMid * 0.43f + rawHigh * 0.23f, 0, 1);
        var rawTransient = Math.Clamp((rawLoudness - _previousRawLoudness) * 7f, 0, 1);
        _previousRawLoudness = rawLoudness;

        var dt = Math.Clamp(deltaSeconds, 1.0 / 240, 0.1);
        _low = Follow(_low, rawLow, dt, 0.045, 0.24);
        _mid = Follow(_mid, rawMid, dt, 0.055, 0.22);
        _high = Follow(_high, rawHigh, dt, 0.035, 0.16);
        _loudness = Follow(_loudness, rawLoudness, dt, 0.05, 0.26);
        _transient = Follow(_transient, rawTransient, dt, 0.018, 0.14);

        return Current = new AudioFeatureFrame(
            spectrum.Left,
            spectrum.Right,
            _low,
            _mid,
            _high,
            _loudness,
            _transient,
            engine.IsReal);
    }

    public void Stop()
    {
        _engineGeneration++;
        _engine?.Dispose();
        _engine = null;
        ResetFeatures();
    }

    private static float BandEnergy(SpectrumFrame spectrum, int start, int end)
    {
        if (end <= start)
            return 0;

        double sumSquares = 0;
        for (var i = start; i < end; i++)
        {
            var value = (spectrum.Left[i] + spectrum.Right[i]) * 0.5f;
            sumSquares += value * value;
        }
        return (float)Math.Clamp(Math.Sqrt(sumSquares / (end - start)), 0, 1);
    }

    private static float Follow(float current, float target, double dt, double attack, double release)
    {
        var timeConstant = target > current ? attack : release;
        var amount = 1 - Math.Exp(-dt / timeConstant);
        return (float)(current + (target - current) * amount);
    }

    private void ResetFeatures()
    {
        _low = 0;
        _mid = 0;
        _high = 0;
        _loudness = 0;
        _transient = 0;
        _previousRawLoudness = 0;
        Current = AudioFeatureFrame.Empty;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }
}

/// <summary>
/// Shared per-frame audio snapshot. Spectrum arrays are owned and reused by the service;
/// consumers must finish reading them before the next Advance call.
/// </summary>
public readonly record struct AudioFeatureFrame(
    float[] Left,
    float[] Right,
    float LowEnergy,
    float MidEnergy,
    float HighEnergy,
    float Loudness,
    float Transient,
    bool IsReal)
{
    public static AudioFeatureFrame Empty { get; } = new(
        Array.Empty<float>(),
        Array.Empty<float>(),
        0,
        0,
        0,
        0,
        0,
        false);
}

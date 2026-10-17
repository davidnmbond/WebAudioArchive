using NAudio.Dsp;

namespace WebAudioArchive.Analysis.Decode;

/// <summary>
/// Interleaved full-rate float PCM to 16 kHz mono for whisper: channels averaged, then NAudio's WDL resampler in sinc
/// mode (64 taps). Measured: level within 0.1 dB up to 6 kHz, -1.5 dB at 7 kHz, and tones that would alias attenuated
/// by 67 dB at 10 kHz and 114 dB at 12 kHz. (WdlResamplingSampleProvider's default, linear interpolation with IIR
/// filters, loses 8 dB at 6 kHz and lets a 10 kHz tone through at -27 dB, so it is not used.)
/// </summary>
public sealed class SpeechResampler
{
    readonly WdlResampler _wdl = new();
    readonly int _channels;
    readonly double _ratio;
    float[] _out = new float[4096];

    public SpeechResampler(int inputRate, int channels, int outputRate)
    {
        _channels = channels;
        _ratio = (double)outputRate / inputRate;
        _wdl.SetMode(true, 0, true, 64, 32);
        _wdl.SetFeedMode(true);
        _wdl.SetRates(inputRate, outputRate);
    }

    /// <summary>Samples returned so far.</summary>
    public long Produced { get; private set; }

    /// <summary>Takes interleaved frames and returns the mono output they produced (valid until the next call).</summary>
    public ReadOnlySpan<float> Push(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / _channels;
        if (frames == 0) return [];
        var wanted = _wdl.ResamplePrepare(frames, 1, out var input);
        for (var i = 0; i < wanted; i++)
        {
            var sum = 0f;
            for (var c = 0; c < _channels; c++) sum += interleaved[i * _channels + c];
            input[i] = sum / _channels;
        }

        var capacity = (int)(frames * _ratio) + 64;
        if (_out.Length < capacity) _out = new float[capacity * 2];
        var got = _wdl.ResampleOut(_out, wanted, capacity, 1);
        Produced += got;
        return _out.AsSpan(0, got);
    }

    /// <summary>Feeds silence until <paramref name="total"/> samples exist (the filter delay), returning only the samples still owed.</summary>
    public ReadOnlySpan<float> Flush(long total)
    {
        var owed = new List<float>();
        var silence = new float[256 * _channels];
        while (Produced < total)
        {
            var before = Produced;
            var chunk = Push(silence);
            var keep = (int)Math.Min(chunk.Length, total - before);
            for (var i = 0; i < keep; i++) owed.Add(chunk[i]);
            if (Produced >= total) Produced = total;
        }

        return owed.ToArray();
    }
}

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace WebAudioArchive.Analysis.Audio;

/// <summary>A run of consecutive samples on one channel at or above a level, by absolute value. StartFrame counts sample frames from the start of the audio.</summary>
public sealed record ClipRun(long StartFrame, int Length, int Channel, float Peak);

/// <summary>Runs kept at one level. Count is every run seen; Runs holds at most ClipLevels.MaxRunsPerLevel of them.</summary>
public sealed record ClipLevelRuns(double Level, long Count, bool Truncated, IReadOnlyList<ClipRun> Runs);

/// <summary>
/// features.json: what the audio detectors need, kept so they can be rerun without decoding again. Window levels are
/// the loudest channel's RMS over 50 ms, in tenths of a dBFS (digital silence is -120.0).
/// </summary>
public sealed record AudioFeatures(
    int Schema,
    int SampleRate,
    int Channels,
    long Frames,
    double WindowSeconds,
    IReadOnlyList<short> WindowDbfsTenths,
    IReadOnlyList<ClipLevelRuns> Clips)
{
    public const int CurrentSchema = 1;

    [JsonIgnore]
    public double Seconds => SampleRate == 0 ? 0 : (double)Frames / SampleRate;

    public double WindowDbfs(int i) => WindowDbfsTenths[i] / 10.0;
}

/// <summary>
/// Streams interleaved float PCM (from the decoder, or as little-endian f32 bytes) into window levels and clip runs. Reads may split
/// a sample, a frame or a run at any byte; the result is the same as for one read.
/// </summary>
public sealed class FeatureExtractor
{
    public const double WindowSeconds = 0.05;

    readonly int _rate;
    readonly int _channels;
    readonly int _windowFrames;
    readonly double[] _sumSquares;
    readonly List<short> _windows = [];
    readonly long[,] _runStart;
    readonly int[,] _runLength;
    readonly float[,] _runPeak;
    readonly int[] _open;
    readonly List<ClipRun>[] _runs;
    readonly long[] _counts;
    readonly bool[] _truncated;
    readonly byte[] _carry = new byte[4];
    int _carryLength;
    int _windowFill;
    long _frame;
    int _channel;
    bool _completed;

    public FeatureExtractor(int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is <= 0 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("f32le input needs a little-endian machine");
        _rate = sampleRate;
        _channels = channels;
        _windowFrames = Math.Max(1, (int)Math.Round(sampleRate * WindowSeconds));
        _sumSquares = new double[channels];
        var levels = ClipLevels.Levels.Length;
        _runStart = new long[channels, levels];
        _runLength = new int[channels, levels];
        _runPeak = new float[channels, levels];
        _open = new int[channels];
        _runs = Enumerable.Range(0, levels).Select(_ => new List<ClipRun>()).ToArray();
        _counts = new long[levels];
        _truncated = new bool[levels];
    }

    public void Push(ReadOnlySpan<float> interleaved)
    {
        if (_completed) throw new InvalidOperationException("Complete has been called");
        var lowest = ClipLevels.Levels[0];
        foreach (var raw in interleaved)
        {
            var v = float.IsFinite(raw) ? raw : 0f;
            var a = Math.Abs(v);
            _sumSquares[_channel] += (double)v * v;
            if (a >= lowest || _open[_channel] > 0) TrackRuns(_channel, a);
            if (++_channel == _channels)
            {
                _channel = 0;
                _frame++;
                if (++_windowFill == _windowFrames) FlushWindow();
            }
        }
    }

    /// <summary>Little-endian 32-bit floats; a float split across reads is carried to the next read.</summary>
    public void PushBytes(ReadOnlySpan<byte> bytes)
    {
        if (_carryLength > 0)
        {
            var take = Math.Min(4 - _carryLength, bytes.Length);
            bytes[..take].CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += take;
            bytes = bytes[take..];
            if (_carryLength < 4) return;
            Push([BinaryPrimitives.ReadSingleLittleEndian(_carry)]);
            _carryLength = 0;
        }

        var whole = bytes.Length / 4 * 4;
        Push(MemoryMarshal.Cast<byte, float>(bytes[..whole]));
        bytes[whole..].CopyTo(_carry);
        _carryLength = bytes.Length - whole;
    }

    public AudioFeatures Complete()
    {
        if (_completed) throw new InvalidOperationException("Complete has been called");
        _completed = true;
        if (_windowFill > 0) FlushWindow();
        for (var c = 0; c < _channels; c++)
            for (var l = 0; l < ClipLevels.Levels.Length; l++)
                if (_runLength[c, l] > 0) Close(c, l);

        var clips = ClipLevels.Levels
            .Select((level, l) => new ClipLevelRuns(level, _counts[l], _truncated[l], _runs[l].OrderBy(r => r.StartFrame).ThenBy(r => r.Channel).ToList()))
            .ToList();
        return new AudioFeatures(AudioFeatures.CurrentSchema, _rate, _channels, _frame, WindowSeconds, _windows.ToList(), clips);
    }

    void TrackRuns(int c, float a)
    {
        for (var l = 0; l < ClipLevels.Levels.Length; l++)
        {
            if (a >= ClipLevels.Levels[l])
            {
                if (_runLength[c, l] == 0)
                {
                    _runStart[c, l] = _frame;
                    _runPeak[c, l] = 0;
                    _open[c]++;
                }

                _runLength[c, l]++;
                if (a > _runPeak[c, l]) _runPeak[c, l] = a;
            }
            else if (_runLength[c, l] > 0)
            {
                Close(c, l);
            }
        }
    }

    void Close(int c, int l)
    {
        var length = _runLength[c, l];
        _runLength[c, l] = 0;
        _open[c]--;
        if (length < ClipLevels.MinStoredRun) return;
        _counts[l]++;
        if (_runs[l].Count < ClipLevels.MaxRunsPerLevel) _runs[l].Add(new ClipRun(_runStart[c, l], length, c, _runPeak[c, l]));
        else _truncated[l] = true;
    }

    void FlushWindow()
    {
        double loudest = 0;
        for (var c = 0; c < _channels; c++)
        {
            loudest = Math.Max(loudest, _sumSquares[c] / _windowFill);
            _sumSquares[c] = 0;
        }

        var db = loudest <= 1e-12 ? -120.0 : Math.Clamp(10 * Math.Log10(loudest), -120.0, 20.0);
        _windows.Add((short)Math.Round(db * 10));
        _windowFill = 0;
    }
}

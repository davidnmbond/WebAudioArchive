using System.Text;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Core.Icy;

public delegate void AudioHandler(ReadOnlySpan<byte> audio);

/// <param name="Title">Stream title as sent by the server.</param>
/// <param name="AtAudioByte">Count of audio bytes (metadata excluded) received before this title.</param>
public sealed record IcyTitle(string Title, long AtAudioByte);

/// <summary>Removes interleaved ICY metadata blocks from a stream, leaving the audio bytes untouched.</summary>
public sealed partial class IcyDemuxer(int metaInterval)
{
    long _audioTotal;
    readonly int _metaInt = metaInterval;
    int _audioLeft = metaInterval;
    int _metaLeft = -1; // -1: the next byte is the length byte
    byte[] _meta = [];
    int _metaFilled;
    string? _lastTitle;

    public long AudioBytes => _audioTotal;

    public void Push(ReadOnlySpan<byte> data, AudioHandler onAudio, Action<IcyTitle> onTitle)
    {
        if (_metaInt <= 0)
        {
            _audioTotal += data.Length;
            onAudio(data);
            return;
        }

        while (!data.IsEmpty)
        {
            if (_audioLeft > 0)
            {
                var n = Math.Min(_audioLeft, data.Length);
                onAudio(data[..n]);
                _audioTotal += n;
                _audioLeft -= n;
                data = data[n..];
            }
            else if (_metaLeft < 0)
            {
                var length = data[0] * 16;
                data = data[1..];
                if (length == 0)
                {
                    _audioLeft = _metaInt;
                }
                else
                {
                    _metaLeft = length;
                    _meta = new byte[length];
                    _metaFilled = 0;
                }
            }
            else
            {
                var n = Math.Min(_metaLeft, data.Length);
                data[..n].CopyTo(_meta.AsSpan(_metaFilled));
                _metaFilled += n;
                _metaLeft -= n;
                data = data[n..];
                if (_metaLeft == 0)
                {
                    HandleMetadata(onTitle);
                    _metaLeft = -1;
                    _audioLeft = _metaInt;
                }
            }
        }
    }

    void HandleMetadata(Action<IcyTitle> onTitle)
    {
        var text = Encoding.UTF8.GetString(_meta).TrimEnd('\0');
        var m = TitlePattern().Match(text);
        if (!m.Success) return;
        var title = m.Groups["t"].Value.Trim();
        if (title.Length == 0 || title == _lastTitle) return;
        _lastTitle = title;
        onTitle(new IcyTitle(title, _audioTotal));
    }

    [GeneratedRegex("StreamTitle='(?<t>.*?)';", RegexOptions.Singleline)]
    private static partial Regex TitlePattern();
}

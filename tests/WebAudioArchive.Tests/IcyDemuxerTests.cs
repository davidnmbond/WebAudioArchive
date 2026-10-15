using WebAudioArchive.Core.Icy;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class IcyDemuxerTests
{
    static (byte[] Audio, List<IcyTitle> Titles) Demux(byte[] wire, int metaInt, int chunk)
    {
        var audio = new MemoryStream();
        var titles = new List<IcyTitle>();
        var d = new IcyDemuxer(metaInt);
        AudioHandler onAudio = a => audio.Write(a);
        for (var i = 0; i < wire.Length; i += chunk)
            d.Push(wire.AsSpan(i, Math.Min(chunk, wire.Length - i)), onAudio, titles.Add);
        return (audio.ToArray(), titles);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1_000_000)]
    public void Titles_and_audio_survive_any_chunking(int chunk)
    {
        var audio = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var wire = IcyTestData.Build(audio, metaInt: 16, block => block switch
        {
            0 => "Artist - Song",
            2 => "Artist - Song",      // repeat of the last title: must be reported once
            3 => "Caf\u00e9 \u2013 Two", // multi-byte UTF-8
            _ => null,
        });

        var (recovered, titles) = Demux(wire, 16, chunk);

        Assert.Equal(audio, recovered);
        Assert.Collection(titles,
            t => { Assert.Equal("Artist - Song", t.Title); Assert.Equal(16, t.AtAudioByte); },
            t => { Assert.Equal("Caf\u00e9 \u2013 Two", t.Title); Assert.Equal(64, t.AtAudioByte); });
    }

    [Fact]
    public void Zero_meta_interval_is_passthrough()
    {
        var audio = new byte[] { 1, 2, 3, 4, 5 };
        var (recovered, titles) = Demux(audio, 0, 2);

        Assert.Equal(audio, recovered);
        Assert.Empty(titles);
    }

    [Fact]
    public void Empty_title_is_ignored()
    {
        var audio = new byte[32];
        var wire = IcyTestData.Build(audio, 16, block => block == 0 ? "" : null);

        var (_, titles) = Demux(wire, 16, 5);

        Assert.Empty(titles);
    }
}

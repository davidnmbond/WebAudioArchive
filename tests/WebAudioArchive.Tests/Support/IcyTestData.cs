using System.Text;

namespace WebAudioArchive.Tests.Support;

public static class IcyTestData
{
    /// <summary>Interleave metadata after every full metaInt bytes. A null title writes the one-byte "no change" block.</summary>
    public static byte[] Build(byte[] audio, int metaInt, Func<int, string?> titleForBlock)
    {
        var ms = new MemoryStream();
        var block = 0;
        for (var pos = 0; pos < audio.Length; pos += metaInt)
        {
            var n = Math.Min(metaInt, audio.Length - pos);
            ms.Write(audio, pos, n);
            if (n < metaInt) break;

            var title = titleForBlock(block++);
            if (title is null)
            {
                ms.WriteByte(0);
                continue;
            }

            var text = Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
            var padded = (text.Length + 15) / 16 * 16;
            ms.WriteByte((byte)(padded / 16));
            ms.Write(text);
            ms.Write(new byte[padded - text.Length]);
        }

        return ms.ToArray();
    }
}

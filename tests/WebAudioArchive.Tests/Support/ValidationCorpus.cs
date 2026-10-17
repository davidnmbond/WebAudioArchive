using WebAudioArchive.Analysis.Validation;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// A labelled synthetic sample (issue #84): no broadcast audio. Audio: 180 s of speech-like signal with planted
/// clipping (hard-clipped tone bursts of 20 to 200 ms) and dead air (5.5 to 15 s of silence), plus near misses that
/// must not be detected: a loud clean tone peaking at 0.9, a 3.5 s pause and 10 s of quiet room tone at -45 dBFS. Speech: lines
/// with every swear and filler spelling the detectors claim to handle and the near misses they must ignore.
/// </summary>
public static class ValidationCorpus
{
    public static (float[] Mono, List<Labelled> Labels) Audio(int seed, DateTimeOffset start)
    {
        var rng = new Random(seed);
        var parts = new List<float[]>();
        var labels = new List<Labelled>();
        var at = 0.0;
        void Add(float[] p) { parts.Add(p); at += p.Length / (double)Signals.Rate; }
        void Label(string type, double from, double to) => labels.Add(new Labelled(type, start.AddSeconds(from), start.AddSeconds(to), null));

        Add(Signals.SpeechLike(10, seed));
        for (var i = 0; i < 6; i++)
        {
            var burst = 0.02 + rng.NextDouble() * 0.18;
            var loud = Signals.Sine(burst, 440 + rng.Next(0, 400), 1.4).Select(x => Math.Clamp(x, -1f, 1f)).ToArray();
            Label(EventTypes.Clipping, at, at + burst);
            Add(loud);
            Add(Signals.SpeechLike(6 + rng.NextDouble() * 4, seed + i + 1));
            if (i % 2 == 0)
            {
                var silence = 5.5 + rng.NextDouble() * 9.5;
                Label(EventTypes.DeadAir, at, at + silence);
                Add(Signals.Silence(silence));
                Add(Signals.Sine(1, 330, 0.3));
            }
        }

        Add(Signals.Sine(3, 523, 0.9));
        Add(Signals.SpeechLike(4, seed + 20));
        Add(Signals.Sine(0.5, 330, 0.3));
        Add(Signals.Silence(3.5));
        Add(Signals.Sine(1, 330, 0.3));
        Add(Signals.Noise(10, -45, seed + 30));
        Add(Signals.Sine(1, 330, 0.3));
        var total = (int)(180 * Signals.Rate);
        var mono = Signals.Concat([.. parts]);
        if (mono.Length < total) mono = Signals.Concat(mono, Signals.SpeechLike((total - mono.Length) / (double)Signals.Rate, seed + 40));
        return (mono, labels);
    }

    public static (TranscriptDocument Transcript, List<Labelled> Labels) Speech(DateTimeOffset start)
    {
        // Each line: the text, the expected detections (type and word index), and whether the line is suppressed.
        (string Text, (string Type, int Word)[] Expected, string? Suppressed)[] lines =
        [
            ("Well that was shit, honestly", [(EventTypes.Swear, 3)], null),
            ("FUCKED it up again, um, sorry", [(EventTypes.Swear, 0), (EventTypes.Filler, 4)], null),
            ("what a bull-shit decision", [(EventTypes.Swear, 2)], null),
            ("f***ing hell, er, right", [(EventTypes.Swear, 0), (EventTypes.Filler, 2)], null),
            ("Scunthorpe beat Dickens Athletic in the cocktail cup", [], null),
            ("I like it, I like it a lot", [], null),
            ("Ummm, erm, hmm, ahh", [(EventTypes.Filler, 0), (EventTypes.Filler, 1), (EventTypes.Filler, 2), (EventTypes.Filler, 3)], null),
            ("we assess the class of shitake knobs", [], null),
            ("fuck um fuck um fuck um", [], "repetition"),
            ("you bloody legend, uhh", [(EventTypes.Swear, 1), (EventTypes.Filler, 3)], null),
            ("the summer era umbrella error", [], null),
            ("Motherfuckers! said the lyric", [(EventTypes.Swear, 0)], null),
        ];

        var transcript = TranscriptFixtures.OfSegments(start, lines.Select(l => (l.Text, l.Suppressed)).ToArray());
        var labels = new List<Labelled>();
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (type, word) in lines[i].Expected)
            {
                var w = transcript.Segments[i].Words[word];
                labels.Add(new Labelled(type, start.AddSeconds(w.Start), start.AddSeconds(w.End), w.Text));
            }
        }

        return (transcript, labels);
    }
}

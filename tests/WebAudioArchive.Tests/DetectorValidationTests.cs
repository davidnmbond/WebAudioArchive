using Xunit.Abstractions;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Validation;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>
/// Issue #84 on synthetic data. The tests print the score tables; docs/analysis-validation.md records them. Real Burst
/// audio is scored by the owner with --validate (labels never committed).
/// </summary>
public sealed class DetectorValidationTests(ITestOutputHelper output) : IDisposable
{
    const string DocPath = "docs/analysis-validation.md";
    static readonly DateTimeOffset T = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    static readonly LoadedConfig Config = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json"));
    static readonly Watershed Uk = Watershed.From(new SwearOptions(), TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
    readonly string _dir = Directory.CreateTempSubdirectory("waa-valid-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static TranscriptDocument Empty(double seconds) => TranscriptFixtures.Of(T) with { AudioSeconds = seconds };

    static IReadOnlyList<AnalysisEvent> AudioEvents(AudioFeatures f, LoadedConfig config) =>
        DetectorRunner.Run(Empty(f.Seconds), f, config, Uk, T).Events;

    [Fact]
    public void Scoring_matches_each_label_once_within_the_tolerance()
    {
        Labelled[] labels = [new("swear", T, T.AddSeconds(1), null), new("swear", T.AddSeconds(10), T.AddSeconds(11), null)];
        AnalysisEvent[] detected =
        [
            Event("swear", T.AddSeconds(0.2)), Event("swear", T.AddSeconds(0.3)), Event("swear", T.AddSeconds(30)), Event("filler", T.AddSeconds(10)),
        ];

        var swear = DetectorScore.Compare(labels, detected, TimeSpan.FromSeconds(0.5)).Single(s => s.Type == "swear");

        Assert.Equal((1, 2, 1), (swear.TruePositives, swear.FalsePositives, swear.FalseNegatives));
        Assert.Equal(1.0 / 3, swear.Precision!.Value, 6);
        Assert.Equal(0.5, swear.Recall!.Value, 6);
        Assert.Contains("| swear | 1 | 2 | 1 | 0.33 | 0.50 |", DetectorScore.Table([swear]));
    }

    [Fact]
    public void Suppressed_swear_events_are_excluded_from_precision_and_recall_and_counted_separately()
    {
        Labelled[] labels = [new("swear", T, T.AddSeconds(1), null)];
        AnalysisEvent[] detected =
        [
            Event("swear", T.AddSeconds(0.2)),
            Event("swear", T.AddSeconds(50), suppressed: "repetition"),
            Event("swear", T.AddSeconds(51), suppressed: "repetition"),
            Event("swear", T.AddSeconds(0.1), suppressed: "repetition"),
        ];

        var swear = DetectorScore.Compare(labels, detected, TimeSpan.FromSeconds(0.5)).Single();

        Assert.Equal((1, 0, 0, 3), (swear.TruePositives, swear.FalsePositives, swear.FalseNegatives, swear.ReviewOnly));
        Assert.Equal(1.0, swear.Precision);
        Assert.Equal(1.0, swear.Recall);
        var table = DetectorScore.Table([swear]);
        Assert.Contains("Review only", table);
        Assert.Contains("| swear | 1 | 0 | 0 | 1.00 | 1.00 | 3 |", table);

        // A suppressed event that sits on a label must not rescue a missed label either.
        var missed = DetectorScore.Compare(labels, [Event("swear", T.AddSeconds(0.1), suppressed: "repetition")], TimeSpan.FromSeconds(0.5)).Single();
        Assert.Equal((0, 0, 1, 1), (missed.TruePositives, missed.FalsePositives, missed.FalseNegatives, missed.ReviewOnly));
    }

    [Fact]
    public void Labels_csv_is_read_in_utc()
    {
        var path = Path.Combine(_dir, "labels.csv");
        File.WriteAllText(path, "type,startUtc,endUtc,note\nfiller,2026-10-16T09:12:03.4Z,2026-10-16T09:12:03.8Z,um\nswear,2026-10-16T09:20:00Z,2026-10-16T09:20:01Z,\n");

        var labels = DetectorScore.ReadCsv(path);

        Assert.Equal(2, labels.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 9, 12, 3, 400, TimeSpan.Zero), labels[0].StartUtc);
        Assert.Equal("um", labels[0].Note);
        Assert.Null(labels[1].Note);
        File.WriteAllText(path, "type,startUtc,endUtc,note\nfiller,09:12,09:13,um\n");
        Assert.Throws<FormatException>(() => DetectorScore.ReadCsv(path));
    }

    [Fact]
    public void The_committed_example_labels_file_is_synthetic_and_scores_against_a_synthetic_detection_set()
    {
        var path = Path.Combine(RepoFiles.Root, "docs", "analysis-validation-labels.example.csv");
        var labels = DetectorScore.ReadCsv(path);

        Assert.NotEmpty(labels);
        Assert.All(labels, l => Assert.True(l.StartUtc <= l.EndUtc));
        Assert.Contains("synthetic", File.ReadAllText(path), StringComparison.OrdinalIgnoreCase);
        var types = labels.Select(l => l.Type).Distinct().Order(StringComparer.Ordinal).ToArray();
        Assert.Equal([EventTypes.Clipping, EventTypes.DeadAir, EventTypes.Filler, EventTypes.Swear], types);
        var detected = labels.Select(l => Event(l.Type, l.StartUtc)).ToList();

        var scores = DetectorScore.Compare(labels, detected, TimeSpan.FromSeconds(1));

        Assert.All(scores, s => Assert.Equal(1.0, s.Recall));
    }

    [Fact]
    public void The_report_scores_audio_and_transcript_types_with_their_tolerances_and_shows_the_review_count()
    {
        Labelled[] labels = [new(EventTypes.Swear, T, T.AddSeconds(1), null), new(EventTypes.DeadAir, T, T.AddSeconds(6), null)];
        AnalysisEvent[] detected = [Event(EventTypes.Swear, T), Event(EventTypes.Swear, T.AddSeconds(100), suppressed: "repetition"), Event(EventTypes.DeadAir, T)];

        var report = DetectorScore.Report(labels, detected, T, T.AddHours(1));

        Assert.Contains("2 labels, 3 detections (1 review only)", report);
        Assert.Contains("Audio events (0.25 s tolerance)", report);
        Assert.Contains("Transcript events (1 s tolerance", report);
        Assert.Contains("| swear | 1 | 0 | 0 | 1.00 | 1.00 | 1 |", report);
        Assert.Contains("| dead-air | 1 | 0 | 0 | 1.00 | 1.00 | 0 |", report);
    }

    [Fact]
    public void Documented_thresholds_match_config_analysis_json()
    {
        var doc = RepoFiles.Read(DocPath).Replace("\r\n", "\n");
        const string begin = "<!-- thresholds:begin -->\n";
        const string end = "<!-- thresholds:end -->";
        var from = doc.IndexOf(begin, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{DocPath} has no '{begin.Trim()}' marker");
        var to = doc.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, $"{DocPath} has no '{end}' marker");

        var documented = doc[(from + begin.Length)..to];
        var expected = ThresholdsDoc.Render(Config);

        Assert.True(documented == expected,
            $"{DocPath} thresholds drifted from config/analysis/analysis.json. Replace the block between the markers with:\n{expected}\n--- documented was:\n{documented}");
    }

    [Fact]
    public void Transcript_detectors_score_perfectly_on_the_labelled_lines_and_report_the_suppressed_run_separately()
    {
        var (transcript, labels) = ValidationCorpus.Speech(T);
        var events = DetectorRunner.Run(transcript, new FeatureExtractor(44100, 1).Complete(), Config, Uk, T).Events;

        var scores = DetectorScore.Compare(labels, events, TimeSpan.FromSeconds(0.1));
        output.WriteLine(DetectorScore.Table(scores));

        foreach (var s in scores.Where(s => s.Type is EventTypes.Swear or EventTypes.Filler))
        {
            Assert.Equal(1.0, s.Precision);
            Assert.Equal(1.0, s.Recall);
        }

        Assert.True(scores.Single(s => s.Type == EventTypes.Swear).ReviewOnly >= 1, "the suppressed repeated-swear line should be reported as review only");
        Assert.Equal(0, scores.Single(s => s.Type == EventTypes.Filler).ReviewOnly);
    }

    [Fact]
    public void Audio_detectors_score_perfectly_on_the_planted_signal_without_a_codec()
    {
        var (mono, labels) = ValidationCorpus.Audio(seed: 11, T);
        var fx = new FeatureExtractor(Signals.Rate, 1);
        fx.Push(mono);

        var scores = DetectorScore.Compare(labels, AudioEvents(fx.Complete(), Config), TimeSpan.FromSeconds(0.25));
        output.WriteLine("PCM, configured thresholds\n" + DetectorScore.Table(scores));

        foreach (var s in scores)
        {
            Assert.Equal(1.0, s.Precision);
            Assert.Equal(1.0, s.Recall);
        }
    }

    [Fact]
    public async Task Audio_detectors_after_the_mp3_codec_are_measured()
    {
        var (mono, labels) = ValidationCorpus.Audio(seed: 11, T);
        var mp3 = Path.Combine(_dir, "corpus.mp3");
        await TestMp3.EncodeMp3Async(mp3, Signals.Stereo(mono), 2);
        var scratch = new ScratchSpace(Path.Combine(_dir, "scratch"), 512L << 20);
        using var lease = scratch.Acquire("validation", SegmentDecoder.ScratchBytesFor(new FileInfo(mp3).Length, 180));
        var features = (await new SegmentDecoder().DecodeAsync(mp3, new FileInfo(mp3).Length, 44100, 2, lease, CancellationToken.None)).Features;

        var configured = DetectorScore.Compare(labels, AudioEvents(features, Config), TimeSpan.FromSeconds(0.25));
        var lenient = Config with { Config = Config.Config with { Clipping = Config.Config.Clipping with { Threshold = 0.98, MinRunSamples = 2 } } };
        var atLowerLevel = DetectorScore.Compare(labels, AudioEvents(features, lenient), TimeSpan.FromSeconds(0.25));
        output.WriteLine("MP3 128 kbps, configured thresholds (clipping 0.99, 3 samples)\n" + DetectorScore.Table(configured));
        output.WriteLine("MP3 128 kbps, clipping 0.98, 2 samples\n" + DetectorScore.Table(atLowerLevel));

        var dead = configured.Single(s => s.Type == EventTypes.DeadAir);
        Assert.Equal(1.0, dead.Precision);
        Assert.Equal(1.0, dead.Recall);
        var clip = atLowerLevel.Single(s => s.Type == EventTypes.Clipping);
        Assert.Equal(1.0, clip.Precision);
        Assert.True(clip.Recall >= 0.9, $"clipping recall after MP3 at 0.98 was {clip.Recall:0.00}");
    }

    static AnalysisEvent Event(string type, DateTimeOffset at, string? suppressed = null) => new(
        AnalysisEvent.MakeId(type, "a/x/y/z", (at - T).TotalSeconds, suppressed), type, at, at.AddSeconds(0.4), Severities.Low, 0.9, type, "1",
        EventSources.Transcript, "a", "a/x/y/z", 0, 0.4, null,
        suppressed is null ? null : new Dictionary<string, string> { ["suppressed"] = suppressed });
}

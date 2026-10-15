using System.Globalization;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web.Schedule;

public enum ShowState
{
    /// <summary>Starts after now.</summary>
    Future,

    /// <summary>On air now; coverage is measured up to now and counts the open .part.</summary>
    InProgress,

    /// <summary>Over, and at least one recorder has all of it.</summary>
    Full,

    /// <summary>Over, and some of it is recorded but no single recorder has all of it.</summary>
    Partial,

    /// <summary>Over, and nothing of it is recorded.</summary>
    NotRecorded,
}

/// <summary>One recorder's coverage of a show (up to now for a show on air). Gaps are the uncovered stretches, UTC.</summary>
public sealed record RecorderCoverage(string Recorder, double CoveredSeconds, double WindowSeconds, IReadOnlyList<Interval> Gaps, bool IncludesOpenSegment)
{
    public bool IsFull => WindowSeconds > 0 && CoveredSeconds >= WindowSeconds - ShowCoverage.ToleranceSeconds;

    public double Fraction => WindowSeconds <= 0 ? 0 : Math.Min(1, CoveredSeconds / WindowSeconds);
}

public sealed record ShowCoverageResult(ShowOccurrence Show, ShowState State, DateTimeOffset MeasuredTo, IReadOnlyList<RecorderCoverage> Recorders)
{
    public IReadOnlyList<RecorderCoverage> FullRecorders => Recorders.Where(r => r.IsFull).ToList();

    public string CssClass => State switch
    {
        ShowState.Full => "show-full",
        ShowState.Partial => "show-partial",
        ShowState.NotRecorded => "show-missing",
        ShowState.InProgress => "show-live",
        _ => "show-future",
    };

    public string Icon => State switch
    {
        ShowState.Full => "fas fa-check-circle",
        ShowState.Partial => "fas fa-adjust",
        ShowState.NotRecorded => "fas fa-times-circle",
        ShowState.InProgress => "fas fa-circle",
        _ => "far fa-clock",
    };

    public string Label => State switch
    {
        ShowState.Full => "Recorded",
        ShowState.Partial => "Partly recorded",
        ShowState.NotRecorded => "Not recorded",
        ShowState.InProgress => "On air now",
        _ => "In the future",
    };

    /// <summary>A one-line description, such as "Recorded in full by A and B".</summary>
    public string Summary
    {
        get
        {
            var full = FullRecorders.Select(r => r.Recorder.ToUpperInvariant()).ToList();
            var parts = string.Join(", ", Recorders.Select(r =>
                string.Create(CultureInfo.InvariantCulture, $"{r.Recorder.ToUpperInvariant()} {r.Fraction:0%}")));
            return State switch
            {
                ShowState.Full => $"Recorded in full by {string.Join(" and ", full)} ({parts})",
                ShowState.Partial => $"Partly recorded ({parts}); no single recorder has all of it",
                ShowState.NotRecorded => "Not recorded by any recorder",
                ShowState.InProgress => full.Count > 0
                    ? $"On air now; being recorded by {string.Join(" and ", full)} so far ({parts})"
                    : $"On air now; so far {parts}",
                _ => "In the future: not yet broadcast",
            };
        }
    }
}

/// <summary>Classifies a scheduled show against the catalog: fully, partly or not recorded, in progress, or in the future.</summary>
public static class ShowCoverage
{
    /// <summary>Coverage within this many seconds of the whole window counts as full (frame rounding and the hourly roll).</summary>
    public const double ToleranceSeconds = 2;

    public static ShowCoverageResult Classify(ShowOccurrence show, IReadOnlyList<RecorderSegments> recorders, DateTimeOffset now)
    {
        if (now < show.StartUtc)
            return new ShowCoverageResult(show, ShowState.Future, show.StartUtc, recorders.Select(r => new RecorderCoverage(r.Recorder, 0, 0, [], false)).ToList());

        var to = now < show.EndUtc ? now : show.EndUtc;
        var from = show.StartUtc;
        var list = new List<RecorderCoverage>();
        foreach (var r in recorders)
        {
            var relevant = r.Segments.Where(s => s.EndUtc > from && PositionModel.AudioStartUtc(s) < to).ToList();
            var covered = WindowCoverage.Covered(relevant, from, to);
            list.Add(new RecorderCoverage(r.Recorder, covered.Sum(i => i.Seconds), (to - from).TotalSeconds, Uncovered(covered, from, to),
                relevant.Any(s => s.IsOpen)));
        }

        ShowState state;
        if (now < show.EndUtc) state = ShowState.InProgress;
        else if (list.Any(r => r.IsFull)) state = ShowState.Full;
        else if (list.Any(r => r.CoveredSeconds >= 1)) state = ShowState.Partial;
        else state = ShowState.NotRecorded;
        return new ShowCoverageResult(show, state, to, list);
    }

    /// <summary>The stretches of [from, to) not in <paramref name="covered"/>, ignoring ones under the tolerance.</summary>
    public static IReadOnlyList<Interval> Uncovered(IReadOnlyList<Interval> covered, DateTimeOffset from, DateTimeOffset to)
    {
        var gaps = new List<Interval>();
        var cursor = from;
        foreach (var c in covered.OrderBy(c => c.Start))
        {
            if (c.Start > cursor) gaps.Add(new Interval(cursor, c.Start));
            if (c.End > cursor) cursor = c.End;
        }

        if (to > cursor) gaps.Add(new Interval(cursor, to));
        return gaps.Where(g => g.Seconds >= ToleranceSeconds).ToList();
    }
}

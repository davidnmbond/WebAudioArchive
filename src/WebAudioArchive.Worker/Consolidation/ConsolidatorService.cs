using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebAudioArchive.Core.Consolidation;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Worker.Consolidation;

/// <summary>One source file of a failed hour, as it was when the hour failed.</summary>
public sealed record FailureSource(string Recorder, string File, string Sha256);

/// <summary>An hour whose merged copy failed verification, stored in &lt;merged&gt;/&lt;station&gt;/failures.json.</summary>
public sealed record ConsolidationFailure(DateTimeOffset HourUtc, string Reason, DateTimeOffset Utc, IReadOnlyList<FailureSource> Sources);

/// <summary>An hour that was planned and held no frame from either recorder, stored in &lt;merged&gt;/&lt;station&gt;/empty.json with its sources then.</summary>
public sealed record EmptyHour(DateTimeOffset HourUtc, DateTimeOffset Utc, IReadOnlyList<FailureSource> Sources);

public sealed record FailureSummary(DateTimeOffset HourUtc, string Reason);

/// <summary>What /healthz reports. Failures are reported here, never as an unhealthy status.</summary>
public sealed record ConsolidatorHealth(
    string Station,
    string Mode,
    DateTimeOffset? LastMergedHourUtc,
    int BacklogHours,
    int WaitingForAnalysis,
    IReadOnlyList<FailureSummary> Failures,
    int Held,
    int Ready,
    int HeldFailed,
    long DeletedBytes,
    DateTimeOffset? LastRunUtc,
    string? LastError);

public sealed record DeletionItem(string? Recorder, string? File);

public sealed record DeletionRequest(string? ConfirmedBy, IReadOnlyList<DeletionItem>? Items);

/// <summary>One requested deletion: deleted, or the reason it was not.</summary>
public sealed record DeletionResult(string? Recorder, string? File, bool Deleted, string? Reason);

public sealed record RetryRequest(DateTimeOffset? HourUtc);

/// <summary>
/// Issue #104: at start and then hourly, finishes interrupted moves, merges the eligible hours oldest first, and in hold
/// mode moves originals whose frames are all merged to held/, then rechecks held originals. One run at a time; an open
/// segment (.part) is never read. Deletions happen only when a manager confirms them through <see cref="DeleteAsync"/>.
/// </summary>
public sealed partial class ConsolidatorService(ConsolidatorSettings settings, IClock clock, ILogger<ConsolidatorService> logger) : BackgroundService
{
    static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>
    /// A ready record is rechecked again once its check is this old, so a confirmed deletion (which needs a check within
    /// <see cref="Holding.ReadyValidFor"/>) normally finds a fresh one.
    /// </summary>
    public static readonly TimeSpan RecheckReadyAfter = TimeSpan.FromHours(20);

    /// <summary>A failed hour's inputs are the source segments overlapping it widened by this, which covers what the merge reads either side.</summary>
    static readonly TimeSpan InputMargin = TimeSpan.FromMinutes(1);

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    readonly SemaphoreSlim _run = new(1, 1);
    readonly SemaphoreSlim _held = new(1, 1);
    readonly Lock _failures = new();
    readonly TaskCompletionSource _firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
    volatile RunSummary? _last;
    volatile string? _lastError;
    long _deletedBytes;

    sealed record RunSummary(DateTimeOffset Utc, int BacklogHours, int WaitingForAnalysis);

    /// <summary>Completes when the run made at start has finished (or failed).</summary>
    public Task FirstRun => _firstRun.Task;

    string FailuresFile => Path.Combine(settings.MergedStationDir, "failures.json");

    string EmptyFile => Path.Combine(settings.MergedStationDir, "empty.json");

    string DeletionsLog =>Path.Combine(settings.MergedStationDir, "deletions.jsonl");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                logger.LogError(ex, "Consolidation run failed");
            }
            finally
            {
                _firstRun.TrySetResult();
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One whole run. A second call waits for the one in progress, so two runs never overlap.</summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await _run.WaitAsync(ct);
        try
        {
            await Task.Run(() => Run(ct), ct);
            _lastError = null;
        }
        finally
        {
            _run.Release();
        }
    }

    void Run(CancellationToken ct)
    {
        var now = clock.UtcNow;

        // Interrupted moves are finished before anything else looks at the folders.
        WithHeld(ct, () =>
        {
            foreach (var source in settings.Sources) Holding.Repair(source.Root, settings.Station);
        });

        var a = Scan(settings.A);
        var b = Scan(settings.B);
        var merged = MergedHours();
        var open = EarliestOpenStart();
        var eligible = Eligibility.Hours(a, b, merged, open, now, Settled);
        var waiting = settings.AnalysisRoot is null ? 0 : Eligibility.Hours(a, b, merged, open, now, _ => true).Count - eligible.Count;
        var failures = Failures().ToDictionary(f => f.HourUtc);
        var empties = ReadEmpty();
        var emptiesChanged = false;

        // Oldest first. Merging stops at the earliest unresolved failure, so the hour after it is merged only once the
        // failed hour is, and joins it by its tail; until then the later hours count in the backlog.
        var settledHours = 0;
        foreach (var hour in eligible)
        {
            ct.ThrowIfCancellationRequested();
            if (failures.TryGetValue(hour, out var failed))
            {
                if (SameInputs(failed.Sources, Inputs(hour, a, b)))
                {
                    logger.LogWarning("{Hour:O}: failed and unchanged since; merging waits here until a source changes or a retry is asked for", hour);
                    break;
                }

                logger.LogInformation("{Hour:O}: a source has changed since it failed; trying it again", hour);
                Retry(hour);
                failures.Remove(hour);
            }
            else if (failures.Keys.Any(f => f < hour && !merged.Contains(f)))
            {
                break;
            }

            if (empties.TryGetValue(hour, out var known))
            {
                if (SameInputs(known.Sources, Inputs(hour, a, b, TimeSpan.Zero)))
                {
                    settledHours++;
                    continue;
                }

                empties.Remove(hour);
                emptiesChanged = true;
            }

            var plan = HourMerger.Plan(hour, a, b, PreviousTail(hour));
            if (plan is null)
            {
                logger.LogInformation("{Hour:O}: neither recorder holds a frame in the hour; recorded as empty", hour);
                empties[hour] = new EmptyHour(hour, now, Inputs(hour, a, b, TimeSpan.Zero));
                emptiesChanged = true;
                settledHours++;
                continue;
            }

            try
            {
                MergedHourWriter.Write(plan, settings.MergedMasterDir, settings.Station);
                merged.Add(hour);
                settledHours++;
                logger.LogInformation("{Hour:O}: merged from {Pieces} piece(s), {Missing} stretch(es) missing from both", hour, plan.Pieces.Count, plan.Missing.Count);
            }
            catch (MergeVerificationException ex)
            {
                Record(new ConsolidationFailure(hour, ex.Message, now, Inputs(hour, a, b)));
                logger.LogError("{Hour:O}: the merged hour failed verification; merging stops here: {Reason}", hour, ex.Message);
                break;
            }
        }

        if (emptiesChanged) WriteEmpty(empties);
        _last = new RunSummary(now, eligible.Count - settledHours, waiting);

        // Holding and rechecks run on every pass, a failed one included, so ready records stay fresh.
        var unresolved = Failures().Select(f => f.HourUtc).ToHashSet();
        if (settings.Holds) WithHeld(ct, () => Hold(now, open, unresolved, empties, ct));
        WithHeld(ct, () => Recheck(now, ct));
    }

    bool Settled(SourceSegment s) => Eligibility.AnalysisSettled(settings.AnalysisRoot, settings.Station, s);

    void WithHeld(CancellationToken ct, Action action)
    {
        _held.Wait(ct);
        try
        {
            action();
        }
        finally
        {
            _held.Release();
        }
    }

    /// <summary>
    /// Holds each closed source segment when every hour it overlaps is merged and the hour before and the hour after it
    /// are each merged or resolved empty (<see cref="ResolvedEmpty"/>). Holding.Hold is given the merged hours among
    /// those, in order, and checks every frame against them.
    /// </summary>
    void Hold(DateTimeOffset now, DateTimeOffset? open, IReadOnlySet<DateTimeOffset> failed, Dictionary<DateTimeOffset, EmptyHour> empties, CancellationToken ct)
    {
        var hours = MergedHours();
        var a = Scan(settings.A);
        var b = Scan(settings.B);
        var segments = a.Concat(b).ToList();
        var resolved = new Dictionary<DateTimeOffset, bool>();
        bool Neighbour(DateTimeOffset h)
        {
            if (hours.Contains(h)) return true;
            if (!resolved.TryGetValue(h, out var ok)) resolved[h] = ok = ResolvedEmpty(h, now, open, failed, empties, segments, a, b);
            return ok;
        }

        foreach (var s in segments)
        {
            ct.ThrowIfCancellationRequested();
            if (s.Meta.EndUtc + Eligibility.MinAge > now || !Settled(s)) continue;

            var first = FloorHour(s.Meta.StartUtc);
            var last = FloorHour(s.Meta.EndUtc > s.Meta.StartUtc ? s.Meta.EndUtc.AddTicks(-1) : s.Meta.StartUtc);
            var complete = Neighbour(first.AddHours(-1)) && Neighbour(last.AddHours(1));
            for (var h = first; h <= last && complete; h = h.AddHours(1)) complete = hours.Contains(h);
            if (!complete) continue;

            var files = new List<string>();
            for (var h = first.AddHours(-1); h <= last.AddHours(1); h = h.AddHours(1))
                if (hours.Contains(h)) files.Add(MergedPath(h));

            try
            {
                Holding.Hold(s, files, now);
                logger.LogInformation("Held {Recorder} {File}", s.Recorder, s.RelativeFile);
            }
            catch (HoldingException ex)
            {
                logger.LogWarning("{Recorder} {File}: {Reason}", s.Recorder, s.RelativeFile, ex.Message);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("{Recorder} {File}: not held: {Reason}", s.Recorder, s.RelativeFile, ex.Message);
            }
        }
    }

    /// <summary>
    /// An unmerged hour that holds nothing to merge and never will: ended <see cref="Eligibility.MinAge"/> ago, no open
    /// segment starts before its end, no failure is recorded for it, and either no segment of either recorder overlaps it
    /// or it was planned and held no frame (empty.json) with the same sources since.
    /// </summary>
    static bool ResolvedEmpty(
        DateTimeOffset hour, DateTimeOffset now, DateTimeOffset? open, IReadOnlySet<DateTimeOffset> failed,
        Dictionary<DateTimeOffset, EmptyHour> empties, List<SourceSegment> segments, IReadOnlyList<SourceSegment> a, IReadOnlyList<SourceSegment> b)
    {
        var end = hour.AddHours(1);
        if (end + Eligibility.MinAge > now) return false;
        if (open is { } o && o < end) return false;
        if (failed.Contains(hour)) return false;
        if (!segments.Any(s => s.Meta.StartUtc < end && s.Meta.EndUtc > hour)) return true;
        return empties.TryGetValue(hour, out var known) && SameInputs(known.Sources, Inputs(hour, a, b, TimeSpan.Zero));
    }

    Dictionary<DateTimeOffset, EmptyHour> ReadEmpty()
    {
        if (!File.Exists(EmptyFile)) return [];
        try
        {
            return (JsonSerializer.Deserialize<List<EmptyHour>>(File.ReadAllText(EmptyFile), Options) ?? []).ToDictionary(e => e.HourUtc);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            logger.LogError("{File} could not be read, so its hours are planned again: {Reason}", EmptyFile, ex.Message);
            return [];
        }
    }

    void WriteEmpty(Dictionary<DateTimeOffset, EmptyHour> empties)
    {
        Directory.CreateDirectory(settings.MergedStationDir);
        var tmp = EmptyFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(empties.Values.OrderBy(e => e.HourUtc).ToList(), Options));
        File.Move(tmp, EmptyFile, overwrite: true);
    }

    /// <summary>Rechecks held records due for their first check, and ready records whose check is older than <see cref="RecheckReadyAfter"/>.</summary>
    void Recheck(DateTimeOffset now, CancellationToken ct)
    {
        foreach (var (path, record) in Records())
        {
            ct.ThrowIfCancellationRequested();
            var due = (record.Status == Holding.Held && now >= record.HeldUtc + Holding.HoldPeriod)
                || (record.Status == Holding.Ready && (record.CheckedUtc is not { } c || now - c >= RecheckReadyAfter));
            if (!due) continue;

            try
            {
                var updated = Holding.Recheck(path, now);
                if (updated.Status == Holding.Failed) logger.LogWarning("{Recorder} {File}: recheck failed: {Reason}", updated.Recorder, updated.File, updated.Reason);
                else logger.LogInformation("{Recorder} {File}: rechecked, {Status}", updated.Recorder, updated.File, updated.Status);
            }
            catch (Exception ex) when (ex is HoldingException or IOException or UnauthorizedAccessException)
            {
                logger.LogWarning("{Record}: not rechecked: {Reason}", path, ex.Message);
            }
        }
    }

    /// <summary>Every readable held record of both recorders, with its path.</summary>
    IEnumerable<(string Path, HeldRecord Record)> Records()
    {
        foreach (var source in settings.Sources)
        {
            var held = Path.Combine(source.Root, settings.Station, "held");
            if (!Directory.Exists(held)) continue;
            foreach (var path in Directory.EnumerateFiles(held, "*.held.json", SearchOption.AllDirectories))
            {
                if (!path.EndsWith(".held.json", StringComparison.Ordinal)) continue;
                HeldRecord? record;
                try
                {
                    record = Holding.ReadRecord(path);
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning("{Record} could not be read: {Reason}", path, ex.Message);
                    continue;
                }

                if (record is not null) yield return (path, record);
            }
        }
    }

    /// <summary>The held records, read now.</summary>
    public IReadOnlyList<HeldRecord> HeldRecords() => [.. Records().Select(r => r.Record)];

    /// <summary>
    /// Deletes each confirmed item with <see cref="Holding.Delete"/>, which refuses anything not ready, not freshly
    /// rechecked or changed. Every item gets its own result.
    /// </summary>
    public async Task<IReadOnlyList<DeletionResult>> DeleteAsync(DeletionRequest request, CancellationToken ct)
    {
        await _held.WaitAsync(ct);
        try
        {
            var now = clock.UtcNow;
            var results = new List<DeletionResult>();
            foreach (var item in request.Items ?? [])
            {
                var source = settings.Sources.FirstOrDefault(s => s.Id == item.Recorder);
                if (source is null)
                {
                    results.Add(new(item.Recorder, item.File, false, $"unknown recorder '{item.Recorder}'"));
                    continue;
                }

                if (item.File is null || !HeldFileName().IsMatch(item.File))
                {
                    results.Add(new(item.Recorder, item.File, false, $"'{item.File}' is not a held file (yyyy/MM-dd/HHmmss.mp3)"));
                    continue;
                }

                var heldMp3 = Path.Combine(source.Root, settings.Station, "held", item.File.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    var bytes = Holding.ReadRecord(Holding.RecordPath(heldMp3))?.Bytes ?? 0;
                    Holding.Delete(Holding.RecordPath(heldMp3), request.ConfirmedBy ?? "", now, DeletionsLog);
                    results.Add(new(item.Recorder, item.File, true, null));
                    logger.LogInformation("Deleted {Recorder} {File} ({Bytes} bytes), confirmed by {ConfirmedBy}", item.Recorder, item.File, bytes, request.ConfirmedBy);
                }
                catch (HoldingException ex)
                {
                    results.Add(new(item.Recorder, item.File, false, ex.Message));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    results.Add(new(item.Recorder, item.File, false, $"not deleted: {ex.Message}"));
                }
            }

            return results;
        }
        finally
        {
            _held.Release();
        }
    }

    /// <summary>Removes a failure entry so the next run tries that hour again. False when there was none.</summary>
    public bool Retry(DateTimeOffset hourUtc)
    {
        lock (_failures)
        {
            var list = ReadFailures();
            var removed = list.RemoveAll(f => f.HourUtc == hourUtc) > 0;
            if (removed) WriteFailures(list);
            return removed;
        }
    }

    void Record(ConsolidationFailure failure)
    {
        lock (_failures)
        {
            var list = ReadFailures().Where(f => f.HourUtc != failure.HourUtc).Append(failure).OrderBy(f => f.HourUtc).ToList();
            WriteFailures(list);
        }
    }

    /// <summary>The failed hours, oldest first.</summary>
    public IReadOnlyList<ConsolidationFailure> Failures()
    {
        lock (_failures) return ReadFailures();
    }

    List<ConsolidationFailure> ReadFailures()
    {
        if (!File.Exists(FailuresFile)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ConsolidationFailure>>(File.ReadAllText(FailuresFile), Options) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogError("{File} could not be read: {Reason}", FailuresFile, ex.Message);
            return [];
        }
    }

    void WriteFailures(List<ConsolidationFailure> list)
    {
        Directory.CreateDirectory(settings.MergedStationDir);
        var tmp = FailuresFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(list, Options));
        File.Move(tmp, FailuresFile, overwrite: true);
    }

    public ConsolidatorHealth Health()
    {
        var records = HeldRecords();
        var hours = MergedHours();
        var last = _last;
        return new ConsolidatorHealth(
            settings.Station,
            settings.Mode,
            hours.Count == 0 ? null : hours.Max(),
            last?.BacklogHours ?? 0,
            last?.WaitingForAnalysis ?? 0,
            [.. Failures().Select(f => new FailureSummary(f.HourUtc, f.Reason))],
            records.Count(r => r.Status == Holding.Held),
            records.Count(r => r.Status == Holding.Ready),
            records.Count(r => r.Status == Holding.Failed),
            DeletedBytes(),
            last?.Utc,
            _lastError);
    }

    /// <summary>The bytes of every deletion in the log. While the log is being written the last total is reported.</summary>
    long DeletedBytes()
    {
        if (!File.Exists(DeletionsLog)) return Interlocked.Read(ref _deletedBytes);
        try
        {
            long total = 0;
            using var stream = new FileStream(DeletionsLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("bytes", out var b) && b.TryGetInt64(out var n))
                        total += n;
                }
                catch (JsonException)
                {
                    // a torn line from a crash; Holding ends it before the next entry
                }
            }

            Interlocked.Exchange(ref _deletedBytes, total);
            return total;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Interlocked.Read(ref _deletedBytes);
        }
    }

    /// <summary>
    /// A recorder's closed segments. Scan leaves out sidecars it cannot use; those are logged here by comparing the
    /// sidecars on disk with what the scan returned.
    /// </summary>
    IReadOnlyList<SourceSegment> Scan(ConsolidatorSource source)
    {
        var list = SourceSegment.Scan(source.Root, settings.Station, source.Id);
        var master = Path.Combine(source.Root, settings.Station, "master");
        if (!Directory.Exists(master)) return list;

        var sidecars = Directory.EnumerateFiles(master, "*.json", SearchOption.AllDirectories)
            .Where(j => j.EndsWith(".json", StringComparison.Ordinal))
            .ToList();
        if (sidecars.Count > list.Count)
        {
            var used = list.Select(s => Path.ChangeExtension(s.AudioPath, ".json")).ToHashSet(StringComparer.Ordinal);
            foreach (var skipped in sidecars.Where(j => !used.Contains(j)))
                logger.LogWarning("Recorder {Recorder}: sidecar {Sidecar} was skipped: unreadable, invalid, or its audio is missing", source.Id, skipped);
        }

        return list;
    }

    /// <summary>The start of the earliest open segment (.part) of either recorder, read from its path. A name that cannot be read blocks every hour.</summary>
    DateTimeOffset? EarliestOpenStart()
    {
        DateTimeOffset? earliest = null;
        foreach (var source in settings.Sources)
        {
            var master = Path.Combine(source.Root, settings.Station, "master");
            if (!Directory.Exists(master)) continue;
            foreach (var part in Directory.EnumerateFiles(master, "*.part", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(master, part).Replace('\\', '/');
                var match = PartName().Match(rel);
                DateTimeOffset start;
                if (!match.Success || !DateTimeOffset.TryParseExact(
                        $"{match.Groups["day"].Value} {match.Groups["time"].Value}", "yyyy/MM-dd HHmmss", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out start))
                {
                    logger.LogWarning("Recorder {Recorder}: open segment {Part} has a name that gives no start time; no hour is merged until it closes", source.Id, part);
                    start = DateTimeOffset.MinValue;
                }

                if (earliest is null || start < earliest) earliest = start;
            }
        }

        return earliest;
    }

    /// <summary>The hours with a merged .mp3 and its sidecar.</summary>
    HashSet<DateTimeOffset> MergedHours()
    {
        var set = new HashSet<DateTimeOffset>();
        var master = settings.MergedMasterDir;
        if (!Directory.Exists(master)) return set;
        foreach (var mp3 in Directory.EnumerateFiles(master, "*.mp3", SearchOption.AllDirectories))
        {
            if (!mp3.EndsWith(".mp3", StringComparison.Ordinal) || !File.Exists(Path.ChangeExtension(mp3, ".json"))) continue;
            var rel = Path.GetRelativePath(master, mp3).Replace('\\', '/');
            if (DateTimeOffset.TryParseExact(rel, "yyyy/MM-dd/HHmmss'.mp3'", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var hour))
                set.Add(hour);
        }

        return set;
    }

    string MergedPath(DateTimeOffset hour)
    {
        var u = hour.UtcDateTime;
        return Path.Combine(
            settings.MergedMasterDir, u.ToString("yyyy", CultureInfo.InvariantCulture), u.ToString("MM-dd", CultureInfo.InvariantCulture),
            u.ToString("HHmmss", CultureInfo.InvariantCulture) + ".mp3");
    }

    IReadOnlyList<ulong> PreviousTail(DateTimeOffset hour)
    {
        var previous = MergedPath(hour.AddHours(-1));
        return File.Exists(previous) && File.Exists(Path.ChangeExtension(previous, ".json")) ? MergedHourWriter.Tail(previous) : [];
    }

    /// <summary>
    /// The source segments overlapping the hour widened by <paramref name="margin"/> (default <see cref="InputMargin"/>,
    /// what a merge of the hour reads), with their checksums now. An empty hour uses no margin, so holding the segments
    /// beside it does not count as a change.
    /// </summary>
    static List<FailureSource> Inputs(DateTimeOffset hour, IReadOnlyList<SourceSegment> a, IReadOnlyList<SourceSegment> b, TimeSpan? margin = null)
    {
        var from = hour - (margin ?? InputMargin);
        var to = hour.AddHours(1) + (margin ?? InputMargin);
        return [.. a.Concat(b)
            .Where(s => s.Meta.StartUtc < to && s.Meta.EndUtc > from)
            .Select(s => new FailureSource(s.Recorder, s.RelativeFile, Sha256(s.AudioPath)))
            .OrderBy(s => s.Recorder, StringComparer.Ordinal)
            .ThenBy(s => s.File, StringComparer.Ordinal)];
    }

    static bool SameInputs(IReadOnlyList<FailureSource> stored, List<FailureSource> now) =>
        stored.OrderBy(s => s.Recorder, StringComparer.Ordinal).ThenBy(s => s.File, StringComparer.Ordinal).SequenceEqual(now);

    static string Sha256(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Convert.ToHexStringLower(SHA256.HashData(fs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "unreadable: " + ex.Message;
        }
    }

    static DateTimeOffset FloorHour(DateTimeOffset t)
    {
        var u = t.UtcDateTime;
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero);
    }

    [GeneratedRegex(@"^\d{4}/\d{2}-\d{2}/\d{6}(-\d+)?\.mp3$")]
    private static partial Regex HeldFileName();

    [GeneratedRegex(@"^(?<day>\d{4}/\d{2}-\d{2})/(?<time>\d{6})(-\d+)?\.mp3\.part$")]
    private static partial Regex PartName();
}

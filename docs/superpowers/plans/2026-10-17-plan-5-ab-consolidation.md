# Plan 5: A/B consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Once an hour is 24 hours old, keep one verified merged copy of every frame recorders A and B hold, hold the
originals for 7 days, and delete them only when a manager confirms (issue #104), with the recorders moved into their
own compose project (issue #102).

**Architecture:** A `consolidator` container (the recorder image started with `--consolidate`) merges each eligible
hour with Core's `FrameMerge` (built for #12), writes `data/merged/<station>/master/...` with an additive sidecar,
verifies it, then moves the originals to `held/`. The web app reads `merged` as another source, prefers it for clips,
shows consolidation status, and asks the consolidator to delete held originals a manager confirms. Nothing is decoded.

**Tech Stack:** .NET 10, ASP.NET Core minimal APIs (Worker host), Blazor Server with PanoramicData.Blazor (web), xUnit,
Docker Compose.

**Spec:** `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md` section 3.14 (also 3.12 consolidated
originals, 3.13 A/B), issues #104, #102, #12.

## Global Constraints

- .NET 10, nullable enabled, warnings as errors, NuGet packages pinned to exact versions; no new packages are needed.
- Tests first (xUnit). Never skip or weaken a test to make it pass. Test audio is synthetic only
  (`tests/WebAudioArchive.Tests/Support/SegmentFiles.Live`, `Mp3TestData`).
- Audio is stored exactly as broadcast: merged files are byte ranges of whole MP3 frames copied from the sources. No
  decode or re-encode anywhere.
- All times stored and shown in UTC; segments aligned to and named by UTC (`<yyyy>/<MM-dd>/<HHmmss>.mp3`).
- No em dashes in code, comments, docs or commit messages.
- Eligibility: an hour is merged once both recorders' segments covering it have been closed for **24 hours**,
  whatever their gaps. Hold period: **7 days**. Deletion: **only on a manager's confirmation**, logged with who, when
  (UTC) and checksums. A manager's "hold" mark (3.12) must block deletion; hold marks are not built yet, so this plan
  leaves `Holding.Delete` the single place where that check will go.
- Spec 3.14 names a `missing` sidecar field; this plan carries the stretches missing from both in the existing `Gaps`
  field instead (so every reader and the position model handle merged files unchanged). Task 10 updates the spec.
- Frame identity: SHA-1 based hash per frame (`FrameIndex`); alignment anchors on **8** consecutive frames
  (`FrameMerge.AnchorFrames`).
- A source segment moves only when its analysis `state.json` (via `AnalysisPaths`) has status `done` or `failed` and
  none for it is `waiting` or `running`; a missing `state.json` means not safe.
- `data/merged` is separate from `data/analysis`. Analysis scans only `master/**`, so it never reads `held/`.
- Recorders are deployed one at a time and never stopped together.
- This checkout is shared with other sessions: commit with explicit paths (`git commit -- <paths>`), never `git add -A`.

## Review Focus

1. A recorder down for a whole hour: the merged hour is the other recorder's frames, its gaps reported as missing.
2. A source changes after merging (crash recovery closes a `.part` late, or a sidecar is rewritten): the stored
   source checksum no longer matches, so the hour is re-merged and nothing moves.
3. Several segments per recorder in one hour (restart names `HHmmss-n`) and a segment that crosses an hour boundary.
4. The consolidator stops mid-step (leftover `.part`, an `.mp3` moved but its `.json` not): the next run completes or
   undoes it and never loses a file.
5. Repeated identical frames (silence) across an hour boundary: the next hour starts after the previous hour's last
   frame, with no frame repeated or dropped.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/WebAudioArchive.Core/Recording/SegmentMeta.cs` (modify) | Optional `Merge` block on the sidecar (additive). |
| `src/WebAudioArchive.Core/Analysis/JobStatus.cs` (moved from Analysis, by its owner) | Job status names, readable without Analysis. |
| `src/WebAudioArchive.Core/Consolidation/SourceSegment.cs` | A source segment on disk: recorder, paths, sidecar, anchors, stem. |
| `src/WebAudioArchive.Core/Consolidation/HourMerger.cs` | Plans one merged hour from A and B frames. |
| `src/WebAudioArchive.Core/Consolidation/MergedHourWriter.cs` | Writes, verifies and publishes a merged hour. |
| `src/WebAudioArchive.Core/Consolidation/Eligibility.cs` | Which hours may be merged now; analysis settled check. |
| `src/WebAudioArchive.Core/Consolidation/Holding.cs` | Moves verified originals to `held/`, rechecks, deletes on confirmation. |
| `src/WebAudioArchive.Worker/Consolidation/ConsolidatorSettings.cs` | Environment settings. |
| `src/WebAudioArchive.Worker/Consolidation/ConsolidatorService.cs` | Hourly loop: merge, hold, recheck, report. |
| `src/WebAudioArchive.Worker/Consolidation/ConsolidatorHost.cs` | `--consolidate` host: `/healthz`, `/held`, `POST /deletions`. |
| `docker/docker-compose.recorders.yml` (create), `docker/docker-compose.yml` (modify) | Recorders in project `waa-recorders`; consolidator and web in the main project; shared network `waa`. |
| `src/WebAudioArchive.Web/...` (modify) | Merged as a source, preferred for clips; Recordings lane; Status card; `/consolidation` review page. |
| `docs/operations.md` (modify) | Migration runbook, consolidation operations, deletion review. |

---

### Task 0: Prerequisite, `JobStatus` in Core (owned by the plan 3 session)

`JobStatus` is in `src/WebAudioArchive.Analysis/Work/JobState.cs`; the consolidator runs in the recorder image, which
must not reference Analysis. Ask session `webaudioarchive-52` (plan 3 owner) to move `JobStatus` to
`src/WebAudioArchive.Core/Analysis/JobStatus.cs` (namespace `WebAudioArchive.Core.Analysis`, same four constants:
`Running = "running"`, `Waiting = "waiting"`, `Done = "done"`, `Failed = "failed"`) after its fix wave lands. Do not
edit Analysis files from this plan. Task 4 consumes `WebAudioArchive.Core.Analysis.JobStatus`.

- [ ] **Step 1:** Send the request with SendMessage; wait for the commit before starting Task 4.

---

### Task 1: Merge block on the sidecar

**Files:**
- Modify: `src/WebAudioArchive.Core/Recording/SegmentMeta.cs`
- Test: `tests/WebAudioArchive.Tests/MergedSidecarTests.cs`

**Interfaces:**
- Produces:
  - `record MergeSource(string Recorder, string File, int FirstFrame, int FrameCount, DateTimeOffset FromUtc, DateTimeOffset ToUtc)`.
    `File` is relative to the recorder's station `master` folder, for example `2026/10-16/140000.mp3`.
  - `record MergeInput(string Recorder, string File, string Sha256, long Bytes)`: every source file used, with its checksum.
  - `record MergeInfo(IReadOnlyList<MergeSource> Sources, IReadOnlyList<MergeInput> Inputs, string Sha256, int FramesFromA, int FramesFromB)`.
  - `SegmentMeta` gains a last optional parameter `MergeInfo? Merge = null`. A merged sidecar uses `Instance = "merged"`,
    `Gaps` = the stretches missing from both (reason `"not recorded by either recorder"`), `EndedBy = "hour-boundary"`.

- [ ] **Step 1: Write the failing tests**
  - `A_merged_sidecar_round_trips_its_sources_inputs_and_checksum`: write a `SegmentMeta` with a two-source `MergeInfo`
    via `SegmentMetaStore.Write`, read it back, assert `Merge.Sources`, `Merge.Inputs`, `Merge.Sha256`,
    `FramesFromA`, `FramesFromB` equal what was written and the JSON contains `"merge":`.
  - `An_existing_sidecar_without_a_merge_block_still_reads`: read a sidecar JSON written by plan 1 (copy the shape from
    `SegmentFiles.Closed`); assert `Merge` is null and every other field is as before.
- [ ] **Step 2:** Run `dotnet test tests/WebAudioArchive.Tests --filter MergedSidecarTests`. Expected: FAIL (types not defined).
- [ ] **Step 3:** Add the three records and the optional parameter in `SegmentMeta.cs`.
- [ ] **Step 4:** Run the same command, then the full suite. Expected: PASS, no other test changes.
- [ ] **Step 5:** `git commit -m "Sidecar: optional merge block for merged hours (#104)" -- <files>`

---

### Task 2: Plan a merged hour

**Files:**
- Create: `src/WebAudioArchive.Core/Consolidation/SourceSegment.cs`, `src/WebAudioArchive.Core/Consolidation/HourMerger.cs`
- Test: `tests/WebAudioArchive.Tests/HourMergerTests.cs`

**Interfaces:**
- Consumes: `FrameIndex.Read`, `FrameMerge.Merge` (Core, #12); `SegmentTimeline.Anchors`/`ToUtc` (Core, #106).
- Produces:
  - `record SourceSegment(string Recorder, string StationMasterDir, string AudioPath, SegmentMeta Meta)` with
    `string RelativeFile` (path under `StationMasterDir`, forward slashes), `string Stem` (`HHmmss` or `HHmmss-n`),
    `IReadOnlyList<TimelineAnchor> Anchors`, `int BytesPerSecond`, and
    `static IReadOnlyList<SourceSegment> Scan(string recorderRoot, string station, string recorder)` (closed segments
    only: an `.mp3` with a readable sidecar; `.part` files are ignored).
  - `record MergedPiece(string Recorder, SourceSegment Source, long StartByte, long EndByte, int FirstFrame, int Frames, DateTimeOffset FromUtc, DateTimeOffset ToUtc)`.
  - `record MergedHourPlan(DateTimeOffset Hour, IReadOnlyList<MergedPiece> Pieces, IReadOnlyList<SegmentGap> Missing, IReadOnlyList<ulong> FrameHashes)`.
  - `static MergedHourPlan? HourMerger.Plan(DateTimeOffset hour, IReadOnlyList<SourceSegment> a, IReadOnlyList<SourceSegment> b, IReadOnlyList<ulong> previousTail)`;
    null when neither recorder holds a frame in the hour.

Rules the implementation must follow:
- Read frames of every source segment overlapping `[hour - 30 s, hour + 1 h + 30 s)` (estimated UTC from its anchors).
- Primary is the recorder with more frames in the hour; ties go to A.
- Start: if the last 8 hashes of the previous merged hour (`previousTail`) occur in the merged order, start at the
  frame after them; otherwise at the first frame whose estimated UTC is at or after `hour`.
- End: the last frame whose estimated UTC (from its own recorder) is before `hour + 1 h`.
- `Missing` = `FrameMerge` missing spans inside the hour, longer than `ClipEvents.MinGapSeconds` equivalent (1.5 s),
  plus the hour's edges when no frame covers them.

- [ ] **Step 1: Write the failing tests** (each builds A and B with `SegmentFiles.Live` on one shared epoch, as `ClipStitchTests` does)
  - `An_hour_both_recorders_hold_is_the_primary_alone_with_no_missing_audio`.
  - `Gaps_in_each_recorder_are_filled_from_the_other_and_every_frame_appears_once`: A gap 200 to 230 s, B gap 400 to
    430 s; assert the plan's frame sequence numbers (via `Mp3TestData.SeqOf` on the piece bytes) are consecutive.
  - `A_recorder_down_for_the_whole_hour_gives_the_other_recorders_frames_and_its_gaps` (Review Focus 1).
  - `Two_segments_in_one_hour_and_one_crossing_the_boundary_are_merged_in_stream_order` (Review Focus 3): a restart at
    20 minutes (`endedBy: "shutdown"`, next segment named `HHmmss-1` via the `name` argument).
  - `Consecutive_hours_join_with_no_frame_repeated_or_lost_even_in_silence` (Review Focus 5): two hours planned in
    order, passing the first plan's last 8 hashes as `previousTail`; the sequence numbers of hour 1 then hour 2 are
    consecutive. Include a run of identical frames across the boundary (add an optional `silence` range to
    `SegmentFiles.Live` that writes one fixed frame for those positions).
- [ ] **Step 2:** Run `dotnet test tests/WebAudioArchive.Tests --filter HourMergerTests`. Expected: FAIL (types not defined).
- [ ] **Step 3:** Implement `SourceSegment` and `HourMerger.Plan` per the rules above.
- [ ] **Step 4:** Run the filter, then the full suite. Expected: PASS.
- [ ] **Step 5:** `git commit -m "Consolidation: plan a merged hour from A and B frames (#104)" -- <files>`

---

### Task 3: Write and verify a merged hour

**Files:**
- Create: `src/WebAudioArchive.Core/Consolidation/MergedHourWriter.cs`
- Test: `tests/WebAudioArchive.Tests/MergedHourWriterTests.cs`

**Interfaces:**
- Consumes: `MergedHourPlan`, `MergeInfo` (Tasks 1 and 2).
- Produces:
  - `sealed class MergeVerificationException(string message) : Exception(message)`.
  - `static SegmentMeta MergedHourWriter.Write(MergedHourPlan plan, string mergedStationMasterDir, string station)`:
    writes `<yyyy>/<MM-dd>/<HHmmss>.mp3.part` (HHmmss = the hour, so `140000`), copies each piece's byte range,
    then verifies: the file parses as whole frames from first byte to last (`FrameIndex.Read` covers every byte), its
    frame hashes equal `plan.FrameHashes`, and each `MergeInput.Sha256` still matches its source file. On success it
    writes the sidecar (Task 1 shape; `StartUtc` = first piece `FromUtc`, `EndUtc` = last piece `ToUtc`,
    `Frames`, `Bytes`, `AudioSeconds`, `Format` copied from the first source) and renames `.part` to `.mp3`. On
    failure it deletes the `.part`, writes nothing else, and throws `MergeVerificationException` naming the check.
  - `static IReadOnlyList<ulong> MergedHourWriter.Tail(string mergedMp3, int frames = 8)`: last frame hashes of a merged file.
- [ ] **Step 1: Write the failing tests**
  - `A_written_hour_is_whole_frames_matching_the_plan_with_a_sidecar_naming_every_source_and_checksum`.
  - `A_source_changed_after_planning_fails_verification_and_leaves_no_file` (Review Focus 2): plan, then append bytes
    to one source; `Write` throws and neither `.mp3`, `.part` nor `.json` exists.
  - `A_leftover_part_from_an_interrupted_run_is_replaced` (Review Focus 4): create a junk `.part` first; `Write` succeeds.
- [ ] **Step 2:** Run the filter. Expected: FAIL.
- [ ] **Step 3:** Implement `Write` and `Tail`. SHA-256 of whole files with `SHA256.HashData(Stream)`.
- [ ] **Step 4:** Run the filter, then the full suite. Expected: PASS.
- [ ] **Step 5:** `git commit -m "Consolidation: write and verify a merged hour (#104)" -- <files>`

---

### Task 4: Eligibility and the analysis handshake

**Files:**
- Create: `src/WebAudioArchive.Core/Consolidation/Eligibility.cs`
- Test: `tests/WebAudioArchive.Tests/EligibilityTests.cs`

**Interfaces:**
- Consumes: `SourceSegment` (Task 2), `AnalysisPaths`, `JobStatus` (Core, Task 0).
- Produces:
  - `static bool Eligibility.AnalysisSettled(string? analysisRoot, string station, SourceSegment s)`: true when
    `analysisRoot` is null (analysis not required); otherwise reads
    `AnalysisPaths(analysisRoot, station).SegmentDir($"{s.Recorder}/{yyyy}/{MM-dd}/{s.Stem}")/state.json` and returns
    true only for status `done` or `failed`. Missing or unreadable file: false.
  - `static IReadOnlyList<DateTimeOffset> Eligibility.Hours(IReadOnlyList<SourceSegment> a, IReadOnlyList<SourceSegment> b, IReadOnlySet<DateTimeOffset> mergedHours, DateTimeOffset? earliestOpenStartUtc, DateTimeOffset now, Func<SourceSegment, bool> settled)`:
    UTC hours, oldest first, not yet merged, where `hour + 1 h + MinAge <= now`, every source segment overlapping the
    hour has `Meta.EndUtc + MinAge <= now` and `settled(segment)`, and `earliestOpenStartUtc` (the earliest `.part` of
    either recorder, from the caller's directory scan) is null or at or after `hour + 1 h`.
    `TimeSpan Eligibility.MinAge = 24 h`.
- [ ] **Step 1: Write the failing tests**
  - `An_hour_closed_less_than_24_hours_ago_is_not_eligible` (23 h 59 m) and `...at_24_hours_is` (exactly 24 h).
  - `An_hour_waits_until_analysis_has_finished_with_it`: state.json `running` false, `waiting` false, missing false,
    `done` true, `failed` true; `analysisRoot: null` true.
  - `Merged_hours_are_skipped_and_the_rest_come_oldest_first`.
- [ ] **Step 2:** Run the filter. Expected: FAIL.
- [ ] **Step 3:** Implement both methods. Read `state.json` with `JsonDocument`, property `status`.
- [ ] **Step 4:** Run the filter, then the full suite. Expected: PASS.
- [ ] **Step 5:** `git commit -m "Consolidation: which hours may be merged, after analysis (#104)" -- <files>`

---

### Task 5: Holding, recheck and confirmed deletion

**Files:**
- Create: `src/WebAudioArchive.Core/Consolidation/Holding.cs`
- Test: `tests/WebAudioArchive.Tests/HoldingTests.cs`

**Interfaces:**
- Consumes: `SourceSegment` (Task 2), `FrameIndex.Read`, merged sidecars' `Merge.Sha256` (Task 1).
- Produces:
  - `record HeldRecord(string Recorder, string Station, string File, string Sha256, long Bytes, DateTimeOffset HeldUtc, IReadOnlyList<string> MergedFiles, string Status, DateTimeOffset? CheckedUtc, string? Reason)`;
    `Status` is `held`, `ready` or `failed`. Stored as `<stem>.held.json` beside the held files.
  - `static string Holding.HeldDir(string recorderRoot, string station, DateTimeOffset segmentStartUtc)` =
    `<recorderRoot>/<station>/held/<yyyy>/<MM-dd>`.
  - `static bool Holding.AllFramesMerged(SourceSegment s, IReadOnlyList<string> mergedMp3s, out string reason)`: every
    distinct frame hash of `s` occurs in the merged files given (the hours from one before to one after it).
  - `static HeldRecord Holding.Hold(SourceSegment s, IReadOnlyList<string> mergedMp3s, DateTimeOffset now)`: moves
    `.mp3` then `.json` into `HeldDir`, writes the record with `Status = "held"`. Throws if `AllFramesMerged` is false.
  - `static HeldRecord Holding.Recheck(string heldJson, DateTimeOffset now)`: after `HoldPeriod = 7 days`, re-hashes the
    held file (must equal `Sha256`) and re-runs `AllFramesMerged` against the merged files (which must still exist and
    match their sidecar `Merge.Sha256`); sets `ready` or `failed` with the reason.
  - `static void Holding.Delete(string heldJson, string confirmedBy, DateTimeOffset now, string deletionsLog)`:
    only when `Status == "ready"` and the file still matches `Sha256`; deletes `.mp3`, `.json`, `.held.json`, appends
    `{utc, confirmedBy, recorder, file, sha256, bytes}` to `deletionsLog` (JSON lines). Otherwise throws.
  - `static void Holding.Repair(string recorderRoot, string station)`: completes a half move (an `.mp3` in `held/`
    whose `.json` is still in `master/`, or the reverse) by moving the other file across (Review Focus 4).
- [ ] **Step 1: Write the failing tests**
  - `A_segment_whose_frames_are_all_merged_moves_to_held_with_a_record`; `...one_frame_short_does_not_move`.
  - `After_seven_days_a_held_segment_is_ready_only_if_it_and_its_merged_copy_are_unchanged` (6 d 23 h: still `held`;
    7 d: `ready`; merged file altered: `failed`).
  - `Only_a_ready_segment_is_deleted_and_the_deletion_is_logged_with_who_and_the_checksum`.
  - `A_half_finished_move_is_completed_on_the_next_run`.
- [ ] **Step 2:** Run the filter. Expected: FAIL.
- [ ] **Step 3:** Implement. `File.Move` within the recorder root (same mount, so a rename).
- [ ] **Step 4:** Run the filter, then the full suite. Expected: PASS.
- [ ] **Step 5:** `git commit -m "Consolidation: hold verified originals, recheck after 7 days, delete on confirmation (#104)" -- <files>`

---

### Task 6: The consolidator host

**Files:**
- Create: `src/WebAudioArchive.Worker/Consolidation/ConsolidatorSettings.cs`, `ConsolidatorService.cs`, `ConsolidatorHost.cs`
- Modify: `src/WebAudioArchive.Worker/Program.cs` (branch on `--consolidate` before the recorder host is built)
- Test: `tests/WebAudioArchive.Tests/ConsolidatorHostTests.cs`

**Interfaces:**
- Consumes: Tasks 2 to 5.
- Produces:
  - Settings from environment: `WAA_STATION` (required), `WAA_SOURCES` (`a=/data/a,b=/data/b`), `WAA_MERGED`
    (`/data/merged`), `WAA_ANALYSIS` (`/data/analysis`, empty means not required), `WAA_CONSOLIDATE_MODE` (`merge`
    writes merged hours only; `hold` also moves originals; default `merge`), `WAA_PORT` (`8080`).
  - `ConsolidatorService` runs at start and every hour: `Holding.Repair`; merge eligible hours oldest first, one at a
    time, passing the previous hour's `Tail`; on `MergeVerificationException` record the failure in
    `<merged>/<station>/failures.json` (`hourUtc`, `reason`, `utc`, and the SHA-256 of each source file) and stop the
    run. A failed hour is retried only when one of its source checksums has changed or after `POST /retry`
    `{ "hourUtc": "..." }` removes its entry (spec 3.14). In `hold` mode, hold each source segment whose neighbouring
    merged hours (one before to one after) all exist; recheck held records that are due.
  - `POST /retry` clears one failure entry so the next run tries that hour again.
  - `GET /healthz` JSON: `{ station, mode, lastMergedHourUtc, backlogHours, waitingForAnalysis, failures: [{ hourUtc, reason }], held, ready, deletedBytes }`, 200 always (failures are reported, not fatal).
  - `GET /held` JSON: the `ready` records.
  - `POST /deletions` body `{ "confirmedBy": "...", "items": [{ "recorder": "a", "file": "2026/10-16/140000.mp3" }] }`:
    calls `Holding.Delete` for each; responds with per-item `deleted` or the reason it was not.
- [ ] **Step 1: Write the failing tests** (WebApplicationFactory with `--consolidate`, as `WorkerHostTests` does)
  - `In_merge_mode_an_eligible_hour_is_merged_and_nothing_moves`.
  - `In_hold_mode_merged_sources_move_to_held_and_healthz_counts_them`.
  - `A_verification_failure_stops_the_run_and_is_reported_on_healthz`.
  - `Deletion_needs_a_ready_record_and_reports_each_item`.
  - `A_failed_hour_is_not_retried_until_a_source_changes_or_retry_is_posted`.
- [ ] **Step 2:** Run the filter. Expected: FAIL.
- [ ] **Step 3:** Implement. Inject `IClock` (Core) so tests control "now".
- [ ] **Step 4:** Run the filter, then the full suite. Expected: PASS.
- [ ] **Step 5:** `git commit -m "Consolidator host: hourly merge, hold, recheck, healthz and confirmed deletion (#104)" -- <files>`

---

### Task 7: Compose split and the consolidator container

**Files:**
- Create: `docker/docker-compose.recorders.yml`, `tests/WebAudioArchive.Tests/RecorderProjectTests.cs`
- Modify: `docker/docker-compose.yml`, `docs/operations.md`
- Coordinate: `tests/WebAudioArchive.Tests/DeploymentFilesTests.cs` belongs to plan 3; ask its owner to point its
  recorder checks (`Recorders_are_untouched_by_analysis`) at `docker/docker-compose.recorders.yml`, or do it with
  their agreement.

**Interfaces:**
- Produces: project `waa-recorders` (`name: waa-recorders`) with `recorder-a` and `recorder-b` unchanged (same
  container names, image, volumes, ports, `restart: always`, `stop_grace_period: 30s`) plus the label
  `waa.role: recorder`, attached to network `waa` (`networks: { waa: { name: waa } }`). The main file keeps its
  implicit project name (so the `docker_web-cache` volume keeps its name), loses both recorder services, joins external
  network `waa` for `web` and `consolidator`, and adds `consolidator`: image `webaudioarchive:local` (shared with the
  recorders, built only from the recorders file), `command: ["--consolidate"]`, environment as Task 6 with
  `WAA_CONSOLIDATE_MODE: merge`, volumes `data/a:/data/a`, `data/b:/data/b`, `data/merged:/data/merged`,
  `data/analysis:/data/analysis:ro`, port `127.0.0.1:8096:8080`, `restart: unless-stopped`, healthcheck
  `--healthcheck`. `web` gains `WAA_MERGED: /data/merged` and `WAA_CONSOLIDATOR: http://consolidator:8080`.
- [ ] **Step 1: Write the failing tests** in `RecorderProjectTests` (use `RepoFiles.ComposeServices`; match lines with
  `\r?$` so the checks hold on Windows checkouts):
  - `Recorders_live_only_in_their_own_project`: recorders file has exactly `recorder-a`, `recorder-b` and
    `name: waa-recorders`; the main file has neither.
  - `The_consolidator_never_builds_the_recorder_image_and_cannot_restart_a_recorder`: its service has no `build:` and
    no `depends_on`; the main file has no service with `waa.role: recorder`.
  - `Web_and_consolidator_reach_the_recorders_over_the_shared_network`.
- [ ] **Step 2:** Run the filter. Expected: FAIL.
- [ ] **Step 3:** Write the compose files. In `docs/operations.md` replace the upgrade commands with the recorders
  file and add the **one-time migration**, one recorder at a time:
  1. `docker compose -f docker/docker-compose.recorders.yml build recorder-a`
  2. `docker stop waa-recorder-a; docker rm waa-recorder-a`, then
     `docker compose -f docker/docker-compose.recorders.yml up -d recorder-a`; wait for `/healthz` connected and a
     frame in the last 10 s.
  3. The same for `recorder-b`.
  4. `docker compose -f docker/docker-compose.yml up -d --build web consolidator` (always name services; never
     `--remove-orphans`).
- [ ] **Step 4:** Run the filter, then the full suite. Expected: PASS.
- [ ] **Step 5:** `git commit -m "Compose: recorders in their own project; consolidator container (#102, #104)" -- <files>`

---

### Task 8: Web reads merged hours

**Files:**
- Modify: `src/WebAudioArchive.Web/WebSettings.cs`, `Clips/SegmentCatalog.cs`, `Clips/ClipPlanner.cs`,
  `Recordings.cs`, `Components/Pages/Recordings.razor`, `wwwroot/css/app.css`
- Test: `tests/WebAudioArchive.Tests/MergedReadingTests.cs`

**Interfaces:**
- Produces: `WebSettings.MergedRoot` (`WAA_MERGED`, optional). `SegmentCatalog.All()` returns `merged` first when set
  (recorder id `"merged"`, label `M`), so it wins ties in `BestCovered`; combined coverage (#103) includes it with no
  further change. Merged segments read with the same `Scan` (their sidecars carry `Gaps` = missing from both).
  Recordings calendar: merged segments in their own lane, CSS class `rec-merged`, legend "Merged".
- [ ] **Step 1: Write the failing tests**
  - `An_auto_clip_of_a_merged_hour_comes_from_the_merged_copy`.
  - `A_clip_spanning_merged_and_unmerged_hours_joins_them_frame_by_frame` (merged and A frames are identical, so #12's
    joining applies; sequence numbers consecutive).
  - `Held_originals_are_not_listed` (files under `held/` never appear).
- [ ] **Step 2:** Run the filter. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the filter, then the full suite. Expected: PASS.
- [ ] **Step 5:** `git commit -m "Web: read merged hours and prefer them for clips (#104)" -- <files>`

---

### Task 9: Status card and the deletion review page

**Files:**
- Create: `src/WebAudioArchive.Web/Consolidation/ConsolidatorClient.cs`, `Components/Pages/Consolidation.razor` (+ `.razor.cs`)
- Modify: `Status/RecorderStatus.cs` (snapshot gains consolidation), `Components/Pages/Status.razor`, `Components/Layout/NavMenu.razor` (or wherever the tabs are)
- Test: `tests/WebAudioArchive.Tests/ConsolidationPageTests.cs`

**Interfaces:**
- Consumes: consolidator `/healthz`, `/held`, `POST /deletions` (Task 6).
- Produces: `ConsolidatorClient` (named HttpClient, 3 s timeout like the recorder health client) with
  `Task<ConsolidatorHealth?> HealthAsync(CancellationToken)`, `Task<IReadOnlyList<HeldItem>> ReadyAsync(CancellationToken)`,
  `Task<IReadOnlyList<DeletionResult>> DeleteAsync(string confirmedBy, IReadOnlyList<HeldItem> items, CancellationToken)`.
  Status shows a "Consolidation" card: mode, last merged hour, backlog, waiting for analysis, failures, held, ready
  to delete, space freed; unreachable is amber, a failure is red. `/consolidation` lists ready items by UTC day with
  checkboxes and a "Delete confirmed originals" button that asks for confirmation and posts them; `confirmedBy` is
  the signed-in manager once plan 4a's sign-in is live, and `"loopback"` until then (the app is loopback only).
- [ ] **Step 1: Write the failing tests** (stub handler as `WebAppTests` does)
  - `Status_shows_the_consolidation_card_and_marks_a_failure_red`.
  - `The_review_page_lists_ready_originals_by_utc_day`.
  - `Confirming_posts_the_chosen_items_and_shows_each_result`.
  - `Nothing_is_deleted_without_the_confirmation_step`.
- [ ] **Step 2:** Run the filter. Expected: FAIL.
- [ ] **Step 3:** Implement.
- [ ] **Step 4:** Run the filter, then the full suite. Expected: PASS.
- [ ] **Step 5:** `git commit -m "Web: consolidation status and confirmed deletion of held originals (#104)" -- <files>`

---

### Task 10: Deploy (owner approval required at each step)

- [ ] **Step 1:** Ask the owner before touching any container. Run the Task 7 migration, one recorder at a time,
  confirming each is recording before the next.
- [ ] **Step 2:** Start `consolidator` in `merge` mode. After the first run, compare one merged hour with its sources
  using the scratch alignment check from 2026-10-16 (every source frame present once; frame counts add up), and show
  the owner the Status card.
- [ ] **Step 3:** Only when the owner says so, set `WAA_CONSOLIDATE_MODE: hold` and redeploy the consolidator. The
  first deletions become possible 7 days later and only on confirmation.
- [ ] **Step 4:** Update spec 3.14 with what was built and close #104 and #102.

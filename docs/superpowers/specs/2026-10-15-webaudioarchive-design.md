# WebAudioArchive design

Status: draft v2 for review, 2026-10-15 (v1 was "record the shows"; v2 is "archive everything")
Repo: https://github.com/davidnmbond/WebAudioArchive (public, personal account)

## 1. Purpose

Written from the point of view of a station manager. Burst Radio (https://www.burstradio.org) broadcasts
around the clock. The manager needs to:

1. **Keep a complete record** of everything broadcast, 24 hours a day, for regulatory and reporting purposes.
2. **Track content**: who presented what, what was said, and what went wrong (swearing, clipping, dead air).
3. **Give presenters a copy** of each of their shows, privately, including the five minutes either side.

The design is generic (a station is configuration plus an optional schedule parser) but Burst is the
primary case and the first configured.

Success looks like: every second of the stream is on disk on two independent machines; each show is a
browsable, playable item with a transcript, derived metadata and a list of flagged events; a presenter can
sign in with Google and fetch only their own shows; the manager can see everything and a weekly report.

### Non-goals (version 1)

- No public access to any audio or transcript. Everything is behind a Google sign-in.
- No audio re-encoding, normalisation or trimming. Audio is stored as broadcast.
- No reconciliation between the two instances (decision, see 3.10). They are independent.
- No automatic deletion unless a retention period is set (see 3.12).
- No live alerts. Detections are reported, not pushed (decision).
- No song identification yet, but the data model reserves a place for it (see 3.9).
- No speaker diarisation. People are identified only as far as the transcript supports.

## 2. Observed facts about Burst (fetched 2026-10-15)

- The schedule page is server-rendered HTML; no JavaScript is needed. A saved copy is the test fixture
  `tests/fixtures/burst-schedule-2026-10-15.html`.
- One `<table>`; columns Monday to Sunday; no `colspan` or `rowspan`; hourly slots labelled `09:00` to
  `21:00`; each show is `<td class="show-cell" data-show="...">`.
- The page states no timezone. `Europe/London` is assumed and is a config value.
- The page lists no hosts as such. Some titles contain a name ("Breakfast with Mackenzie").
- The stream is found from https://www.burstradio.org/listen: a RadioJar widget (streamName in a script block)
  naming a Revma stream, MP3, 128 kbps, 44.1 kHz, stereo (measured 2026-10-15). The stream URL answers 302
  with a short-lived token (rj-ttl=5), so the final URL must be requested afresh on every connect. The
  response carries icy-metaint 16000, so ICY metadata is interleaved and must be removed from the audio.
- Nothing is scheduled between 22:00 and 09:00. With the 24x7 requirement that audio is recorded anyway and
  appears as unassigned time (automation, silence or whatever the station plays).
- Thursday 20:00 is "Backseat Aux", confirmed by Amy as hers and Raff's.

## 3. Architecture

Each instance is one .NET 10 worker plus one web app (same image, two roles) and two optional companions:
a whisper.cpp server and Ollama. Two instances run independently, one on the station manager's machine and one
in the test cluster, both on Linux containers.

```
config/stations.json
   |
 StationSession  ---------- one always-on stream connection per station
   |  raw bytes (fan-out)
   +--> MasterRecorder ---> /data/<station>/master/<yyyy>/<MM-dd>/<HHmmss>.mp3   (gapless UTC-hour segments)
   +--> Analyser (decode once) --> PCM --> ClippingDetector
   |                              +--> Transcriber (whisper) --> transcript timeline
   |                                                              +--> SwearDetector, FillerDetector
   |                                                              +--> Enricher (Ollama)
 ScheduleService (15 min) --> schedule.json --> WindowPlanner --> ShowCutter (lossless cut from master)
   |
 events timeline  <--  detectors (and, later, song identification)
   |
 Web app (Google sign-in, roles, SQLite users) ---> per-show pages, audio, transcript, flags, manager report
```

The key shift from v1: the **master recording is continuous and independent of the schedule**. A show file
is a lossless cut from the master (start minus 5 minutes to end plus 5 minutes). So the schedule can be wrong,
change, or fail to load, and no audio is ever lost: at worst a show cut is re-made later. Overlap between
back-to-back shows is not special: two cuts simply share a stretch of the master, which satisfies the
requirement to produce up to two overlapping show files from one stream.

### 3.1 Config

`config/stations.json`, validated against a JSON schema at start-up (fail loudly, naming the field):
`id`, `name`, `listenUrl`, `scheduleUrl`, `timezone` (default `Europe/London`), `scheduleParser`
(`burst` or `llm`), `marginBefore` and `marginAfter` (default 5 minutes each), `scheduleRefresh` (default
15 minutes), `watershed` (default `21:00` local), `enabled`. The segment length is fixed at one UTC hour in plan 1 and is not configurable.
Service-wide settings (data directory, endpoints and model names for whisper and Ollama, Google client id,
bootstrap manager email, retention) are environment variables. Secrets are only ever environment variables or
mounted files, never committed, because the repo is public.

### 3.2 Schedule service

- Every `scheduleRefresh` it fetches `scheduleUrl` (plain User-Agent, conditional requests where the server
  supports them, a body hash otherwise) and parses it.
- **Burst parser:** AngleSharp over the real HTML, not regular expressions. Header gives the weekday, row label
  gives the slot start, `data-show` gives the title. A slot is one hour. Consecutive cells in a column with the
  same title are one show. Empty cells are off air.
- **LLM fallback:** used when the station has no parser, or the parser throws or returns fewer than half the
  previous shows. Page text goes to Ollama with a JSON schema for the same shape, and the result is marked
  `"parsedBy": "llm"`. An LLM result may add or change shows but may not remove shows that a deterministic parse
  found.
- **Output:** `/data/<station>/schedule.json`, written atomically: source URL, fetch time, content hash, parser,
  the weekly grid, and dated windows for the next 14 days (UTC plus local time, so DST behaviour is visible).
  Changes are appended as a diff to `schedule-changes.jsonl`.
- A failed fetch or parse never discards the last good schedule; an empty parse counts as a failure; after 3
  consecutive failures a warning is logged every attempt.

### 3.3 Window planner and show cutter

- `cutFrom = start - marginBefore`, `cutTo = end + marginAfter`, computed in the station timezone, stored in
  UTC. A non-existent local time (spring forward) moves to the next valid instant; an ambiguous one (autumn
  back) takes the first occurrence.
- **Schedule changes apply immediately** (decision: 15-minute re-check, applied at once). Because cuts come
  from the master, applying a change is safe and reversible: a moved or renamed show is re-cut; a removed show
  has its cut marked `superseded` (file kept for 7 days, then removed with the rest of its show folder). A
  show still in progress is cut when it ends, so a change before then simply sets the cut points.
- A cut waits until `cutTo` is covered by finished master data, then is produced with
  `ffmpeg -c copy` at frame boundaries (no re-encode). For MP3, frames can depend on the previous few frames, so
  the first few milliseconds of a cut may decode imperfectly; this is documented and measured, not hidden.
- Unassigned stretches (no show) are not cut. They exist in the master and appear in the web app as
  "Unassigned" time that a manager can browse and play from.

### 3.4 Stream session

- One connection per station, always on, because the requirement is 24x7.
- Discovery from `listenUrl`: Revma/RadioJar widgets, `<audio>`/`<source>`, media URLs in the page, playlists
  (`.pls`, `.m3u`), one level of iframe. The resolved URL is cached and re-resolved after repeated failure.
- ICY metadata blocks, when present, are removed from the audio and each title is stored with its offset.
- A dropped connection is retried with backoff (1 s doubling to 30 s). The gap is recorded with wall-clock
  start and end in the segment's metadata and in the instance's coverage figures. Audio during a gap is lost. A station may also replay a few seconds of buffered audio when a connection is re-established (Revma: audio at connect starts about 5 s before the arrival time), so reconnects can overlap slightly with what was already recorded.
- Fan-out through bounded channels. The master writer is the priority subscriber. A slow subscriber (the
  analyser) drops oldest chunks, and the drop is counted and logged; it can never stall the recorder.

### 3.5 Master recorder

- Writes segments aligned to UTC clock hours, named by UTC: master/<yyyy>/<MM-dd>/<HHmmss>.mp3 (HHmmss is the
  first frame's arrival time) plus <HHmmss>.json (actual start and end, bytes, gaps, ICY titles, audio format,
  instance id, endedBy). UTC naming replaces station-local naming because local clock hours repeat when the
  clocks go back and would collide. A restart makes a new segment starting at the restart time.
- Splits only at MP3 frame boundaries, so every segment decodes on its own and concatenating segments in order
  reproduces the bytes received exactly. This holds within one connection; across reconnects the stream can have a gap or a short replay, and a partial frame at either end of a connection is skipped.
- Writes to `<HHmmss>.mp3.part`, renamed when the segment closes. On start-up any `.part` left by a crash is
  closed with `endedBy` set to `"crash"`, cut back to whole frames.
- Each instance logs free disk space, warns below a threshold and, below a lower threshold, **keeps recording
  and stops everything else first** (analysis, cutting, web), because the record matters most.
- Rough size: 128 kbps is about 57.6 MB per hour, 1.38 GB per day, about 500 GB per year, per instance for the
  master, plus about 0.9 GB per day for show cuts.

### 3.6 Analyser, transcriber and detectors

All run off the master data on their own thread and queue, never in the recorder's path. Each instance
analyses independently, so both need local whisper and an LLM, as you have confirmed.

- **Decode once:** ffmpeg decodes the live stream to 16 kHz mono PCM for speech and to full-rate PCM for the
  clipping detector, in chunks of about 30 seconds with a short overlap.
- **Transcriber:** whisper.cpp server, segments with word timestamps stored on the **stream timeline** (UTC),
  one transcript for the station. Each show's transcript is a slice of it. The prompt is primed with the show
  title and a station glossary.
- **Clipping detector:** on the decoded full-rate audio, an event is a run of at least 3 consecutive samples at
  or above 0.99 of full scale (configurable). Events within 250 ms merge. Each event stores time, duration,
  channel and peak. Per show it reports event count, total clipped milliseconds and worst peak. The definition
  is mine and must be tuned against real Burst audio before the numbers are trusted; the spec treats the
  thresholds as provisional.
- **Swear detector:** matches whisper words against a tiered list (`mild`, `strong`, `severe`), configurable
  and versioned in the repo, matching whole words and common inflections. Each hit stores the word, tier,
  time, an audio clip reference and whether it aired **before the watershed** (default 21:00 local), which is
  reported more seriously. Because speech to text can mis-hear or soften words, every hit links to the audio so
  a human can confirm it; counts are reported as detected, not as proven.
- **Filler detector (er, um and similar):** whisper normally removes disfluencies. To keep them the transcriber
  uses a verbatim prompt (a short priming sentence that contains fillers) and word-level timestamps, and the
  detector counts `um`, `uh`, `er`, `erm`, `ah`, `hmm`, `like` is **excluded** (too ambiguous) and
  `you know` is counted only when configured. Output is a count and a rate per minute of speech per show. This
  is an estimate: it will be validated against a hand-labelled sample of Burst audio and the measured
  precision and recall recorded in the docs before the figure is used for anything but trends.
- **Events timeline:** every detector writes `{type, start, end, severity, evidence, detector, version}` to
  `events` for the station. The web app and reports read only this. Song identification will add a `song` type
  later without any change to consumers (see 3.9).
- Dead air (silence longer than a threshold) is a free by-product of the decode and is included as an event
  type because it matters for a continuous service.

### 3.7 Enricher

- When a show cut and its transcript slice are complete, the title, slot and text go to Ollama with a JSON
  schema and a low temperature.
- Fields: hosts, guests, topics, songs and artists mentioned, locations, competitions or calls to action,
  language, and a two or three sentence summary.
- **Every derived claim needs evidence:** each entry carries a verbatim transcript quote and a confidence. A
  claim whose quote is not in the transcript is dropped. This guards against invented names.
- Schedule facts stay authoritative. LLM output lives under `derived` with model, prompt version and date, and
  can be regenerated without touching audio. If Ollama is down the show is marked `"enrichment": "pending"` and
  retried.
- **Derived hosts never grant access.** They are suggestions shown to a manager (see 3.8).

### 3.8 Web app, accounts and access

- ASP.NET Core, server-rendered pages, audio played with HTTP range requests. One web role per instance.
- **Sign-in is Google only** (OpenID Connect). There are no local passwords. Only Google accounts that a
  manager has added can sign in; any other Google account is shown "not authorised".
- **Roles:** `manager` and `presenter`.
  - Managers: all shows, all unassigned time, all flags, the weekly report, and user management.
  - Presenters: only the shows a manager has assigned to their account, **including the 5 minutes before and
    after**, with that show's transcript and flags. They can download the audio. They see nothing else, not
    even the titles of other shows.
- **Assignment:** a manager assigns Google accounts to shows (by show title in the schedule, covering every
  occurrence, optionally limited to a date range). Derived hosts are shown to the manager as suggestions with
  their evidence quotes and are never used to grant access, so a mis-heard name cannot expose a private show.
- **First user:** "the first user is the station manager" is implemented as a **configured bootstrap email**
  (`BOOTSTRAP_MANAGER_EMAIL`), not first-come-first-served. On an internet-facing site, the first person to
  find the URL must not be able to become manager. Changing this to true first-user-wins is a one line option
  but is not recommended.
- **Storage:** SQLite at `/data/app.db` (users, roles, assignments, audit log). It is per instance.
- **Authorisation is enforced on the server for every request** for audio, transcript, clip and metadata,
  never only by hiding links. Audio and clip URLs are not guessable and are never served without a valid
  session and permission check. Every access to a show by anyone is written to an audit log.
- **Sessions:** secure, HttpOnly, SameSite cookies; CSRF protection on all forms; sign-out ends the session.
- **Instance mode** (`INSTANCE_MODE`): `primary` (managers and presenters) or `manager-only`. The secondary
  is expected to run `manager-only` (decision, 2026-10-15): presenters are refused sign-in on it, presenter
  assignments are neither stored nor shown there, and only the manager role exists. Assumption: the secondary
  is the instance on the manager's machine and the primary is the test cluster; the setting is symmetric, so
  swapping them is configuration only.
- **Two instances, two sets of accounts** (both serve their own site). Presenter accounts and assignments live
  only on the primary. Managers are added on each instance. The app offers export and import of the user list
  as a JSON file so a manager can copy the manager list across by hand.
- **Reachability:** the primary (test cluster) is served by its ingress with TLS, because presenters need to
  reach it. A `manager-only` secondary does not need to be on the internet: managers can use it at
  `http://localhost` (Google permits localhost redirect URIs) or over a private network such as Tailscale, which
  keeps the home machine off the public internet. A public Cloudflare Tunnel is possible but is not needed and
  is not recommended for a manager-only instance.
- **Manager views:** a per-show page (audio, transcript with flagged words highlighted, flags list, derived
  facts, coverage), an unassigned-time browser, and a weekly report page.
- **Weekly report** (page plus CSV download): per show, duration, coverage percentage, swear hits by tier and
  how many before the watershed, clipping events and milliseconds, filler rate, dead air, and recording gaps
  per instance.

**Playback screen** (added 2026-10-15, built in plan 4; the same page serves managers and presenters, with the
same permission checks as every other audio request):

- **Calendar picker:** shows are chosen from the schedule in a calendar view (week and month, showing each
  occurrence as scheduled). A manager sees every occurrence and unassigned time; a presenter sees only their
  assigned shows, so the calendar never reveals the titles of other shows. Occurrences with no recording or
  partial coverage are marked as such.
- **Player:** HTTP range playback of the cut show including the 5 minute margins, with playback speed options
  (0.75x, 1x, 1.25x, 1.5x, 2x, pitch preserved), skip back and forward, and a position that survives reloads.
  Speed is a browser feature (`playbackRate`), so no audio is re-encoded.
- **Event navigation:** the events timeline (dead air, clipping, swearing, fillers and, later, songs) is drawn
  along the seek bar and listed beside it; selecting an event jumps to its time, and previous or next event
  buttons filter by type. Events come from `events.json` (3.6); nothing is computed in the browser.
- **Search:** full-text search over the transcripts for a word or phrase, with the hits listed per show and
  occurrence with a snippet and timestamp, each hit playing from a few seconds before the match. Scope follows
  the same access rules (presenters search only their shows). Search also covers the derived facts (3.7: names,
  artists, topics) and, when song identification exists, songs. Search is served from a SQLite FTS5 index built
  from the transcript JSON, so it can be rebuilt at any time and holds no data that is not in the show files.
- **Songs:** "cases of a song being played" needs song identification, which is a non-goal for v1 (3.9). Until
  then it can only find songs that are named in ICY titles (Burst sends none, as measured on 2026-10-15), said
  aloud in the transcript, or recorded in the enricher output. Real song search depends on the 3.9 work and is
  recorded as a decision for the manager (section 7).

### 3.9 Songs (later, not built in v1)

Reserved so it can be added without redesign: ICY titles are already captured when the stream provides them;
the `events` timeline accepts a `song` type with title, artist and a confidence and source. The intended later
approach is audio fingerprinting against open music databases (Chromaprint with AcoustID and MusicBrainz) with
the source and confidence recorded for each identification. It will be designed separately; matching
fingerprints needs clean music segments, and speech over music will reduce accuracy.

### 3.10 Two instances, no reconciliation

Both instances run the full pipeline independently on separate storage. Consequences, stated plainly:

- A hole in one archive (host down, network drop) is not filled automatically from the other.
- The two archives can differ slightly: byte offsets, segment boundaries and gaps will not match exactly.
- Transcripts, detections and derived metadata can differ, because models and timing differ. No claim is made
  that they are identical.
- Each instance publishes its own **coverage figure** (percent of the last 24 hours and 30 days with audio) in
  `/healthz`, the weekly report and the manager page, so a hole is visible and the manager knows which
  archive to use for a given period.
- Accounts are per instance (see 3.8).

### 3.11 Output layout

```
/data/<station>/
  schedule.json, schedule-changes.jsonl
  master/<yyyy>/<MM-dd>/<HHmmss>.mp3  <HHmmss>.json
  shows/<yyyy>/<yyyy-MM-dd>-<HHmm>-<show-slug>/
      show.mp3  show.json  show.transcript.jsonl  show.transcript.txt  clips/
  events/<yyyy>/<MM-dd>.jsonl
/data/app.db        users, assignments, audit log
```

`show.json` holds: station, show title (and any rename), scheduled and cut times, actual coverage and gaps,
audio format, stream URL and how it was found, ICY headers and titles, schedule version, transcript status,
flag totals, and `derived` (with evidence, confidence and model details). It is the single record of all
known details for the show. Master segment JSON is the equivalent for unassigned time.

### 3.12 Retention, data protection and compliance

- **Retention is a setting, undecided.** Default: keep everything, delete nothing automatically. When a period
  is set (`RETENTION_MASTER_DAYS`, `RETENTION_SHOW_DAYS`) deletion only happens after it, logs every deletion,
  and never deletes a segment that a manager has marked "hold" (for example an incident under investigation).
  The correct period for Burst depends on its licence or university policy and is the manager's to determine;
  this spec does not assert a legal requirement.
- **Personal data:** recordings and transcripts contain people's voices and speech, and the user table and
  audit log hold Google account emails. The service is private by design, with access limited by role,
  an audit trail, and a retention setting. This is design guidance for the operator, not legal advice; the
  operator remains responsible for the data protection position.
- The README states that recordings are private archive copies and must not be republished.

## 4. Repository layout

```
src/WebAudioArchive.Core/      stream discovery, ICY, fan-out, master segments, window planner, events
src/WebAudioArchive.Stations/  Burst parser (and future per-station parsers)
src/WebAudioArchive.Analysis/  decode, clipping, swear, filler, transcriber and enricher clients
src/WebAudioArchive.Worker/    recorder host and background services
src/WebAudioArchive.Web/       sign-in, roles, pages, audio endpoints, reports
tests/WebAudioArchive.Tests/   unit and integration, fixtures under tests/fixtures
config/stations.json           Burst configured
docker/                        Dockerfile, compose files (cpu, gpu), tunnel notes
docs/                          specs, operations, detector validation notes
.github/workflows/ci.yml       build and test on Linux
```

.NET 10, AngleSharp, ffmpeg in the image, `Microsoft.Extensions.Hosting`, ASP.NET Core with Google OIDC, SQLite.
All packages pinned to exact versions.

## 5. Deployment

- `docker compose up` brings up worker, web, whisper.cpp and Ollama. GPU use is opt-in via an override file.
- The same image runs on the manager's machine and the test cluster. Config and secrets are injected by the
  environment, so nothing differs between them except values. A Kubernetes manifest example is provided for the
  cluster.
- Non-root user. The data directory is a volume. Timezone comes from config, not the host.
- `/healthz` reports per station: connected, last byte time, current segment, coverage figures, last schedule
  fetch, analysis lag, free disk. The container health check fails if no audio has arrived for 2 minutes or the
  schedule is more than 1 hour old.
- Structured JSON logs to stdout.

## 6. Testing

- **Burst parser** against the fixture: known cells (Thursday 20:00 is "Backseat Aux", Monday 09:00 is
  "Breakfast with Mackenzie", 7 columns by 13 rows). Expected values come from the page as fetched.
- **Master recorder:** segments split on frame boundaries; concatenation of segments equals the stream bytes; a
  crash leaves a `.part` that is recovered on restart; a mid-stream drop records a gap.
- **Window planner and cutter:** overlaps, back-to-back shows, DST both ways, changes (moved, renamed, removed),
  restart mid-show, and a cut across an hour boundary equal to the same range of the master.
- **Stream session:** in-process fake Icecast with ICY metadata, a drop and reconnect, a `.pls` playlist, and a
  slow subscriber that must not slow the recorder.
- **Detectors:** synthetic audio with known clipped regions (exact counts and times); a transcript fixture with
  known swear words including near misses ("Scunthorpe" style) that must not match; fillers checked against a
  hand-labelled real sample, with measured precision and recall written to `docs/`.
- **Enricher:** a fake Ollama returning a hallucinated name with no matching quote, which must be dropped.
- **Web and security:** a presenter requesting another show's audio, transcript, clip, or JSON by URL gets 403
  or 404; an unlisted Google account is refused; a manager sees everything; the audit log records accesses; a
  presenter's view includes exactly the 5 minute margins and no more.
- **End to end (CI on Linux):** fake station and schedule, a show created "one minute from now", asserting
  master, cut, JSON, transcript and events appear, with whisper and Ollama stubbed.
- A manual smoke test against the real Burst stream is documented and not run in CI.

## 7. Risks and open points

| Risk | Handling |
|---|---|
| Burst changes its page | Parser fails closed (last good schedule kept), LLM fallback proposes one, warning logged. The master is unaffected. |
| Filler and swear counts are estimates | Reported as detected, linked to audio, validated on a labelled sample, thresholds provisional. |
| Clipping thresholds are not yet tuned | Provisional defaults, tuned on real audio and recorded in `docs/`. |
| 24x7 transcription load | Needs a GPU for large models; CPU works with a small model with lag. Recording is unaffected either way. |
| Disk fills | Free-space guard keeps recording and stops everything else first. |
| Primary (internet-facing) instance exposed | Google-only sign-in, listed accounts only, server-side authorisation on every request, audit log, no local passwords. |
| Secondary exposed | Not needed: `manager-only`, reached via localhost or a private network, not the public internet. |
| Manager lists diverge between instances | Export and import of the user list; documented as manual. |
| Public repo | Code only. No recordings, secrets or user data in git. |
| Compliance requirement unknown | Retention is a setting with no default deletion; manager decides the period. |
| Song search wanted but identification is deferred | Playback and transcript search ship in plan 4; song search is limited to ICY titles, spoken mentions and enricher output until 3.9 is built. Manager to decide whether to bring 3.9 forward. |

## 8. Decisions recorded

- Repo `davidnmbond/WebAudioArchive`, public, personal account; no panoramicdata code copied in.
- C# on .NET 10, Docker, whisper.cpp and Ollama for local AI.
- 24x7 continuous master; show files are lossless cuts with 5 minute margins; unassigned audio is kept.
- Two Linux instances (manager's machine and test cluster), independent, no reconciliation.
- Schedule: per-station parser with LLM fallback, re-checked every 15 minutes, applied immediately.
- Detect and report only (swearing with watershed, clipping, fillers); no live alerts.
- Web app: Google sign-in only; roles manager and presenter; presenters see only assigned shows with margins;
  manager assigns; both instances serve their own site; bootstrap manager by configured email. The secondary
  runs `manager-only` (managers only, no presenters, not internet-facing); the primary serves presenters.
- Primary is the test cluster (presenters, HTTPS ingress); the manager's machine is the manager-only secondary.
- Weekly report: coverage and gaps per instance, swearing by tier with before-watershed count, clipping and
  dead air, filler rate per show (labelled an estimate).
- Retention: keep everything indefinitely. No automatic deletion. The retention settings exist but are off;
  deletion is manual, by a manager, logged. Revisit if a required period is found.
- Google OAuth clients: not yet created. The operator will be guided through creating one per instance at
  implementation time; client id and secret come from environment variables only.
- Song identification deferred; events timeline reserves for it.

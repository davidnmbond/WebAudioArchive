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
- No song identification yet, but the data model reserves a place for it (see 3.9). Designed since (2026-10-17):
  now-playing capture and fingerprint identification with PanoramicData.SoundPrint, not yet built.
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
- **Now playing (fetched 2026-10-17):** the listen page shows the current song. A script on the page fetches
  `https://burst-now-playing.joshalexgay.workers.dev/now-playing` every 60 s, which answers JSON
  `{"artist", "title", "artwork", "timestamp"}` (artwork is a Deezer cover URL; `Cache-Control: no-store`). The
  `timestamp` is when that service last refreshed, not when the song began: it stayed fixed across three polls 20 s
  apart. RadioJar's own API (`proxy.radiojar.com/api/stations/nkyraup3h3nwv/now_playing/`) answers with every field
  empty for this station, and the stream sends no ICY titles. The feed keeps no history: what is not captured when it
  is current is lost.
- **A and B carry identical audio (measured 2026-10-17):** frame by frame, the two recorders' MP3 frames are
  byte-identical, offset by a few frames where each cut the hour or resumed after a reconnect (issue #104).

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

- **All times are shown and stored in UTC (decision, 2026-10-15).** File names, sidecars, `/healthz`, logs, the
  show files, the web app, reports and CSV exports all use UTC, with a visible "UTC" label. The station's
  timezone (`Europe/London`) is used for one thing only: converting Burst's published local schedule times into
  UTC instants when the schedule is parsed (so a 20:00 show in BST is 19:00 UTC). Nothing is displayed in local
  time, and the "UTC plus local time" columns in 3.2 are dropped in favour of UTC only.
`config/stations.json`, validated against a JSON schema at start-up (fail loudly, naming the field):
`id`, `name`, `listenUrl`, `scheduleUrl`, `timezone` (default `Europe/London`), `scheduleParser`
(`burst` or `llm`), `marginBefore` and `marginAfter` (default 5 minutes each), `scheduleRefresh` (default
15 minutes), `watershed` (default `21:00` local), `enabled`, and an optional `nowPlaying` object (3.9.1:
`url`, `titleField`, `artistField`, `artworkField`, `pollSeconds`, or `"enabled": false`). The segment length is fixed at one UTC hour in plan 1 and is not configurable.
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
  the weekly grid, and dated windows for the next 14 days (in UTC, so DST behaviour is visible as a shift of the UTC time).
  Changes are appended as a diff to `schedule-changes.jsonl`.
- A failed fetch or parse never discards the last good schedule; an empty parse counts as a failure; after 3
  consecutive failures a warning is logged every attempt.
- **MVP (built 2026-10-15, issue #24; the rest is issue #8):** a minimal schedule service runs inside the web app, not
  as its own service. It fetches the page at start and every 15 minutes, parses it with the AngleSharp Burst parser
  above (also accepting `rowspan`; it fails closed on any table it does not recognise), and keeps the last good weekly
  grid in the web cache volume (`/cache/schedule/<station>.json`), because the web container mounts `/data` read-only.
  Dated occurrences are worked out from the grid on request (any range, UTC, rules of 3.3 for skipped and repeated
  local times) rather than stored for 14 days. Not built yet: the LLM fallback, conditional requests, the content
  hash, `schedule.json` and `schedule-changes.jsonl` under `/data`.

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
  The same pass looks for now-playing sources (3.9.1).
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
- **Where the LLM runs (decision, 2026-10-17):** on the owner's own Ollama servers, `pdl-rune-02.panoramicdata.com`
  for the test instance and `pdl-rune-01.panoramicdata.com` for production (both `:11434`, reachable on the private
  network), instead of an Ollama on the analysis host. Transcript text for enrichment therefore leaves the analysis
  host, but only to those two named servers (an allow-list; cloud models and any other host are refused), and
  currently over plain HTTP. Audio never leaves the analysis host: transcription stays local.
- **Derived hosts never grant access.** They are suggestions shown to a manager (see 3.8).

### 3.8 Web app, accounts and access

- ASP.NET Core (Blazor, see the UI stack bullet below), audio played with HTTP range requests. One web role per instance.
- **UI stack (decision, 2026-10-15):** Blazor (.NET 10) in **interactive server** render mode, using the
  `PanoramicData.Blazor` component library from NuGet (MIT licence, public package; pinned to an exact version,
  10.0.350 at the time of writing) for the design elements: `PDTable` (with `PDPager`, filtering and sorting) for
  shows, events, search hits, users and the weekly report; `PDTabSet`, `PDToolbar`, `PDModal`, `PDForm`,
  `PDTagInput`, `PDDateTime`, `PDProgressBar` and `PDTree` where they fit; `PDTimeline` is the first candidate
  for the events lane on the seek bar and is evaluated in plan 4, with a small custom component as the fallback.
  The library has no calendar or media player component, so the **calendar view and the audio player are
  built in this repository** on Bootstrap (the library's own dependency, 5.1.3 or later) and the browser's HTML
  `audio` element. Interactive server fits the access model: authorisation stays on the server, nothing
  sensitive is sent to a client that is not allowed it, and there is no separate API surface to secure. Only
  the audio and download endpoints are plain minimal-API endpoints, because they need HTTP range requests
  and cookie authorisation, and the browser fetches them directly. Only the library is used, not any other
  PanoramicData code or conventions. Consequence: the Web project is a Blazor web app, the session cookie also
  carries the circuit, and the page tests use bUnit plus a few Playwright smoke tests (Playwright in CI only).
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
- **Segments (regions):** besides point events, the timeline carries labelled spans from one marker to the next:
  song regions ("Artist - Title", with source and confidence), talking sections (with the transcript text, timed
  lines highlighted as playback moves, click a line to seek), ads and jingles, each on its own lane under the
  seek bar. The player shows what is playing now, plays or loops a single segment, and steps to the previous or
  next segment of a kind. Segments come from `events.json` and the transcript (3.6, 3.7); until those exist the
  recordings simply have none. The `PDMediaPlayer` component supports this (`Segments`, `PDMediaSegment`, and a
  helper that derives segments from consecutive markers).
- **Search:** full-text search over the transcripts for a word or phrase, with the hits listed per show and
  occurrence with a snippet and timestamp, each hit playing from a few seconds before the match. Scope follows
  the same access rules (presenters search only their shows). Search also covers the derived facts (3.7: names,
  artists, topics) and, when song identification exists, songs. Search is served from a SQLite FTS5 index built
  from the transcript JSON, so it can be rebuilt at any time and holds no data that is not in the show files.
- **Songs:** "cases of a song being played" needs song identification (3.9). Until it is built, songs can be
  found only in ICY titles (Burst sends none, as measured on 2026-10-15), in the now-playing log once 3.9.1 is
  built (approximate times), in speech in the transcript, or in the enricher output.

**Web app MVP (built 2026-10-15, deviates from this section on purpose):** a manager-only web container
(`docker/Dockerfile.web`, service `web`) ahead of the full design. Deviations, all to be removed when 3.8 is built
in full: **no sign-in, roles or audit log**, so it is published on `127.0.0.1` only and must
not be exposed; clips are made on request for any UTC range (Player page and `GET /api/clip`) rather than from
scheduled show cuts; clip and audio URLs are unguessable hashes but are **not** permission checked. Since issue #24
(PanoramicData.Blazor 10.0.366) the player is `PDMediaPlayer` (inside `ClipPlayer`) and the calendar picker exists as
the **Shows** tab (`PDCalendar`, week, month and agenda, in UTC) over the minimal schedule of 3.2, with coverage per
show; a show's page plays a clip made on request from the masters with the 5 minute margins (the show proper shaded,
show start and end, recording gaps and recorder restarts as markers, margins and show as segments), not a cut show
file. Events, transcripts, search and a remembered position are not built (issue #11). With Auto, a clip that no
single recorder runs unbroken through is joined from A and B by matching frames (3.13, issue #12, built 2026-10-17,
using the `FrameMerge` shared with 3.14); a chosen recorder is never mixed. Details in `docs/operations.md`.

### 3.9 Songs (designed 2026-10-17, not built)

Two parts that work together: the station tells us roughly **what** is playing (3.9.1), and audio fingerprints
tell us exactly **where** it plays, and recognise it again when nothing tells us (3.9.2). The `events` timeline's
reserved `song` type (plan 3) carries the result. Issues: now-playing capture #105, identification #2, the
SoundPrint package #3. The earlier idea of matching against open databases (Chromaprint with AcoustID and
MusicBrainz) is dropped: SoundPrint matches against our own catalogue, which the now-playing feed fills.

#### 3.9.1 Now-playing capture (both recorders)

Station metadata cannot be fetched afterwards, so it is captured live, by both recorders, for the same reason
both record audio: if one is down, the other still has it (owner decision, 2026-10-17).

- **Sources**, tried in this order and all recorded with how they were found:
  1. `nowPlaying.url` in `stations.json`, with dotted field paths for title, artist and artwork (defaults
     `title`, `artist`, `artwork`).
  2. Found on `listenUrl` in the same pass as the stream (3.4): URLs in the page's scripts whose path names
     now playing (`now-playing`, `nowplaying`, `now_playing`, `currentsong`, `current-track`). A candidate is
     accepted only when it answers JSON with a non-empty title field. Burst's worker is found this way.
  3. Provider APIs for a known widget or server, accepted only when they answer a non-empty title: RadioJar
     (`proxy.radiojar.com/api/stations/<streamName>/now_playing/`), Icecast (`/status-json.xsl` on the stream
     host), Shoutcast (`/currentsong`).
  4. ICY `StreamTitle`, already stored in segment sidecars (3.4) whenever the stream sends it.
- **Polling:** every `pollSeconds` (default 30, minimum 10; Burst's own page polls every 60), plain User-Agent,
  10 s timeout, backoff to 5 minutes on failure, rediscovery after an hour of failures. It runs on its own task with
  its own HTTP client; nothing it does can block, slow or stop the recording, and its failures are caught and
  reported, never thrown into the recorder.
- **Storage:** `/data/<station>/nowplaying/<yyyy>/<MM-dd>.jsonl`, UTC days, append-only, beside the master (so
  `data/a` and `data/b` each have a copy). One line per **change**, not per poll:
  `{ "utc", "kind": "song" | "unavailable" | "available", "source", "sourceUrl", "howFound", "title", "artist",
  "artworkUrl", "sourceTimestamp", "raw" }`, where `utc` is our receive time and `raw` the response body as
  received (small; kept so a parser fix can re-read history). A song lasts until the next line. `unavailable`
  marks the feed failing and `available` its return, so a stretch with no data is never mistaken for one song.
  Artwork is stored as a URL only.
- **Timing is approximate:** a change is seen up to one poll interval after the feed changes, and the feed lags
  the audio by an unknown amount. Feed times are labelled approximate everywhere; 3.9.2 places the boundaries.
- **Reporting:** `/healthz` gains per station `nowPlaying: { source, howFound, lastOkUtc, lastError, current }`;
  Status shows it. The web app reads both recorders' files and merges them: the same title and artist from A and B
  within two poll intervals is one change.
- **Recorder change:** this adds to the recorder, which is deployed one recorder at a time as in 3.13.

#### 3.9.2 Identification with PanoramicData.SoundPrint (after it is published, #3)

SoundPrint (MIT, public, pure managed .NET 10, no native dependencies) is landmark fingerprinting in the style of
Wang (2003). It takes decoded mono float PCM at any sample rate, stores landmarks against track ids we choose, and
matches a window of audio, returning `TrackId`, `QueryStart` and `QueryEnd` on the query's own clock, the
matching `TrackStart` and `TrackEnd` in the song, and a confidence. It holds no song metadata and calls no outside
service. Its integration guide describes this use: label spans some other way, fingerprint them, and use matches
for boundaries and for plays nobody announced.

- **Decoding:** SoundPrint never touches MP3. It reads the analyser's existing scratch decode (3.6, 16 kHz mono),
  so the archive path is unchanged and nothing is decoded twice.
- **Catalogue (ours):** `Song { id, title, artist, artworkUrl, firstHeardUtc }`, keyed by normalised artist and
  title, plus **enrolments** `{ songId, recorder, fromUtc, toUtc, source, parameterFingerprint }`. SoundPrint's
  store (`PanoramicData.SoundPrint.Postgres`, a PostgreSQL container per instance) is a cache that can be rebuilt
  from the enrolments and the archive, which is how a change of `FingerprintOptions` is handled.
- **Enrolment from the feed:** for a feed song with no fingerprint yet, take its approximate span, trim one poll
  interval plus a measured feed lag from each end, require at least 60 s, and fingerprint that core. Spans with
  `unavailable` inside are skipped. A human label (#2) can enrol too, and replaces a wrong one
  (`RemoveTrackAsync`, then add).
- **Matching:** per closed segment, 12 s windows every 3 s (`RollingLandmarkWindow`), frame 0 set to the segment's
  first audio UTC. Consecutive matches of one track whose `TrackStart` keeps advancing merge into one play; a window
  holding two songs places the boundary between the first's `QueryEnd` and the second's `QueryStart` (benchmarks:
  within about 0.1 to 0.2 s on a hard cut). One new fingerprinter per segment, so the frame counter never nears
  its limit.
- **Song events:** `type: song`, with title, artist, UTC start and end, and `source` (`feed`, `fingerprint`,
  `human`) with confidence. Feed and fingerprint agreeing is the normal case. When they disagree, both are kept and
  the play is flagged for review. Human edits outrank both and are never overwritten by re-analysis (#2).
- **Unknown music:** a run of windows with no match between known songs, with music but no speech, is a candidate
  for the review queue, and for enrolment once the feed or a person names it.
- **Behind an interface:** the rest of the app sees a small song-matching interface. Tests use SoundPrint's
  in-memory store and synthetic audio. No recordings or real fingerprints enter the repository.
- **Package rule:** no project reference to the SoundPrint source. Wait for the published NuGet package and pin
  its exact version (and EF Core and Npgsql exactly, through the Postgres package).
- **A/B and consolidation:** enrolments and song events are UTC spans, not file offsets, so they survive the
  merged archive of #104 unchanged.

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
- **Consolidated originals (3.14, owner decision 2026-10-17):** once A and B have been merged into one verified
  copy, the originals are held for 7 days and then deleted **only when a manager confirms**, with each deletion
  logged. This is not automatic deletion, and it removes duplicates only: every frame is kept in the merged copy.
- **Personal data:** recordings and transcripts contain people's voices and speech, and the user table and
  audit log hold Google account emails. The service is private by design, with access limited by role,
  an audit trail, and a retention setting. This is design guidance for the operator, not legal advice; the
  operator remains responsible for the data protection position.
- The README states that recordings are private archive copies and must not be republished.

### 3.13 Recorder separation and A/B recorders (later enhancement, requested 2026-10-15)

Plan 1 ships the recorder and the rest of the system in one process and one container. Upgrading that container
interrupts recording for the length of a restart (seconds, recorded as a gap). For a regulatory archive that
interruption should be avoidable, so the architecture must allow two changes. Neither is built in v1, but
nothing built before them may make them harder.

- **Separate recording engine.** The recorder (`StationSession`, `MasterRecorder`, `/healthz`) runs as its own
  container and image, with as few dependencies as possible and its own version. It does one thing: hold the
  stream connection and write segments plus sidecars. Analysis, schedule, cutting, transcription, the web app
  and the weekly report run in other containers and only **read** the recorder's output (segments and sidecars
  on a shared volume, read-only mount) and never call into it in-process. The files and the sidecar schema are
  the contract between the two sides, so the recorder rarely needs to change and the rest can be upgraded freely
  without touching an open stream.
- **A/B recorders.** The system supports two recorders per station (A and B), each with its own stream
  connection, its own instance id and its own folder (`master-a`, `master-b`), writing independently. To
  upgrade, stop and upgrade A while B records, check A is healthy and recording, then do the same to B. Each
  recorder still has a short gap while it is itself restarted, but the other one covers it.
- **Reading two recorders.** Readers treat the union of A and B segments as the station's coverage. For any
  time window (for example a show with its margins) the cutter prefers a single recorder that covers the whole
  window with no gap; if neither does, it stitches from both at MP3 frame boundaries. The two connections start
  at different moments (and a Revma connection starts about 5 seconds ahead of its arrival time, see 3.4), so
  A and B's **files are not byte-identical and their segments overlap**. Stitching must choose a cut point, not
  concatenate, and the sidecar for a cut records which recorder supplied each part. Coverage reported in
  `/healthz` and the weekly report is computed over the union. Measured since (2026-10-17): the MP3 **frames**
  are byte-identical between A and B, only offset by a few frames, so cut points can be found exactly by matching
  frames (3.14). The union is built in the web app (#103); each recorder's `/healthz` reports only itself.
- **What it does and does not protect.** A/B protects recording during upgrades and against one recorder
  failing. It does not protect against the host or the shared volume failing; for that, the two-instance setup
  in 3.10 remains the answer. Two recorders double the load on the station's stream server (two listeners) and
  the disk use of the master unless one is retired or thinned; retention (3.12) must cover both folders.
- **Constraints on earlier plans so this stays possible:** the recorder must not import analysis or web code;
  sidecar schema changes are additive with a `schema` version (already present); readers address segments by
  sidecar `startUtc` and `instance`, never by file name or by assuming one recorder; folder names carry the
  recorder id when more than one exists.

### 3.14 Consolidating A and B into one merged copy (designed 2026-10-17, not built; issue #104)

Two recorders double the disk use. Once an hour is safely in the past, keep one merged copy holding every frame
either recorder has, then retire the originals. Owner decisions, 2026-10-17: a merged copy, not "keep one
recorder"; hours with gaps are merged too; an hour is eligible once both recorders' segments covering it have been
closed for 24 hours; originals are held for 7 days, then deleted when a manager confirms; the work runs in its own
container.

**Evidence (2026-10-16, Burst, read-only).** 13:00 UTC: A 137,866 frames, B 137,860; every overlapping frame
byte-identical at offset 0; A cut the hour 6 frames (0.16 s) later. 14:00 UTC, which held an outage on both: before
it B = A + 6 frames, after reconnecting B = A - 30, so A holds 36 frames (about 0.94 s) that B lacks (corrected
2026-10-17; first written the other way round); every frame of each file matches the other at one of those two
offsets. Both recorders' 14:00 files also hold about 36 s less audio than their wall span minus their recorded gaps:
the stream stalled before the connection dropped, so sidecar gap times understate what was lost (issue #106). Sidecar wall times are only estimates (a mid-run hour holds about
2.2 s more audio than its wall-clock span; a reconnect replays about 5 s).

- **Process:** a `consolidator` container, the recorder image started with `--consolidate`, mounting `data/a`,
  `data/b` and `data/merged` read-write. Recorders never run it and it never touches an open segment. It wakes
  hourly, does the eligible hours oldest first, one hour at a time, and can be stopped at any point without harm.
  It reports through its own `/healthz` (last hour merged, backlog, last failure).
- **Frame identity:** each frame's hash (SHA-1 of its bytes) is its identity. Silence and other repeated content
  makes single frames ambiguous (about 400 repeats an hour measured), so alignment anchors on runs of 8
  consecutive identical frames. Wall-clock times only narrow the search. No decoding at any point.
- **Merging an hour:** read the frame lists of every A and B segment overlapping the hour (plus the last frames
  already merged for the previous hour). Walk the primary recorder (A, else whichever has more frames); at each
  of its gaps or ends, find the other recorder's run that matches the frames before the gap and copy from it until
  the primary's frames resume (matched by an anchor), then switch back. Where neither has the next frames, record
  a gap missing from both. Frames already written for the previous hour are skipped by identity, so files
  join with no repeat and no loss.
- **Hour boundaries:** a merged file starts at the first frame whose estimated UTC is at or after the hour, the
  estimate coming from the supplying recorder's timeline. Concatenating merged files in order reproduces the
  station's stream as captured, without duplicates.
- **Output:** `data/merged/<station>/master/<yyyy>/<MM-dd>/<HHmmss>.mp3` with `.json`, the same sidecar schema
  plus `sources` (per stretch: recorder, source file, first and last frame index, UTC), `missing` (stretches
  neither had), `sha256` of the file and of each source file, and `framesFromA`, `framesFromB`. Written to `.part`
  and renamed after verification.
- **Verification, before anything moves:** every frame of every source segment is accounted for (present in the
  merged file in order, or before the hour in the previous merged file, or after it in the next once merged); the
  merged file parses as whole frames from start to end; the frame count and duration equal the sum of the
  stretches. A failure leaves the originals in place, marks the hour `failed` with the reason, and stops the
  run so a person can look; it is retried only when its inputs change or a manager asks.
- **Holding:** verified originals move (a rename within their own folder tree) to
  `data/<recorder>/<station>/held/<yyyy>/<MM-dd>/`, keeping their sidecars. After 7 days the merged copy is
  verified again against the held files, and the hour is listed as **ready to delete**. A manager confirms in the
  web app (one hour, a day, or everything listed); each deletion is logged with who, when (UTC) and the checksums.
  Nothing is deleted without that confirmation, and a manager's "hold" (3.12) blocks it.
- **Readers:** the segment catalog reads `merged` as a third source. Combined coverage (#103) takes the union of
  A, B and merged. Clips prefer merged and otherwise behave as now. Held originals are listed only in the
  deletion review. Analysis events and song data address UTC spans, not files, so they are unaffected. Analysis
  (plan 3) discovers segments in `data/a` and `data/b`; it normally finishes an hour long before the hour is
  eligible, but re-analysis of an older hour must also look in `data/merged` (same `master/<yyyy>/<MM-dd>/`
  layout and sidecar schema, recorder id `merged`). Consolidation waits for an hour's analysis to finish, so an
  hour is never moved out from under a running job. `data/merged` is separate from `data/analysis`.
  **Handshake (agreed with the plan 3 work, 2026-10-17):** a source segment may move only when its analysis
  `state.json` (located with `AnalysisPaths` in Core:
  `data/analysis/<station>/segments/<recorder>/<yyyy>/<MM-dd>/<stem>/state.json`) exists with status `done`
  (or `failed`, parked) per the `JobStatus` constants, and none for that segment is `waiting` or `running`. A missing
  `state.json` means not yet analysed: not safe. Analysis scans only `master/**`, so it never reads `held/`.
- **Reporting:** Status gains a consolidation card (eligible backlog, last merged hour, failures, hours awaiting
  confirmation, disk freed); Recordings shows merged segments as their own lane.
- **Deployment safety (#102, approved 2026-10-17):** the recorders move to their own compose project
  (`docker/docker-compose.recorders.yml`) so that building or restarting any other service can never recreate
  them; the consolidator lives in the main project.
- **Tests:** synthetic MP3 frame streams for A and B with chosen offsets, gaps on one side, gaps on both,
  reconnect replays, repeated silence frames and hour cut differences; property: the merged stream equals the
  de-duplicated union in order; a corrupted source or a lost frame makes verification fail and moves nothing.

## 4. Repository layout

```
src/WebAudioArchive.Core/      stream discovery, ICY, fan-out, master segments, window planner, events
src/WebAudioArchive.Stations/  Burst parser (and future per-station parsers)
src/WebAudioArchive.Analysis/  decode, clipping, swear, filler, transcriber and enricher clients
src/WebAudioArchive.Worker/    recorder host and background services
src/WebAudioArchive.Web/       Blazor app (PanoramicData.Blazor), sign-in, roles, pages, audio endpoints, reports
tests/WebAudioArchive.Tests/   unit and integration, fixtures under tests/fixtures
config/stations.json           Burst configured
docker/                        Dockerfile, compose files (cpu, gpu), tunnel notes
docs/                          specs, operations, detector validation notes
.github/workflows/ci.yml       build and test on Linux
```

.NET 10, AngleSharp, ffmpeg in the image, `Microsoft.Extensions.Hosting`, ASP.NET Core Blazor with Google OIDC,
PanoramicData.Blazor, SQLite.
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
| Upgrading the single recorder container interrupts recording | v1 accepts a recorded gap of a few seconds per upgrade. Section 3.13 specifies the recorder/rest split and A/B recorders to remove it later. |
| Song search wanted but identification is deferred | Now-playing capture (3.9.1) is built first so the labels accumulate; fingerprint identification (3.9.2) follows the SoundPrint release. |
| Now-playing feed is a third party's small service and may change or vanish | Every source and how it was found is logged; discovery runs again after an hour of failures; `unavailable` stretches are recorded; Status shows the feed's state. The audio is unaffected. |
| Feed times lag the audio | Feed times are labelled approximate; enrolment trims the ends; fingerprint matches place the boundaries. |

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
- A/B consolidation (2026-10-17, 3.14): a merged copy of every frame from A and B, for hours closed 24 hours,
  gaps included; made by a separate consolidator container; originals held 7 days, then deleted only on a
  manager's confirmation.
- Songs (2026-10-17): both recorders capture the station's now-playing data around the clock; identification
  uses PanoramicData.SoundPrint from its published NuGet package, trained from the now-playing labels, against our
  own catalogue (3.9). Design first; capture to be built next, identification once the package is published.

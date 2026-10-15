# WebAudioArchive design

Status: draft for review, 2026-10-15
Repo: https://github.com/davidnmbond/WebAudioArchive (public, personal account)

## 1. Purpose

Record every show on a web radio station, unattended, according to the station's published schedule, with a
margin either side of each show. Transcribe locally, and use a local LLM to derive who was on the show.
Burst Radio (https://www.burstradio.org) is the primary use case and the first station configured. The
design is generic: a station is configuration plus, optionally, a schedule parser.

Success looks like: for each Burst show, a folder on disk holding the broadcast audio exactly as streamed,
a JSON file describing it, and a transcript, with no manual steps and nothing recorded twice.

### Non-goals (version 1)

- No publishing, web UI, search or playback. Recordings are for private use and are never committed to git.
- No audio re-encoding, normalisation or trimming. Audio is saved as broadcast.
- No retention or clean-up policy. Disk space is the operator's concern; the service logs free space.
- No speaker diarisation. Speakers are identified only as far as the LLM can derive them from the words.

## 2. Observed facts about Burst (fetched 2026-10-15)

- The schedule page is server-rendered HTML. No JavaScript is needed to read it. A saved copy is the test
  fixture `tests/fixtures/burst-schedule-2026-10-15.html`.
- One `<table>`; header columns are Monday to Sunday; no `colspan` or `rowspan`.
- Rows are hourly slots labelled `09:00` to `21:00`. Each show cell is `<td class="show-cell" data-show="...">`.
- The page states no timezone. Burst is a UK student station, so `Europe/London` is assumed and is a config value.
- The page lists no hosts as such. Some titles contain them ("Breakfast with Mackenzie"). Hosts and guests
  therefore come mainly from the transcript.
- The stream is found from the listen page (https://www.burstradio.org/listen): a RadioJar widget naming a
  Revma stream, served as MP3 (verified 2026-10-15, 128 kbps, 44.1 kHz, stereo, no ICY track titles seen).
- Thursday 20:00 is "Backseat Aux" and was confirmed by Amy as hers and Raff's.

## 3. Architecture

One .NET 10 worker process (`WebAudioArchive.Worker`) in one container, plus two optional companion
containers in the compose file: a whisper.cpp server and Ollama. All three are host-neutral. Recordings go to
a mounted volume (`/data`).

```
config/stations.json
        |
  ScheduleService --(every 15 min)--> schedule.json --> WindowPlanner --> open/close events
        |                                                                         |
  (parser: burst | llm)                                                            v
                                                   StationSession (one stream connection per station)
                                                       |  fan-out of raw stream bytes
                          +----------------------------+----------------------+
                          v                            v                      v
                   RecordingWriter A           RecordingWriter B        Transcriber (decode once)
                   (show N + margins)          (show N+1 + margins)            |
                                                                        transcript segments
                                                                               v
                                                           per-recording transcript slice + Enricher (Ollama)
```

### 3.1 Config

`config/stations.json`, validated against a JSON schema at start-up (fail loudly, naming the bad field):

| Field | Meaning |
|---|---|
| `id` | Short slug, used in paths. |
| `name` | Display name. |
| `listenUrl` | Station page or direct stream URL. |
| `scheduleUrl` | The schedule page. |
| `timezone` | IANA zone, default `Europe/London`. |
| `scheduleParser` | `burst` or `llm`. |
| `marginBefore`, `marginAfter` | Defaults `00:05:00` each. |
| `scheduleRefresh` | Default `00:15:00`. |
| `enabled` | Default true. |

Service-wide settings (data directory, whisper and Ollama endpoints, model names) are environment variables.
No secrets are needed. Nothing sensitive is in the config, because the repo is public.

### 3.2 Schedule service

- Every `scheduleRefresh` it fetches `scheduleUrl` (a plain User-Agent, `If-None-Match`/`If-Modified-Since`
  when the server supports them, a hash of the body otherwise) and parses it.
- **Burst parser:** reads the table with a real HTML parser (AngleSharp), not regular expressions. Column
  headers map to weekdays; the row label gives the slot start; the cell's `data-show` gives the title (text
  content if absent). A slot is one hour. Consecutive cells in a column with the same title are one show.
  Empty cells mean off air.
- **LLM fallback:** used when the station has no parser, or when the parser throws or returns fewer than half
  the shows of the previous good schedule. The page text goes to Ollama with a JSON schema for the same output
  shape. LLM-derived schedules are marked `"parsedBy": "llm"` in `schedule.json`, and are not trusted to
  *remove* shows that a deterministic parse previously found, only to add or change them.
- **Output:** `/data/<station>/schedule.json`, written atomically (temp file then rename), holding: source URL,
  fetch time, content hash, parser used, the weekly grid, and concrete dated windows for the next 14 days.
  A window is `{show, start, end, recordFrom, recordTo}` in UTC plus the local times, so the behaviour across a
  DST change is visible in the file.
- **Change log:** each time the result differs from the previous schedule, a diff (added, removed, moved,
  renamed) is appended to `/data/<station>/schedule-changes.jsonl`.
- **Failure:** a failed fetch or parse never discards the last good schedule. After 3 consecutive failures a
  warning is logged on every attempt. An empty parse result is treated as a failure.

### 3.3 Window planner

Converts dated windows into open and close events. `recordFrom = start - marginBefore`,
`recordTo = end + marginAfter`. Adjacent shows therefore overlap by two margins, and two writers are open at
once. Times are computed in the station's timezone and stored in UTC. A local time that does not exist (spring
forward) moves to the next valid instant; an ambiguous one (autumn back) takes the first occurrence.

**Schedule changes apply immediately** (decision: re-check every 15 minutes and apply at once):

| Change while a window is running | Behaviour |
|---|---|
| End moved later | Writer's end is extended. |
| End moved earlier | Writer closes at the new end plus margin, or now if that is past. File kept. |
| Show removed | Writer closes now; file kept, `"truncated": true`, reason `schedule-removed`. |
| Start moved later | No effect on a window already open; audio already recorded is kept. |
| Show renamed | Folder name is not changed mid-recording; `show.json` records both names. |

A window not yet open simply follows the new schedule.

### 3.4 Stream session

- One connection per station, opened at the earliest `recordFrom` of any open window and closed after the
  last `recordTo`. If nothing is scheduled, no connection is held.
- Stream discovery runs from `listenUrl`: Revma/RadioJar widget names, `<audio>`/`<source>` elements, media
  URLs in the page, playlists (`.pls`, `.m3u`), one level of iframe. The resolved URL is cached and
  re-resolved on repeated connection failure. Candidates are probed for an audio content type.
- ICY metadata blocks, when the server sends them, are removed from the audio and each title is recorded with
  its offset. Nothing else about the bytes is changed.
- A dropped connection is retried with backoff (1 s doubling to 30 s). The gap is recorded in the metadata with
  wall-clock start and end. Audio during a gap cannot be recovered.
- Raw chunks are published to subscribers through bounded channels. A slow subscriber (the transcriber) is
  never allowed to block the writers: its channel drops oldest chunks and the drop is counted and logged.
  Writers use unbounded-in-practice buffers flushed to disk, and are the priority.

### 3.5 Recording writer

- Subscribes at `recordFrom` (or at once, if the window is already running when the service starts) and writes
  the stream bytes to `<show>.<ext>`, extension from the content type (`mp3`, `aac`, `ogg`).
- Writes to `<name>.part` and renames on close, so an incomplete file is never mistaken for a finished one. A
  crash leaves `.part` files; on start-up these are finished off with `"truncated": true`, reason `crash`.
- Up to N writers at once per station (N is 2 for Burst; there is no hard cap in code).

### 3.6 Transcriber (separate thread)

- Runs once per station while any window is open, on its own thread and its own channel, never in the writer's
  path.
- Decodes the live stream with ffmpeg (16 kHz mono PCM) in about 30 second chunks with a short overlap, sends
  each to the whisper.cpp server (`/inference`), and stores the segments on the **stream timeline** (UTC time
  of the audio, derived from wall clock at the chunk's first byte).
- Each recording's transcript is a **slice** of that timeline between its `recordFrom` and `recordTo`. Two
  overlapping shows therefore share one transcription of the overlap, not two.
- Output: `<show>.transcript.jsonl`, one segment per line: `{start, end, text}` with seconds from the start of
  that recording's audio file plus the absolute UTC time. A plain `<show>.transcript.txt` is also written.
- The whisper prompt is primed with the show title and the station's known terms (configurable glossary).
- If the whisper server is unreachable, chunks are queued to disk (bounded) and retried; audio recording is
  unaffected. A recording that ends with an incomplete transcript says so in `show.json`.

### 3.7 Enricher

- When a recording closes and its transcript slice is complete, the transcript (title, schedule slot and the
  text) goes to Ollama with a JSON schema and a low temperature.
- Requested fields: hosts, guests, topics, songs and artists mentioned, locations, competitions or calls to
  action, language, and a two or three sentence summary.
- **Every derived claim needs evidence:** each host, guest or similar entry carries a verbatim quote from the
  transcript and a confidence. A claim whose quote is not found in the transcript is dropped. This is the
  guard against invented names.
- Schedule facts (title, planned times) stay authoritative. LLM output lives under `derived`, with the model
  name, prompt version and date, and can be regenerated with a command without touching the audio.
- Long transcripts are processed in chunks and merged. If Ollama is down, enrichment is retried later and the
  recording is marked `"enrichment": "pending"`.

## 4. Output layout and metadata

```
/data/<station>/
  schedule.json
  schedule-changes.jsonl
  <yyyy>/<yyyy-MM-dd>-<HHmm>-<show-slug>/
    show.mp3
    show.json
    show.transcript.jsonl
    show.transcript.txt
```

`show.json` fields: station; show title (schedule) and any rename; scheduled start and end; recording start and
end (actual); margins; audio format detected from frame headers (codec, bitrate, sample rate, channels); stream
URL and how it was found; ICY headers and track titles with offsets; gaps; `truncated` and reason; schedule
version it came from; transcript status; and `derived` (hosts, guests, topics and so on, each with evidence
and confidence, plus model details). The JSON is the single record of "all known details".

## 5. Repository layout

```
src/WebAudioArchive.Core/      stream discovery, ICY, fan-out, window planner, schedule model
src/WebAudioArchive.Stations/  Burst parser (and future per-station parsers)
src/WebAudioArchive.Worker/    host, services, Ollama and whisper clients
tests/WebAudioArchive.Tests/   unit + integration, fixtures under tests/fixtures
config/stations.json           Burst configured
docker/Dockerfile, docker-compose.yml (worker, whisper, ollama)
docs/                          specs, operations notes
.github/workflows/ci.yml       build and test on Linux
```

Stack: .NET 10, AngleSharp for HTML, ffmpeg in the image for decoding, `Microsoft.Extensions.Hosting` for the
worker. Packages are pinned to exact versions.

## 6. Deployment

- `docker compose up` brings up the worker, a whisper.cpp server and Ollama. GPU use is opt-in through a
  compose override (`docker-compose.gpu.yml`); without it everything runs on CPU, slower than real time for
  large models, which is acceptable for a small model.
- Host not yet decided, so nothing in the image assumes one. The data directory is a volume, the timezone comes
  from config rather than the host, and the container runs as a non-root user.
- Health: `/healthz` reports per-station state (connected, windows open, last schedule fetch, last transcript
  chunk) and exits non-zero in the container health check if the schedule is more than 1 hour stale.
- Logs are structured JSON to stdout.

## 7. Testing

- **Burst parser:** against the saved fixture, asserting known cells (for example Thursday 20:00 is "Backseat
  Aux"; Monday 09:00 is "Breakfast with Mackenzie"; 7 columns by 13 rows). Expected values come from the
  fixture as fetched, not from reasoning about the page.
- **Window planner:** overlaps, back-to-back shows, DST in both directions, changes while a window is running
  (each row of the table in 3.3), restart in the middle of a window.
- **Stream session:** an in-process fake Icecast server covering ICY metadata, a mid-stream drop and reconnect
  with the gap recorded, a `.pls` playlist, and a slow subscriber that must not slow the writers.
- **Recorder:** two overlapping windows produce two files whose common section is byte-identical.
- **Enricher:** a transcript fixture with known hosts, a fake Ollama returning a hallucinated name with no
  matching quote, asserting it is dropped.
- **End to end (CI, Linux):** fake station and fake schedule page, a window created for "one minute from now",
  asserting audio file, JSON, and transcript files appear, with whisper and Ollama stubbed.
- A manual smoke test against the real Burst stream is documented but not run in CI.

## 8. Risks and open points

| Risk | Handling |
|---|---|
| Burst changes its page layout | Burst parser fails closed (last good schedule kept), LLM fallback proposes a schedule, a warning is logged. |
| Page lists a show but the station plays something else | Out of scope; the schedule is the source of truth. The transcript and derived fields help a human spot it. |
| Last slot ends at 22:00 | Assumed from the 21:00 row. Confirm against the stream; if Burst runs later, it is a config option. |
| Real-time transcription needs a GPU | CPU mode works with a small model; the transcript may lag but recording is unaffected. |
| 15-minute immediate application can cut a recording on a page glitch | A parse that drops shows needs two consecutive agreeing fetches before it can *remove* a running window. |
| Public repo and copyright | Code only in git. README states recordings are private archive copies and must not be republished. |
| Station terms of use | Recording is by the operator for personal use; the schedule fetch is polite (15 minute interval, conditional requests). |

## 9. Decisions recorded

- Repo `davidnmbond/WebAudioArchive`, public, personal GitHub account. No panoramicdata code is copied in;
  the stream code is written fresh.
- C# on .NET 10, Docker, whisper.cpp plus Ollama for local AI.
- Schedule: per-station parser with LLM fallback, re-checked every 15 minutes, changes applied immediately.
- Host and recordings location: undecided; the design is host-neutral.

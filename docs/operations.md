# Operations

## Run

The recorders are their own compose project (`waa-recorders`), so building or restarting anything else never recreates them.
First install only (no recorders running yet): start the recorders first (they create the shared network `waa`), then the rest:

    docker compose -f docker/docker-compose.recorders.yml up -d --build recorder-a recorder-b
    docker compose -f docker/docker-compose.yml up -d --build web consolidator

This starts both recorders together, so use it only when neither is recording. For every later change, upgrade one
recorder at a time as described under "Dual recording (A and B)", and never stop both together. Always name the
services, and never use `--remove-orphans`.

Set `WAA_INSTANCE_ID` (for example `home` or `cluster`) and `WAA_DATA_DIR` in the environment or a `.env` file
beside the compose file. On Linux the data folder must be writable by uid 1654.

## Where things are

- Audio: `<data>/<station>/master/<yyyy>/<MM-dd>/<HHmmss>.mp3`, sidecar `.json` beside it. UTC throughout.
- A `.mp3.part` file is the segment being written. After a crash it is finished on the next start
  (`endedBy: crash`).
- Health: `http://127.0.0.1:8080/healthz` for recorder A and `http://127.0.0.1:8081/healthz` for recorder B (503 if an enabled station has written no frame for 2 minutes after the
  first 3 minutes of uptime).

## Reading the sidecar

`gaps` lists every break in the stream (start, end, seconds, reason) that ended inside the segment;
`endedBy` is `hour-boundary`, `shutdown` or `crash`; `titles` are ICY track titles with the file byte offset.
Segment spans minus gaps give the coverage figure on `/healthz` (24 hours and 30 days).

## Known limits (plan 1)

MP3 streams only; one UTC-hour segment length; no schedule, show cutting or transcript yet (the web app MVP below
makes clips on request); no disk-space guard (plan 5). Two instances are independent and are not reconciled.

- Gaps, titles and stream info for a segment are held in memory and written to the sidecar when the segment
  closes, so after a crash the recovered segment's sidecar has `endedBy: crash` but no gaps or titles.
- Revma start offset: the audio at connect starts about 5 s before the arrival timestamp (214.15 s of audio in 209 s of
  wall time, a fixed offset). `startUtc`, title times and gap times are arrival times, so `audioSeconds` can exceed
  `endUtc - startUtc`. Each reconnect may also replay about 5 s of buffered audio, so consecutive segments can overlap
  slightly. Overlap on reconnect is not verified live.
- `reconnects` on `/healthz` counts connection attempts that ended, including failed attempts and stalls. The
  coverage figures exclude the segment currently being written.
- Health is based on the last frame written to disk (`lastFrameWrittenUtc`), not on bytes received. A recorder
  failure (unwritable or full disk) shows as `lastError`, the connection is dropped and retried with a growing backoff.
- The worker exits with code 3 at start-up if the data folder is not writable.
- Segments are named by UTC start with a `-n` suffix for same-second restarts; order segments by the sidecar
  `startUtc`, not by file name (`103000-1.mp3` sorts before `103000.mp3`).

- Upgrading the container interrupts recording for the length of a restart (seconds); the break is recorded as a gap
  in the sidecar. The recorders are split from the rest (their own compose project) and run as A and B, upgraded one
  at a time, so the other keeps recording during a restart (see Dual recording).

## Dual recording (A and B)

The recorders compose file (`docker/docker-compose.recorders.yml`) runs two recorders, `waa-recorder-a` and `waa-recorder-b`, each with its own stream connection,
instance id (`<WAA_INSTANCE_ID>-a`, `-b`) and data folder (`data/a`, `data/b`). They do not talk to each other.
Their segments overlap and are not byte-identical (see the Known limits). To get a period, use whichever
recorder has no gap in it (read each segment's `.json`), or stitch from both.

Upgrade one at a time and never stop both together:

    docker compose -f docker/docker-compose.recorders.yml up -d --build recorder-a
    curl http://127.0.0.1:8080/healthz      # wait for connected true and a recent lastFrameWrittenUtc
    docker compose -f docker/docker-compose.recorders.yml up -d --build recorder-b

One-time migration from the old single project (when the recorders still belong to the `docker` project). Do one
recorder at a time and never use `--remove-orphans`:

1. `docker compose -f docker/docker-compose.recorders.yml build recorder-a`
2. `docker stop waa-recorder-a; docker rm waa-recorder-a`, then
   `docker compose -f docker/docker-compose.recorders.yml up -d recorder-a`. Wait for `/healthz` to show connected and
   a frame in the last 10 s.
3. The same for `recorder-b` (ports 8081).
4. `docker compose -f docker/docker-compose.yml up -d --build web consolidator` (always name services; never
   `--remove-orphans`).

Containers use `restart: always`, so Docker restarts them after a crash. They only run while Docker Desktop is
running: Docker Desktop must start on login, the computer must not sleep, and a reboot (for example for Windows
Update) stops recording until Docker Desktop is back. This setup does not protect against the host or its disk
failing.

## Web app (MVP)

Open `http://127.0.0.1:8090`. The container is `waa-web` (image `webaudioarchive-web:local`, built from
`docker/Dockerfile.web`). It is separate from the recorders: it reads their folders through a **read-only** mount of
`data/` (`/data/a`, `/data/b`) and asks each one for `/healthz` over the compose network. It never writes to the data;
the only place it writes is its clip cache (the `web-cache` volume, mounted at `/cache`).

**No sign-in.** This MVP has no authentication. Anyone who can reach the port can play and download every recording,
so the port is published on `127.0.0.1` only. Do not publish it on another address or put it behind a tunnel until
Google sign-in (spec 3.8) is built.

**UTC only.** Every time shown, typed or returned by the API is UTC and labelled "UTC".

Pages:

- **Status** (`/`): per recorder, a green, amber or red state from its `/healthz` (green: connected and a frame written
  in the last 10 s; amber: reconnecting, a recent write error, frames 10 to 120 s late, or no health URL; red: health
  URL unreachable or no frame for over 120 s), connected, seconds since the last frame, last error, current segment
  start, reconnects, uptime, and coverage over the last 24 hours. Coverage is computed by the web app from the sidecars
  (segment spans minus gaps) plus the open `.part` up to its last write time. Refreshes every 15 s. Above the recorder
  cards, **Station: A and B together** gives the coverage of the last 24 hours by any recorder (the union of A and B)
  and lists, newest first, the stretches of 1.5 s or more that neither recorder has (up to 10; the Recordings calendar
  shows the rest). A gap in one recorder that the other covers is not listed there.
- **Shows** (`/shows`): the station's schedule in a `PDCalendar` (week by default; month and agenda; weeks start on
  Monday; the calendar runs in UTC and every time is labelled UTC). `?view=month` and `?date=2026-10-26` (a UTC date)
  choose the first view. Each show is marked, by colour, icon and tooltip: **recorded** (A and B together have all of
  it, within 2 s; the tooltip says when no single recorder does), **partly recorded** (some of it is missing from both
  recorders), **not recorded**,
  **on air now** (measured up to now, counting the open `.part`) or **in the future**. Above the calendar: where the
  schedule came from and when, or a warning (see "Schedule" below).
- **Show** (`/shows/<station>-<yyyyMMdd>T<HHmm>Z`, the UTC start, for example `/shows/burst-20261015T1900Z`): the one
  place audio is played. A toolbar along the top has the title, UTC start and end, the coverage badge, a margin
  (default **5 minutes** before and after, 0 to 60), a recorder (Auto, A, B) and Update. The coverage badge opens the
  coverage per recorder with the uncovered stretches listed in UTC, a row for A and B together listing what is missing
  from both, the clip's real start and end and the planner's notes. When only the two recorders together have all of
  the show, a warning says the player uses one recorder and so has that recorder's gaps (joining A and B is issue #12).
  Below the toolbar the show's clip (from the show's start minus the margin to its end plus the margin, or to now while
  it is on air) plays in `PDMediaPlayer` (speeds 0.75x to 2x with pitch kept, back 15 s, forward 30 s, previous and next
  marker, a download named like `burst_20261015T185500Z_20261015T200500Z_A.mp3`): the show proper is the shaded range
  of the seek bar; markers at the show's start and end, recording gaps, recorder restarts and detected events; a lane
  with the lead-in, show and tail. Beside it, the **UTC wall time** of the current position. Below the player, a
  splitter (`PDSplitter`, needs `lib/splitjs/split.min.js`): on the left every event in time order (lead-in, show,
  tail, gaps, restarts, talk sections from the transcript, detected swearing, filler, clipping and dead air, and songs;
  review-only swear hits are never listed); on the right the clip's transcript, with the selected event's lines
  highlighted, or the song's metadata (title, artist, times, confidence, source) when a song is selected. Selecting an
  event or a transcript line moves the player there (a detected event 5 s before it); the event and line playing now
  are marked as it plays. `?at=<UTC time ending in Z>` selects what is playing at that time and starts the player 5 s
  before it. A show that has not started says "Not yet broadcast"; a partly recorded one plays what exists with a
  warning; one with nothing recorded says so. The id is the station and UTC start, so a link works after a restart or
  reload as long as the schedule still has a show starting then. Hours with nothing scheduled are pages too, titled
  "Off air (unscheduled)": the UTC hour, cut short by any show that ends or starts within it.
- **At** (`/at?utc=2026-10-15T19:10:00Z&recorder=a`): redirects to the show on air at that UTC time (or that off-air
  hour) with `at` set, so analysis event links and the Recordings page open the right moment.
- **Recordings** (`/recordings`): a week calendar (`PDCalendar`, UTC) of each recorder's segments (A and B side by side,
  the open segment dashed), recording gaps (red; sidecar gaps and missing audio between segments) and the stretches
  missing from both recorders ("A+B: missing", dark red with a heavy outline; from the first audio of either recorder up
  to now). Selecting a segment, gap or missing stretch opens the show on air at its start, as the table's Play button
  does. Below it, the table of every segment from both recorders (recorder, start, end, audio duration, size,
  ended by, number of gaps, open), newest first, with a UTC date filter.
- **Consolidation** (`/consolidation`, only when `WAA_CONSOLIDATOR` is set, for example `http://consolidator:8080`): the
  originals the consolidator holds and has rechecked as ready to delete, by UTC day, each with a checkbox. Opening the
  page deletes nothing. Choose originals, press "Delete confirmed originals", read the list and press "Yes, delete N
  originals"; the web app then posts them to the consolidator (`confirmedBy` is `loopback` until sign-in exists), which
  still refuses any original whose recheck is older than 24 hours or that has changed, and each item shows its own
  result. The Status page gains a Consolidation card (mode, last merged hour in UTC, backlog, waiting for analysis,
  failures, held, ready to delete, space freed): amber when the consolidator does not answer, red when an hour failed
  to merge, the last run failed or a held original failed its recheck.

Rendering without flicker. The pages are prerendered on the server and then taken over by the interactive circuit,
which draws them again from scratch; each page makes that second render match the first, so the hand-over shows no
change:

- Status shows the last reading at once (with the time it was read) and refreshes behind it, rather than drawing
  "Reading the recorders..." between the prerendered cards and the new ones. A reading older than 15 s is read again
  before a page is prerendered.
- Show takes a clip that is already in the cache in the same render, so the page does not draw the busy
  overlay without a player first. Only a clip that has to be built shows the overlay.
- The calendars keep their scroll position: Shows keeps where its week grid was scrolled to (per browser tab) through
  the hand-over, view switches and Back from a show; Recordings starts its grid at three hours before now. Both are
  placed by `wwwroot/js/calendar-state.js` before the frame is painted (PDCalendar's own ScrollToHour scrolls after a
  frame at the top has been drawn). The Shows week and view are kept in the address (`?view=...&date=...`), so Back
  from a show returns to the same week.
- The Recordings table says "Loading recordings..." until it has its rows (PDTable reads them after its first render).
- The Font Awesome fonts are preloaded, so icons do not appear after the text and shift it.
- Idle pages do not churn: the Shows calendar changes only its current time line once a minute, and Status changes only
  the text of its readings every 15 s.

Schedule (the Shows tab):

- The web app reads the station's `scheduleUrl` and `timezone` from the stations config (`WAA_STATIONS_CONFIG`; in
  compose `../config` is mounted read-only at `/config` and the variable is `/config/stations.json`). It fetches the
  page at start-up and then every **15 minutes** (20 s timeout, User-Agent `WebAudioArchive/1.0`), and parses it with
  a parser for Burst's table (header Monday to Sunday, one row per hour from 09:00 to 21:00, `data-show` as the title;
  a show filling consecutive hours of a day, by `rowspan` or by the same title repeated, is one show; an empty cell is
  off air). The parser fails closed: if the table is not exactly what it expects, it reports why and returns nothing.
- **Last good schedule:** a good parse replaces the schedule and is saved to `/cache/schedule/<station>.json` (the
  cache volume, so it survives a restart and is used until the first fetch after a restart). A failed fetch or a page
  that does not parse never replaces it: the Shows page keeps showing the last good schedule with a warning giving the
  error and when the last good one was fetched. If there has never been a good schedule, the Shows page says
  "Schedule unavailable" with the reason and a link to the Recordings page. A schedule problem never stops the app.
- **UTC rule and the clock change:** the page states no time zone; its times are UK local time, so the configured
  `Europe/London` is applied **only** when turning a weekly slot into dated UTC instants. Nothing local is stored or
  shown. In British Summer Time (UTC+1) a 20:00 show is 19:00 UTC; in GMT (UTC+0) it is 20:00 UTC. On 25 October 2026
  the clocks go back at 02:00, so from the week of 26 October every show is an hour later in UTC; on 28 March 2027
  they go forward and the shows move an hour earlier in UTC. A local time skipped by the spring change moves to the
  next valid instant; one repeated in the autumn takes the first.
- Not built yet (issue #8): the LLM fallback, `schedule.json` and a change log under `/data`, and cutting shows to
  files; the web app makes each show's clip on request from the masters.

API:

- `GET /api/clip?station=burst&from=<UTC>&to=<UTC>&marginBefore=<min>&marginAfter=<min>&recorder=auto|a|b` returns
  JSON: `id`, `url`, `downloadUrl`, `downloadName`, `recorder`, `from` and `to` (what the clip really covers), `seconds`,
  `bytes`, `warnings`, `notes`, `coverageSeconds` per recorder, the `pieces` joined, `gaps` (each with `fromUtc`,
  `toUtc`, `seconds`, `reason` and `clipSeconds`, where it falls in the clip) and `restarts` (`atUtc`, `endedBy`,
  `clipSeconds`). 400 with `{ "error": ... }` for
  bad input (from not before to, a window ending in the future, longer than 4 hours, unknown station or recorder,
  margins outside 0 to 60 minutes); 404 when nothing was recorded in the window.
- `GET /media/clip/<id>.mp3` serves the cached clip as `audio/mpeg` with range requests (206); add `?download=1` for a
  `Content-Disposition` download name.
- `GET /healthz` is 200 when every configured data root exists (used by the container health check).

How clips are made and how accurate they are:

- Auto picks the recorder whose segments (minus gaps) cover most of the window; ties go to A. When that recorder does
  not run unbroken through the window (it lacks part of it, or has a restart or gap inside it), the clip is joined from
  A and B (issue #12): every frame in the window is listed from both recorders and matched by its bytes (A and B
  receive identical frames), so the clip switches recorder exactly where one has audio the other lacks, never repeats
  audio, and drops a reconnect's replayed frames precisely. The clip's recorder then shows as `A+B`, the Pieces tab
  names the recorder of each piece, and the only gaps reported are what neither recorder has. If the other recorder
  adds nothing, the clip stays with one recorder but its restarts are still joined frame by frame. Choosing A or B
  explicitly never mixes recorders.
- Position estimates come from the bitrate, using one model shared with analysis (`SegmentTimeline` in Core). When a
  stream stalls before it drops, a segment holds less audio than its wall time minus its recorded gaps; that
  shortfall is placed just before the gaps, not at the end of the hour, and coverage does not count it as recorded
  (issue #106). A stretch missing from both recorders is therefore placed to within about a second.
- A gap or break inside the window is joined straight across (no silence is inserted); a warning gives its UTC times.
  A join after a segment that ended by `crash` or `shutdown` is also warned.
- Wall time maps to a byte offset by the stream bitrate (128 kbps is 16000 bytes a second), allowing for the Revma start
  offset (byte 0 of a segment that began on a connect holds audio from about 5 s before its `startUtc`). The cut is then
  moved to a real MP3 frame boundary (three consecutive valid frame headers), so a clip never starts or ends mid-frame,
  and every finished clip is checked to be whole frames. **Positions are accurate to about a second**; after a reconnect
  inside a segment they can be off by a few seconds, because the replayed audio at a reconnect is estimated.
- The open segment (`.mp3.part`) can be clipped while it is written; its gaps are not known until the hour closes.
- Marker positions, the shaded show range and the UTC wall time beside the player come from UTC times mapped through
  the clip's timeline (where each piece starts and where the audio jumps across a gap), so they have the same accuracy:
  about a second. A time inside a gap is shown where the audio jumps over it.
- A one minute clip is about 1 MB. The longest clip is 4 hours (about 230 MB); margins that would pass that are
  reduced, with a warning.

Cache: clips are kept in `/cache` named by a hash of the exact pieces (including each file's size and time, so a
growing `.part` gives a new clip). Housekeeping at start-up and daily removes clips older than 24 hours and then the
oldest until the cache is under 2 GB.

Settings (environment variables): `WAA_STATION` (default `burst`), `WAA_RECORDERS` (`a=/data/a,b=/data/b`, required;
start-up fails naming the setting if it is missing or a root does not exist), `WAA_RECORDER_HEALTH`
(`a=http://recorder-a:8080/healthz,b=http://recorder-b:8080/healthz`), `WAA_CACHE` (default `/cache`), `WAA_PORT`
(default 8080 inside the container), `WAA_STATIONS_CONFIG` (the stations config with the schedule URL and time zone;
without it the Shows tab says the schedule is unavailable and everything else works).

Upgrading: the recorders and the web app are upgraded independently. A web change never needs a recorder restart, and
the recorders must not be restarted for one. Rebuild and restart only the web app with:

    docker compose -f docker/docker-compose.yml up -d --build web

The consolidator (`waa-consolidator`, the recorder image started with `--consolidate`, health at
`http://127.0.0.1:8096/healthz`) is in the same file and is restarted the same way, naming `consolidator`.
Look and feel: the pages follow the operating system's light or dark preference ("Auto"), or a choice of Light or
Dark from the toggle at the top right, remembered in the browser (localStorage; without it the page simply follows
the OS). The theme is set by a small script in the page head before anything is drawn, so there is no flash of the
wrong theme. The UI uses the PanoramicData.Blazor components, pinned to **10.0.366** (toolbar, table and pager, tab
set, progress bar, date picker, text boxes, combo box, toggle switch, buttons, busy overlay, `PDCalendar` and
`PDMediaPlayer`; the player's script is a module served from `_content/PanoramicData.Blazor`). Calendar and player
colours come from Bootstrap's variables (`wwwroot/css/app.css`), so they follow the Light and Dark choice.

Vendored front-end files (served from the container, no CDN, so the app works offline):

- Bootstrap 5.3.3 CSS (`wwwroot/lib/bootstrap`, MIT, licence beside it).
- Font Awesome Free 5.14.0 (`wwwroot/lib/font-awesome`: `css/all.min.css` and the woff2, woff and ttf fonts), the same
  version the PanoramicData.Blazor demo pins; the library's icons (pager, sort arrows, buttons) need it. Licence file
  beside it: icons CC BY 4.0, fonts SIL OFL 1.1, code MIT.

## Analysis container (plan 3)

The analysis service (transcripts, detectors, events, show enrichment) is its own image (`docker/Dockerfile.analysis`, one
CPU-only runtime stage, about 234 MB of filesystem content) and its own compose project, **`waa-analysis`**, defined in
`docker/docker-compose.analysis.yml`. It is deliberately not part of `docker/docker-compose.yml`, so no command for the
recorder project can recreate it and no command for this one can touch a recorder or the web app. Since decision D19 (the
Kubernetes cluster has no GPU nodes) speech to text is an HTTP call to a whisper.cpp server container on the owner's GPU
servers (pdl-rune-02 test, pdl-rune-01 production, port 8080); the image has no Whisper.net, no CUDA, no GPU override and
no models mount (it was about 2.0 GB with the CUDA target and 376 MB for the old CPU target, plan 3 D18 evidence). There is no
ffmpeg and no whisper container in this compose project. See `DESIGN.md`.

For Kubernetes (no GPU nodes) the same service is deployed by the kustomize base in `k8s/analysis` (analysis only, never the recorders): see [k8s/README.md](../k8s/README.md).

Before the first start (owner actions): the whisper.cpp server must be running on the GPU server (plan 3 Task 23, issue #111;
the service starts without it and shows `whisperReady: false` and `paused` until it answers), create the output folder
`data/analysis`, and make sure the web app has saved the schedule once (the analysis reads it from the web app's `web-cache`
volume, read-only).

What it may touch: `data/a` and `data/b` are mounted read-only; it writes only `data/analysis` and a 2 GiB tmpfs
scratch; `config/` is read-only. It runs as the image's non-root user (UID 1654, set `APP_UID` if your
`data/analysis` folder is owned by someone else), under `nice 10`, `cpu_shares: 256`, `cpus: 6`, `mem_limit: 6g`,
`oom_score_adj: 800`. Its `/healthz` is published on `127.0.0.1:8095` only. It reaches the owner's Ollama at
`http://pdl-rune-02.panoramicdata.com:11434` (test; production uses pdl-rune-01), named in `WAA_OLLAMA_ALLOWED_HOSTS`, and the
whisper server at `http://pdl-rune-02.panoramicdata.com:8080`, named in `WAA_WHISPER_ALLOWED_HOSTS`; transcript text and speech audio
cross the private network over plain HTTP (see `DESIGN.md`, "What leaves which machine").

**Stopping.** `stop_grace_period` is 90 s (never below 60 s): a graceful stop finishes or aborts the current chunk
and restores the job's attempts before Docker would kill the container. Use `docker compose ... stop analysis` (or
`down`) rather than `docker kill`.

Commands (always from the repository root, always naming the service; these never touch the recorders):

    docker compose -f docker/docker-compose.analysis.yml up -d --build analysis
    # test against the other server: WAA_WHISPER_URL=http://pdl-rune-01.panoramicdata.com:8080 docker compose ... up -d analysis
    curl http://127.0.0.1:8095/healthz

The `docker_web-cache` volume belongs to the main project in `docker/docker-compose.yml` (named after its implicit compose project
plus `_web-cache`). When that project is restructured or renamed, find the new name with `docker volume ls` and set
`WAA_WEB_CACHE_VOLUME` to it in the shell or in `.env`.

**Showing the analysis in the web app.** The web service needs `WAA_ANALYSIS=/data/analysis` and a read-only view of
`data/analysis`. That is `docker/docker-compose.web-analysis.yml`, which adds only those two things to the `web`
service. It must be merged with the existing web service definition (the web app's file, `docker/docker-compose.yml`), never used alone:

    docker compose -f docker/docker-compose.yml -f docker/docker-compose.web-analysis.yml up -d web

(Name only `web`: this recreates the web container, which is rebuilt on its own and never touches a recorder. Do it
when you may interrupt the web app for a few seconds.) If that compose file has moved by then, use
its new path in place of `docker/docker-compose.yml`. The current web service already mounts `data/` read-only, so
`data/analysis` is visible read-only there; the override makes the mount and the setting explicit.

**Analysis health (`http://127.0.0.1:8095/healthz`).** `status` is one of: `ok` (200); `catching-up` (200: lag is above the
6 hour alarm only because of the backlog, a job finished within the stall window; expected for days after the first start, watch
`backlogAudioHours` and `catchUpEtaHours` instead of `lagSeconds`); `paused` (200: low disk or the whisper server not ready, `paused` says
why); `lagging` (503: lag above the alarm and no job finished within the stall window, so nothing is progressing); `stalled`
(503: no sign of life for 15 minutes, or a job running longer than the stall window); `failing` (503: whole passes keep
failing, see `lastPassError`). Full detail, the settings table (`WAA_WHISPER_*`), the live contract check of the whisper server
(`--check-whisper`), the first-run checklist, the maintenance commands (stop the service first), the GPU sharing with Ollama and the
warning about the web override are in `docs/analysis.md`.

For trying the service without the rune servers there is a development-only local whisper container,
`docker/docker-compose.whisper-dev.yml` (its own compose project `waa-whisper-dev`, loopback only, needs an NVIDIA GPU and the model in
`models/`); production never uses it.

CI builds the one CPU image and validates the compose files (analysis, the dev whisper file, and the recorder project with the web
override) with `docker compose config`, which starts nothing.

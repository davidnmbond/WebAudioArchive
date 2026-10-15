# Operations

## Run

    docker compose -f docker/docker-compose.yml up -d --build

That starts everything the first time. Once the recorders are running, always name a service (`recorder-a`,
`recorder-b` or `web`); an `up` without a service name may rebuild and restart the recorders.

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
  in the sidecar. Splitting the recorder from the rest and running A/B recorders to avoid this is specified in
  spec section 3.13 and is not built yet.

## Dual recording (A and B)

The compose file runs two recorders, `waa-recorder-a` and `waa-recorder-b`, each with its own stream connection,
instance id (`<WAA_INSTANCE_ID>-a`, `-b`) and data folder (`data/a`, `data/b`). They do not talk to each other.
Their segments overlap and are not byte-identical (see the Known limits). To get a period, use whichever
recorder has no gap in it (read each segment's `.json`), or stitch from both.

Upgrade one at a time and never stop both together:

    docker compose -f docker/docker-compose.yml up -d --build recorder-a
    curl http://127.0.0.1:8080/healthz      # wait for connected true and a recent lastFrameWrittenUtc
    docker compose -f docker/docker-compose.yml up -d --build recorder-b

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

**UTC only.** Every time shown, typed or returned by the API is UTC and labelled "UTC". The Player's From and To are
UTC times of day on the chosen UTC date; a To earlier than From means the next day.

Pages:

- **Status** (`/`): per recorder, a green, amber or red state from its `/healthz` (green: connected and a frame written
  in the last 10 s; amber: reconnecting, a recent write error, frames 10 to 120 s late, or no health URL; red: health
  URL unreachable or no frame for over 120 s), connected, seconds since the last frame, last error, current segment
  start, reconnects, uptime, and coverage over the last 24 hours. Coverage is computed by the web app from the sidecars
  (segment spans minus gaps) plus the open `.part` up to its last write time. Refreshes every 15 s.
- **Recordings** (`/recordings`): every segment from both recorders (recorder, start, end, audio duration, size, ended
  by, number of gaps, open), newest first, with a UTC date filter. Play opens the Player for that segment.
- **Player** (`/player`): date, From, To, "add N minutes before and after" (off by default, 5 minutes), recorder (Auto,
  A, B). Prepare clip shows the recorder used, the clip's real start and end, duration, size and warnings, then plays it
  (speed 0.75x to 2x, back 15 s, forward 30 s, seek bar with the UTC wall time) with a download link named like
  `burst_20261015T151500Z_20261015T151600Z_A.mp3`. A link such as
  `/player?from=2026-10-15T15:15:00Z&to=2026-10-15T15:16:00Z&recorder=auto` prepares the clip on load.

API:

- `GET /api/clip?station=burst&from=<UTC>&to=<UTC>&marginBefore=<min>&marginAfter=<min>&recorder=auto|a|b` returns
  JSON: `id`, `url`, `downloadUrl`, `downloadName`, `recorder`, `from` and `to` (what the clip really covers), `seconds`,
  `bytes`, `warnings`, `notes`, `coverageSeconds` per recorder and the `pieces` joined. 400 with `{ "error": ... }` for
  bad input (from not before to, a window ending in the future, longer than 4 hours, unknown station or recorder,
  margins outside 0 to 60 minutes); 404 when nothing was recorded in the window.
- `GET /media/clip/<id>.mp3` serves the cached clip as `audio/mpeg` with range requests (206); add `?download=1` for a
  `Content-Disposition` download name.
- `GET /healthz` is 200 when every configured data root exists (used by the container health check).

How clips are made and how accurate they are:

- Recorders are never mixed in one clip. Auto picks the recorder whose segments (minus gaps) cover most of the window;
  ties go to A. A warning says when the other recorder would have covered more, or when neither covers it all.
- A gap or break inside the window is joined straight across (no silence is inserted); a warning gives its UTC times.
  A join after a segment that ended by `crash` or `shutdown` is also warned.
- Wall time maps to a byte offset by the stream bitrate (128 kbps is 16000 bytes a second), allowing for the Revma start
  offset (byte 0 of a segment that began on a connect holds audio from about 5 s before its `startUtc`). The cut is then
  moved to a real MP3 frame boundary (three consecutive valid frame headers), so a clip never starts or ends mid-frame,
  and every finished clip is checked to be whole frames. **Positions are accurate to about a second**; after a reconnect
  inside a segment they can be off by a few seconds, because the replayed audio at a reconnect is estimated.
- The open segment (`.mp3.part`) can be clipped while it is written; its gaps are not known until the hour closes.
- A one minute clip is about 1 MB. The longest clip is 4 hours (about 230 MB); margins that would pass that are
  reduced, with a warning.

Cache: clips are kept in `/cache` named by a hash of the exact pieces (including each file's size and time, so a
growing `.part` gives a new clip). Housekeeping at start-up and daily removes clips older than 24 hours and then the
oldest until the cache is under 2 GB.

Settings (environment variables): `WAA_STATION` (default `burst`), `WAA_RECORDERS` (`a=/data/a,b=/data/b`, required;
start-up fails naming the setting if it is missing or a root does not exist), `WAA_RECORDER_HEALTH`
(`a=http://recorder-a:8080/healthz,b=http://recorder-b:8080/healthz`), `WAA_CACHE` (default `/cache`), `WAA_PORT`
(default 8080 inside the container).

Upgrading: the recorders and the web app are upgraded independently. A web change never needs a recorder restart, and
the recorders must not be restarted for one. Rebuild and restart only the web app with:

    docker compose -f docker/docker-compose.yml up -d --build web

Look and feel: the pages follow the operating system's light or dark preference ("Auto"), or a choice of Light or
Dark from the toggle at the top right, remembered in the browser (localStorage; without it the page simply follows
the OS). The theme is set by a small script in the page head before anything is drawn, so there is no flash of the
wrong theme. The UI uses the PanoramicData.Blazor components (toolbar, table and pager, tab set, progress bar, date
picker, text boxes, combo box, toggle switch, buttons, busy overlay).

Vendored front-end files (served from the container, no CDN, so the app works offline):

- Bootstrap 5.3.3 CSS (`wwwroot/lib/bootstrap`, MIT, licence beside it).
- Font Awesome Free 5.14.0 (`wwwroot/lib/font-awesome`: `css/all.min.css` and the woff2, woff and ttf fonts), the same
  version the PanoramicData.Blazor demo pins; the library's icons (pager, sort arrows, buttons) need it. Licence file
  beside it: icons CC BY 4.0, fonts SIL OFL 1.1, code MIT.

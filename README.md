# WebAudioArchive

Records web radio streams around the clock as gapless MP3 segments, with sidecar metadata, health and coverage
reporting. Burst Radio is the primary use case.

Recordings are private archive copies and must not be republished. Nothing in this repository contains
recordings, credentials or user data.

Design overview with diagrams: [DESIGN.md](DESIGN.md). Detailed spec: `docs/superpowers/specs/`. Plans: `docs/superpowers/plans/`.

## Run

    docker compose -f docker/docker-compose.yml up --build

That is for the first start only. Once the recorders are running, always name the service you mean (for example
`up -d --build web`), so a recorder is never rebuilt or restarted by accident.

Two independent recorders run (A and B). Recordings appear under `data/a/` and `data/b/`, each as `<station>/master/<yyyy>/<MM-dd>/<HHmmss>.mp3` with a `.json` beside each.
The data folder must be writable by uid 1654 (the container user): `sudo chown 1654:1654 data` on Linux.
Health: `http://127.0.0.1:8080/healthz` (recorder A) and `http://127.0.0.1:8081/healthz` (recorder B).

## Web app

Open `http://127.0.0.1:8090`. Every time on it is UTC and labelled "UTC".

- **Status**: each recorder's health (read from its `/healthz` by the server) in green, amber or red, and its coverage
  over the last 24 hours.
- **Shows**: the station's schedule as a calendar (week by default, month and agenda too, weeks start on Monday, all
  times UTC), each show marked recorded, partly recorded, not recorded, on air now or in the future. The schedule is
  read from Burst's own page at start-up and every 15 minutes; Burst lists UK local times, which are converted to UTC
  when the page is read (a 20:00 show is 19:00 UTC in British Summer Time and 20:00 UTC in GMT, so the UTC times move
  by an hour when the clocks change). Selecting a show opens `/shows/<id>` (for example `/shows/burst-20261015T1900Z`),
  the one place audio is played: a toolbar with its UTC times, coverage (open it for the per-recorder detail), margin
  (**5 minutes** either side by default) and recorder; then the player (speeds 0.75x to 2x, back 15 s, forward 30 s,
  the show proper shaded on the seek bar, markers at its start and end, gaps, restarts and detected events, the UTC
  wall time, a download button); then a splitter with every event on the left (margins, the show, gaps, talk,
  detected events, songs) and the transcript on the right, or the song's details when a song is selected. A show
  that has not started says "not yet broadcast". Hours with nothing scheduled have an "Off air (unscheduled)" page.
- **At**: `/at?utc=2026-10-15T19:10:00Z&recorder=a` opens the show on air at that UTC time, at that moment.
- **Recordings**: a week calendar of each recorder's segments and recording gaps (select one to open the show on air
  then) above the table of every segment from both recorders, newest first, with a UTC date filter and a Play button.
- **Clip API**: `GET /api/clip?from=...&to=...&marginBefore=5&marginAfter=5&recorder=auto` returns JSON with the clip's
  URL (`/media/clip/<id>.mp3`, range requests supported, `?download=1` for a named download), and its gaps and
  restarts with their UTC times and where they fall in the clip.

**There is no sign-in in this version.** The web container is published on 127.0.0.1 only and must not be exposed
to a network. Google sign-in comes later. The web container mounts the data read-only, so it cannot change a
recording. Clip positions are accurate to about a second. The UI uses PanoramicData.Blazor 10.0.366 (including
`PDCalendar` and `PDMediaPlayer`). See `docs/operations.md`.

The web app is rebuilt and restarted on its own, never touching a recorder:

    docker compose -f docker/docker-compose.yml up -d --build web

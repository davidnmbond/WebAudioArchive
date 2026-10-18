# Analysis (plan 3)

The analysis service turns the archive into transcripts, detected events and per-show facts. It is a separate container
(`waa-analysis`) with its own image and its own compose project, below the recorders' priority, that runs whisper.cpp in
process (Whisper.net) on the GPU or the CPU. It never writes to the recordings: it reads closed segments read-only, copies
each one into a 2 GiB tmpfs scratch, decodes the copy, and writes only under `data/analysis`. All times are UTC. Every count
is a detection from speech to text and audio levels, not a proven event.

**Planned change (decision D19, 2026-10-17; not built yet).** Production runs in a Kubernetes cluster with no GPU, so
whisper is moving out of this container: the analysis service will send each chunk (a 16 kHz mono WAV of about 5 minutes)
over HTTP to a whisper.cpp server on the owner's GPU servers (`pdl-rune-02` for test, `pdl-rune-01` for production), the
same servers that run Ollama. The container becomes CPU only: no CUDA image, no GPU override, no model files here. Until
that lands (plan 3 Tasks 20 to 23, issues #108 to #111), the settings and commands below for the in-process engine still
apply. Speech audio will then leave this machine, but only to the named server. Overview and diagrams: `DESIGN.md`.

## What it does

One work lane, one pass every 60 seconds:

1. Stops at once if free disk is below `WAA_MIN_FREE_GB`, so the recorders keep the space.
2. Plans the work: every closed segment of recorder A, newest first; a segment of B only where A covers under 98 percent of it.
3. Per segment: decode, features, transcription in chunks (progress saved after each), then the detectors (swearing, fillers,
   clipping, dead air) into `events.json`.
4. Rebuilds the day timelines and the show files (`show-analysis.json`) for what changed.
5. When no segment work is ready, enriches up to two finished shows with the LLM on the owner's Ollama server (pdl-rune-02 for test, pdl-rune-01 for production): hosts, guests, topics, songs.

## Files

Under `data/analysis/<station>/`:

| Path | Content |
|---|---|
| `segments/<recorder>/<yyyy>/<MM-dd>/<hhmmss>/state.json` | what the job was made from, how far it got, attempts, the model identity |
| `.../features.json` | decoded audio features (levels, clip runs) |
| `.../transcript.json` | the transcript with its timeline, engine, model name, model size and device |
| `.../events.json` | the detector events of that segment |
| `events/<yyyy>/<MM-dd>.json` | the UTC day's merged timeline |
| `shows/<yyyy>/<show id>/show-analysis.json`, `enrichment.json` | per-show counts and the LLM-derived facts |

`data/analysis/.analysis-service.lock` shows that the service is running (see The lock). JSON files are written to a
temporary name and renamed, so a reader never sees half a file.

## Settings

Environment variables (compose sets the defaults):

| Variable | Default | Meaning |
|---|---|---|
| `WAA_MIN_FREE_GB` | 20 | below this much free disk the service pauses |
| `WAA_WHISPER_MODEL_PATH` | `/models/ggml-large-v3-turbo-q8_0.bin` (GPU), `ggml-base.en.bin` (CPU compose) | the model file (in-process engine; removed by D19, Task 21) |
| `WAA_WHISPER_DEVICE` | `auto` | `auto` tries CUDA then CPU, `cpu`, `cuda` (in-process engine; removed by D19, Task 21) |
| `WAA_WHISPER_URL`, `WAA_WHISPER_ALLOWED_HOSTS`, `WAA_WHISPER_MODEL` | planned (D19, Task 20) | the whisper.cpp server on the GPU server, for example `http://pdl-rune-02.panoramicdata.com:8080`; the allow-list follows the Ollama rule |
| `WAA_RETRANSCRIBE` | `false` | `true`: transcribe again every segment made by a different model (name or file size); see Changing the model |
| `WAA_OLLAMA_URL`, `WAA_OLLAMA_MODEL` | `http://pdl-rune-02.panoramicdata.com:11434`, `qwen3.6:latest` | the owner's Ollama server (never a cloud model). For production use `http://pdl-rune-01.panoramicdata.com:11434` and `WAA_OLLAMA_MODEL=qwen3.6:27b`, which is what that server has |
| `WAA_OLLAMA_ALLOWED_HOSTS` | `pdl-rune-02.panoramicdata.com,pdl-rune-01.panoramicdata.com` | the only named hosts transcript text may be sent to (a name that is not listed is refused; loopback and private IP addresses are accepted) |
| `WAA_OLLAMA_KEEP_ALIVE` | `5m` in the compose file (`0` in code) | how long Ollama keeps its model after a request. A shared server should keep it for a few minutes; use `0` only when Ollama shares this machine's GPU |
| `WAA_ENRICHMENT` | `on` | `off` stops the LLM step |
| `WAA_LAG_ALARM_HOURS` | 6 | lag above this is `catching-up` or `lagging` |
| `WAA_ANALYSIS_SINCE` | none | ignore segments that ended before this UTC time |

## Health and what each status means

`http://127.0.0.1:8095/healthz` (loopback only). A status other than 200 makes `docker ps` show `unhealthy`.

| Status | Code | Meaning |
|---|---|---|
| `ok` | 200 | working, lag within the alarm |
| `catching-up` | 200 | lag is above the alarm only because of the backlog: a job finished within the stall window (3 hours, or the Ollama timeout plus 5 minutes if longer) and nothing is paused. Expected for the whole first catch-up, because the queue is newest first |
| `paused` | 200 | low disk or whisper not ready; `paused` says why. Not an error until the lag has no progress behind it |
| `lagging` | 503 | lag above the alarm and no job has finished within the stall window: no progress is being made |
| `stalled` | 503 | no sign of life (15 minutes between jobs, or one job running past the stall window) |
| `failing` | 503 | whole passes keep failing (the folder cannot be listed, the disk check throws); `lastPassError` says why |

`backlogAudioHours` is the audio still to transcribe; `catchUpEtaHours` is that times the average realtime factor
(transcription seconds per audio second, so 0.05 is 20 times faster than real time), `null` until a job has finished.
During the first catch-up watch `backlogAudioHours` fall; `lagSeconds` stays high until the oldest segments are done.
`lastErrorBySegmentClass` and `lastPassError` carry error strings.

## First run checklist

1. Download the models into `models/` (gitignored), with the pinned commit and hashes:

       $hf = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1'
       curl.exe -L --fail -o models/ggml-large-v3-turbo-q8_0.bin "$hf/ggml-large-v3-turbo-q8_0.bin"
       curl.exe -L --fail -o models/ggml-base.en.bin "$hf/ggml-base.en.bin"

   sha256 must be `317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1` (874,188,075 bytes) and
   `a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002` (147,964,211 bytes). A different hash means a different file.
2. Create `data/analysis`, and make sure the web app has saved the schedule once.
3. GPU check (in-process engine only; after D19 the GPU check is on the rune server, plan 3 Task 23): `nvidia-smi` on the host must show at least 3 GB free with Ollama's model loaded.
4. Start (always name the service; never touch the recorders):

       docker compose -f docker/docker-compose.analysis.yml -f docker/docker-compose.gpu.yml up -d --build analysis

   Without a GPU leave out the second file: the CPU image uses `base.en` and is far slower.
5. `docker exec waa-analysis nvidia-smi` names the GPU; `curl http://127.0.0.1:8095/healthz` shows `"whisperReady":true` within a few minutes.
6. See progress with `/healthz` (`backlogSegments`, `backlogAudioHours`, `catchUpEtaHours`, `realtimeFactor`, `currentJob`) and
   `docker logs waa-analysis`.
7. A 503 means `lagging`, `stalled` or `failing` (table above). `docker logs waa-analysis` and the `last...Error` fields say why.

**Stop and roll back.** `docker compose -f docker/docker-compose.analysis.yml stop analysis` (or `down`) is graceful: the job in
hand is left waiting without using an attempt. Removing analysis removes nothing else: the recorders and the web app do not depend
on it, and `data/analysis` can be deleted and rebuilt from the recordings. Do not `docker kill` it.

## Maintenance commands

Both refuse to run while the service is live: stop the service first.

    docker compose -f docker/docker-compose.analysis.yml stop analysis
    docker compose -f docker/docker-compose.analysis.yml -f docker/docker-compose.gpu.yml run --rm analysis --unpark
    docker compose -f docker/docker-compose.analysis.yml -f docker/docker-compose.gpu.yml run --rm analysis --rebuild
    docker compose -f docker/docker-compose.analysis.yml -f docker/docker-compose.gpu.yml up -d analysis

- `--unpark`: every failed or parked segment goes back in the queue with its attempts reset.
- `--rebuild`: events, day files and shows are rebuilt from the stored transcripts and features (no whisper, no audio decode). The
  timeline is worked out again from the current sidecar, so a position-model fix re-places every event.
- `--rebuild --retranscribe`: also marks every finished segment made by a different model for the service to transcribe again.
- `--benchmark <segment> 600` measures the realtime factor. Stop the service first and use `run --rm` (the entrypoint keeps `nice`, and
  `run` publishes no ports); an `exec` into the live container would run un-niced and double the GPU memory.

## Changing the model

The job version holds the Whisper.net package major version and the transcript options (language, chunk length, glossary, prompt).
It does not hold the model. Switching the model, for example to `base.en` on the CPU, leaves every finished transcript as it is: the
model name, file size and device are recorded in each transcript and in `state.json` for information. Transcribing again happens only
when `WAA_RETRANSCRIBE=true` (each segment whose recorded model differs is run once and then records the new model, so turn the setting
off again afterwards), when `--rebuild --retranscribe` marks segments, or when the options or the package major version change.

## Sharing the GPU with Ollama (only if you run Ollama on this machine)

After D19 whisper and Ollama share the rune server's GPU instead; the service still never transcribes and enriches at the
same time (one work lane).

By default Ollama now runs on the owner's servers (`pdl-rune-02`, or `pdl-rune-01` for production), so none of this applies unless `WAA_OLLAMA_URL` points at an Ollama on this machine.


Whisper and Ollama share the 12 GB card. Whisper builds a processor (with its own GPU state) for every chunk, and a model held by
Ollama can make that allocation fail. The service therefore: sends `keep_alive` `0` to Ollama so its model is unloaded straight after
each enrichment; drops whisper's model before an enrichment batch so Ollama can have the GPU (the next pass loads it again); and treats
any failure to set up a chunk as transient (the job waits and retries without using an attempt, so a busy GPU never parks a segment).

## The lock

The service holds `.analysis-service.lock` in `data/analysis` (process id, host name, process start time), refreshed every 30 seconds.
A lock is ignored when it is older than 5 minutes, when it names this process id on this host (a restarted container is PID 1 again),
or when it names a pid on this host that is gone or started at a different time. A fresh lock from another host (for example the
maintenance container) always counts as running. After a hard kill of a different container it can take up to 5 minutes before a
replacement starts; the service exits with code 2 and `restart: unless-stopped` retries.

## The web app and the analysis API

The web app shows analysis only when its service has `WAA_ANALYSIS` and a read-only mount of `data/analysis`. That is
`docker/docker-compose.web-analysis.yml`, merged with the recorder project's file:

    docker compose -f docker/docker-compose.yml -f docker/docker-compose.web-analysis.yml up -d web

**Warning.** Running `docker compose -f docker/docker-compose.yml up -d --build web` without `docker/docker-compose.web-analysis.yml`
(the override) silently drops `WAA_ANALYSIS`, and the analysis API then answers 404. Always include the override.

## Limits

- Counts are detections, not proven events; whisper leaves out some fillers, so the filler rate is an estimate.
- A swear inside Revma's roughly 5 second reconnect burst can be detected twice in segments with gaps.
- The 98 percent rule leaves up to about 72 seconds per hour of recorder A unanalysed even when recorder B has it.
- The web analysis API is unauthenticated until plan 4a. It exposes exactly: events (7 days at most), transcript (6 hours, 20,000 lines
  at most) and shows (the analysis and the enrichment, with LLM-derived names). The analysis `/healthz` on `127.0.0.1:8095` shows last-error
  strings. The spec 3.8 requirement of no separate API surface needs plan 4a coverage.
- The first catch-up runs newest first, so lag stays above the alarm for days; `catching-up` is the status for it.
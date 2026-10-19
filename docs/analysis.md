# Analysis (plan 3)

The analysis service turns the archive into transcripts, detected events and per-show facts. It is a separate container
(`waa-analysis`) with its own image and its own compose project, below the recorders' priority. It is CPU only (decision D19,
2026-10-17): the production Kubernetes cluster has no GPU, so speech to text is an HTTP call to a whisper.cpp server container on
the owner's GPU servers (`pdl-rune-02` for test, `pdl-rune-01` for production, port 8080), the same servers that run Ollama.
It never writes to the recordings: it reads closed segments read-only, copies each one into a 2 GiB tmpfs scratch, decodes the
copy, and writes only under `data/analysis`. All times are UTC. Every count is a detection from speech to text and audio levels,
not a proven event.

**Where the data goes.** Speech audio (16 kHz mono WAV chunks of about 5 minutes) is sent to the whisper server and transcript text
to Ollama, both only to the hosts you configure: loopback, private addresses, or names listed in `WAA_WHISPER_ALLOWED_HOSTS` and
`WAA_OLLAMA_ALLOWED_HOSTS`. The archive MP3s are never sent anywhere. Overview and diagrams: `DESIGN.md`.

## The whisper.cpp server client (decision D19)

`WhisperServerTranscriber` sends each 300 s chunk to `POST {WAA_WHISPER_URL}/inference` as multipart (`file` = the WAV
unchanged, `response_format=verbose_json`, `language`, `prompt`, `carry_initial_prompt=true`, `temperature=0`,
`temperature_inc=0.2`, `token_timestamps=true`, `no_language_probabilities=true`) and reads `segments[]` with `words[]`
(whisper tokens: `WhisperResponse.MergeTokens` merges them) and the per-segment `avg_logprob` and `no_speech_prob` that
`TranscriptFilter` uses. Failure handling:

| What happens | Result |
|---|---|
| connection refused, DNS failure, reset, timeout, 502, 503 (also while the model loads), 504 | retried with backoff, then the job waits (no attempt used); `/healthz` goes `paused`, then `catching-up` |
| 500 whose body mentions memory or CUDA | the same (transient) |
| 500 `{"error":"failed to process audio"}` (the server's `whisper_full` failed, usually a busy shared GPU) | the same (transient), until the SAME chunk has done it more than `WAA_WHISPER_MAX_PROCESS_FAILURES` times (default 10, counted per chunk in memory, cleared by a success or a restart); then the chunk fails (an attempt is used) |
| any other 500 (after the retries), 4xx, a redirect, a body that is not the expected JSON, an answer over 32 MB | the chunk fails with a clear message (an attempt is used) |
| the service is stopping | the request is aborted at once and no attempt is used |

The client never follows a redirect (a 307 would resend the audio to another host), uses no proxy, and logs no audio,
transcript text or token. `GET /health` is the readiness check (10 s). `/healthz` shows `whisperReady` and `whisperUrl`
(scheme, host and port only). Transcripts made by the retired in-process engine (`whisper.net 1`) stay current: the
the engine family (`whisper.cpp`; a stored `whisper.net 1` counts as equal) and the transcript options are the job version, while the model, the server and the device are not, so the switch re-transcribes nothing. The segment `avg_logprob` is the mean of ln(probability) over the segment's words, recomputed by the client: the server divides by all tokens including special ones, which makes short segments look about 40 percent more confident and would stop the hallucination filter's rule from firing.

### Live contract check (manual, never in CI)

The server is deployed by the owner (plan 3 Task 23). Once it runs, check that its answer has what the client needs:

    docker run --rm -e WAA_RECORDERS=a=/data/a -e WAA_WHISPER_URL=http://pdl-rune-02.panoramicdata.com:8080 \
      -e WAA_WHISPER_ALLOWED_HOSTS=pdl-rune-02.panoramicdata.com webaudioarchive-analysis:local --check-whisper [chunk.wav]

or `dotnet run --project src/WebAudioArchive.Analysis.Service -- --check-whisper [chunk.wav]` with the same variables set (and
`WAA_RECORDERS` pointing at an existing folder). It prints PASS or FAIL lines and exits 0, 1 or 2. Without a file it sends 20 s of
generated tones (the server may answer no segments, which passes with a note); with a real 16 kHz mono 16-bit WAV (never
commit one) it requires segments with scores and words with `start`, `end` and `probability`. The check code itself is
tested against a stub server in CI (`WhisperContractCheckTests`); the real server is never contacted by the tests.

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
| `WAA_WHISPER_URL` | `http://pdl-rune-02.panoramicdata.com:8080` in compose; required (the service refuses to start without it) | the whisper.cpp server on the GPU server, for example `http://pdl-rune-02.panoramicdata.com:8080`. The audio chunks go there. `http` or `https`; the host must be loopback, `localhost`, a private address or named in `WAA_WHISPER_ALLOWED_HOSTS`, otherwise the service refuses to start (the Ollama rule, the same code); a URL with user info is refused (use the token) |
| `WAA_WHISPER_ALLOWED_HOSTS` | `pdl-rune-02.panoramicdata.com,pdl-rune-01.panoramicdata.com` in compose, none in code | comma separated names the audio may be sent to; independent of `WAA_OLLAMA_ALLOWED_HOSTS` |
| `WAA_WHISPER_MODEL` | `ggml-large-v3-turbo-q8_0` | the model the server was started with. whisper.cpp's server does not report it, so this is recorded in every transcript and job for information only |
| `WAA_WHISPER_LANGUAGE` | the language in `analysis.json` (`en`) | override only |
| `WAA_WHISPER_TIMEOUT_MINUTES` | 10 (10 to 240) | per request. A 300 s chunk takes 11 to 16 s on an RTX 3060, a whole hour 127 s, so 10 minutes leaves room for a slower or shared GPU |
| `WAA_WHISPER_CONCURRENCY` | 1 (1 to 16) | requests in flight; the server handles one at a time, so more helps only with several servers behind a load balancer |
| `WAA_WHISPER_RETRIES` | 3 (0 to 10) | retries inside one chunk (2, 4, 8 s, then 16 s, at most 30 s, with jitter) for a connection error, 502, 503, 504, a timeout, a memory or CUDA 500 or a "failed to process audio" 500 before the job waits (no attempt used); any other 500 is retried too but then fails the chunk (an attempt is used) |
| `WAA_WHISPER_MAX_PROCESS_FAILURES` | 10 (1 to 1000) | how often the same chunk may end in the server's "failed to process audio" answer before that counts as a failed attempt, so a chunk the server can never process is eventually parked while a busy GPU never parks a segment |
| `WAA_WHISPER_TOKEN` | none | optional bearer token sent as `Authorization: Bearer`; never logged or shown in an error (issue #112) |

The old in-process settings `WAA_WHISPER_MODEL_PATH`, `WAA_WHISPER_DEVICE`, `WAA_WHISPER_THREADS` and `WAA_WHISPER_DTW` are gone: setting one
stops the service at start-up with "... is no longer used: transcription runs on the Whisper service; set WAA_WHISPER_URL". Whisper.net,
the CUDA image target, `docker-compose.gpu.yml` and the `models/` mount were removed.

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

1. The whisper.cpp server must be running on the GPU server (owner action, plan 3 Task 23, issue #111): deploy it on pdl-rune-02 first,
   then pdl-rune-01. `curl http://pdl-rune-02.panoramicdata.com:8080/health` answers `{"status":"ok"}` (503 while the model loads). Until it
   answers, the service starts anyway: `/healthz` shows `"whisperReady":false` and the status `paused`, and it catches up by itself when the
   server appears. Check the server's answer once with the live contract check above; for real speech use the `curl` command in Task 23 Step 4.
   The model (`ggml-large-v3-turbo-q8_0`, 874,188,075 bytes, sha256 `317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1`)
   lives on that server, not here: there is no `models/` mount and nothing downloads a model at run time.
2. Create `data/analysis`, and make sure the web app has saved the schedule once.
3. Confirm this machine (or the Kubernetes node) can reach the rune server on 8080 and 11434, and that Ollama and the whisper server fit the
   GPU together (`nvidia-smi` on the rune server, Task 23 Step 1). To try the service without those servers, run the development-only local
   container, `docker/docker-compose.whisper-dev.yml` (it needs an NVIDIA GPU, the model in `models/` and the container toolkit; loopback
   only), and set `WAA_WHISPER_URL=http://host.docker.internal:8178`.
4. Start (always name the service; never touch the recorders):

       docker compose -f docker/docker-compose.analysis.yml up -d --build analysis

5. `curl http://127.0.0.1:8095/healthz` shows `"whisperReady":true` and `"whisperUrl"` once the server answers (it is checked every pass).
6. See progress with `/healthz` (`backlogSegments`, `backlogAudioHours`, `catchUpEtaHours`, `realtimeFactor`, `currentJob`) and
   `docker logs waa-analysis`.
7. A 503 means `lagging`, `stalled` or `failing` (table above). `docker logs waa-analysis` and the `last...Error` fields say why.

**Stop and roll back.** `docker compose -f docker/docker-compose.analysis.yml stop analysis` (or `down`) is graceful: the job in
hand is left waiting without using an attempt. Removing analysis removes nothing else: the recorders and the web app do not depend
on it, and `data/analysis` can be deleted and rebuilt from the recordings. Do not `docker kill` it.

## Maintenance commands

Both refuse to run while the service is live: stop the service first.

    docker compose -f docker/docker-compose.analysis.yml stop analysis
    docker compose -f docker/docker-compose.analysis.yml run --rm analysis --unpark
    docker compose -f docker/docker-compose.analysis.yml run --rm analysis --rebuild
    docker compose -f docker/docker-compose.analysis.yml up -d analysis

- `--unpark`: every failed or parked segment goes back in the queue with its attempts reset.
- `--rebuild`: events, day files and shows are rebuilt from the stored transcripts and features (no whisper, no audio decode). The
  timeline is worked out again from the current sidecar, so a position-model fix re-places every event.
- `--rebuild --retranscribe`: also marks every finished segment made by a different model for the service to transcribe again.
- `--benchmark <segment> 600` measures the realtime factor. Stop the service first and use `run --rm` (the entrypoint keeps `nice`, and
  `run` publishes no ports); an `exec` into the live container would run un-niced.
- `--check-whisper [chunk.wav]` is the live contract check of the whisper server (see above); it sends audio, so it needs `WAA_WHISPER_URL`.

## Changing the model

The job version holds the engine family (`whisper.cpp`) and the transcript options (language, chunk length, glossary, prompt).
It does not hold the model or the server. Switching the model on the server (and `WAA_WHISPER_MODEL`, which only labels it), or the server
itself, leaves every finished transcript as it is: the
model name, file size and device are recorded in each transcript and in `state.json` for information. Transcribing again happens only
when `WAA_RETRANSCRIBE=true` (each segment whose recorded model differs is run once and then records the new model, so turn the setting
off again afterwards), when `--rebuild --retranscribe` marks segments, or when the options change.

## Sharing the GPU with Ollama

Whisper and Ollama share each rune server's GPU, so the service never transcribes and enriches at the same time (one work lane:
transcription first, enrichment only when no segment job is ready). `WAA_OLLAMA_KEEP_ALIVE` (`5m` in compose) keeps Ollama's model loaded
between the shows of one enrichment batch; the whisper server keeps its own model loaded (about 1.2 GB of VRAM). If the server cannot
get GPU memory it answers with a memory error, which the client treats as transient: the job waits and retries without using an
attempt, so a busy GPU never parks a segment. Owner check before production: both fit next to each other (`nvidia-smi` with the Ollama
model loaded, plan 3 Task 23 Step 1).

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
- Changing `WAA_WHISPER_LANGUAGE` does not invalidate existing transcripts: the job version holds the language of `analysis.json`, not this override. Re-transcribe with `WAA_RETRANSCRIBE` or by changing the transcript options.
- A proxy in front of the server that answers 408, 429 or an HTML 200 makes the chunk fail at once and uses an attempt (only connection errors, 502, 503, 504, timeouts and the GPU errors above wait).
- A request that always times out is retried for ever: a timeout is transient and uses no attempt (plan-mandated). Each try can hold the server's GPU for the whole `WAA_WHISPER_TIMEOUT_MINUTES`, so watch `/healthz` and the server if one chunk keeps timing out.
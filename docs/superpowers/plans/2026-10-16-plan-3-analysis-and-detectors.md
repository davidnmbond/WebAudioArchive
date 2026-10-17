# WebAudioArchive Plan 3: Analysis and Detectors Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A separate, low-priority analysis container that finds every closed master segment, decodes a scratch copy in managed code, transcribes it locally on the GPU with whisper.cpp (in process, through the Whisper.net NuGet packages), runs the swearing, clipping, filler and dead-air detectors, writes a versioned, rebuildable `events.json` timeline and per-segment transcripts in UTC, maps them onto the schedule's shows, and enriches each finished show with hosts, guests, topics, songs and artists from the local Ollama, all without ever touching a recorder.

**Architecture:** Two new projects: `WebAudioArchive.Analysis` (a class library: decode, features, detectors, transcriber and LLM clients, work planning, the segment pipeline) and `WebAudioArchive.Analysis.Service` (the host: one background worker, `/healthz` with lag metrics, and maintenance commands). The file contracts (transcript, events, show documents, the segment timeline that maps audio offsets to UTC) live in `WebAudioArchive.Core` so the web app reads them without depending on the analysis code. The analysis container mounts the recorders' folders read-only, writes only to `data/analysis`, decodes only into a size-bounded tmpfs scratch, runs at `nice 10` with low CPU shares and a memory cap, transcribes in its own process with Whisper.net (CUDA runtime in the opt-in GPU image, CPU otherwise; decision D18), and talks to Ollama on the host over HTTP. There is no ffmpeg and no separate whisper container.

**Tech Stack:** .NET 10, ASP.NET Core minimal host (for `/healthz`), System.Text.Json, NuGet (all MIT unless noted, pinned, decision D18): `NLayer` 3.0.0 (managed MP3 frame decoder), `NAudio.Core` 3.1.0 (WDL resampler), `Whisper.net` 1.9.1 with `Whisper.net.Runtime` 1.9.1 (CPU) and `Whisper.net.Runtime.Cuda.Linux` 1.9.1 (CUDA 13; whisper.cpp commit `f24588a2`), test project only `GroovyMp3` 0.1.3 (LGPL-3.0, managed LAME port, makes synthetic MP3s); CUDA 13.0 `cuda-cudart-13-0` and `libcublas-13-0` from NVIDIA's Ubuntu 24.04 apt repository in the GPU image; model `ggml-large-v3-turbo-q8_0`; Ollama 0.40.4 on the host (model `qwen3.6:latest`) over plain `HttpClient`; xUnit 2, Docker Compose, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md` (sections 3.5, 3.6, 3.7, 3.8, 3.9, 3.11, 3.13, 5, 6). This is plan 3 of 5. It is written before plan 2 (schedule service, cutter) and does not depend on it: it maps analysis onto shows using the schedule the web app already saves (`/cache/schedule/<station>.json`, spec 3.2 MVP) and works per master segment, not per cut show file. When plan 2 lands, `ScheduleProvider` switches to `/data/<station>/schedule.json` (one constructor argument).

## Global Constraints

- .NET 10, `net10.0`, `Nullable` enabled, `TreatWarningsAsErrors` true. Packages pinned to exact versions; this plan adds only those listed in the Tech Stack (D18).
- **All times UTC everywhere**: file names, JSON, logs, `/healthz`, API responses. The station zone (`Europe/London`) is used for exactly two things: turning the schedule's local slots into UTC (existing code) and deciding whether an instant is before the 21:00 watershed. Nothing local is stored or shown.
- **The archive is stored exactly as broadcast.** Analysis never opens a master `.mp3` or sidecar for writing, never decodes the archive in place: it copies a closed segment into scratch and decodes the copy. `.mp3.part` files are never read.
- **The recorders are never affected.** Analysis is its own image and container, mounts `data/a` and `data/b` read-only, writes only to `data/analysis` and its tmpfs scratch, runs below the recorders' priority (nice 10, `cpu_shares: 256`, CPU and memory caps, high `oom_score_adj`), and stops work first when free disk falls below its threshold. No compose change may alter the `recorder-a` or `recorder-b` service definitions.
- **Public repo:** no broadcast audio, recordings, transcripts of real broadcasts, model files or credentials in git. Test audio is synthetic, generated at test time (signals in code; MP3 made by GroovyMp3 in the test process into a temp folder). The `models/` and `data/` folders are gitignored.
- No transcript or audio leaves the machine: Ollama must be local or on a private network (the settings refuse `:cloud` models and public hosts).
- No em dashes in any text, code comment, commit message or doc.
- No skipped tests. A test that cannot run (for example a test helper whose package internals changed) must fail with a message saying what is missing and how to fix it. No test needs a native tool or a whisper model: decoding and MP3 encoding are managed code, and transcription is faked in tests.
- Commit messages end with the attribution trailer the harness supplies. Commit after each task. `git add` named paths only; never `git add -A` (unrelated tracked files such as `CLAUDE.md` and `AGENTS.md` must not be touched).
- Detector figures are estimates. Every event carries `severity`, `confidence`, `source` (`audio`, `transcript` or `llm`), the detector name and version, and enough to play the audio around it; reports say "detected", never "proven".

Working directory for every command: `C:\Users\david\source\repos\davidnmbond\WebAudioArchive` (PowerShell). Another agent may be working on `src/WebAudioArchive.Web` (Shows and calendar pages); run `git pull --rebase` before Tasks 13 and 17, which touch Web files, and carry over any changes made there since commit `19a06f0`.

## Decisions made

Each decision is the owner's to overturn; the alternatives were considered and rejected for the reason given.

| # | Decision | Evidence and reasons | Rejected alternatives |
|---|---|---|---|
| D1 | *Superseded by D18: whisper.cpp now runs in the analysis process through Whisper.net 1.9.1; the reasons below for a separate HTTP server were weighed again there.* **Transcription engine (original): whisper.cpp `whisper-server` v1.9.5, built from source in `docker/Dockerfile.whisper` (CUDA 13.0.1, compute capability 86 for the RTX 3060), called over HTTP.** | MIT licence (checked 2026-10-16, `LICENSE` at tag v1.9.5, commit `d2a764c648779feb36d44363f212a29b00aad1af`). The server's `verbose_json` response gives segments with `no_speech_prob`, `avg_logprob` and per-token `start`, `end`, `t_dtw` and `probability` (read in `examples/server/server.cpp` at v1.9.5), has `/health`, takes `prompt`, `language` and `temperature` per request, and serialises requests with a mutex. HTTP keeps CUDA, model memory and any native crash out of the .NET process; the analysis image stays a plain aspnet image with ffmpeg. The spec already names whisper.cpp. | (b) **Whisper.net 1.9.1** (MIT; `Whisper.net.Runtime.Cuda` needs CUDA toolkit 13.0.1 runtime libraries in the image, `Whisper.net.Runtime.Cuda12` for 12.4.1): in-process native code, a CUDA runtime in the analysis image (several GB), a native fault kills the worker, and its word or token timestamp API is not documented on the package page. (c) **faster-whisper 1.2.1** (MIT, last release 2025-10-31; CUDA 12 and cuDNN 9; batched mode is the fastest measured: 13 min of audio in 16 to 17 s on an RTX 3070 Ti): needs a Python service we would write and maintain (no official server), a second language in the repo, and its speed advantage is not needed (see D3). The official `ghcr.io/ggml-org/whisper.cpp:main-cuda-<sha>` images were rejected because no image exists for the v1.9.5 release commit (checked on GHCR: only `main-cuda-<sha>` tags of main commits), so pinning one would pin an unreleased commit. |
| D2 | *Model kept; DTW revised by D18 (off by default, measured).* **Model: `ggml-large-v3-turbo-q8_0.bin`** (874,188,075 bytes, sha256 `317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1`, Hugging Face `ggerganov/whisper.cpp` commit `5359861c739e955e79d9a303bcbc70fb988958b1`), language forced to `en`, DTW token timestamps (`--dtw large.v3.turbo`) with flash attention off (whisper.cpp disables DTW when flash attention is on, `src/whisper.cpp` line 3817 at v1.9.5). | large-v3-turbo has the large-v3 encoder with 4 decoder layers instead of 32: near large-v3 accuracy at several times the speed. q8_0 halves VRAM against f16 with negligible accuracy loss, which matters because the 3060's 12 GB is shared with the Windows desktop (5.4 GB in use when measured) and Ollama. DTW gives better word times for swear and filler links. | `medium.en`: slower than turbo (24 decoder layers) and less accurate. `large-v3` f16: about 3 to 4 times slower to decode, about 4 GB VRAM. `small.en` / `base.en`: kept only as the CPU fallback (`ggml-base.en.bin`, 147,964,211 bytes, sha256 `a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002`). |
| D3 | *Replaced by measurements in D18 (22x to 46x on speech, 7.5x on a music-heavy hour under full host CPU load).* **Expected speed: 15x to 30x realtime on the RTX 3060** (realtime factor 0.03 to 0.07), to be measured in Task 19 and recorded in `docs/analysis-validation.md`. | Published figure: whisper.cpp large-v2 fp16 with flash attention transcribes 13 min in 65 s (12x) on an RTX 3070 Ti (faster-whisper README benchmark). A 3060 has about 70% of a 3070 Ti's throughput; turbo decodes far faster than large-v2; DTW without flash attention costs some of that back. 24 hours of audio a day at 15x is 1.6 GPU hours a day, so the queue keeps up with a wide margin and can clear a day of backlog in about 2 hours. If Task 19 measures slower than 5x realtime, switch to flash attention on without DTW (Task 16 shows the flag) and record it. | n/a |
| D4 | **GPU passthrough works on this host** (checked 2026-10-16: `docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi` shows the RTX 3060, driver 610.88, CUDA 13.3, inside Docker Desktop 29.8.2). GPU use is opt-in through `docker/docker-compose.gpu.yml` (spec 5), which since D18 builds the `cuda` target of the analysis image and gives the analysis container the GPU; without it the analysis image is the `cpu` target and transcribes with `base.en`. Re-checked 2026-10-16 with the Whisper.net CUDA runtime inside a throwaway aspnet-based image (D18). | The driver's CUDA 13.3 supports CUDA 13.0.1 images. | n/a |
| D5 | **Enricher model: `qwen3.6:latest`** (Ollama digest `07d35212591fc27746f0a317c975a6d68754fb38e9053d82e25f06057af28522`, 36B mixture of experts, 8 of 256 experts active, Q4_K_M, 22.3 GB, Apache 2.0), with `think: false`, temperature 0.1, fixed seed, JSON schema output, `num_ctx` 32768. | Measured on this host 2026-10-16 with the same synthetic extraction prompt and JSON schema: qwen3.6:latest 154 tokens/s prompt, 15.0 tokens/s output, every quote verbatim; gemma4:26b (MoE) 11 tokens/s prompt, 7.8 tokens/s output, short but correct quotes; llama3.1:8b 35 tokens/s prompt, 47.9 tokens/s output, but gave Raff a quote that does not contain his name (the validator would drop it). A 1 hour show transcript (about 10,000 to 15,000 tokens) is 2 to 4 minutes on qwen3.6:latest; about 13 shows a day is under an hour. MoE means it runs well partly on CPU, so it coexists with whisper on the 12 GB card. The analysis records the model digest it used in every result. | Dense 27B and 31B models (`qwen3.8:27b`, `qwen3.6:27b`, `gemma4:31b`): 16 to 18.5 GB do not fit the free VRAM and run slowly on CPU. `qwen3.5:2b`, `qwen2.5-coder:*`: too small or code models. `nomic-embed-text`: embeddings only (useful later for search). **`kimi-k2.6:cloud` is refused by configuration**: it runs remotely, so transcripts would leave the machine. |
| D6 | **Unit of work: one closed recorder segment** (an hourly `.mp3` plus its sidecar). Transcripts and events are stored per segment; shows are views over them (spec 3.6: "one transcript for the station; each show's transcript is a slice"). | Segments are immutable once closed, so a job is idempotent and keyed by the sidecar's bytes, frames, end and `endedBy`. Show times can change; re-mapping a show is cheap and never re-transcribes. | Per show cut files: plan 2 does not exist yet, and a schedule change would force re-transcription. Live decode in the recorder's fan-out (spec 3.6 "decode the live stream"): would put analysis in the recorder process, which spec 3.13 forbids. |
| D7 | **A/B: recorder A is analysed; B only fills A's holes.** A B segment is queued only when A's segments cover less than 98% of its span (spans minus gaps). Timelines and transcript slices drop B events and lines at instants that an A segment covers. `WAA_ANALYSIS_PREFERENCE` (default `a,b`) sets the order. | Analysing both doubles GPU work for near-identical results; the two copies are not byte-identical (3.13) so they cannot be merged sample by sample. | Analyse both and reconcile: double the cost and no reconciliation rule in the spec (3.10). |
| D8 | **Newest segment first.** The queue is ordered by segment start, newest first. | After an outage the latest shows (the ones people ask about) are done first, and the backlog drains behind them. Lag is reported as the age of the oldest unanalysed closed segment, so a backlog is visible. | Oldest first: a long backlog would delay today's shows by days. |
| D9 | **Scratch is a 2 GiB tmpfs** (`/scratch`), per job: a copy of the segment (about 57.6 MB), the 16 kHz mono WAV for whisper (about 115 MB an hour); the full-rate float PCM for the audio detectors is streamed from the managed decoder into the feature extractor and never written (D18). Budget enforced in code (`WAA_SCRATCH_MAX_MB`, default 2048); scratch is swept at start-up and after every job, success or failure. | tmpfs vanishes on restart (no stale files on disk), costs no disk wear and never competes with the recorders for disk. 96 GB RAM makes 2 GiB cheap. | Scratch on the data disk: competes with the recorders for space and IO, the very thing to avoid. |
| D10 | **Audio detectors are rebuildable without decoding.** Each segment keeps `features.json`: 50 ms window levels (dBFS) and the clipped-sample runs at fixed levels 0.98, 0.99 and 0.999 of full scale (runs of 2 or more samples, at most 50,000 per level). Thresholds can be retuned and events rebuilt (`--rebuild`) from it; a clipping threshold not in that list needs a re-decode (the config loader says so). | Retuning is expected (spec 3.6 says the thresholds are provisional). | Storing PCM: 600 MB an hour. Storing nothing: every retune re-decodes the archive. |
| D11 | **Watershed: before-watershed means UK local time from 05:30 to 21:00** (both configurable). | Spec 3.6 gives 21:00 local; Ofcom's watershed runs 21:00 to 05:30. | 21:00 only: would mark 01:00 swearing as before the watershed. |
| D12 | **Swear list content:** general profanity in three tiers (`mild`, `strong`, `severe`), whole words plus listed suffixes, masked forms (`f***`) at reduced confidence, an allow list for near misses. Discriminatory slurs are not shipped in this public repo; the list is a mounted config file the owner can extend locally. Lyrics in songs are transcribed too, so a hit may be a lyric; the event says so in its evidence (`context: unknown`) and links to the audio. | Ofcom treats aired lyrics like speech, so they should be reported, not filtered. | Filtering music: no reliable music detector yet (3.9). |
| D13 | **Hallucination handling:** segments are kept but marked `suppressed` (with the reason) when whisper's own no-speech rule fires (`no_speech_prob > 0.6` and `avg_logprob < -1.0`), when the text is a known hallucination ("thank you for watching", "subtitles by", ...), when it is only an annotation (`[BLANK_AUDIO]`, `(music)`), or when the same text repeats 3 or more times in a row. Detectors ignore suppressed segments. | Whisper invents text on music and silence; a radio stream is mostly music. | Silero VAD in whisper.cpp: needs a second model and drops sung lyrics, which D12 wants kept. Can be added later. |
| D14 | *Superseded by D18: there is no ffmpeg; the decoder library versions (`NLayer 3.0.0 frame decoder, NAudio.Core 3.1.0 WDL sinc resampler`) are recorded in every transcript instead.* **ffmpeg comes from the Ubuntu 24.04 package (6.1.1) and is not version-pinned in apt**; the version used is recorded in every transcript. | apt pins of old point releases break the build when Ubuntu replaces them with security fixes. The aspnet 10.0 image is Ubuntu 24.04 (checked). | A static ffmpeg download: another binary to verify and update by hand. |
| D15 | **Show mapping uses the web app's saved schedule** (`/cache/schedule/<station>.json`, mounted read-only from the `web-cache` volume). The schedule types (`WeeklySchedule`, `ShowOccurrence`, `ScheduleExpander`) move from Web to Core so both use one implementation. | No new schedule fetcher; one expansion rule for DST. | Calling the web app's HTTP API: analysis would stop mapping shows whenever the web app is down. A second schedule parser: duplicate code. |
| D16 | **Health alarm:** `/healthz` returns 503 when the analysis lag exceeds `WAA_LAG_ALARM_HOURS` (default 6) or the worker loop has not completed a pass for 15 minutes, so `docker ps` shows `unhealthy`. Docker does not restart unhealthy containers; it is an alarm, not a restart. | Spec 5 asks for analysis lag in `/healthz`; spec 1 says no pushed alerts. | Restart on unhealthy: a restart cannot fix a lag. |
| D17 | **One work lane:** transcription first, enrichment only when no segment job is ready. | Whisper and Ollama share one GPU; running them at once slows both and risks running out of VRAM. | Two concurrent lanes. |
| D18 | **The analysis stack comes from NuGet (owner's question, spike 2026-10-16; evidence below).** (a) **Decode:** our own MPEG-1 Layer III frame splitter feeding `NLayer` 3.0.0's `MpegFrameDecoder` frame by frame, with a fresh decoder primed on the previous frames when NLayer refuses a frame; resample to 16 kHz mono with `NAudio.Core` 3.1.0's `WdlResampler` in sinc mode; one pass gives the full-rate features and the speech WAV. No ffmpeg anywhere (image, CI, development host). (b) **Transcription:** `Whisper.net` 1.9.1 in the analysis process behind the same `ITranscriber`, `Whisper.net.Runtime` (CPU) plus `Whisper.net.Runtime.Cuda.Linux` (CUDA 13); one Dockerfile with a `cpu` target (aspnet image plus `libgomp1`) and a `cuda` target (adds NVIDIA's `cuda-cudart-13-0` and `libcublas-13-0`); `WAA_WHISPER_DEVICE` `auto` tries CUDA then falls back to the CPU. Chunks of 300 s as before, no carried context, prompt carried into every 30 s window, flash attention on, **DTW off by default** (`WAA_WHISPER_DTW=on` only with `chunkSeconds` 30). The separate whisper container, `Dockerfile.whisper` and its CUDA workflow are dropped. (c) **Enrichment:** keep the plain `HttpClient` Ollama client (no package). (d) **Tests:** synthetic MP3s from `GroovyMp3` 0.1.3 in the test process. | Decode: the frame-by-frame decode of a real 1 hour Burst segment gave 137,749 frames and 158,686,848 samples per channel, exactly the sidecar's frame count x 1152 and exactly ffmpeg 7.1's count; 81.5 dB SNR against ffmpeg's float decode; samples at or above 0.99: 61,373 in both; above 1.0: 45,608 in both; 79x to 93x realtime on one core (38.7 to 45.5 s an hour), 71 MB peak working set streaming; same on Windows and in a Linux container. Resampler: within 0.1 dB to 6 kHz, -67 dB at 10 kHz. Whisper.net: same whisper.cpp engine, the same per-token start, end, DTW time and probability as whisper-server's verbose_json plus the segment's no-speech probability (so `WhisperResponse.MergeTokens` and `TranscriptFilter` are unchanged); RTX 3060 large-v3-turbo q8_0 on 5 minutes of real speech: 46x realtime (300 s request, no DTW) natively on Windows, 28x in a Linux container with `--gpus all`; about 1 GB of VRAM above idle. In process removes a container, an HTTP hop, a CMake and nvcc build and the 3.8 GB CUDA devel image; a native crash restarts the container and the job resumes after its last finished chunk (Task 12). DTW: a 300 s request with DTW on stopped after 58 s (18 of 140 segments, 131 of 943 words) in every combination tried (context on or off, DTW memory 128 MB or 1 GB); with 30 s requests DTW works at 16.3x. | NLayer's `MpegFile` alone: drops a frame (26 ms) about every 4.7 minutes when its 8 KiB bit-reservoir ring wraps at a frame boundary, 17 to 18 frames (0.47 s) short per hour, so the timeline drifts. `MP3Sharp` 1.0.5 (LGPL-3.0, 2019): correct length, 112x, but 16-bit output only, so the overshoot above full scale that clipping needs is lost. NAudio's `WdlResamplingSampleProvider` default (linear plus IIR): -8.2 dB at 6 kHz, a 10 kHz tone through at -27 dB. A Whisper.net HTTP wrapper container: keeps the crash isolation but is a second service to write and run, for a fault the resume already absorbs. The plain `nvidia/cuda:13.0.1-runtime-ubuntu24.04` base: 1.57 GB compressed against 2.0 GB uncompressed for the whole aspnet-based CUDA image. `OllamaSharp` 5.5.0: works (structured output, `think: false`, streaming, 9.1 s against 9.3 s for `HttpClient`) but adds `Microsoft.Extensions.AI` 10.8 for 15 lines we already have; `Microsoft.Extensions.AI.Ollama` 9.7.0-preview is deprecated in favour of OllamaSharp. ffmpeg kept only for test MP3s: a native tool on every developer machine and CI runner for test data a managed encoder makes. |

### D18 evidence: the NuGet spike (2026-10-16 UTC)

Measured on this host (i9-11900K, 16 threads, 96 GB, RTX 3060 12 GB, driver 610.88, Docker Desktop 29.8.2) on a copy of `data/a/burst/master/2026/10-16/120000.mp3` (a closed hour: 57,573,460 bytes, 137,749 frames, 128 kbps, 44.1 kHz, joint stereo; the copy lived in a scratch folder outside the repo and was deleted afterwards). The reference decode was ffmpeg 7.1 in a throwaway container. The host CPU was at 60% to 100% from other work during most runs, so the CPU figures are lower bounds; Task 19 measures again on the real deployment.

| Question | Package (licence) | Result |
|---|---|---|
| MP3 decode, correctness | NLayer 3.0.0 (MIT) `MpegFile` | 137,731 of 137,749 frames: one frame lost at the start and one at each of 17 ring-buffer wraps (frames 10,788, 11,603, 22,391, ... about every 10,787 frames); 0.47 s short. Rejected. |
| | NLayer 3.0.0 `MpegFrameDecoder` + our frame splitter and re-priming (Task 9) | 137,749 frames, 158,686,848 samples per channel = sidecar frames x 1152 = ffmpeg's count; 18 frames recovered by re-priming, 1 written as silence (the first, whose reservoir is in the previous file); SNR 81.5 dB against ffmpeg; samples >= 0.99: 61,373 and 61,373; > 1.0: 45,608 and 45,608; peak 1.4746 in both. |
| MP3 decode, speed and memory | same | 79x to 93x realtime on one thread (38.7 to 45.5 s for the hour, including resampling and writing the WAV); 71 MB peak working set when streaming; identical figures in a Linux container. |
| | MP3Sharp 1.0.5 (LGPL-3.0) | 112x, correct length, 16-bit only. Rejected (clipping needs floats above 1.0). |
| Resampling to 16 kHz mono | NAudio.Core 3.1.0 (MIT) `WdlResampler`, sinc 64 taps | 0.0 dB at 100 Hz to 4 kHz, -0.1 dB at 6 kHz, -1.5 dB at 7 kHz, -3.9 dB at 7.5 kHz; aliases: -15.5 dB at 8.5 kHz, -67 dB at 10 kHz, -114 dB at 12 kHz, -135 dB at 15 kHz. Whisper's features stop at 8 kHz: adequate. 14 s of the hour's decode time. |
| | `WdlResamplingSampleProvider` default | -2.1 dB at 4 kHz, -8.2 dB at 6 kHz; aliases -27 dB at 10 kHz. Rejected. |
| Test MP3 encoding | GroovyMp3 0.1.3 (LGPL-3.0, 2018, test project only) | Valid 128 kbps frames that NLayer and ffmpeg decode. LAME's default input scale is 0.95 (a 0.5 sine decodes at 0.4751; ffmpeg agrees); with the scale set to 1.0 (Task 9 helper) a planted clip at 5.0 to 5.5 s is found at 5.051 to 5.550 s and planted silence at 3.0 to 7.0 s at 3.05 to 7.05 s (25 ms encoder delay, not trimmed). 330 s of audio encodes in 13 s and reproduces NLayer's wrap (2 frames recovered). |
| Whisper.net packages | Whisper.net 1.9.1 (MIT, 2026-06-01), Runtime 1.9.1, Runtime.Cuda.Linux 1.9.1, Runtime.Cuda.Windows 1.9.1 | Bundle whisper.cpp commit `f24588a272ae8e23280d9c220536437164e6ed28` (2026-05-29, between v1.8.x tags; not v1.9.5). (Also found: the original D1 pin `d2a764c6...` is not the commit GitHub lists for tag v1.9.5, `d1be6fde11ac6e0407606b4e42fe72d34add8037`, so the original Dockerfile's `rev-parse` check would have stopped the build.) CPU package: `libwhisper.so` and `libggml-*-whisper.so` for linux-x64 (needs `libgomp.so.1`, not in the aspnet image: without it the load fails with "Failed to load native whisper library"). CUDA Linux package: adds a 167 MB `libggml-cuda-whisper.so` built for compute capabilities 75 to 121, linking `libcudart.so.13`, `libcublas.so.13` and the driver's `libcuda.so.1`; it bundles no CUDA libraries. Windows CUDA needs `cublas64_13.dll` (and `cublasLt64_13.dll`) on PATH: NVIDIA's cuBLAS 13.0.2.14 redistributable zip was enough, no toolkit install. |
| Whisper.net on the RTX 3060 (Windows, native) | large-v3-turbo q8_0, 5 min of real speech | 300 s request, no context, prompt carried, flash attention: 46.2x realtime, 159 segments; 30 s requests: 22.0x (no DTW), 16.3x (DTW). Whole hour, mostly music, 30 s requests, host CPU at 100%: 7.5x. base.en on the GPU: 8.3x to 9.9x (more temperature fallbacks). VRAM: about 1 GB above the idle desktop (2.5 to 3.5 GB). |
| Whisper.net on the GPU in a Linux container | aspnet:10.0 + `libgomp1` + `cuda-cudart-13-0` + `libcublas-13-0`, `docker run --gpus all` | Loads the CUDA runtime (`auto` order); 300 s request: 28.2x (no DTW); 30 s requests: 12.7x (no DTW), 9.5x (DTW). Same words as on Windows. Without `--gpus` the same image falls back to the CPU runtime (after a 92 s first-load attempt). |
| Whisper.net on the CPU | base.en | 9.0x (Windows, 8 threads, host 57% busy, 60 s); 1.8x to 2.4x in a container with the host at 100%. With the host saturated, whisper.cpp's OpenMP threads starve: a 300 s run did not finish its first 30 s window in 18 minutes. CPU transcription is for machines without a GPU and needs few threads. large-v3-turbo on the CPU: 0.94x (4 threads, loaded host). |
| Word timestamps | Whisper.net `SegmentData` and `WhisperToken` | Per token `Text`, `Start` and `End` (centiseconds), `DtwTimestamp` (centiseconds, -1 without DTW), `Probability`, `ProbabilityLog`; per segment `NoSpeechProbability`. Sample with DTW: `' As' 0 25 dtw 80 p 0.790`, `' she' 25 63 dtw 104 p 0.983`. `SegmentData` and `WhisperToken` are constructible in tests without the native library. |
| DTW on long requests | whisper.cpp `f24588a2` via Whisper.net | Stops after about 58 s of a 300 s request (18 segments) with context on or off and DTW memory 128 MB or 1 GB; 60 s requests lose about 20%. Not tested on v1.9.5's whisper-server, which the original Task 16 would have called with `--dtw` and 300 s chunks. Hence DTW off by default. |
| Images | throwaway builds, removed afterwards | `aspnet:10.0`: 340 MB; + app + `libgomp1` (cpu target): 376 MB; + CUDA libraries (cuda target): 2.01 GB (of which 155 MB was a Windows CUDA library the real build does not carry). For comparison the original plan's runtime base `nvidia/cuda:13.0.1-runtime-ubuntu24.04` is 1.57 GB compressed and its build stage `13.0.1-devel` 3.77 GB compressed. |
| LLM enrichment | OllamaSharp 5.5.0 (MIT) and plain `HttpClient` against Ollama 0.40.4, qwen3.6:latest | Both return schema-valid JSON with `format` = the schema and `think: false` (117 output tokens, 9.1 s and 9.3 s warm; the first call 179 s while the model loaded under host load). OllamaSharp streams (93 chunks, first after 0.84 s). An `HttpClient` timeout surfaces as `TaskCanceledException` with an inner `TimeoutException`, which `OllamaClient` (Task 14) already maps to "pending". |
| Native tools elsewhere | plan Tasks 5 to 8 | The clipping, dead-air, swear and filler detectors and `FeatureExtractor` are plain C# (checked); nothing else in the plan starts a process. |

## Prerequisites and owner actions

1. **Nothing to install for building or testing** (D18): decoding, resampling and test MP3 encoding are NuGet packages restored by `dotnet build`; no ffmpeg, CMake, CUDA toolkit or compiler. The GPU image fetches its two CUDA libraries from NVIDIA's apt repository at `docker build` time (about 1.3 GB), so the first CUDA build needs internet access and some minutes.
2. **Download the whisper models** into `models/` at the repo root (gitignored, about 1 GB). Task 16 Step 1 gives the exact commands with the pinned commit and sha256 checks. They are mounted read-only into the analysis container; nothing downloads a model at run time.
3. **GPU:** already verified (D4, D18). Keep Docker Desktop on the WSL 2 backend with the NVIDIA driver installed on Windows (no driver inside WSL, no CUDA toolkit on Windows: the image carries the CUDA 13.0 runtime libraries, the driver supplies `libcuda`). The driver must support CUDA 13.0 (610.88 reports 13.3). If `nvidia-smi` in a container ever fails after a driver update, rerun the check in D4. Running the service natively on Windows with CUDA (not needed for deployment) also needs NVIDIA's cuBLAS 13 and cudart 13 redistributable DLLs on PATH and `WAA_WHISPER_DEVICE=cuda`.
4. **Ollama:** keep it running on the host at `http://127.0.0.1:11434` and keep `qwen3.6:latest` pulled. Do not change its tag without re-running the Task 14 smoke check; the digest used is recorded per result. If Ollama is updated so that `qwen3.6:latest` points at a new digest, enrichments are not redone automatically (they record the digest); run `--rebuild-enrichment` if wanted.
5. **Disk:** decide the free-space threshold (`WAA_MIN_FREE_GB`, default 20). Analysis output is about 1.5 MB per audio hour (about 13 GB a year).
6. **Validation labels (Task 18):** listen to at least 30 minutes of real Burst speech (several presenters, before and after 21:00) and label fillers, swears, dead air and clipping in a local CSV under `data/analysis/validation/` (never committed). The measured precision and recall go into `docs/analysis-validation.md`.
7. **Review the swear list** `config/analysis/swearwords.json` (D12) and add any local terms in a mounted copy if needed.
8. **Review the decisions above**, especially D5 (model), D7 (A first), D11 (watershed hours), D13 (what is suppressed) and D18 (NuGet stack, whisper in process, DTW off by default, GroovyMp3's LGPL licence in the test project only).

## Issues closed by this plan

| Issue | Closed by | Notes |
|---|---|---|
| #76 ffmpeg decode of a scratch copy | Task 9 (managed decode, D18), Task 16 (image) | Decoded without ffmpeg; say so when closing. |
| #77 Transcription with whisper.cpp | Tasks 10, 12, 16; realtime factor measured in Task 19 | whisper.cpp through Whisper.net (D18). Close after Task 19 records the model and the realtime factor. |
| #78 Swearing with tiers and the watershed | Task 7 | |
| #79 Clipping | Task 5 | Thresholds stay provisional until Task 18's real-audio labels. |
| #80 Filler words | Task 8 | Labelled an estimate. |
| #81 Dead air | Task 6 | |
| #82 events.json timeline | Tasks 3, 12, 13 | |
| #83 Enricher | Task 14 | |
| #84 Validation on a labelled sample | Task 18 (synthetic, in CI), Task 19 (owner's real labels) | Close when the real-sample figures are in the doc. |
| #85 Throughput | Tasks 15, 16, 19 | |
| #9 Plan 3 umbrella | Task 19, when the above are closed | The player UI (markers and segments from events) is plan 4; Task 17 provides its data API. |

Context only: #2 and #3 (song identification) are not built; `song` is a reserved event type and the enricher's `songs` and `artists` lists (spoken mentions with evidence) are the input #2 step 1 describes. #99 (write plans) gets this plan.

## Review Focus

Input classes the spec implies but that the main path would not exercise. Each has a test in the task that owns the code.

1. Whisper inventing text over music and silence: repeated lines, "Thank you for watching", `[BLANK_AUDIO]`, a segment of only fillers repeated (Task 10: `Known_hallucinations_annotations_and_repeats_are_suppressed`; Task 8: `Suppressed_segments_are_not_counted_and_do_not_add_speech_time`).
2. Clipping that straddles a read buffer, and decoded samples above full scale (MP3 decode overshoots past 1.0) or at exactly -1.0 (Task 5: `Run_split_across_reads_counts_once`, `Overshoot_and_negative_full_scale_count`).
3. Swear tokens as whisper really writes them: `Shit!`, `FUCKED`, `fuckin'`, `bull-shit`, `f***ing`, and near misses `Scunthorpe`, `Dickens`, `assess`, `cocktail`, `shitake`, door `knobs` (Task 7: `Inflections_punctuation_and_masks_match`, `Near_misses_do_not_match`).
4. The watershed on the clock-change weekend, at exactly 21:00 and 05:30 local, and a UTC instant that is a different local day (Task 7: `Watershed_follows_uk_local_time_across_the_clock_change`).
5. A container killed mid-transcription, and the disk filling mid-job: resume must not re-transcribe finished chunks, scratch must be empty afterwards, the job must stop between chunks (Task 12: `Resume_after_a_crash_skips_finished_chunks`, `Low_disk_stops_between_chunks_and_keeps_progress`).
6. An LLM answer that is valid JSON but invents a guest, quotes words that are not in the transcript, names someone the quote does not contain, or comes back wrapped in a code fence (Task 14: `Invented_claims_are_dropped`, `Fenced_json_is_accepted`).
7. Segments that are not tidy hours: a 2 second restart segment, a sidecar rewritten after crash recovery, an audio file whose size disagrees with its sidecar, a `.part` beside it (Task 11: `Discovery_skips_parts_tmp_and_mismatched_sizes`, `Changed_sidecar_changes_the_fingerprint_and_reprocesses`, and the 2 s segment in `Discovery_reads_closed_segments_and_builds_job_ids_newest_first`).
8. A managed decoder that silently drops frames (NLayer's bit-reservoir ring wrapping at a frame boundary, D18) or loses sync on stray bytes: the decoded length must stay frames x 1152 and match the sidecar (Task 9: `A_long_stream_keeps_every_frame_through_the_reservoir_wrap`, `Stray_bytes_and_a_corrupt_frame_keep_the_timeline`).

---

## File structure

```
.gitignore                                              add /models/
config/analysis/analysis.json                           thresholds, prompts, transcription options (versioned)
config/analysis/swearwords.json                         tiered list, suffixes, allow list (versioned)
config/analysis/prompts/enrich-v1.system.md             enricher system prompt
config/analysis/prompts/enrich-v1.user.md               enricher user prompt template
config/analysis/prompts/enrich-v1.schema.json           JSON schema sent to Ollama
src/WebAudioArchive.Core/Analysis/AnalysisJson.cs       JSON options, atomic write, safe read
src/WebAudioArchive.Core/Analysis/AnalysisPaths.cs      output layout under data/analysis
src/WebAudioArchive.Core/Analysis/SegmentTimeline.cs    audio offset <-> UTC for a segment
src/WebAudioArchive.Core/Analysis/TranscriptDocument.cs transcript.json schema
src/WebAudioArchive.Core/Analysis/AnalysisEvent.cs      events.json schema, types, severities
src/WebAudioArchive.Core/Analysis/RecorderPreference.cs A before B rule shared by readers
src/WebAudioArchive.Core/Analysis/TranscriptSlicer.cs   UTC lines for any range (web, enricher)
src/WebAudioArchive.Core/Analysis/ShowAnalysis.cs       show-analysis.json schema
src/WebAudioArchive.Core/Analysis/ShowEnrichment.cs     enrichment.json schema
src/WebAudioArchive.Core/Analysis/AnalysisStore.cs      read-only folder helpers for readers (web)
src/WebAudioArchive.Core/Schedule/WeeklySchedule.cs     moved from Web (namespace change only)
src/WebAudioArchive.Core/Schedule/ScheduleExpander.cs   moved from Web (namespace change only)
src/WebAudioArchive.Core/Schedule/SavedScheduleFile.cs  the saved schedule format, shared
src/WebAudioArchive.Analysis/WebAudioArchive.Analysis.csproj
src/WebAudioArchive.Analysis/Settings/AnalysisSettings.cs   environment settings
src/WebAudioArchive.Analysis/Settings/AnalysisConfig.cs     config/analysis loader and validation
src/WebAudioArchive.Analysis/Audio/ClipLevels.cs            levels kept in features.json
src/WebAudioArchive.Analysis/Audio/WavFile.cs               read, slice and write PCM WAV
src/WebAudioArchive.Analysis/Audio/FeatureExtractor.cs      window levels and clip runs, streaming
src/WebAudioArchive.Analysis/Detectors/ClippingDetector.cs
src/WebAudioArchive.Analysis/Detectors/DeadAirDetector.cs
src/WebAudioArchive.Analysis/Detectors/WordNormaliser.cs
src/WebAudioArchive.Analysis/Detectors/SwearList.cs
src/WebAudioArchive.Analysis/Detectors/SwearList.Matching.cs
src/WebAudioArchive.Analysis/Detectors/Watershed.cs
src/WebAudioArchive.Analysis/Detectors/SwearDetector.cs
src/WebAudioArchive.Analysis/Detectors/FillerDetector.cs
src/WebAudioArchive.Analysis/Detectors/DetectorRunner.cs    detector output -> AnalysisEvent
src/WebAudioArchive.Analysis/Decode/ScratchSpace.cs
src/WebAudioArchive.Analysis/Decode/Mp3FrameReader.cs       frame splitter + NLayer frame decoder (D18)
src/WebAudioArchive.Analysis/Decode/SpeechResampler.cs      16 kHz mono, NAudio WDL sinc
src/WebAudioArchive.Analysis/Decode/SegmentDecoder.cs
src/WebAudioArchive.Analysis/Transcription/ITranscriber.cs
src/WebAudioArchive.Analysis/Transcription/WhisperNetTranscriber.cs  whisper.cpp in process (D18)
src/WebAudioArchive.Analysis/Transcription/WhisperResponse.cs       Whisper.net segments to RawSegment
src/WebAudioArchive.Analysis/Transcription/TranscriptFilter.cs
src/WebAudioArchive.Analysis/Transcription/ChunkPlanner.cs
src/WebAudioArchive.Analysis/Transcription/PromptBuilder.cs
src/WebAudioArchive.Analysis/Transcription/TranscriptAssembler.cs  transcript.json and the resume file
src/WebAudioArchive.Analysis/Work/SegmentDiscovery.cs
src/WebAudioArchive.Analysis/Work/WorkPlanner.cs
src/WebAudioArchive.Analysis/Work/JobState.cs
src/WebAudioArchive.Analysis/Work/DiskGuard.cs
src/WebAudioArchive.Analysis/Work/SegmentPipeline.cs
src/WebAudioArchive.Analysis/Shows/ScheduleProvider.cs
src/WebAudioArchive.Analysis/Shows/DayTimeline.cs
src/WebAudioArchive.Analysis/Shows/ShowAnalysisBuilder.cs
src/WebAudioArchive.Analysis/Shows/ShowProcessor.cs
src/WebAudioArchive.Analysis/Enrichment/ILlmClient.cs
src/WebAudioArchive.Analysis/Enrichment/OllamaClient.cs
src/WebAudioArchive.Analysis/Enrichment/EnrichmentPrompt.cs
src/WebAudioArchive.Analysis/Enrichment/EnrichmentValidator.cs
src/WebAudioArchive.Analysis/Enrichment/Enricher.cs
src/WebAudioArchive.Analysis/Validation/DetectorScore.cs
src/WebAudioArchive.Analysis/AnalysisMetrics.cs
src/WebAudioArchive.Analysis/AnalysisHealth.cs              /healthz body and status rule
src/WebAudioArchive.Analysis/AnalysisRunner.cs              one pass of the work lane, --rebuild
src/WebAudioArchive.Analysis/AnalysisComposition.cs         builds the service from settings
src/WebAudioArchive.Analysis/Benchmark.cs                   --benchmark
src/WebAudioArchive.Analysis.Service/WebAudioArchive.Analysis.Service.csproj
src/WebAudioArchive.Analysis.Service/Program.cs             host, /healthz, --healthcheck, --benchmark, --rebuild, --validate
src/WebAudioArchive.Analysis.Service/AnalysisWorker.cs      runs a pass every 60 s; AnalysisEntryPoint
src/WebAudioArchive.Web/Analysis/AnalysisReader.cs          read-only access for the web app
src/WebAudioArchive.Web/Analysis/AnalysisMarkers.cs         events -> PDMediaMarker / PDMediaSegment
src/WebAudioArchive.Web/WebSettings.cs, Program.cs           WAA_ANALYSIS, three read-only endpoints
src/WebAudioArchive.Web/Schedule/ScheduleService.cs          uses the shared saved schedule format (Task 13)
tests/WebAudioArchive.Tests/Support/Signals.cs              synthetic audio
tests/WebAudioArchive.Tests/Support/TestMp3.cs              synthetic MP3 with GroovyMp3 (no native tool)
tests/WebAudioArchive.Tests/Support/FakeTranscriber.cs
tests/WebAudioArchive.Tests/Support/FakeLlm.cs
tests/WebAudioArchive.Tests/Support/AnalysisFixtures.cs     decodable synthetic segments, transcriber answers
tests/WebAudioArchive.Tests/Support/TranscriptFixtures.cs   hand-made transcripts
tests/WebAudioArchive.Tests/Support/ValidationCorpus.cs     the labelled synthetic sample
tests/WebAudioArchive.Tests/Support/RepoFiles.cs            deployment file checks
tests/WebAudioArchive.Tests/*Tests.cs                       one file per task area
docker/Dockerfile.analysis                                  targets cpu and cuda (D18)
docker/docker-compose.yml                                   add analysis (recorders untouched)
docker/docker-compose.gpu.yml                               GPU override for analysis only
.github/workflows/ci.yml                                    analysis image builds (cpu and cuda targets)
docs/analysis.md                                            how analysis works, files, schemas, operations
docs/analysis-validation.md                                 thresholds, synthetic and real-sample results
docs/operations.md, README.md, spec                         updated in Task 19
```

---

### Task 1: Analysis projects, settings and a passing smoke test

**Files:**
- Create: `src/WebAudioArchive.Analysis/WebAudioArchive.Analysis.csproj`, `src/WebAudioArchive.Analysis/Settings/AnalysisSettings.cs`
- Create: `src/WebAudioArchive.Analysis.Service/WebAudioArchive.Analysis.Service.csproj`, `src/WebAudioArchive.Analysis.Service/Program.cs` (placeholder, replaced in Task 15)
- Modify: `WebAudioArchive.slnx`, `tests/WebAudioArchive.Tests/WebAudioArchive.Tests.csproj`, `.gitignore`
- Test: `tests/WebAudioArchive.Tests/AnalysisSettingsTests.cs`

**Interfaces:**
- Produces:
  - `sealed record RecorderRoot(string Id, string Root)`
  - `sealed class AnalysisSettingsException(string message) : Exception`
  - `sealed record AnalysisSettings(string Station, IReadOnlyList<RecorderRoot> Recorders, IReadOnlyList<string> Preference, string OutputDir, string ScratchDir, long ScratchMaxBytes, long MinFreeBytes, string WhisperModelPath, WhisperDevice WhisperDevice, int WhisperThreads, bool WhisperDtw, Uri OllamaUrl, string OllamaModel, bool EnrichmentEnabled, string ConfigPath, string? StationsConfigPath, string? ScheduleFile, DateTimeOffset? Since, TimeSpan LagAlarm, int Port)` with `string WhisperModel` (the model file name without extension) and `static AnalysisSettings Load(Func<string, string?> get)`; `enum WhisperDevice { Auto, Cpu, Cuda }` (D18).
  - Test project references `WebAudioArchive.Analysis` (no alias) and `WebAudioArchive.Analysis.Service` with `Aliases="analysissvc"`; `config/analysis/**` is copied to `AppContext.BaseDirectory/config/analysis/`.

- [ ] **Step 1: Create the projects**

Run:
```powershell
New-Item -ItemType Directory -Force src/WebAudioArchive.Analysis/Settings, src/WebAudioArchive.Analysis.Service, config/analysis/prompts | Out-Null
```

`src/WebAudioArchive.Analysis/WebAudioArchive.Analysis.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <!-- Decision D18: managed MP3 frame decoder, resampler and whisper.cpp bindings. No native runtime here (see the service). -->
    <PackageReference Include="NLayer" Version="3.0.0" />
    <PackageReference Include="NAudio.Core" Version="3.1.0" />
    <PackageReference Include="Whisper.net" Version="1.9.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\WebAudioArchive.Core\WebAudioArchive.Core.csproj" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="WebAudioArchive.Tests" />
  </ItemGroup>
</Project>
```

`src/WebAudioArchive.Analysis.Service/WebAudioArchive.Analysis.Service.csproj` (the native whisper.cpp runtimes are referenced only here: their build targets do not flow to referencing projects, so the test project never copies the 167 MB CUDA library and never loads native code):
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Whisper.net.Runtime" Version="1.9.1" />
    <PackageReference Include="Whisper.net.Runtime.Cuda.Linux" Version="1.9.1" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\WebAudioArchive.Core\WebAudioArchive.Core.csproj" />
    <ProjectReference Include="..\WebAudioArchive.Analysis\WebAudioArchive.Analysis.csproj" />
  </ItemGroup>
</Project>
```

`src/WebAudioArchive.Analysis.Service/Program.cs` (placeholder until Task 15):
```csharp
return 0;
```

In `tests/WebAudioArchive.Tests/WebAudioArchive.Tests.csproj`, add inside the `ProjectReference` item group:
```xml
    <ProjectReference Include="..\..\src\WebAudioArchive.Analysis\WebAudioArchive.Analysis.csproj" />
    <!-- Aliased because the Worker project has a public top-level Program class. -->
    <ProjectReference Include="..\..\src\WebAudioArchive.Analysis.Service\WebAudioArchive.Analysis.Service.csproj" Aliases="analysissvc" />
```
inside the item group holding the fixtures `None` item:
```xml
    <None Include="..\..\config\analysis\**\*" Link="config\analysis\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
```
and inside the `PackageReference` item group (decision D18; a managed LAME port, LGPL-3.0, used only by the tests to make synthetic MP3s, never shipped in an image):
```xml
    <PackageReference Include="GroovyMp3" Version="0.1.3" />
```

Append to `.gitignore`:
```
/models/
```

Run:
```powershell
dotnet sln add src/WebAudioArchive.Analysis src/WebAudioArchive.Analysis.Service
```
Expected: `Project ... added to the solution.` twice; `WebAudioArchive.slnx` now lists both under `/src/`.

- [ ] **Step 2: Write the failing settings tests**

`tests/WebAudioArchive.Tests/AnalysisSettingsTests.cs`:
```csharp
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Tests;

public sealed class AnalysisSettingsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-aset-" + Guid.NewGuid().ToString("N"));

    public AnalysisSettingsTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        Directory.CreateDirectory(Path.Combine(_root, "b"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    static Func<string, string?> Env(params (string Name, string Value)[] pairs)
    {
        var d = pairs.ToDictionary(p => p.Name, p => p.Value);
        return n => d.GetValueOrDefault(n);
    }

    string Recorders => $"a={_root}/a,b={_root}/b";

    [Fact]
    public void Defaults_are_the_compose_values()
    {
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders)));

        Assert.Equal("burst", s.Station);
        Assert.Equal(new[] { "a", "b" }, s.Preference);
        Assert.Equal("/analysis", s.OutputDir);
        Assert.Equal("/scratch", s.ScratchDir);
        Assert.Equal(2048L * 1024 * 1024, s.ScratchMaxBytes);
        Assert.Equal(20L * 1024 * 1024 * 1024, s.MinFreeBytes);
        Assert.Equal("/models/ggml-large-v3-turbo-q8_0.bin", s.WhisperModelPath);
        Assert.Equal("ggml-large-v3-turbo-q8_0", s.WhisperModel);
        Assert.Equal(WhisperDevice.Auto, s.WhisperDevice);
        Assert.Equal(4, s.WhisperThreads);
        Assert.False(s.WhisperDtw);
        Assert.Equal(new Uri("http://host.docker.internal:11434"), s.OllamaUrl);
        Assert.Equal("qwen3.6:latest", s.OllamaModel);
        Assert.True(s.EnrichmentEnabled);
        Assert.Equal("/config/analysis/analysis.json", s.ConfigPath);
        Assert.Equal(TimeSpan.FromHours(6), s.LagAlarm);
        Assert.Equal(8080, s.Port);
        Assert.Null(s.Since);
    }

    [Fact]
    public void Preference_puts_listed_recorders_first_and_appends_the_rest()
    {
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_ANALYSIS_PREFERENCE", "b")));
        Assert.Equal(new[] { "b", "a" }, s.Preference);
    }

    [Fact]
    public void Since_must_be_utc()
    {
        var ok = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_ANALYSIS_SINCE", "2026-10-15T00:00:00Z")));
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 0, 0, 0, TimeSpan.Zero), ok.Since);

        Assert.Contains("WAA_ANALYSIS_SINCE", Assert.Throws<AnalysisSettingsException>(() =>
            AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_ANALYSIS_SINCE", "2026-10-15T01:00:00+01:00")))).Message);
    }

    [Theory]
    [InlineData("WAA_OLLAMA_MODEL", "kimi-k2.6:cloud", "cloud model")]
    [InlineData("WAA_OLLAMA_URL", "https://ollama.example.com", "leave")]
    [InlineData("WAA_WHISPER_DEVICE", "gpu", "auto, cpu or cuda")]
    [InlineData("WAA_SCRATCH_MAX_MB", "10", "WAA_SCRATCH_MAX_MB")]
    [InlineData("WAA_ENRICHMENT", "maybe", "on or off")]
    [InlineData("WAA_ANALYSIS_PREFERENCE", "c", "recorder 'c'")]
    [InlineData("WAA_STATION", "../etc", "WAA_STATION")]
    public void Bad_values_are_refused_by_name(string name, string value, string expected)
    {
        var ex = Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), (name, value))));
        Assert.Contains(expected, ex.Message);
    }

    [Theory]
    [InlineData("http://127.0.0.1:11434")]
    [InlineData("http://ollama:11434")]
    [InlineData("http://192.168.1.20:11434")]
    [InlineData("http://10.0.0.5:11434")]
    [InlineData("http://172.20.0.3:11434")]
    [InlineData("http://[::1]:11434")]
    public void Local_and_private_ollama_hosts_are_accepted(string url)
    {
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_OLLAMA_URL", url)));
        Assert.Equal(new Uri(url), s.OllamaUrl);
    }

    [Fact]
    public void A_remote_ollama_needs_an_explicit_opt_in()
    {
        var s = AnalysisSettings.Load(Env(("WAA_RECORDERS", Recorders), ("WAA_OLLAMA_URL", "https://ollama.example.com"), ("WAA_OLLAMA_ALLOW_REMOTE", "true")));
        Assert.Equal("ollama.example.com", s.OllamaUrl.Host);
    }

    [Fact]
    public void Missing_recorders_or_roots_name_the_setting()
    {
        Assert.Contains("WAA_RECORDERS is not set", Assert.Throws<AnalysisSettingsException>(() => AnalysisSettings.Load(Env())).Message);
        Assert.Contains("does not exist", Assert.Throws<AnalysisSettingsException>(() =>
            AnalysisSettings.Load(Env(("WAA_RECORDERS", $"a={_root}/missing")))).Message);
        Assert.Contains("listed twice", Assert.Throws<AnalysisSettingsException>(() =>
            AnalysisSettings.Load(Env(("WAA_RECORDERS", $"a={_root}/a,a={_root}/b")))).Message);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AnalysisSettingsTests`
Expected: build FAILS with `CS0246: The type or namespace name 'AnalysisSettings' could not be found`.

- [ ] **Step 4: Implement the settings**

`src/WebAudioArchive.Analysis/Settings/AnalysisSettings.cs`:
```csharp
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Analysis.Settings;

/// <summary>One recorder whose closed segments are analysed: its id (a, b) and its data root, mounted read-only.</summary>
public sealed record RecorderRoot(string Id, string Root);

/// <summary>Where whisper runs (D18): Auto tries the CUDA runtime and falls back to the CPU.</summary>
public enum WhisperDevice
{
    Auto,
    Cpu,
    Cuda,
}

public sealed class AnalysisSettingsException(string message) : Exception(message);

/// <summary>
/// Analysis service settings, from environment variables (or any configuration source with the same names). Every
/// problem names the setting. Defaults are the values used in docker/docker-compose.yml.
/// </summary>
public sealed partial record AnalysisSettings(
    string Station,
    IReadOnlyList<RecorderRoot> Recorders,
    IReadOnlyList<string> Preference,
    string OutputDir,
    string ScratchDir,
    long ScratchMaxBytes,
    long MinFreeBytes,
    string WhisperModelPath,
    WhisperDevice WhisperDevice,
    int WhisperThreads,
    bool WhisperDtw,
    Uri OllamaUrl,
    string OllamaModel,
    bool EnrichmentEnabled,
    string ConfigPath,
    string? StationsConfigPath,
    string? ScheduleFile,
    DateTimeOffset? Since,
    TimeSpan LagAlarm,
    int Port)
{
    /// <summary>The model label recorded in transcripts: the model file name without its extension.</summary>
    public string WhisperModel => Path.GetFileNameWithoutExtension(WhisperModelPath);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,31}$")]
    private static partial Regex IdPattern();

    public static AnalysisSettings Load(Func<string, string?> get)
    {
        string Text(string name, string fallback)
        {
            var v = get(name);
            return string.IsNullOrWhiteSpace(v) ? fallback : v.Trim();
        }

        string? Optional(string name)
        {
            var v = get(name);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        double Number(string name, double fallback, double min, double max)
        {
            var v = get(name);
            if (string.IsNullOrWhiteSpace(v)) return fallback;
            if (!double.TryParse(v.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d < min || d > max)
                throw new AnalysisSettingsException(string.Create(CultureInfo.InvariantCulture, $"{name}: '{v}' must be a number from {min} to {max}."));
            return d;
        }

        var station = Text("WAA_STATION", "burst");
        if (!IdPattern().IsMatch(station))
            throw new AnalysisSettingsException($"WAA_STATION: '{station}' is not a valid station id (lower-case letters, digits and dashes).");

        var recordersText = get("WAA_RECORDERS");
        if (string.IsNullOrWhiteSpace(recordersText))
            throw new AnalysisSettingsException("WAA_RECORDERS is not set. Give each recorder id and its read-only data root, for example WAA_RECORDERS=a=/data/a,b=/data/b");
        var recorders = new List<RecorderRoot>();
        foreach (var (id, root) in ParsePairs("WAA_RECORDERS", recordersText))
        {
            if (!Directory.Exists(root))
                throw new AnalysisSettingsException($"WAA_RECORDERS: the data root '{root}' for recorder '{id}' does not exist.");
            recorders.Add(new RecorderRoot(id, root));
        }

        var preference = Text("WAA_ANALYSIS_PREFERENCE", string.Join(',', recorders.Select(r => r.Id)))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToLowerInvariant())
            .Distinct()
            .ToList();
        foreach (var p in preference)
            if (recorders.All(r => r.Id != p))
                throw new AnalysisSettingsException($"WAA_ANALYSIS_PREFERENCE: recorder '{p}' is not listed in WAA_RECORDERS.");
        foreach (var r in recorders.Where(r => !preference.Contains(r.Id)))
            preference.Add(r.Id);

        var device = Text("WAA_WHISPER_DEVICE", "auto").ToLowerInvariant() switch
        {
            "auto" => WhisperDevice.Auto,
            "cpu" => WhisperDevice.Cpu,
            "cuda" => WhisperDevice.Cuda,
            var v => throw new AnalysisSettingsException($"WAA_WHISPER_DEVICE: '{v}' must be auto, cpu or cuda."),
        };
        var dtw = Text("WAA_WHISPER_DTW", "off").ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            var v => throw new AnalysisSettingsException($"WAA_WHISPER_DTW: '{v}' must be on or off."),
        };
        var ollama = HttpUrl("WAA_OLLAMA_URL", Text("WAA_OLLAMA_URL", "http://host.docker.internal:11434"));
        var allowRemote = string.Equals(Optional("WAA_OLLAMA_ALLOW_REMOTE"), "true", StringComparison.OrdinalIgnoreCase);
        if (!allowRemote && !IsLocalOrPrivate(ollama))
            throw new AnalysisSettingsException($"WAA_OLLAMA_URL: '{ollama}' is not on this machine or a private network, so transcripts would leave it. Use a local Ollama, or set WAA_OLLAMA_ALLOW_REMOTE=true if that host is yours.");

        var model = Text("WAA_OLLAMA_MODEL", "qwen3.6:latest");
        if (model.EndsWith(":cloud", StringComparison.OrdinalIgnoreCase) || model.EndsWith("-cloud", StringComparison.OrdinalIgnoreCase))
            throw new AnalysisSettingsException($"WAA_OLLAMA_MODEL: '{model}' is an Ollama cloud model, which sends the transcript off the machine. Choose a local model such as qwen3.6:latest.");

        var enrichment = Text("WAA_ENRICHMENT", "on").ToLowerInvariant() switch
        {
            "on" => true,
            "off" => false,
            var v => throw new AnalysisSettingsException($"WAA_ENRICHMENT: '{v}' must be on or off."),
        };

        DateTimeOffset? since = null;
        if (Optional("WAA_ANALYSIS_SINCE") is { } sinceText)
        {
            if (!sinceText.EndsWith('Z')
                || !DateTimeOffset.TryParse(sinceText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var s))
                throw new AnalysisSettingsException($"WAA_ANALYSIS_SINCE: '{sinceText}' must be a UTC time ending in Z, such as 2026-10-15T00:00:00Z.");
            since = s;
        }

        return new AnalysisSettings(
            station,
            recorders,
            preference,
            Text("WAA_ANALYSIS_OUT", "/analysis"),
            Text("WAA_SCRATCH", "/scratch"),
            (long)Number("WAA_SCRATCH_MAX_MB", 2048, 64, 1_048_576) * 1024 * 1024,
            (long)(Number("WAA_MIN_FREE_GB", 20, 0, 1_000_000) * 1024 * 1024 * 1024),
            Text("WAA_WHISPER_MODEL_PATH", "/models/ggml-large-v3-turbo-q8_0.bin"),
            device,
            (int)Number("WAA_WHISPER_THREADS", 4, 1, 64),
            dtw,
            ollama,
            model,
            enrichment,
            Text("WAA_ANALYSIS_CONFIG", "/config/analysis/analysis.json"),
            Optional("WAA_STATIONS_CONFIG"),
            Optional("WAA_SCHEDULE_FILE"),
            since,
            TimeSpan.FromHours(Number("WAA_LAG_ALARM_HOURS", 6, 0.1, 24 * 365)),
            (int)Number("WAA_PORT", 8080, 1, 65535));
    }

    static Uri HttpUrl(string name, string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u
            : throw new AnalysisSettingsException($"{name}: '{value}' is not an absolute http(s) URL.");

    /// <summary>Loopback, Docker's host alias, a single-label name (a compose service) or a private address.</summary>
    static bool IsLocalOrPrivate(Uri u)
    {
        var host = u.Host.Trim('[', ']').ToLowerInvariant();
        if (host is "localhost" or "host.docker.internal") return true;
        if (!IPAddress.TryParse(host, out var ip)) return !host.Contains('.');
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
        }

        return ip.AddressFamily == AddressFamily.InterNetworkV6 && (ip.IsIPv6UniqueLocal || ip.IsIPv6LinkLocal);
    }

    static List<(string Id, string Value)> ParsePairs(string name, string text)
    {
        var result = new List<(string, string)>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0 || eq == part.Length - 1)
                throw new AnalysisSettingsException($"{name}: '{part}' is not of the form id=value.");
            var id = part[..eq].Trim().ToLowerInvariant();
            if (!IdPattern().IsMatch(id))
                throw new AnalysisSettingsException($"{name}: '{id}' is not a valid recorder id (lower-case letters, digits and dashes).");
            if (result.Any(r => r.Item1 == id))
                throw new AnalysisSettingsException($"{name}: recorder '{id}' is listed twice.");
            result.Add((id, part[(eq + 1)..].Trim()));
        }

        return result;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AnalysisSettingsTests`
Expected: PASS, 18 tests (5 facts and 13 theory cases). Then run `dotnet test` and expect every earlier test still to pass.

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Analysis src/WebAudioArchive.Analysis.Service WebAudioArchive.slnx tests/WebAudioArchive.Tests/WebAudioArchive.Tests.csproj tests/WebAudioArchive.Tests/AnalysisSettingsTests.cs .gitignore
git commit -m "Analysis: add the analysis library and service projects and their settings (#9)"
```

---

### Task 2: Detector configuration and the swear list

**Files:**
- Create: `config/analysis/analysis.json`, `config/analysis/swearwords.json`
- Create: `src/WebAudioArchive.Analysis/Settings/AnalysisConfig.cs`, `src/WebAudioArchive.Analysis/Audio/ClipLevels.cs`, `src/WebAudioArchive.Analysis/Detectors/SwearList.cs`
- Test: `tests/WebAudioArchive.Tests/AnalysisConfigTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - Option records (all `init` properties with defaults): `ClippingOptions { double Threshold = 0.99; int MinRunSamples = 3; int MergeWithinMs = 250; int MediumMs = 100; }`, `DeadAirOptions { double ThresholdDbfs = -50; double MinSeconds = 5; double SevereSeconds = 30; }`, `SwearOptions { string WordList = "swearwords.json"; string WatershedStart = "21:00"; string WatershedEnd = "05:30"; }`, `FillerOptions { IReadOnlyList<string> Words; bool CountYouKnow; }`, `TranscriptionOptions { string Language; double ChunkSeconds; double BoundarySearchSeconds; IReadOnlyList<string> Glossary; string VerbatimPrompt; double MaxNoSpeechProb; double MinAvgLogProb; IReadOnlyList<string> Hallucinations; int MaxRepeats; string TranscriptKey(); }`, `EnrichmentOptions { string PromptDir; string PromptName; int NumCtx; int MaxTranscriptChars; int Seed; double Temperature; int MaxItems; }`, `AnalysisConfig { required string Version; ... }`.
  - `sealed record LoadedConfig(AnalysisConfig Config, SwearList Swears, string ConfigDir, string DetectorVersion)`; `static class AnalysisConfigLoader { const string DetectorCodeVersion; static LoadedConfig Load(string path); }`; `sealed class AnalysisConfigException(IReadOnlyList<string> errors)` with `Errors`.
  - `static class ClipLevels { static readonly double[] Levels = [0.98, 0.99, 0.999]; const int MinStoredRun = 2; const int MaxRunsPerLevel = 50_000; static int IndexOf(double level); }`
  - `static class SwearTiers { Mild, Strong, Severe; All; static int Rank(string) }`; `sealed class SwearList { string Version; IReadOnlyDictionary<string, string> Forms; IReadOnlySet<string> Allow; IReadOnlyList<string> Suffixes; static SwearList Load(string path); static SwearList Parse(string json, string source); }` (matching is added in Task 7).

- [ ] **Step 1: Write the config files**

`config/analysis/analysis.json`:
```json
{
  "version": "2026-10-16.1",
  "clipping": { "threshold": 0.99, "minRunSamples": 3, "mergeWithinMs": 250, "mediumMs": 100 },
  "deadAir": { "thresholdDbfs": -50, "minSeconds": 5, "severeSeconds": 30 },
  "swearing": { "wordList": "swearwords.json", "watershedStart": "21:00", "watershedEnd": "05:30" },
  "fillers": { "words": ["um", "uh", "er", "erm", "ah", "hmm"], "countYouKnow": false },
  "transcription": {
    "language": "en",
    "chunkSeconds": 300,
    "boundarySearchSeconds": 8,
    "glossary": ["Burst Radio", "Burst", "Backseat Aux"],
    "verbatimPrompt": "Um, so, er, welcome back to Burst Radio. Uh, hmm, erm, right, here's the next one.",
    "maxNoSpeechProb": 0.6,
    "minAvgLogProb": -1.0,
    "hallucinations": ["thank you for watching", "thanks for watching", "subtitles by the amara org community", "please subscribe", "you"],
    "maxRepeats": 3
  },
  "enrichment": { "promptDir": "prompts", "promptName": "enrich-v1", "numCtx": 32768, "maxTranscriptChars": 60000, "seed": 42, "temperature": 0.1, "maxItems": 40 }
}
```

`config/analysis/swearwords.json`:
```json
{
  "version": "2026-10-16.1",
  "suffixes": ["s", "es", "ed", "er", "ers", "in", "ing", "y"],
  "tiers": {
    "mild": ["arse", "arsey", "bloody", "bugger", "crap", "crappy", "damn", "piss", "sod"],
    "strong": ["arsehole", "asshole", "bastard", "bellend", "bitch", "bollocks", "bullshit", "dick", "dickhead", "knob", "knobhead", "prick", "shit", "shite", "shitty", "twat", "wanker"],
    "severe": ["cunt", "fuck", "motherfucker"]
  },
  "allow": ["dicker", "dicky", "knobs", "knobby", "pricked", "pricker", "pricking", "pricky", "sods"]
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/AnalysisConfigTests.cs`:
```csharp
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Tests;

public sealed class AnalysisConfigTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "waa-acfg-" + Guid.NewGuid().ToString("N"));

    public AnalysisConfigTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static string CommittedConfig => Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json");

    string WriteConfig(string json, bool withSwears = true)
    {
        if (withSwears) File.Copy(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "swearwords.json"), Path.Combine(_dir, "swearwords.json"), overwrite: true);
        var path = Path.Combine(_dir, "analysis.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void The_committed_config_loads()
    {
        var loaded = AnalysisConfigLoader.Load(CommittedConfig);

        Assert.Equal(0.99, loaded.Config.Clipping.Threshold);
        Assert.Equal(-50, loaded.Config.DeadAir.ThresholdDbfs);
        Assert.Equal("21:00", loaded.Config.Swearing.WatershedStart);
        Assert.DoesNotContain("like", loaded.Config.Fillers.Words);
        Assert.Equal(300, loaded.Config.Transcription.ChunkSeconds);
        Assert.StartsWith("2026-10-16.1|", loaded.DetectorVersion);
        Assert.EndsWith("|code" + AnalysisConfigLoader.DetectorCodeVersion, loaded.DetectorVersion);
    }

    [Fact]
    public void The_committed_swear_list_has_three_tiers_suffixes_and_an_allow_list()
    {
        var list = AnalysisConfigLoader.Load(CommittedConfig).Swears;

        Assert.Equal(SwearTiers.Severe, list.Forms["fucking"]);
        Assert.Equal(SwearTiers.Severe, list.Forms["motherfuckers"]);
        Assert.Equal(SwearTiers.Strong, list.Forms["bitches"]);
        Assert.Equal(SwearTiers.Mild, list.Forms["pissed"]);
        Assert.False(list.Forms.ContainsKey("pricked"));
        Assert.False(list.Forms.ContainsKey("knobs"));
        foreach (var tier in SwearTiers.All) Assert.Contains(tier, list.Forms.Values);
    }

    [Fact]
    public void Unknown_field_is_rejected_by_name()
    {
        var path = WriteConfig("""{ "version": "x", "clipping": { "threshhold": 0.99 } }""");
        var ex = Assert.Throws<AnalysisConfigException>(() => AnalysisConfigLoader.Load(path));
        Assert.Contains("threshhold", ex.Message);
    }

    [Fact]
    public void Missing_version_is_rejected()
    {
        var path = WriteConfig("""{ }""");
        Assert.Contains("version", Assert.Throws<AnalysisConfigException>(() => AnalysisConfigLoader.Load(path)).Message);
    }

    [Theory]
    [InlineData("""{ "version": "x", "clipping": { "threshold": 0.97 } }""", "clipping.threshold")]
    [InlineData("""{ "version": "x", "clipping": { "minRunSamples": 1 } }""", "clipping.minRunSamples")]
    [InlineData("""{ "version": "x", "deadAir": { "thresholdDbfs": 3 } }""", "deadAir.thresholdDbfs")]
    [InlineData("""{ "version": "x", "deadAir": { "minSeconds": 10, "severeSeconds": 5 } }""", "deadAir.severeSeconds")]
    [InlineData("""{ "version": "x", "swearing": { "watershedStart": "9pm" } }""", "swearing.watershedStart")]
    [InlineData("""{ "version": "x", "fillers": { "words": ["um", "like"] } }""", "too ambiguous")]
    [InlineData("""{ "version": "x", "transcription": { "chunkSeconds": 5 } }""", "transcription.chunkSeconds")]
    [InlineData("""{ "version": "x", "transcription": { "maxRepeats": 1 } }""", "transcription.maxRepeats")]
    [InlineData("""{ "version": "x", "enrichment": { "numCtx": 100 } }""", "enrichment.numCtx")]
    public void Bad_values_are_refused_by_field(string json, string expected)
    {
        var path = WriteConfig(json);
        Assert.Contains(expected, Assert.Throws<AnalysisConfigException>(() => AnalysisConfigLoader.Load(path)).Message);
    }

    [Fact]
    public void Missing_swear_list_is_named()
    {
        var path = WriteConfig("""{ "version": "x" }""", withSwears: false);
        Assert.Contains("swearwords.json", Assert.Throws<AnalysisConfigException>(() => AnalysisConfigLoader.Load(path)).Message);
    }

    [Theory]
    [InlineData("""{ "version": "v", "suffixes": [], "tiers": { "rude": ["x"] }, "allow": [] }""", "unknown tier 'rude'")]
    [InlineData("""{ "version": "v", "suffixes": [], "tiers": { "mild": ["damn"], "strong": ["damn"] }, "allow": [] }""", "'damn' is in more than one tier")]
    [InlineData("""{ "version": "v", "suffixes": [], "tiers": { "mild": ["Damn!"] }, "allow": [] }""", "lower-case letters")]
    [InlineData("""{ "suffixes": [], "tiers": { "mild": ["damn"] }, "allow": [] }""", "version")]
    public void Bad_swear_lists_are_refused(string json, string expected)
    {
        Assert.Contains(expected, Assert.Throws<AnalysisConfigException>(() => SwearList.Parse(json, "test.json")).Message);
    }

    [Fact]
    public void Changing_the_config_changes_the_detector_version()
    {
        var a = AnalysisConfigLoader.Load(WriteConfig("""{ "version": "x" }""")).DetectorVersion;
        var b = AnalysisConfigLoader.Load(WriteConfig("""{ "version": "x", "deadAir": { "minSeconds": 6 } }""")).DetectorVersion;
        Assert.NotEqual(a, b);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AnalysisConfigTests`
Expected: build FAILS with `CS0246` for `AnalysisConfigLoader` and `SwearList`.

- [ ] **Step 4: Implement the clip levels, the config loader and the swear list**

`src/WebAudioArchive.Analysis/Audio/ClipLevels.cs`:
```csharp
namespace WebAudioArchive.Analysis.Audio;

/// <summary>
/// The fixed levels (fractions of full scale) at which clipped-sample runs are kept in features.json, so the clipping
/// threshold can be retuned without decoding again. A threshold that is not one of these needs a new features format.
/// </summary>
public static class ClipLevels
{
    public static readonly double[] Levels = [0.98, 0.99, 0.999];

    /// <summary>Shorter runs are not kept: one sample at full scale is normal for loud material.</summary>
    public const int MinStoredRun = 2;

    /// <summary>Per level, per segment; beyond this the level is marked truncated (very loud or distorted audio).</summary>
    public const int MaxRunsPerLevel = 50_000;

    public static int IndexOf(double level)
    {
        for (var i = 0; i < Levels.Length; i++)
            if (Math.Abs(Levels[i] - level) < 1e-9) return i;
        return -1;
    }
}
```

`src/WebAudioArchive.Analysis/Settings/AnalysisConfig.cs`:
```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Detectors;

namespace WebAudioArchive.Analysis.Settings;

public sealed record ClippingOptions
{
    /// <summary>A sample at or above this fraction of full scale (either sign) counts as clipped. Must be in ClipLevels.</summary>
    public double Threshold { get; init; } = 0.99;
    public int MinRunSamples { get; init; } = 3;
    public int MergeWithinMs { get; init; } = 250;
    public int MediumMs { get; init; } = 100;
}

public sealed record DeadAirOptions
{
    public double ThresholdDbfs { get; init; } = -50;
    public double MinSeconds { get; init; } = 5;
    public double SevereSeconds { get; init; } = 30;
}

public sealed record SwearOptions
{
    public string WordList { get; init; } = "swearwords.json";

    /// <summary>Station local time the watershed starts (after this, strong language is less serious).</summary>
    public string WatershedStart { get; init; } = "21:00";

    /// <summary>Station local time the watershed ends (from this until WatershedStart is "before the watershed").</summary>
    public string WatershedEnd { get; init; } = "05:30";
}

public sealed record FillerOptions
{
    public IReadOnlyList<string> Words { get; init; } = ["um", "uh", "er", "erm", "ah", "hmm"];
    public bool CountYouKnow { get; init; }
}

public sealed record TranscriptionOptions
{
    public string Language { get; init; } = "en";
    public double ChunkSeconds { get; init; } = 300;
    public double BoundarySearchSeconds { get; init; } = 8;
    public IReadOnlyList<string> Glossary { get; init; } = [];
    public string VerbatimPrompt { get; init; } = "Um, so, er, welcome back. Uh, hmm, erm, right.";
    public double MaxNoSpeechProb { get; init; } = 0.6;
    public double MinAvgLogProb { get; init; } = -1.0;
    public IReadOnlyList<string> Hallucinations { get; init; } = [];
    public int MaxRepeats { get; init; } = 3;

    /// <summary>The options that change what whisper is asked; a change re-transcribes. Filter options do not.</summary>
    public string TranscriptKey() =>
        AnalysisConfigLoader.Hash(string.Join('|', Language, ChunkSeconds.ToString(CultureInfo.InvariantCulture),
            BoundarySearchSeconds.ToString(CultureInfo.InvariantCulture), string.Join(',', Glossary), VerbatimPrompt));
}

public sealed record EnrichmentOptions
{
    public string PromptDir { get; init; } = "prompts";
    public string PromptName { get; init; } = "enrich-v1";
    public int NumCtx { get; init; } = 32768;
    public int MaxTranscriptChars { get; init; } = 60000;
    public int Seed { get; init; } = 42;
    public double Temperature { get; init; } = 0.1;
    public int MaxItems { get; init; } = 40;
}

public sealed record AnalysisConfig
{
    public required string Version { get; init; }
    public ClippingOptions Clipping { get; init; } = new();
    public DeadAirOptions DeadAir { get; init; } = new();
    public SwearOptions Swearing { get; init; } = new();
    public FillerOptions Fillers { get; init; } = new();
    public TranscriptionOptions Transcription { get; init; } = new();
    public EnrichmentOptions Enrichment { get; init; } = new();
}

/// <summary>The config, its swear list, where it was read from, and the version stamped on every event it produces.</summary>
public sealed record LoadedConfig(AnalysisConfig Config, SwearList Swears, string ConfigDir, string DetectorVersion);

public sealed class AnalysisConfigException(IReadOnlyList<string> errors)
    : Exception("Invalid analysis configuration: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static partial class AnalysisConfigLoader
{
    /// <summary>Bump when detector code changes what it reports, so stored events are rebuilt from transcripts and features.</summary>
    public const string DetectorCodeVersion = "1";

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static LoadedConfig Load(string path)
    {
        if (!File.Exists(path)) throw new AnalysisConfigException([$"{path}: file not found"]);
        var json = File.ReadAllText(path);
        AnalysisConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<AnalysisConfig>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new AnalysisConfigException([$"{path}: {ex.Message}"]);
        }

        if (config is null) throw new AnalysisConfigException([$"{path}: empty configuration"]);
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var errors = Validate(config, path);

        SwearList? swears = null;
        var swearPath = Path.Combine(dir, config.Swearing.WordList);
        var swearJson = "";
        if (!File.Exists(swearPath))
        {
            errors.Add($"{path}: swearing.wordList '{config.Swearing.WordList}' was not found beside the config");
        }
        else
        {
            swearJson = File.ReadAllText(swearPath);
            try
            {
                swears = SwearList.Parse(swearJson, swearPath);
            }
            catch (AnalysisConfigException ex)
            {
                errors.AddRange(ex.Errors);
            }
        }

        if (errors.Count > 0 || swears is null) throw new AnalysisConfigException(errors);
        var version = $"{config.Version}|{Hash(json)}|{swears.Version}|{Hash(swearJson)}|code{DetectorCodeVersion}";
        return new LoadedConfig(config, swears, dir, version);
    }

    internal static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();

    static List<string> Validate(AnalysisConfig c, string path)
    {
        var e = new List<string>();
        void Check(bool ok, string message)
        {
            if (!ok) e.Add($"{path}: {message}");
        }

        Check(!string.IsNullOrWhiteSpace(c.Version), "version must not be empty");

        Check(ClipLevels.IndexOf(c.Clipping.Threshold) >= 0,
            $"clipping.threshold {c.Clipping.Threshold.ToString(CultureInfo.InvariantCulture)} must be one of {string.Join(", ", ClipLevels.Levels.Select(l => l.ToString(CultureInfo.InvariantCulture)))} (the levels kept in features.json)");
        Check(c.Clipping.MinRunSamples >= ClipLevels.MinStoredRun && c.Clipping.MinRunSamples <= 10_000, $"clipping.minRunSamples must be from {ClipLevels.MinStoredRun} to 10000");
        Check(c.Clipping.MergeWithinMs is >= 0 and <= 10_000, "clipping.mergeWithinMs must be from 0 to 10000");
        Check(c.Clipping.MediumMs > 0, "clipping.mediumMs must be above 0");

        Check(c.DeadAir.ThresholdDbfs is >= -120 and <= -10, "deadAir.thresholdDbfs must be from -120 to -10");
        Check(c.DeadAir.MinSeconds is > 0 and <= 600, "deadAir.minSeconds must be above 0 and at most 600");
        Check(c.DeadAir.SevereSeconds >= c.DeadAir.MinSeconds, "deadAir.severeSeconds must be at least deadAir.minSeconds");

        Check(IsClock(c.Swearing.WatershedStart), "swearing.watershedStart must be HH:mm");
        Check(IsClock(c.Swearing.WatershedEnd), "swearing.watershedEnd must be HH:mm");

        Check(c.Fillers.Words.Count > 0, "fillers.words must not be empty");
        foreach (var w in c.Fillers.Words)
        {
            Check(FillerWord().IsMatch(w), $"fillers.words '{w}' must be lower-case letters");
            Check(w != "like", "fillers.words must not contain 'like': it is too ambiguous to count (spec 3.6)");
        }

        var t = c.Transcription;
        Check(LanguageCode().IsMatch(t.Language), "transcription.language must be a 2 or 3 letter code such as en");
        Check(t.ChunkSeconds is >= 30 and <= 1800, "transcription.chunkSeconds must be from 30 to 1800");
        Check(t.BoundarySearchSeconds >= 0 && t.BoundarySearchSeconds <= t.ChunkSeconds / 4, "transcription.boundarySearchSeconds must be from 0 to a quarter of chunkSeconds");
        Check(t.MaxNoSpeechProb is >= 0 and <= 1, "transcription.maxNoSpeechProb must be from 0 to 1");
        Check(t.MaxRepeats >= 2, "transcription.maxRepeats must be at least 2");

        var n = c.Enrichment;
        Check(n.NumCtx is >= 2048 and <= 262_144, "enrichment.numCtx must be from 2048 to 262144");
        Check(n.MaxTranscriptChars is >= 1000 and <= 1_000_000, "enrichment.maxTranscriptChars must be from 1000 to 1000000");
        Check(n.Temperature is >= 0 and <= 2, "enrichment.temperature must be from 0 to 2");
        Check(n.MaxItems is >= 1 and <= 200, "enrichment.maxItems must be from 1 to 200");
        return e;
    }

    static bool IsClock(string s) => TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    [GeneratedRegex("^[a-z]+( [a-z]+)?$")]
    private static partial Regex FillerWord();

    [GeneratedRegex("^[a-z]{2,3}$")]
    private static partial Regex LanguageCode();
}
```

`src/WebAudioArchive.Analysis/Detectors/SwearList.cs`:
```csharp
using System.Text.Json;
using System.Text.RegularExpressions;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Detectors;

public static class SwearTiers
{
    public const string Mild = "mild";
    public const string Strong = "strong";
    public const string Severe = "severe";

    public static readonly string[] All = [Mild, Strong, Severe];

    public static int Rank(string tier) => tier switch { Mild => 1, Strong => 2, Severe => 3, _ => 0 };
}

/// <summary>
/// The tiered word list (config/analysis/swearwords.json): each entry and each entry plus a listed suffix is a form
/// that matches a whole transcript word; forms on the allow list never match. Matching is added in Task 7.
/// </summary>
public sealed partial class SwearList
{
    sealed record ListFile(string? Version, List<string>? Suffixes, Dictionary<string, List<string>>? Tiers, List<string>? Allow);

    SwearList(string version, IReadOnlyDictionary<string, string> forms, IReadOnlySet<string> allow, IReadOnlyList<string> suffixes)
    {
        Version = version;
        Forms = forms;
        Allow = allow;
        Suffixes = suffixes;
    }

    public string Version { get; }

    /// <summary>Every matchable form (lower case) and its tier.</summary>
    public IReadOnlyDictionary<string, string> Forms { get; }

    public IReadOnlySet<string> Allow { get; }

    public IReadOnlyList<string> Suffixes { get; }

    public static SwearList Load(string path) => Parse(File.ReadAllText(path), path);

    public static SwearList Parse(string json, string source)
    {
        ListFile? file;
        try
        {
            file = JsonSerializer.Deserialize<ListFile>(json, AnalysisConfigLoader.Options);
        }
        catch (JsonException ex)
        {
            throw new AnalysisConfigException([$"{source}: {ex.Message}"]);
        }

        var errors = new List<string>();
        if (file is null || string.IsNullOrWhiteSpace(file.Version)) errors.Add($"{source}: version is required");
        var suffixes = file?.Suffixes ?? [];
        var allow = new HashSet<string>(file?.Allow ?? [], StringComparer.Ordinal);
        foreach (var s in suffixes.Concat(allow))
            if (!Letters().IsMatch(s)) errors.Add($"{source}: '{s}' must be lower-case letters only");

        var forms = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (tier, words) in file?.Tiers ?? new Dictionary<string, List<string>>())
        {
            if (SwearTiers.Rank(tier) == 0)
            {
                errors.Add($"{source}: unknown tier '{tier}' (use mild, strong or severe)");
                continue;
            }

            foreach (var w in words)
            {
                if (!Letters().IsMatch(w))
                {
                    errors.Add($"{source}: '{w}' in tier {tier} must be lower-case letters only");
                    continue;
                }

                if (seen.TryGetValue(w, out var other) && other != tier)
                {
                    errors.Add($"{source}: '{w}' is in more than one tier ({other} and {tier})");
                    continue;
                }

                seen[w] = tier;
                foreach (var form in suffixes.Select(s => w + s).Prepend(w))
                    if (!forms.TryGetValue(form, out var existing) || SwearTiers.Rank(existing) < SwearTiers.Rank(tier))
                        forms[form] = tier;
            }
        }

        if (forms.Count == 0) errors.Add($"{source}: tiers must list at least one word");
        if (errors.Count > 0) throw new AnalysisConfigException(errors);
        foreach (var a in allow) forms.Remove(a);
        return new SwearList(file!.Version!, forms, allow, suffixes);
    }

    [GeneratedRegex("^[a-z]+$")]
    private static partial Regex Letters();
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AnalysisConfigTests`
Expected: PASS, 19 tests. If `Missing_version_is_rejected` reports a different message, check that `Version` is `required` (System.Text.Json then reports "missing required properties including: 'version'").

- [ ] **Step 6: Commit**

```powershell
git add config/analysis/analysis.json config/analysis/swearwords.json src/WebAudioArchive.Analysis/Settings/AnalysisConfig.cs src/WebAudioArchive.Analysis/Audio/ClipLevels.cs src/WebAudioArchive.Analysis/Detectors/SwearList.cs tests/WebAudioArchive.Tests/AnalysisConfigTests.cs
git commit -m "Analysis: versioned detector config and tiered swear list with validation (#78, #79, #80, #81)"
```

---

### Task 3: File contracts in Core (transcript, events, timeline, paths)

These records are the contract between the analysis service and every reader (web app, reports, later plans), as the sidecar is between the recorder and its readers (spec 3.13). Schema changes are additive with a `schema` number.

**Files:**
- Create: `src/WebAudioArchive.Core/Analysis/AnalysisJson.cs`, `AnalysisPaths.cs`, `SegmentTimeline.cs`, `TranscriptDocument.cs`, `AnalysisEvent.cs`, `RecorderPreference.cs`, `TranscriptSlicer.cs` (all under `src/WebAudioArchive.Core/Analysis/`)
- Test: `tests/WebAudioArchive.Tests/AnalysisContractsTests.cs`

**Interfaces:**
- Consumes: `SegmentMeta`, `SegmentGap` (Core, plan 1).
- Produces (namespace `WebAudioArchive.Core.Analysis`):
  - `static class AnalysisJson { JsonSerializerOptions Options; void Write<T>(string path, T value); T? Read<T>(string path) where T : class; T? TryRead<T>(string path, out string? error) where T : class; }`
  - `sealed class AnalysisPaths(string root, string station)` with constants `TranscriptFile`, `EventsFile`, `FeaturesFile`, `StateFile`, `PartialFile`, `ShowAnalysisFile`, `EnrichmentFile` and `string SegmentDir(string jobId)`, `string SegmentsDir`, `string DayEvents(DateOnly utcDate)`, `string EventsDir`, `string ShowDir(string showId, DateTimeOffset startUtc)`, `string ShowsDir`.
  - `sealed record TimelineAnchor(double AudioOffset, DateTimeOffset Utc)`; `static class SegmentTimeline { IReadOnlyList<TimelineAnchor> Anchors(SegmentMeta); DateTimeOffset ToUtc(IReadOnlyList<TimelineAnchor>, double); double ToOffset(IReadOnlyList<TimelineAnchor>, DateTimeOffset); IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> Pieces(IReadOnlyList<TimelineAnchor>, double audioSeconds); }`
  - `sealed record TranscriptWord(string Text, double Start, double End, double Probability)`; `sealed record TranscriptSegment(double Start, double End, string Text, double NoSpeechProb, double AvgLogProb, string? Suppressed, IReadOnlyList<TranscriptWord> Words)`; `sealed record TranscriptDocument(int Schema, string Station, string Recorder, string Instance, string JobId, string SourceFile, DateTimeOffset SegmentStartUtc, DateTimeOffset SegmentEndUtc, double AudioSeconds, IReadOnlyList<TimelineAnchor> Timeline, string Engine, string Model, string Language, string TranscriptVersion, string Decoder, DateTimeOffset CreatedUtc, double TranscribeSeconds, IReadOnlyList<TranscriptSegment> Segments)` with `const int CurrentSchema = 1`, `ToUtc(double)`, `AudioFromUtc`, `AudioToUtc`.
  - `static class EventTypes { Swear = "swear", Clipping = "clipping", Filler = "filler", DeadAir = "dead-air", Song = "song" }`, `static class Severities { Info, Low, Medium, High }`, `static class EventSources { Audio, Transcript, Llm }`.
  - `sealed record AnalysisEvent(string Id, string Type, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Severity, double Confidence, string Detector, string DetectorVersion, string Source, string Recorder, string JobId, double AudioOffset, double AudioSeconds, string? Label, IReadOnlyDictionary<string, string>? Evidence)` with `string Link` and `static string MakeId(...)`.
  - `sealed record SourceSpan(string Recorder, DateTimeOffset FromUtc, DateTimeOffset ToUtc)`; `sealed record EventsDocument(int Schema, string Station, string Scope, DateTimeOffset FromUtc, DateTimeOffset ToUtc, DateTimeOffset BuiltUtc, IReadOnlyDictionary<string, string> Versions, IReadOnlyList<SourceSpan> Coverage, IReadOnlyDictionary<string, double>? Stats, IReadOnlyList<AnalysisEvent> Events)` with `const int CurrentSchema = 1`.
  - `static class RecorderPreference { int Rank(string recorder, IReadOnlyList<string> preference); bool Keep(string recorder, DateTimeOffset at, IEnumerable<SourceSpan> spans, IReadOnlyList<string> preference); }`
  - `sealed record TimedWord(string Text, DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Probability)`; `sealed record TimedLine(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Text, string Recorder, string JobId, IReadOnlyList<TimedWord> Words)`; `static class TranscriptSlicer { IReadOnlyList<TimedLine> Slice(IEnumerable<TranscriptDocument> docs, DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<string> preference); IReadOnlyList<SourceSpan> Coverage(TranscriptDocument doc); }`

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/AnalysisContractsTests.cs`:
```csharp
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests;

public sealed class AnalysisContractsTests : IDisposable
{
    static readonly DateTimeOffset T = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "waa-contract-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static SegmentMeta Meta(double wallSeconds, double audioSeconds, params SegmentGap[] gaps) => new(
        1, "home-a", "burst", "080000.mp3", T, T.AddSeconds(wallSeconds), 1000, 100, audioSeconds,
        new SegmentFormat("MPEG-1 Layer III", 128, 44100, 2), null, [], gaps, "hour-boundary");

    [Fact]
    public void Timeline_without_gaps_puts_the_connect_burst_before_the_start()
    {
        var anchors = SegmentTimeline.Anchors(Meta(3600, 3605));

        var only = Assert.Single(anchors);
        Assert.Equal(0, only.AudioOffset);
        Assert.Equal(T.AddSeconds(-5), only.Utc);
        Assert.Equal(T, SegmentTimeline.ToUtc(anchors, 5));
        Assert.Equal(105, SegmentTimeline.ToOffset(anchors, T.AddSeconds(100)), 6);
    }

    [Fact]
    public void Timeline_with_a_gap_jumps_over_it()
    {
        // 10 s gap at 1000 s; 4 s of connect burst shared by the two connections (2 s each).
        var anchors = SegmentTimeline.Anchors(Meta(3600, 3594, new SegmentGap(T.AddSeconds(1000), T.AddSeconds(1010), 10, "stream ended by server")));

        Assert.Equal(2, anchors.Count);
        Assert.Equal(new TimelineAnchor(0, T.AddSeconds(-2)), anchors[0]);
        Assert.Equal(new TimelineAnchor(1002, T.AddSeconds(1008)), anchors[1]);
        Assert.Equal(T.AddSeconds(999), SegmentTimeline.ToUtc(anchors, 1001));
        Assert.Equal(T.AddSeconds(1009), SegmentTimeline.ToUtc(anchors, 1003));
        Assert.Equal(1002, SegmentTimeline.ToOffset(anchors, T.AddSeconds(1005)), 6);

        var pieces = SegmentTimeline.Pieces(anchors, 3594);
        Assert.Equal((T.AddSeconds(-2), T.AddSeconds(1000)), pieces[0]);
        Assert.Equal((T.AddSeconds(1008), T.AddSeconds(3600)), pieces[1]);
    }

    [Fact]
    public void Gaps_outside_the_span_are_ignored_and_short_audio_has_no_burst()
    {
        var anchors = SegmentTimeline.Anchors(Meta(600, 590, new SegmentGap(T.AddSeconds(-30), T.AddSeconds(-20), 10, "before")));
        Assert.Equal(new TimelineAnchor(0, T), Assert.Single(anchors));
    }

    [Fact]
    public void Json_is_written_atomically_in_utc_and_reads_back()
    {
        var path = Path.Combine(_dir, "x", AnalysisPaths.TranscriptFile);
        var doc = new TranscriptDocument(TranscriptDocument.CurrentSchema, "burst", "a", "home-a", "a/2026/10-16/080000", "080000.mp3",
            T, T.AddHours(1), 3600, [new TimelineAnchor(0, T)], "whisper.net 1.9.1 (cuda)", "ggml-large-v3-turbo-q8_0", "en", "tv", "NLayer 3.0.0 frame decoder, NAudio.Core 3.1.0 WDL sinc resampler",
            T.AddHours(1), 120,
            [new TranscriptSegment(1, 2, "Hello there.", 0.01, -0.2, null, [new TranscriptWord("Hello", 1, 1.4, 0.9), new TranscriptWord("there.", 1.5, 1.9, 0.8)])]);

        AnalysisJson.Write(path, doc);
        var text = File.ReadAllText(path);
        var back = AnalysisJson.Read<TranscriptDocument>(path)!;

        Assert.Contains("\"segmentStartUtc\": \"2026-10-16T08:00:00+00:00\"", text);
        Assert.DoesNotContain("suppressed", text);
        Assert.DoesNotContain("audioFromUtc", text);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp"));
        Assert.Equal("there.", back.Segments[0].Words[1].Text);
        Assert.Equal(T.AddSeconds(1.5), back.ToUtc(1.5));
    }

    [Fact]
    public void Unreadable_json_is_reported_not_thrown()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "bad.json");
        File.WriteAllText(path, "{ not json");
        Assert.Null(AnalysisJson.TryRead<TranscriptDocument>(path, out var error));
        Assert.NotNull(error);
    }

    [Fact]
    public void Event_ids_are_stable_and_links_open_the_player_with_padding()
    {
        var a = AnalysisEvent.MakeId(EventTypes.Swear, "a/2026/10-16/080000", 12.3456, "shit");
        Assert.Equal(a, AnalysisEvent.MakeId(EventTypes.Swear, "a/2026/10-16/080000", 12.3456, "shit"));
        Assert.NotEqual(a, AnalysisEvent.MakeId(EventTypes.Swear, "a/2026/10-16/080000", 12.5, "shit"));
        Assert.Equal(16, a.Length);

        var e = new AnalysisEvent(a, EventTypes.Swear, T.AddSeconds(10), T.AddSeconds(10.4), Severities.High, 0.9, "swear", "1",
            EventSources.Transcript, "a", "a/2026/10-16/080000", 15, 0.4, "shit", null);
        Assert.Equal("/player?from=2026-10-16T08:00:05Z&to=2026-10-16T08:00:16Z&recorder=a", e.Link);
    }

    [Fact]
    public void Paths_follow_the_documented_layout()
    {
        var p = new AnalysisPaths("/analysis", "burst");
        Assert.Equal(Path.Combine("/analysis", "burst", "segments", "a", "2026", "10-16", "080000"), p.SegmentDir("a/2026/10-16/080000"));
        Assert.Equal(Path.Combine("/analysis", "burst", "events", "2026", "10-16.json"), p.DayEvents(new DateOnly(2026, 10, 16)));
        Assert.Equal(Path.Combine("/analysis", "burst", "shows", "2026", "burst-20261016T1900Z"), p.ShowDir("burst-20261016T1900Z", T.AddHours(11)));
        Assert.Throws<ArgumentException>(() => p.SegmentDir("a/../../etc"));
        Assert.Throws<ArgumentException>(() => p.SegmentDir("a\\2026"));
    }

    [Fact]
    public void A_preferred_recorder_hides_the_other_where_it_has_audio()
    {
        string[] pref = ["a", "b"];
        SourceSpan[] spans = [new("a", T, T.AddHours(1)), new("b", T.AddMinutes(30), T.AddMinutes(90))];

        Assert.False(RecorderPreference.Keep("b", T.AddMinutes(45), spans, pref));
        Assert.True(RecorderPreference.Keep("b", T.AddMinutes(60), spans, pref));
        Assert.True(RecorderPreference.Keep("a", T.AddMinutes(45), spans, pref));
        Assert.Equal(2, RecorderPreference.Rank("c", pref));
    }

    [Fact]
    public void Slices_take_a_where_it_has_audio_b_elsewhere_and_skip_suppressed_lines()
    {
        TranscriptDocument Doc(string recorder, DateTimeOffset start, params (double At, string Text, string? Suppressed)[] lines) => new(
            1, "burst", recorder, "home-" + recorder, $"{recorder}/2026/10-16/{start:HHmmss}", $"{start:HHmmss}.mp3", start, start.AddHours(1), 3600,
            [new TimelineAnchor(0, start)], "fake", "fake", "en", "tv", "decoder", start, 1,
            lines.Select(l => new TranscriptSegment(l.At, l.At + 2, l.Text, 0, 0, l.Suppressed, [new TranscriptWord(l.Text, l.At, l.At + 1, 1)])).ToList());

        var a = Doc("a", T, (60, "from a", null), (120, "music noise", "repetition"), (3500, "late a", null));
        var b = Doc("b", T.AddMinutes(30), (60, "b inside a", null), (1900, "b after a", null));

        var lines = TranscriptSlicer.Slice([a, b], T, T.AddHours(2), ["a", "b"]);

        Assert.Equal(new[] { "from a", "late a", "b after a" }, lines.Select(l => l.Text));
        Assert.Equal(T.AddSeconds(60), lines[0].StartUtc);
        Assert.Equal("b", lines[2].Recorder);
        Assert.Empty(TranscriptSlicer.Slice([a], T.AddHours(3), T.AddHours(4), ["a"]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AnalysisContractsTests`
Expected: build FAILS with `CS0234: The type or namespace name 'Analysis' does not exist in the namespace 'WebAudioArchive.Core'`.

- [ ] **Step 3: Implement the contracts**

`src/WebAudioArchive.Core/Analysis/AnalysisJson.cs`:
```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WebAudioArchive.Core.Analysis;

/// <summary>JSON for every analysis file: camelCase, UTC offsets as written, nulls left out, written atomically.</summary>
public static class AnalysisJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Written to a unique temporary file and renamed, so a reader never sees half a file.</summary>
    public static void Write<T>(string path, T value)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Options));
            File.Move(tmp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) : null;

    /// <summary>Null with a reason instead of an exception, for files that may be half written by an older version or damaged.</summary>
    public static T? TryRead<T>(string path, out string? error) where T : class
    {
        try
        {
            error = null;
            return Read<T>(path);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            error = ex.Message;
            return null;
        }
    }
}
```

`src/WebAudioArchive.Core/Analysis/AnalysisPaths.cs`:
```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Core.Analysis;

/// <summary>
/// The analysis output layout, under the analysis root (data/analysis on the host):
/// &lt;station&gt;/segments/&lt;recorder&gt;/&lt;yyyy&gt;/&lt;MM-dd&gt;/&lt;stem&gt;/ (state, features, transcript, events per master segment),
/// &lt;station&gt;/events/&lt;yyyy&gt;/&lt;MM-dd&gt;.json (the UTC day's merged timeline) and
/// &lt;station&gt;/shows/&lt;yyyy&gt;/&lt;show id&gt;/ (show-analysis.json, enrichment.json).
/// </summary>
public sealed partial class AnalysisPaths(string root, string station)
{
    public const string StateFile = "state.json";
    public const string FeaturesFile = "features.json";
    public const string PartialFile = "transcript.partial.json";
    public const string TranscriptFile = "transcript.json";
    public const string EventsFile = "events.json";
    public const string ShowAnalysisFile = "show-analysis.json";
    public const string EnrichmentFile = "enrichment.json";

    public string Root => root;

    public string Station => station;

    public string StationDir => Path.Combine(root, station);

    public string SegmentsDir => Path.Combine(StationDir, "segments");

    public string EventsDir => Path.Combine(StationDir, "events");

    public string ShowsDir => Path.Combine(StationDir, "shows");

    /// <summary>The folder for a job id such as a/2026/10-16/080000. Only plain path parts are accepted.</summary>
    public string SegmentDir(string jobId)
    {
        var parts = jobId.Split('/');
        if (parts.Length < 2 || parts.Any(p => !SafePart().IsMatch(p) || p is "." or ".."))
            throw new ArgumentException($"'{jobId}' is not a valid job id", nameof(jobId));
        return Path.Combine([SegmentsDir, .. parts]);
    }

    public string DayEvents(DateOnly utcDate) =>
        Path.Combine(EventsDir, utcDate.ToString("yyyy", CultureInfo.InvariantCulture), utcDate.ToString("MM-dd", CultureInfo.InvariantCulture) + ".json");

    public string ShowDir(string showId, DateTimeOffset startUtc)
    {
        if (!SafePart().IsMatch(showId)) throw new ArgumentException($"'{showId}' is not a valid show id", nameof(showId));
        return Path.Combine(ShowsDir, startUtc.UtcDateTime.ToString("yyyy", CultureInfo.InvariantCulture), showId);
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex SafePart();
}
```

`src/WebAudioArchive.Core/Analysis/SegmentTimeline.cs`:
```csharp
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Core.Analysis;

/// <summary>Audio at <see cref="AudioOffset"/> seconds into the segment file was broadcast at <see cref="Utc"/>; time advances 1:1 until the next anchor.</summary>
public sealed record TimelineAnchor(double AudioOffset, DateTimeOffset Utc);

/// <summary>
/// Maps seconds into a segment's audio to UTC and back, with the same model as the web app's position model: a segment
/// is one connection per stretch between its gaps; each connection starts with a short burst of audio from before its
/// first arrival (Revma sends about 5 s at connect) and then runs in real time. The measured surplus
/// (AudioSeconds minus the wall span less gaps) is shared equally between connections, so after a reconnect inside a
/// segment a time can be a few seconds out; otherwise it is good to about a second.
/// </summary>
public static class SegmentTimeline
{
    public static IReadOnlyList<TimelineAnchor> Anchors(SegmentMeta meta)
    {
        var start = meta.StartUtc;
        var end = meta.EndUtc < start ? start : meta.EndUtc;
        var gaps = (meta.Gaps ?? [])
            .Where(g => g.EndUtc > start && g.StartUtc < end)
            .OrderBy(g => g.StartUtc)
            .Select(g => (Start: g.StartUtc < start ? start : g.StartUtc, End: g.EndUtc > end ? end : g.EndUtc))
            .ToList();
        var gapSeconds = gaps.Sum(g => (g.End - g.Start).TotalSeconds);
        var burst = Math.Max(0, meta.AudioSeconds - ((end - start).TotalSeconds - gapSeconds));
        var lead = burst / (gaps.Count + 1);

        var anchors = new List<TimelineAnchor>(gaps.Count + 1);
        double offset = 0;
        var connectionStart = start;
        for (var i = 0; i <= gaps.Count; i++)
        {
            var connectionEnd = i < gaps.Count ? gaps[i].Start : end;
            if (connectionEnd < connectionStart) connectionEnd = connectionStart;
            anchors.Add(new TimelineAnchor(offset, connectionStart.AddSeconds(-lead)));
            offset += (connectionEnd - connectionStart).TotalSeconds + lead;
            if (i < gaps.Count && gaps[i].End > connectionStart) connectionStart = gaps[i].End;
        }

        return anchors;
    }

    public static DateTimeOffset ToUtc(IReadOnlyList<TimelineAnchor> anchors, double audioOffset)
    {
        var a = anchors[0];
        foreach (var c in anchors)
            if (c.AudioOffset <= audioOffset) a = c;
        return a.Utc.AddTicks((long)Math.Round((audioOffset - a.AudioOffset) * TimeSpan.TicksPerSecond));
    }

    /// <summary>Seconds into the audio for a UTC time; a time inside a gap maps to where the audio jumps over it.</summary>
    public static double ToOffset(IReadOnlyList<TimelineAnchor> anchors, DateTimeOffset utc)
    {
        var index = 0;
        for (var i = 0; i < anchors.Count; i++)
            if (anchors[i].Utc <= utc) index = i;
        var a = anchors[index];
        var offset = a.AudioOffset + (utc - a.Utc).TotalSeconds;
        if (index + 1 < anchors.Count) offset = Math.Min(offset, anchors[index + 1].AudioOffset);
        return Math.Max(0, offset);
    }

    /// <summary>The UTC stretches the audio covers, one per connection (gaps excluded).</summary>
    public static IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> Pieces(IReadOnlyList<TimelineAnchor> anchors, double audioSeconds)
    {
        var list = new List<(DateTimeOffset, DateTimeOffset)>(anchors.Count);
        for (var i = 0; i < anchors.Count; i++)
        {
            var endOffset = i + 1 < anchors.Count ? anchors[i + 1].AudioOffset : Math.Max(anchors[i].AudioOffset, audioSeconds);
            list.Add((anchors[i].Utc, anchors[i].Utc.AddSeconds(endOffset - anchors[i].AudioOffset)));
        }

        return list;
    }
}
```

`src/WebAudioArchive.Core/Analysis/TranscriptDocument.cs`:
```csharp
using System.Text.Json.Serialization;

namespace WebAudioArchive.Core.Analysis;

/// <summary>A word with its times in seconds from the start of the segment's audio (map to UTC with the document's timeline).</summary>
public sealed record TranscriptWord(string Text, double Start, double End, double Probability);

/// <summary>
/// One whisper segment. <see cref="Suppressed"/> is null for speech the detectors use, or the reason it is ignored
/// (non-speech, no-speech, known-hallucination, repetition). Suppressed segments are kept so a person can see them.
/// </summary>
public sealed record TranscriptSegment(
    double Start,
    double End,
    string Text,
    double NoSpeechProb,
    double AvgLogProb,
    string? Suppressed,
    IReadOnlyList<TranscriptWord> Words);

/// <summary>transcript.json: the transcript of one master segment. Times are offsets into its audio; the timeline maps them to UTC.</summary>
public sealed record TranscriptDocument(
    int Schema,
    string Station,
    string Recorder,
    string Instance,
    string JobId,
    string SourceFile,
    DateTimeOffset SegmentStartUtc,
    DateTimeOffset SegmentEndUtc,
    double AudioSeconds,
    IReadOnlyList<TimelineAnchor> Timeline,
    string Engine,
    string Model,
    string Language,
    string TranscriptVersion,
    string Decoder,
    DateTimeOffset CreatedUtc,
    double TranscribeSeconds,
    IReadOnlyList<TranscriptSegment> Segments)
{
    public const int CurrentSchema = 1;

    public DateTimeOffset ToUtc(double audioOffset) => SegmentTimeline.ToUtc(Timeline, audioOffset);

    [JsonIgnore]
    public DateTimeOffset AudioFromUtc => ToUtc(0);

    [JsonIgnore]
    public DateTimeOffset AudioToUtc => ToUtc(AudioSeconds);
}
```

`src/WebAudioArchive.Core/Analysis/AnalysisEvent.cs`:
```csharp
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace WebAudioArchive.Core.Analysis;

public static class EventTypes
{
    public const string Swear = "swear";
    public const string Clipping = "clipping";
    public const string Filler = "filler";
    public const string DeadAir = "dead-air";

    /// <summary>Reserved for song identification (spec 3.9); nothing writes it yet.</summary>
    public const string Song = "song";
}

public static class Severities
{
    public const string Info = "info";
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";
}

public static class EventSources
{
    public const string Audio = "audio";
    public const string Transcript = "transcript";
    public const string Llm = "llm";
}

/// <summary>
/// One detected event on the station timeline. Times are UTC; <see cref="AudioOffset"/> and <see cref="AudioSeconds"/>
/// locate it in the segment the job read. Detected, not proven: <see cref="Confidence"/> is the detector's own estimate.
/// </summary>
public sealed record AnalysisEvent(
    string Id,
    string Type,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Severity,
    double Confidence,
    string Detector,
    string DetectorVersion,
    string Source,
    string Recorder,
    string JobId,
    double AudioOffset,
    double AudioSeconds,
    string? Label,
    IReadOnlyDictionary<string, string>? Evidence)
{
    public const double LinkPaddingSeconds = 5;

    /// <summary>The web app's Player for this event with 5 s either side (a clip made on request from that recorder).</summary>
    public string Link
    {
        get
        {
            var from = StartUtc.AddSeconds(-LinkPaddingSeconds);
            var to = (EndUtc > StartUtc.AddSeconds(1) ? EndUtc : StartUtc.AddSeconds(1)).AddSeconds(LinkPaddingSeconds);
            return $"/player?from={Utc(from)}&to={Utc(to)}&recorder={Recorder}";
        }
    }

    /// <summary>Stable across rebuilds: the same detection on the same audio always gets the same id.</summary>
    public static string MakeId(string type, string jobId, double audioOffset, string? discriminator = null)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{type}|{jobId}|{Math.Round(audioOffset, 3):0.000}|{discriminator}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16].ToLowerInvariant();
    }

    static string Utc(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}

/// <summary>A UTC stretch of audio that a recorder's analysed segment covers.</summary>
public sealed record SourceSpan(string Recorder, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>
/// events.json. Scope is "segment:&lt;job id&gt;" for one master segment or "day:&lt;yyyy-MM-dd&gt;" for the merged UTC day.
/// Versions names the detector and transcript versions used; Coverage is the audio the events were looked for in.
/// Rebuildable at any time from transcript.json and features.json, without touching audio.
/// </summary>
public sealed record EventsDocument(
    int Schema,
    string Station,
    string Scope,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset BuiltUtc,
    IReadOnlyDictionary<string, string> Versions,
    IReadOnlyList<SourceSpan> Coverage,
    IReadOnlyDictionary<string, double>? Stats,
    IReadOnlyList<AnalysisEvent> Events)
{
    public const int CurrentSchema = 1;

    [JsonIgnore]
    public bool IsDay => Scope.StartsWith("day:", StringComparison.Ordinal);
}
```

`src/WebAudioArchive.Core/Analysis/RecorderPreference.cs`:
```csharp
namespace WebAudioArchive.Core.Analysis;

/// <summary>
/// The A-before-B rule shared by every reader: where a better-ranked recorder's analysed audio covers an instant, results
/// from a worse-ranked recorder at that instant are dropped. Recorders not in the preference list rank last.
/// </summary>
public static class RecorderPreference
{
    public static int Rank(string recorder, IReadOnlyList<string> preference)
    {
        for (var i = 0; i < preference.Count; i++)
            if (preference[i] == recorder) return i;
        return preference.Count;
    }

    public static bool Keep(string recorder, DateTimeOffset at, IEnumerable<SourceSpan> spans, IReadOnlyList<string> preference)
    {
        var rank = Rank(recorder, preference);
        foreach (var s in spans)
            if (Rank(s.Recorder, preference) < rank && s.FromUtc <= at && at < s.ToUtc) return false;
        return true;
    }
}
```

`src/WebAudioArchive.Core/Analysis/TranscriptSlicer.cs`:
```csharp
namespace WebAudioArchive.Core.Analysis;

public sealed record TimedWord(string Text, DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Probability);

/// <summary>One transcript line (a whisper segment) in UTC, with the recorder and job it came from.</summary>
public sealed record TimedLine(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Text, string Recorder, string JobId, IReadOnlyList<TimedWord> Words);

/// <summary>Builds the station transcript for any UTC range (a show with its margins, a search hit) from per-segment transcripts.</summary>
public static class TranscriptSlicer
{
    public static IReadOnlyList<SourceSpan> Coverage(TranscriptDocument doc) =>
        SegmentTimeline.Pieces(doc.Timeline, doc.AudioSeconds).Select(p => new SourceSpan(doc.Recorder, p.From, p.To)).ToList();

    /// <summary>Lines overlapping [fromUtc, toUtc), suppressed lines left out, preferred recorders first where they overlap.</summary>
    public static IReadOnlyList<TimedLine> Slice(IEnumerable<TranscriptDocument> docs, DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<string> preference)
    {
        var list = docs.ToList();
        var spans = list.SelectMany(Coverage).ToList();
        var lines = new List<TimedLine>();
        foreach (var d in list)
        {
            foreach (var s in d.Segments)
            {
                if (s.Suppressed is not null || string.IsNullOrWhiteSpace(s.Text)) continue;
                var start = d.ToUtc(s.Start);
                var end = d.ToUtc(Math.Max(s.Start, s.End));
                if (end <= fromUtc || start >= toUtc) continue;
                if (!RecorderPreference.Keep(d.Recorder, start, spans, preference)) continue;
                var words = s.Words
                    .Select(w => new TimedWord(w.Text, d.ToUtc(w.Start), d.ToUtc(Math.Max(w.Start, w.End)), w.Probability))
                    .ToList();
                lines.Add(new TimedLine(start, end, s.Text.Trim(), d.Recorder, d.JobId, words));
            }
        }

        return lines
            .OrderBy(l => l.StartUtc)
            .ThenBy(l => RecorderPreference.Rank(l.Recorder, preference))
            .ToList();
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AnalysisContractsTests`
Expected: PASS, 9 tests. If `Json_is_written_atomically...` fails on the date text, check that `segmentStartUtc` is serialised from a `DateTimeOffset` with offset zero (System.Text.Json writes `+00:00`, not `Z`; readers accept both).

- [ ] **Step 5: Commit**

```powershell
git add src/WebAudioArchive.Core/Analysis tests/WebAudioArchive.Tests/AnalysisContractsTests.cs
git commit -m "Core: analysis file contracts (transcript, events, timeline, paths, A before B) (#82)"
```

---

### Task 4: WAV files and synthetic test signals

**Files:**
- Create: `src/WebAudioArchive.Analysis/Audio/WavFile.cs`
- Create: `tests/WebAudioArchive.Tests/Support/Signals.cs`
- Test: `tests/WebAudioArchive.Tests/WavFileTests.cs`

**Interfaces:**
- Produces:
  - `sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, int FormatTag, long DataOffset, long DataLength)` with `BlockAlign`, `Frames`, `Seconds`.
  - `static class WavFile { WavInfo ReadInfo(string path); byte[] Slice(string path, WavInfo info, double startSeconds, double endSeconds); byte[] Build(int sampleRate, int channels, ReadOnlySpan<byte> pcm16); void WritePcm16(string path, int sampleRate, int channels, ReadOnlySpan<float> interleaved); short[] ReadPcm16(string path, out WavInfo info); }`
  - Test helper `Signals` (44.1 kHz unless a rate is given): `Sine`, `Silence`, `Noise(seconds, dbfs, seed)`, `SpeechLike(seconds, seed)`, `Concat`, `Clip(signal, atSeconds, samples, level)`, `Stereo(left, right)`, `Stereo(mono)`, `Dbfs(signal)`.

- [ ] **Step 1: Write the signal helper**

`tests/WebAudioArchive.Tests/Support/Signals.cs`:
```csharp
namespace WebAudioArchive.Tests.Support;

/// <summary>Synthetic audio for detector tests: tones, silence, noise at a level, speech-like bursts, planted clipping. Mono unless made stereo.</summary>
public static class Signals
{
    public const int Rate = 44100;

    public static float[] Sine(double seconds, double hz = 440, double amp = 0.5, int rate = Rate)
    {
        var n = (int)Math.Round(seconds * rate);
        var s = new float[n];
        for (var i = 0; i < n; i++) s[i] = (float)(amp * Math.Sin(2 * Math.PI * hz * i / rate));
        return s;
    }

    public static float[] Silence(double seconds, int rate = Rate) => new float[(int)Math.Round(seconds * rate)];

    /// <summary>Uniform noise whose RMS level is <paramref name="dbfs"/>.</summary>
    public static float[] Noise(double seconds, double dbfs, int seed, int rate = Rate)
    {
        var rng = new Random(seed);
        var amp = Math.Pow(10, dbfs / 20) * Math.Sqrt(3);
        var s = new float[(int)Math.Round(seconds * rate)];
        for (var i = 0; i < s.Length; i++) s[i] = (float)(amp * (rng.NextDouble() * 2 - 1));
        return s;
    }

    /// <summary>
    /// Syllable-like bursts (120 to 250 ms of noise under a smooth envelope, peak 0.3) separated by short pauses
    /// (50 to 500 ms of digital silence) and an occasional breath (0.8 to 1.5 s). Never a pause of 2 s or more.
    /// </summary>
    public static float[] SpeechLike(double seconds, int seed, int rate = Rate)
    {
        var rng = new Random(seed);
        var total = (int)Math.Round(seconds * rate);
        var s = new float[total];
        var i = 0;
        var syllables = 0;
        while (i < total)
        {
            var len = (int)(rate * (0.12 + rng.NextDouble() * 0.13));
            for (var k = 0; k < len && i + k < total; k++)
                s[i + k] = (float)(0.3 * Math.Sin(Math.PI * k / len) * (rng.NextDouble() * 2 - 1));
            i += len;
            var pause = ++syllables % 12 == 0 ? 0.8 + rng.NextDouble() * 0.7 : 0.05 + rng.NextDouble() * 0.45;
            i += (int)(rate * pause);
        }

        return s;
    }

    public static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>A copy with <paramref name="samples"/> consecutive samples set to <paramref name="level"/> from <paramref name="atSeconds"/>.</summary>
    public static float[] Clip(float[] signal, double atSeconds, int samples, float level = 1f, int rate = Rate)
    {
        var copy = (float[])signal.Clone();
        var start = (int)Math.Round(atSeconds * rate);
        for (var k = 0; k < samples; k++) copy[start + k] = level;
        return copy;
    }

    public static float[] Stereo(float[] left, float[] right)
    {
        var s = new float[left.Length * 2];
        for (var i = 0; i < left.Length; i++)
        {
            s[2 * i] = left[i];
            s[2 * i + 1] = right[i];
        }

        return s;
    }

    public static float[] Stereo(float[] mono) => Stereo(mono, mono);

    public static double Dbfs(float[] signal) => 10 * Math.Log10(signal.Average(x => (double)x * x));
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/WavFileTests.cs`:
```csharp
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class WavFileTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("waa-wav-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void Write_then_read_round_trips_16_bit_pcm()
    {
        var path = Path.Combine(_dir, "a.wav");
        var signal = Signals.Sine(1, rate: 16000);

        WavFile.WritePcm16(path, 16000, 1, signal);
        var samples = WavFile.ReadPcm16(path, out var info);

        Assert.Equal(16000, info.SampleRate);
        Assert.Equal(1, info.Channels);
        Assert.Equal(16, info.BitsPerSample);
        Assert.Equal(1.0, info.Seconds, 6);
        Assert.Equal(16000, samples.Length);
        Assert.Equal((short)Math.Round(signal[1000] * 32767), samples[1000]);
    }

    [Fact]
    public void Extra_chunks_before_data_are_skipped()
    {
        var path = Path.Combine(_dir, "list.wav");
        var plain = WavFile.Build(16000, 1, new byte[3200]);
        // Insert a LIST chunk (odd length, padded) after fmt, as some encoders write.
        var list = "LIST"u8.ToArray().Concat(BitConverter.GetBytes(5)).Concat("INFOx"u8.ToArray()).Concat(new byte[] { 0 }).ToArray();
        var withList = plain.Take(36).Concat(list).Concat(plain.Skip(36)).ToArray();
        BitConverter.GetBytes(withList.Length - 8).CopyTo(withList, 4);
        File.WriteAllBytes(path, withList);

        var info = WavFile.ReadInfo(path);

        Assert.Equal(36 + list.Length + 8, info.DataOffset);
        Assert.Equal(3200, info.DataLength);
    }

    [Fact]
    public void Slice_returns_a_valid_wav_of_the_range_and_clamps_to_the_data()
    {
        var path = Path.Combine(_dir, "b.wav");
        WavFile.WritePcm16(path, 16000, 1, Signals.Sine(10, rate: 16000));
        var info = WavFile.ReadInfo(path);

        var slice = WavFile.Slice(path, info, 2.0, 3.5);
        var slicePath = Path.Combine(_dir, "slice.wav");
        File.WriteAllBytes(slicePath, slice);
        var sliceInfo = WavFile.ReadInfo(slicePath);

        Assert.Equal(1.5, sliceInfo.Seconds, 6);
        Assert.Equal(1.0, WavFile.ReadInfo(WriteBytes(WavFile.Slice(path, info, 9.0, 99.0))).Seconds, 6);
        Assert.Equal(0.0, WavFile.ReadInfo(WriteBytes(WavFile.Slice(path, info, 12.0, 13.0))).Seconds, 6);
    }

    [Fact]
    public void Not_a_wav_is_refused()
    {
        var path = Path.Combine(_dir, "x.wav");
        File.WriteAllBytes(path, "ID3 not a wav file at all"u8.ToArray());
        Assert.Throws<InvalidDataException>(() => WavFile.ReadInfo(path));
    }

    [Fact]
    public void Signals_have_the_levels_they_claim()
    {
        Assert.Equal(-45, Signals.Dbfs(Signals.Noise(5, -45, 1)), 0);
        Assert.Equal(-9.03, Signals.Dbfs(Signals.Sine(1)), 1);
        var speech = Signals.SpeechLike(30, 7);
        Assert.Equal(30 * Signals.Rate, speech.Length);
    }

    string WriteBytes(byte[] bytes)
    {
        var p = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav");
        File.WriteAllBytes(p, bytes);
        return p;
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~WavFileTests`
Expected: build FAILS with `CS0246: The type or namespace name 'WavFile' could not be found`.

- [ ] **Step 4: Implement WAV reading, slicing and writing**

`src/WebAudioArchive.Analysis/Audio/WavFile.cs`:
```csharp
using System.Buffers.Binary;
using System.Text;

namespace WebAudioArchive.Analysis.Audio;

public sealed record WavInfo(int SampleRate, int Channels, int BitsPerSample, int FormatTag, long DataOffset, long DataLength)
{
    public int BlockAlign => Channels * BitsPerSample / 8;

    public long Frames => BlockAlign == 0 ? 0 : DataLength / BlockAlign;

    public double Seconds => SampleRate == 0 ? 0 : (double)Frames / SampleRate;
}

/// <summary>Minimal RIFF WAVE support for the 16 kHz mono PCM that whisper takes. Chunks other than fmt and data are skipped.</summary>
public static class WavFile
{
    public static WavInfo ReadInfo(string path)
    {
        using var fs = File.OpenRead(path);
        var header = new byte[12];
        if (fs.Read(header) != 12 || Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || Encoding.ASCII.GetString(header, 8, 4) != "WAVE")
            throw new InvalidDataException($"{path}: not a RIFF WAVE file");

        int? rate = null, channels = null, bits = null, tag = null;
        var chunk = new byte[8];
        while (fs.Position + 8 <= fs.Length)
        {
            fs.ReadExactly(chunk);
            var id = Encoding.ASCII.GetString(chunk, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
            var start = fs.Position;
            if (id == "fmt ")
            {
                var fmt = new byte[16];
                fs.ReadExactly(fmt);
                tag = BinaryPrimitives.ReadUInt16LittleEndian(fmt);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(2));
                rate = (int)BinaryPrimitives.ReadUInt32LittleEndian(fmt.AsSpan(4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(fmt.AsSpan(14));
            }
            else if (id == "data")
            {
                if (rate is null || channels is null || bits is null || tag is null)
                    throw new InvalidDataException($"{path}: data chunk before fmt chunk");
                var length = Math.Min(size, fs.Length - start);
                return new WavInfo(rate.Value, channels.Value, bits.Value, tag.Value, start, length);
            }

            fs.Position = start + size + (size % 2);
        }

        throw new InvalidDataException($"{path}: no data chunk");
    }

    /// <summary>A complete WAV (header and samples) for [startSeconds, endSeconds) of a 16-bit PCM file, clamped to its data.</summary>
    public static byte[] Slice(string path, WavInfo info, double startSeconds, double endSeconds)
    {
        if (info.FormatTag != 1 || info.BitsPerSample != 16)
            throw new InvalidDataException($"{path}: only 16-bit PCM WAV can be sliced (format {info.FormatTag}, {info.BitsPerSample} bits)");
        var first = Math.Clamp((long)Math.Round(startSeconds * info.SampleRate), 0, info.Frames);
        var last = Math.Clamp((long)Math.Round(endSeconds * info.SampleRate), first, info.Frames);
        var data = new byte[checked((int)((last - first) * info.BlockAlign))];
        using (var fs = File.OpenRead(path))
        {
            fs.Position = info.DataOffset + first * info.BlockAlign;
            fs.ReadExactly(data);
        }

        return Build(info.SampleRate, info.Channels, data);
    }

    public static byte[] Build(int sampleRate, int channels, ReadOnlySpan<byte> pcm16)
    {
        var bytes = new byte[44 + pcm16.Length];
        var s = bytes.AsSpan();
        "RIFF"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s[4..], (uint)(36 + pcm16.Length));
        "WAVEfmt "u8.CopyTo(s[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(s[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], (ushort)channels);
        BinaryPrimitives.WriteUInt32LittleEndian(s[24..], (uint)sampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(s[28..], (uint)(sampleRate * channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(s[32..], (ushort)(channels * 2));
        BinaryPrimitives.WriteUInt16LittleEndian(s[34..], 16);
        "data"u8.CopyTo(s[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(s[40..], (uint)pcm16.Length);
        pcm16.CopyTo(s[44..]);
        return bytes;
    }

    /// <summary>Writes interleaved float samples (clamped to -1..1) as 16-bit PCM. Used by tests and the benchmark.</summary>
    public static void WritePcm16(string path, int sampleRate, int channels, ReadOnlySpan<float> interleaved)
    {
        var pcm = new byte[interleaved.Length * 2];
        for (var i = 0; i < interleaved.Length; i++)
        {
            var v = (short)Math.Round(Math.Clamp(interleaved[i], -1f, 1f) * 32767);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), v);
        }

        File.WriteAllBytes(path, Build(sampleRate, channels, pcm));
    }

    public static short[] ReadPcm16(string path, out WavInfo info)
    {
        info = ReadInfo(path);
        if (info.FormatTag != 1 || info.BitsPerSample != 16) throw new InvalidDataException($"{path}: not 16-bit PCM");
        var data = new byte[checked((int)info.DataLength)];
        using (var fs = File.OpenRead(path))
        {
            fs.Position = info.DataOffset;
            fs.ReadExactly(data);
        }

        var samples = new short[data.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(i * 2));
        return samples;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~WavFileTests`
Expected: PASS, 5 tests.

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Audio/WavFile.cs tests/WebAudioArchive.Tests/Support/Signals.cs tests/WebAudioArchive.Tests/WavFileTests.cs
git commit -m "Analysis: WAV read, slice and write, and synthetic test signals (#76)"
```

---

### Task 5: Audio features and the clipping detector

**Files:**
- Create: `src/WebAudioArchive.Analysis/Audio/FeatureExtractor.cs`, `src/WebAudioArchive.Analysis/Detectors/ClippingDetector.cs`
- Test: `tests/WebAudioArchive.Tests/ClippingDetectorTests.cs`

**Interfaces:**
- Consumes: `ClipLevels`, `ClippingOptions` (Task 2), `Severities` (Task 3), `Signals` (Task 4).
- Produces:
  - `sealed record ClipRun(long StartFrame, int Length, int Channel, float Peak)`; `sealed record ClipLevelRuns(double Level, long Count, bool Truncated, IReadOnlyList<ClipRun> Runs)`; `sealed record AudioFeatures(int Schema, int SampleRate, int Channels, long Frames, double WindowSeconds, IReadOnlyList<short> WindowDbfsTenths, IReadOnlyList<ClipLevelRuns> Clips)` with `const int CurrentSchema = 1`, `double Seconds`, `double WindowDbfs(int i)`.
  - `sealed class FeatureExtractor(int sampleRate, int channels) { const double WindowSeconds = 0.05; void Push(ReadOnlySpan<float> interleaved); void PushBytes(ReadOnlySpan<byte> f32le); AudioFeatures Complete(); }`
  - `sealed record ClipEvent(double StartSeconds, double EndSeconds, int Runs, long ClippedSamples, float Peak, IReadOnlyList<int> Channels)` with `DurationMs`; `sealed record ClippingResult(IReadOnlyList<ClipEvent> Events, bool Truncated, long RunCount)`; `static class ClippingDetector { const string Name = "clipping"; const string Version = "1"; ClippingResult Detect(AudioFeatures, ClippingOptions); string Severity(ClipEvent, ClippingOptions); }`

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/ClippingDetectorTests.cs`:
```csharp
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class ClippingDetectorTests
{
    const double OneSample = 1.0 / Signals.Rate;
    static readonly ClippingOptions Defaults = new();

    static AudioFeatures Features(float[] interleaved, int channels = 1)
    {
        var fx = new FeatureExtractor(Signals.Rate, channels);
        fx.Push(interleaved);
        return fx.Complete();
    }

    [Fact]
    public void Planted_runs_are_found_at_the_right_time_and_short_runs_are_not()
    {
        var s = Signals.Clip(Signals.Clip(Signals.Sine(2), 0.5, 5), 1.5, 2);

        var result = ClippingDetector.Detect(Features(s), Defaults);

        var e = Assert.Single(result.Events);
        Assert.Equal(0.5, e.StartSeconds, OneSample * 2);
        Assert.Equal(0.5 + 5 * OneSample, e.EndSeconds, OneSample * 2);
        Assert.Equal(1, e.Runs);
        Assert.Equal(5, e.ClippedSamples);
        Assert.Equal(new[] { 0 }, e.Channels);
        Assert.False(result.Truncated);
    }

    [Fact]
    public void Threshold_is_inclusive_and_just_below_does_not_count()
    {
        var s = Signals.Clip(Signals.Clip(Signals.Sine(2), 0.5, 4, 0.99f), 1.5, 4, 0.989f);
        var e = Assert.Single(ClippingDetector.Detect(Features(s), Defaults).Events);
        Assert.Equal(0.5, e.StartSeconds, OneSample * 2);
    }

    [Theory]
    [InlineData(0.2, 1)]
    [InlineData(0.3, 2)]
    public void Runs_within_the_merge_window_become_one_event(double secondStart, int expectedEvents)
    {
        var s = Signals.Clip(Signals.Clip(Signals.Sine(2), 1.0, 3), 1.0 + secondStart, 3);
        var events = ClippingDetector.Detect(Features(s), Defaults).Events;
        Assert.Equal(expectedEvents, events.Count);
        if (expectedEvents == 1) Assert.Equal(2, events[0].Runs);
    }

    [Fact]
    public void Run_split_across_reads_counts_once()
    {
        var s = Signals.Stereo(Signals.Clip(Signals.Sine(1), 0.25, 50));
        var whole = Features(s, 2);

        var bySample = new FeatureExtractor(Signals.Rate, 2);
        foreach (var x in s) bySample.Push([x]);
        var bytes = new byte[s.Length * 4];
        Buffer.BlockCopy(s, 0, bytes, 0, bytes.Length);
        var byOddBytes = new FeatureExtractor(Signals.Rate, 2);
        for (var i = 0; i < bytes.Length; i += 3) byOddBytes.PushBytes(bytes.AsSpan(i, Math.Min(3, bytes.Length - i)));

        static IEnumerable<string> Shape(AudioFeatures f) => ClippingDetector.Detect(f, Defaults).Events
            .Select(e => $"{e.StartSeconds:R}|{e.EndSeconds:R}|{e.Runs}|{e.ClippedSamples}|{string.Join(',', e.Channels)}");

        foreach (var other in new[] { bySample.Complete(), byOddBytes.Complete() })
        {
            Assert.Equal(whole.WindowDbfsTenths, other.WindowDbfsTenths);
            Assert.Equal(whole.Clips.Select(c => c.Runs.Count), other.Clips.Select(c => c.Runs.Count));
            Assert.Equal(Shape(whole), Shape(other));
        }

        Assert.Equal(2, ClippingDetector.Detect(whole, Defaults).Events[0].Channels.Count);
    }

    [Fact]
    public void Overshoot_and_negative_full_scale_count()
    {
        var s = Signals.Clip(Signals.Clip(Signals.Sine(3), 0.5, 3, 1.2f), 2.0, 3, -1.0f);

        var events = ClippingDetector.Detect(Features(s), Defaults).Events;

        Assert.Equal(2, events.Count);
        Assert.Equal(1.2, events[0].Peak, 3);
        Assert.Equal(1.0, events[1].Peak, 3);
    }

    [Fact]
    public void Stereo_reports_the_clipped_channel()
    {
        var left = Signals.Sine(1);
        var right = Signals.Clip(Signals.Sine(1), 0.5, 10);
        var e = Assert.Single(ClippingDetector.Detect(Features(Signals.Stereo(left, right), 2), Defaults).Events);
        Assert.Equal(new[] { 1 }, e.Channels);
    }

    [Fact]
    public void Severity_follows_duration()
    {
        var shortRun = new ClipEvent(1, 1.01, 1, 400, 1, [0]);
        var longRun = new ClipEvent(1, 1.2, 3, 9000, 1, [0]);
        Assert.Equal(Severities.Low, ClippingDetector.Severity(shortRun, Defaults));
        Assert.Equal(Severities.Medium, ClippingDetector.Severity(longRun, Defaults));
    }

    [Fact]
    public void Window_levels_are_dbfs_and_silence_is_the_floor()
    {
        var f = Features(Signals.Concat(Signals.Sine(1, amp: 1.0), Signals.Silence(1)));

        Assert.Equal(40, f.WindowDbfsTenths.Count);
        Assert.Equal(-3.0, f.WindowDbfs(5), 0);
        Assert.Equal(-120, f.WindowDbfs(30));
        Assert.Equal(2.0, f.Seconds, 6);
    }

    [Fact]
    public void Too_many_runs_marks_the_level_truncated_but_keeps_counting()
    {
        var n = ClipLevels.MaxRunsPerLevel + 1;
        var s = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            s[3 * i] = 1f;
            s[3 * i + 1] = 1f;
        }

        var f = Features(s);
        var level = f.Clips.Single(c => c.Level == 0.99);

        Assert.True(level.Truncated);
        Assert.Equal(n, level.Count);
        Assert.Equal(ClipLevels.MaxRunsPerLevel, level.Runs.Count);
        Assert.True(ClippingDetector.Detect(f, Defaults with { MinRunSamples = 2 }).Truncated);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ClippingDetectorTests`
Expected: build FAILS with `CS0246` for `FeatureExtractor` and `ClippingDetector`.

- [ ] **Step 3: Implement the feature extractor**

`src/WebAudioArchive.Analysis/Audio/FeatureExtractor.cs`:
```csharp
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace WebAudioArchive.Analysis.Audio;

/// <summary>A run of consecutive samples on one channel at or above a level, by absolute value. StartFrame counts sample frames from the start of the audio.</summary>
public sealed record ClipRun(long StartFrame, int Length, int Channel, float Peak);

/// <summary>Runs kept at one level. Count is every run seen; Runs holds at most ClipLevels.MaxRunsPerLevel of them.</summary>
public sealed record ClipLevelRuns(double Level, long Count, bool Truncated, IReadOnlyList<ClipRun> Runs);

/// <summary>
/// features.json: what the audio detectors need, kept so they can be rerun without decoding again. Window levels are
/// the loudest channel's RMS over 50 ms, in tenths of a dBFS (digital silence is -120.0).
/// </summary>
public sealed record AudioFeatures(
    int Schema,
    int SampleRate,
    int Channels,
    long Frames,
    double WindowSeconds,
    IReadOnlyList<short> WindowDbfsTenths,
    IReadOnlyList<ClipLevelRuns> Clips)
{
    public const int CurrentSchema = 1;

    [JsonIgnore]
    public double Seconds => SampleRate == 0 ? 0 : (double)Frames / SampleRate;

    public double WindowDbfs(int i) => WindowDbfsTenths[i] / 10.0;
}

/// <summary>
/// Streams interleaved float PCM (from the decoder, or as little-endian f32 bytes) into window levels and clip runs. Reads may split
/// a sample, a frame or a run at any byte; the result is the same as for one read.
/// </summary>
public sealed class FeatureExtractor
{
    public const double WindowSeconds = 0.05;

    readonly int _rate;
    readonly int _channels;
    readonly int _windowFrames;
    readonly double[] _sumSquares;
    readonly List<short> _windows = [];
    readonly long[,] _runStart;
    readonly int[,] _runLength;
    readonly float[,] _runPeak;
    readonly int[] _open;
    readonly List<ClipRun>[] _runs;
    readonly long[] _counts;
    readonly bool[] _truncated;
    readonly byte[] _carry = new byte[4];
    int _carryLength;
    int _windowFill;
    long _frame;
    int _channel;
    bool _completed;

    public FeatureExtractor(int sampleRate, int channels)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (channels is <= 0 or > 8) throw new ArgumentOutOfRangeException(nameof(channels));
        if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("f32le input needs a little-endian machine");
        _rate = sampleRate;
        _channels = channels;
        _windowFrames = Math.Max(1, (int)Math.Round(sampleRate * WindowSeconds));
        _sumSquares = new double[channels];
        var levels = ClipLevels.Levels.Length;
        _runStart = new long[channels, levels];
        _runLength = new int[channels, levels];
        _runPeak = new float[channels, levels];
        _open = new int[channels];
        _runs = Enumerable.Range(0, levels).Select(_ => new List<ClipRun>()).ToArray();
        _counts = new long[levels];
        _truncated = new bool[levels];
    }

    public void Push(ReadOnlySpan<float> interleaved)
    {
        if (_completed) throw new InvalidOperationException("Complete has been called");
        var lowest = ClipLevels.Levels[0];
        foreach (var raw in interleaved)
        {
            var v = float.IsFinite(raw) ? raw : 0f;
            var a = Math.Abs(v);
            _sumSquares[_channel] += (double)v * v;
            if (a >= lowest || _open[_channel] > 0) TrackRuns(_channel, a);
            if (++_channel == _channels)
            {
                _channel = 0;
                _frame++;
                if (++_windowFill == _windowFrames) FlushWindow();
            }
        }
    }

    /// <summary>Little-endian 32-bit floats; a float split across reads is carried to the next read.</summary>
    public void PushBytes(ReadOnlySpan<byte> bytes)
    {
        if (_carryLength > 0)
        {
            var take = Math.Min(4 - _carryLength, bytes.Length);
            bytes[..take].CopyTo(_carry.AsSpan(_carryLength));
            _carryLength += take;
            bytes = bytes[take..];
            if (_carryLength < 4) return;
            Push([BinaryPrimitives.ReadSingleLittleEndian(_carry)]);
            _carryLength = 0;
        }

        var whole = bytes.Length / 4 * 4;
        Push(MemoryMarshal.Cast<byte, float>(bytes[..whole]));
        bytes[whole..].CopyTo(_carry);
        _carryLength = bytes.Length - whole;
    }

    public AudioFeatures Complete()
    {
        if (_completed) throw new InvalidOperationException("Complete has been called");
        _completed = true;
        if (_windowFill > 0) FlushWindow();
        for (var c = 0; c < _channels; c++)
            for (var l = 0; l < ClipLevels.Levels.Length; l++)
                if (_runLength[c, l] > 0) Close(c, l);

        var clips = ClipLevels.Levels
            .Select((level, l) => new ClipLevelRuns(level, _counts[l], _truncated[l], _runs[l].OrderBy(r => r.StartFrame).ThenBy(r => r.Channel).ToList()))
            .ToList();
        return new AudioFeatures(AudioFeatures.CurrentSchema, _rate, _channels, _frame, WindowSeconds, _windows.ToList(), clips);
    }

    void TrackRuns(int c, float a)
    {
        for (var l = 0; l < ClipLevels.Levels.Length; l++)
        {
            if (a >= ClipLevels.Levels[l])
            {
                if (_runLength[c, l] == 0)
                {
                    _runStart[c, l] = _frame;
                    _runPeak[c, l] = 0;
                    _open[c]++;
                }

                _runLength[c, l]++;
                if (a > _runPeak[c, l]) _runPeak[c, l] = a;
            }
            else if (_runLength[c, l] > 0)
            {
                Close(c, l);
            }
        }
    }

    void Close(int c, int l)
    {
        var length = _runLength[c, l];
        _runLength[c, l] = 0;
        _open[c]--;
        if (length < ClipLevels.MinStoredRun) return;
        _counts[l]++;
        if (_runs[l].Count < ClipLevels.MaxRunsPerLevel) _runs[l].Add(new ClipRun(_runStart[c, l], length, c, _runPeak[c, l]));
        else _truncated[l] = true;
    }

    void FlushWindow()
    {
        double loudest = 0;
        for (var c = 0; c < _channels; c++)
        {
            loudest = Math.Max(loudest, _sumSquares[c] / _windowFill);
            _sumSquares[c] = 0;
        }

        var db = loudest <= 1e-12 ? -120.0 : Math.Clamp(10 * Math.Log10(loudest), -120.0, 20.0);
        _windows.Add((short)Math.Round(db * 10));
        _windowFill = 0;
    }
}
```

`src/WebAudioArchive.Analysis/Detectors/ClippingDetector.cs`:
```csharp
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

public sealed record ClipEvent(double StartSeconds, double EndSeconds, int Runs, long ClippedSamples, float Peak, IReadOnlyList<int> Channels)
{
    public double DurationMs => (EndSeconds - StartSeconds) * 1000;
}

/// <summary>Events, whether the level's run list was truncated (then counts are a floor), and how many runs the level saw.</summary>
public sealed record ClippingResult(IReadOnlyList<ClipEvent> Events, bool Truncated, long RunCount);

/// <summary>
/// Spec 3.6: an event is a run of at least MinRunSamples consecutive samples at or above Threshold of full scale on a
/// channel; runs on any channel less than MergeWithinMs apart are one event. Provisional thresholds (Task 18).
/// </summary>
public static class ClippingDetector
{
    public const string Name = "clipping";
    public const string Version = "1";

    public static ClippingResult Detect(AudioFeatures f, ClippingOptions o)
    {
        var level = f.Clips.FirstOrDefault(c => Math.Abs(c.Level - o.Threshold) < 1e-9)
            ?? throw new InvalidOperationException($"features.json has no clip runs at level {o.Threshold}; decode again with that level in ClipLevels");
        var rate = (double)f.SampleRate;
        var merge = o.MergeWithinMs / 1000.0;
        var events = new List<ClipEvent>();

        double start = 0, end = 0;
        var runs = 0;
        long samples = 0;
        float peak = 0;
        var channels = new SortedSet<int>();
        foreach (var r in level.Runs.Where(r => r.Length >= o.MinRunSamples).OrderBy(r => r.StartFrame))
        {
            var rs = r.StartFrame / rate;
            var re = (r.StartFrame + r.Length) / rate;
            if (runs > 0 && rs - end <= merge)
            {
                end = Math.Max(end, re);
                runs++;
                samples += r.Length;
                peak = Math.Max(peak, r.Peak);
                channels.Add(r.Channel);
                continue;
            }

            if (runs > 0) events.Add(new ClipEvent(start, end, runs, samples, peak, channels.ToList()));
            (start, end, runs, samples, peak) = (rs, re, 1, r.Length, r.Peak);
            channels = [r.Channel];
        }

        if (runs > 0) events.Add(new ClipEvent(start, end, runs, samples, peak, channels.ToList()));
        return new ClippingResult(events, level.Truncated, level.Count);
    }

    public static string Severity(ClipEvent e, ClippingOptions o) => e.DurationMs >= o.MediumMs ? Severities.Medium : Severities.Low;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ClippingDetectorTests`
Expected: PASS, 10 tests. (`ClipEvent` holds a list, so record equality would compare `Channels` by reference; that is why the split-read test compares a text shape of each event.)

- [ ] **Step 5: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Audio/FeatureExtractor.cs src/WebAudioArchive.Analysis/Detectors/ClippingDetector.cs tests/WebAudioArchive.Tests/ClippingDetectorTests.cs
git commit -m "Analysis: streaming audio features and the clipping detector (#79)"
```

---

### Task 6: Dead air detector

**Files:**
- Create: `src/WebAudioArchive.Analysis/Detectors/DeadAirDetector.cs`
- Test: `tests/WebAudioArchive.Tests/DeadAirDetectorTests.cs`

**Interfaces:**
- Consumes: `AudioFeatures`, `FeatureExtractor` (Task 5), `DeadAirOptions` (Task 2).
- Produces: `sealed record DeadAirEvent(double StartSeconds, double EndSeconds, double QuietestDbfs)` with `Seconds`; `static class DeadAirDetector { const string Name = "dead-air"; const string Version = "1"; IReadOnlyList<DeadAirEvent> Detect(AudioFeatures, DeadAirOptions); string Severity(DeadAirEvent, DeadAirOptions); }`

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/DeadAirDetectorTests.cs`:
```csharp
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class DeadAirDetectorTests
{
    static readonly DeadAirOptions Defaults = new();

    static IReadOnlyList<DeadAirEvent> Detect(float[] mono, DeadAirOptions? o = null)
    {
        var fx = new FeatureExtractor(Signals.Rate, 1);
        fx.Push(mono);
        return DeadAirDetector.Detect(fx.Complete(), o ?? Defaults);
    }

    [Fact]
    public void Speech_with_pauses_and_breaths_is_not_dead_air()
    {
        Assert.Empty(Detect(Signals.SpeechLike(60, seed: 3)));
    }

    [Fact]
    public void Six_seconds_of_silence_between_tones_is_one_event_at_the_right_time()
    {
        var e = Assert.Single(Detect(Signals.Concat(Signals.Sine(3), Signals.Silence(6), Signals.Sine(3))));
        Assert.Equal(3.0, e.StartSeconds, 2);
        Assert.Equal(9.0, e.EndSeconds, 2);
        Assert.Equal(-120, e.QuietestDbfs);
        Assert.Equal(Severities.Medium, DeadAirDetector.Severity(e, Defaults));
    }

    [Theory]
    [InlineData(4.0, 0)]
    [InlineData(5.0, 1)]
    public void The_minimum_length_is_inclusive(double silence, int expected)
    {
        Assert.Equal(expected, Detect(Signals.Concat(Signals.Sine(3), Signals.Silence(silence), Signals.Sine(3))).Count);
    }

    [Fact]
    public void Quiet_room_tone_is_not_dead_air_but_low_hiss_is()
    {
        Assert.Empty(Detect(Signals.Concat(Signals.Sine(1), Signals.Noise(10, -45, 1), Signals.Sine(1))));
        Assert.Single(Detect(Signals.Concat(Signals.Sine(1), Signals.Noise(10, -60, 1), Signals.Sine(1))));
    }

    [Fact]
    public void Silence_running_to_the_end_of_the_segment_is_reported()
    {
        var e = Assert.Single(Detect(Signals.Concat(Signals.Sine(2), Signals.Silence(7))));
        Assert.Equal(9.0, e.EndSeconds, 2);
    }

    [Fact]
    public void Long_silence_is_high_severity()
    {
        var e = Assert.Single(Detect(Signals.Concat(Signals.Sine(1), Signals.Silence(40), Signals.Sine(1))));
        Assert.Equal(Severities.High, DeadAirDetector.Severity(e, Defaults));
    }

    [Fact]
    public void The_threshold_comes_from_config()
    {
        var quiet = Signals.Concat(Signals.Sine(1), Signals.Noise(10, -45, 1), Signals.Sine(1));
        Assert.Single(Detect(quiet, Defaults with { ThresholdDbfs = -40 }));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DeadAirDetectorTests`
Expected: build FAILS with `CS0246: The type or namespace name 'DeadAirDetector' could not be found`.

- [ ] **Step 3: Implement the detector**

`src/WebAudioArchive.Analysis/Detectors/DeadAirDetector.cs`:
```csharp
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

public sealed record DeadAirEvent(double StartSeconds, double EndSeconds, double QuietestDbfs)
{
    public double Seconds => EndSeconds - StartSeconds;
}

/// <summary>
/// Dead air: every 50 ms window (loudest channel) below ThresholdDbfs for at least MinSeconds. Pauses in speech and
/// the gap between songs are far shorter, which is how intentional pauses are excluded; quiet room tone above the
/// threshold is not dead air. A recording gap is not dead air either: the audio is joined across it, so it never
/// reaches the decoder (gaps are reported from the sidecars instead).
/// </summary>
public static class DeadAirDetector
{
    public const string Name = "dead-air";
    public const string Version = "1";

    public static IReadOnlyList<DeadAirEvent> Detect(AudioFeatures f, DeadAirOptions o)
    {
        var events = new List<DeadAirEvent>();
        var w = f.WindowSeconds;
        var start = -1;
        var quietest = 0.0;
        for (var i = 0; i <= f.WindowDbfsTenths.Count; i++)
        {
            var quiet = i < f.WindowDbfsTenths.Count && f.WindowDbfs(i) < o.ThresholdDbfs;
            if (quiet)
            {
                if (start < 0)
                {
                    start = i;
                    quietest = f.WindowDbfs(i);
                }
                else
                {
                    quietest = Math.Min(quietest, f.WindowDbfs(i));
                }

                continue;
            }

            if (start < 0) continue;
            var from = start * w;
            var to = Math.Min(i * w, f.Seconds);
            if (to - from >= o.MinSeconds - 1e-9) events.Add(new DeadAirEvent(from, to, quietest));
            start = -1;
        }

        return events;
    }

    public static string Severity(DeadAirEvent e, DeadAirOptions o) => e.Seconds >= o.SevereSeconds ? Severities.High : Severities.Medium;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~DeadAirDetectorTests`
Expected: PASS, 8 tests.

- [ ] **Step 5: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Detectors/DeadAirDetector.cs tests/WebAudioArchive.Tests/DeadAirDetectorTests.cs
git commit -m "Analysis: dead air detector from window levels (#81)"
```

---

### Task 7: Swear detector with tiers and the watershed

**Files:**
- Create: `src/WebAudioArchive.Analysis/Detectors/WordNormaliser.cs`, `src/WebAudioArchive.Analysis/Detectors/SwearList.Matching.cs`, `src/WebAudioArchive.Analysis/Detectors/Watershed.cs`, `src/WebAudioArchive.Analysis/Detectors/SwearDetector.cs`
- Create: `tests/WebAudioArchive.Tests/Support/TranscriptFixtures.cs`
- Test: `tests/WebAudioArchive.Tests/SwearDetectorTests.cs`

**Interfaces:**
- Consumes: `SwearList`, `SwearTiers`, `SwearOptions`, `AnalysisConfigLoader` (Task 2); `TranscriptDocument`, `TranscriptSegment`, `TranscriptWord`, `TimelineAnchor`, `Severities` (Task 3).
- Produces:
  - `static class WordNormaliser { string Normalise(string raw); }` (lower-case letters, digits and `*` only)
  - `sealed record SwearMatch(string Form, string Tier, bool Masked, double Certainty)`; `SwearMatch? SwearList.Match(string rawWord)`
  - `sealed class Watershed(TimeZoneInfo zone, TimeOnly start, TimeOnly end) { static Watershed From(SwearOptions, TimeZoneInfo); bool IsBeforeWatershed(DateTimeOffset utc); DateTimeOffset StartUtcOn(DateTimeOffset utc); }`
  - `sealed record SwearHit(int Segment, int Word, string Text, string Form, string Tier, bool Masked, double Confidence, double Start, double End, string Context)`; `static class SwearDetector { const string Name = "swear"; const string Version = "1"; IReadOnlyList<SwearHit> Detect(TranscriptDocument, SwearList); string Severity(string tier, bool beforeWatershed); }`
  - Test helper `TranscriptFixtures.Of(DateTimeOffset start, params string[] lines)` and `TranscriptFixtures.OfSegments(DateTimeOffset start, params (string Text, string? Suppressed)[] lines)`: segment i starts at i * 10 s, one word every 0.5 s, word probability 0.9, timeline anchored at `start`.

- [ ] **Step 1: Write the transcript fixture helper**

`tests/WebAudioArchive.Tests/Support/TranscriptFixtures.cs`:
```csharp
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Tests.Support;

/// <summary>Hand-made transcripts: segment i starts at i * 10 s; its words are spaced 0.5 s apart, 0.4 s long, probability 0.9.</summary>
public static class TranscriptFixtures
{
    public static TranscriptDocument Of(DateTimeOffset start, params string[] lines) =>
        OfSegments(start, lines.Select(l => (l, (string?)null)).ToArray());

    public static TranscriptDocument OfSegments(DateTimeOffset start, params (string Text, string? Suppressed)[] lines)
    {
        var segments = new List<TranscriptSegment>();
        for (var i = 0; i < lines.Length; i++)
        {
            var at = i * 10.0;
            var words = lines[i].Text
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select((w, j) => new TranscriptWord(w, at + j * 0.5, at + j * 0.5 + 0.4, 0.9))
                .ToList();
            segments.Add(new TranscriptSegment(at, at + Math.Max(1, words.Count * 0.5), lines[i].Text, 0.01, -0.2, lines[i].Suppressed, words));
        }

        return new TranscriptDocument(TranscriptDocument.CurrentSchema, "burst", "a", "home-a", "a/2026/10-16/190000", "190000.mp3",
            start, start.AddHours(1), 3600, [new TimelineAnchor(0, start)], "fake", "fake-model", "en", "tv1", "decoder test",
            start.AddHours(1), 1, segments);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/SwearDetectorTests.cs`:
```csharp
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class SwearDetectorTests
{
    static readonly SwearList List = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json")).Swears;
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    static readonly Watershed Uk = Watershed.From(new SwearOptions(), London);

    [Theory]
    [InlineData("Shit!", SwearTiers.Strong, false)]
    [InlineData("FUCKED", SwearTiers.Severe, false)]
    [InlineData("fuckin'", SwearTiers.Severe, false)]
    [InlineData("Motherfuckers,", SwearTiers.Severe, false)]
    [InlineData("bull-shit", SwearTiers.Strong, false)]
    [InlineData("bitches", SwearTiers.Strong, false)]
    [InlineData("pissed", SwearTiers.Mild, false)]
    [InlineData("Bloody", SwearTiers.Mild, false)]
    [InlineData("f***ing", SwearTiers.Severe, true)]
    [InlineData("f*ck", SwearTiers.Severe, true)]
    [InlineData("s***", SwearTiers.Strong, true)]
    public void Inflections_punctuation_and_masks_match(string word, string tier, bool masked)
    {
        var m = List.Match(word);
        Assert.NotNull(m);
        Assert.Equal(tier, m.Tier);
        Assert.Equal(masked, m.Masked);
        Assert.True(masked ? m.Certainty < 1 : m.Certainty == 1);
    }

    [Theory]
    [InlineData("Scunthorpe")]
    [InlineData("Dickens")]
    [InlineData("assess")]
    [InlineData("cocktail")]
    [InlineData("shitake")]
    [InlineData("knobs")]
    [InlineData("pricked")]
    [InlineData("class")]
    [InlineData("hello")]
    [InlineData("****")]
    [InlineData("...")]
    [InlineData("")]
    public void Near_misses_do_not_match(string word)
    {
        Assert.Null(List.Match(word));
    }

    [Fact]
    public void Hits_carry_tier_time_confidence_and_context_and_suppressed_segments_are_ignored()
    {
        var start = new DateTimeOffset(2026, 10, 16, 19, 0, 0, TimeSpan.Zero);
        var t = TranscriptFixtures.OfSegments(start,
            ("Well that was shit, honestly it was", null),
            ("fuck fuck fuck fuck", "repetition"),
            ("a lovely clean sentence", null));

        var hit = Assert.Single(SwearDetector.Detect(t, List));

        Assert.Equal(0, hit.Segment);
        Assert.Equal(3, hit.Word);
        Assert.Equal("shit,", hit.Text);
        Assert.Equal("shit", hit.Form);
        Assert.Equal(SwearTiers.Strong, hit.Tier);
        Assert.Equal(1.5, hit.Start);
        Assert.Equal(0.9, hit.Confidence, 3);
        Assert.Equal("Well that was shit, honestly it was", hit.Context);
    }

    [Theory]
    [InlineData("2026-10-24T19:59:00Z", true)]   // 20:59 BST
    [InlineData("2026-10-24T20:00:00Z", false)]  // 21:00 BST, the watershed starts
    [InlineData("2026-10-24T04:29:00Z", false)]  // 05:29 BST
    [InlineData("2026-10-24T04:30:00Z", true)]   // 05:30 BST, the watershed ends
    [InlineData("2026-10-24T23:30:00Z", false)]  // 00:30 BST on the 25th, a different local day
    [InlineData("2026-10-25T00:30:00Z", false)]  // 01:30 BST, the first 01:30 of the change night
    [InlineData("2026-10-25T01:30:00Z", false)]  // 01:30 GMT, the second
    [InlineData("2026-10-25T05:30:00Z", true)]   // 05:30 GMT
    [InlineData("2026-10-26T20:30:00Z", true)]   // 20:30 GMT, which in summer would have been after the watershed
    [InlineData("2026-10-26T21:00:00Z", false)]  // 21:00 GMT
    [InlineData("2027-03-28T00:30:00Z", false)]  // 00:30 GMT before the spring change
    [InlineData("2027-03-28T04:30:00Z", true)]   // 05:30 BST after it
    public void Watershed_follows_uk_local_time_across_the_clock_change(string utc, bool before)
    {
        Assert.Equal(before, Uk.IsBeforeWatershed(DateTimeOffset.Parse(utc, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Watershed_start_in_utc_moves_with_the_clocks()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 20, 0, 0, TimeSpan.Zero), Uk.StartUtcOn(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 21, 0, 0, TimeSpan.Zero), Uk.StartUtcOn(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));
    }

    [Theory]
    [InlineData(SwearTiers.Mild, false, Severities.Low)]
    [InlineData(SwearTiers.Mild, true, Severities.Medium)]
    [InlineData(SwearTiers.Strong, false, Severities.Medium)]
    [InlineData(SwearTiers.Strong, true, Severities.High)]
    [InlineData(SwearTiers.Severe, false, Severities.High)]
    [InlineData(SwearTiers.Severe, true, Severities.High)]
    public void Severity_rises_before_the_watershed(string tier, bool before, string severity)
    {
        Assert.Equal(severity, SwearDetector.Severity(tier, before));
    }

    [Theory]
    [InlineData("Fuckin'", "fuckin")]
    [InlineData("bull-shit", "bullshit")]
    [InlineData("F***!", "f***")]
    [InlineData("Café", "caf")]
    public void Normalising_keeps_letters_digits_and_stars(string raw, string expected)
    {
        Assert.Equal(expected, WordNormaliser.Normalise(raw));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SwearDetectorTests`
Expected: build FAILS with `CS1061: 'SwearList' does not contain a definition for 'Match'` and `CS0246` for `Watershed`, `SwearDetector`, `WordNormaliser`.

- [ ] **Step 4: Implement normalising, matching, the watershed and the detector**

`src/WebAudioArchive.Analysis/Detectors/WordNormaliser.cs`:
```csharp
using System.Text;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>
/// One whisper word to its matching form: lower case, only a to z, digits and '*' kept, so "Fuckin'" is "fuckin",
/// "bull-shit" is "bullshit" and "F***!" is "f***". Letters with accents are dropped (the lists are English).
/// </summary>
public static class WordNormaliser
{
    public static string Normalise(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw.Normalize(NormalizationForm.FormKC))
        {
            var c = char.ToLowerInvariant(ch);
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '*') sb.Append(c);
        }

        return sb.ToString();
    }
}
```

`src/WebAudioArchive.Analysis/Detectors/SwearList.Matching.cs`:
```csharp
namespace WebAudioArchive.Analysis.Detectors;

/// <summary>A matched form, its tier, whether the word was masked with asterisks, and how sure the match is (1, or less when masked).</summary>
public sealed record SwearMatch(string Form, string Tier, bool Masked, double Certainty);

public sealed partial class SwearList
{
    /// <summary>
    /// Whole-word match after normalising. A masked word ("f***", "f*ck", "s***ing") matches forms with the same first
    /// letters, the same ending and a length within 2; the highest tier wins, at certainty 0.5 when every candidate is
    /// in that tier and 0.3 otherwise. A word with no letter before the first '*' never matches.
    /// </summary>
    public SwearMatch? Match(string rawWord)
    {
        var w = WordNormaliser.Normalise(rawWord);
        if (w.Length == 0 || Allow.Contains(w)) return null;
        if (!w.Contains('*')) return Forms.TryGetValue(w, out var tier) ? new SwearMatch(w, tier, false, 1.0) : null;

        var first = w.IndexOf('*');
        var last = w.LastIndexOf('*');
        if (first == 0 || w[first..(last + 1)].Any(c => c != '*')) return null;
        var prefix = w[..first];
        var suffix = w[(last + 1)..];
        var candidates = Forms
            .Where(f => f.Key.Length > prefix.Length + suffix.Length
                && f.Key.StartsWith(prefix, StringComparison.Ordinal)
                && f.Key.EndsWith(suffix, StringComparison.Ordinal)
                && Math.Abs(f.Key.Length - w.Length) <= 2)
            .ToList();
        if (candidates.Count == 0) return null;
        var best = candidates.MaxBy(c => SwearTiers.Rank(c.Value));
        var certainty = candidates.All(c => c.Value == best.Value) ? 0.5 : 0.3;
        return new SwearMatch(w, best.Value, true, certainty);
    }
}
```

`src/WebAudioArchive.Analysis/Detectors/Watershed.cs`:
```csharp
using System.Globalization;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>
/// The broadcast watershed in the station's local time (UK: 21:00 to 05:30). "Before the watershed" is the protected
/// daytime period, local time from End up to Start. The zone is used only to decide this; times stay UTC.
/// </summary>
public sealed class Watershed(TimeZoneInfo zone, TimeOnly start, TimeOnly end)
{
    public static Watershed From(SwearOptions o, TimeZoneInfo zone) => new(
        zone,
        TimeOnly.ParseExact(o.WatershedStart, "HH:mm", CultureInfo.InvariantCulture),
        TimeOnly.ParseExact(o.WatershedEnd, "HH:mm", CultureInfo.InvariantCulture));

    public bool IsBeforeWatershed(DateTimeOffset utc)
    {
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
        var inWatershed = start <= end ? local >= start && local < end : local >= start || local < end;
        return !inWatershed;
    }

    /// <summary>The UTC instant the watershed starts on the local day containing <paramref name="utc"/>.</summary>
    public DateTimeOffset StartUtcOn(DateTimeOffset utc)
    {
        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, zone).DateTime);
        var local = localDate.ToDateTime(start);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }
}
```

`src/WebAudioArchive.Analysis/Detectors/SwearDetector.cs`:
```csharp
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>A matched word: its segment and word index, the word as transcribed, the matched form, tier, times (seconds into the audio) and the words around it.</summary>
public sealed record SwearHit(int Segment, int Word, string Text, string Form, string Tier, bool Masked, double Confidence, double Start, double End, string Context);

/// <summary>
/// Spec 3.6: whisper words against the tiered list, whole words and listed inflections. Speech to text can mis-hear or
/// soften words, so confidence is whisper's word probability times the match certainty, and every hit links to its
/// audio for a person to confirm. Song lyrics are transcribed too and are reported the same way (D12).
/// </summary>
public static class SwearDetector
{
    public const string Name = "swear";
    public const string Version = "1";
    const int ContextWords = 5;

    public static IReadOnlyList<SwearHit> Detect(TranscriptDocument t, SwearList list)
    {
        var hits = new List<SwearHit>();
        for (var i = 0; i < t.Segments.Count; i++)
        {
            var s = t.Segments[i];
            if (s.Suppressed is not null) continue;
            for (var j = 0; j < s.Words.Count; j++)
            {
                var w = s.Words[j];
                var m = list.Match(w.Text);
                if (m is null) continue;
                var from = Math.Max(0, j - ContextWords);
                var to = Math.Min(s.Words.Count, j + ContextWords + 1);
                var context = string.Join(' ', s.Words.Skip(from).Take(to - from).Select(x => x.Text));
                var confidence = Math.Round(Math.Clamp(w.Probability, 0, 1) * m.Certainty, 3);
                hits.Add(new SwearHit(i, j, w.Text, m.Form, m.Tier, m.Masked, confidence, w.Start, Math.Max(w.Start, w.End), context));
            }
        }

        return hits;
    }

    /// <summary>mild is low, strong medium, severe high; before the watershed each is one step more serious.</summary>
    public static string Severity(string tier, bool beforeWatershed) =>
        (SwearTiers.Rank(tier) + (beforeWatershed ? 1 : 0)) switch
        {
            <= 1 => Severities.Low,
            2 => Severities.Medium,
            _ => Severities.High,
        };
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SwearDetectorTests`
Expected: PASS, 47 tests (11 + 12 + 1 + 12 + 1 + 6 + 4). If `s***` resolves to a different tier than strong, look at the candidates it found (`sod` is mild, `shit` and `shite` strong): the highest tier wins.

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Detectors/WordNormaliser.cs src/WebAudioArchive.Analysis/Detectors/SwearList.Matching.cs src/WebAudioArchive.Analysis/Detectors/Watershed.cs src/WebAudioArchive.Analysis/Detectors/SwearDetector.cs tests/WebAudioArchive.Tests/Support/TranscriptFixtures.cs tests/WebAudioArchive.Tests/SwearDetectorTests.cs
git commit -m "Analysis: swear detector with tiers, masks, near-miss allow list and the UK watershed (#78)"
```

---

### Task 8: Filler detector

**Files:**
- Create: `src/WebAudioArchive.Analysis/Detectors/FillerDetector.cs`
- Test: `tests/WebAudioArchive.Tests/FillerDetectorTests.cs`

**Interfaces:**
- Consumes: `WordNormaliser`, `FillerOptions`, `TranscriptFixtures` (Tasks 2, 7), `TranscriptDocument` (Task 3).
- Produces: `sealed record FillerHit(int Segment, int Word, string Text, string Canonical, double Start, double End, double Confidence)`; `sealed record FillerResult(IReadOnlyList<FillerHit> Hits, double SpeechSeconds)`; `static class FillerDetector { const string Name = "filler"; const string Version = "1"; string? Canonical(string normalised); FillerResult Detect(TranscriptDocument, FillerOptions); }`

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/FillerDetectorTests.cs`:
```csharp
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class FillerDetectorTests
{
    static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    static readonly FillerOptions Defaults = new();

    [Fact]
    public void Common_fillers_are_counted_with_their_times()
    {
        var r = FillerDetector.Detect(TranscriptFixtures.Of(Start, "Um, so, er, we're on, uh, Burst."), Defaults);

        Assert.Equal(new[] { "um", "er", "uh" }, r.Hits.Select(h => h.Canonical));
        Assert.Equal(1.0, r.Hits[1].Start);
        Assert.Equal(0.72, r.Hits[0].Confidence, 3);
    }

    [Theory]
    [InlineData("Ummm", "um")]
    [InlineData("uhh,", "uh")]
    [InlineData("Errr", "er")]
    [InlineData("Erm.", "erm")]
    [InlineData("Ahh", "ah")]
    [InlineData("Hmm?", "hmm")]
    [InlineData("hmmmm", "hmm")]
    public void Drawn_out_spellings_are_one_filler(string word, string canonical)
    {
        Assert.Equal(canonical, Assert.Single(FillerDetector.Detect(TranscriptFixtures.Of(Start, word), Defaults).Hits).Canonical);
    }

    [Theory]
    [InlineData("I like it like that")]
    [InlineData("umbrella error ahead hmmph")]
    [InlineData("the summer era")]
    public void Like_and_lookalike_words_are_not_fillers(string line)
    {
        Assert.Empty(FillerDetector.Detect(TranscriptFixtures.Of(Start, line), Defaults).Hits);
    }

    [Fact]
    public void Suppressed_segments_are_not_counted_and_do_not_add_speech_time()
    {
        var t = TranscriptFixtures.OfSegments(Start, ("um um um um", "repetition"), ("so um yes", null));

        var r = FillerDetector.Detect(t, Defaults);

        Assert.Single(r.Hits);
        Assert.Equal(1.5, r.SpeechSeconds, 6);
    }

    [Fact]
    public void You_know_is_counted_only_when_configured_and_only_as_a_filler()
    {
        var t = TranscriptFixtures.Of(Start, "it was, you know, brilliant", "do you know the way");

        Assert.Empty(FillerDetector.Detect(t, Defaults).Hits);
        var hit = Assert.Single(FillerDetector.Detect(t, Defaults with { CountYouKnow = true }).Hits);
        Assert.Equal("you know", hit.Canonical);
    }

    [Fact]
    public void Only_configured_words_count()
    {
        var r = FillerDetector.Detect(TranscriptFixtures.Of(Start, "um ah hmm"), Defaults with { Words = ["um"] });
        Assert.Equal("um", Assert.Single(r.Hits).Canonical);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~FillerDetectorTests`
Expected: build FAILS with `CS0246: The type or namespace name 'FillerDetector' could not be found`.

- [ ] **Step 3: Implement the detector**

`src/WebAudioArchive.Analysis/Detectors/FillerDetector.cs`:
```csharp
using System.Text.RegularExpressions;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

public sealed record FillerHit(int Segment, int Word, string Text, string Canonical, double Start, double End, double Confidence);

/// <summary>The fillers found and the seconds of speech they were found in (non-suppressed segments with words), for a rate per minute.</summary>
public sealed record FillerResult(IReadOnlyList<FillerHit> Hits, double SpeechSeconds);

/// <summary>
/// Spec 3.6: counts um, uh, er, erm, ah and hmm (drawn-out spellings folded: "ummm" is "um"). "like" is never counted;
/// "you know" only when configured, and only when set off by commas. Whisper drops many fillers even with the verbatim
/// prompt, so this is an estimate; Task 18 records its measured precision and recall. Confidence is the word
/// probability times 0.8 for that reason.
/// </summary>
public static partial class FillerDetector
{
    public const string Name = "filler";
    public const string Version = "1";
    const double EstimateFactor = 0.8;

    public static string? Canonical(string normalised) => normalised switch
    {
        _ when Um().IsMatch(normalised) => "um",
        _ when Uh().IsMatch(normalised) => "uh",
        _ when Er().IsMatch(normalised) => "er",
        _ when Erm().IsMatch(normalised) => "erm",
        _ when Ah().IsMatch(normalised) => "ah",
        _ when Hmm().IsMatch(normalised) => "hmm",
        _ => null,
    };

    public static FillerResult Detect(TranscriptDocument t, FillerOptions o)
    {
        var words = new HashSet<string>(o.Words, StringComparer.Ordinal);
        var hits = new List<FillerHit>();
        double speech = 0;
        for (var i = 0; i < t.Segments.Count; i++)
        {
            var s = t.Segments[i];
            if (s.Suppressed is not null || s.Words.Count == 0) continue;
            speech += Math.Max(0, s.End - s.Start);
            for (var j = 0; j < s.Words.Count; j++)
            {
                var w = s.Words[j];
                var canonical = Canonical(WordNormaliser.Normalise(w.Text));
                if (canonical is not null && words.Contains(canonical))
                {
                    hits.Add(Hit(i, j, w.Text, canonical, w.Start, w.End, w.Probability));
                    continue;
                }

                if (o.CountYouKnow && j + 1 < s.Words.Count && IsYouKnow(s.Words, j))
                {
                    var next = s.Words[j + 1];
                    hits.Add(Hit(i, j, w.Text + " " + next.Text, "you know", w.Start, next.End, Math.Min(w.Probability, next.Probability)));
                }
            }
        }

        return new FillerResult(hits, speech);
    }

    /// <summary>"you know" set off as an aside: "know" ends in a comma, or "you" follows a word ending in a comma.</summary>
    static bool IsYouKnow(IReadOnlyList<TranscriptWord> words, int j)
    {
        if (WordNormaliser.Normalise(words[j].Text) != "you" || WordNormaliser.Normalise(words[j + 1].Text) != "know") return false;
        var knowEndsWithComma = words[j + 1].Text.TrimEnd().EndsWith(',');
        var afterComma = j > 0 && words[j - 1].Text.TrimEnd().EndsWith(',');
        return knowEndsWithComma || afterComma;
    }

    static FillerHit Hit(int segment, int word, string text, string canonical, double start, double end, double probability) =>
        new(segment, word, text, canonical, start, Math.Max(start, end), Math.Round(Math.Clamp(probability, 0, 1) * EstimateFactor, 3));

    [GeneratedRegex("^u+m+$")]
    private static partial Regex Um();

    [GeneratedRegex("^u+h+$")]
    private static partial Regex Uh();

    [GeneratedRegex("^e+r+$")]
    private static partial Regex Er();

    [GeneratedRegex("^e+r+m+$")]
    private static partial Regex Erm();

    [GeneratedRegex("^a+h+$")]
    private static partial Regex Ah();

    [GeneratedRegex("^h+m+$")]
    private static partial Regex Hmm();
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~FillerDetectorTests`
Expected: PASS, 14 tests.

- [ ] **Step 5: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Detectors/FillerDetector.cs tests/WebAudioArchive.Tests/FillerDetectorTests.cs
git commit -m "Analysis: filler detector (um, uh, er, erm, ah, hmm), labelled an estimate (#80)"
```

---

### Task 9: Scratch space and managed decode of a scratch copy

**Files:**
- Create: `src/WebAudioArchive.Analysis/Decode/ScratchSpace.cs`, `src/WebAudioArchive.Analysis/Decode/Mp3FrameReader.cs`, `src/WebAudioArchive.Analysis/Decode/SpeechResampler.cs`, `src/WebAudioArchive.Analysis/Decode/SegmentDecoder.cs`
- Create: `tests/WebAudioArchive.Tests/Support/TestMp3.cs`
- Test: `tests/WebAudioArchive.Tests/SegmentDecoderTests.cs`

Decision D18: no ffmpeg. The decoder is managed code from NuGet (`NLayer` 3.0.0 frame decoder, `NAudio.Core` 3.1.0 resampler, referenced in Task 1), and test MP3s are made by `GroovyMp3` 0.1.3 in the test process. Nothing to install, nothing to add to CI.

**Interfaces:**
- Consumes: `FeatureExtractor`, `AudioFeatures` (Task 5), `WavFile` (Task 4), `DeadAirDetector`, `ClippingDetector` (Tasks 5, 6), `Signals`.
- Produces:
  - `sealed class ScratchFullException(string message)`; `sealed class ScratchSpace(string root, long maxBytes) { string Root; long MaxBytes; long UsedBytes(); int SweepStale(); ScratchLease Acquire(string jobName, long expectedBytes); }`; `sealed class ScratchLease : IDisposable { string Dir; string PathFor(string fileName); }`
  - `sealed class Mp3FrameReader(Stream source) { const int SamplesPerFrame = 1152; const int MaxFloatsPerFrame = 2304; int SampleRate; int Channels; long Frames; int SilencedFrames; int RecoveredFrames; long SkippedBytes; int ReadFrame(float[] interleaved); }`
  - `sealed class SpeechResampler(int inputRate, int channels, int outputRate) { long Produced; ReadOnlySpan<float> Push(ReadOnlySpan<float> interleaved); ReadOnlySpan<float> Flush(long total); }`
  - `sealed class DecodeException(string message)`; `sealed record DecodedSegment(string SpeechWavPath, AudioFeatures Features, string Decoder, long Frames, int SilencedFrames, int RecoveredFrames)`; `sealed class SegmentDecoder { const int SpeechRate = 16000; static readonly string DecoderName; static long ScratchBytesFor(long mp3Bytes, double audioSeconds); Task<DecodedSegment> DecodeAsync(string sourceAudio, long expectedBytes, int sampleRate, int channels, ScratchLease lease, CancellationToken ct); }`
  - Test helper `TestMp3 { static Task EncodeMp3Async(string mp3Path, float[] interleaved, int channels, int rate = 44100); static byte[] Encode(float[] interleaved, int channels, int rate = 44100); }`

- [ ] **Step 1: Write the MP3 test helper**

`tests/WebAudioArchive.Tests/Support/TestMp3.cs`:
```csharp
using System.Buffers.Binary;
using System.Reflection;
using GroovyCodecs.Mp3;
using GroovyCodecs.Types;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// 128 kbps MP3 files made from synthetic signals with GroovyMp3 (a managed port of LAME, test project only), in a temp
/// folder, never committed. Nothing to install. LAME's default input scale of 0.95 is set to 1.0 so a planted full-scale
/// run stays at full scale (measured: with 0.95 a hard-clipped tone decodes at a peak of 0.968 and no sample reaches 0.98).
/// </summary>
public static class TestMp3
{
    public static Task EncodeMp3Async(string mp3Path, float[] interleaved, int channels, int rate = 44100)
    {
        File.WriteAllBytes(mp3Path, Encode(interleaved, channels, rate));
        return Task.CompletedTask;
    }

    public static byte[] Encode(float[] interleaved, int channels, int rate = 44100)
    {
        var format = new AudioFormat
        {
            SampleRate = rate,
            Channels = (short)channels,
            BitsPerSample = 16,
            BlockAlign = (short)(channels * 2),
            AverageBytesPerSecond = rate * channels * 2,
            BigEndian = false,
            IsFloatingPoint = false,
        };
        var encoder = new Mp3Encoder(format, 128, channels == 1 ? Mp3Encoder.CHANNEL_MODE_MONO : Mp3Encoder.CHANNEL_MODE_JOINT_STEREO, Mp3Encoder.QUALITY_MIDDLE, false);
        SetFullScale(encoder);

        var pcm = new byte[interleaved.Length * 2];
        for (var i = 0; i < interleaved.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)Math.Round(Math.Clamp(interleaved[i], -1f, 1f) * 32767));

        using var mp3 = new MemoryStream();
        var buffer = new byte[encoder.MP3BufferSize];
        for (var offset = 0; offset < pcm.Length; offset += encoder.PCMBufferSize)
        {
            var n = encoder.EncodeBuffer(pcm, offset, Math.Min(encoder.PCMBufferSize, pcm.Length - offset), buffer);
            mp3.Write(buffer, 0, n);
        }

        mp3.Write(buffer, 0, encoder.EncodeFinish(buffer));
        encoder.Close();
        return mp3.ToArray();
    }

    static void SetFullScale(Mp3Encoder encoder)
    {
        var flags = typeof(Mp3Encoder).GetField("gfp", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(encoder);
        var scale = flags?.GetType().GetField("scale", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.True(scale is not null, "GroovyMp3's internal LAME flags (gfp.scale) were not found; this helper is written for GroovyMp3 0.1.3.");
        scale!.SetValue(flags, 1f);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/SegmentDecoderTests.cs`:
```csharp
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class SegmentDecoderTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("waa-decode-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string ScratchRoot => Path.Combine(_dir, "scratch");

    async Task<string> Mp3(float[] stereo, string name = "080000.mp3")
    {
        var p = Path.Combine(_dir, name);
        await TestMp3.EncodeMp3Async(p, stereo, 2);
        return p;
    }

    async Task<string> Write(string name, byte[] bytes)
    {
        var p = Path.Combine(_dir, name);
        await File.WriteAllBytesAsync(p, bytes);
        return p;
    }

    static async Task<DecodedSegment> DecodeAsync(ScratchSpace scratch, string mp3, double seconds)
    {
        var length = new FileInfo(mp3).Length;
        using var lease = scratch.Acquire(Path.GetFileNameWithoutExtension(mp3), SegmentDecoder.ScratchBytesFor(length, seconds));
        return await new SegmentDecoder().DecodeAsync(mp3, length, 44100, 2, lease, CancellationToken.None);
    }

    [Fact]
    public async Task Decode_gives_16k_mono_speech_and_full_rate_features_from_a_scratch_copy()
    {
        var mp3 = await Mp3(Signals.Stereo(Signals.Concat(Signals.Sine(3), Signals.Silence(4), Signals.Sine(3))));
        var before = await File.ReadAllBytesAsync(mp3);
        var written = File.GetLastWriteTimeUtc(mp3);
        var scratch = new ScratchSpace(ScratchRoot, 512L * 1024 * 1024);
        var decoder = new SegmentDecoder();

        DecodedSegment decoded;
        using (var lease = scratch.Acquire("a/2026/10-16/080000", SegmentDecoder.ScratchBytesFor(before.Length, 10)))
        {
            decoded = await decoder.DecodeAsync(mp3, before.Length, 44100, 2, lease, CancellationToken.None);
            var info = WavFile.ReadInfo(decoded.SpeechWavPath);
            Assert.Equal(16000, info.SampleRate);
            Assert.Equal(1, info.Channels);
            Assert.InRange(info.Seconds, 9.9, 10.1);
            Assert.Equal(decoded.Features.Seconds, info.Seconds, 3);
            Assert.StartsWith(scratch.Root, decoded.SpeechWavPath);
        }

        Assert.InRange(decoded.Features.Seconds, 9.9, 10.1);
        Assert.Equal(2, decoded.Features.Channels);
        Assert.Equal(decoded.Frames * Mp3FrameReader.SamplesPerFrame, decoded.Features.Frames);
        var dead = Assert.Single(DeadAirDetector.Detect(decoded.Features, new DeadAirOptions { MinSeconds = 3 }));
        Assert.InRange(dead.StartSeconds, 2.9, 3.15);
        Assert.InRange(dead.EndSeconds, 6.85, 7.1);
        Assert.StartsWith("NLayer 3.0.0", decoded.Decoder);

        Assert.Equal(before, await File.ReadAllBytesAsync(mp3));
        Assert.Equal(written, File.GetLastWriteTimeUtc(mp3));
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [Fact]
    public async Task Hard_clipping_survives_the_mp3_codec_where_it_was_planted()
    {
        var quiet = Signals.Sine(5, amp: 0.3);
        var loud = Signals.Sine(0.5, amp: 1.4).Select(x => Math.Clamp(x, -1f, 1f)).ToArray();
        var mp3 = await Mp3(Signals.Stereo(Signals.Concat(quiet, loud, Signals.Sine(4.5, amp: 0.3))));

        var decoded = await DecodeAsync(new ScratchSpace(ScratchRoot, 512L * 1024 * 1024), mp3, 10);
        var events = ClippingDetector.Detect(decoded.Features, new ClippingOptions { Threshold = 0.98 }).Events;

        Assert.NotEmpty(events);
        Assert.InRange(events[0].StartSeconds, 4.95, 5.1);
        Assert.All(events, e => Assert.InRange(e.StartSeconds, 4.9, 5.6));
    }

    [Fact]
    public async Task A_file_that_is_not_audio_fails_and_leaves_no_scratch()
    {
        var bad = Path.Combine(_dir, "bad.mp3");
        await File.WriteAllTextAsync(bad, string.Concat(Enumerable.Repeat("this is not audio. ", 300)));
        var scratch = new ScratchSpace(ScratchRoot, 64L * 1024 * 1024);

        var ex = await Assert.ThrowsAsync<DecodeException>(async () =>
        {
            using var lease = scratch.Acquire("bad", 1_000_000);
            await new SegmentDecoder().DecodeAsync(bad, new FileInfo(bad).Length, 44100, 2, lease, CancellationToken.None);
        });
        Assert.Contains("no MPEG audio frames", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [Fact]
    public async Task A_source_whose_size_disagrees_with_its_sidecar_is_refused()
    {
        var mp3 = await Mp3(Signals.Stereo(Signals.Sine(1)));
        var scratch = new ScratchSpace(ScratchRoot, 64L * 1024 * 1024);
        using var lease = scratch.Acquire("size", 1_000_000);

        var ex = await Assert.ThrowsAsync<DecodeException>(() =>
            new SegmentDecoder().DecodeAsync(mp3, new FileInfo(mp3).Length + 1, 44100, 2, lease, CancellationToken.None));
        Assert.Contains("sidecar says", ex.Message);
    }

    [Fact]
    public void The_scratch_budget_refuses_a_job_that_would_not_fit()
    {
        var scratch = new ScratchSpace(ScratchRoot, 1000);

        Assert.Throws<ScratchFullException>(() => scratch.Acquire("big", 2000));
        var first = scratch.Acquire("one", 500);
        Assert.Throws<ScratchFullException>(() => scratch.Acquire("two", 600));
        first.Dispose();
        scratch.Acquire("two", 600).Dispose();
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [Fact]
    public void Leftovers_from_a_crash_are_swept_at_start_up()
    {
        Directory.CreateDirectory(Path.Combine(ScratchRoot, "old-job"));
        File.WriteAllText(Path.Combine(ScratchRoot, "old-job", "speech.wav"), "x");
        File.WriteAllText(Path.Combine(ScratchRoot, "stray.tmp"), "x");

        var scratch = new ScratchSpace(ScratchRoot, 1000);

        Assert.Equal(2, scratch.SweepStale());
        Assert.Empty(Directory.EnumerateFileSystemEntries(scratch.Root));
    }

    [Fact]
    public async Task A_long_stream_keeps_every_frame_through_the_reservoir_wrap()
    {
        // 330 s of the signal that, in the D18 spike, made NLayer's 8 KiB bit-reservoir ring wrap at frame boundaries
        // where the next frame needs the reservoir (NLayer's own MpegFile dropped 3 of its 12,635 frames).
        var random = new Random(7);
        var mono = new float[330 * Signals.Rate];
        for (var i = 0; i < mono.Length; i++)
        {
            var t = i / (double)Signals.Rate;
            var envelope = 0.5 + 0.5 * Math.Sin(2 * Math.PI * 3 * t);
            mono[i] = (float)(0.25 * envelope * (Math.Sin(2 * Math.PI * 180 * t) + 0.5 * Math.Sin(2 * Math.PI * 1300 * t)) + 0.05 * (random.NextDouble() - 0.5));
        }

        var mp3 = await Mp3(Signals.Stereo(mono), "long.mp3");
        var decoded = await DecodeAsync(new ScratchSpace(ScratchRoot, 512L * 1024 * 1024), mp3, 330);

        Assert.True(decoded.RecoveredFrames >= 1, "the stream never hit NLayer's reservoir wrap, so the recovery path was not exercised");
        Assert.Equal(0, decoded.SilencedFrames);
        Assert.Equal(decoded.Frames * Mp3FrameReader.SamplesPerFrame, decoded.Features.Frames);
        Assert.InRange(decoded.Features.Seconds, 330, 330.1);
    }

    [Fact]
    public async Task Stray_bytes_and_a_corrupt_frame_keep_the_timeline()
    {
        var clean = await File.ReadAllBytesAsync(await Mp3(Signals.Stereo(Signals.SpeechLike(10, 4)), "clean.mp3"));
        var scratch = new ScratchSpace(ScratchRoot, 512L * 1024 * 1024);
        var reference = await DecodeAsync(scratch, await Write("reference.mp3", clean), 10);

        // 1000 bytes of false frame headers (FF FB 90 00) in front: no second header follows any of them, so all are skipped.
        var junk = Enumerable.Repeat(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 250).SelectMany(b => b).ToArray();
        var damaged = junk.Concat(clean).ToArray();
        // and the audio data of the 101st frame zeroed, its header and side information kept (128 kbps frames are 417 or 418 bytes)
        var at = 0;
        for (var k = 0; k < 100; k++) at += 417 + ((clean[at + 2] >> 1) & 1);
        Array.Clear(damaged, junk.Length + at + 40, 300);

        var decoded = await DecodeAsync(scratch, await Write("damaged.mp3", damaged), 10);

        Assert.Equal(reference.Frames, decoded.Frames);
        Assert.Equal(reference.Features.Frames, decoded.Features.Frames);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SegmentDecoderTests`
Expected: build FAILS with `CS0246` for `ScratchSpace`, `Mp3FrameReader`, `SegmentDecoder`, `DecodedSegment`.

- [ ] **Step 4: Implement scratch, the frame reader, the resampler and the decoder**

`src/WebAudioArchive.Analysis/Decode/ScratchSpace.cs`:
```csharp
using System.Text.RegularExpressions;

namespace WebAudioArchive.Analysis.Decode;

public sealed class ScratchFullException(string message) : Exception(message);

/// <summary>
/// The only place decoded audio is written: a size-bounded folder (a tmpfs in the container). Each job gets its own
/// folder, deleted when the lease is disposed, success or failure. The budget counts files on disk plus what running
/// jobs have reserved, so a job that would overflow it is refused before it starts.
/// </summary>
public sealed partial class ScratchSpace
{
    readonly object _gate = new();
    readonly Dictionary<string, long> _reserved = new();

    public ScratchSpace(string root, long maxBytes)
    {
        Root = Path.GetFullPath(root);
        MaxBytes = maxBytes;
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    public long MaxBytes { get; }

    public long UsedBytes() => Directory.Exists(Root)
        ? Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Sum(f =>
        {
            try
            {
                return new FileInfo(f).Length;
            }
            catch (IOException)
            {
                return 0L;
            }
        })
        : 0;

    /// <summary>Deletes everything under the root. Call at start-up, before any job runs. Returns how many entries went.</summary>
    public int SweepStale()
    {
        var n = 0;
        foreach (var d in Directory.EnumerateDirectories(Root))
        {
            TryDelete(d);
            n++;
        }

        foreach (var f in Directory.EnumerateFiles(Root))
        {
            try
            {
                File.Delete(f);
            }
            catch (IOException)
            {
            }

            n++;
        }

        return n;
    }

    public ScratchLease Acquire(string jobName, long expectedBytes)
    {
        var name = Unsafe().Replace(jobName, "_") + "-" + Guid.NewGuid().ToString("N")[..8];
        lock (_gate)
        {
            var wanted = UsedBytes() + _reserved.Values.Sum() + expectedBytes;
            if (wanted > MaxBytes)
                throw new ScratchFullException($"scratch {Root} would need {wanted / 1048576.0:0.0} MB, over its {MaxBytes / 1048576.0:0.0} MB limit (WAA_SCRATCH_MAX_MB)");
            _reserved[name] = expectedBytes;
        }

        var dir = Path.Combine(Root, name);
        Directory.CreateDirectory(dir);
        return new ScratchLease(dir, () =>
        {
            TryDelete(dir);
            lock (_gate) _reserved.Remove(name);
        });
    }

    static void TryDelete(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("[^A-Za-z0-9-]")]
    private static partial Regex Unsafe();
}

public sealed class ScratchLease : IDisposable
{
    readonly Action _release;
    bool _disposed;

    internal ScratchLease(string dir, Action release)
    {
        Dir = dir;
        _release = release;
    }

    public string Dir { get; }

    public string PathFor(string fileName) => Path.Combine(Dir, fileName);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _release();
    }
}
```

`src/WebAudioArchive.Analysis/Decode/Mp3FrameReader.cs`:
```csharp
using NLayer;

namespace WebAudioArchive.Analysis.Decode;

/// <summary>
/// Reads an MPEG-1 Layer III stream (the recorders' format) one frame at a time and decodes each frame with NLayer's
/// managed frame decoder. Every frame header found gives exactly 1152 samples per channel, so the decoded length is
/// always frames x 1152, the count the sidecar records. A frame that cannot be decoded is written as silence (its
/// place on the timeline is kept). NLayer 3.0.0's bit reservoir refuses a frame when its 8 KiB ring buffer wraps
/// exactly at a frame boundary (about once every 4.7 minutes at 128 kbps, measured on a real Burst hour); such a frame
/// is decoded exactly by a fresh decoder primed with the previous frames, whose output is thrown away.
/// NLayer's own MpegFile is not used: it skips those frames, shortening an hour by about 0.47 s.
/// </summary>
public sealed class Mp3FrameReader
{
    public const int SamplesPerFrame = 1152;

    /// <summary>The largest frame NLayer can return: 1152 stereo samples.</summary>
    public const int MaxFloatsPerFrame = SamplesPerFrame * 2;

    const int MaxFrameBytes = 1441 + 4;
    const int KeepFrames = 4;

    readonly Stream _source;
    readonly byte[] _buffer = new byte[1 << 16];
    readonly Queue<byte[]> _recent = new();
    readonly RawFrame _frame = new();
    int _have;
    int _pos;
    bool _eof;
    bool _inSync;
    MpegFrameDecoder _decoder = new();

    public Mp3FrameReader(Stream source) => _source = source;

    public int SampleRate { get; private set; }

    public int Channels { get; private set; }

    public long Frames { get; private set; }

    /// <summary>Frames NLayer could not decode, written as silence (usually only the first, whose bit reservoir is in the previous file).</summary>
    public int SilencedFrames { get; private set; }

    /// <summary>Frames refused by NLayer's ring-buffer wrap and decoded by a primed fresh decoder.</summary>
    public int RecoveredFrames { get; private set; }

    public long SkippedBytes { get; private set; }

    /// <summary>Decodes the next frame into <paramref name="interleaved"/> (at least MaxFloatsPerFrame long). Returns the floats written, 0 at the end.</summary>
    public int ReadFrame(float[] interleaved)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(interleaved.Length, MaxFloatsPerFrame);
        while (true)
        {
            Fill();
            var available = _have - _pos;
            if (available < 4) return 0;
            var span = _buffer.AsSpan(_pos, available);
            var length = RawFrame.LengthAt(span);
            // Out of sync, a header counts only when another header follows it (or the file ends), so stray 0xFFF bytes are skipped.
            var fits = length > 0 && length <= available;
            if (!fits || !(_inSync || (length + 4 <= available ? RawFrame.LengthAt(span[length..]) > 0 : _eof)))
            {
                if (fits || length == 0 || !_eof)
                {
                    _pos++;
                    SkippedBytes++;
                    _inSync = false;
                    continue;
                }

                return 0;
            }

            var bytes = span[..length].ToArray();
            _pos += length;
            _inSync = true;
            return Decode(bytes, interleaved);
        }
    }

    int Decode(byte[] bytes, float[] interleaved)
    {
        _frame.Load(bytes);
        var channels = _frame.ChannelMode == MpegChannelMode.Mono ? 1 : 2;
        if (Frames == 0)
        {
            SampleRate = _frame.SampleRate;
            Channels = channels;
        }

        var expected = SamplesPerFrame * Channels;
        var got = channels == Channels ? TryDecode(_decoder, _frame, interleaved) : 0;
        if (got != expected && channels == Channels && _recent.Count > 0)
        {
            for (var prime = 2; prime <= KeepFrames && got != expected; prime++)
            {
                _decoder = new MpegFrameDecoder();
                var discard = new float[MaxFloatsPerFrame];
                foreach (var previous in _recent.Skip(Math.Max(0, _recent.Count - prime)))
                {
                    var p = new RawFrame();
                    p.Load(previous);
                    TryDecode(_decoder, p, discard);
                }

                _frame.Load(bytes);
                got = TryDecode(_decoder, _frame, interleaved);
            }

            if (got == expected) RecoveredFrames++;
        }

        _recent.Enqueue(bytes);
        if (_recent.Count > KeepFrames) _recent.Dequeue();
        if (got != expected)
        {
            Array.Clear(interleaved, 0, expected);
            SilencedFrames++;
        }

        Frames++;
        return expected;
    }

    static int TryDecode(MpegFrameDecoder decoder, RawFrame frame, float[] dest)
    {
        try
        {
            return decoder.DecodeFrame(frame, dest, 0);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }

    void Fill()
    {
        if (_eof || _have - _pos >= 2 * MaxFrameBytes) return;
        Buffer.BlockCopy(_buffer, _pos, _buffer, 0, _have - _pos);
        _have -= _pos;
        _pos = 0;
        while (_have < _buffer.Length)
        {
            var n = _source.Read(_buffer, _have, _buffer.Length - _have);
            if (n == 0)
            {
                _eof = true;
                break;
            }

            _have += n;
        }
    }

    /// <summary>One frame's bytes as NLayer's IMpegFrame: header fields, and the bits after the header and CRC.</summary>
    sealed class RawFrame : IMpegFrame
    {
        static readonly int[] Kbps = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        static readonly int[] Rates = [44100, 48000, 32000, 0];
        byte[] _data = [];
        int _offset;
        int _bits;
        ulong _bucket;

        /// <summary>The frame length for an MPEG-1 Layer III header at the start of <paramref name="b"/>, or 0.</summary>
        public static int LengthAt(ReadOnlySpan<byte> b)
        {
            if (b.Length < 4 || b[0] != 0xFF || (b[1] & 0xFE) != 0xFA) return 0;
            var kbps = Kbps[b[2] >> 4];
            var rate = Rates[(b[2] >> 2) & 3];
            if (kbps == 0 || rate == 0) return 0;
            return 144 * kbps * 1000 / rate + ((b[2] >> 1) & 1);
        }

        public void Load(byte[] data)
        {
            _data = data;
            FrameLength = data.Length;
            BitRateIndex = data[2] >> 4;
            BitRate = Kbps[BitRateIndex] * 1000;
            SampleRateIndex = (data[2] >> 2) & 3;
            SampleRate = Rates[SampleRateIndex];
            HasCrc = (data[1] & 1) == 0;
            ChannelMode = (MpegChannelMode)(data[3] >> 6);
            ChannelModeExtension = (data[3] >> 4) & 3;
            IsCopyrighted = (data[3] & 8) != 0;
            Reset();
        }

        public int SampleRate { get; private set; }

        public int SampleRateIndex { get; private set; }

        public int FrameLength { get; private set; }

        public int BitRate { get; private set; }

        public MpegVersion Version => MpegVersion.Version1;

        public MpegLayer Layer => MpegLayer.LayerIII;

        public MpegChannelMode ChannelMode { get; private set; }

        public int ChannelModeExtension { get; private set; }

        public int SampleCount => SamplesPerFrame;

        public int BitRateIndex { get; private set; }

        public bool IsCopyrighted { get; private set; }

        public bool HasCrc { get; private set; }

        public bool IsCorrupted => false;

        public void Reset()
        {
            _offset = HasCrc ? 6 : 4;
            _bucket = 0;
            _bits = 0;
        }

        public int ReadBits(int bitCount)
        {
            while (_bits < bitCount)
            {
                if (_offset >= FrameLength) return -1;
                _bucket = (_bucket << 8) | _data[_offset++];
                _bits += 8;
            }

            var value = (int)((_bucket >> (_bits - bitCount)) & ((1UL << bitCount) - 1));
            _bits -= bitCount;
            return value;
        }
    }
}
```

`src/WebAudioArchive.Analysis/Decode/SpeechResampler.cs`:
```csharp
using NAudio.Dsp;

namespace WebAudioArchive.Analysis.Decode;

/// <summary>
/// Interleaved full-rate float PCM to 16 kHz mono for whisper: channels averaged, then NAudio's WDL resampler in sinc
/// mode (64 taps). Measured: level within 0.1 dB up to 6 kHz, -1.5 dB at 7 kHz, and tones that would alias attenuated
/// by 67 dB at 10 kHz and 114 dB at 12 kHz. (WdlResamplingSampleProvider's default, linear interpolation with IIR
/// filters, loses 8 dB at 6 kHz and lets a 10 kHz tone through at -27 dB, so it is not used.)
/// </summary>
public sealed class SpeechResampler
{
    readonly WdlResampler _wdl = new();
    readonly int _channels;
    readonly double _ratio;
    float[] _out = new float[4096];

    public SpeechResampler(int inputRate, int channels, int outputRate)
    {
        _channels = channels;
        _ratio = (double)outputRate / inputRate;
        _wdl.SetMode(true, 0, true, 64, 32);
        _wdl.SetFeedMode(true);
        _wdl.SetRates(inputRate, outputRate);
    }

    /// <summary>Samples returned so far.</summary>
    public long Produced { get; private set; }

    /// <summary>Takes interleaved frames and returns the mono output they produced (valid until the next call).</summary>
    public ReadOnlySpan<float> Push(ReadOnlySpan<float> interleaved)
    {
        var frames = interleaved.Length / _channels;
        if (frames == 0) return [];
        var wanted = _wdl.ResamplePrepare(frames, 1, out var input);
        for (var i = 0; i < wanted; i++)
        {
            var sum = 0f;
            for (var c = 0; c < _channels; c++) sum += interleaved[i * _channels + c];
            input[i] = sum / _channels;
        }

        var capacity = (int)(frames * _ratio) + 64;
        if (_out.Length < capacity) _out = new float[capacity * 2];
        var got = _wdl.ResampleOut(_out, wanted, capacity, 1);
        Produced += got;
        return _out.AsSpan(0, got);
    }

    /// <summary>Feeds silence until <paramref name="total"/> samples exist (the filter delay), returning only the samples still owed.</summary>
    public ReadOnlySpan<float> Flush(long total)
    {
        var owed = new List<float>();
        var silence = new float[256 * _channels];
        while (Produced < total)
        {
            var before = Produced;
            var chunk = Push(silence);
            var keep = (int)Math.Min(chunk.Length, total - before);
            for (var i = 0; i < keep; i++) owed.Add(chunk[i]);
            if (Produced >= total) Produced = total;
        }

        return owed.ToArray();
    }
}
```

`src/WebAudioArchive.Analysis/Decode/SegmentDecoder.cs`:
```csharp
using System.Buffers.Binary;
using System.Reflection;
using NAudio.Dsp;
using NLayer;
using WebAudioArchive.Analysis.Audio;

namespace WebAudioArchive.Analysis.Decode;

public sealed class DecodeException(string message) : Exception(message);

/// <summary>What one decode produced. <see cref="Decoder"/> (library versions) is recorded in every transcript.</summary>
public sealed record DecodedSegment(string SpeechWavPath, AudioFeatures Features, string Decoder, long Frames, int SilencedFrames, int RecoveredFrames);

/// <summary>
/// Copies a closed master segment into the job's scratch folder (the archive file is only ever opened for reading,
/// sharing read and write so nothing else is blocked), checks the copy against the sidecar's byte count, then decodes
/// the copy once, in managed code (decision D18): each frame's full-rate float PCM goes into the feature extractor for
/// the audio detectors (never written), and its 16 kHz mono resampling into a 16-bit WAV in scratch for whisper.
/// </summary>
public sealed class SegmentDecoder
{
    public const int SpeechRate = 16000;

    public static readonly string DecoderName =
        $"NLayer {Version(typeof(MpegFrameDecoder))} frame decoder, NAudio.Core {Version(typeof(WdlResampler))} WDL sinc resampler";

    /// <summary>Scratch needed for one segment: the copy, the speech WAV (32000 bytes a second) and 1 MB to spare.</summary>
    public static long ScratchBytesFor(long mp3Bytes, double audioSeconds) =>
        mp3Bytes + (long)Math.Ceiling(Math.Max(0, audioSeconds) + 10) * SpeechRate * 2 + 1_000_000;

    public async Task<DecodedSegment> DecodeAsync(string sourceAudio, long expectedBytes, int sampleRate, int channels, ScratchLease lease, CancellationToken ct)
    {
        var copy = lease.PathFor("source.mp3");
        await using (var src = new FileStream(sourceAudio, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, useAsync: true))
        await using (var dst = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
        {
            await src.CopyToAsync(dst, ct);
        }

        var length = new FileInfo(copy).Length;
        if (length != expectedBytes)
            throw new DecodeException($"{sourceAudio} is {length} bytes but its sidecar says {expectedBytes}; it is retried when they agree");

        var wav = lease.PathFor("speech.wav");
        return await Task.Run(() => Decode(sourceAudio, copy, wav, sampleRate, channels, ct), ct);
    }

    static DecodedSegment Decode(string sourceAudio, string copy, string wav, int sampleRate, int channels, CancellationToken ct)
    {
        using var input = new FileStream(copy, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        using var output = new FileStream(wav, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        output.Write(WavFile.Build(SpeechRate, 1, []));

        var reader = new Mp3FrameReader(input);
        var features = new FeatureExtractor(sampleRate, channels);
        var resampler = new SpeechResampler(sampleRate, channels, SpeechRate);
        var pcm = new float[Mp3FrameReader.MaxFloatsPerFrame];
        var bytes = new byte[8192];
        long written = 0;
        int n;
        while ((n = reader.ReadFrame(pcm)) > 0)
        {
            if (reader.Frames == 1 && (reader.SampleRate != sampleRate || reader.Channels != channels))
                throw new DecodeException($"{sourceAudio} is {reader.SampleRate} Hz with {reader.Channels} channel(s) but its sidecar says {sampleRate} Hz with {channels}");
            if (reader.Frames % 256 == 0) ct.ThrowIfCancellationRequested();
            features.Push(pcm.AsSpan(0, n));
            written += WritePcm16(output, resampler.Push(pcm.AsSpan(0, n)), bytes);
        }

        if (reader.Frames == 0) throw new DecodeException($"{sourceAudio}: no MPEG audio frames found (not an MP3, or empty)");
        var total = (long)Math.Round(reader.Frames * (double)Mp3FrameReader.SamplesPerFrame * SpeechRate / sampleRate);
        written += WritePcm16(output, resampler.Flush(total), bytes);

        var header = new byte[4];
        output.Position = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(36 + written * 2));
        output.Write(header);
        output.Position = 40;
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(written * 2));
        output.Write(header);

        return new DecodedSegment(wav, features.Complete(), DecoderName, reader.Frames, reader.SilencedFrames, reader.RecoveredFrames);
    }

    static long WritePcm16(Stream output, ReadOnlySpan<float> mono, byte[] bytes)
    {
        for (var start = 0; start < mono.Length; start += bytes.Length / 2)
        {
            var count = Math.Min(mono.Length - start, bytes.Length / 2);
            for (var i = 0; i < count; i++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)Math.Round(Math.Clamp(mono[start + i], -1f, 1f) * 32767));
            output.Write(bytes, 0, count * 2);
        }

        return mono.Length;
    }

    static string Version(Type t) =>
        (t.Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? t.Assembly.GetName().Version?.ToString() ?? "?").Split('+')[0];
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SegmentDecoderTests`
Expected: PASS, 8 tests, in about 15 s (encoding the 330 s stream is most of it). These are the files the D18 spike ran: the same 8 tests passed there against the same code. If `Hard_clipping_survives...` finds no event, print `decoded.Features.Clips` counts per level: at 0.98 the flattened 1.4-amplitude tone must leave hundreds of runs (882 in the spike); none means the test helper's full-scale setting did not apply (GroovyMp3 then scales by 0.95 and the peak stays near 0.968). If `A_long_stream...` reports no recovered frame, check that `Mp3FrameReader` is used and not NLayer's `MpegFile`.

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Decode tests/WebAudioArchive.Tests/Support/TestMp3.cs tests/WebAudioArchive.Tests/SegmentDecoderTests.cs
git commit -m "Analysis: bounded scratch space and managed decode (NLayer, NAudio) of a scratch copy only (#76)"
```

---

### Task 10: Transcriber interface, Whisper.net transcriber, hallucination filter, chunks and prompt

**Files:**
- Create: `src/WebAudioArchive.Analysis/Transcription/ITranscriber.cs`, `WhisperResponse.cs`, `WhisperNetTranscriber.cs`, `TranscriptFilter.cs`, `ChunkPlanner.cs`, `PromptBuilder.cs` (all under `src/WebAudioArchive.Analysis/Transcription/`)

Decision D18: whisper.cpp runs in this process through `Whisper.net` 1.9.1 (referenced in Task 1); the native CPU and CUDA runtimes are referenced by the service project only, so these tests never load native code or need a model.
- Create: `tests/WebAudioArchive.Tests/Support/FakeTranscriber.cs`
- Test: `tests/WebAudioArchive.Tests/TranscriptionTests.cs`

**Interfaces:**
- Consumes: `TranscriptWord` (Task 3), `TranscriptionOptions` (Task 2), `WordNormaliser` (Task 7), `AudioFeatures` (Task 5), `WhisperDevice` (Task 1).
- Produces:
  - `sealed record TranscriptionRequest(byte[] Wav, string Prompt, string Language)`; `sealed record RawSegment(double Start, double End, string Text, double NoSpeechProb, double AvgLogProb, IReadOnlyList<TranscriptWord> Words)` with `RawSegment Shift(double seconds)`.
  - `interface ITranscriber { string Engine { get; } string Model { get; } Task<bool> IsReadyAsync(CancellationToken ct); Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct); }`
  - `sealed class TranscriberUnavailableException(string message, Exception? inner = null)` (retry later, not a job failure); `sealed class TranscriptionFailedException(string message)` (this input failed).
  - `static class WhisperResponse { RawSegment FromSegment(Whisper.net.SegmentData s); readonly record struct Token(string Text, double Start, double End, double Dtw, double Probability); IReadOnlyList<TranscriptWord> MergeTokens(IEnumerable<Token>); }` (token `Start` and `End` in seconds, `Dtw` in centiseconds or -1).
  - `sealed class WhisperNetTranscriber(string modelPath, WhisperDevice device, int threads, bool dtw) : ITranscriber, IDisposable { string? LastError; static IReadOnlyList<RuntimeLibrary> RuntimeOrder(WhisperDevice); static WhisperAlignmentHeadsPreset? HeadsFor(string model); }` (Engine `whisper.net 1.9.1 (cuda)` or `(cpu)` once loaded; Model is the model file name without extension).
  - `static class TranscriptFilter { const string NonSpeech, NoSpeech, KnownHallucination, Repetition; string?[] Reasons(IReadOnlyList<(string Text, double NoSpeechProb, double AvgLogProb)> segments, TranscriptionOptions o); string Key(string text); }`
  - `sealed record AudioChunk(int Index, double Start, double End)`; `static class ChunkPlanner { IReadOnlyList<AudioChunk> Plan(AudioFeatures f, double chunkSeconds, double searchSeconds); }`
  - `static class PromptBuilder { const int MaxChars = 600; string Build(TranscriptionOptions o, string? showTitle); }`
  - Test helper `FakeTranscriber : ITranscriber` with `Func<TranscriptionRequest, int, IReadOnlyList<RawSegment>> Respond`, `int Calls`, `List<TranscriptionRequest> Requests`, `bool Ready`.

- [ ] **Step 1: Write the fake transcriber**

`tests/WebAudioArchive.Tests/Support/FakeTranscriber.cs`:
```csharp
using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// A transcriber for tests: Respond gets each request and its call number (0 based) and returns the segments, or throws
/// to simulate whisper being down (TranscriberUnavailableException), a bad input, or a crash.
/// </summary>
public sealed class FakeTranscriber : ITranscriber
{
    public Func<TranscriptionRequest, int, IReadOnlyList<RawSegment>> Respond { get; set; } = (_, _) => [];

    public List<TranscriptionRequest> Requests { get; } = [];

    public int Calls => Requests.Count;

    public bool Ready { get; set; } = true;

    public string Engine => "fake";

    public string Model { get; set; } = "fake-model";

    public Task<bool> IsReadyAsync(CancellationToken ct) => Task.FromResult(Ready);

    public Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var n = Requests.Count;
        Requests.Add(request);
        return Task.FromResult(Respond(request, n));
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/TranscriptionTests.cs`:
```csharp
using Whisper.net;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Tests;

public class TranscriptionTests
{
    static TranscriptionRequest Request => new(WavFile.Build(16000, 1, new byte[3200]), "Um, er, Burst Radio.", "en");

    /// <summary>A Whisper.net segment as the library returns it (built without the native library). Token times are centiseconds.</summary>
    static SegmentData Segment(double start, double end, string text, float noSpeech, params WhisperToken[] tokens) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(end), 0.1f, 1f, 0.9f, noSpeech, "en", tokens);

    static WhisperToken Token(string text, long startCs, long endCs, long dtwCs, float p, float logP) =>
        new() { Text = text, Start = startCs, End = endCs, DtwTimestamp = dtwCs, Probability = p, ProbabilityLog = logP };

    [Fact]
    public void Tokens_merge_into_words_with_dtw_start_punctuation_and_lowest_probability()
    {
        var seg = WhisperResponse.FromSegment(Segment(0.0, 2.4, " Um, welcome to Burst.", 0.02f,
            Token("[_BEG_]", 0, 0, -1, 0.8f, -0.22f),
            Token(" Um", 10, 30, 12, 0.80f, -0.22f),
            Token(",", 30, 32, -1, 0.95f, -0.05f),
            Token(" wel", 50, 70, -1, 0.90f, -0.11f),
            Token("come", 70, 90, -1, 0.70f, -0.36f),
            Token(" to", 100, 110, -1, 0.99f, -0.01f),
            Token(" Burst.", 120, 160, -1, 0.97f, -0.03f),
            Token("[_TT_120]", 160, 160, -1, 0.1f, -2.3f)));

        Assert.Equal("Um, welcome to Burst.", seg.Text);
        Assert.Equal(new[] { "Um,", "welcome", "to", "Burst." }, seg.Words.Select(w => w.Text));
        Assert.Equal(0.12, seg.Words[0].Start, 6);
        Assert.Equal(0.32, seg.Words[0].End, 6);
        Assert.Equal(0.70, seg.Words[1].Probability, 6);
        Assert.Equal(0.02, seg.NoSpeechProb, 6);
        Assert.Equal(-0.13, seg.AvgLogProb, 4);
    }

    [Fact]
    public void Special_tokens_are_dropped_and_shift_moves_every_time()
    {
        var words = WhisperResponse.MergeTokens([
            new(" [_BEG_]", 0, 0, -1, 1), new(" Hi", 0.1, 0.2, -1, 0.9), new("<|endoftext|>", 0.2, 0.2, -1, 1)]);
        Assert.Equal("Hi", Assert.Single(words).Text);

        var shifted = new RawSegment(1, 2, "Hi", 0, 0, words).Shift(300);
        Assert.Equal(301, shifted.Start);
        Assert.Equal(300.1, shifted.Words[0].Start, 6);
    }

    [Fact]
    public async Task A_missing_model_is_not_ready_and_transcribing_waits_instead_of_failing()
    {
        using var t = new WhisperNetTranscriber(Path.Combine(Path.GetTempPath(), "no-such-model-waa.bin"), WhisperDevice.Auto, 4, dtw: false);

        Assert.False(await t.IsReadyAsync(CancellationToken.None));
        Assert.Contains("WAA_WHISPER_MODEL_PATH", t.LastError);
        await Assert.ThrowsAsync<TranscriberUnavailableException>(() => t.TranscribeAsync(Request, CancellationToken.None));
        Assert.Equal("no-such-model-waa", t.Model);
        Assert.StartsWith("whisper.net 1.9.1", t.Engine);
    }

    [Theory]
    [InlineData(WhisperDevice.Auto, "Cuda,Cpu")]
    [InlineData(WhisperDevice.Cpu, "Cpu")]
    [InlineData(WhisperDevice.Cuda, "Cuda")]
    public void Runtime_order_follows_the_device_setting(WhisperDevice device, string expected) =>
        Assert.Equal(expected, string.Join(',', WhisperNetTranscriber.RuntimeOrder(device)));

    [Theory]
    [InlineData("ggml-large-v3-turbo-q8_0", "LargeV3Turbo")]
    [InlineData("ggml-small.en", "SmallEn")]
    [InlineData("ggml-base.en", "BaseEn")]
    [InlineData("ggml-medium", null)]
    public void Dtw_heads_follow_the_model_file(string model, string? expected) =>
        Assert.Equal(expected, WhisperNetTranscriber.HeadsFor(model)?.ToString());

    [Fact]
    public void Known_hallucinations_annotations_and_repeats_are_suppressed()
    {
        var o = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json")).Config.Transcription;
        (string, double, double)[] segments =
        [
            ("[BLANK_AUDIO]", 0.1, -0.2),
            ("(music)", 0.1, -0.2),
            ("Thank you for watching!", 0.1, -0.2),
            ("Hello and welcome", 0.9, -1.5),
            ("Hello and welcome", 0.1, -0.2),
            ("Ooh baby", 0.1, -0.3),
            ("Ooh, baby!", 0.1, -0.3),
            ("ooh baby", 0.1, -0.3),
            ("Thank you.", 0.1, -0.2),
        ];

        var reasons = TranscriptFilter.Reasons(segments, o);

        Assert.Equal(new string?[]
        {
            TranscriptFilter.NonSpeech, TranscriptFilter.NonSpeech, TranscriptFilter.KnownHallucination, TranscriptFilter.NoSpeech,
            null, TranscriptFilter.Repetition, TranscriptFilter.Repetition, TranscriptFilter.Repetition, null,
        }, reasons);
    }

    static AudioFeatures Levels(double seconds, params (double At, short Tenths)[] quiet)
    {
        var n = (int)Math.Round(seconds / 0.05);
        var tenths = Enumerable.Repeat((short)-200, n).ToArray();
        foreach (var (at, value) in quiet) tenths[(int)Math.Round(at / 0.05)] = value;
        return new AudioFeatures(AudioFeatures.CurrentSchema, 44100, 1, (long)(seconds * 44100), 0.05, tenths, []);
    }

    [Fact]
    public void Chunks_end_at_the_quietest_moment_near_the_nominal_boundary()
    {
        var chunks = ChunkPlanner.Plan(Levels(20, (9.3, -600)), 10, 2);

        Assert.Equal(2, chunks.Count);
        Assert.Equal(9.3, chunks[0].End, 6);
        Assert.Equal(9.3, chunks[1].Start, 6);
        Assert.Equal(20, chunks[1].End, 6);
    }

    [Fact]
    public void Even_audio_is_cut_at_the_nominal_boundaries_and_short_audio_is_one_chunk()
    {
        var chunks = ChunkPlanner.Plan(Levels(35), 10, 2);
        Assert.Equal(new[] { 0.0, 10, 20, 30 }, chunks.Select(c => Math.Round(c.Start, 6)));
        Assert.Equal(35, chunks[^1].End, 6);
        Assert.Equal(new[] { 0, 1, 2, 3 }, chunks.Select(c => c.Index));

        Assert.Single(ChunkPlanner.Plan(Levels(5), 10, 2));
        Assert.Empty(ChunkPlanner.Plan(Levels(0), 10, 2));
    }

    [Fact]
    public void The_prompt_primes_fillers_and_names_without_repeats_and_is_bounded()
    {
        var o = new TranscriptionOptions { VerbatimPrompt = "Um, er, right.", Glossary = ["Burst Radio", "Backseat Aux"] };

        Assert.Equal("Um, er, right. Burst Radio, Backseat Aux.", PromptBuilder.Build(o, "Backseat Aux"));
        Assert.Equal("Um, er, right. Burst Radio, Backseat Aux, Breakfast with Mackenzie.", PromptBuilder.Build(o, "Breakfast with Mackenzie"));
        Assert.Equal("Um, er, right.", PromptBuilder.Build(o with { Glossary = [] }, null));
        Assert.Equal(PromptBuilder.MaxChars, PromptBuilder.Build(o, new string('x', 2000)).Length);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~TranscriptionTests`
Expected: build FAILS with `CS0246` for `WhisperResponse`, `WhisperNetTranscriber`, `TranscriptFilter`, `ChunkPlanner`, `PromptBuilder`, `ITranscriber`.

- [ ] **Step 4: Implement the transcription pieces**

`src/WebAudioArchive.Analysis/Transcription/ITranscriber.cs`:
```csharp
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>One chunk of 16 kHz mono 16-bit WAV, the priming prompt and the language.</summary>
public sealed record TranscriptionRequest(byte[] Wav, string Prompt, string Language);

/// <summary>A segment as the engine returned it. Times are seconds from the start of the request's audio until shifted.</summary>
public sealed record RawSegment(double Start, double End, string Text, double NoSpeechProb, double AvgLogProb, IReadOnlyList<TranscriptWord> Words)
{
    public RawSegment Shift(double seconds) => this with
    {
        Start = Start + seconds,
        End = End + seconds,
        Words = Words.Select(w => w with { Start = w.Start + seconds, End = w.End + seconds }).ToList(),
    };
}

/// <summary>
/// Speech to text, kept behind this interface so the engine can change (whisper.cpp through Whisper.net today) and tests use a fake.
/// TranscribeAsync throws <see cref="TranscriberUnavailableException"/> when the engine cannot be reached (the job waits
/// and retries, it is not failed) and <see cref="TranscriptionFailedException"/> when this input cannot be transcribed.
/// </summary>
public interface ITranscriber
{
    string Engine { get; }

    string Model { get; }

    Task<bool> IsReadyAsync(CancellationToken ct);

    Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct);
}

public sealed class TranscriberUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class TranscriptionFailedException(string message) : Exception(message);
```

`src/WebAudioArchive.Analysis/Transcription/WhisperResponse.cs`:
```csharp
using Whisper.net;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// Turns Whisper.net's segments into <see cref="RawSegment"/>s. Whisper.net gives per token the text, start and end in
/// centiseconds, the DTW time in centiseconds (-1 when DTW is off), the probability and its log; per segment the
/// no-speech probability. avg_logprob is the mean log probability of the segment's text tokens, as whisper.cpp's
/// server computes it.
/// </summary>
public static class WhisperResponse
{
    public readonly record struct Token(string Text, double Start, double End, double Dtw, double Probability);

    public static RawSegment FromSegment(SegmentData s)
    {
        var start = s.Start.TotalSeconds;
        var end = Math.Max(start, s.End.TotalSeconds);
        var tokens = (s.Tokens ?? []).Select(t => new Token(t.Text ?? "", t.Start / 100.0, t.End / 100.0, t.DtwTimestamp, t.Probability)).ToList();
        var logs = (s.Tokens ?? []).Where(t => !IsSpecial((t.Text ?? "").Trim())).Select(t => (double)t.ProbabilityLog).ToList();
        var avgLogProb = logs.Count == 0 ? 0 : Math.Round(logs.Average(), 4);
        return new RawSegment(start, end, (s.Text ?? "").Trim(), Math.Round(s.NoSpeechProbability, 4), avgLogProb, MergeTokens(tokens));
    }

    /// <summary>A token that starts with a space starts a new word; others continue it ("wel" "come" ","). A known DTW time (centiseconds) gives the word's start.</summary>
    public static IReadOnlyList<TranscriptWord> MergeTokens(IEnumerable<Token> tokens)
    {
        var words = new List<TranscriptWord>();
        string? text = null;
        double start = 0, end = 0, p = 1;
        foreach (var t in tokens)
        {
            var trimmed = t.Text.Trim();
            if (trimmed.Length == 0 || IsSpecial(trimmed)) continue;
            var tokenStart = t.Dtw >= 0 ? t.Dtw * 0.01 : t.Start;
            if (text is null || t.Text.StartsWith(' '))
            {
                if (text is not null) words.Add(new TranscriptWord(text, start, Math.Max(start, end), Math.Round(p, 4)));
                (text, start, end, p) = (trimmed, tokenStart, t.End, t.Probability);
            }
            else
            {
                text += trimmed;
                end = Math.Max(end, t.End);
                p = Math.Min(p, t.Probability);
            }
        }

        if (text is not null) words.Add(new TranscriptWord(text, start, Math.Max(start, end), Math.Round(p, 4)));
        return words;
    }

    static bool IsSpecial(string s) =>
        (s.StartsWith("[_", StringComparison.Ordinal) && s.EndsWith(']')) || (s.StartsWith("<|", StringComparison.Ordinal) && s.EndsWith("|>", StringComparison.Ordinal));
}
```

`src/WebAudioArchive.Analysis/Transcription/WhisperNetTranscriber.cs`:
```csharp
using System.Reflection;
using Whisper.net;
using Whisper.net.LibraryLoader;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// whisper.cpp in this process through Whisper.net (decision D18). The model is loaded once, on the first readiness
/// check, onto the GPU (CUDA runtime) or the CPU; one chunk is transcribed at a time (the service has one work lane).
/// Each chunk is decoded without the previous window's text as context (it feeds repetition loops over music) and with
/// the priming prompt carried into every 30 s window, so fillers are kept throughout the chunk. A missing model or a
/// native library that does not load means "try later" (the backlog and lag show it); an error on one chunk fails
/// that chunk.
/// </summary>
public sealed class WhisperNetTranscriber(string modelPath, WhisperDevice device, int threads, bool dtw) : ITranscriber, IDisposable
{
    static readonly string LibraryVersion =
        (typeof(WhisperFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];

    readonly SemaphoreSlim _gate = new(1, 1);
    WhisperFactory? _factory;

    public string Engine => RuntimeOptions.LoadedLibrary is { } lib
        ? $"whisper.net {LibraryVersion} ({lib.ToString().ToLowerInvariant()})"
        : $"whisper.net {LibraryVersion}";

    public string Model => Path.GetFileNameWithoutExtension(modelPath);

    /// <summary>Why the last readiness check failed, for the log; null when ready.</summary>
    public string? LastError { get; private set; }

    /// <summary>Native runtimes to try, in order. Auto tries CUDA and falls back to the CPU when no GPU or CUDA library is there.</summary>
    public static IReadOnlyList<RuntimeLibrary> RuntimeOrder(WhisperDevice d) => d switch
    {
        WhisperDevice.Cpu => [RuntimeLibrary.Cpu],
        WhisperDevice.Cuda => [RuntimeLibrary.Cuda],
        _ => [RuntimeLibrary.Cuda, RuntimeLibrary.Cpu],
    };

    /// <summary>The DTW alignment heads for a model file name, or null when the model has no preset here.</summary>
    public static WhisperAlignmentHeadsPreset? HeadsFor(string model)
    {
        var m = model.ToLowerInvariant();
        if (m.Contains("large-v3-turbo", StringComparison.Ordinal)) return WhisperAlignmentHeadsPreset.LargeV3Turbo;
        if (m.Contains("small.en", StringComparison.Ordinal)) return WhisperAlignmentHeadsPreset.SmallEn;
        if (m.Contains("base.en", StringComparison.Ordinal)) return WhisperAlignmentHeadsPreset.BaseEn;
        return null;
    }

    public async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            await FactoryAsync(ct);
            LastError = null;
            return true;
        }
        catch (TranscriberUnavailableException ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public async Task<IReadOnlyList<RawSegment>> TranscribeAsync(TranscriptionRequest request, CancellationToken ct)
    {
        var factory = await FactoryAsync(ct);
        var builder = factory.CreateBuilder()
            .WithLanguage(request.Language)
            .WithThreads(threads)
            .WithTokenTimestamps()
            .WithProbabilities()
            .WithTemperature(0f)
            .WithTemperatureInc(0.2f)
            .WithNoContext();
        if (request.Prompt.Length > 0) builder = builder.WithPrompt(request.Prompt).WithCarryInitialPrompt(true);

        var segments = new List<RawSegment>();
        try
        {
            await using var processor = builder.Build();
            await foreach (var s in processor.ProcessAsync(new MemoryStream(request.Wav, writable: false), ct))
                segments.Add(WhisperResponse.FromSegment(s));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new TranscriptionFailedException($"whisper could not transcribe this chunk: {ex.GetType().Name}: {ex.Message}");
        }

        return segments;
    }

    async Task<WhisperFactory> FactoryAsync(CancellationToken ct)
    {
        if (_factory is { } ready) return ready;
        await _gate.WaitAsync(ct);
        try
        {
            if (_factory is { } loaded) return loaded;
            if (!File.Exists(modelPath))
                throw new TranscriberUnavailableException($"whisper model {modelPath} was not found; download it into models/ (docs/analysis.md) or set WAA_WHISPER_MODEL_PATH");
            var options = new WhisperFactoryOptions { UseGpu = device != WhisperDevice.Cpu, UseFlashAttention = !dtw, UseDtwTimeStamps = dtw };
            if (dtw)
                options.HeadsPreset = HeadsFor(Model) ?? throw new TranscriberUnavailableException($"WAA_WHISPER_DTW is on but {Model} has no DTW preset; use large-v3-turbo, small.en or base.en, or turn DTW off");

            RuntimeOptions.RuntimeLibraryOrder = [.. RuntimeOrder(device)];
            WhisperFactory? factory = null;
            try
            {
                factory = WhisperFactory.FromPath(modelPath, options);
                // Building a processor loads the native library and the model now, not in the middle of a job.
                await using (factory.CreateBuilder().WithLanguage("en").Build())
                {
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                factory?.Dispose();
                throw new TranscriberUnavailableException($"whisper could not load {modelPath} ({device}): {ex.Message}", ex);
            }

            _factory = factory;
            return factory;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _gate.Dispose();
    }
}
```

`src/WebAudioArchive.Analysis/Transcription/TranscriptFilter.cs`:
```csharp
using System.Text.RegularExpressions;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// Decides which whisper segments the detectors ignore (D13). They stay in the transcript with the reason. Applied when
/// the transcript is assembled and again when events are rebuilt, so tuning it never needs whisper.
/// </summary>
public static partial class TranscriptFilter
{
    public const string NonSpeech = "non-speech";
    public const string NoSpeech = "no-speech";
    public const string KnownHallucination = "known-hallucination";
    public const string Repetition = "repetition";

    public static string?[] Reasons(IReadOnlyList<(string Text, double NoSpeechProb, double AvgLogProb)> segments, TranscriptionOptions o)
    {
        var reasons = new string?[segments.Count];
        var known = new HashSet<string>(o.Hallucinations.Select(Key), StringComparer.Ordinal);
        var runStart = 0;
        string? previous = null;
        for (var i = 0; i < segments.Count; i++)
        {
            var (text, noSpeech, logProb) = segments[i];
            var key = Key(text);
            if (key.Length == 0) reasons[i] = NonSpeech;
            else if (noSpeech > o.MaxNoSpeechProb && logProb < o.MinAvgLogProb) reasons[i] = NoSpeech;
            else if (known.Contains(key)) reasons[i] = KnownHallucination;

            if (key.Length > 0 && key == previous)
            {
                if (i - runStart + 1 >= o.MaxRepeats)
                    for (var k = runStart; k <= i; k++) reasons[k] ??= Repetition;
            }
            else
            {
                runStart = i;
            }

            previous = key;
        }

        return reasons;
    }

    /// <summary>Lower-case words without punctuation, with annotations such as [BLANK_AUDIO], (music) and note signs removed.</summary>
    public static string Key(string text) =>
        string.Join(' ', Annotation().Replace(text, " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(WordNormaliser.Normalise)
            .Where(w => w.Length > 0 && w.Any(char.IsLetterOrDigit)));

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)|[♪♫♬]+")]
    private static partial Regex Annotation();
}
```

`src/WebAudioArchive.Analysis/Transcription/ChunkPlanner.cs`:
```csharp
using WebAudioArchive.Analysis.Audio;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>A stretch of the segment's audio sent to whisper in one request. Index is its place in the plan (resume key).</summary>
public sealed record AudioChunk(int Index, double Start, double End);

/// <summary>
/// Splits a segment into chunks of about chunkSeconds, each boundary moved to the quietest 50 ms window within
/// searchSeconds of the nominal point (ties go to the nearest), so a word is rarely cut in two. The plan depends only on
/// the audio, so a resumed job gets the same chunks. A tail shorter than chunkSeconds plus searchSeconds joins the last.
/// </summary>
public static class ChunkPlanner
{
    public static IReadOnlyList<AudioChunk> Plan(AudioFeatures f, double chunkSeconds, double searchSeconds)
    {
        var total = f.Seconds;
        var chunks = new List<AudioChunk>();
        if (total <= 0) return chunks;
        var w = f.WindowSeconds;
        var start = 0.0;
        while (total - start > chunkSeconds + searchSeconds)
        {
            var nominal = start + chunkSeconds;
            var lo = Math.Max((int)Math.Ceiling((start + 1) / w), (int)Math.Floor((nominal - searchSeconds) / w));
            var hi = Math.Min(f.WindowDbfsTenths.Count - 1, (int)Math.Ceiling((nominal + searchSeconds) / w));
            var best = nominal;
            var bestLevel = short.MaxValue;
            var bestDistance = double.MaxValue;
            for (var i = lo; i <= hi; i++)
            {
                var t = i * w;
                var level = f.WindowDbfsTenths[i];
                var distance = Math.Abs(t - nominal);
                if (level < bestLevel || (level == bestLevel && distance < bestDistance))
                    (best, bestLevel, bestDistance) = (t, level, distance);
            }

            chunks.Add(new AudioChunk(chunks.Count, start, best));
            start = best;
        }

        chunks.Add(new AudioChunk(chunks.Count, start, total));
        return chunks;
    }
}
```

`src/WebAudioArchive.Analysis/Transcription/PromptBuilder.cs`:
```csharp
using WebAudioArchive.Analysis.Settings;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>
/// The initial prompt for each chunk (spec 3.6): a verbatim sentence full of fillers, so whisper keeps um and er, then
/// the station glossary and the show title, so names are spelled right. Whisper reads at most about 224 prompt tokens,
/// so the prompt is cut at 600 characters.
/// </summary>
public static class PromptBuilder
{
    public const int MaxChars = 600;

    public static string Build(TranscriptionOptions o, string? showTitle)
    {
        var names = new List<string>();
        foreach (var n in o.Glossary.Append(showTitle ?? ""))
        {
            var name = n.Trim();
            if (name.Length > 0 && !names.Contains(name, StringComparer.OrdinalIgnoreCase)) names.Add(name);
        }

        var text = names.Count == 0 ? o.VerbatimPrompt.Trim() : $"{o.VerbatimPrompt.Trim()} {string.Join(", ", names)}.";
        return text.Length <= MaxChars ? text : text[..MaxChars];
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~TranscriptionTests`
Expected: PASS, 14 tests. The Whisper.net mapping and the missing-model tests ran in the D18 spike against this code (10 tests, all passing); none of them loads the native library. Real transcription is measured in Task 19 (`--benchmark`).

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Transcription tests/WebAudioArchive.Tests/Support/FakeTranscriber.cs tests/WebAudioArchive.Tests/TranscriptionTests.cs
git commit -m "Analysis: transcriber interface, Whisper.net transcriber, hallucination filter, chunking and prompt (#77)"
```

---

### Task 11: Work discovery, A-first planning, job state and the disk guard

**Files:**
- Create: `src/WebAudioArchive.Analysis/Work/SegmentDiscovery.cs`, `src/WebAudioArchive.Analysis/Work/WorkPlanner.cs`, `src/WebAudioArchive.Analysis/Work/JobState.cs`, `src/WebAudioArchive.Analysis/Work/DiskGuard.cs`
- Test: `tests/WebAudioArchive.Tests/WorkPlanningTests.cs`

**Interfaces:**
- Consumes: `SegmentMeta`, `SegmentMetaStore`, `SegmentGap` (Core); `RecorderRoot` (Task 1); `RecorderPreference`, `AnalysisJson`, `AnalysisPaths` (Task 3); test helper `SegmentFiles.Closed`/`Open` (plan 1, writes synthetic segments in the recorder layout).
- Produces:
  - `sealed record SourceSegment(string Recorder, string SidecarPath, string AudioPath, string JobId, SegmentMeta Meta)` with `string Fingerprint`, `int SampleRate`, `int Channels`.
  - `sealed class SegmentDiscovery(IReadOnlyList<RecorderRoot> recorders, string station, ILogger log) { IReadOnlyList<SourceSegment> Scan(ICollection<string>? problems = null); }` (caches parsed sidecars by path, size and write time)
  - `sealed record SegmentJob(SourceSegment Source, bool FillIn)`; `static class WorkPlanner { const double MinPreferredCover = 0.98; IReadOnlyList<SegmentJob> Plan(IReadOnlyList<SourceSegment>, IReadOnlyList<string> preference, DateTimeOffset? since); double CoveredFraction(SourceSegment, IEnumerable<SourceSegment>); }`
  - `static class JobStatus { Running, Waiting, Done, Failed }`; `sealed record JobState(int Schema, string JobId, string Fingerprint, string TranscriptVersion, string DetectorVersion, string Status, int Attempts, DateTimeOffset? NextAttemptUtc, string? LastError, IReadOnlyList<int> ChunksDone, int ChunkCount, DateTimeOffset UpdatedUtc, double AudioSeconds, double TranscribeSeconds, double WallSeconds)`; `enum JobAction { Skip, Wait, Run, Redetect }`; `sealed record JobDecision(JobAction Action, string Reason)`; `static class JobRules { const int MaxAttempts = 5; static readonly TimeSpan UnavailableRetry; JobDecision Decide(JobState?, string fingerprint, string transcriptVersion, string detectorVersion, DateTimeOffset now); DateTimeOffset NextAttempt(int attempts, DateTimeOffset now); }`; `static class JobStore { JobState? Read(string dir); void Write(string dir, JobState state); }`
  - `sealed class DiskGuard(string path, long minFreeBytes, Func<string, long>? freeBytes = null) { long MinFreeBytes; long FreeBytes(); bool IsLow(); }`

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/WorkPlanningTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class WorkPlanningTests : IDisposable
{
    static readonly DateTimeOffset T = new(2026, 10, 16, 8, 0, 0, TimeSpan.Zero);
    readonly string _root = Directory.CreateTempSubdirectory("waa-work-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string A => Path.Combine(_root, "a");
    string B => Path.Combine(_root, "b");

    SegmentDiscovery Discovery() => new([new RecorderRoot("a", A), new RecorderRoot("b", B)], "burst", NullLogger.Instance);

    [Fact]
    public void Discovery_reads_closed_segments_and_builds_job_ids_newest_first()
    {
        SegmentFiles.Closed(A, "burst", T, 3600, 0);
        SegmentFiles.Closed(A, "burst", T.AddHours(1), 2, 0, endedBy: "shutdown");

        var found = Discovery().Scan();

        Assert.Equal(new[] { "a/2026/10-16/090000", "a/2026/10-16/080000" }, found.Select(s => s.JobId));
        Assert.Equal(44100, found[1].SampleRate);
        Assert.Equal(2, found[1].Channels);
        Assert.EndsWith("080000.mp3", found[1].AudioPath);
    }

    [Fact]
    public void Discovery_skips_parts_tmp_and_mismatched_sizes()
    {
        var good = SegmentFiles.Closed(A, "burst", T, 600, 0);
        SegmentFiles.Open(A, "burst", T.AddHours(1), T.AddHours(1).AddMinutes(5), 0);
        File.WriteAllText(Path.ChangeExtension(good.AudioPath, ".json") + ".tmp", "{ half");
        var grown = SegmentFiles.Closed(A, "burst", T.AddHours(2), 600, 0);
        File.AppendAllText(grown.AudioPath, "extra");
        var missing = SegmentFiles.Closed(A, "burst", T.AddHours(3), 600, 0);
        File.Delete(missing.AudioPath);
        var problems = new List<string>();

        var found = Discovery().Scan(problems);

        Assert.Equal("a/2026/10-16/080000", Assert.Single(found).JobId);
        Assert.Contains(problems, p => p.Contains("sidecar says"));
        Assert.Contains(problems, p => p.Contains("is missing"));
    }

    [Fact]
    public void Changed_sidecar_changes_the_fingerprint_and_reprocesses()
    {
        var seg = SegmentFiles.Closed(A, "burst", T, 600, 0);
        var discovery = Discovery();
        var first = Assert.Single(discovery.Scan());
        var done = new JobState(1, first.JobId, first.Fingerprint, "tv", "dv", JobStatus.Done, 0, null, null, [0], 1, T, 600, 30, 40);
        Assert.Equal(JobAction.Skip, JobRules.Decide(done, first.Fingerprint, "tv", "dv", T).Action);

        var json = Path.ChangeExtension(seg.AudioPath, ".json");
        SegmentMetaStore.Write(json, SegmentMetaStore.Read(json)! with { EndedBy = "crash" });
        File.SetLastWriteTimeUtc(json, DateTime.UtcNow.AddMinutes(1));
        var second = Assert.Single(discovery.Scan());

        Assert.NotEqual(first.Fingerprint, second.Fingerprint);
        Assert.Equal(JobAction.Run, JobRules.Decide(done, second.Fingerprint, "tv", "dv", T).Action);
    }

    [Fact]
    public void B_is_planned_only_where_a_leaves_a_hole()
    {
        SegmentFiles.Closed(A, "burst", T, 3600, 0);
        SegmentFiles.Closed(B, "burst", T, 3600, 0);
        SegmentFiles.Closed(A, "burst", T.AddHours(1), 3600, 0, gaps: [new SegmentGap(T.AddHours(1).AddMinutes(20), T.AddHours(1).AddMinutes(30), 600, "stream ended by server")]);
        SegmentFiles.Closed(B, "burst", T.AddHours(1), 3600, 0);
        SegmentFiles.Closed(B, "burst", T.AddHours(2), 3600, 0);

        var jobs = WorkPlanner.Plan(Discovery().Scan(), ["a", "b"], since: null);

        Assert.Equal(new[] { "b/2026/10-16/100000", "a/2026/10-16/090000", "b/2026/10-16/090000", "a/2026/10-16/080000" }, jobs.Select(j => j.Source.JobId));
        Assert.True(jobs[0].FillIn);
        Assert.False(jobs[1].FillIn);
    }

    [Fact]
    public void Covered_fraction_counts_spans_minus_gaps_once()
    {
        SegmentFiles.Closed(A, "burst", T, 1800, 0, gaps: [new SegmentGap(T.AddMinutes(20), T.AddMinutes(30), 600, "x")]);
        SegmentFiles.Closed(A, "burst", T.AddMinutes(30), 1800, 0, name: "083000");
        SegmentFiles.Closed(B, "burst", T, 3600, 0);
        var all = Discovery().Scan();
        var b = all.Single(s => s.Recorder == "b");

        Assert.Equal(50.0 / 60, WorkPlanner.CoveredFraction(b, all.Where(s => s.Recorder == "a")), 6);
    }

    [Fact]
    public void Since_leaves_out_older_segments()
    {
        SegmentFiles.Closed(A, "burst", T, 3600, 0);
        SegmentFiles.Closed(A, "burst", T.AddHours(1), 3600, 0);
        var jobs = WorkPlanner.Plan(Discovery().Scan(), ["a", "b"], since: T.AddHours(1).AddMinutes(1));
        Assert.Equal("a/2026/10-16/090000", Assert.Single(jobs).Source.JobId);
    }

    public static TheoryData<string?, string, JobAction> Decisions => new()
    {
        { null, "", JobAction.Run },
        { JobStatus.Done, "", JobAction.Skip },
        { JobStatus.Done, "detector", JobAction.Redetect },
        { JobStatus.Done, "transcript", JobAction.Run },
        { JobStatus.Running, "", JobAction.Run },
        { JobStatus.Waiting, "future", JobAction.Wait },
        { JobStatus.Waiting, "past", JobAction.Run },
        { JobStatus.Failed, "future", JobAction.Wait },
        { JobStatus.Failed, "exhausted", JobAction.Skip },
    };

    [Theory]
    [MemberData(nameof(Decisions))]
    public void Job_rules(string? status, string change, JobAction expected)
    {
        var state = status is null ? null : new JobState(1, "a/x/y/z", "fp", "tv", "dv", status,
            change == "exhausted" ? JobRules.MaxAttempts : 1,
            change switch { "future" => T.AddMinutes(5), "past" => T.AddMinutes(-5), "exhausted" => T.AddMinutes(-5), _ => null },
            null, [], 0, T, 0, 0, 0);
        var tv = change == "transcript" ? "tv2" : "tv";
        var dv = change == "detector" ? "dv2" : "dv";

        Assert.Equal(expected, JobRules.Decide(state, "fp", tv, dv, T).Action);
    }

    [Fact]
    public void Retry_backoff_doubles_from_five_minutes_and_stops_at_six_hours()
    {
        Assert.Equal(T.AddMinutes(5), JobRules.NextAttempt(1, T));
        Assert.Equal(T.AddMinutes(10), JobRules.NextAttempt(2, T));
        Assert.Equal(T.AddMinutes(20), JobRules.NextAttempt(3, T));
        Assert.Equal(T.AddHours(6), JobRules.NextAttempt(20, T));
    }

    [Fact]
    public void Job_state_round_trips_and_a_damaged_file_reads_as_none()
    {
        var dir = Path.Combine(_root, "state");
        var state = new JobState(1, "a/2026/10-16/080000", "fp", "tv", "dv", JobStatus.Running, 2, T, "x", [0, 1], 3, T, 3600, 120, 150);
        JobStore.Write(dir, state);
        var back = JobStore.Read(dir)!;
        Assert.Equal(state with { ChunksDone = back.ChunksDone }, back);
        Assert.Equal(new[] { 0, 1 }, back.ChunksDone);

        File.WriteAllText(Path.Combine(dir, "state.json"), "{");
        Assert.Null(JobStore.Read(dir));
    }

    [Fact]
    public void Disk_guard_reports_low_space()
    {
        var free = 30L << 30;
        var guard = new DiskGuard(_root, 20L << 30, _ => free);
        Assert.False(guard.IsLow());
        free = 19L << 30;
        Assert.True(guard.IsLow());
        Assert.True(new DiskGuard(_root, 0).FreeBytes() > 0);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~WorkPlanningTests`
Expected: build FAILS with `CS0246` for `SegmentDiscovery`, `WorkPlanner`, `JobState`, `DiskGuard`.

- [ ] **Step 3: Implement discovery, planning, job state and the guard**

`src/WebAudioArchive.Analysis/Work/SegmentDiscovery.cs`:
```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis.Work;

/// <summary>A closed master segment: recorder, sidecar, audio, the job id it is analysed under (recorder/yyyy/MM-dd/stem) and its sidecar.</summary>
public sealed record SourceSegment(string Recorder, string SidecarPath, string AudioPath, string JobId, SegmentMeta Meta)
{
    /// <summary>Changes whenever the recorder rewrites the segment (crash recovery), so the job runs again.</summary>
    public string Fingerprint => string.Create(CultureInfo.InvariantCulture, $"{Meta.Bytes}:{Meta.Frames}:{Meta.EndUtc.UtcTicks}:{Meta.EndedBy}");

    public int SampleRate => Meta.Format?.SampleRateHz ?? 44100;

    public int Channels => Meta.Format?.Channels ?? 2;
}

/// <summary>
/// Finds closed segments (a sidecar beside its .mp3, sizes agreeing) under each recorder's read-only root. Open .part
/// files, temporary sidecars and segments of another station are ignored. Parsed sidecars are cached by path, size and
/// write time, so a pass over a year of hourly segments reads only what changed.
/// </summary>
public sealed class SegmentDiscovery(IReadOnlyList<RecorderRoot> recorders, string station, ILogger log)
{
    readonly ConcurrentDictionary<string, (long Length, DateTime Written, SegmentMeta? Meta, string? Problem)> _cache = new();

    public IReadOnlyList<SourceSegment> Scan(ICollection<string>? problems = null)
    {
        var list = new List<SourceSegment>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in recorders)
        {
            var master = Path.Combine(r.Root, station, "master");
            if (!Directory.Exists(master)) continue;
            foreach (var json in Directory.EnumerateFiles(master, "*.json", SearchOption.AllDirectories))
            {
                if (!json.EndsWith(".json", StringComparison.Ordinal)) continue;
                seen.Add(json);
                var segment = Read(r.Id, master, json, out var problem);
                if (segment is not null)
                {
                    list.Add(segment);
                }
                else if (problem is not null)
                {
                    problems?.Add(problem);
                    log.LogDebug("Not analysed: {Problem}", problem);
                }
            }
        }

        foreach (var stale in _cache.Keys.Where(k => !seen.Contains(k)).ToList()) _cache.TryRemove(stale, out _);
        return list.OrderByDescending(s => s.Meta.StartUtc).ThenBy(s => s.Recorder, StringComparer.Ordinal).ToList();
    }

    SourceSegment? Read(string recorder, string master, string json, out string? problem)
    {
        var info = new FileInfo(json);
        SegmentMeta? meta;
        if (_cache.TryGetValue(json, out var hit) && hit.Length == info.Length && hit.Written == info.LastWriteTimeUtc)
        {
            meta = hit.Meta;
            problem = hit.Problem;
        }
        else
        {
            (meta, problem) = Parse(json);
            _cache[json] = (info.Length, info.LastWriteTimeUtc, meta, problem);
        }

        if (meta is null) return null;
        var audio = Path.Combine(Path.GetDirectoryName(json)!, meta.File);
        long length;
        try
        {
            length = new FileInfo(audio).Length;
        }
        catch (FileNotFoundException)
        {
            problem = $"{json}: its audio {meta.File} is missing";
            return null;
        }

        if (length != meta.Bytes)
        {
            problem = $"{json}: {meta.File} is {length} bytes but the sidecar says {meta.Bytes}; skipped until they agree";
            return null;
        }

        var relative = Path.GetRelativePath(master, json).Replace('\\', '/');
        problem = null;
        return new SourceSegment(recorder, json, audio, recorder + "/" + relative[..^".json".Length], meta);
    }

    (SegmentMeta? Meta, string? Problem) Parse(string json)
    {
        SegmentMeta? meta;
        try
        {
            meta = SegmentMetaStore.Read(json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (null, $"{json}: unreadable sidecar ({ex.Message})");
        }

        if (meta is null || string.IsNullOrEmpty(meta.File) || meta.StartUtc == default || meta.EndUtc < meta.StartUtc || meta.Bytes < 0)
            return (null, $"{json}: incomplete sidecar");
        if (Path.GetFileName(meta.File) != meta.File || meta.File.Contains("..", StringComparison.Ordinal))
            return (null, $"{json}: file name {meta.File} is not a plain name");
        if (!string.Equals(meta.Station, station, StringComparison.Ordinal))
            return (null, $"{json}: station '{meta.Station}', not '{station}'");
        return (meta, null);
    }
}
```

`src/WebAudioArchive.Analysis/Work/WorkPlanner.cs`:
```csharp
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Work;

/// <summary>A segment to analyse. FillIn is true for a less preferred recorder's segment queued because the preferred one has a hole there.</summary>
public sealed record SegmentJob(SourceSegment Source, bool FillIn);

/// <summary>
/// D7 and D8: every segment of the first recorder in the preference is a job; another recorder's segment is a job only
/// when better recorders cover less than 98% of its span (spans minus gaps). Jobs run newest first.
/// </summary>
public static class WorkPlanner
{
    public const double MinPreferredCover = 0.98;

    public static IReadOnlyList<SegmentJob> Plan(IReadOnlyList<SourceSegment> segments, IReadOnlyList<string> preference, DateTimeOffset? since)
    {
        var eligible = segments.Where(s => since is null || s.Meta.EndUtc > since.Value).ToList();
        var byHour = eligible.ToLookup(s => Hour(s.Meta.StartUtc));
        var jobs = new List<SegmentJob>();
        foreach (var s in eligible)
        {
            var rank = RecorderPreference.Rank(s.Recorder, preference);
            if (rank == 0)
            {
                jobs.Add(new SegmentJob(s, false));
                continue;
            }

            var candidates = new List<SourceSegment>();
            for (var h = Hour(s.Meta.StartUtc) - 1; h <= Hour(s.Meta.EndUtc); h++)
                candidates.AddRange(byHour[h].Where(o => RecorderPreference.Rank(o.Recorder, preference) < rank));
            if (CoveredFraction(s, candidates) < MinPreferredCover) jobs.Add(new SegmentJob(s, true));
        }

        return jobs
            .OrderByDescending(j => j.Source.Meta.StartUtc)
            .ThenBy(j => RecorderPreference.Rank(j.Source.Recorder, preference))
            .ToList();
    }

    /// <summary>The fraction of <paramref name="s"/>'s wall span that the others' spans, less their gaps, cover (overlaps counted once).</summary>
    public static double CoveredFraction(SourceSegment s, IEnumerable<SourceSegment> others)
    {
        var from = s.Meta.StartUtc;
        var to = s.Meta.EndUtc;
        var span = (to - from).TotalSeconds;
        if (span <= 0) return 1;

        var pieces = new List<(DateTimeOffset From, DateTimeOffset To)>();
        foreach (var o in others)
        {
            if (o.Meta.EndUtc <= from || o.Meta.StartUtc >= to) continue;
            var cursor = o.Meta.StartUtc;
            foreach (var g in (o.Meta.Gaps ?? []).OrderBy(g => g.StartUtc))
            {
                if (g.StartUtc > cursor) pieces.Add((cursor, g.StartUtc < o.Meta.EndUtc ? g.StartUtc : o.Meta.EndUtc));
                if (g.EndUtc > cursor) cursor = g.EndUtc;
            }

            if (o.Meta.EndUtc > cursor) pieces.Add((cursor, o.Meta.EndUtc));
        }

        double covered = 0;
        DateTimeOffset? runFrom = null, runTo = null;
        foreach (var p in pieces
                     .Select(p => (From: p.From < from ? from : p.From, To: p.To > to ? to : p.To))
                     .Where(p => p.To > p.From)
                     .OrderBy(p => p.From))
        {
            if (runTo is null || p.From > runTo)
            {
                if (runTo is not null) covered += (runTo.Value - runFrom!.Value).TotalSeconds;
                (runFrom, runTo) = (p.From, p.To);
            }
            else if (p.To > runTo)
            {
                runTo = p.To;
            }
        }

        if (runTo is not null) covered += (runTo.Value - runFrom!.Value).TotalSeconds;
        return covered / span;
    }

    static long Hour(DateTimeOffset t) => t.UtcTicks / TimeSpan.TicksPerHour;
}
```

`src/WebAudioArchive.Analysis/Work/JobState.cs`:
```csharp
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Work;

public static class JobStatus
{
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>
/// state.json beside a segment's results: what it was made from (fingerprint and versions), how far it got (chunks
/// done) and, when it failed or is waiting, why and when to try again. A crash leaves "running", which resumes.
/// </summary>
public sealed record JobState(
    int Schema,
    string JobId,
    string Fingerprint,
    string TranscriptVersion,
    string DetectorVersion,
    string Status,
    int Attempts,
    DateTimeOffset? NextAttemptUtc,
    string? LastError,
    IReadOnlyList<int> ChunksDone,
    int ChunkCount,
    DateTimeOffset UpdatedUtc,
    double AudioSeconds,
    double TranscribeSeconds,
    double WallSeconds)
{
    public const int CurrentSchema = 1;
}

public enum JobAction
{
    Skip,
    Wait,
    Run,
    Redetect,
}

public sealed record JobDecision(JobAction Action, string Reason);

public static class JobRules
{
    public const int MaxAttempts = 5;

    /// <summary>How long to wait when whisper is unreachable; not counted as an attempt.</summary>
    public static readonly TimeSpan UnavailableRetry = TimeSpan.FromMinutes(5);

    public static JobDecision Decide(JobState? state, string fingerprint, string transcriptVersion, string detectorVersion, DateTimeOffset now)
    {
        if (state is null) return new(JobAction.Run, "new");
        if (state.Fingerprint != fingerprint) return new(JobAction.Run, "the segment changed");
        if (state.TranscriptVersion != transcriptVersion) return new(JobAction.Run, "the transcriber or its options changed");
        if (state.Status == JobStatus.Done)
            return state.DetectorVersion == detectorVersion ? new(JobAction.Skip, "done") : new(JobAction.Redetect, "the detectors changed");
        if (state.Status == JobStatus.Failed && state.Attempts >= MaxAttempts)
            return new(JobAction.Skip, $"failed {state.Attempts} times: {state.LastError}");
        if (state.NextAttemptUtc is { } next && next > now) return new(JobAction.Wait, $"retry at {next:u}");
        return new(JobAction.Run, state.Status == JobStatus.Running ? "resume after an interruption" : "retry");
    }

    /// <summary>5 minutes after the first failure, doubling, at most 6 hours.</summary>
    public static DateTimeOffset NextAttempt(int attempts, DateTimeOffset now) =>
        now + TimeSpan.FromMinutes(Math.Min(360, 5 * Math.Pow(2, Math.Max(0, attempts - 1))));
}

public static class JobStore
{
    public static JobState? Read(string dir) => AnalysisJson.TryRead<JobState>(Path.Combine(dir, AnalysisPaths.StateFile), out _);

    public static void Write(string dir, JobState state) => AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.StateFile), state);
}
```

`src/WebAudioArchive.Analysis/Work/DiskGuard.cs`:
```csharp
namespace WebAudioArchive.Analysis.Work;

/// <summary>
/// Free space on the disk holding the analysis output, which on a single-disk host is the recorders' disk too. Below
/// the threshold analysis stops between chunks and does not start new jobs, so the recorders keep the space (spec 3.5).
/// </summary>
public sealed class DiskGuard(string path, long minFreeBytes, Func<string, long>? freeBytes = null)
{
    public long MinFreeBytes => minFreeBytes;

    public long FreeBytes() => (freeBytes ?? Free)(path);

    public bool IsLow() => FreeBytes() < minFreeBytes;

    static long Free(string p)
    {
        var full = Path.GetFullPath(p);
        Directory.CreateDirectory(full);
        return new DriveInfo(OperatingSystem.IsWindows() ? Path.GetPathRoot(full)! : full).AvailableFreeSpace;
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~WorkPlanningTests`
Expected: PASS, 18 tests (9 facts and 9 theory cases).

- [ ] **Step 5: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Work tests/WebAudioArchive.Tests/WorkPlanningTests.cs
git commit -m "Analysis: discover closed segments, plan A first with B filling holes, job state and disk guard (#9, #85)"
```

---

### Task 12: The resumable segment pipeline

**Files:**
- Create: `src/WebAudioArchive.Analysis/Transcription/TranscriptAssembler.cs`, `src/WebAudioArchive.Analysis/Detectors/DetectorRunner.cs`, `src/WebAudioArchive.Analysis/Work/SegmentPipeline.cs`
- Create: `tests/WebAudioArchive.Tests/Support/AnalysisFixtures.cs`
- Test: `tests/WebAudioArchive.Tests/SegmentPipelineTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2 to 11; `IClock` (Core); `ManualClock` (test support, plan 1).
- Produces:
  - `sealed record PartialChunk(int Index, double Start, double End, IReadOnlyList<RawSegment> Segments)`; `sealed record TranscriptPartial(int Schema, string Fingerprint, string TranscriptVersion, int ChunkCount, double TranscribeSeconds, IReadOnlyList<PartialChunk> Chunks)`.
  - `static class TranscriptAssembler { TranscriptDocument Build(SourceSegment s, string station, IReadOnlyList<TimelineAnchor> anchors, IEnumerable<RawSegment> segments, TranscriptionOptions o, string engine, string model, string transcriptVersion, string decoder, DateTimeOffset now, double transcribeSeconds); TranscriptDocument Refilter(TranscriptDocument t, TranscriptionOptions o); }`
  - `static class DetectorRunner { EventsDocument Run(TranscriptDocument t, AudioFeatures f, LoadedConfig config, Watershed watershed, DateTimeOffset now); }` (stats keys: `speechSeconds`, `fillers`, `swears`, `swearsBeforeWatershed`, `clippingEvents`, `clippedMs`, `clippingTruncated`, `deadAirEvents`, `deadAirSeconds`)
  - `sealed class DiskLowException(string message)`; `sealed record JobOutcome(string JobId, JobAction Action, string Status, string Reason, double AudioSeconds, double TranscribeSeconds, double WallSeconds)` with `double? RealtimeFactor`; `sealed class SegmentPipeline(AnalysisPaths paths, LoadedConfig config, ScratchSpace scratch, SegmentDecoder decoder, ITranscriber transcriber, DiskGuard disk, Watershed watershed, Func<DateTimeOffset, string?> showTitleAt, IClock clock, ILogger log) { string TranscriptVersion; JobDecision Decide(SegmentJob job); bool RebuildEvents(SegmentJob job); Task<JobOutcome> ProcessAsync(SegmentJob job, CancellationToken ct); }` (outcome `Status` is a `JobStatus` value or `paused-disk`)
  - Test helper `AnalysisFixtures.WriteDecodableSegmentAsync(string recorderRoot, string station, DateTimeOffset start, float[] mono, IReadOnlyList<SegmentGap>? gaps = null)` and `AnalysisFixtures.Said(double start, string text)` (a `RawSegment` with one word every 0.3 s).

- [ ] **Step 1: Write the fixtures helper**

`tests/WebAudioArchive.Tests/Support/AnalysisFixtures.cs`:
```csharp
using System.Globalization;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests.Support;

public static class AnalysisFixtures
{
    /// <summary>
    /// A real, decodable segment in the recorder's layout: the mono signal encoded as a stereo 128 kbps MP3 at
    /// &lt;root&gt;/&lt;station&gt;/master/yyyy/MM-dd/HHmmss.mp3 with a sidecar beside it. Written to a temp folder, never committed.
    /// </summary>
    public static async Task<string> WriteDecodableSegmentAsync(string recorderRoot, string station, DateTimeOffset start, float[] mono, IReadOnlyList<SegmentGap>? gaps = null)
    {
        var utc = start.UtcDateTime;
        var dir = Path.Combine(recorderRoot, station, "master", utc.ToString("yyyy", CultureInfo.InvariantCulture), utc.ToString("MM-dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(dir);
        var name = utc.ToString("HHmmss", CultureInfo.InvariantCulture) + ".mp3";
        var mp3 = Path.Combine(dir, name);
        await TestMp3.EncodeMp3Async(mp3, Signals.Stereo(mono), 2);
        var seconds = mono.Length / (double)Signals.Rate;
        SegmentMetaStore.Write(Path.ChangeExtension(mp3, ".json"), new SegmentMeta(
            1, "home-a", station, name, start, start.AddSeconds(seconds), new FileInfo(mp3).Length, (int)(seconds * 44100 / 1152), seconds,
            new SegmentFormat("MPEG-1 Layer III", 128, 44100, 2), null, [], gaps ?? [], "hour-boundary"));
        return mp3;
    }

    /// <summary>What a transcriber would return for <paramref name="text"/> said from <paramref name="start"/> seconds: one word every 0.3 s.</summary>
    public static RawSegment Said(double start, string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select((w, i) => new TranscriptWord(w, start + i * 0.3, start + i * 0.3 + 0.25, 0.9))
            .ToList();
        return new RawSegment(start, start + words.Count * 0.3, text, 0.01, -0.2, words);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/SegmentPipelineTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>The whole segment job on a real (synthetic) MP3 with the managed decoder and a fake transcriber. 19:00 UTC on 16 October 2026 is 20:00 BST, before the watershed.</summary>
public sealed class SegmentPipelineTests : IAsyncLifetime
{
    static readonly DateTimeOffset T = new(2026, 10, 16, 19, 0, 0, TimeSpan.Zero);
    static readonly TimeZoneInfo London = TimeZoneInfo.FindSystemTimeZoneById("Europe/London");
    readonly string _root = Directory.CreateTempSubdirectory("waa-pipe-").FullName;
    readonly ManualClock _clock = new(T.AddHours(2));
    readonly FakeTranscriber _whisper = new();
    LoadedConfig _config = null!;
    string _mp3 = null!;
    long _free = long.MaxValue;

    string A => Path.Combine(_root, "a");
    string Out => Path.Combine(_root, "analysis");
    string Scratch => Path.Combine(_root, "scratch");
    AnalysisPaths Paths => new(Out, "burst");

    public async Task InitializeAsync()
    {
        var loaded = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json"));
        _config = loaded with
        {
            Config = loaded.Config with
            {
                Transcription = loaded.Config.Transcription with { ChunkSeconds = 30, BoundarySearchSeconds = 2 },
                Clipping = loaded.Config.Clipping with { Threshold = 0.98 },
            },
        };

        // 70 s: speech 0-9 and a tone to 10 (so the dead air starts exactly at 10), dead air 10-17, a hard-clipped tone
        // 17-20, speech 20-70. Three chunks: about 0-30, 30-60, 60-70.
        var clipped = Signals.Sine(3, amp: 1.4).Select(x => Math.Clamp(x, -1f, 1f)).ToArray();
        _mp3 = await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", T,
            Signals.Concat(Signals.SpeechLike(9, 1), Signals.Sine(1), Signals.Silence(7), clipped, Signals.SpeechLike(50, 2)));
        _whisper.Respond = (_, call) => call == 0 ? [AnalysisFixtures.Said(1.0, "Um, welcome to Burst, that was shit.")] : [AnalysisFixtures.Said(1.0, "Hello again.")];
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    SegmentPipeline Pipeline(ITranscriber? transcriber = null, LoadedConfig? config = null) => new(
        Paths, config ?? _config, new ScratchSpace(Scratch, 512L * 1024 * 1024), new SegmentDecoder(),
        transcriber ?? _whisper, new DiskGuard(Out, 1L << 30, _ => _free), Watershed.From(new SwearOptions(), London),
        _ => "Backseat Aux", _clock, NullLogger.Instance);

    SegmentJob Job => new(Assert.Single(new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance).Scan()), false);

    string SegmentDir => Paths.SegmentDir("a/2026/10-16/190000");

    [Fact]
    public async Task A_segment_becomes_a_transcript_and_events_and_scratch_is_cleaned()
    {
        var archived = await File.ReadAllBytesAsync(_mp3);

        var outcome = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Done, outcome.Status);
        Assert.Equal(3, _whisper.Calls);
        Assert.Contains("Backseat Aux", _whisper.Requests[0].Prompt);
        Assert.NotNull(outcome.RealtimeFactor);

        var transcript = AnalysisJson.Read<TranscriptDocument>(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile))!;
        Assert.Equal(3, transcript.Segments.Count);
        Assert.Equal(1.0, transcript.Segments[0].Start, 3);
        Assert.InRange(transcript.Segments[1].Start, 29, 33);
        Assert.Equal(T.AddSeconds(1.0), transcript.ToUtc(transcript.Segments[0].Start));
        Assert.StartsWith("NLayer 3.0.0", transcript.Decoder);

        var events = AnalysisJson.Read<EventsDocument>(Path.Combine(SegmentDir, AnalysisPaths.EventsFile))!;
        var swear = Assert.Single(events.Events, e => e.Type == EventTypes.Swear);
        Assert.Equal(Severities.High, swear.Severity);
        Assert.Equal("true", swear.Evidence!["beforeWatershed"]);
        Assert.Equal(T.AddSeconds(1.0 + 6 * 0.3), swear.StartUtc);
        Assert.Equal(EventSources.Transcript, swear.Source);
        Assert.Single(events.Events, e => e.Type == EventTypes.Filler);
        var dead = Assert.Single(events.Events, e => e.Type == EventTypes.DeadAir);
        Assert.InRange((dead.StartUtc - T).TotalSeconds, 9.8, 10.3);
        Assert.InRange((dead.EndUtc - T).TotalSeconds, 16.8, 17.2);
        Assert.Contains(events.Events, e => e.Type == EventTypes.Clipping && (e.StartUtc - T).TotalSeconds is > 16.9 and < 20.1);
        Assert.Equal(1, events.Stats!["swearsBeforeWatershed"]);
        Assert.Equal(_config.DetectorVersion, events.Versions["detectors"]);

        Assert.Equal(archived, await File.ReadAllBytesAsync(_mp3));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
        Assert.False(File.Exists(Path.Combine(SegmentDir, AnalysisPaths.PartialFile)));
        Assert.Equal(JobStatus.Done, JobStore.Read(SegmentDir)!.Status);
    }

    [Fact]
    public async Task Running_again_is_a_no_op()
    {
        await Pipeline().ProcessAsync(Job, CancellationToken.None);
        var again = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobAction.Skip, again.Action);
        Assert.Equal(3, _whisper.Calls);
    }

    [Fact]
    public async Task Resume_after_a_crash_skips_finished_chunks()
    {
        var crashing = new FakeTranscriber
        {
            Respond = (_, call) => call == 1 ? throw new InvalidOperationException("simulated crash") : [AnalysisFixtures.Said(1, "one")],
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Pipeline(crashing).ProcessAsync(Job, CancellationToken.None));
        var interrupted = JobStore.Read(SegmentDir)!;
        Assert.Equal(JobStatus.Running, interrupted.Status);
        Assert.Equal(new[] { 0 }, interrupted.ChunksDone);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));

        var outcome = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Done, outcome.Status);
        Assert.Equal(2, _whisper.Calls);
        var transcript = AnalysisJson.Read<TranscriptDocument>(Path.Combine(SegmentDir, AnalysisPaths.TranscriptFile))!;
        Assert.Equal("one", transcript.Segments[0].Text);
    }

    [Fact]
    public async Task Whisper_down_waits_without_using_an_attempt()
    {
        var down = new FakeTranscriber { Respond = (_, _) => throw new TranscriberUnavailableException("refused") };

        var outcome = await Pipeline(down).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Waiting, outcome.Status);
        var state = JobStore.Read(SegmentDir)!;
        Assert.Equal(0, state.Attempts);
        Assert.Equal(_clock.UtcNow + JobRules.UnavailableRetry, state.NextAttemptUtc);
        Assert.Equal(JobAction.Wait, (await Pipeline(down).ProcessAsync(Job, CancellationToken.None)).Action);
        Assert.Equal(1, down.Calls);

        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.Equal(JobStatus.Done, (await Pipeline().ProcessAsync(Job, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Low_disk_stops_between_chunks_and_keeps_progress()
    {
        var calls = 0;
        var whisper = new FakeTranscriber
        {
            Respond = (_, _) =>
            {
                if (++calls == 1) _free = 1;
                return [AnalysisFixtures.Said(1, "one")];
            },
        };

        var outcome = await Pipeline(whisper).ProcessAsync(Job, CancellationToken.None);

        Assert.Equal("paused-disk", outcome.Status);
        Assert.Equal(1, whisper.Calls);
        Assert.Equal(new[] { 0 }, JobStore.Read(SegmentDir)!.ChunksDone);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));

        Assert.Equal("paused-disk", (await Pipeline(whisper).ProcessAsync(Job, CancellationToken.None)).Status);
        Assert.Equal(1, whisper.Calls);
    }

    [Fact]
    public async Task A_detector_change_rebuilds_events_without_whisper_or_audio()
    {
        var job = Job;
        await Pipeline().ProcessAsync(job, CancellationToken.None);
        var changed = _config with { DetectorVersion = "changed" };
        File.Move(_mp3, _mp3 + ".away");

        var outcome = await Pipeline(config: changed).ProcessAsync(job, CancellationToken.None);

        File.Move(_mp3 + ".away", _mp3);
        Assert.Equal(JobAction.Redetect, outcome.Action);
        Assert.Equal(JobStatus.Done, outcome.Status);
        Assert.Equal(3, _whisper.Calls);
        Assert.Equal("changed", AnalysisJson.Read<EventsDocument>(Path.Combine(SegmentDir, AnalysisPaths.EventsFile))!.Versions["detectors"]);
        Assert.Equal("changed", JobStore.Read(SegmentDir)!.DetectorVersion);
    }

    [Fact]
    public async Task A_rewritten_segment_is_analysed_again()
    {
        await Pipeline().ProcessAsync(Job, CancellationToken.None);
        var json = Path.ChangeExtension(_mp3, ".json");
        SegmentMetaStore.Write(json, SegmentMetaStore.Read(json)! with { EndedBy = "crash" });

        await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(6, _whisper.Calls);
    }

    [Fact]
    public async Task Undecodable_audio_fails_with_backoff()
    {
        await File.WriteAllTextAsync(_mp3, string.Concat(Enumerable.Repeat("not audio ", 1000)));
        var json = Path.ChangeExtension(_mp3, ".json");
        SegmentMetaStore.Write(json, SegmentMetaStore.Read(json)! with { Bytes = new FileInfo(_mp3).Length });

        var outcome = await Pipeline().ProcessAsync(Job, CancellationToken.None);

        Assert.Equal(JobStatus.Failed, outcome.Status);
        var state = JobStore.Read(SegmentDir)!;
        Assert.Equal(1, state.Attempts);
        Assert.Equal(_clock.UtcNow.AddMinutes(5), state.NextAttemptUtc);
        Assert.Contains("no MPEG audio frames", state.LastError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(Scratch));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SegmentPipelineTests`
Expected: build FAILS with `CS0246` for `SegmentPipeline` (and `TranscriptAssembler`, `DetectorRunner` once referenced).

- [ ] **Step 4: Implement the assembler, the detector runner and the pipeline**

`src/WebAudioArchive.Analysis/Transcription/TranscriptAssembler.cs`:
```csharp
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Transcription;

/// <summary>A finished chunk of a job: where it lies in the audio and what whisper returned for it, already shifted to segment time.</summary>
public sealed record PartialChunk(int Index, double Start, double End, IReadOnlyList<RawSegment> Segments);

/// <summary>transcript.partial.json: chunks finished so far, so a restart resumes instead of transcribing again.</summary>
public sealed record TranscriptPartial(int Schema, string Fingerprint, string TranscriptVersion, int ChunkCount, double TranscribeSeconds, IReadOnlyList<PartialChunk> Chunks)
{
    public const int CurrentSchema = 1;
}

public static class TranscriptAssembler
{
    public static TranscriptDocument Build(
        SourceSegment s, string station, IReadOnlyList<TimelineAnchor> anchors, IEnumerable<RawSegment> segments, TranscriptionOptions o,
        string engine, string model, string transcriptVersion, string decoder, DateTimeOffset now, double transcribeSeconds)
    {
        var ordered = segments.OrderBy(x => x.Start).ToList();
        var reasons = TranscriptFilter.Reasons(ordered.Select(x => (x.Text, x.NoSpeechProb, x.AvgLogProb)).ToList(), o);
        var list = ordered.Select((x, i) => new TranscriptSegment(
                Round(x.Start), Round(x.End), x.Text, x.NoSpeechProb, x.AvgLogProb, reasons[i],
                x.Words.Select(w => w with { Start = Round(w.Start), End = Round(w.End) }).ToList()))
            .ToList();
        return new TranscriptDocument(TranscriptDocument.CurrentSchema, station, s.Recorder, s.Meta.Instance, s.JobId, s.Meta.File,
            s.Meta.StartUtc, s.Meta.EndUtc, s.Meta.AudioSeconds, anchors, engine, model, o.Language, transcriptVersion, decoder,
            now, Math.Round(transcribeSeconds, 3), list);
    }

    /// <summary>The same transcript with suppression worked out again under the current options (used when events are rebuilt).</summary>
    public static TranscriptDocument Refilter(TranscriptDocument t, TranscriptionOptions o)
    {
        var reasons = TranscriptFilter.Reasons(t.Segments.Select(x => (x.Text, x.NoSpeechProb, x.AvgLogProb)).ToList(), o);
        return t with { Segments = t.Segments.Select((x, i) => x with { Suppressed = reasons[i] }).ToList() };
    }

    static double Round(double x) => Math.Round(x, 3);
}
```

`src/WebAudioArchive.Analysis/Detectors/DetectorRunner.cs`:
```csharp
using System.Globalization;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Detectors;

/// <summary>Runs every detector over one segment's transcript and features and turns the results into its events.json, in UTC.</summary>
public static class DetectorRunner
{
    /// <summary>Provisional confidences for the audio detectors until Task 18 measures them on real audio.</summary>
    public const double ClippingConfidence = 0.9;
    public const double DeadAirConfidence = 0.95;

    public static EventsDocument Run(TranscriptDocument t, AudioFeatures f, LoadedConfig config, Watershed watershed, DateTimeOffset now)
    {
        var c = config.Config;
        var v = config.DetectorVersion;
        DateTimeOffset Utc(double offset) => t.ToUtc(offset);
        var events = new List<AnalysisEvent>();

        var clipping = ClippingDetector.Detect(f, c.Clipping);
        foreach (var e in clipping.Events)
        {
            events.Add(new AnalysisEvent(
                AnalysisEvent.MakeId(EventTypes.Clipping, t.JobId, e.StartSeconds), EventTypes.Clipping, Utc(e.StartSeconds), Utc(e.EndSeconds),
                ClippingDetector.Severity(e, c.Clipping), ClippingConfidence, ClippingDetector.Name, v, EventSources.Audio, t.Recorder, t.JobId,
                R(e.StartSeconds), R(e.EndSeconds - e.StartSeconds), string.Create(CultureInfo.InvariantCulture, $"Clipping {e.DurationMs:0} ms"),
                new Dictionary<string, string>
                {
                    ["peak"] = e.Peak.ToString("0.000", CultureInfo.InvariantCulture),
                    ["runs"] = e.Runs.ToString(CultureInfo.InvariantCulture),
                    ["clippedSamples"] = e.ClippedSamples.ToString(CultureInfo.InvariantCulture),
                    ["channels"] = string.Join(',', e.Channels),
                    ["threshold"] = c.Clipping.Threshold.ToString(CultureInfo.InvariantCulture),
                }));
        }

        var deadAir = DeadAirDetector.Detect(f, c.DeadAir);
        foreach (var e in deadAir)
        {
            events.Add(new AnalysisEvent(
                AnalysisEvent.MakeId(EventTypes.DeadAir, t.JobId, e.StartSeconds), EventTypes.DeadAir, Utc(e.StartSeconds), Utc(e.EndSeconds),
                DeadAirDetector.Severity(e, c.DeadAir), DeadAirConfidence, DeadAirDetector.Name, v, EventSources.Audio, t.Recorder, t.JobId,
                R(e.StartSeconds), R(e.Seconds), string.Create(CultureInfo.InvariantCulture, $"Dead air {e.Seconds:0.0} s"),
                new Dictionary<string, string>
                {
                    ["quietestDbfs"] = e.QuietestDbfs.ToString("0.0", CultureInfo.InvariantCulture),
                    ["thresholdDbfs"] = c.DeadAir.ThresholdDbfs.ToString(CultureInfo.InvariantCulture),
                }));
        }

        var swears = SwearDetector.Detect(t, config.Swears);
        var beforeCount = 0;
        foreach (var h in swears)
        {
            var at = Utc(h.Start);
            var before = watershed.IsBeforeWatershed(at);
            if (before) beforeCount++;
            events.Add(new AnalysisEvent(
                AnalysisEvent.MakeId(EventTypes.Swear, t.JobId, h.Start, $"{h.Segment}:{h.Word}"), EventTypes.Swear, at, Utc(h.End),
                SwearDetector.Severity(h.Tier, before), h.Confidence, SwearDetector.Name, v, EventSources.Transcript, t.Recorder, t.JobId,
                R(h.Start), R(h.End - h.Start), $"{h.Tier}: {h.Text}",
                new Dictionary<string, string>
                {
                    ["tier"] = h.Tier,
                    ["word"] = h.Text,
                    ["form"] = h.Form,
                    ["masked"] = h.Masked ? "true" : "false",
                    ["beforeWatershed"] = before ? "true" : "false",
                    ["watershedStartUtc"] = watershed.StartUtcOn(at).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
                    ["context"] = h.Context,
                    ["speechOrLyrics"] = "unknown",
                }));
        }

        var fillers = FillerDetector.Detect(t, c.Fillers);
        foreach (var h in fillers.Hits)
        {
            events.Add(new AnalysisEvent(
                AnalysisEvent.MakeId(EventTypes.Filler, t.JobId, h.Start, $"{h.Segment}:{h.Word}"), EventTypes.Filler, Utc(h.Start), Utc(h.End),
                Severities.Info, h.Confidence, FillerDetector.Name, v, EventSources.Transcript, t.Recorder, t.JobId,
                R(h.Start), R(h.End - h.Start), h.Canonical,
                new Dictionary<string, string> { ["word"] = h.Text, ["filler"] = h.Canonical, ["estimate"] = "true" }));
        }

        var stats = new Dictionary<string, double>
        {
            ["speechSeconds"] = R(fillers.SpeechSeconds),
            ["fillers"] = fillers.Hits.Count,
            ["swears"] = swears.Count,
            ["swearsBeforeWatershed"] = beforeCount,
            ["clippingEvents"] = clipping.Events.Count,
            ["clippedMs"] = R(clipping.Events.Sum(e => e.DurationMs)),
            ["clippingTruncated"] = clipping.Truncated ? 1 : 0,
            ["deadAirEvents"] = deadAir.Count,
            ["deadAirSeconds"] = R(deadAir.Sum(e => e.Seconds)),
        };
        var versions = new Dictionary<string, string>
        {
            ["detectors"] = v,
            ["transcript"] = t.TranscriptVersion,
            ["engine"] = $"{t.Engine} {t.Model}",
            [ClippingDetector.Name] = ClippingDetector.Version,
            [DeadAirDetector.Name] = DeadAirDetector.Version,
            [SwearDetector.Name] = SwearDetector.Version + " list " + config.Swears.Version,
            [FillerDetector.Name] = FillerDetector.Version,
        };
        var coverage = TranscriptSlicer.Coverage(t);

        return new EventsDocument(EventsDocument.CurrentSchema, t.Station, "segment:" + t.JobId, t.AudioFromUtc, t.AudioToUtc, now,
            versions, coverage, stats, events.OrderBy(e => e.StartUtc).ThenBy(e => e.Type, StringComparer.Ordinal).ToList());
    }

    static double R(double x) => Math.Round(x, 3);
}
```

`src/WebAudioArchive.Analysis/Work/SegmentPipeline.cs`:
```csharp
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis.Work;

public sealed class DiskLowException(string message) : Exception(message);

/// <summary>What one call did. Status is a JobStatus value, or "paused-disk" when the disk guard stopped it.</summary>
public sealed record JobOutcome(string JobId, JobAction Action, string Status, string Reason, double AudioSeconds, double TranscribeSeconds, double WallSeconds)
{
    public const string PausedDisk = "paused-disk";

    /// <summary>Transcription seconds per audio second (0.05 is 20 times faster than real time), for finished runs.</summary>
    public double? RealtimeFactor => Action == JobAction.Run && Status == JobStatus.Done && AudioSeconds > 0 ? TranscribeSeconds / AudioSeconds : null;
}

/// <summary>
/// One segment, idempotent and resumable: decide from state.json; copy and decode into scratch; save features; send
/// each chunk not yet done to the transcriber, saving progress after each; assemble transcript.json; run the
/// detectors into events.json; mark done. Scratch is released whatever happens. The archive is only read.
/// </summary>
public sealed class SegmentPipeline(
    AnalysisPaths paths,
    LoadedConfig config,
    ScratchSpace scratch,
    SegmentDecoder decoder,
    ITranscriber transcriber,
    DiskGuard disk,
    Watershed watershed,
    Func<DateTimeOffset, string?> showTitleAt,
    IClock clock,
    ILogger log)
{
    public string TranscriptVersion => $"{transcriber.Engine}|{transcriber.Model}|{config.Config.Transcription.TranscriptKey()}";

    public JobDecision Decide(SegmentJob job) =>
        JobRules.Decide(JobStore.Read(paths.SegmentDir(job.Source.JobId)), job.Source.Fingerprint, TranscriptVersion, config.DetectorVersion, clock.UtcNow);

    /// <summary>Rebuilds a finished job's events.json (and its transcript's suppression flags) from the stored transcript and features: no whisper, no audio.</summary>
    public bool RebuildEvents(SegmentJob job)
    {
        var dir = paths.SegmentDir(job.Source.JobId);
        var state = JobStore.Read(dir);
        if (state is null || state.Status != JobStatus.Done) return false;
        return Redetect(job.Source, dir, state, new JobDecision(JobAction.Redetect, "rebuild")).Status == JobStatus.Done;
    }

    public async Task<JobOutcome> ProcessAsync(SegmentJob job, CancellationToken ct)
    {
        var s = job.Source;
        var dir = paths.SegmentDir(s.JobId);
        var state = JobStore.Read(dir);
        var decision = JobRules.Decide(state, s.Fingerprint, TranscriptVersion, config.DetectorVersion, clock.UtcNow);
        if (decision.Action is JobAction.Skip or JobAction.Wait)
            return new JobOutcome(s.JobId, decision.Action, state?.Status ?? "none", decision.Reason, 0, 0, 0);
        if (decision.Action == JobAction.Redetect)
            return Redetect(s, dir, state!, decision);
        if (disk.IsLow())
            return new JobOutcome(s.JobId, decision.Action, JobOutcome.PausedDisk, "free disk is below the threshold", 0, 0, 0);

        var wall = Stopwatch.StartNew();
        var resumable = state is not null && state.Fingerprint == s.Fingerprint && state.TranscriptVersion == TranscriptVersion;
        var partial = resumable ? LoadPartial(dir, s.Fingerprint) : null;
        var attempts = resumable ? state!.Attempts : 0;
        var current = new JobState(JobState.CurrentSchema, s.JobId, s.Fingerprint, TranscriptVersion, config.DetectorVersion, JobStatus.Running,
            attempts, null, null, partial?.Chunks.Select(c => c.Index).Order().ToList() ?? [], partial?.ChunkCount ?? 0, clock.UtcNow,
            s.Meta.AudioSeconds, partial?.TranscribeSeconds ?? 0, 0);
        JobStore.Write(dir, current);
        log.LogInformation("Analysing {Job} ({Reason})", s.JobId, decision.Reason);

        try
        {
            using var lease = scratch.Acquire(s.JobId, SegmentDecoder.ScratchBytesFor(s.Meta.Bytes, s.Meta.AudioSeconds));
            var decoded = await decoder.DecodeAsync(s.AudioPath, s.Meta.Bytes, s.SampleRate, s.Channels, lease, ct);
            AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.FeaturesFile), decoded.Features);

            var anchors = SegmentTimeline.Anchors(s.Meta);
            var o = config.Config.Transcription;
            var chunks = ChunkPlanner.Plan(decoded.Features, o.ChunkSeconds, o.BoundarySearchSeconds);
            if (partial is not null && partial.ChunkCount != chunks.Count) partial = null;
            partial ??= new TranscriptPartial(TranscriptPartial.CurrentSchema, s.Fingerprint, TranscriptVersion, chunks.Count, 0, []);
            var wav = WavFile.ReadInfo(decoded.SpeechWavPath);

            foreach (var chunk in chunks)
            {
                if (partial.Chunks.Any(c => c.Index == chunk.Index)) continue;
                ct.ThrowIfCancellationRequested();
                if (disk.IsLow()) throw new DiskLowException("free disk fell below the threshold");
                var title = showTitleAt(SegmentTimeline.ToUtc(anchors, (chunk.Start + chunk.End) / 2));
                var audio = WavFile.Slice(decoded.SpeechWavPath, wav, chunk.Start, chunk.End);
                var started = Stopwatch.GetTimestamp();
                var raw = await transcriber.TranscribeAsync(new TranscriptionRequest(audio, PromptBuilder.Build(o, title), o.Language), ct);
                var took = Stopwatch.GetElapsedTime(started).TotalSeconds;
                var shifted = raw.Select(r => r.Shift(chunk.Start)).Where(r => r.Start < chunk.End + 1).ToList();
                partial = partial with
                {
                    TranscribeSeconds = partial.TranscribeSeconds + took,
                    Chunks = [.. partial.Chunks, new PartialChunk(chunk.Index, chunk.Start, chunk.End, shifted)],
                };
                AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.PartialFile), partial);
                current = current with
                {
                    ChunksDone = partial.Chunks.Select(c => c.Index).Order().ToList(),
                    ChunkCount = chunks.Count,
                    TranscribeSeconds = partial.TranscribeSeconds,
                    UpdatedUtc = clock.UtcNow,
                };
                JobStore.Write(dir, current);
            }

            var transcript = TranscriptAssembler.Build(s, paths.Station, anchors, partial.Chunks.OrderBy(c => c.Index).SelectMany(c => c.Segments),
                o, transcriber.Engine, transcriber.Model, TranscriptVersion, decoded.Decoder, clock.UtcNow, partial.TranscribeSeconds);
            AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.TranscriptFile), transcript);
            AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.EventsFile), DetectorRunner.Run(transcript, decoded.Features, config, watershed, clock.UtcNow));
            File.Delete(Path.Combine(dir, AnalysisPaths.PartialFile));

            current = current with { Status = JobStatus.Done, LastError = null, NextAttemptUtc = null, WallSeconds = Math.Round(wall.Elapsed.TotalSeconds, 3), UpdatedUtc = clock.UtcNow };
            JobStore.Write(dir, current);
            return new JobOutcome(s.JobId, JobAction.Run, JobStatus.Done, decision.Reason, s.Meta.AudioSeconds, partial.TranscribeSeconds, current.WallSeconds);
        }
        catch (TranscriberUnavailableException ex)
        {
            return Pause(s, dir, current, JobStatus.Waiting, ex.Message, clock.UtcNow + JobRules.UnavailableRetry);
        }
        catch (ScratchFullException ex)
        {
            return Pause(s, dir, current, JobStatus.Waiting, ex.Message, clock.UtcNow + JobRules.UnavailableRetry);
        }
        catch (DiskLowException ex)
        {
            Pause(s, dir, current, JobStatus.Waiting, ex.Message, null);
            return new JobOutcome(s.JobId, JobAction.Run, JobOutcome.PausedDisk, ex.Message, 0, 0, 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            JobStore.Write(dir, current with { Status = JobStatus.Waiting, LastError = "stopped", UpdatedUtc = clock.UtcNow });
            throw;
        }
        catch (Exception ex) when (ex is DecodeException or TranscriptionFailedException or InvalidDataException or IOException or JsonException)
        {
            var failed = current with
            {
                Status = JobStatus.Failed,
                Attempts = current.Attempts + 1,
                LastError = ex.Message,
                NextAttemptUtc = JobRules.NextAttempt(current.Attempts + 1, clock.UtcNow),
                UpdatedUtc = clock.UtcNow,
            };
            JobStore.Write(dir, failed);
            log.LogWarning("Analysis of {Job} failed (attempt {Attempt}): {Error}", s.JobId, failed.Attempts, ex.Message);
            return new JobOutcome(s.JobId, JobAction.Run, JobStatus.Failed, ex.Message, 0, 0, 0);
        }
    }

    JobOutcome Pause(SourceSegment s, string dir, JobState current, string status, string reason, DateTimeOffset? next)
    {
        JobStore.Write(dir, current with { Status = status, LastError = reason, NextAttemptUtc = next, UpdatedUtc = clock.UtcNow });
        log.LogInformation("Analysis of {Job} paused: {Reason}", s.JobId, reason);
        return new JobOutcome(s.JobId, JobAction.Run, status, reason, 0, 0, 0);
    }

    JobOutcome Redetect(SourceSegment s, string dir, JobState state, JobDecision decision)
    {
        var transcript = AnalysisJson.TryRead<TranscriptDocument>(Path.Combine(dir, AnalysisPaths.TranscriptFile), out _);
        var features = AnalysisJson.TryRead<AudioFeatures>(Path.Combine(dir, AnalysisPaths.FeaturesFile), out _);
        if (transcript is null || features is null)
        {
            // Results are missing or damaged: forget the state so the next pass runs the job in full.
            File.Delete(Path.Combine(dir, AnalysisPaths.StateFile));
            return new JobOutcome(s.JobId, JobAction.Redetect, "none", "results missing, the job runs again", 0, 0, 0);
        }

        var refiltered = TranscriptAssembler.Refilter(transcript, config.Config.Transcription);
        AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.TranscriptFile), refiltered);
        AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.EventsFile), DetectorRunner.Run(refiltered, features, config, watershed, clock.UtcNow));
        JobStore.Write(dir, state with { DetectorVersion = config.DetectorVersion, UpdatedUtc = clock.UtcNow });
        return new JobOutcome(s.JobId, JobAction.Redetect, JobStatus.Done, decision.Reason, 0, 0, 0);
    }

    static TranscriptPartial? LoadPartial(string dir, string fingerprint)
    {
        var p = AnalysisJson.TryRead<TranscriptPartial>(Path.Combine(dir, AnalysisPaths.PartialFile), out _);
        return p is not null && p.Fingerprint == fingerprint ? p : null;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~SegmentPipelineTests`
Expected: PASS, 8 tests. Each test encodes a 70 s MP3 once (about 3 s with GroovyMp3). If the dead-air times are a few tens of milliseconds outside the ranges, remember the encoder delay: GroovyMp3 output decoded by the managed decoder starts about 25 ms late (no LAME-header trimming, measured in D18); do not widen the ranges by more than 0.1 s.

Then run the whole suite: `dotnet test`. Expected: all tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Transcription/TranscriptAssembler.cs src/WebAudioArchive.Analysis/Detectors/DetectorRunner.cs src/WebAudioArchive.Analysis/Work/SegmentPipeline.cs tests/WebAudioArchive.Tests/Support/AnalysisFixtures.cs tests/WebAudioArchive.Tests/SegmentPipelineTests.cs
git commit -m "Analysis: resumable, idempotent segment pipeline writing transcript.json and events.json (#77, #82)"
```

---

### Task 13: Schedule types to Core, the day timeline and per-show analysis

**Files:**
- Move: `src/WebAudioArchive.Web/Schedule/WeeklySchedule.cs` to `src/WebAudioArchive.Core/Schedule/WeeklySchedule.cs`; `src/WebAudioArchive.Web/Schedule/ScheduleExpander.cs` to `src/WebAudioArchive.Core/Schedule/ScheduleExpander.cs` (namespace line only)
- Create: `src/WebAudioArchive.Core/Schedule/SavedScheduleFile.cs`, `src/WebAudioArchive.Core/Analysis/ShowAnalysis.cs`
- Modify: `src/WebAudioArchive.Web/Schedule/ScheduleService.cs:180,202,211-235`, `src/WebAudioArchive.Web/Components/Pages/Recordings.razor.cs` (one fully qualified `WeeklySchedule` reference), `src/WebAudioArchive.Web/WebAudioArchive.Web.csproj`, `tests/WebAudioArchive.Tests/WebAudioArchive.Tests.csproj`
- Create: `src/WebAudioArchive.Analysis/Shows/ScheduleProvider.cs`, `DayTimeline.cs`, `ShowAnalysisBuilder.cs`, `ShowProcessor.cs` (under `src/WebAudioArchive.Analysis/Shows/`)
- Test: `tests/WebAudioArchive.Tests/ShowAnalysisTests.cs`

**Interfaces:**
- Consumes: `EventsDocument`, `AnalysisEvent`, `SourceSpan`, `RecorderPreference`, `TranscriptSlicer`, `TimedLine`, `AnalysisPaths`, `AnalysisJson` (Task 3); `StationConfigLoader` (Core); `IClock`.
- Produces:
  - Namespace `WebAudioArchive.Core.Schedule`: `ScheduleSlot`, `WeeklySchedule`, `ScheduleParseResult`, `ShowOccurrence`, `ScheduleExpander` (unchanged APIs, new namespace; Web and Tests get it as a global using); `sealed record SavedScheduleFile(string Station, string? Url, string? Timezone, DateTimeOffset FetchedUtc, List<SavedScheduleSlot> Slots)` with `static JsonSerializerOptions Json`, `static SavedScheduleFile From(string station, string? url, string? timezone, WeeklySchedule schedule, DateTimeOffset fetched)`, `WeeklySchedule? ToSchedule()`, `static SavedScheduleFile? Read(string path)`; `sealed record SavedScheduleSlot(string Day, string LocalStart, int Minutes, string Title)`.
  - `sealed record ShowSource(string Recorder, string JobId, DateTimeOffset FromUtc, DateTimeOffset ToUtc)`; `sealed record ShowCounts(int SwearsMild, int SwearsStrong, int SwearsSevere, int SwearsBeforeWatershed, int ClippingEvents, double ClippedMs, int DeadAirEvents, double DeadAirSeconds, int Fillers, double SpeechMinutes, double? FillersPerMinute, double AnalysedSeconds)`; `sealed record ShowAnalysis(int Schema, string ShowId, string Station, string Title, DateTimeOffset StartUtc, DateTimeOffset EndUtc, DateTimeOffset WindowFromUtc, DateTimeOffset WindowToUtc, DateTimeOffset BuiltUtc, bool Complete, IReadOnlyList<ShowSource> Sources, ShowCounts Counts, IReadOnlyList<string> Notes)` with `const int CurrentSchema = 1`.
  - `sealed class ScheduleProvider(string? scheduleFile, string? stationsConfig, string station, ILogger log) { TimeZoneInfo Zone; DateTimeOffset? FetchedUtc; IReadOnlyList<ShowOccurrence> Occurrences(DateTimeOffset fromUtc, DateTimeOffset toUtc); string? TitleAt(DateTimeOffset utc); }`
  - `static class DayTimeline { IReadOnlyList<EventsDocument> ReadSegments(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc); IReadOnlyList<AnalysisEvent> Merge(IReadOnlyList<EventsDocument> segments, IReadOnlyList<string> preference); EventsDocument BuildDay(string station, DateOnly day, IReadOnlyList<EventsDocument> segments, IReadOnlyList<string> preference, DateTimeOffset now); void RebuildDay(AnalysisPaths paths, DateOnly day, IReadOnlyList<string> preference, DateTimeOffset now); }` (an event is filed in the UTC day of its start)
  - `static class ShowAnalysisBuilder { ShowAnalysis Build(ShowOccurrence show, TimeSpan margin, IReadOnlyList<AnalysisEvent> events, IReadOnlyList<TimedLine> lines, IReadOnlyList<ShowSource> sources, bool complete, DateTimeOffset now); }`
  - `sealed class ShowProcessor(AnalysisPaths paths, ScheduleProvider schedule, IReadOnlyList<string> preference, TimeSpan margin, IClock clock, ILogger log) { IReadOnlyList<ShowAnalysis> Update(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<SourceSpan> pending); }`

- [ ] **Step 1: Pull, then move the schedule types to Core**

Run:
```powershell
git pull --rebase
git log --oneline 19a06f0..HEAD -- src/WebAudioArchive.Web/Schedule/WeeklySchedule.cs src/WebAudioArchive.Web/Schedule/ScheduleExpander.cs src/WebAudioArchive.Web/Schedule/ScheduleService.cs src/WebAudioArchive.Web/Components/Pages/Recordings.razor.cs
New-Item -ItemType Directory -Force src/WebAudioArchive.Core/Schedule | Out-Null
git mv src/WebAudioArchive.Web/Schedule/WeeklySchedule.cs src/WebAudioArchive.Core/Schedule/WeeklySchedule.cs
git mv src/WebAudioArchive.Web/Schedule/ScheduleExpander.cs src/WebAudioArchive.Core/Schedule/ScheduleExpander.cs
```
Expected: the `git log` lists any commits the web UI work made to these files since this plan was written; read them so the edits below are applied to the current text. The two `git mv` succeed.

In both moved files replace the line `namespace WebAudioArchive.Web.Schedule;` with `namespace WebAudioArchive.Core.Schedule;`. Nothing else in them changes.

In `src/WebAudioArchive.Web/WebAudioArchive.Web.csproj` and in `tests/WebAudioArchive.Tests/WebAudioArchive.Tests.csproj` add:
```xml
  <ItemGroup>
    <Using Include="WebAudioArchive.Core.Schedule" />
  </ItemGroup>
```

In `src/WebAudioArchive.Web/Components/Pages/Recordings.razor.cs` replace `WebAudioArchive.Web.Schedule.WeeklySchedule.DayIndex(` with `WebAudioArchive.Core.Schedule.WeeklySchedule.DayIndex(`.

`src/WebAudioArchive.Core/Schedule/SavedScheduleFile.cs`:
```csharp
using System.Globalization;
using System.Text.Json;

namespace WebAudioArchive.Core.Schedule;

/// <summary>
/// The last good schedule as the web app saves it in its cache volume (cache/schedule/&lt;station&gt;.json) and the analysis
/// service reads it (mounted read-only). Slot times are the station page's local times; nothing here is shown.
/// </summary>
public sealed record SavedScheduleFile(string Station, string? Url, string? Timezone, DateTimeOffset FetchedUtc, List<SavedScheduleSlot> Slots)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static SavedScheduleFile From(string station, string? url, string? timezone, WeeklySchedule schedule, DateTimeOffset fetched) => new(
        station, url, timezone, fetched.ToUniversalTime(),
        schedule.Slots.Select(x => new SavedScheduleSlot(x.Day.ToString(), x.LocalStart.ToString("HH:mm", CultureInfo.InvariantCulture), (int)x.Duration.TotalMinutes, x.Title)).ToList());

    public static SavedScheduleFile? Read(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<SavedScheduleFile>(File.ReadAllText(path), Json) : null;

    public WeeklySchedule? ToSchedule()
    {
        if (Slots is null || Slots.Count == 0) return null;
        var slots = new List<ScheduleSlot>();
        foreach (var s in Slots)
        {
            if (!Enum.TryParse<DayOfWeek>(s.Day, out var day)
                || !TimeOnly.TryParseExact(s.LocalStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
                || s.Minutes <= 0 || string.IsNullOrWhiteSpace(s.Title))
                return null;
            slots.Add(new ScheduleSlot(day, start, TimeSpan.FromMinutes(s.Minutes), s.Title));
        }

        return new WeeklySchedule(slots);
    }
}

public sealed record SavedScheduleSlot(string Day, string LocalStart, int Minutes, string Title);
```

In `src/WebAudioArchive.Web/Schedule/ScheduleService.cs`:
- replace `var saved = JsonSerializer.Deserialize<SavedSchedule>(File.ReadAllText(SavedPath), Json);` with `var saved = JsonSerializer.Deserialize<SavedScheduleFile>(File.ReadAllText(SavedPath), Json);`
- replace `File.WriteAllText(tmp, JsonSerializer.Serialize(SavedSchedule.From(source, schedule, fetched), Json));` with `File.WriteAllText(tmp, JsonSerializer.Serialize(SavedScheduleFile.From(source.Station, source.Url?.ToString(), source.Zone?.Id, schedule, fetched), Json));`
- delete the nested records from the line `/// <summary>The last good schedule as kept in the cache volume. Slot times are the page's local times.</summary>` down to and including `sealed record SavedSlot(string Day, string LocalStart, int Minutes, string Title);` (the class's closing brace stays).

Run: `dotnet test`
Expected: every existing test passes (the JSON shape of the saved schedule is unchanged, so a cache written by the old code still loads). Commit this move on its own:
```powershell
git add src/WebAudioArchive.Core/Schedule src/WebAudioArchive.Web/Schedule/ScheduleService.cs src/WebAudioArchive.Web/Components/Pages/Recordings.razor.cs src/WebAudioArchive.Web/WebAudioArchive.Web.csproj tests/WebAudioArchive.Tests/WebAudioArchive.Tests.csproj
git commit -m "Move the weekly schedule types and the saved schedule format to Core so analysis can map shows (#9)"
```
(`git mv` already staged the moves.)

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/ShowAnalysisTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class ShowAnalysisTests : IDisposable
{
    // Thursday 15 October 2026, British Summer Time: Backseat Aux at 20:00 local is 19:00 to 20:00 UTC.
    static readonly DateTimeOffset Show = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    readonly string _root = Directory.CreateTempSubdirectory("waa-shows-").FullName;
    readonly ManualClock _clock = new(Show.AddHours(3));

    public void Dispose() => Directory.Delete(_root, recursive: true);

    AnalysisPaths Paths => new(Path.Combine(_root, "analysis"), "burst");

    const string SavedSchedule = """
        { "station": "burst", "url": "https://www.burstradio.org/schedule", "timezone": "Europe/London",
          "fetchedUtc": "2026-10-15T12:00:00+00:00",
          "slots": [ { "day": "Thursday", "localStart": "20:00", "minutes": 60, "title": "Backseat Aux" },
                     { "day": "Thursday", "localStart": "21:00", "minutes": 60, "title": "Late Show" } ] }
        """;

    string WriteSchedule()
    {
        var path = Path.Combine(_root, "burst.json");
        File.WriteAllText(path, SavedSchedule);
        return path;
    }

    static AnalysisEvent Event(string type, DateTimeOffset at, string recorder = "a", string? tier = null, bool before = false, double seconds = 0.4) => new(
        AnalysisEvent.MakeId(type, recorder + "/job", (at - Show).TotalSeconds), type, at, at.AddSeconds(seconds), Severities.Medium, 0.9, type, "1",
        EventSources.Transcript, recorder, recorder + "/job", 0, seconds, type,
        tier is null ? null : new Dictionary<string, string> { ["tier"] = tier, ["beforeWatershed"] = before ? "true" : "false" });

    static EventsDocument Segment(string recorder, DateTimeOffset from, DateTimeOffset to, params AnalysisEvent[] events) => new(
        1, "burst", $"segment:{recorder}/{from:yyyy}/{from:MM-dd}/{from:HHmmss}", from, to, from, new Dictionary<string, string> { ["detectors"] = "dv" },
        [new SourceSpan(recorder, from, to)], null, events);

    [Fact]
    public void The_saved_schedule_written_by_the_web_app_maps_to_utc_shows()
    {
        var provider = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance);

        var shows = provider.Occurrences(Show.AddHours(-1), Show.AddHours(3));

        Assert.Equal(new[] { "Backseat Aux", "Late Show" }, shows.Select(s => s.Title));
        Assert.Equal(Show, shows[0].StartUtc);
        Assert.Equal("burst-20261015T1900Z", shows[0].Id);
        Assert.Equal("Backseat Aux", provider.TitleAt(Show.AddMinutes(30)));
        Assert.Null(provider.TitleAt(Show.AddHours(-3)));
        Assert.Equal("Europe/London", provider.Zone.Id);
    }

    [Fact]
    public void No_schedule_file_means_no_shows_and_no_titles()
    {
        var provider = new ScheduleProvider(Path.Combine(_root, "missing.json"), null, "burst", NullLogger.Instance);
        Assert.Empty(provider.Occurrences(Show, Show.AddDays(1)));
        Assert.Null(provider.TitleAt(Show));
    }

    [Fact]
    public void The_day_timeline_prefers_a_and_files_each_event_by_its_start()
    {
        var day = new DateOnly(2026, 10, 15);
        var a = Segment("a", new(2026, 10, 15, 23, 0, 0, TimeSpan.Zero), new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero),
            Event(EventTypes.Swear, new(2026, 10, 15, 23, 59, 59, TimeSpan.Zero)));
        var b = Segment("b", new(2026, 10, 15, 23, 30, 0, TimeSpan.Zero), new(2026, 10, 16, 0, 30, 0, TimeSpan.Zero),
            Event(EventTypes.Swear, new(2026, 10, 15, 23, 45, 0, TimeSpan.Zero), "b"),
            Event(EventTypes.Filler, new(2026, 10, 16, 0, 10, 0, TimeSpan.Zero), "b"));

        var d15 = DayTimeline.BuildDay("burst", day, [a, b], ["a", "b"], _clock.UtcNow);
        var d16 = DayTimeline.BuildDay("burst", day.AddDays(1), [a, b], ["a", "b"], _clock.UtcNow);

        Assert.Equal("day:2026-10-15", d15.Scope);
        Assert.Equal("a", Assert.Single(d15.Events).Recorder);
        Assert.Equal(EventTypes.Filler, Assert.Single(d16.Events).Type);
        Assert.Single(DayTimeline.Merge([a, a], ["a", "b"]));
    }

    [Fact]
    public void Show_counts_cover_the_show_proper_and_the_filler_rate_is_per_minute_of_speech()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        AnalysisEvent[] events =
        [
            Event(EventTypes.Swear, Show.AddMinutes(10), tier: "strong", before: true),
            Event(EventTypes.Swear, Show.AddMinutes(-2), tier: "mild"),
            Event(EventTypes.Clipping, Show.AddMinutes(30), seconds: 0.12),
            Event(EventTypes.DeadAir, Show.AddMinutes(40), seconds: 6),
            Event(EventTypes.Filler, Show.AddMinutes(5)),
            Event(EventTypes.Filler, Show.AddMinutes(6)),
            Event(EventTypes.Filler, Show.AddMinutes(7)),
        ];
        var lines = Enumerable.Range(0, 10)
            .Select(i => new TimedLine(Show.AddMinutes(i * 5), Show.AddMinutes(i * 5).AddSeconds(30), "words", "a", "a/job", []))
            .ToList();

        var doc = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), events, lines,
            [new ShowSource("a", "a/2026/10-15/190000", Show, Show.AddHours(1))], complete: true, _clock.UtcNow);

        Assert.Equal(Show.AddMinutes(-5), doc.WindowFromUtc);
        Assert.Equal(1, doc.Counts.SwearsStrong);
        Assert.Equal(0, doc.Counts.SwearsMild);
        Assert.Equal(1, doc.Counts.SwearsBeforeWatershed);
        Assert.Equal(1, doc.Counts.ClippingEvents);
        Assert.Equal(120, doc.Counts.ClippedMs, 3);
        Assert.Equal(6, doc.Counts.DeadAirSeconds, 3);
        Assert.Equal(3, doc.Counts.Fillers);
        Assert.Equal(5, doc.Counts.SpeechMinutes, 6);
        Assert.Equal(0.6, doc.Counts.FillersPerMinute!.Value, 6);
        Assert.Equal(3600, doc.Counts.AnalysedSeconds, 3);
        Assert.Contains(doc.Notes, n => n.Contains("estimate"));
    }

    [Fact]
    public void Less_than_a_minute_of_speech_gives_no_filler_rate()
    {
        var occurrence = new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance).Occurrences(Show, Show.AddMinutes(1))[0];
        var doc = ShowAnalysisBuilder.Build(occurrence, TimeSpan.FromMinutes(5), [Event(EventTypes.Filler, Show.AddMinutes(1))],
            [new TimedLine(Show, Show.AddSeconds(20), "um", "a", "a/job", [])], [], complete: true, _clock.UtcNow);
        Assert.Null(doc.Counts.FillersPerMinute);
        Assert.Contains(doc.Notes, n => n.Contains("not analysed"));
    }

    [Fact]
    public void The_processor_writes_day_files_and_finished_shows_from_segment_results()
    {
        var job = "a/2026/10-15/190000";
        var dir = Paths.SegmentDir(job);
        AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.EventsFile), Segment("a", Show, Show.AddHours(1), Event(EventTypes.Filler, Show.AddMinutes(3))) with { Scope = "segment:" + job });
        AnalysisJson.Write(Path.Combine(dir, AnalysisPaths.TranscriptFile), TranscriptFixtures.Of(Show, "um hello there", "more words here") with { JobId = job });
        var processor = new ShowProcessor(Paths, new ScheduleProvider(WriteSchedule(), null, "burst", NullLogger.Instance), ["a", "b"], TimeSpan.FromMinutes(5), _clock, NullLogger.Instance);

        var docs = processor.Update(Show, Show.AddHours(3), pending: []);

        var backseat = Assert.Single(docs, d => d.Title == "Backseat Aux");
        Assert.True(backseat.Complete);
        Assert.Equal(1, backseat.Counts.Fillers);
        Assert.True(File.Exists(Path.Combine(Paths.ShowDir("burst-20261015T1900Z", Show), AnalysisPaths.ShowAnalysisFile)));
        Assert.True(File.Exists(Paths.DayEvents(new DateOnly(2026, 10, 15))));

        var pending = processor.Update(Show, Show.AddHours(3), pending: [new SourceSpan("a", Show.AddMinutes(50), Show.AddHours(2))]);
        Assert.False(Assert.Single(pending, d => d.Title == "Backseat Aux").Complete);

        _clock.Set(Show.AddMinutes(30));
        Assert.Empty(processor.Update(Show, Show.AddHours(3), pending: []));
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~ShowAnalysisTests`
Expected: build FAILS with `CS0246` for `ScheduleProvider`, `DayTimeline`, `ShowAnalysisBuilder`, `ShowProcessor` and `ShowSource`.

- [ ] **Step 4: Implement the show documents, the provider, the timeline and the processor**

`src/WebAudioArchive.Core/Analysis/ShowAnalysis.cs`:
```csharp
namespace WebAudioArchive.Core.Analysis;

/// <summary>An analysed segment that overlaps a show's window.</summary>
public sealed record ShowSource(string Recorder, string JobId, DateTimeOffset FromUtc, DateTimeOffset ToUtc);

/// <summary>
/// Per-show figures for the weekly report (spec 3.8), over the show proper (start to end, not the margins). Counts are
/// detections, not proven events. FillersPerMinute is per minute of transcribed speech, an estimate, null under a minute.
/// AnalysedSeconds is how much of the show proper had analysed audio.
/// </summary>
public sealed record ShowCounts(
    int SwearsMild,
    int SwearsStrong,
    int SwearsSevere,
    int SwearsBeforeWatershed,
    int ClippingEvents,
    double ClippedMs,
    int DeadAirEvents,
    double DeadAirSeconds,
    int Fillers,
    double SpeechMinutes,
    double? FillersPerMinute,
    double AnalysedSeconds);

/// <summary>show-analysis.json: one scheduled show mapped onto the analysed segments. Rebuilt whenever its segments change.</summary>
public sealed record ShowAnalysis(
    int Schema,
    string ShowId,
    string Station,
    string Title,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    DateTimeOffset WindowFromUtc,
    DateTimeOffset WindowToUtc,
    DateTimeOffset BuiltUtc,
    bool Complete,
    IReadOnlyList<ShowSource> Sources,
    ShowCounts Counts,
    IReadOnlyList<string> Notes)
{
    public const int CurrentSchema = 1;
}
```

`src/WebAudioArchive.Analysis/Shows/ScheduleProvider.cs`:
```csharp
using System.Text.Json;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Config;
using WebAudioArchive.Core.Schedule;

namespace WebAudioArchive.Analysis.Shows;

/// <summary>
/// The station's shows in UTC, from the schedule the web app saved (read-only), reloaded when the file changes. The
/// zone comes from the stations config (Europe/London when it is not given). No file means no shows: segments are still
/// analysed, they just map to unassigned time.
/// </summary>
public sealed class ScheduleProvider
{
    readonly string? _file;
    readonly string _station;
    readonly ILogger _log;
    readonly object _gate = new();
    DateTime _loadedWrite;
    WeeklySchedule? _schedule;

    public ScheduleProvider(string? scheduleFile, string? stationsConfig, string station, ILogger log)
    {
        _file = scheduleFile;
        _station = station;
        _log = log;
        Zone = LoadZone(stationsConfig, station, log);
    }

    public TimeZoneInfo Zone { get; }

    public DateTimeOffset? FetchedUtc { get; private set; }

    public IReadOnlyList<ShowOccurrence> Occurrences(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        lock (_gate)
        {
            Reload();
            return _schedule is null ? [] : ScheduleExpander.Expand(_schedule, Zone, _station, fromUtc, toUtc);
        }
    }

    public string? TitleAt(DateTimeOffset utc) =>
        Occurrences(utc.AddDays(-1), utc.AddSeconds(1)).LastOrDefault(o => o.StartUtc <= utc && utc < o.EndUtc)?.Title;

    void Reload()
    {
        if (_file is null) return;
        try
        {
            var info = new FileInfo(_file);
            if (!info.Exists || info.LastWriteTimeUtc == _loadedWrite) return;
            _loadedWrite = info.LastWriteTimeUtc;
            var saved = SavedScheduleFile.Read(_file);
            var schedule = saved?.ToSchedule();
            if (saved is null || schedule is null || saved.Station != _station)
            {
                _log.LogWarning("Ignoring the saved schedule {Path}: it is not a valid schedule for {Station}", _file, _station);
                return;
            }

            _schedule = schedule;
            FetchedUtc = saved.FetchedUtc;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            _log.LogWarning("Could not read the saved schedule {Path}: {Error}", _file, ex.Message);
        }
    }

    static TimeZoneInfo LoadZone(string? stationsConfig, string station, ILogger log)
    {
        var id = "Europe/London";
        if (stationsConfig is not null)
        {
            try
            {
                id = StationConfigLoader.Load(stationsConfig).FirstOrDefault(s => s.Id == station)?.Timezone ?? id;
            }
            catch (ConfigException ex)
            {
                log.LogWarning("Stations config {Path} not usable ({Error}); using {Zone} for the watershed and schedule", stationsConfig, ex.Message, id);
            }
        }

        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }
}
```

`src/WebAudioArchive.Analysis/Shows/DayTimeline.cs`:
```csharp
using System.Globalization;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Shows;

/// <summary>
/// events/&lt;yyyy&gt;/&lt;MM-dd&gt;.json: every segment's events for one UTC day, merged, with the preferred recorder's events
/// wherever it has audio (D7), each event filed in the day of its start. Rebuilt from the segment files at any time.
/// </summary>
public static class DayTimeline
{
    public static IReadOnlyList<EventsDocument> ReadSegments(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var docs = new List<EventsDocument>();
        if (!Directory.Exists(paths.SegmentsDir)) return docs;
        var first = DateOnly.FromDateTime(fromUtc.UtcDateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(toUtc.UtcDateTime);
        foreach (var recorder in Directory.EnumerateDirectories(paths.SegmentsDir))
        {
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                var dayDir = Path.Combine(recorder, d.ToString("yyyy", CultureInfo.InvariantCulture), d.ToString("MM-dd", CultureInfo.InvariantCulture));
                if (!Directory.Exists(dayDir)) continue;
                foreach (var job in Directory.EnumerateDirectories(dayDir))
                {
                    var doc = AnalysisJson.TryRead<EventsDocument>(Path.Combine(job, AnalysisPaths.EventsFile), out _);
                    if (doc is not null && doc.FromUtc < toUtc && doc.ToUtc > fromUtc) docs.Add(doc);
                }
            }
        }

        return docs;
    }

    public static IReadOnlyList<AnalysisEvent> Merge(IReadOnlyList<EventsDocument> segments, IReadOnlyList<string> preference)
    {
        var spans = segments.SelectMany(s => s.Coverage).ToList();
        return segments
            .SelectMany(s => s.Events)
            .Where(e => RecorderPreference.Keep(e.Recorder, e.StartUtc, spans, preference))
            .GroupBy(e => e.Id)
            .Select(g => g.First())
            .OrderBy(e => e.StartUtc)
            .ThenBy(e => e.Type, StringComparer.Ordinal)
            .ToList();
    }

    public static EventsDocument BuildDay(string station, DateOnly day, IReadOnlyList<EventsDocument> segments, IReadOnlyList<string> preference, DateTimeOffset now)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        var events = Merge(segments, preference).Where(e => e.StartUtc >= from && e.StartUtc < to).ToList();
        var versions = new Dictionary<string, string>
        {
            ["detectors"] = string.Join(' ', segments.Select(s => s.Versions.GetValueOrDefault("detectors")).OfType<string>().Distinct().Order(StringComparer.Ordinal)),
            ["segments"] = segments.Count.ToString(CultureInfo.InvariantCulture),
        };
        var coverage = segments.SelectMany(s => s.Coverage).Where(c => c.ToUtc > from && c.FromUtc < to).OrderBy(c => c.FromUtc).ToList();
        return new EventsDocument(EventsDocument.CurrentSchema, station, "day:" + day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            from, to, now, versions, coverage, null, events);
    }

    public static void RebuildDay(AnalysisPaths paths, DateOnly day, IReadOnlyList<string> preference, DateTimeOffset now)
    {
        var from = new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var segments = ReadSegments(paths, from, from.AddDays(1));
        AnalysisJson.Write(paths.DayEvents(day), BuildDay(paths.Station, day, segments, preference, now));
    }
}
```

`src/WebAudioArchive.Analysis/Shows/ShowAnalysisBuilder.cs`:
```csharp
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Schedule;

namespace WebAudioArchive.Analysis.Shows;

public static class ShowAnalysisBuilder
{
    public static ShowAnalysis Build(ShowOccurrence show, TimeSpan margin, IReadOnlyList<AnalysisEvent> events, IReadOnlyList<TimedLine> lines,
        IReadOnlyList<ShowSource> sources, bool complete, DateTimeOffset now)
    {
        var inShow = events.Where(e => e.StartUtc >= show.StartUtc && e.StartUtc < show.EndUtc).ToList();
        var swears = inShow.Where(e => e.Type == EventTypes.Swear).ToList();
        int Tier(string tier) => swears.Count(e => e.Evidence?.GetValueOrDefault("tier") == tier);
        var clipping = inShow.Where(e => e.Type == EventTypes.Clipping).ToList();
        var deadAir = inShow.Where(e => e.Type == EventTypes.DeadAir).ToList();
        var fillers = inShow.Count(e => e.Type == EventTypes.Filler);
        var speechMinutes = lines.Sum(l => Overlap(l.StartUtc, l.EndUtc, show.StartUtc, show.EndUtc)) / 60;
        var analysed = Union(sources.Select(s => (s.FromUtc, s.ToUtc)), show.StartUtc, show.EndUtc);

        var notes = new List<string> { "Counts are detections from speech to text and audio levels, not proven events; check each against the audio." };
        notes.Add(speechMinutes >= 1 ? "The filler rate is an estimate (whisper leaves out some fillers)." : "Less than a minute of speech was transcribed, so no filler rate is given.");
        if (analysed < show.Duration.TotalSeconds * 0.98)
            notes.Add($"Only {analysed / 60:0.#} of {show.Duration.TotalMinutes:0} minutes were analysed; the rest was not recorded or not analysed yet.");
        if (!complete) notes.Add("Analysis of this show is not finished; figures will change.");

        var counts = new ShowCounts(
            Tier(SwearTiers.Mild), Tier(SwearTiers.Strong), Tier(SwearTiers.Severe),
            swears.Count(e => e.Evidence?.GetValueOrDefault("beforeWatershed") == "true"),
            clipping.Count, Math.Round(clipping.Sum(e => (e.EndUtc - e.StartUtc).TotalMilliseconds), 3),
            deadAir.Count, Math.Round(deadAir.Sum(e => (e.EndUtc - e.StartUtc).TotalSeconds), 3),
            fillers, Math.Round(speechMinutes, 6), speechMinutes >= 1 ? Math.Round(fillers / speechMinutes, 6) : null,
            Math.Round(analysed, 3));

        return new ShowAnalysis(ShowAnalysis.CurrentSchema, show.Id, show.Station, show.Title, show.StartUtc, show.EndUtc,
            show.StartUtc - margin, show.EndUtc + margin, now, complete, sources, counts, notes);
    }

    static double Overlap(DateTimeOffset a0, DateTimeOffset a1, DateTimeOffset b0, DateTimeOffset b1)
    {
        var from = a0 > b0 ? a0 : b0;
        var to = a1 < b1 ? a1 : b1;
        return to > from ? (to - from).TotalSeconds : 0;
    }

    static double Union(IEnumerable<(DateTimeOffset From, DateTimeOffset To)> spans, DateTimeOffset from, DateTimeOffset to)
    {
        double total = 0;
        DateTimeOffset? runFrom = null, runTo = null;
        foreach (var s in spans.Select(s => (From: s.From < from ? from : s.From, To: s.To > to ? to : s.To)).Where(s => s.To > s.From).OrderBy(s => s.From))
        {
            if (runTo is null || s.From > runTo)
            {
                if (runTo is not null) total += (runTo.Value - runFrom!.Value).TotalSeconds;
                (runFrom, runTo) = (s.From, s.To);
            }
            else if (s.To > runTo)
            {
                runTo = s.To;
            }
        }

        if (runTo is not null) total += (runTo.Value - runFrom!.Value).TotalSeconds;
        return total;
    }
}
```

`src/WebAudioArchive.Analysis/Shows/ShowProcessor.cs`:
```csharp
using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis.Shows;

/// <summary>
/// After segment jobs finish: rebuilds the UTC day timelines they touch and the show-analysis.json of every show
/// whose window (show plus margins) overlaps them and has ended. A show is complete when no planned job overlaps its
/// window any more; only complete shows are enriched.
/// </summary>
public sealed class ShowProcessor(AnalysisPaths paths, ScheduleProvider schedule, IReadOnlyList<string> preference, TimeSpan margin, IClock clock, ILogger log)
{
    public IReadOnlyList<ShowAnalysis> Update(DateTimeOffset fromUtc, DateTimeOffset toUtc, IReadOnlyList<SourceSpan> pending)
    {
        var now = clock.UtcNow;
        for (var d = DateOnly.FromDateTime(fromUtc.UtcDateTime); d <= DateOnly.FromDateTime(toUtc.UtcDateTime); d = d.AddDays(1))
            DayTimeline.RebuildDay(paths, d, preference, now);

        var results = new List<ShowAnalysis>();
        foreach (var show in schedule.Occurrences(fromUtc - margin, toUtc + margin))
        {
            var windowFrom = show.StartUtc - margin;
            var windowTo = show.EndUtc + margin;
            if (windowTo > now) continue;

            var segments = DayTimeline.ReadSegments(paths, windowFrom, windowTo);
            var events = DayTimeline.Merge(segments, preference).Where(e => e.StartUtc >= windowFrom && e.StartUtc < windowTo).ToList();
            var transcripts = segments
                .Select(s => s.Scope.StartsWith("segment:", StringComparison.Ordinal) ? s.Scope["segment:".Length..] : null)
                .OfType<string>()
                .Select(job => AnalysisJson.TryRead<TranscriptDocument>(Path.Combine(paths.SegmentDir(job), AnalysisPaths.TranscriptFile), out _))
                .OfType<TranscriptDocument>()
                .ToList();
            var lines = TranscriptSlicer.Slice(transcripts, windowFrom, windowTo, preference);
            var sources = segments
                .Select(s => new ShowSource(s.Coverage.FirstOrDefault()?.Recorder ?? "", s.Scope["segment:".Length..], s.FromUtc, s.ToUtc))
                .OrderBy(s => s.FromUtc)
                .ToList();
            var complete = !pending.Any(p => p.FromUtc < windowTo && p.ToUtc > windowFrom);

            var doc = ShowAnalysisBuilder.Build(show, margin, events, lines, sources, complete, now);
            AnalysisJson.Write(Path.Combine(paths.ShowDir(show.Id, show.StartUtc), AnalysisPaths.ShowAnalysisFile), doc);
            results.Add(doc);
            log.LogInformation("Show {Show} ({Title}) analysed: {Complete}", show.Id, show.Title, complete ? "complete" : "partial");
        }

        return results;
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~ShowAnalysisTests`
Expected: PASS, 6 tests. Then `dotnet test` for the whole suite: all pass (the Web tests still use the moved types through the global using).

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Core/Analysis/ShowAnalysis.cs src/WebAudioArchive.Analysis/Shows tests/WebAudioArchive.Tests/ShowAnalysisTests.cs
git commit -m "Analysis: merged UTC day timelines and per-show analysis mapped from the saved schedule (#82, #9)"
```

---

### Task 14: The Ollama enricher with evidence quotes and pending retry

**Files:**
- Create: `config/analysis/prompts/enrich-v1.system.md`, `config/analysis/prompts/enrich-v1.user.md`, `config/analysis/prompts/enrich-v1.schema.json`
- Create: `src/WebAudioArchive.Core/Analysis/ShowEnrichment.cs`
- Create: `src/WebAudioArchive.Analysis/Enrichment/ILlmClient.cs`, `OllamaClient.cs`, `EnrichmentPrompt.cs`, `EnrichmentValidator.cs`, `Enricher.cs` (under `src/WebAudioArchive.Analysis/Enrichment/`)
- Create: `tests/WebAudioArchive.Tests/Support/FakeLlm.cs`
- Test: `tests/WebAudioArchive.Tests/EnricherTests.cs`

**Interfaces:**
- Consumes: `ShowAnalysis`, `ShowSource` (Task 13); `TranscriptSlicer`, `TimedLine`, `AnalysisPaths`, `AnalysisJson`, `TranscriptDocument` (Task 3); `EnrichmentOptions`, `AnalysisConfigLoader.Hash`, `AnalysisConfigException` (Task 2); `TranscriptFixtures`, `StubHandler`, `ManualClock`.
- Produces:
  - Core: `static class EnrichmentStatus { Pending, Done, Failed }`; `sealed record DerivedClaim(string Value, string? Detail, double Confidence, string Quote, DateTimeOffset? QuoteUtc)`; `sealed record DerivedSong(string Title, string? Artist, double Confidence, string Quote, DateTimeOffset? QuoteUtc)`; `sealed record EnrichmentDerived(string Model, string? ModelDigest, string PromptVersion, DateTimeOffset CreatedUtc, string TranscriptFingerprint, IReadOnlyList<DerivedClaim> Hosts, IReadOnlyList<DerivedClaim> Guests, IReadOnlyList<DerivedClaim> Topics, IReadOnlyList<DerivedSong> Songs, IReadOnlyList<DerivedClaim> Artists, IReadOnlyList<DerivedClaim> Locations, IReadOnlyList<DerivedClaim> CallsToAction, string? Language, string? Summary, int Dropped, IReadOnlyList<string> DropReasons)`; `sealed record ShowEnrichment(int Schema, string ShowId, string Status, int Attempts, int Unavailable, DateTimeOffset? NextAttemptUtc, string? LastError, DateTimeOffset UpdatedUtc, EnrichmentDerived? Derived)` with `const int CurrentSchema = 1`.
  - `sealed record LlmRequest(string Model, string System, string User, JsonElement Schema, double Temperature, int Seed, int NumCtx, bool DisableThinking)`; `sealed record LlmReply(string Content, int PromptTokens, int OutputTokens, double Seconds)`; `sealed record LlmModel(string Name, string? Digest, bool Thinking)`; `interface ILlmClient { Task<LlmModel?> DescribeAsync(string model, CancellationToken ct); Task<LlmReply> ChatAsync(LlmRequest request, CancellationToken ct); }`; `sealed class LlmUnavailableException(string message, Exception? inner = null)`.
  - `sealed class OllamaClient(HttpClient http, Uri baseUrl) : ILlmClient`
  - `sealed record EnrichmentPrompt(string Name, string System, string UserTemplate, JsonElement Schema, string Version)` with `static EnrichmentPrompt Load(string configDir, EnrichmentOptions o)` and `string User(string station, string title, DateTimeOffset startUtc, DateTimeOffset endUtc, int part, int parts, IReadOnlyList<TimedLine> lines)`.
  - `sealed class EnrichmentInvalidException(string message)`; `sealed record EnrichmentParts(...)` and `static class EnrichmentValidator { EnrichmentParts Validate(string content, IReadOnlyList<TimedLine> lines, int maxItems); string Normalise(string text); string StripWrapping(string content); }`
  - `sealed class Enricher(AnalysisPaths paths, ILlmClient llm, EnrichmentPrompt prompt, EnrichmentOptions options, string model, IReadOnlyList<string> preference, IClock clock, ILogger log) { const int MaxInvalidAttempts = 3; bool IsDue(ShowAnalysis show); string? StatusOf(ShowAnalysis show); Task<ShowEnrichment> EnrichAsync(ShowAnalysis show, CancellationToken ct); }`
  - Test helper `FakeLlm : ILlmClient` with `Respond`, `Requests`, `Down`, `Model`.

- [ ] **Step 1: Write the prompts (committed, versioned by content hash)**

`config/analysis/prompts/enrich-v1.system.md`:
```markdown
You read transcripts of shows on a student radio station and list facts that the transcript itself supports.

Rules:
- Use only what is said in the transcript. Do not use outside knowledge to add names, songs, artists or facts.
- Every entry must have "quote": words copied exactly from the transcript, long enough to show the fact. For hosts, guests, artists, locations and songs the quote must contain the name or title itself.
- If you are not sure, leave the entry out. An empty list is a good answer.
- hosts: people presenting the show. guests: people interviewed or visiting. topics: subjects discussed, a few words each. songs: songs named aloud, with the artist only if the artist is said. artists: performers named aloud. locations: places mentioned. callsToAction: competitions, phone-ins, events or requests made to listeners.
- confidence: a number from 0 to 1, how sure you are that the quote supports the entry.
- language: the main language spoken, as a two letter code.
- summary: two or three plain sentences about the show, using only names that appear in the transcript.
- The transcript comes from speech recognition: it has mistakes, and song lyrics appear in it. Lyrics are not facts about the show.

Answer with JSON only, matching the schema.
```

`config/analysis/prompts/enrich-v1.user.md`:
```markdown
Station: {{station}}
Show title (from the schedule): {{title}}
Scheduled: {{startUtc}} to {{endUtc}} (UTC)
Transcript part {{part}} of {{parts}}. Each line starts with its UTC time.

{{transcript}}
```

`config/analysis/prompts/enrich-v1.schema.json`:
```json
{
  "type": "object",
  "properties": {
    "hosts": { "$ref": "#/$defs/claims" },
    "guests": { "$ref": "#/$defs/claims" },
    "topics": { "$ref": "#/$defs/claims" },
    "artists": { "$ref": "#/$defs/claims" },
    "locations": { "$ref": "#/$defs/claims" },
    "callsToAction": { "$ref": "#/$defs/claims" },
    "songs": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "title": { "type": "string" },
          "artist": { "type": "string" },
          "quote": { "type": "string" },
          "confidence": { "type": "number" }
        },
        "required": ["title", "quote", "confidence"]
      }
    },
    "language": { "type": "string" },
    "summary": { "type": "string" }
  },
  "required": ["hosts", "guests", "topics", "artists", "locations", "callsToAction", "songs", "language", "summary"],
  "$defs": {
    "claims": {
      "type": "array",
      "items": {
        "type": "object",
        "properties": {
          "value": { "type": "string" },
          "detail": { "type": "string" },
          "quote": { "type": "string" },
          "confidence": { "type": "number" }
        },
        "required": ["value", "quote", "confidence"]
      }
    }
  }
}
```

- [ ] **Step 2: Write the fake LLM and the failing tests**

`tests/WebAudioArchive.Tests/Support/FakeLlm.cs`:
```csharp
using WebAudioArchive.Analysis.Enrichment;

namespace WebAudioArchive.Tests.Support;

/// <summary>A deterministic LLM for tests: Respond gets each request and its call number and returns the reply text.</summary>
public sealed class FakeLlm : ILlmClient
{
    public Func<LlmRequest, int, string> Respond { get; set; } = (_, _) => "{}";

    public List<LlmRequest> Requests { get; } = [];

    public bool Down { get; set; }

    public LlmModel? Model { get; set; } = new("qwen3.6:latest", "07d35212591fc27746f0a317c975a6d68754fb38e9053d82e25f06057af28522", true);

    public Task<LlmModel?> DescribeAsync(string model, CancellationToken ct) =>
        Down ? throw new LlmUnavailableException("Connection refused (host.docker.internal:11434)") : Task.FromResult(Model);

    public Task<LlmReply> ChatAsync(LlmRequest request, CancellationToken ct)
    {
        if (Down) throw new LlmUnavailableException("Connection refused (host.docker.internal:11434)");
        var n = Requests.Count;
        Requests.Add(request);
        return Task.FromResult(new LlmReply(Respond(request, n), 1000, 200, 1.5));
    }
}
```

`tests/WebAudioArchive.Tests/EnricherTests.cs`:
```csharp
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class EnricherTests : IDisposable
{
    static readonly DateTimeOffset Show = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    const string Job = "a/2026/10-15/190000";
    readonly string _root = Directory.CreateTempSubdirectory("waa-enrich-").FullName;
    readonly ManualClock _clock = new(Show.AddHours(3));
    readonly FakeLlm _llm = new();
    static readonly string ConfigDir = Path.Combine(AppContext.BaseDirectory, "config", "analysis");
    static readonly EnrichmentOptions Options = new();

    static readonly string[] Said =
    [
        "Good evening and welcome to Backseat Aux on Burst Radio, I'm Amy and with me as always is Raff.",
        "Hi everyone. Tonight we have Priya Shah from the Students Union in the studio.",
        "That was Mr Brightside by The Killers, an absolute classic.",
        "Tickets for the winter ball are fifteen pounds.",
        "Up next is Espresso by Sabrina Carpenter.",
    ];

    const string Answer = """
        {
          "hosts": [
            { "value": "Amy", "quote": "I'm Amy and with me as always is Raff", "confidence": 1 },
            { "value": "Raff", "quote": "Hi everyone", "confidence": 0.9 }
          ],
          "guests": [
            { "value": "Priya Shah", "detail": "Students Union", "quote": "Priya Shah from the Students Union", "confidence": 7 },
            { "value": "Tom Hanks", "quote": "Tom Hanks is in the studio", "confidence": 0.8 }
          ],
          "topics": [ { "value": "winter ball tickets", "quote": "Tickets for the winter ball are fifteen pounds", "confidence": 0.9 } ],
          "artists": [ { "value": "The Killers", "quote": "Mr Brightside by The Killers", "confidence": 0.9 } ],
          "locations": [],
          "callsToAction": [],
          "songs": [
            { "title": "Mr Brightside", "artist": "The Killers", "quote": "That was Mr Brightside by The Killers", "confidence": 0.95 },
            { "title": "Espresso", "artist": "Taylor Swift", "quote": "Up next is Espresso by Sabrina Carpenter", "confidence": 0.9 }
          ],
          "language": "en",
          "summary": "Amy and Raff present Backseat Aux with a guest from the Students Union."
        }
        """;

    public EnricherTests() => _llm.Respond = (_, _) => Answer;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    AnalysisPaths Paths => new(Path.Combine(_root, "analysis"), "burst");

    static IReadOnlyList<TimedLine> Lines() => TranscriptSlicer.Slice([TranscriptFixtures.Of(Show, Said)], Show, Show.AddHours(1), ["a"]);

    ShowAnalysis Analysed(params string[] lines)
    {
        AnalysisJson.Write(Path.Combine(Paths.SegmentDir(Job), AnalysisPaths.TranscriptFile), TranscriptFixtures.Of(Show, lines) with { JobId = Job });
        return new ShowAnalysis(1, "burst-20261015T1900Z", "burst", "Backseat Aux", Show, Show.AddHours(1), Show.AddMinutes(-5), Show.AddMinutes(65),
            _clock.UtcNow, true, [new ShowSource("a", Job, Show, Show.AddHours(1))],
            new ShowCounts(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, 3600), []);
    }

    Enricher Enricher(EnrichmentOptions? options = null) => new(Paths, _llm, EnrichmentPrompt.Load(ConfigDir, options ?? Options), options ?? Options,
        "qwen3.6:latest", ["a", "b"], _clock, NullLogger.Instance);

    [Fact]
    public void Invented_claims_are_dropped()
    {
        var parts = EnrichmentValidator.Validate(Answer, Lines(), 40);

        Assert.Equal(new[] { "Amy" }, parts.Hosts.Select(h => h.Value));
        Assert.Equal(new[] { "Priya Shah" }, parts.Guests.Select(g => g.Value));
        Assert.Equal(1.0, parts.Guests[0].Confidence);
        Assert.Equal(new[] { "Mr Brightside", "Espresso" }, parts.Songs.Select(s => s.Title));
        Assert.Null(parts.Songs[1].Artist);
        Assert.Equal("The Killers", parts.Songs[0].Artist);
        Assert.Equal(Show, parts.Hosts[0].QuoteUtc);
        Assert.Equal(Show.AddSeconds(20), parts.Songs[0].QuoteUtc);
        Assert.Equal(3, parts.Dropped);
        Assert.Contains(parts.DropReasons, r => r.Contains("Raff") && r.Contains("does not contain"));
        Assert.Contains(parts.DropReasons, r => r.Contains("Tom Hanks") && r.Contains("not in the transcript"));
        Assert.Contains(parts.DropReasons, r => r.Contains("Taylor Swift"));
    }

    [Theory]
    [InlineData("```json\n{ \"hosts\": [], \"summary\": \"x\" }\n```")]
    [InlineData("<think>let me see</think>\n{ \"hosts\": [], \"summary\": \"x\" }")]
    [InlineData("  { \"hosts\": [], \"summary\": \"x\" }  ")]
    public void Fenced_json_is_accepted(string content)
    {
        Assert.Equal("x", EnrichmentValidator.Validate(content, Lines(), 40).Summary);
    }

    [Theory]
    [InlineData("Sorry, I cannot help with that.")]
    [InlineData("[1, 2, 3]")]
    [InlineData("{ \"hosts\": \"Amy\" }")]
    public void Answers_that_are_not_the_schema_are_invalid(string content)
    {
        Assert.Throws<EnrichmentInvalidException>(() => EnrichmentValidator.Validate(content, Lines(), 40));
    }

    [Fact]
    public async Task A_finished_show_is_enriched_with_model_digest_and_prompt_version()
    {
        var show = Analysed(Said);

        var result = await Enricher().EnrichAsync(show, CancellationToken.None);

        Assert.Equal(EnrichmentStatus.Done, result.Status);
        var d = result.Derived!;
        Assert.Equal("qwen3.6:latest", d.Model);
        Assert.StartsWith("07d35212", d.ModelDigest);
        Assert.StartsWith("enrich-v1+", d.PromptVersion);
        Assert.Equal("Amy", Assert.Single(d.Hosts).Value);
        Assert.True(File.Exists(Path.Combine(Paths.ShowDir(show.ShowId, show.StartUtc), AnalysisPaths.EnrichmentFile)));

        var request = Assert.Single(_llm.Requests);
        Assert.True(request.DisableThinking);
        Assert.Equal(0.1, request.Temperature);
        Assert.Equal(32768, request.NumCtx);
        Assert.Contains("Backseat Aux", request.User);
        Assert.Contains("[19:00:20] That was Mr Brightside by The Killers", request.User);
        Assert.Contains("Answer with JSON only", request.System);
        Assert.Equal(System.Text.Json.JsonValueKind.Object, request.Schema.ValueKind);
    }

    [Fact]
    public async Task Ollama_down_leaves_the_show_pending_and_retries_later()
    {
        var show = Analysed(Said);
        _llm.Down = true;

        var pending = await Enricher().EnrichAsync(show, CancellationToken.None);

        Assert.Equal(EnrichmentStatus.Pending, pending.Status);
        Assert.Equal(1, pending.Unavailable);
        Assert.Equal(0, pending.Attempts);
        Assert.Equal(_clock.UtcNow.AddMinutes(5), pending.NextAttemptUtc);
        Assert.False(Enricher().IsDue(show));

        _llm.Down = false;
        _clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(Enricher().IsDue(show));
        Assert.Equal(EnrichmentStatus.Done, (await Enricher().EnrichAsync(show, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Invalid_answers_fail_after_three_attempts_and_keep_the_raw_reply()
    {
        var show = Analysed(Said);
        _llm.Respond = (_, _) => "not json at all";

        for (var i = 0; i < Enricher.MaxInvalidAttempts; i++)
        {
            await Enricher().EnrichAsync(show, CancellationToken.None);
            _clock.Advance(TimeSpan.FromHours(7));
        }

        var stored = AnalysisJson.Read<ShowEnrichment>(Path.Combine(Paths.ShowDir(show.ShowId, show.StartUtc), AnalysisPaths.EnrichmentFile))!;
        Assert.Equal(EnrichmentStatus.Failed, stored.Status);
        Assert.Equal(3, stored.Attempts);
        Assert.True(File.Exists(Path.Combine(Paths.ShowDir(show.ShowId, show.StartUtc), "enrichment.raw.txt")));
        Assert.False(Enricher().IsDue(show));
    }

    [Fact]
    public async Task It_runs_again_only_when_the_transcript_or_prompt_changes()
    {
        var show = Analysed(Said);
        await Enricher().EnrichAsync(show, CancellationToken.None);
        Assert.False(Enricher().IsDue(show));
        await Enricher().EnrichAsync(show, CancellationToken.None);
        Assert.Single(_llm.Requests);

        Analysed([.. Said, "One more line about the ball."]);
        Assert.True(Enricher().IsDue(show));
        await Enricher().EnrichAsync(show, CancellationToken.None);
        Assert.Equal(2, _llm.Requests.Count);
    }

    [Fact]
    public async Task A_long_transcript_is_sent_in_parts_and_the_claims_are_merged()
    {
        var show = Analysed(Said);
        _llm.Respond = (r, n) => n == 0 ? Answer : """{ "hosts": [ { "value": "AMY", "quote": "I'm Amy", "confidence": 0.5 } ], "summary": "" }""";

        var result = await Enricher(Options with { MaxTranscriptChars = 150 }).EnrichAsync(show, CancellationToken.None);

        Assert.True(_llm.Requests.Count >= 2);
        Assert.Contains("part 1 of", _llm.Requests[0].User);
        Assert.Equal("Amy", Assert.Single(result.Derived!.Hosts).Value);
    }

    [Fact]
    public void Prompt_files_load_with_a_content_version_and_missing_files_are_named()
    {
        var a = EnrichmentPrompt.Load(ConfigDir, Options);
        Assert.Equal(a.Version, EnrichmentPrompt.Load(ConfigDir, Options).Version);
        Assert.Matches("^enrich-v1\\+[0-9a-f]{12}$", a.Version);
        Assert.Contains("enrich-v9.system.md", Assert.Throws<AnalysisConfigException>(() => EnrichmentPrompt.Load(ConfigDir, Options with { PromptName = "enrich-v9" })).Message);
    }

    [Fact]
    public async Task The_ollama_client_asks_for_structured_output_without_thinking()
    {
        string? chatBody = null;
        var handler = new StubHandler(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/tags" => Json("""{ "models": [ { "name": "qwen3.6:latest", "digest": "07d35212" } ] }"""),
            "/api/show" => Json("""{ "capabilities": ["completion", "thinking"] }"""),
            "/api/chat" => Capture(req, b => chatBody = b, """{ "message": { "role": "assistant", "content": "{}" }, "prompt_eval_count": 10, "eval_count": 5, "total_duration": 2000000000 }"""),
            _ => StubHandler.NotFound(),
        });
        var client = new OllamaClient(new HttpClient(handler), new Uri("http://host.docker.internal:11434"));

        var model = await client.DescribeAsync("qwen3.6:latest", CancellationToken.None);
        var schema = EnrichmentPrompt.Load(ConfigDir, Options).Schema;
        var reply = await client.ChatAsync(new LlmRequest("qwen3.6:latest", "sys", "user", schema, 0.1, 42, 32768, model!.Thinking), CancellationToken.None);

        Assert.Equal("07d35212", model.Digest);
        Assert.True(model.Thinking);
        Assert.Equal("{}", reply.Content);
        Assert.Equal(2.0, reply.Seconds, 6);
        Assert.Contains("\"think\":false", chatBody);
        Assert.Contains("\"format\":{", chatBody);
        Assert.Contains("\"num_ctx\":32768", chatBody);
        Assert.Contains("\"stream\":false", chatBody);
        Assert.Null(await client.DescribeAsync("missing:model", CancellationToken.None));
    }

    [Fact]
    public async Task The_ollama_client_reports_unreachable_and_missing_models_as_unavailable()
    {
        var down = new OllamaClient(new HttpClient(new StubHandler(_ => throw new HttpRequestException("refused"))), new Uri("http://host.docker.internal:11434"));
        await Assert.ThrowsAsync<LlmUnavailableException>(() => down.DescribeAsync("qwen3.6:latest", CancellationToken.None));

        var noModel = new OllamaClient(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"error\":\"model 'x' not found\"}") })), new Uri("http://host.docker.internal:11434"));
        var schema = EnrichmentPrompt.Load(ConfigDir, Options).Schema;
        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() => noModel.ChatAsync(new LlmRequest("x", "s", "u", schema, 0, 1, 2048, false), CancellationToken.None));
        Assert.Contains("ollama pull x", ex.Message);
    }

    static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    static HttpResponseMessage Capture(HttpRequestMessage req, Action<string> keep, string body)
    {
        keep(req.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return Json(body);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~EnricherTests`
Expected: build FAILS with `CS0246` for `ILlmClient`, `EnrichmentValidator`, `Enricher`, `EnrichmentPrompt`, `OllamaClient`, `ShowEnrichment`.

- [ ] **Step 4: Implement the enrichment types, client, validator and enricher**

`src/WebAudioArchive.Core/Analysis/ShowEnrichment.cs`:
```csharp
namespace WebAudioArchive.Core.Analysis;

public static class EnrichmentStatus
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>A derived claim with its verbatim evidence quote and where the quote was said (UTC).</summary>
public sealed record DerivedClaim(string Value, string? Detail, double Confidence, string Quote, DateTimeOffset? QuoteUtc);

public sealed record DerivedSong(string Title, string? Artist, double Confidence, string Quote, DateTimeOffset? QuoteUtc);

/// <summary>
/// What the local LLM said about a show (spec 3.7), every claim backed by a quote found in the transcript. Schedule
/// facts stay authoritative; this is "derived" and can be regenerated. Derived hosts never grant access to anything:
/// they are suggestions for a manager.
/// </summary>
public sealed record EnrichmentDerived(
    string Model,
    string? ModelDigest,
    string PromptVersion,
    DateTimeOffset CreatedUtc,
    string TranscriptFingerprint,
    IReadOnlyList<DerivedClaim> Hosts,
    IReadOnlyList<DerivedClaim> Guests,
    IReadOnlyList<DerivedClaim> Topics,
    IReadOnlyList<DerivedSong> Songs,
    IReadOnlyList<DerivedClaim> Artists,
    IReadOnlyList<DerivedClaim> Locations,
    IReadOnlyList<DerivedClaim> CallsToAction,
    string? Language,
    string? Summary,
    int Dropped,
    IReadOnlyList<string> DropReasons);

/// <summary>enrichment.json beside show-analysis.json. Pending while Ollama is unreachable (Unavailable counts those tries) or after an invalid answer.</summary>
public sealed record ShowEnrichment(
    int Schema,
    string ShowId,
    string Status,
    int Attempts,
    int Unavailable,
    DateTimeOffset? NextAttemptUtc,
    string? LastError,
    DateTimeOffset UpdatedUtc,
    EnrichmentDerived? Derived)
{
    public const int CurrentSchema = 1;
}
```

`src/WebAudioArchive.Analysis/Enrichment/ILlmClient.cs`:
```csharp
using System.Text.Json;

namespace WebAudioArchive.Analysis.Enrichment;

public sealed record LlmRequest(string Model, string System, string User, JsonElement Schema, double Temperature, int Seed, int NumCtx, bool DisableThinking);

public sealed record LlmReply(string Content, int PromptTokens, int OutputTokens, double Seconds);

/// <summary>A local model: its name, the digest of the weights in use (recorded with every result) and whether it can think.</summary>
public sealed record LlmModel(string Name, string? Digest, bool Thinking);

/// <summary>The local LLM, behind an interface so tests use a fake and the runtime can change. Unreachable means try later.</summary>
public interface ILlmClient
{
    /// <summary>The model if it is installed, null if it is not.</summary>
    Task<LlmModel?> DescribeAsync(string model, CancellationToken ct);

    Task<LlmReply> ChatAsync(LlmRequest request, CancellationToken ct);
}

public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
```

`src/WebAudioArchive.Analysis/Enrichment/OllamaClient.cs`:
```csharp
using System.Net.Http.Json;
using System.Text.Json;

namespace WebAudioArchive.Analysis.Enrichment;

/// <summary>
/// Ollama's HTTP API (0.40): /api/tags for the digest, /api/show for capabilities, /api/chat with a JSON schema in
/// "format", stream off, a fixed seed and low temperature, "think": false for thinking models (the reasoning text is
/// not wanted and costs minutes), and keep_alive 10 minutes so a run of shows does not reload the model each time.
/// </summary>
public sealed class OllamaClient(HttpClient http, Uri baseUrl) : ILlmClient
{
    public async Task<LlmModel?> DescribeAsync(string model, CancellationToken ct)
    {
        try
        {
            using var tags = await http.GetAsync(new Uri(baseUrl, "api/tags"), ct);
            if (!tags.IsSuccessStatusCode) throw new LlmUnavailableException($"Ollama at {baseUrl} answered {(int)tags.StatusCode} to /api/tags");
            using var tagsDoc = JsonDocument.Parse(await tags.Content.ReadAsStringAsync(ct));
            string? digest = null;
            var found = false;
            if (tagsDoc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            {
                foreach (var m in models.EnumerateArray())
                {
                    if (m.TryGetProperty("name", out var name) && name.GetString() == model)
                    {
                        found = true;
                        digest = m.TryGetProperty("digest", out var d) ? d.GetString() : null;
                    }
                }
            }

            if (!found) return null;
            var thinking = false;
            using var show = await http.PostAsJsonAsync(new Uri(baseUrl, "api/show"), new { model }, ct);
            if (show.IsSuccessStatusCode)
            {
                using var showDoc = JsonDocument.Parse(await show.Content.ReadAsStringAsync(ct));
                if (showDoc.RootElement.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array)
                    thinking = caps.EnumerateArray().Any(c => c.GetString() == "thinking");
            }

            return new LlmModel(model, digest, thinking);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} did not answer in time", ex);
        }
        catch (JsonException ex)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} answered with something that is not JSON: {ex.Message}", ex);
        }
    }

    public async Task<LlmReply> ChatAsync(LlmRequest r, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["model"] = r.Model,
            ["stream"] = false,
            ["format"] = r.Schema,
            ["keep_alive"] = "10m",
            ["options"] = new Dictionary<string, object> { ["temperature"] = r.Temperature, ["seed"] = r.Seed, ["num_ctx"] = r.NumCtx },
            ["messages"] = new object[] { new { role = "system", content = r.System }, new { role = "user", content = r.User } },
        };
        if (r.DisableThinking) body["think"] = false;

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsJsonAsync(new Uri(baseUrl, "api/chat"), body, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} is not reachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LlmUnavailableException($"Ollama at {baseUrl} did not answer in time", ex);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new LlmUnavailableException($"Ollama has no model '{r.Model}' ({text.Trim()}); on the host run: ollama pull {r.Model}");
            if ((int)response.StatusCode >= 500)
                throw new LlmUnavailableException($"Ollama answered {(int)response.StatusCode}: {text.Trim()}");
            if (!response.IsSuccessStatusCode)
                throw new EnrichmentInvalidException($"Ollama refused the request ({(int)response.StatusCode}): {text.Trim()}");

            try
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var content = root.GetProperty("message").GetProperty("content").GetString() ?? "";
                int Count(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
                var seconds = root.TryGetProperty("total_duration", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetDouble() / 1e9 : 0;
                return new LlmReply(content, Count("prompt_eval_count"), Count("eval_count"), seconds);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new EnrichmentInvalidException($"Ollama's reply was not the expected chat response: {ex.Message}");
            }
        }
    }
}
```

`src/WebAudioArchive.Analysis/Enrichment/EnrichmentPrompt.cs`:
```csharp
using System.Globalization;
using System.Text;
using System.Text.Json;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Enrichment;

/// <summary>
/// The committed prompt (config/analysis/prompts/&lt;name&gt;.system.md, .user.md, .schema.json). Its version is the name
/// plus a hash of the three files, recorded with every result, so editing a prompt re-enriches shows.
/// </summary>
public sealed record EnrichmentPrompt(string Name, string System, string UserTemplate, JsonElement Schema, string Version)
{
    public static EnrichmentPrompt Load(string configDir, EnrichmentOptions o)
    {
        var stem = Path.Combine(configDir, o.PromptDir, o.PromptName);
        string Read(string extension)
        {
            var path = stem + extension;
            if (!File.Exists(path)) throw new AnalysisConfigException([$"enrichment prompt file {path} is missing"]);
            return File.ReadAllText(path);
        }

        var system = Read(".system.md");
        var user = Read(".user.md");
        var schemaText = Read(".schema.json");
        if (!user.Contains("{{transcript}}", StringComparison.Ordinal))
            throw new AnalysisConfigException([$"{stem}.user.md must contain {{{{transcript}}}}"]);
        JsonElement schema;
        try
        {
            using var doc = JsonDocument.Parse(schemaText);
            schema = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new AnalysisConfigException([$"{stem}.schema.json is not JSON: {ex.Message}"]);
        }

        return new EnrichmentPrompt(o.PromptName, system.Trim(), user, schema, o.PromptName + "+" + AnalysisConfigLoader.Hash(system + "\n" + user + "\n" + schemaText));
    }

    public string User(string station, string title, DateTimeOffset startUtc, DateTimeOffset endUtc, int part, int parts, IReadOnlyList<TimedLine> lines)
    {
        var transcript = new StringBuilder();
        foreach (var l in lines)
            transcript.Append('[').Append(l.StartUtc.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture)).Append("] ").AppendLine(l.Text);
        return UserTemplate
            .Replace("{{station}}", station, StringComparison.Ordinal)
            .Replace("{{title}}", title, StringComparison.Ordinal)
            .Replace("{{startUtc}}", startUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{endUtc}}", endUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{part}}", part.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{parts}}", parts.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{{transcript}}", transcript.ToString().TrimEnd(), StringComparison.Ordinal);
    }
}
```

`src/WebAudioArchive.Analysis/Enrichment/EnrichmentValidator.cs`:
```csharp
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Enrichment;

public sealed class EnrichmentInvalidException(string message) : Exception(message);

public sealed record EnrichmentParts(
    List<DerivedClaim> Hosts,
    List<DerivedClaim> Guests,
    List<DerivedClaim> Topics,
    List<DerivedSong> Songs,
    List<DerivedClaim> Artists,
    List<DerivedClaim> Locations,
    List<DerivedClaim> CallsToAction,
    string? Language,
    string? Summary,
    int Dropped,
    List<string> DropReasons);

/// <summary>
/// Spec 3.7 and 6: every claim needs a quote that is in the transcript (compared in lower case with punctuation and
/// spacing ignored), and for names, titles and places the quote must contain the name itself. Anything else is dropped
/// and counted with the reason. A reply that is not an object of the expected shape is invalid (retried).
/// </summary>
public static partial class EnrichmentValidator
{
    public const int MaxSummaryChars = 600;

    public static EnrichmentParts Validate(string content, IReadOnlyList<TimedLine> lines, int maxItems)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(StripWrapping(content));
        }
        catch (JsonException ex)
        {
            throw new EnrichmentInvalidException($"the answer is not JSON: {ex.Message}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new EnrichmentInvalidException("the answer is not a JSON object");
            var normalisedLines = lines.Select(l => (Line: l, Text: Normalise(l.Text))).ToList();
            var whole = " " + string.Join(' ', normalisedLines.Select(l => l.Text)) + " ";
            var reasons = new List<string>();

            // Where the quote was said: the line holding all of it, or (for a quote running over two lines) the line holding its first three words.
            DateTimeOffset? Where(string quote)
            {
                var head = string.Join(' ', quote.Split(' ').Take(3));
                return normalisedLines.FirstOrDefault(l => l.Text.Contains(quote, StringComparison.Ordinal)).Line?.StartUtc
                    ?? normalisedLines.FirstOrDefault(l => l.Text.Contains(head, StringComparison.Ordinal)).Line?.StartUtc;
            }

            List<DerivedClaim> Claims(string name, bool nameInQuote)
            {
                var result = new Dictionary<string, DerivedClaim>(StringComparer.Ordinal);
                foreach (var item in Items(root, name))
                {
                    var value = Text(item, "value").Trim();
                    var quote = Text(item, "quote").Trim();
                    var q = Normalise(quote);
                    if (value.Length == 0) { reasons.Add($"{name}: an entry with no value"); continue; }
                    if (q.Length < 3 || !whole.Contains(" " + q + " ", StringComparison.Ordinal)) { reasons.Add($"{name} '{value}': the quote is not in the transcript"); continue; }
                    if (nameInQuote && !(" " + q + " ").Contains(" " + Normalise(value) + " ", StringComparison.Ordinal)) { reasons.Add($"{name} '{value}': the quote does not contain the name"); continue; }
                    var claim = new DerivedClaim(value, NullIfEmpty(Text(item, "detail")), Confidence(item), quote, Where(q));
                    var key = Normalise(value);
                    if (!result.TryGetValue(key, out var existing) || existing.Confidence < claim.Confidence) result[key] = claim;
                }

                return result.Values.Take(maxItems).ToList();
            }

            var songs = new Dictionary<string, DerivedSong>(StringComparer.Ordinal);
            foreach (var item in Items(root, "songs"))
            {
                var title = Text(item, "title").Trim();
                var artist = NullIfEmpty(Text(item, "artist"));
                var quote = Text(item, "quote").Trim();
                var q = " " + Normalise(quote) + " ";
                if (title.Length == 0) { reasons.Add("songs: an entry with no title"); continue; }
                if (q.Trim().Length < 3 || !whole.Contains(q, StringComparison.Ordinal)) { reasons.Add($"songs '{title}': the quote is not in the transcript"); continue; }
                if (!q.Contains(" " + Normalise(title) + " ", StringComparison.Ordinal)) { reasons.Add($"songs '{title}': the quote does not contain the title"); continue; }
                if (artist is not null && !q.Contains(" " + Normalise(artist) + " ", StringComparison.Ordinal))
                {
                    reasons.Add($"songs '{title}': artist '{artist}' is not in the quote, so it was left out");
                    artist = null;
                }

                var song = new DerivedSong(title, artist, Confidence(item), quote, Where(q.Trim()));
                var key = Normalise(title);
                if (!songs.TryGetValue(key, out var existing) || existing.Confidence < song.Confidence) songs[key] = song;
            }

            var hosts = Claims("hosts", true);
            var guests = Claims("guests", true);
            var topics = Claims("topics", false);
            var artists = Claims("artists", true);
            var locations = Claims("locations", true);
            var calls = Claims("callsToAction", false);
            var summary = NullIfEmpty(Text(root, "summary"));
            if (summary is not null && summary.Length > MaxSummaryChars) summary = summary[..MaxSummaryChars];
            var language = NullIfEmpty(Text(root, "language"));
            if (language is not null && language.Length > 8) language = null;

            return new EnrichmentParts(hosts, guests, topics, songs.Values.Take(maxItems).ToList(), artists, locations, calls, language, summary,
                reasons.Count, reasons);
        }
    }

    /// <summary>Lower case, letters and digits only, single spaces: "I'm Amy," is "i m amy".</summary>
    public static string Normalise(string text) => NotWord().Replace(text.ToLower(CultureInfo.InvariantCulture), " ").Trim();

    /// <summary>The JSON inside a code fence, after a thinking block, or with spaces around it.</summary>
    public static string StripWrapping(string content)
    {
        var t = content.Trim();
        var thinkEnd = t.IndexOf("</think>", StringComparison.Ordinal);
        if (t.StartsWith("<think>", StringComparison.Ordinal) && thinkEnd > 0) t = t[(thinkEnd + "</think>".Length)..].Trim();
        if (t.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLine = t.IndexOf('\n');
            var close = t.LastIndexOf("```", StringComparison.Ordinal);
            if (firstLine > 0 && close > firstLine) t = t[(firstLine + 1)..close].Trim();
        }

        return t;
    }

    static IEnumerable<JsonElement> Items(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var list)) return [];
        if (list.ValueKind != JsonValueKind.Array) throw new EnrichmentInvalidException($"'{name}' is not a list");
        return list.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object).ToList();
    }

    static string Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    static double Confidence(JsonElement e) =>
        e.TryGetProperty("confidence", out var v) && v.ValueKind == JsonValueKind.Number && double.IsFinite(v.GetDouble()) ? Math.Clamp(v.GetDouble(), 0, 1) : 0.5;

    static string? NullIfEmpty(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NotWord();
}
```

`src/WebAudioArchive.Analysis/Enrichment/Enricher.cs`:
```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis.Enrichment;

/// <summary>
/// Enriches a complete show (spec 3.7): the transcript of its window (show plus margins) goes to the local LLM in
/// parts that fit the context, each answer is validated against its part, and the claims are merged. Ollama
/// unreachable or the model missing: "pending", retried with backoff (not counted as an attempt). An invalid answer:
/// retried up to 3 times, then "failed" with the raw reply kept. Runs again only when the transcript, the prompt or
/// the model changes.
/// </summary>
public sealed class Enricher(AnalysisPaths paths, ILlmClient llm, EnrichmentPrompt prompt, EnrichmentOptions options, string model,
    IReadOnlyList<string> preference, IClock clock, ILogger log)
{
    public const int MaxInvalidAttempts = 3;

    public bool IsDue(ShowAnalysis show)
    {
        // Only finished shows with analysed speech: a scheduled show with no recording has nothing to enrich.
        if (!show.Complete || show.Sources.Count == 0) return false;
        var lines = Lines(show);
        if (lines.Count == 0) return false;
        var existing = Read(show);
        if (existing is null) return true;
        var fingerprint = Fingerprint(lines);
        var current = existing.Derived is { } d && d.TranscriptFingerprint == fingerprint && d.PromptVersion == prompt.Version && d.Model == model;
        if (existing.Status == EnrichmentStatus.Done) return !current;
        if (existing.Status == EnrichmentStatus.Failed && existing.Attempts >= MaxInvalidAttempts && existing.LastError?.EndsWith($"(transcript {fingerprint})", StringComparison.Ordinal) == true) return false;
        return existing.NextAttemptUtc is null || existing.NextAttemptUtc <= clock.UtcNow;
    }

    public async Task<ShowEnrichment> EnrichAsync(ShowAnalysis show, CancellationToken ct)
    {
        var existing = Read(show);
        if (!IsDue(show) && existing is not null) return existing;

        var now = clock.UtcNow;
        var lines = Lines(show);
        var fingerprint = Fingerprint(lines);
        ShowEnrichment result;
        string? raw = null;
        try
        {
            var info = await llm.DescribeAsync(model, ct)
                ?? throw new LlmUnavailableException($"Ollama has no model '{model}'; on the host run: ollama pull {model}");
            var parts = Split(lines, options.MaxTranscriptChars);
            var merged = new List<EnrichmentParts>();
            for (var i = 0; i < parts.Count; i++)
            {
                var user = prompt.User(show.Station, show.Title, show.StartUtc, show.EndUtc, i + 1, parts.Count, parts[i]);
                var reply = await llm.ChatAsync(new LlmRequest(model, prompt.System, user, prompt.Schema, options.Temperature, options.Seed, options.NumCtx, info.Thinking), ct);
                raw = reply.Content;
                merged.Add(EnrichmentValidator.Validate(reply.Content, parts[i], options.MaxItems));
                log.LogInformation("Enriched {Show} part {Part}/{Parts} in {Seconds:0.0} s ({Prompt} prompt and {Output} output tokens)",
                    show.ShowId, i + 1, parts.Count, reply.Seconds, reply.PromptTokens, reply.OutputTokens);
            }

            result = new ShowEnrichment(ShowEnrichment.CurrentSchema, show.ShowId, EnrichmentStatus.Done, (existing?.Attempts ?? 0) + 1, existing?.Unavailable ?? 0,
                null, null, now, Merge(merged, info, fingerprint, now));
        }
        catch (LlmUnavailableException ex)
        {
            var unavailable = (existing?.Unavailable ?? 0) + 1;
            var wait = TimeSpan.FromMinutes(Math.Min(120, 5 * Math.Pow(2, unavailable - 1)));
            result = new ShowEnrichment(ShowEnrichment.CurrentSchema, show.ShowId, EnrichmentStatus.Pending, existing?.Attempts ?? 0, unavailable,
                now + wait, ex.Message, now, existing?.Derived);
            log.LogInformation("Enrichment of {Show} pending: {Error}", show.ShowId, ex.Message);
        }
        catch (EnrichmentInvalidException ex)
        {
            var attempts = (existing?.Attempts ?? 0) + 1;
            var failed = attempts >= MaxInvalidAttempts;
            result = new ShowEnrichment(ShowEnrichment.CurrentSchema, show.ShowId, failed ? EnrichmentStatus.Failed : EnrichmentStatus.Pending, attempts,
                existing?.Unavailable ?? 0, failed ? null : now + TimeSpan.FromMinutes(5 * Math.Pow(2, attempts - 1)),
                $"{ex.Message} (transcript {fingerprint})", now, existing?.Derived);
            if (raw is not null)
            {
                var dir = paths.ShowDir(show.ShowId, show.StartUtc);
                Directory.CreateDirectory(dir);
                await File.WriteAllTextAsync(Path.Combine(dir, "enrichment.raw.txt"), raw, ct);
            }
            log.LogWarning("Enrichment of {Show} gave an invalid answer (attempt {Attempt}): {Error}", show.ShowId, attempts, ex.Message);
        }

        AnalysisJson.Write(Path.Combine(paths.ShowDir(show.ShowId, show.StartUtc), AnalysisPaths.EnrichmentFile), result);
        return result;
    }

    /// <summary>The stored enrichment status of a show, or null when it has none yet.</summary>
    public string? StatusOf(ShowAnalysis show) => Read(show)?.Status;

    ShowEnrichment? Read(ShowAnalysis show) =>
        AnalysisJson.TryRead<ShowEnrichment>(Path.Combine(paths.ShowDir(show.ShowId, show.StartUtc), AnalysisPaths.EnrichmentFile), out _);

    IReadOnlyList<TimedLine> Lines(ShowAnalysis show)
    {
        var docs = show.Sources
            .Select(s => AnalysisJson.TryRead<TranscriptDocument>(Path.Combine(paths.SegmentDir(s.JobId), AnalysisPaths.TranscriptFile), out _))
            .OfType<TranscriptDocument>()
            .ToList();
        return TranscriptSlicer.Slice(docs, show.WindowFromUtc, show.WindowToUtc, preference);
    }

    static string Fingerprint(IReadOnlyList<TimedLine> lines)
    {
        var sb = new StringBuilder();
        foreach (var l in lines) sb.Append(l.StartUtc.UtcTicks).Append('|').Append(l.Text).Append('\n');
        return AnalysisConfigLoader.Hash(sb.ToString());
    }

    static List<IReadOnlyList<TimedLine>> Split(IReadOnlyList<TimedLine> lines, int maxChars)
    {
        var parts = new List<IReadOnlyList<TimedLine>>();
        var current = new List<TimedLine>();
        var size = 0;
        foreach (var l in lines)
        {
            if (current.Count > 0 && size + l.Text.Length + 12 > maxChars)
            {
                parts.Add(current);
                current = [];
                size = 0;
            }

            current.Add(l);
            size += l.Text.Length + 12;
        }

        if (current.Count > 0 || parts.Count == 0) parts.Add(current);
        return parts;
    }

    EnrichmentDerived Merge(IReadOnlyList<EnrichmentParts> parts, LlmModel info, string fingerprint, DateTimeOffset now)
    {
        List<DerivedClaim> Union(Func<EnrichmentParts, List<DerivedClaim>> pick) => parts
            .SelectMany(pick)
            .GroupBy(c => EnrichmentValidator.Normalise(c.Value))
            .Select(g => g.OrderByDescending(c => c.Confidence).First())
            .Take(options.MaxItems)
            .ToList();

        var songs = parts.SelectMany(p => p.Songs)
            .GroupBy(s => EnrichmentValidator.Normalise(s.Title))
            .Select(g => g.OrderByDescending(s => s.Artist is not null).ThenByDescending(s => s.Confidence).First())
            .Take(options.MaxItems)
            .ToList();
        var summaries = parts.Select(p => p.Summary).OfType<string>().Take(2).ToList();
        var language = parts.Select(p => p.Language).OfType<string>().GroupBy(l => l).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault();

        return new EnrichmentDerived(model, info.Digest, prompt.Version, now, fingerprint,
            Union(p => p.Hosts), Union(p => p.Guests), Union(p => p.Topics), songs, Union(p => p.Artists), Union(p => p.Locations), Union(p => p.CallsToAction),
            language, summaries.Count == 0 ? null : string.Join(' ', summaries), parts.Sum(p => p.Dropped), parts.SelectMany(p => p.DropReasons).ToList());
    }
}
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~EnricherTests`
Expected: PASS, 15 tests (9 facts and 6 theory cases).

- [ ] **Step 6: Check the real model once (manual, not in CI)**

With Ollama running on the host, run (PowerShell):
```powershell
$schema = Get-Content config/analysis/prompts/enrich-v1.schema.json -Raw | ConvertFrom-Json
$body = @{ model = 'qwen3.6:latest'; stream = $false; think = $false; format = $schema; options = @{ temperature = 0.1; seed = 42; num_ctx = 8192 };
  messages = @(@{ role = 'system'; content = (Get-Content config/analysis/prompts/enrich-v1.system.md -Raw) },
               @{ role = 'user'; content = "Station: burst`nShow title (from the schedule): Backseat Aux`n`n[19:00:05] Good evening, I'm Amy and with me is Raff.`n[19:01:40] That was Mr Brightside by The Killers." }) } | ConvertTo-Json -Depth 20
(Invoke-RestMethod -Method Post http://127.0.0.1:11434/api/chat -Body $body -ContentType 'application/json' -TimeoutSec 900).message.content
```
Expected: a JSON object with `hosts` containing Amy (and Raff, with a quote containing "Raff"), `songs` containing Mr Brightside by The Killers, every quote copied from the two lines. Note the time taken in the Task 19 record. If the model is missing, run `ollama pull qwen3.6:latest` on the host.

- [ ] **Step 7: Commit**

```powershell
git add config/analysis/prompts src/WebAudioArchive.Core/Analysis/ShowEnrichment.cs src/WebAudioArchive.Analysis/Enrichment tests/WebAudioArchive.Tests/Support/FakeLlm.cs tests/WebAudioArchive.Tests/EnricherTests.cs
git commit -m "Analysis: Ollama enricher with evidence quotes, validation, pending retry and committed prompts (#83)"
```

---

### Task 15: The service: one work lane, lag metrics in /healthz, benchmark and rebuild

**Files:**
- Create: `src/WebAudioArchive.Analysis/AnalysisMetrics.cs`, `src/WebAudioArchive.Analysis/AnalysisHealth.cs`, `src/WebAudioArchive.Analysis/AnalysisRunner.cs`, `src/WebAudioArchive.Analysis/AnalysisComposition.cs`, `src/WebAudioArchive.Analysis/Benchmark.cs`
- Modify (replace): `src/WebAudioArchive.Analysis.Service/Program.cs`
- Create: `src/WebAudioArchive.Analysis.Service/AnalysisWorker.cs`
- Test: `tests/WebAudioArchive.Tests/AnalysisServiceTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1 to 14.
- Produces:
  - `sealed record MetricsSnapshot(DateTimeOffset StartedUtc, DateTimeOffset? LastPassUtc, int Backlog, int FailedSegments, DateTimeOffset? OldestPendingEndUtc, int JobsDone, int JobsFailed, double? LastRealtimeFactor, double? AverageRealtimeFactor, double AudioHoursLast24h, string? CurrentJob, string? Paused, bool? WhisperReady, int EnrichmentPending, int EnrichmentDone, int EnrichmentFailed, long FreeBytes, long ScratchBytes)`; `sealed class AnalysisMetrics(DateTimeOffset startedUtc)` with `Snapshot()`, `SetDisk`, `SetWhisper`, `SetPaused`, `SetCurrent`, `SetBacklog`, `SetEnrichment`, `JobFinished(JobOutcome, DateTimeOffset)`, `PassCompleted(DateTimeOffset)`.
  - `sealed record HealthResult(int StatusCode, object Body)`; `static class AnalysisHealth { static readonly TimeSpan StallAfter; HealthResult Build(MetricsSnapshot m, TimeSpan lagAlarm, DateTimeOffset now); }`
  - `sealed class AnalysisRunner(SegmentDiscovery, SegmentPipeline, ShowProcessor, Enricher?, ITranscriber, DiskGuard, ScratchSpace, AnalysisMetrics, IReadOnlyList<string> preference, DateTimeOffset? since, IClock, ILogger) { Task PassAsync(CancellationToken); int RebuildAll(); }`
  - `sealed record AnalysisRuntime(AnalysisRunner Runner, AnalysisMetrics Metrics, ScratchSpace Scratch, SegmentDecoder Decoder, ITranscriber Transcriber)`; `static class AnalysisComposition { static readonly TimeSpan ShowMargin; AnalysisRuntime Build(AnalysisSettings, LoadedConfig, ILoggerFactory, IClock); }`
  - `static class Benchmark { Task<int> RunAsync(string mp3, double seconds, SegmentDecoder decoder, ScratchSpace scratch, ITranscriber transcriber, TranscriptionOptions o, TextWriter output, CancellationToken ct); }`
  - Service: `public sealed class AnalysisEntryPoint` (namespace `WebAudioArchive.Analysis.Service`); `AnalysisWorker : BackgroundService`; command line `--healthcheck`, `--benchmark <file.mp3> [seconds]`, `--rebuild`; exit code 2 (config). A missing whisper model is not an exit: whisper reports not ready, the backlog and lag grow and `/healthz` says so (D18).

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/AnalysisServiceTests.cs`:
```csharp
extern alias analysissvc;

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;
using analysissvc::WebAudioArchive.Analysis.Service;

namespace WebAudioArchive.Tests;

public sealed class AnalysisServiceTests : IAsyncLifetime
{
    static readonly DateTimeOffset Show = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    static readonly string ConfigPath = Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json");
    readonly string _root = Directory.CreateTempSubdirectory("waa-svc-").FullName;
    readonly ManualClock _clock = new(Show.AddHours(3));
    readonly FakeTranscriber _whisper = new();
    readonly FakeLlm _llm = new();
    long _free = long.MaxValue;

    string A => Path.Combine(_root, "a");
    AnalysisPaths Paths => new(Path.Combine(_root, "analysis"), "burst");

    public async Task InitializeAsync()
    {
        await AnalysisFixtures.WriteDecodableSegmentAsync(A, "burst", Show, Signals.Concat(Signals.SpeechLike(30, 4), Signals.Sine(5)));
        File.WriteAllText(Path.Combine(_root, "schedule.json"), """
            { "station": "burst", "url": null, "timezone": "Europe/London", "fetchedUtc": "2026-10-15T12:00:00+00:00",
              "slots": [ { "day": "Thursday", "localStart": "20:00", "minutes": 60, "title": "Backseat Aux" } ] }
            """);
        _whisper.Respond = (_, _) => [AnalysisFixtures.Said(1, "Um, hello, I'm Amy on Burst.")];
    }

    public Task DisposeAsync()
    {
        Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    (AnalysisRunner Runner, AnalysisMetrics Metrics) Runner()
    {
        var config = AnalysisConfigLoader.Load(ConfigPath);
        var scratch = new ScratchSpace(Path.Combine(_root, "scratch"), 512L * 1024 * 1024);
        var disk = new DiskGuard(Paths.Root, 1L << 30, _ => _free);
        var schedule = new ScheduleProvider(Path.Combine(_root, "schedule.json"), null, "burst", NullLogger.Instance);
        var pipeline = new SegmentPipeline(Paths, config, scratch, new SegmentDecoder(), _whisper, disk,
            Watershed.From(config.Config.Swearing, schedule.Zone), schedule.TitleAt, _clock, NullLogger.Instance);
        var shows = new ShowProcessor(Paths, schedule, ["a"], AnalysisComposition.ShowMargin, _clock, NullLogger.Instance);
        var enricher = new Enricher(Paths, _llm, EnrichmentPrompt.Load(config.ConfigDir, config.Config.Enrichment), config.Config.Enrichment,
            "qwen3.6:latest", ["a"], _clock, NullLogger.Instance);
        var metrics = new AnalysisMetrics(_clock.UtcNow);
        var runner = new AnalysisRunner(new SegmentDiscovery([new RecorderRoot("a", A)], "burst", NullLogger.Instance), pipeline, shows, enricher,
            _whisper, disk, scratch, metrics, ["a"], null, _clock, NullLogger.Instance);
        return (runner, metrics);
    }

    [Fact]
    public async Task One_pass_analyses_the_segment_maps_the_show_and_enriches_it()
    {
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        var m = metrics.Snapshot();
        Assert.Equal(1, m.JobsDone);
        Assert.Equal(0, m.Backlog);
        Assert.Null(m.OldestPendingEndUtc);
        Assert.NotNull(m.LastRealtimeFactor);
        Assert.Null(m.Paused);
        Assert.True(m.WhisperReady);
        Assert.Equal(1, m.EnrichmentDone);
        var showDir = Paths.ShowDir("burst-20261015T1900Z", Show);
        Assert.True(AnalysisJson.Read<ShowAnalysis>(Path.Combine(showDir, AnalysisPaths.ShowAnalysisFile))!.Complete);
        Assert.Equal(EnrichmentStatus.Done, AnalysisJson.Read<ShowEnrichment>(Path.Combine(showDir, AnalysisPaths.EnrichmentFile))!.Status);
        Assert.Single(_llm.Requests);

        await runner.PassAsync(CancellationToken.None);
        Assert.Equal(1, _whisper.Calls);
        Assert.Single(_llm.Requests);
    }

    [Fact]
    public async Task Low_disk_stops_analysis_before_any_work()
    {
        _free = 1;
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        Assert.Equal(0, _whisper.Calls);
        Assert.Contains("free disk", metrics.Snapshot().Paused);
        Assert.NotNull(metrics.Snapshot().LastPassUtc);
    }

    [Fact]
    public async Task Whisper_not_ready_leaves_a_visible_backlog_and_lag()
    {
        _whisper.Ready = false;
        var (runner, metrics) = Runner();

        await runner.PassAsync(CancellationToken.None);

        var m = metrics.Snapshot();
        Assert.Equal(0, _whisper.Calls);
        Assert.Equal(1, m.Backlog);
        Assert.Contains("whisper", m.Paused);
        Assert.Equal(Show.AddSeconds(35), m.OldestPendingEndUtc);
        var health = AnalysisHealth.Build(m, TimeSpan.FromHours(6), _clock.UtcNow);
        Assert.Equal(200, health.StatusCode);
        Assert.Equal(503, AnalysisHealth.Build(m, TimeSpan.FromHours(1), _clock.UtcNow).StatusCode);
        Assert.Empty(_llm.Requests);
    }

    [Fact]
    public async Task Rebuild_redoes_events_without_whisper()
    {
        var (runner, _) = Runner();
        await runner.PassAsync(CancellationToken.None);
        File.Delete(Path.Combine(Paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.EventsFile));

        Assert.Equal(1, runner.RebuildAll());

        Assert.True(File.Exists(Path.Combine(Paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.EventsFile)));
        Assert.Equal(1, _whisper.Calls);
    }

    public static TheoryData<string, int, int, int, string?> HealthCases => new()
    {
        // name, minutes since last pass, hours of lag, expected code, paused reason
        { "ok", 1, 0, 200, null },
        { "paused", 1, 0, 200, "free disk below 20 GB" },
        { "lagging", 1, 7, 503, null },
        { "stalled", 20, 0, 503, null },
    };

    [Theory]
    [MemberData(nameof(HealthCases))]
    public void Health_reports_lag_stalls_and_pauses(string expected, int minutesSincePass, int lagHours, int code, string? paused)
    {
        var now = Show.AddHours(10);
        var m = new MetricsSnapshot(now.AddHours(-2), now.AddMinutes(-minutesSincePass), lagHours > 0 ? 3 : 0, 0, lagHours > 0 ? now.AddHours(-lagHours) : null,
            10, 0, 0.05, 0.06, 23.5, null, paused, true, 0, 4, 0, 50L << 30, 0);

        var h = AnalysisHealth.Build(m, TimeSpan.FromHours(6), now);
        var json = JsonSerializer.Serialize(h.Body);

        Assert.Equal(code, h.StatusCode);
        Assert.Contains($"\"status\":\"{expected}\"", json);
        Assert.Contains("\"lagSeconds\":" + (lagHours * 3600), json);
        Assert.Contains("\"audioHoursAnalysedLast24h\":23.5", json);
    }

    [Fact]
    public async Task The_benchmark_prints_the_realtime_factor()
    {
        var mp3 = Path.Combine(_root, "bench.mp3");
        await TestMp3.EncodeMp3Async(mp3, Signals.Stereo(Signals.SpeechLike(40, 9)), 2);
        var output = new StringWriter();

        var code = await Benchmark.RunAsync(mp3, 35, new SegmentDecoder(), new ScratchSpace(Path.Combine(_root, "bscratch"), 512L << 20),
            _whisper, new TranscriptionOptions(), output, CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains("35.0 s of audio", output.ToString());
        Assert.Contains("realtime factor", output.ToString());
        Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(_root, "bscratch")));
    }

    WebApplicationFactory<AnalysisEntryPoint> Host(params (string Name, string Value)[] extra) => new WebApplicationFactory<AnalysisEntryPoint>().WithWebHostBuilder(b =>
    {
        b.UseSetting("WAA_RECORDERS", $"a={A}");
        b.UseSetting("WAA_ANALYSIS_OUT", Path.Combine(_root, "host-out"));
        b.UseSetting("WAA_SCRATCH", Path.Combine(_root, "host-scratch"));
        b.UseSetting("WAA_ANALYSIS_CONFIG", ConfigPath);
        b.UseSetting("WAA_WHISPER_MODEL_PATH", Path.Combine(_root, "no-model.bin"));
        b.UseSetting("WAA_ENRICHMENT", "off");
        b.UseSetting("WAA_MIN_FREE_GB", "0");
        // Nothing is planned (the test segment is older), so the lag, and with it the status code, does not depend on today's date.
        b.UseSetting("WAA_ANALYSIS_SINCE", "2100-01-01T00:00:00Z");
        foreach (var (name, value) in extra) b.UseSetting(name, value);
    });

    [Fact]
    public async Task The_host_serves_healthz()
    {
        await using var factory = Host();
        var response = await factory.CreateClient().GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("\"status\"", body);
        Assert.Contains("\"lagSeconds\"", body);
    }

    [Fact]
    public void The_host_refuses_to_start_with_bad_settings()
    {
        using var factory = Host(("WAA_OLLAMA_MODEL", "kimi-k2.6:cloud"), ("WAA_ENRICHMENT", "on"));
        Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AnalysisServiceTests`
Expected: build FAILS with `CS0246` for `AnalysisRunner`, `AnalysisMetrics`, `AnalysisHealth`, `Benchmark`, `AnalysisComposition`, and `CS0234` for `analysissvc::WebAudioArchive.Analysis.Service`.

- [ ] **Step 3: Implement metrics, health, the runner, composition and the benchmark**

`src/WebAudioArchive.Analysis/AnalysisMetrics.cs`:
```csharp
using WebAudioArchive.Analysis.Work;

namespace WebAudioArchive.Analysis;

/// <summary>What /healthz reports. OldestPendingEndUtc is the end of the oldest closed segment not yet analysed: lag is now minus that.</summary>
public sealed record MetricsSnapshot(
    DateTimeOffset StartedUtc,
    DateTimeOffset? LastPassUtc,
    int Backlog,
    int FailedSegments,
    DateTimeOffset? OldestPendingEndUtc,
    int JobsDone,
    int JobsFailed,
    double? LastRealtimeFactor,
    double? AverageRealtimeFactor,
    double AudioHoursLast24h,
    string? CurrentJob,
    string? Paused,
    bool? WhisperReady,
    int EnrichmentPending,
    int EnrichmentDone,
    int EnrichmentFailed,
    long FreeBytes,
    long ScratchBytes);

public sealed class AnalysisMetrics(DateTimeOffset startedUtc)
{
    readonly object _gate = new();
    readonly Queue<(DateTimeOffset At, double Seconds)> _done = new();
    MetricsSnapshot _s = new(startedUtc, null, 0, 0, null, 0, 0, null, null, 0, null, null, null, 0, 0, 0, 0, 0);

    public MetricsSnapshot Snapshot()
    {
        lock (_gate) return _s;
    }

    public void SetDisk(long free, long scratch) => Set(s => s with { FreeBytes = free, ScratchBytes = scratch });

    public void SetWhisper(bool ready) => Set(s => s with { WhisperReady = ready });

    public void SetPaused(string? reason) => Set(s => s with { Paused = reason });

    public void SetCurrent(string? job) => Set(s => s with { CurrentJob = job });

    public void SetBacklog(int backlog, DateTimeOffset? oldestPendingEnd, int failed) =>
        Set(s => s with { Backlog = backlog, OldestPendingEndUtc = oldestPendingEnd, FailedSegments = failed });

    public void SetEnrichment(int pending, int done, int failed) =>
        Set(s => s with { EnrichmentPending = pending, EnrichmentDone = done, EnrichmentFailed = failed });

    public void PassCompleted(DateTimeOffset now) => Set(s => s with { LastPassUtc = now });

    /// <summary>Counts the job; for a full run, updates the realtime factor (last and a moving average) and the audio hours analysed in the last 24 hours.</summary>
    public void JobFinished(JobOutcome o, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (o.Status == JobStatus.Failed) _s = _s with { JobsFailed = _s.JobsFailed + 1 };
            if (o.Status != JobStatus.Done) return;
            _s = _s with { JobsDone = _s.JobsDone + 1 };
            if (o.RealtimeFactor is not { } rtf) return;
            _done.Enqueue((now, o.AudioSeconds));
            while (_done.Count > 0 && _done.Peek().At < now.AddHours(-24)) _done.Dequeue();
            _s = _s with
            {
                LastRealtimeFactor = Math.Round(rtf, 4),
                AverageRealtimeFactor = Math.Round(_s.AverageRealtimeFactor is { } avg ? 0.8 * avg + 0.2 * rtf : rtf, 4),
                AudioHoursLast24h = _done.Sum(d => d.Seconds) / 3600,
            };
        }
    }

    void Set(Func<MetricsSnapshot, MetricsSnapshot> change)
    {
        lock (_gate) _s = change(_s);
    }
}
```

`src/WebAudioArchive.Analysis/AnalysisHealth.cs`:
```csharp
namespace WebAudioArchive.Analysis;

public sealed record HealthResult(int StatusCode, object Body);

/// <summary>
/// /healthz for the analysis service (spec 5, D16): 503 when the lag passes the alarm or no pass has finished for 15
/// minutes, so `docker ps` shows unhealthy. A pause (low disk, whisper not ready) is reported but is 200 until the lag
/// it causes reaches the alarm. All times UTC.
/// </summary>
public static class AnalysisHealth
{
    public static readonly TimeSpan StallAfter = TimeSpan.FromMinutes(15);

    public static HealthResult Build(MetricsSnapshot m, TimeSpan lagAlarm, DateTimeOffset now)
    {
        var lag = m.OldestPendingEndUtc is { } oldest && now > oldest ? now - oldest : TimeSpan.Zero;
        var stalled = now - (m.LastPassUtc ?? m.StartedUtc) > StallAfter;
        var lagging = lag > lagAlarm;
        var status = stalled ? "stalled" : lagging ? "lagging" : m.Paused is not null ? "paused" : "ok";
        var body = new
        {
            status,
            lagSeconds = Math.Round(lag.TotalSeconds),
            lagAlarmSeconds = Math.Round(lagAlarm.TotalSeconds),
            backlogSegments = m.Backlog,
            failedSegments = m.FailedSegments,
            oldestPendingEndUtc = m.OldestPendingEndUtc,
            currentJob = m.CurrentJob,
            paused = m.Paused,
            realtimeFactor = new { last = m.LastRealtimeFactor, average = m.AverageRealtimeFactor },
            audioHoursAnalysedLast24h = Math.Round(m.AudioHoursLast24h, 2),
            jobsDone = m.JobsDone,
            jobsFailed = m.JobsFailed,
            whisperReady = m.WhisperReady,
            enrichment = new { pending = m.EnrichmentPending, done = m.EnrichmentDone, failed = m.EnrichmentFailed },
            freeDiskBytes = m.FreeBytes,
            scratchBytes = m.ScratchBytes,
            lastPassUtc = m.LastPassUtc,
            uptimeSeconds = Math.Round((now - m.StartedUtc).TotalSeconds),
        };
        return new HealthResult(stalled || lagging ? 503 : 200, body);
    }
}
```

`src/WebAudioArchive.Analysis/AnalysisRunner.cs`:
```csharp
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis;

/// <summary>
/// One pass of the single work lane (D17): stop at once if the disk is low; plan the jobs (A first, newest first);
/// run segment jobs (only redetects when whisper is not ready); rebuild the day timelines and shows they touched
/// (and the last 7 days once after start-up); then, only if no segment job is ready, enrich up to 2 finished shows.
/// </summary>
public sealed class AnalysisRunner(
    SegmentDiscovery discovery,
    SegmentPipeline pipeline,
    ShowProcessor shows,
    Enricher? enricher,
    ITranscriber transcriber,
    DiskGuard disk,
    ScratchSpace scratch,
    AnalysisMetrics metrics,
    IReadOnlyList<string> preference,
    DateTimeOffset? since,
    IClock clock,
    ILogger log)
{
    public const int MaxEnrichmentsPerPass = 2;
    public static readonly TimeSpan CatchUp = TimeSpan.FromDays(7);
    readonly Dictionary<string, ShowAnalysis> _shows = new(StringComparer.Ordinal);
    bool _caughtUp;

    public async Task PassAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        metrics.SetDisk(disk.FreeBytes(), scratch.UsedBytes());
        if (disk.IsLow())
        {
            metrics.SetPaused(LowDisk());
            log.LogWarning("Analysis paused: {Reason}", LowDisk());
            metrics.PassCompleted(now);
            return;
        }

        var ready = await transcriber.IsReadyAsync(ct);
        metrics.SetWhisper(ready);
        var plan = Decide();
        metrics.SetPaused(!ready && plan.Any(p => p.Decision.Action == JobAction.Run) ? "whisper is not ready (starting, loading the model, or not running)" : null);

        var touched = new List<(DateTimeOffset From, DateTimeOffset To)>();
        foreach (var (job, decision) in plan)
        {
            if (decision.Action is JobAction.Skip or JobAction.Wait) continue;
            if (decision.Action == JobAction.Run && !ready) continue;
            ct.ThrowIfCancellationRequested();
            if (disk.IsLow())
            {
                metrics.SetPaused(LowDisk());
                break;
            }

            metrics.SetCurrent(job.Source.JobId);
            var outcome = await pipeline.ProcessAsync(job, ct);
            metrics.JobFinished(outcome, clock.UtcNow);
            if (outcome.Status == JobOutcome.PausedDisk)
            {
                metrics.SetPaused(LowDisk());
                break;
            }

            if (outcome.Status == JobStatus.Done) touched.Add((job.Source.Meta.StartUtc, job.Source.Meta.EndUtc));
            if (outcome.Status == JobStatus.Waiting) ready = await transcriber.IsReadyAsync(ct);
        }

        metrics.SetCurrent(null);
        var after = Decide();
        var pending = Pending(after);
        metrics.SetBacklog(pending.Count, pending.Count == 0 ? null : pending.Min(p => p.ToUtc),
            after.Count(p => p.Decision.Action == JobAction.Skip && p.Decision.Reason.StartsWith("failed", StringComparison.Ordinal)));

        if (!_caughtUp)
        {
            touched.Add((now - CatchUp, now));
            _caughtUp = true;
        }

        foreach (var (from, to) in MergeRanges(touched))
            foreach (var doc in shows.Update(from, to, pending))
                _shows[doc.ShowId] = doc;
        foreach (var old in _shows.Values.Where(s => s.StartUtc < now - TimeSpan.FromDays(14)).Select(s => s.ShowId).ToList()) _shows.Remove(old);

        if (enricher is not null)
        {
            var segmentWorkReady = ready && after.Any(p => p.Decision.Action is JobAction.Run or JobAction.Redetect);
            if (!segmentWorkReady)
            {
                foreach (var show in _shows.Values.Where(enricher.IsDue).OrderByDescending(s => s.StartUtc).Take(MaxEnrichmentsPerPass).ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    await enricher.EnrichAsync(show, ct);
                }
            }

            var statuses = _shows.Values.Select(enricher.StatusOf).ToList();
            metrics.SetEnrichment(_shows.Values.Count(enricher.IsDue), statuses.Count(s => s == EnrichmentStatus.Done), statuses.Count(s => s == EnrichmentStatus.Failed));
        }

        metrics.PassCompleted(clock.UtcNow);
    }

    /// <summary>--rebuild: events.json of every finished segment from its transcript and features, then the day files and shows. No whisper, no audio decode.</summary>
    public int RebuildAll()
    {
        var jobs = WorkPlanner.Plan(discovery.Scan(), preference, since);
        var rebuilt = jobs.Count(pipeline.RebuildEvents);
        if (jobs.Count > 0)
        {
            var pending = Pending(Decide());
            foreach (var doc in shows.Update(jobs.Min(j => j.Source.Meta.StartUtc), jobs.Max(j => j.Source.Meta.EndUtc), pending))
                _shows[doc.ShowId] = doc;
        }

        log.LogInformation("Rebuilt events for {Count} segments", rebuilt);
        return rebuilt;
    }

    List<(SegmentJob Job, JobDecision Decision)> Decide() =>
        WorkPlanner.Plan(discovery.Scan(), preference, since).Select(j => (j, pipeline.Decide(j))).ToList();

    static List<SourceSpan> Pending(List<(SegmentJob Job, JobDecision Decision)> decided) => decided
        .Where(p => p.Decision.Action is not JobAction.Skip)
        .Select(p => new SourceSpan(p.Job.Source.Recorder, p.Job.Source.Meta.StartUtc, p.Job.Source.Meta.EndUtc))
        .ToList();

    string LowDisk() => $"free disk below {disk.MinFreeBytes / 1073741824.0:0.#} GB: analysis stopped so the recorders keep the space";

    static IEnumerable<(DateTimeOffset From, DateTimeOffset To)> MergeRanges(List<(DateTimeOffset From, DateTimeOffset To)> ranges)
    {
        (DateTimeOffset From, DateTimeOffset To)? run = null;
        foreach (var r in ranges.OrderBy(r => r.From))
        {
            if (run is { } current && r.From <= current.To)
            {
                run = (current.From, r.To > current.To ? r.To : current.To);
                continue;
            }

            if (run is { } done) yield return done;
            run = r;
        }

        if (run is { } last) yield return last;
    }
}
```

`src/WebAudioArchive.Analysis/AnalysisComposition.cs`:
```csharp
using Microsoft.Extensions.Logging;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Enrichment;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Shows;
using WebAudioArchive.Analysis.Transcription;
using WebAudioArchive.Analysis.Work;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Analysis;

public sealed record AnalysisRuntime(AnalysisRunner Runner, AnalysisMetrics Metrics, ScratchSpace Scratch, SegmentDecoder Decoder, ITranscriber Transcriber);

/// <summary>Builds the service from its settings and config: whisper in process (Whisper.net, D18), Ollama over HTTP (20 minutes), the saved schedule, the 5 minute show margin.</summary>
public static class AnalysisComposition
{
    public static readonly TimeSpan ShowMargin = TimeSpan.FromMinutes(5);

    public static AnalysisRuntime Build(AnalysisSettings s, LoadedConfig c, ILoggerFactory loggers, IClock clock)
    {
        var log = loggers.CreateLogger("WebAudioArchive.Analysis");
        var paths = new AnalysisPaths(s.OutputDir, s.Station);
        var scratch = new ScratchSpace(s.ScratchDir, s.ScratchMaxBytes);
        var decoder = new SegmentDecoder();
        var transcriber = new WhisperNetTranscriber(s.WhisperModelPath, s.WhisperDevice, s.WhisperThreads, s.WhisperDtw);
        var disk = new DiskGuard(s.OutputDir, s.MinFreeBytes);
        var schedule = new ScheduleProvider(s.ScheduleFile, s.StationsConfigPath, s.Station, log);
        var watershed = Watershed.From(c.Config.Swearing, schedule.Zone);
        var pipeline = new SegmentPipeline(paths, c, scratch, decoder, transcriber, disk, watershed, schedule.TitleAt, clock, log);
        var shows = new ShowProcessor(paths, schedule, s.Preference, ShowMargin, clock, log);
        var enricher = s.EnrichmentEnabled
            ? new Enricher(paths, new OllamaClient(new HttpClient { Timeout = TimeSpan.FromMinutes(20) }, s.OllamaUrl),
                EnrichmentPrompt.Load(c.ConfigDir, c.Config.Enrichment), c.Config.Enrichment, s.OllamaModel, s.Preference, clock, log)
            : null;
        var metrics = new AnalysisMetrics(clock.UtcNow);
        var runner = new AnalysisRunner(new SegmentDiscovery(s.Recorders, s.Station, log), pipeline, shows, enricher, transcriber, disk, scratch,
            metrics, s.Preference, s.Since, clock, log);
        return new AnalysisRuntime(runner, metrics, scratch, decoder, transcriber);
    }
}
```

`src/WebAudioArchive.Analysis/Benchmark.cs`:
```csharp
using System.Diagnostics;
using System.Globalization;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Transcription;

namespace WebAudioArchive.Analysis;

/// <summary>
/// --benchmark &lt;file.mp3&gt; [seconds]: decodes a copy of the file in scratch, transcribes its first N seconds (600 by
/// default) with the configured transcriber and prints the realtime factor. Writes no analysis output (issue #85).
/// </summary>
public static class Benchmark
{
    public static async Task<int> RunAsync(string mp3, double seconds, SegmentDecoder decoder, ScratchSpace scratch, ITranscriber transcriber,
        TranscriptionOptions o, TextWriter output, CancellationToken ct)
    {
        if (!File.Exists(mp3))
        {
            output.WriteLine($"benchmark: {mp3} not found");
            return 2;
        }

        var length = new FileInfo(mp3).Length;
        using var lease = scratch.Acquire("benchmark", SegmentDecoder.ScratchBytesFor(length, length / 16000.0));
        var decodeClock = Stopwatch.StartNew();
        var decoded = await decoder.DecodeAsync(mp3, length, 44100, 2, lease, ct);
        var decodeSeconds = decodeClock.Elapsed.TotalSeconds;
        var wav = WavFile.ReadInfo(decoded.SpeechWavPath);
        var audio = Math.Min(seconds, wav.Seconds);
        var chunks = ChunkPlanner.Plan(decoded.Features, o.ChunkSeconds, o.BoundarySearchSeconds)
            .Where(c => c.Start < audio)
            .Select(c => c with { End = Math.Min(c.End, audio) })
            .ToList();

        double transcribe = 0;
        var words = 0;
        foreach (var c in chunks)
        {
            var bytes = WavFile.Slice(decoded.SpeechWavPath, wav, c.Start, c.End);
            var started = Stopwatch.GetTimestamp();
            var segments = await transcriber.TranscribeAsync(new TranscriptionRequest(bytes, PromptBuilder.Build(o, null), o.Language), ct);
            transcribe += Stopwatch.GetElapsedTime(started).TotalSeconds;
            words += segments.Sum(s => s.Words.Count);
        }

        var rtf = audio > 0 ? transcribe / audio : 0;
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"benchmark: {transcriber.Engine} {transcriber.Model}: {audio:0.0} s of audio in {chunks.Count} chunks, decode {decodeSeconds:0.0} s, transcribe {transcribe:0.0} s, realtime factor {rtf:0.000} ({(rtf > 0 ? 1 / rtf : 0):0.0} times faster than real time), {words} words"));
        return 0;
    }
}
```

- [ ] **Step 4: Replace the service host**

`src/WebAudioArchive.Analysis.Service/AnalysisWorker.cs`:
```csharp
using WebAudioArchive.Analysis;

namespace WebAudioArchive.Analysis.Service;

/// <summary>A public type in this assembly, for WebApplicationFactory in the tests.</summary>
public sealed class AnalysisEntryPoint;

/// <summary>Sweeps scratch left by a crash, then runs a pass every 60 s until stopped. A failed pass is logged and retried; it never stops the service.</summary>
public sealed class AnalysisWorker(AnalysisRuntime runtime, ILogger<AnalysisWorker> log) : BackgroundService
{
    public static readonly TimeSpan PassInterval = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        var swept = runtime.Scratch.SweepStale();
        if (swept > 0) log.LogInformation("Removed {Count} leftover scratch entries", swept);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await runtime.Runner.PassAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Analysis pass failed; trying again in {Seconds} s", PassInterval.TotalSeconds);
            }

            try
            {
                await Task.Delay(PassInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
```

`src/WebAudioArchive.Analysis.Service/Program.cs`:
```csharp
using System.Globalization;
using WebAudioArchive.Analysis;
using WebAudioArchive.Analysis.Service;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Core.Recording;

if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("WAA_PORT") ?? "8080";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
string? Setting(string name) => builder.Configuration[name] ?? Environment.GetEnvironmentVariable(name);

AnalysisSettings settings;
LoadedConfig config;
try
{
    settings = AnalysisSettings.Load(Setting);
    config = AnalysisConfigLoader.Load(settings.ConfigPath);
}
catch (AnalysisSettingsException ex)
{
    Console.Error.WriteLine("config error: " + ex.Message);
    return 2;
}
catch (AnalysisConfigException ex)
{
    foreach (var e in ex.Errors) Console.Error.WriteLine("config error: " + e);
    return 2;
}

builder.Logging.AddSimpleConsole(o =>
{
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z' ";
});
using var loggers = LoggerFactory.Create(l => l.AddSimpleConsole(o =>
{
    o.UseUtcTimestamp = true;
    o.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss'Z' ";
}));

AnalysisRuntime runtime;
try
{
    runtime = AnalysisComposition.Build(settings, config, loggers, new SystemClock());
}
catch (AnalysisConfigException ex)
{
    foreach (var e in ex.Errors) Console.Error.WriteLine("config error: " + e);
    return 2;
}

var benchmark = Array.IndexOf(args, "--benchmark");
if (benchmark >= 0)
{
    if (benchmark + 1 >= args.Length)
    {
        Console.Error.WriteLine("usage: --benchmark <file.mp3> [seconds]");
        return 2;
    }

    var seconds = benchmark + 2 < args.Length && double.TryParse(args[benchmark + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 600;
    return await Benchmark.RunAsync(args[benchmark + 1], seconds, runtime.Decoder, runtime.Scratch, runtime.Transcriber, config.Config.Transcription, Console.Out, CancellationToken.None);
}

if (args.Contains("--rebuild"))
{
    Console.WriteLine($"rebuilt events for {runtime.Runner.RebuildAll()} segments");
    return 0;
}

if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls($"http://+:{settings.Port}");

builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(runtime);
builder.Services.AddHostedService<AnalysisWorker>();

var app = builder.Build();
app.Logger.LogInformation(
    "Analysing {Station} from recorders {Recorders} (preference {Preference}) into {Out}; whisper {Model} on {Device} (DTW {Dtw}); enrichment {Enrichment}",
    settings.Station, string.Join(", ", settings.Recorders.Select(r => $"{r.Id}={r.Root}")), string.Join(",", settings.Preference), settings.OutputDir,
    settings.WhisperModelPath, settings.WhisperDevice, settings.WhisperDtw ? "on" : "off", settings.EnrichmentEnabled ? $"{settings.OllamaModel} at {settings.OllamaUrl}" : "off");

app.MapGet("/healthz", (AnalysisRuntime rt, AnalysisSettings s) =>
{
    var h = AnalysisHealth.Build(rt.Metrics.Snapshot(), s.LagAlarm, DateTimeOffset.UtcNow);
    return Results.Json(h.Body, statusCode: h.StatusCode);
});

app.Run();
return 0;
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AnalysisServiceTests`
Expected: PASS, 11 tests (7 facts and 4 theory cases). Then `dotnet test`: all pass.

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Analysis/AnalysisMetrics.cs src/WebAudioArchive.Analysis/AnalysisHealth.cs src/WebAudioArchive.Analysis/AnalysisRunner.cs src/WebAudioArchive.Analysis/AnalysisComposition.cs src/WebAudioArchive.Analysis/Benchmark.cs src/WebAudioArchive.Analysis.Service tests/WebAudioArchive.Tests/AnalysisServiceTests.cs
git commit -m "Analysis service: single work lane, lag and realtime factor in /healthz, benchmark and rebuild commands (#85, #77)"
```

---

### Task 16: Containers: the analysis image (CPU and CUDA targets, whisper in process), compose, the GPU override and CI

**Files:**
- Create: `docker/Dockerfile.analysis`, `docker/docker-compose.gpu.yml`
- Modify: `docker/docker-compose.yml` (add `analysis`; the recorder and web services are not changed), `.github/workflows/ci.yml`

Decision D18: there is no whisper container, no `Dockerfile.whisper` and no CUDA build workflow; whisper.cpp comes as prebuilt native libraries in the Whisper.net runtime packages, and the GPU image only adds NVIDIA's CUDA 13.0 runtime and cuBLAS packages.
- Create: `tests/WebAudioArchive.Tests/Support/RepoFiles.cs`
- Test: `tests/WebAudioArchive.Tests/DeploymentFilesTests.cs`

**Interfaces:**
- Consumes: the service from Task 15 (`--healthcheck`, port 8080, settings names).
- Produces: images `webaudioarchive-analysis:local` (target `cpu`) and `webaudioarchive-analysis:cuda` (target `cuda`); container `waa-analysis` (health on `http://127.0.0.1:8095/healthz`); host folders `data/analysis` (output) and `models/` (whisper models, read-only in the container at `/models`). Test helper `RepoFiles.Root` and `RepoFiles.ComposeServices(string relativePath)` (service name to its YAML block).

- [ ] **Step 1: Download the whisper models (owner action, about 1 GB, not committed)**

Run:
```powershell
New-Item -ItemType Directory -Force models | Out-Null
$hf = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/5359861c739e955e79d9a303bcbc70fb988958b1'
curl.exe -L --fail -o models/ggml-large-v3-turbo-q8_0.bin "$hf/ggml-large-v3-turbo-q8_0.bin"
curl.exe -L --fail -o models/ggml-base.en.bin "$hf/ggml-base.en.bin"
(Get-FileHash models/ggml-large-v3-turbo-q8_0.bin -Algorithm SHA256).Hash.ToLower()
(Get-FileHash models/ggml-base.en.bin -Algorithm SHA256).Hash.ToLower()
git status --short models
```
Expected: the two hashes are `317eb69c11673c9de1e1f0d459b253999804ec71ac4c23c17ecf5fbe24e259a1` and `a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002`; sizes 874,188,075 and 147,964,211 bytes; `git status` prints nothing (the folder is ignored, Task 1). A different hash means a different file: stop and check the URL.

- [ ] **Step 2: Write the repository file helper and the failing tests**

`tests/WebAudioArchive.Tests/Support/RepoFiles.cs`:
```csharp
using System.Text.RegularExpressions;

namespace WebAudioArchive.Tests.Support;

/// <summary>Files in the repository checkout (found by walking up to WebAudioArchive.slnx), for checks on deployment files.</summary>
public static partial class RepoFiles
{
    public static string Root
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WebAudioArchive.slnx"))) dir = dir.Parent;
            Assert.True(dir is not null, "WebAudioArchive.slnx was not found above " + AppContext.BaseDirectory);
            return dir!.FullName;
        }
    }

    public static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    /// <summary>Each service under "services:" and its block of lines (to the next service or top-level key).</summary>
    public static Dictionary<string, string> ComposeServices(string relative)
    {
        var services = new Dictionary<string, string>(StringComparer.Ordinal);
        string? current = null;
        var inServices = false;
        var block = new List<string>();
        foreach (var line in Read(relative).Replace("\r", "").Split('\n'))
        {
            if (line.StartsWith("services:", StringComparison.Ordinal)) { inServices = true; continue; }
            if (!inServices) continue;
            if (line.Length > 0 && !char.IsWhiteSpace(line[0]) && !line.StartsWith('#'))
            {
                if (current is not null) services[current] = string.Join('\n', block);
                current = null;
                inServices = false;
                continue;
            }

            var m = ServiceLine().Match(line);
            if (m.Success)
            {
                if (current is not null) services[current] = string.Join('\n', block);
                current = m.Groups[1].Value;
                block = [];
                continue;
            }

            if (current is not null) block.Add(line);
        }

        if (current is not null) services[current] = string.Join('\n', block);
        return services;
    }

    [GeneratedRegex("^  ([a-z0-9-]+):\\s*$")]
    private static partial Regex ServiceLine();
}
```

`tests/WebAudioArchive.Tests/DeploymentFilesTests.cs`:
```csharp
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>The deployment rules that keep the recorders safe, checked on the files so a later edit cannot quietly break them.</summary>
public class DeploymentFilesTests
{
    static readonly Dictionary<string, string> Compose = RepoFiles.ComposeServices("docker/docker-compose.yml");

    [Fact]
    public void Analysis_reads_the_recordings_read_only_and_writes_only_its_own_folder()
    {
        var a = Compose["analysis"];

        Assert.Contains("/a:/data/a:ro", a);
        Assert.Contains("/b:/data/b:ro", a);
        Assert.Contains("/analysis:/analysis", a);
        Assert.Contains("web-cache:/web-cache:ro", a);
        Assert.Contains("../config:/config:ro", a);
        Assert.Contains("/models:/models:ro", a);
        foreach (var mount in a.Split('\n').Where(l => l.Contains(":/data", StringComparison.Ordinal)))
            Assert.EndsWith(":ro", mount.Trim());
        Assert.Contains("/scratch:size=2g", a);
    }

    [Fact]
    public void Analysis_runs_below_the_recorders_and_whisper_has_no_container_of_its_own()
    {
        var a = Compose["analysis"];
        Assert.Contains("cpu_shares: 256", a);
        Assert.Contains("oom_score_adj: 800", a);
        Assert.Contains("mem_limit:", a);
        Assert.Contains("cpus:", a);
        Assert.DoesNotContain("depends_on", a);
        Assert.False(Compose.ContainsKey("whisper"), "whisper runs inside the analysis process since decision D18");

        Assert.Contains("\"nice\", \"-n\", \"10\"", RepoFiles.Read("docker/Dockerfile.analysis"));
    }

    [Fact]
    public void Recorders_are_untouched_by_analysis()
    {
        foreach (var name in new[] { "recorder-a", "recorder-b" })
        {
            Assert.DoesNotContain("analysis", Compose[name]);
            Assert.DoesNotContain("gpu", Compose[name]);
            Assert.DoesNotContain("depends_on", Compose[name]);
        }
    }

    [Fact]
    public void The_gpu_override_changes_only_analysis()
    {
        var gpu = RepoFiles.ComposeServices("docker/docker-compose.gpu.yml");

        Assert.Equal(new[] { "analysis" }, gpu.Keys.Order(StringComparer.Ordinal));
        Assert.Contains("capabilities: [gpu]", gpu["analysis"]);
        Assert.Contains("target: cuda", gpu["analysis"]);
        Assert.Contains("WAA_WHISPER_DEVICE: cuda", gpu["analysis"]);
        Assert.Contains("ggml-large-v3-turbo-q8_0.bin", gpu["analysis"]);
    }

    [Fact]
    public void The_image_is_non_root_with_cpu_and_cuda_targets_and_no_native_build()
    {
        var analysis = RepoFiles.Read("docker/Dockerfile.analysis");
        Assert.Contains("USER $APP_UID", analysis);
        Assert.Contains("--healthcheck", analysis);
        Assert.Contains("AS cpu", analysis);
        Assert.Contains("FROM cpu AS cuda", analysis);
        Assert.Contains("libgomp1", analysis);
        Assert.Contains("cuda-cudart-13-0", analysis);
        Assert.Contains("libcublas-13-0", analysis);
        Assert.DoesNotContain("ffmpeg", analysis);
        Assert.DoesNotContain("cmake", analysis);
        Assert.False(File.Exists(Path.Combine(RepoFiles.Root, "docker", "Dockerfile.whisper")));
    }

    [Fact]
    public void Models_and_data_are_never_committed()
    {
        var ignore = RepoFiles.Read(".gitignore");
        Assert.Contains("/models/", ignore);
        Assert.Contains("/data/", ignore);
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DeploymentFilesTests`
Expected: FAIL: `KeyNotFoundException` for `analysis` (the compose file has no such service yet) and file-not-found for the new Dockerfile and override; `Models_and_data_are_never_committed` and `Recorders_are_untouched_by_analysis` pass.

- [ ] **Step 4: Write the image, the compose service, the override and CI**

`docker/Dockerfile.analysis`:
```dockerfile
# The analysis service (plan 3). Its own image and container: it never shares a process or an image with a recorder.
# Decision D18: MP3 decoding is managed code and whisper.cpp runs in process through Whisper.net, so nothing is compiled
# here beyond the .NET publish.
#   target cpu  (docker-compose.yml): the aspnet image plus the OpenMP runtime that whisper.cpp's CPU library links.
#   target cuda (docker-compose.gpu.yml): adds the CUDA 13.0 runtime and cuBLAS from NVIDIA's Ubuntu 24.04 repository
#               (the two libraries Whisper.net's CUDA runtime links); the NVIDIA container toolkit supplies the driver.
# Build and restart only it: docker compose -f docker/docker-compose.yml up -d --build analysis
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/WebAudioArchive.Core/ src/WebAudioArchive.Core/
COPY src/WebAudioArchive.Analysis/ src/WebAudioArchive.Analysis/
COPY src/WebAudioArchive.Analysis.Service/ src/WebAudioArchive.Analysis.Service/
# Keep only the linux-x64 native libraries; the CUDA ones go to /cuda for the cuda target.
RUN dotnet publish src/WebAudioArchive.Analysis.Service -c Release -o /app \
    && find /app/runtimes -mindepth 1 -maxdepth 1 ! -name linux-x64 ! -name cuda -exec rm -rf {} + \
    && find /app/runtimes/cuda -mindepth 1 -maxdepth 1 ! -name linux-x64 -exec rm -rf {} + \
    && mkdir /cuda && mv /app/runtimes/cuda /cuda/cuda

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS cpu
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 tzdata ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV WAA_STATION=burst \
    WAA_ANALYSIS_OUT=/analysis \
    WAA_SCRATCH=/scratch \
    WAA_ANALYSIS_CONFIG=/config/analysis/analysis.json \
    WAA_PORT=8080
RUN mkdir -p /analysis /scratch && chown $APP_UID /analysis /scratch
VOLUME /analysis
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=60s --timeout=10s --start-period=120s --retries=3 \
    CMD ["dotnet", "WebAudioArchive.Analysis.Service.dll", "--healthcheck"]
# nice 10: the service, and the whisper.cpp threads inside it, always run below the recorders.
ENTRYPOINT ["nice", "-n", "10", "dotnet", "WebAudioArchive.Analysis.Service.dll"]

FROM cpu AS cuda
USER root
ADD https://developer.download.nvidia.com/compute/cuda/repos/ubuntu2404/x86_64/cuda-keyring_1.1-1_all.deb /tmp/cuda-keyring.deb
RUN dpkg -i /tmp/cuda-keyring.deb && rm /tmp/cuda-keyring.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends cuda-cudart-13-0 libcublas-13-0 \
    && rm -rf /var/lib/apt/lists/*
ENV LD_LIBRARY_PATH=/usr/local/cuda-13.0/targets/x86_64-linux/lib \
    NVIDIA_VISIBLE_DEVICES=all \
    NVIDIA_DRIVER_CAPABILITIES=compute,utility
COPY --from=build /cuda/cuda /app/runtimes/cuda
USER $APP_UID
```

In `docker/docker-compose.yml`, add this service after the `web` service and before the top-level `volumes:` key (leave every existing line as it is):
```yaml

  # Analysis (plan 3): transcripts, detectors, events and show enrichment. Its own image and container, below the
  # recorders' priority (nice 10, low CPU shares, capped CPU and memory, killed first under memory pressure). It reads
  # the recorders' folders read-only, writes only data/analysis and a 2 GiB tmpfs scratch, and stops first when free
  # disk falls below WAA_MIN_FREE_GB. whisper.cpp runs inside it (Whisper.net, decision D18): here the CPU build with
  # base.en; docker-compose.gpu.yml switches to the CUDA target and large-v3-turbo. Models come from models/ (see
  # docs/analysis.md). Health: http://127.0.0.1:8095/healthz. Always name the service:
  #   docker compose -f docker/docker-compose.yml up -d --build analysis
  analysis:
    build:
      context: ..
      dockerfile: docker/Dockerfile.analysis
      target: cpu
    image: webaudioarchive-analysis:local
    container_name: waa-analysis
    restart: unless-stopped
    environment:
      WAA_STATION: burst
      WAA_RECORDERS: a=/data/a,b=/data/b
      WAA_ANALYSIS_PREFERENCE: a,b
      WAA_ANALYSIS_OUT: /analysis
      WAA_SCRATCH: /scratch
      WAA_SCRATCH_MAX_MB: "2048"
      WAA_MIN_FREE_GB: ${WAA_MIN_FREE_GB:-20}
      WAA_WHISPER_MODEL_PATH: ${WAA_WHISPER_MODEL_PATH:-/models/ggml-base.en.bin}
      WAA_WHISPER_DEVICE: cpu
      WAA_WHISPER_THREADS: "4"
      WAA_OLLAMA_URL: ${WAA_OLLAMA_URL:-http://host.docker.internal:11434}
      WAA_OLLAMA_MODEL: ${WAA_OLLAMA_MODEL:-qwen3.6:latest}
      WAA_ENRICHMENT: ${WAA_ENRICHMENT:-on}
      WAA_ANALYSIS_CONFIG: /config/analysis/analysis.json
      WAA_STATIONS_CONFIG: /config/stations.json
      WAA_SCHEDULE_FILE: /web-cache/schedule/burst.json
      WAA_LAG_ALARM_HOURS: "6"
    volumes:
      - ${WAA_DATA_DIR:-../data}/a:/data/a:ro
      - ${WAA_DATA_DIR:-../data}/b:/data/b:ro
      - ${WAA_DATA_DIR:-../data}/analysis:/analysis
      - ${WAA_MODELS_DIR:-../models}:/models:ro
      - ../config:/config:ro
      - web-cache:/web-cache:ro
    tmpfs:
      - /scratch:size=2g,mode=1777
    extra_hosts:
      - "host.docker.internal:host-gateway"
    cpus: 6
    cpu_shares: 256
    mem_limit: 6g
    oom_score_adj: 800
    pids_limit: 512
    ports:
      - "127.0.0.1:8095:8080"
```

`docker/docker-compose.gpu.yml`:
```yaml
# GPU override (spec 5, decisions D1 to D4 and D18): the analysis image's cuda target with whisper.cpp on the NVIDIA GPU,
# large-v3-turbo (q8_0), flash attention on and DTW off (D18: DTW cut 300 s requests short in the spike). It changes
# only analysis:
#   docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml up -d --build analysis
# Never run it without naming the service, so the recorders are never rebuilt or restarted by it.
services:
  analysis:
    build:
      target: cuda
    image: webaudioarchive-analysis:cuda
    environment:
      WAA_WHISPER_MODEL_PATH: /models/ggml-large-v3-turbo-q8_0.bin
      WAA_WHISPER_DEVICE: cuda
    deploy:
      resources:
        reservations:
          devices:
            - driver: nvidia
              count: 1
              capabilities: [gpu]
```

In `.github/workflows/ci.yml`, in the `docker` job, add after the existing two `docker build` lines (the `ldd` checks prove the images carry the libraries whisper.cpp links; `libcuda` itself comes from the driver at run time, so it is not checked):
```yaml
      - run: docker build -f docker/Dockerfile.analysis --target cpu -t webaudioarchive-analysis:ci .
      - run: docker run --rm --entrypoint sh webaudioarchive-analysis:ci -c "! ldd /app/runtimes/linux-x64/libggml-cpu-whisper.so | grep -E 'libgomp[^ ]* => not found'"
      - run: docker build -f docker/Dockerfile.analysis --target cuda -t webaudioarchive-analysis:ci-cuda .
      - run: docker run --rm --entrypoint sh webaudioarchive-analysis:ci-cuda -c "! ldd /app/runtimes/cuda/linux-x64/libggml-cuda-whisper.so | grep -E 'lib(cudart|cublas)[^ ]* => not found'"
      - run: docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml config --quiet
```

- [ ] **Step 5: Run the tests, then build both targets locally**

Run: `dotnet test --filter FullyQualifiedName~DeploymentFilesTests`
Expected: PASS, 6 tests.

Run:
```powershell
docker compose -f docker/docker-compose.yml config --quiet
docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml config --quiet
docker build -f docker/Dockerfile.analysis --target cpu -t webaudioarchive-analysis:local .
docker build -f docker/Dockerfile.analysis --target cuda -t webaudioarchive-analysis:cuda .
docker run --rm --entrypoint sh webaudioarchive-analysis:local -c "ls /app/runtimes; ! ldd /app/runtimes/linux-x64/libggml-cpu-whisper.so | grep -E 'libgomp[^ ]* => not found'"
docker run --rm --entrypoint sh webaudioarchive-analysis:cuda -c "! ldd /app/runtimes/cuda/linux-x64/libggml-cuda-whisper.so | grep -E 'lib(cudart|cublas)[^ ]* => not found'"
docker images webaudioarchive-analysis --format "{{.Tag}} {{.Size}}"
docker compose -f docker/docker-compose.yml ps
```
Expected: both `config` commands print nothing (valid); both builds succeed (the cuda build downloads about 1.3 GB from NVIDIA the first time); `ls` prints `linux-x64` only in the cpu image; the `ldd` checks exit 0; sizes about 380 MB (`local`) and about 1.9 GB (`cuda`), as measured for D18; `ps` still shows `waa-recorder-a`, `waa-recorder-b` and `waa-web` with their original uptime (nothing above restarted them). Starting the service is Task 19.

- [ ] **Step 6: Commit**

```powershell
git add docker/Dockerfile.analysis docker/docker-compose.yml docker/docker-compose.gpu.yml .github/workflows/ci.yml tests/WebAudioArchive.Tests/Support/RepoFiles.cs tests/WebAudioArchive.Tests/DeploymentFilesTests.cs
git commit -m "Analysis container (cpu and cuda targets, whisper in process), GPU override, resource limits below the recorders, CI image builds (#76, #77, #85)"
```

---

### Task 17: Web integration points (read-only data API for the player)

The web app gets read-only access to the analysis output so plan 4 can draw markers and segments in `PDMediaPlayer` and list transcripts. No page changes here: the UI is plan 4 (issue #11).

**Files:**
- Create: `src/WebAudioArchive.Core/Analysis/AnalysisStore.cs`
- Create: `src/WebAudioArchive.Web/Analysis/AnalysisReader.cs`, `src/WebAudioArchive.Web/Analysis/AnalysisMarkers.cs`
- Modify: `src/WebAudioArchive.Web/WebSettings.cs` (add `AnalysisDir`), `src/WebAudioArchive.Web/Program.cs` (register the reader, map three endpoints), `docker/docker-compose.yml` (web: `WAA_ANALYSIS`)
- Test: `tests/WebAudioArchive.Tests/AnalysisWebTests.cs`

**Interfaces:**
- Consumes: `AnalysisPaths`, `AnalysisJson`, `EventsDocument`, `AnalysisEvent`, `TranscriptDocument`, `TranscriptSlicer`, `TimedLine`, `ShowAnalysis`, `ShowEnrichment` (Core); `ShowOccurrence.TryParseId` (Core, Task 13); `WebSettings`, `ClipClock`, `ClipMarkers.Time`, `PDMediaMarker`, `PDMediaSegment` (Web).
- Produces:
  - `static class AnalysisStore { IEnumerable<string> SegmentDirs(AnalysisPaths, DateTimeOffset fromUtc, DateTimeOffset toUtc); IReadOnlyList<TranscriptDocument> Transcripts(AnalysisPaths, DateTimeOffset, DateTimeOffset); IReadOnlyList<AnalysisEvent> DayEvents(AnalysisPaths, DateTimeOffset, DateTimeOffset); }`
  - `WebSettings.AnalysisDir` (`string?`, from `WAA_ANALYSIS`; optional).
  - `sealed class AnalysisQueryException(int statusCode, string message)`; `sealed class AnalysisReader(WebSettings settings) { static TimeSpan MaxEventsRange (7 days), MaxTranscriptRange (6 hours); static (DateTimeOffset From, DateTimeOffset To) Range(string? from, string? to, TimeSpan max); IReadOnlyList<AnalysisEvent> Events(DateTimeOffset, DateTimeOffset, string? types); IReadOnlyList<TimedLine> Transcript(DateTimeOffset, DateTimeOffset); (ShowAnalysis? Analysis, ShowEnrichment? Enrichment) Show(string id); static IResult Respond(Func<object> make); }`
  - Endpoints (JSON, UTC): `GET /api/analysis/events?from=<UTC>&to=<UTC>[&types=swear,clipping]`, `GET /api/analysis/transcript?from=<UTC>&to=<UTC>`, `GET /api/analysis/shows/{id}` (id as on the Shows tab, for example `burst-20261015T1900Z`). 400 `{ error }` for bad input, 404 when analysis is not configured or the show has no analysis.
  - `static class AnalysisMarkers { const string Talk = "talk"; IReadOnlyDictionary<string, string> KindLabels, KindColors; IReadOnlyList<PDMediaMarker> Markers(IEnumerable<AnalysisEvent>, ClipClock); IReadOnlyList<PDMediaSegment> Segments(IEnumerable<AnalysisEvent>, IEnumerable<TimedLine>, ClipClock); }` (dead air becomes a segment as well as a marker; each transcript line becomes a `talk` segment)

- [ ] **Step 1: Pull first**

Run: `git pull --rebase` and `git log --oneline 19a06f0..HEAD -- src/WebAudioArchive.Web/Program.cs src/WebAudioArchive.Web/WebSettings.cs`. Read any changes the web UI work made so the edits below go onto the current text.

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/AnalysisWebTests.cs`:
```csharp
extern alias web;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Analysis;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed class AnalysisWebTests : IDisposable
{
    static readonly DateTimeOffset T = new(2026, 10, 15, 19, 0, 0, TimeSpan.Zero);
    readonly string _root = Directory.CreateTempSubdirectory("waa-aweb-").FullName;

    public AnalysisWebTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "a"));
        var paths = Paths;
        AnalysisJson.Write(paths.DayEvents(new DateOnly(2026, 10, 15)), Day("day:2026-10-15",
            Event(EventTypes.Swear, T.AddMinutes(10)), Event(EventTypes.Filler, T.AddMinutes(11)), Event(EventTypes.DeadAir, T.AddMinutes(30), 8),
            Event(EventTypes.Clipping, T.AddHours(2))));
        AnalysisJson.Write(Path.Combine(paths.SegmentDir("a/2026/10-15/190000"), AnalysisPaths.TranscriptFile),
            TranscriptFixtures.Of(T, "Good evening, this is Backseat Aux.", "Next up, a song.") with { JobId = "a/2026/10-15/190000" });
        AnalysisJson.Write(Path.Combine(paths.ShowDir("burst-20261015T1900Z", T), AnalysisPaths.ShowAnalysisFile),
            new ShowAnalysis(1, "burst-20261015T1900Z", "burst", "Backseat Aux", T, T.AddHours(1), T.AddMinutes(-5), T.AddMinutes(65), T.AddHours(2), true,
                [], new ShowCounts(0, 1, 0, 1, 0, 0, 1, 8, 1, 0.1, null, 3600), []));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    string AnalysisDir => Path.Combine(_root, "analysis");
    AnalysisPaths Paths => new(AnalysisDir, "burst");

    static AnalysisEvent Event(string type, DateTimeOffset at, double seconds = 0.4) => new(
        AnalysisEvent.MakeId(type, "a/2026/10-15/190000", (at - T).TotalSeconds), type, at, at.AddSeconds(seconds), Severities.Medium, 0.9, type, "1",
        EventSources.Transcript, "a", "a/2026/10-15/190000", (at - T).TotalSeconds, seconds, type + " label", null);

    static EventsDocument Day(string scope, params AnalysisEvent[] events) => new(1, "burst", scope, new DateTimeOffset(T.UtcDateTime.Date, TimeSpan.Zero),
        new DateTimeOffset(T.UtcDateTime.Date.AddDays(1), TimeSpan.Zero), T, new Dictionary<string, string>(), [], null, events);

    AnalysisReader Reader(bool configured = true) =>
        new(TestSettings.Create(("a", Path.Combine(_root, "a"))) with { AnalysisDir = configured ? AnalysisDir : null });

    [Fact]
    public void Events_come_from_the_day_files_in_range_and_by_type()
    {
        var r = Reader();
        Assert.Equal(3, r.Events(T, T.AddHours(1), null).Count);
        Assert.Equal(EventTypes.Swear, Assert.Single(r.Events(T, T.AddHours(1), "swear")).Type);
        Assert.Equal(2, r.Events(T, T.AddHours(1), "swear, filler").Count);
        Assert.Empty(r.Events(T.AddHours(5), T.AddHours(6), null));
    }

    [Fact]
    public void Transcript_lines_and_show_files_are_read()
    {
        var r = Reader();
        var lines = r.Transcript(T, T.AddMinutes(30));
        Assert.Equal(new[] { "Good evening, this is Backseat Aux.", "Next up, a song." }, lines.Select(l => l.Text));

        var (analysis, enrichment) = r.Show("burst-20261015T1900Z");
        Assert.Equal("Backseat Aux", analysis!.Title);
        Assert.Null(enrichment);
        Assert.Equal(404, Assert.Throws<AnalysisQueryException>(() => r.Show("burst-20261015T2300Z")).StatusCode);
        Assert.Equal(404, Assert.Throws<AnalysisQueryException>(() => r.Show("../../etc")).StatusCode);
    }

    [Theory]
    [InlineData(null, "2026-10-15T20:00:00Z")]
    [InlineData("2026-10-15T19:00:00", "2026-10-15T20:00:00Z")]
    [InlineData("2026-10-15T20:00:00Z", "2026-10-15T19:00:00Z")]
    [InlineData("2026-10-01T00:00:00Z", "2026-10-15T00:00:00Z")]
    public void Bad_ranges_are_400(string? from, string to)
    {
        Assert.Equal(400, Assert.Throws<AnalysisQueryException>(() => AnalysisReader.Range(from, to, AnalysisReader.MaxEventsRange)).StatusCode);
    }

    [Fact]
    public void Without_analysis_the_reader_says_404()
    {
        Assert.Equal(404, Assert.Throws<AnalysisQueryException>(() => Reader(configured: false).Events(T, T.AddHours(1), null)).StatusCode);
    }

    [Fact]
    public void Events_become_player_markers_and_dead_air_and_talk_become_segments()
    {
        var clock = new ClipClock([(0, T.AddMinutes(-5))], 4200);
        var events = Reader().Events(T, T.AddHours(1), null);
        var lines = Reader().Transcript(T, T.AddHours(1));

        var markers = AnalysisMarkers.Markers(events, clock);
        var segments = AnalysisMarkers.Segments(events, lines, clock);

        var swear = Assert.Single(markers, m => m.Kind == EventTypes.Swear);
        Assert.Equal(TimeSpan.FromMinutes(15), swear.Position);
        Assert.Contains("19:10:00 UTC", swear.Label);
        Assert.Contains("confidence 0.90", swear.Detail);
        var dead = Assert.Single(segments, s => s.Kind == EventTypes.DeadAir);
        Assert.Equal(TimeSpan.FromMinutes(35), dead.Start);
        Assert.Equal(TimeSpan.FromMinutes(35).Add(TimeSpan.FromSeconds(8)), dead.End);
        Assert.Equal(2, segments.Count(s => s.Kind == AnalysisMarkers.Talk));
        foreach (var kind in new[] { EventTypes.Swear, EventTypes.Clipping, EventTypes.Filler, EventTypes.DeadAir, EventTypes.Song, AnalysisMarkers.Talk })
        {
            Assert.True(AnalysisMarkers.KindLabels.ContainsKey(kind));
            Assert.True(AnalysisMarkers.KindColors.ContainsKey(kind));
        }
    }

    WebApplicationFactory<WebEntryPoint> Host(bool analysis) => new WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(b =>
    {
        b.UseSetting("WAA_RECORDERS", $"a={Path.Combine(_root, "a")}");
        b.UseSetting("WAA_CACHE", Path.Combine(_root, "cache"));
        if (analysis) b.UseSetting("WAA_ANALYSIS", AnalysisDir);
    });

    [Fact]
    public async Task The_endpoints_serve_json_and_errors()
    {
        await using var factory = Host(analysis: true);
        var client = factory.CreateClient();

        var events = await client.GetAsync("/api/analysis/events?from=2026-10-15T19:00:00Z&to=2026-10-15T20:00:00Z&types=swear");
        Assert.Equal(HttpStatusCode.OK, events.StatusCode);
        var body = await events.Content.ReadAsStringAsync();
        Assert.Contains("\"type\":\"swear\"", body);
        Assert.Contains("\"link\":\"/player?from=2026-10-15T19:09:55Z", body);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/analysis/transcript?from=2026-10-15T19:00:00Z&to=2026-10-15T19:30:00Z")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/analysis/shows/burst-20261015T1900Z")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/analysis/shows/burst-20261016T1900Z")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/analysis/events?from=yesterday&to=today")).StatusCode);
    }

    [Fact]
    public async Task Without_waa_analysis_the_endpoints_are_404()
    {
        await using var factory = Host(analysis: false);
        var response = await factory.CreateClient().GetAsync("/api/analysis/events?from=2026-10-15T19:00:00Z&to=2026-10-15T20:00:00Z");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("WAA_ANALYSIS", await response.Content.ReadAsStringAsync());
    }
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AnalysisWebTests`
Expected: build FAILS with `CS0234` for `web::WebAudioArchive.Web.Analysis` and `CS0117` for `AnalysisDir`.

- [ ] **Step 4: Implement the store, the reader, the markers and the endpoints**

`src/WebAudioArchive.Core/Analysis/AnalysisStore.cs`:
```csharp
using System.Globalization;

namespace WebAudioArchive.Core.Analysis;

/// <summary>Read-only helpers over the analysis folder for readers such as the web app. Damaged files are skipped.</summary>
public static class AnalysisStore
{
    /// <summary>Segment result folders for segments filed from the day before <paramref name="fromUtc"/> to the day of <paramref name="toUtc"/>.</summary>
    public static IEnumerable<string> SegmentDirs(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (!Directory.Exists(paths.SegmentsDir)) yield break;
        var first = DateOnly.FromDateTime(fromUtc.UtcDateTime).AddDays(-1);
        var last = DateOnly.FromDateTime(toUtc.UtcDateTime);
        foreach (var recorder in Directory.EnumerateDirectories(paths.SegmentsDir).Order(StringComparer.Ordinal))
        {
            for (var d = first; d <= last; d = d.AddDays(1))
            {
                var dayDir = Path.Combine(recorder, d.ToString("yyyy", CultureInfo.InvariantCulture), d.ToString("MM-dd", CultureInfo.InvariantCulture));
                if (!Directory.Exists(dayDir)) continue;
                foreach (var job in Directory.EnumerateDirectories(dayDir).Order(StringComparer.Ordinal)) yield return job;
            }
        }
    }

    public static IReadOnlyList<TranscriptDocument> Transcripts(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc) =>
        SegmentDirs(paths, fromUtc, toUtc)
            .Select(d => AnalysisJson.TryRead<TranscriptDocument>(Path.Combine(d, AnalysisPaths.TranscriptFile), out _))
            .OfType<TranscriptDocument>()
            .Where(t => t.AudioFromUtc < toUtc && t.AudioToUtc > fromUtc)
            .ToList();

    /// <summary>Events starting in [fromUtc, toUtc), from the merged day files (each event is filed in the day it starts).</summary>
    public static IReadOnlyList<AnalysisEvent> DayEvents(AnalysisPaths paths, DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        var events = new List<AnalysisEvent>();
        for (var d = DateOnly.FromDateTime(fromUtc.UtcDateTime); d <= DateOnly.FromDateTime(toUtc.UtcDateTime); d = d.AddDays(1))
        {
            var doc = AnalysisJson.TryRead<EventsDocument>(paths.DayEvents(d), out _);
            if (doc is not null) events.AddRange(doc.Events.Where(e => e.StartUtc >= fromUtc && e.StartUtc < toUtc));
        }

        return events.OrderBy(e => e.StartUtc).ToList();
    }
}
```

In `src/WebAudioArchive.Web/WebSettings.cs`:
- after `public int DefaultMarginMinutes { get; init; } = 5;` add:
```csharp

    /// <summary>The analysis output folder (WAA_ANALYSIS, read-only), or null when analysis is not set up.</summary>
    public string? AnalysisDir { get; init; }
```
- replace `return new WebSettings(station, recorders, string.IsNullOrWhiteSpace(cache) ? "/cache" : cache.Trim());` with:
```csharp
        var analysis = get("WAA_ANALYSIS");
        return new WebSettings(station, recorders, string.IsNullOrWhiteSpace(cache) ? "/cache" : cache.Trim())
        {
            AnalysisDir = string.IsNullOrWhiteSpace(analysis) ? null : analysis.Trim(),
        };
```

`src/WebAudioArchive.Web/Analysis/AnalysisReader.cs`:
```csharp
using System.Globalization;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Web.Analysis;

public sealed class AnalysisQueryException(int statusCode, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// The web app's read-only view of the analysis output (WAA_ANALYSIS, mounted read-only): events, transcript lines and
/// show documents for a UTC range or a show id. It never writes there. Access rules for presenters come with plan 4.
/// </summary>
public sealed class AnalysisReader(WebSettings settings)
{
    public static readonly TimeSpan MaxEventsRange = TimeSpan.FromDays(7);
    public static readonly TimeSpan MaxTranscriptRange = TimeSpan.FromHours(6);

    AnalysisPaths Paths => settings.AnalysisDir is { } dir && Directory.Exists(dir)
        ? new AnalysisPaths(dir, settings.Station)
        : throw new AnalysisQueryException(404, "Analysis is not set up for this web app (WAA_ANALYSIS is not set or its folder does not exist yet).");

    IReadOnlyList<string> Preference => settings.Recorders.Select(r => r.Id).ToList();

    /// <summary>Both ends must be UTC times ending in Z, from before to, at most <paramref name="max"/> apart.</summary>
    public static (DateTimeOffset From, DateTimeOffset To) Range(string? from, string? to, TimeSpan max)
    {
        var f = Utc(from, "from");
        var t = Utc(to, "to");
        if (t <= f) throw new AnalysisQueryException(400, "to must be after from.");
        if (t - f > max) throw new AnalysisQueryException(400, string.Create(CultureInfo.InvariantCulture, $"The range may be at most {max.TotalHours:0} hours."));
        return (f, t);
    }

    public IReadOnlyList<AnalysisEvent> Events(DateTimeOffset from, DateTimeOffset to, string? types)
    {
        var wanted = (types ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.Ordinal);
        return AnalysisStore.DayEvents(Paths, from, to).Where(e => wanted.Count == 0 || wanted.Contains(e.Type)).ToList();
    }

    public IReadOnlyList<TimedLine> Transcript(DateTimeOffset from, DateTimeOffset to) =>
        TranscriptSlicer.Slice(AnalysisStore.Transcripts(Paths, from, to), from, to, Preference);

    public (ShowAnalysis? Analysis, ShowEnrichment? Enrichment) Show(string id)
    {
        if (!ShowOccurrence.TryParseId(id, out var station, out var start) || station != settings.Station)
            throw new AnalysisQueryException(404, $"There is no show '{id}'.");
        var dir = Paths.ShowDir(id, start);
        var analysis = AnalysisJson.TryRead<ShowAnalysis>(Path.Combine(dir, AnalysisPaths.ShowAnalysisFile), out _)
            ?? throw new AnalysisQueryException(404, $"The show {id} has not been analysed yet.");
        return (analysis, AnalysisJson.TryRead<ShowEnrichment>(Path.Combine(dir, AnalysisPaths.EnrichmentFile), out _));
    }

    public static IResult Respond(Func<object> make)
    {
        try
        {
            return Results.Json(make());
        }
        catch (AnalysisQueryException ex)
        {
            return Results.Json(new { error = ex.Message }, statusCode: ex.StatusCode);
        }
    }

    static DateTimeOffset Utc(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !value.EndsWith('Z')
            || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var t))
            throw new AnalysisQueryException(400, $"{name} must be a UTC time ending in Z, such as 2026-10-15T19:00:00Z.");
        return t;
    }
}
```

`src/WebAudioArchive.Web/Analysis/AnalysisMarkers.cs`:
```csharp
using System.Globalization;
using PanoramicData.Blazor.Models;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Web.Analysis;

/// <summary>
/// Analysis results as PDMediaPlayer markers and segments for a clip (wired into the pages in plan 4). Positions come
/// from UTC through the clip's clock, so they share the clip's accuracy (about a second). Every label says detected.
/// </summary>
public static class AnalysisMarkers
{
    public const string Talk = "talk";

    public static readonly IReadOnlyDictionary<string, string> KindLabels = new Dictionary<string, string>
    {
        [EventTypes.Swear] = "Swearing (detected)",
        [EventTypes.Clipping] = "Clipping (detected)",
        [EventTypes.Filler] = "Filler word (estimate)",
        [EventTypes.DeadAir] = "Dead air (detected)",
        [EventTypes.Song] = "Song",
        [Talk] = "Talk (transcript)",
    };

    public static readonly IReadOnlyDictionary<string, string> KindColors = new Dictionary<string, string>
    {
        [EventTypes.Swear] = "var(--bs-danger)",
        [EventTypes.Clipping] = "var(--bs-warning)",
        [EventTypes.Filler] = "var(--bs-secondary)",
        [EventTypes.DeadAir] = "var(--bs-dark)",
        [EventTypes.Song] = "var(--bs-primary)",
        [Talk] = "var(--bs-info)",
    };

    public static IReadOnlyList<PDMediaMarker> Markers(IEnumerable<AnalysisEvent> events, ClipClock clock) => events
        .Select(e => new PDMediaMarker
        {
            Id = "event-" + e.Id,
            Position = clock.ToClip(e.StartUtc),
            Kind = e.Type,
            Label = $"{e.Label ?? e.Type} at {ClipMarkers.Time(e.StartUtc)}",
            Detail = string.Create(CultureInfo.InvariantCulture, $"{e.Severity}, confidence {e.Confidence:0.00}, {e.Source} ({e.Detector} {e.DetectorVersion}). Detected, not proven: listen to confirm."),
        })
        .ToList();

    public static IReadOnlyList<PDMediaSegment> Segments(IEnumerable<AnalysisEvent> events, IEnumerable<TimedLine> lines, ClipClock clock)
    {
        var list = events
            .Where(e => e.Type == EventTypes.DeadAir)
            .Select(e => new PDMediaSegment
            {
                Id = "dead-" + e.Id,
                Start = clock.ToClip(e.StartUtc),
                End = clock.ToClip(e.EndUtc),
                Kind = EventTypes.DeadAir,
                Label = e.Label ?? "Dead air",
                Detail = $"{ClipMarkers.Time(e.StartUtc)} to {ClipMarkers.Time(e.EndUtc)}",
            })
            .ToList();
        var i = 0;
        foreach (var l in lines)
        {
            list.Add(new PDMediaSegment
            {
                Id = "talk-" + i++,
                Start = clock.ToClip(l.StartUtc),
                End = clock.ToClip(l.EndUtc),
                Kind = Talk,
                Label = l.Text.Length <= 80 ? l.Text : l.Text[..80] + "...",
                Detail = $"{ClipMarkers.Time(l.StartUtc)}: {l.Text}",
            });
        }

        return list;
    }
}
```

In `src/WebAudioArchive.Web/Program.cs`:
- add `using WebAudioArchive.Web.Analysis;` to the usings at the top;
- after `builder.Services.AddSingleton<RecordingsQuery>();` add `builder.Services.AddSingleton<AnalysisReader>();`;
- before `app.MapRazorComponents<App>().AddInteractiveServerRenderMode();` add:
```csharp
// Analysis results (plan 3), read-only, all times UTC. The player pages use them in plan 4.
app.MapGet("/api/analysis/events", (HttpRequest req, AnalysisReader a) => AnalysisReader.Respond(() =>
{
    var (from, to) = AnalysisReader.Range(req.Query["from"], req.Query["to"], AnalysisReader.MaxEventsRange);
    var events = a.Events(from, to, req.Query["types"]);
    return new { from, to, count = events.Count, events };
}));
app.MapGet("/api/analysis/transcript", (HttpRequest req, AnalysisReader a) => AnalysisReader.Respond(() =>
{
    var (from, to) = AnalysisReader.Range(req.Query["from"], req.Query["to"], AnalysisReader.MaxTranscriptRange);
    var lines = a.Transcript(from, to);
    return new { from, to, count = lines.Count, lines };
}));
app.MapGet("/api/analysis/shows/{id}", (string id, AnalysisReader a) => AnalysisReader.Respond(() =>
{
    var (analysis, enrichment) = a.Show(id);
    return new { analysis, enrichment };
}));
```

In `docker/docker-compose.yml`, in the `web` service's `environment`, add (the web container already mounts the whole data folder read-only at `/data`):
```yaml
      # Analysis output (plan 3), read-only through the /data mount.
      WAA_ANALYSIS: /data/analysis
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `dotnet test --filter FullyQualifiedName~AnalysisWebTests`
Expected: PASS, 10 tests. Then `dotnet test`: all pass, including the existing web tests.

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Core/Analysis/AnalysisStore.cs src/WebAudioArchive.Web/Analysis src/WebAudioArchive.Web/WebSettings.cs src/WebAudioArchive.Web/Program.cs docker/docker-compose.yml tests/WebAudioArchive.Tests/AnalysisWebTests.cs
git commit -m "Web: read-only analysis API (events, transcript, shows) and PDMediaPlayer marker and segment mapping (#9, #82)"
```

---

### Task 18: Validation on a labelled synthetic sample, scoring real labels, thresholds in docs

**Files:**
- Create: `src/WebAudioArchive.Analysis/Validation/DetectorScore.cs`
- Modify: `src/WebAudioArchive.Analysis.Service/Program.cs` (add `--validate`)
- Create: `tests/WebAudioArchive.Tests/Support/ValidationCorpus.cs`
- Create: `docs/analysis-validation.md`
- Test: `tests/WebAudioArchive.Tests/DetectorValidationTests.cs`

**Interfaces:**
- Consumes: detectors (Tasks 5 to 8), `DetectorRunner` (Task 12), `SegmentDecoder` (Task 9), `AnalysisStore.DayEvents` (Task 17), `TestMp3`, `Signals`, `TranscriptFixtures`.
- Produces:
  - `sealed record Labelled(string Type, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Note)`; `sealed record Score(string Type, int TruePositives, int FalsePositives, int FalseNegatives)` with `double? Precision`, `double? Recall`; `static class DetectorScore { IReadOnlyList<Score> Compare(IReadOnlyList<Labelled> labels, IReadOnlyList<AnalysisEvent> detected, TimeSpan tolerance); IReadOnlyList<Labelled> ReadCsv(string path); string Table(IReadOnlyList<Score> scores); }`
  - CLI: `--validate <labels.csv> <fromUtc> <toUtc>` prints the markdown table for the analysed events in that range.
  - Test helper `ValidationCorpus { static (float[] Mono, List<Labelled> Labels) Audio(int seed, DateTimeOffset start); static (TranscriptDocument Transcript, List<Labelled> Labels) Speech(DateTimeOffset start); }`

- [ ] **Step 1: Write the corpus helper**

`tests/WebAudioArchive.Tests/Support/ValidationCorpus.cs`:
```csharp
using WebAudioArchive.Analysis.Validation;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// A labelled synthetic sample (issue #84): no broadcast audio. Audio: 180 s of speech-like signal with planted
/// clipping (hard-clipped tone bursts of 20 to 200 ms) and dead air (5.5 to 15 s of silence), plus near misses that
/// must not be detected: a loud clean tone peaking at 0.9, a 3.5 s pause and 10 s of quiet room tone at -45 dBFS. Speech: lines
/// with every swear and filler spelling the detectors claim to handle and the near misses they must ignore.
/// </summary>
public static class ValidationCorpus
{
    public static (float[] Mono, List<Labelled> Labels) Audio(int seed, DateTimeOffset start)
    {
        var rng = new Random(seed);
        var parts = new List<float[]>();
        var labels = new List<Labelled>();
        var at = 0.0;
        void Add(float[] p) { parts.Add(p); at += p.Length / (double)Signals.Rate; }
        void Label(string type, double from, double to) => labels.Add(new Labelled(type, start.AddSeconds(from), start.AddSeconds(to), null));

        Add(Signals.SpeechLike(10, seed));
        for (var i = 0; i < 6; i++)
        {
            var burst = 0.02 + rng.NextDouble() * 0.18;
            var loud = Signals.Sine(burst, 440 + rng.Next(0, 400), 1.4).Select(x => Math.Clamp(x, -1f, 1f)).ToArray();
            Label(EventTypes.Clipping, at, at + burst);
            Add(loud);
            Add(Signals.SpeechLike(6 + rng.NextDouble() * 4, seed + i + 1));
            if (i % 2 == 0)
            {
                var silence = 5.5 + rng.NextDouble() * 9.5;
                Label(EventTypes.DeadAir, at, at + silence);
                Add(Signals.Silence(silence));
                Add(Signals.Sine(1, 330, 0.3));
            }
        }

        Add(Signals.Sine(3, 523, 0.9));
        Add(Signals.SpeechLike(4, seed + 20));
        Add(Signals.Sine(0.5, 330, 0.3));
        Add(Signals.Silence(3.5));
        Add(Signals.Sine(1, 330, 0.3));
        Add(Signals.Noise(10, -45, seed + 30));
        Add(Signals.Sine(1, 330, 0.3));
        var total = (int)(180 * Signals.Rate);
        var mono = Signals.Concat([.. parts]);
        if (mono.Length < total) mono = Signals.Concat(mono, Signals.SpeechLike((total - mono.Length) / (double)Signals.Rate, seed + 40));
        return (mono, labels);
    }

    public static (TranscriptDocument Transcript, List<Labelled> Labels) Speech(DateTimeOffset start)
    {
        // Each line: the text, the expected detections (type and word index), and whether the line is suppressed.
        (string Text, (string Type, int Word)[] Expected, string? Suppressed)[] lines =
        [
            ("Well that was shit, honestly", [(EventTypes.Swear, 3)], null),
            ("FUCKED it up again, um, sorry", [(EventTypes.Swear, 0), (EventTypes.Filler, 4)], null),
            ("what a bull-shit decision", [(EventTypes.Swear, 2)], null),
            ("f***ing hell, er, right", [(EventTypes.Swear, 0), (EventTypes.Filler, 2)], null),
            ("Scunthorpe beat Dickens Athletic in the cocktail cup", [], null),
            ("I like it, I like it a lot", [], null),
            ("Ummm, erm, hmm, ahh", [(EventTypes.Filler, 0), (EventTypes.Filler, 1), (EventTypes.Filler, 2), (EventTypes.Filler, 3)], null),
            ("we assess the class of shitake knobs", [], null),
            ("fuck um fuck um fuck um", [], "repetition"),
            ("you bloody legend, uhh", [(EventTypes.Swear, 1), (EventTypes.Filler, 3)], null),
            ("the summer era umbrella error", [], null),
            ("Motherfuckers! said the lyric", [(EventTypes.Swear, 0)], null),
        ];

        var transcript = TranscriptFixtures.OfSegments(start, lines.Select(l => (l.Text, l.Suppressed)).ToArray());
        var labels = new List<Labelled>();
        for (var i = 0; i < lines.Length; i++)
        {
            foreach (var (type, word) in lines[i].Expected)
            {
                var w = transcript.Segments[i].Words[word];
                labels.Add(new Labelled(type, start.AddSeconds(w.Start), start.AddSeconds(w.End), w.Text));
            }
        }

        return (transcript, labels);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/DetectorValidationTests.cs`:
```csharp
using Xunit.Abstractions;
using WebAudioArchive.Analysis.Audio;
using WebAudioArchive.Analysis.Decode;
using WebAudioArchive.Analysis.Detectors;
using WebAudioArchive.Analysis.Settings;
using WebAudioArchive.Analysis.Validation;
using WebAudioArchive.Core.Analysis;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>
/// Issue #84 on synthetic data. The tests print the score tables; docs/analysis-validation.md records them. Real Burst
/// audio is scored by the owner with --validate (labels never committed).
/// </summary>
public sealed class DetectorValidationTests(ITestOutputHelper output) : IDisposable
{
    static readonly DateTimeOffset T = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    static readonly LoadedConfig Config = AnalysisConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "analysis", "analysis.json"));
    static readonly Watershed Uk = Watershed.From(new SwearOptions(), TimeZoneInfo.FindSystemTimeZoneById("Europe/London"));
    readonly string _dir = Directory.CreateTempSubdirectory("waa-valid-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    static TranscriptDocument Empty(double seconds) => TranscriptFixtures.Of(T) with { AudioSeconds = seconds };

    IReadOnlyList<AnalysisEvent> AudioEvents(AudioFeatures f, LoadedConfig config) =>
        DetectorRunner.Run(Empty(f.Seconds), f, config, Uk, T).Events;

    [Fact]
    public void Scoring_matches_each_label_once_within_the_tolerance()
    {
        Labelled[] labels = [new("swear", T, T.AddSeconds(1), null), new("swear", T.AddSeconds(10), T.AddSeconds(11), null)];
        AnalysisEvent[] detected =
        [
            Event("swear", T.AddSeconds(0.2)), Event("swear", T.AddSeconds(0.3)), Event("swear", T.AddSeconds(30)), Event("filler", T.AddSeconds(10)),
        ];

        var swear = DetectorScore.Compare(labels, detected, TimeSpan.FromSeconds(0.5)).Single(s => s.Type == "swear");

        Assert.Equal((1, 2, 1), (swear.TruePositives, swear.FalsePositives, swear.FalseNegatives));
        Assert.Equal(1.0 / 3, swear.Precision!.Value, 6);
        Assert.Equal(0.5, swear.Recall!.Value, 6);
        Assert.Contains("| swear | 1 | 2 | 1 | 0.33 | 0.50 |", DetectorScore.Table([swear]));
    }

    [Fact]
    public void Labels_csv_is_read_in_utc()
    {
        var path = Path.Combine(_dir, "labels.csv");
        File.WriteAllText(path, "type,startUtc,endUtc,note\nfiller,2026-10-16T09:12:03.4Z,2026-10-16T09:12:03.8Z,um\nswear,2026-10-16T09:20:00Z,2026-10-16T09:20:01Z,\n");

        var labels = DetectorScore.ReadCsv(path);

        Assert.Equal(2, labels.Count);
        Assert.Equal(new DateTimeOffset(2026, 10, 16, 9, 12, 3, 400, TimeSpan.Zero), labels[0].StartUtc);
        Assert.Equal("um", labels[0].Note);
        Assert.Null(labels[1].Note);
        File.WriteAllText(path, "type,startUtc,endUtc,note\nfiller,09:12,09:13,um\n");
        Assert.Throws<FormatException>(() => DetectorScore.ReadCsv(path));
    }

    [Fact]
    public void Transcript_detectors_score_perfectly_on_the_labelled_lines()
    {
        var (transcript, labels) = ValidationCorpus.Speech(T);
        var events = DetectorRunner.Run(transcript, new FeatureExtractor(44100, 1).Complete(), Config, Uk, T).Events;

        var scores = DetectorScore.Compare(labels, events, TimeSpan.FromSeconds(0.1));
        output.WriteLine(DetectorScore.Table(scores));

        foreach (var s in scores.Where(s => s.Type is EventTypes.Swear or EventTypes.Filler))
        {
            Assert.Equal(1.0, s.Precision);
            Assert.Equal(1.0, s.Recall);
        }
    }

    [Fact]
    public void Audio_detectors_score_perfectly_on_the_planted_signal_without_a_codec()
    {
        var (mono, labels) = ValidationCorpus.Audio(seed: 11, T);
        var fx = new FeatureExtractor(Signals.Rate, 1);
        fx.Push(mono);

        var scores = DetectorScore.Compare(labels, AudioEvents(fx.Complete(), Config), TimeSpan.FromSeconds(0.25));
        output.WriteLine("PCM, configured thresholds\n" + DetectorScore.Table(scores));

        foreach (var s in scores)
        {
            Assert.Equal(1.0, s.Precision);
            Assert.Equal(1.0, s.Recall);
        }
    }

    [Fact]
    public async Task Audio_detectors_after_the_mp3_codec_are_measured()
    {
        var (mono, labels) = ValidationCorpus.Audio(seed: 11, T);
        var mp3 = Path.Combine(_dir, "corpus.mp3");
        await TestMp3.EncodeMp3Async(mp3, Signals.Stereo(mono), 2);
        var scratch = new ScratchSpace(Path.Combine(_dir, "scratch"), 512L << 20);
        using var lease = scratch.Acquire("validation", SegmentDecoder.ScratchBytesFor(new FileInfo(mp3).Length, 180));
        var features = (await new SegmentDecoder().DecodeAsync(mp3, new FileInfo(mp3).Length, 44100, 2, lease, CancellationToken.None)).Features;

        var configured = DetectorScore.Compare(labels, AudioEvents(features, Config), TimeSpan.FromSeconds(0.25));
        var lenient = Config with { Config = Config.Config with { Clipping = Config.Config.Clipping with { Threshold = 0.98, MinRunSamples = 2 } } };
        var atLowerLevel = DetectorScore.Compare(labels, AudioEvents(features, lenient), TimeSpan.FromSeconds(0.25));
        output.WriteLine("MP3 128 kbps, configured thresholds (clipping 0.99, 3 samples)\n" + DetectorScore.Table(configured));
        output.WriteLine("MP3 128 kbps, clipping 0.98, 2 samples\n" + DetectorScore.Table(atLowerLevel));

        var dead = configured.Single(s => s.Type == EventTypes.DeadAir);
        Assert.Equal(1.0, dead.Precision);
        Assert.Equal(1.0, dead.Recall);
        var clip = atLowerLevel.Single(s => s.Type == EventTypes.Clipping);
        Assert.Equal(1.0, clip.Precision);
        Assert.True(clip.Recall >= 0.9, $"clipping recall after MP3 at 0.98 was {clip.Recall:0.00}");
    }

    static AnalysisEvent Event(string type, DateTimeOffset at) => new(
        AnalysisEvent.MakeId(type, "a/x/y/z", (at - T).TotalSeconds), type, at, at.AddSeconds(0.4), Severities.Low, 0.9, type, "1",
        EventSources.Transcript, "a", "a/x/y/z", 0, 0.4, null, null);
}
```

- [ ] **Step 3: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~DetectorValidationTests`
Expected: build FAILS with `CS0246` for `Labelled`, `DetectorScore`.

- [ ] **Step 4: Implement the scoring**

`src/WebAudioArchive.Analysis/Validation/DetectorScore.cs`:
```csharp
using System.Globalization;
using System.Text;
using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Analysis.Validation;

/// <summary>A hand label: something a person heard (type as in events.json), UTC start and end, and an optional note.</summary>
public sealed record Labelled(string Type, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string? Note);

public sealed record Score(string Type, int TruePositives, int FalsePositives, int FalseNegatives)
{
    public double? Precision => TruePositives + FalsePositives == 0 ? null : (double)TruePositives / (TruePositives + FalsePositives);

    public double? Recall => TruePositives + FalseNegatives == 0 ? null : (double)TruePositives / (TruePositives + FalseNegatives);
}

/// <summary>
/// Precision and recall of detections against labels, per type. A detection matches a label of the same type when
/// their spans overlap once both are widened by the tolerance; each label and each detection is used at most once.
/// </summary>
public static class DetectorScore
{
    public static IReadOnlyList<Score> Compare(IReadOnlyList<Labelled> labels, IReadOnlyList<AnalysisEvent> detected, TimeSpan tolerance)
    {
        var types = labels.Select(l => l.Type).Concat(detected.Select(d => d.Type)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var scores = new List<Score>();
        foreach (var type in types)
        {
            var open = detected.Where(d => d.Type == type).OrderBy(d => d.StartUtc).ToList();
            var tp = 0;
            var fn = 0;
            foreach (var label in labels.Where(l => l.Type == type).OrderBy(l => l.StartUtc))
            {
                var match = open.FirstOrDefault(d => d.StartUtc - tolerance < label.EndUtc + tolerance && label.StartUtc - tolerance < d.EndUtc + tolerance);
                if (match is null)
                {
                    fn++;
                    continue;
                }

                open.Remove(match);
                tp++;
            }

            scores.Add(new Score(type, tp, open.Count, fn));
        }

        return scores;
    }

    /// <summary>CSV with a header: type,startUtc,endUtc,note. Times must be UTC ending in Z.</summary>
    public static IReadOnlyList<Labelled> ReadCsv(string path)
    {
        var list = new List<Labelled>();
        foreach (var (line, n) in File.ReadAllLines(path).Select((l, i) => (l, i + 1)).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var cells = line.Split(',', 4);
            if (cells.Length < 3) throw new FormatException($"{path} line {n}: expected type,startUtc,endUtc,note");
            list.Add(new Labelled(cells[0].Trim(), Utc(cells[1], path, n), Utc(cells[2], path, n), cells.Length > 3 && cells[3].Trim().Length > 0 ? cells[3].Trim() : null));
        }

        return list;
    }

    public static string Table(IReadOnlyList<Score> scores)
    {
        var sb = new StringBuilder("| Type | True positives | False positives | False negatives | Precision | Recall |\n|---|---|---|---|---|---|\n");
        foreach (var s in scores)
            sb.Append(CultureInfo.InvariantCulture, $"| {s.Type} | {s.TruePositives} | {s.FalsePositives} | {s.FalseNegatives} | {F(s.Precision)} | {F(s.Recall)} |\n");
        return sb.ToString();
    }

    static string F(double? v) => v is null ? "n/a" : v.Value.ToString("0.00", CultureInfo.InvariantCulture);

    static DateTimeOffset Utc(string text, string path, int line)
    {
        var t = text.Trim();
        if (!t.EndsWith('Z') || !DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var value))
            throw new FormatException($"{path} line {line}: '{t}' is not a UTC time ending in Z");
        return value;
    }
}
```

In `src/WebAudioArchive.Analysis.Service/Program.cs`, add `using WebAudioArchive.Analysis.Validation;` and `using WebAudioArchive.Core.Analysis;` to the usings, and directly before the line `if (args.Contains("--rebuild"))` add:
```csharp
var validate = Array.IndexOf(args, "--validate");
if (validate >= 0)
{
    if (validate + 3 >= args.Length)
    {
        Console.Error.WriteLine("usage: --validate <labels.csv> <fromUtc> <toUtc>");
        return 2;
    }

    var from = DateTimeOffset.Parse(args[validate + 2], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
    var to = DateTimeOffset.Parse(args[validate + 3], CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal);
    var labels = DetectorScore.ReadCsv(args[validate + 1]);
    var detected = AnalysisStore.DayEvents(new AnalysisPaths(settings.OutputDir, settings.Station), from, to);
    Console.WriteLine($"{labels.Count} labels, {detected.Count} detections, {from:u} to {to:u}");
    Console.WriteLine("Audio events (0.25 s tolerance)");
    Console.WriteLine(DetectorScore.Table(DetectorScore.Compare(labels.Where(l => l.Type is EventTypes.Clipping or EventTypes.DeadAir).ToList(),
        detected.Where(d => d.Type is EventTypes.Clipping or EventTypes.DeadAir).ToList(), TimeSpan.FromSeconds(0.25))));
    Console.WriteLine("Transcript events (1 s tolerance, word times are approximate)");
    Console.WriteLine(DetectorScore.Table(DetectorScore.Compare(labels.Where(l => l.Type is EventTypes.Swear or EventTypes.Filler).ToList(),
        detected.Where(d => d.Type is EventTypes.Swear or EventTypes.Filler).ToList(), TimeSpan.FromSeconds(1))));
    return 0;
}
```

- [ ] **Step 5: Run the tests, then record the results**

Run: `dotnet test --filter FullyQualifiedName~DetectorValidationTests --logger "console;verbosity=detailed"`
Expected: PASS, 5 tests; the output shows the score tables (the detailed logger prints `ITestOutputHelper` lines). Copy the two MP3 tables into the document below. If `clip.Recall` is under 0.9 the codec smears the planted bursts more than expected: report the measured figure in the commit message and the document, and lower nothing without the owner's agreement.

`docs/analysis-validation.md`:
```markdown
# Detector validation (issue #84)

All figures are for detections, not proven events. Thresholds are provisional until the real-sample section below is
filled in from hand labels of real Burst audio. Labels and audio are never committed (the repository is public).

## Thresholds in use (config/analysis/analysis.json, version 2026-10-16.1)

| Detector | Rule | Values |
|---|---|---|
| Clipping | a run of at least N consecutive samples at or above the threshold (either sign); runs closer than the merge window are one event; medium severity from the given length | threshold 0.99 of full scale, N = 3, merge 250 ms, medium from 100 ms |
| Dead air | every 50 ms window (loudest channel) below the level for at least the minimum length | below -50 dBFS for 5 s; high severity from 30 s |
| Swearing | whole words and listed suffixes from config/analysis/swearwords.json, masked forms at certainty 0.5 or 0.3, allow list for near misses; severity from the tier, one step higher before the watershed | tiers mild, strong, severe; watershed 21:00 to 05:30 Europe/London |
| Fillers | um, uh, er, erm, ah, hmm (drawn-out spellings folded), never "like", "you know" off | rate per minute of transcribed speech, an estimate |
| Hallucination filter | suppress no-speech, annotations, known phrases, 3 or more repeats | no_speech_prob above 0.6 with avg_logprob below -1.0 |

## Synthetic sample (CI, tests/WebAudioArchive.Tests/DetectorValidationTests.cs)

180 s of speech-like signal with 6 planted hard-clipped bursts (20 to 200 ms) and 3 dead-air gaps (5.5 to 15 s), plus
near misses (a loud clean tone peaking at 0.9, a 3.5 s pause, 10 s of room tone at -45 dBFS); 12 transcript lines covering the
swear and filler spellings and near misses the detectors claim to handle.

| Case | Clipping P / R | Dead air P / R | Swearing P / R | Fillers P / R |
|---|---|---|---|---|
| PCM, configured thresholds | 1.00 / 1.00 | 1.00 / 1.00 | n/a | n/a |
| Transcript lines | n/a | n/a | 1.00 / 1.00 | 1.00 / 1.00 |
| MP3 128 kbps, configured thresholds | from the test output | from the test output | n/a | n/a |
| MP3 128 kbps, clipping 0.98 and 2 samples | from the test output | n/a | n/a | n/a |

Fill the two MP3 rows from the tables `Audio_detectors_after_the_mp3_codec_are_measured` prints, in the same commit.
What this shows: the rules do what they say, and how much the MP3 codec (the archive is stored as broadcast) blurs
clipping. It does not show how well whisper hears fillers and swearing on real speech: that needs the real sample.

## Real sample (owner, Task 19)

Method: pick at least 30 minutes of recorded Burst speech across several presenters and both sides of 21:00. Listen
with the web Player and write every filler, swear, clip and dead air you hear into
data/analysis/validation/labels-<date>.csv (columns type,startUtc,endUtc,note; UTC times ending in Z; types filler,
swear, clipping, dead-air). Then run, for the same range:

    docker compose -f docker/docker-compose.yml exec analysis dotnet WebAudioArchive.Analysis.Service.dll --validate /analysis/validation/labels-<date>.csv <fromUtc> <toUtc>

and paste the two tables here with the date, the range, the presenters' show names and the model in use.

| Date | Range (UTC) | Model | Clipping P / R | Dead air P / R | Swearing P / R | Fillers P / R |
|---|---|---|---|---|---|---|
```

- [ ] **Step 6: Commit**

```powershell
git add src/WebAudioArchive.Analysis/Validation src/WebAudioArchive.Analysis.Service/Program.cs tests/WebAudioArchive.Tests/Support/ValidationCorpus.cs tests/WebAudioArchive.Tests/DetectorValidationTests.cs docs/analysis-validation.md
git commit -m "Detector validation: labelled synthetic sample in CI, --validate for real labels, thresholds recorded (#84)"
```

---

### Task 19: Deploy on the GPU, measure, document, push and close the issues

**Files:**
- Create: `docs/analysis.md`
- Modify: `docs/operations.md`, `README.md`, `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md`, `docs/analysis-validation.md`

- [ ] **Step 1: Start analysis on the GPU without touching the recorders**

Run:
```powershell
docker compose -f docker/docker-compose.yml ps --format "table {{.Name}}\t{{.Status}}"
docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml up -d --build analysis
docker compose -f docker/docker-compose.yml ps --format "table {{.Name}}\t{{.Status}}"
docker exec waa-analysis nvidia-smi --query-gpu=name,driver_version,memory.used --format=csv
curl.exe -s http://127.0.0.1:8095/healthz
```
Expected: the first and second `ps` show `waa-recorder-a`, `waa-recorder-b` and `waa-web` with the same "Up ..." durations (not restarted); `waa-analysis` is new. `nvidia-smi` inside the container names `NVIDIA GeForce RTX 3060` (the toolkit injects it with the driver). `/healthz` returns JSON with `"whisperReady":true` within a few minutes of the first pass (the first cuda build downloads about 1.3 GB of CUDA libraries; the model loads in a few seconds). If `nvidia-smi` fails, rerun the D4 check (`docker run --rm --gpus all nvidia/cuda:12.4.1-base-ubuntu22.04 nvidia-smi`); if whisper stays not ready, `docker logs waa-analysis` shows `LastError` from the transcriber (model path, or the CUDA library that did not load).

- [ ] **Step 2: Measure the realtime factor**

Run (any closed hour from recorder A):
```powershell
$seg = (Get-ChildItem data/a/burst/master -Recurse -Filter *.mp3 | Sort-Object LastWriteTime | Select-Object -Last 2 | Select-Object -First 1)
$rel = $seg.FullName.Substring((Resolve-Path data/a).Path.Length).Replace('\', '/')
docker compose -f docker/docker-compose.yml exec analysis dotnet WebAudioArchive.Analysis.Service.dll --benchmark "/data/a$rel" 600
nvidia-smi --query-gpu=memory.used,utilization.gpu --format=csv
```
Expected: a line `benchmark: whisper.net 1.9.1 (cuda) ggml-large-v3-turbo-q8_0: 600.0 s of audio in 2 chunks, decode ... s, transcribe ... s, realtime factor 0.0xx (...)`. The engine must say `(cuda)`; `(cpu)` means the CUDA runtime did not load (see Step 1). Record the figure. Pass: realtime factor at most 0.2 (5 times faster than real time, so a day of audio takes under 5 hours); the D18 spike measured 0.035 (28x) for speech in a container. If it is above 0.2, check that the host CPU is not saturated (whisper.cpp's threads starve under full load, D18) and try `WAA_WHISPER_THREADS` 2 or 6; record each figure. Run the benchmark a second time while Ollama is enriching (watch `docker logs -f waa-analysis` for "Enriched") and record that figure too: it shows the cost of sharing the GPU.

- [ ] **Step 3: Watch it catch up and check the recorders are unaffected**

Run, about every 30 minutes for 2 hours:
```powershell
curl.exe -s http://127.0.0.1:8095/healthz
docker stats --no-stream --format "table {{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}"
curl.exe -s http://127.0.0.1:8080/healthz
curl.exe -s http://127.0.0.1:8081/healthz
```
Expected: `backlogSegments` falls and `lagSeconds` falls below 3600 once the backlog is cleared (newest first, so today's hours are done first); `realtimeFactor.average` close to Step 2; `waa-analysis` under its 6 CPU and 6 GB limits. Both recorders stay healthy: `secondsSinceLastFrameWritten` under 2, no new `lastError`, and the sidecars of hours closed during the test show no new gaps:
```powershell
Get-ChildItem data/a/burst/master, data/b/burst/master -Recurse -Filter *.json | Where-Object LastWriteTime -gt (Get-Date).AddHours(-2) | ForEach-Object { "{0} gaps={1}" -f $_.Name, ((Get-Content $_.FullName -Raw | ConvertFrom-Json).gaps.Count) }
```
Expected: `gaps=0` for every line (or the same gaps as before analysis started, if the stream itself dropped). Then look at the output: `Get-ChildItem data/analysis/burst -Recurse -Filter *.json | Select-Object -First 20 FullName`, open one `transcript.json`, one `events.json` and one `show-analysis.json` and check the times are UTC and plausible against the audio in the web Player (open an event's `link`).

- [ ] **Step 4: Prove disk pressure and Ollama outages are handled**

Run:
```powershell
$env:WAA_MIN_FREE_GB = "100000"; docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml up -d analysis
Start-Sleep -Seconds 90; curl.exe -s http://127.0.0.1:8095/healthz
Remove-Item Env:WAA_MIN_FREE_GB
$env:WAA_OLLAMA_URL = "http://host.docker.internal:1"; docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml up -d analysis
Start-Sleep -Seconds 120; Get-ChildItem data/analysis/burst/shows -Recurse -Filter enrichment.json | Sort-Object LastWriteTime | Select-Object -Last 1 | Get-Content
Remove-Item Env:WAA_OLLAMA_URL
docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml up -d analysis
```
Expected: with the huge threshold `/healthz` says `"status":"paused"` and `"paused":"free disk below ..."` and no job runs; with Ollama unreachable a newly finished show's `enrichment.json` is `"status":"pending"` with `nextAttemptUtc`; after the last command both recover (the pending show becomes `done` on a later pass). The recorders were never restarted (check `ps` as in Step 1).

- [ ] **Step 5: Record the measurements and the owner's real-sample labels**

In `docs/analysis-validation.md` add the realtime factors from Step 2 (alone and while enriching, with the date, model, flags and GPU) under a new heading "Throughput (RTX 3060)", and, when the owner has labelled a real sample (owner action 6), run the `--validate` command shown there and paste the tables into "Real sample". If the labels are not ready yet, leave #84 open and say so in its issue.

- [ ] **Step 6: Write the analysis guide**

`docs/analysis.md`:
````markdown
# Analysis (plan 3)

The analysis service turns the archive into transcripts, detected events and per-show facts. It is a separate
container (`waa-analysis`) with its own image, below the recorders' priority, that runs whisper.cpp in process on the
GPU (Whisper.net). It never writes to the recordings: it reads closed segments read-only, copies each one into a 2 GiB tmpfs
scratch, decodes the copy, and writes only under `data/analysis`. All times are UTC.

## Run

First time, or after a change to analysis (always name the service; never touch the recorders):

    docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml up -d --build analysis

Without an NVIDIA GPU leave out the second `-f` file: the image is then the cpu target and whisper runs on the CPU with
`base.en`, which is far slower and needs a host that is not otherwise busy (a day of audio can take longer than a day;
the lag in `/healthz` shows it). Nothing in the image needs ffmpeg or a compiler: decoding is managed code (NLayer,
NAudio) and whisper.cpp comes prebuilt in the Whisper.net runtime packages.

Models are not in git. Download them into `models/` once (sizes and hashes in plan 3, Task 16):
`ggml-large-v3-turbo-q8_0.bin` (GPU) and `ggml-base.en.bin` (CPU).

Health: `http://127.0.0.1:8095/healthz` (503 when the lag passes `WAA_LAG_ALARM_HOURS`, default 6, or no pass has
finished for 15 minutes; `docker ps` then shows the container unhealthy; it is not restarted).

## What it does, in order

1. Every 60 s: if free disk is below `WAA_MIN_FREE_GB` (default 20) it stops (and stops a running job between chunks),
   so the recorders keep the space.
2. Finds closed segments (a `.json` sidecar beside its `.mp3`, sizes agreeing). Recorder A is analysed; recorder B only
   where A covers less than 98% of the span (`WAA_ANALYSIS_PREFERENCE`). Newest first.
3. Per segment (a job, idempotent and resumable through `state.json`): copy into scratch, decode once in managed code to
   16 kHz mono WAV (for whisper) and full-rate float PCM (streamed into the level and clipping features), split into about
   5 minute chunks at quiet moments, transcribe each chunk (progress saved after each), assemble `transcript.json`, run
   the detectors into `events.json`. A job runs again only when the segment, the transcriber or its options change;
   detector changes rebuild events from the stored transcript and features without whisper or audio.
4. Rebuilds the UTC day timeline (`events/<yyyy>/<MM-dd>.json`) and the `show-analysis.json` of each scheduled show
   (from the schedule the web app saved) whose window, the show plus 5 minutes either side, has ended.
5. When no segment job is ready, enriches up to 2 finished shows with the local LLM (`enrichment.json`).

## Files (under data/analysis/<station>/)

| Path | What |
|---|---|
| `segments/<recorder>/<yyyy>/<MM-dd>/<stem>/state.json` | job state: fingerprint, versions, status, attempts, chunks done |
| `.../features.json` | 50 ms window levels and clip runs at 0.98, 0.99, 0.999 (rebuild without decoding) |
| `.../transcript.json` | schema 1: segments with words, times in seconds from the segment's audio start, `timeline` anchors to map to UTC, `suppressed` reasons |
| `.../events.json` | schema 1, scope `segment:<job>`: events, coverage, stats, versions |
| `events/<yyyy>/<MM-dd>.json` | the merged UTC day: each event filed by its start, A preferred where it has audio |
| `shows/<yyyy>/<show id>/show-analysis.json` | counts for the show proper (swears by tier and before the watershed, clipping, dead air, fillers per minute of speech), sources, notes |
| `shows/<yyyy>/<show id>/enrichment.json` | hosts, guests, topics, songs, artists, locations, calls to action, language, summary; each claim with a verbatim quote and its UTC time; model, model digest and prompt version |

An event: `id` (stable), `type` (`swear`, `clipping`, `filler`, `dead-air`; `song` reserved), `startUtc`, `endUtc`,
`severity` (`info`, `low`, `medium`, `high`), `confidence` (0 to 1), `detector`, `detectorVersion`, `source` (`audio`,
`transcript`, `llm`), `recorder`, `jobId`, `audioOffset`, `audioSeconds`, `label`, `evidence` and `link` (the web
Player 5 s either side).

## Web API (read-only, no sign-in yet, loopback only like the rest of the web app)

- `GET /api/analysis/events?from=<UTC>&to=<UTC>[&types=swear,clipping]` (at most 7 days)
- `GET /api/analysis/transcript?from=<UTC>&to=<UTC>` (at most 6 hours)
- `GET /api/analysis/shows/<show id>` (for example `burst-20261015T1900Z`)

The player markers and segments are built from these in plan 4.

## Commands

    docker compose -f docker/docker-compose.yml exec analysis dotnet WebAudioArchive.Analysis.Service.dll --benchmark /data/a/burst/master/<yyyy>/<MM-dd>/<HHmmss>.mp3 600
    docker compose -f docker/docker-compose.yml exec analysis dotnet WebAudioArchive.Analysis.Service.dll --rebuild
    docker compose -f docker/docker-compose.yml exec analysis dotnet WebAudioArchive.Analysis.Service.dll --validate /analysis/validation/<labels>.csv <fromUtc> <toUtc>

## Settings (environment)

`WAA_STATION`, `WAA_RECORDERS` (required), `WAA_ANALYSIS_PREFERENCE`, `WAA_ANALYSIS_OUT`, `WAA_SCRATCH`,
`WAA_SCRATCH_MAX_MB` (2048), `WAA_MIN_FREE_GB` (20), `WAA_WHISPER_MODEL_PATH` (the ggml model file; its name is recorded
in transcripts), `WAA_WHISPER_DEVICE` (`auto`, `cpu` or `cuda`), `WAA_WHISPER_THREADS` (4), `WAA_WHISPER_DTW` (`off`;
turn on only with `chunkSeconds` 30, D18), `WAA_OLLAMA_URL`, `WAA_OLLAMA_MODEL` (`qwen3.6:latest`; cloud
models are refused), `WAA_OLLAMA_ALLOW_REMOTE`, `WAA_ENRICHMENT` (on or off), `WAA_ANALYSIS_CONFIG`,
`WAA_STATIONS_CONFIG`, `WAA_SCHEDULE_FILE`, `WAA_ANALYSIS_SINCE` (UTC), `WAA_LAG_ALARM_HOURS` (6), `WAA_PORT`. Thresholds, word lists and prompts are in `config/analysis/` (versioned; a change rebuilds events or
re-enriches shows as needed).

## Limits

- Detections are estimates: speech recognition mishears and softens words, drops many fillers, and transcribes song
  lyrics (a swear in a lyric is reported, with `speechOrLyrics: unknown`). Every event links to its audio to confirm.
- Clipping is measured on the decoded MP3, which blurs it; thresholds are provisional (docs/analysis-validation.md).
- Times are good to about a second; after a reconnect inside a segment, a few seconds (as in the web Player).
- Derived hosts never grant access to anything.
- Not built: speaker diarisation, song identification (#2, #3), the player UI for events (plan 4), search (plan 4).
````

- [ ] **Step 7: Update the operations guide, README and spec**

In `docs/operations.md` append:
```markdown

## Analysis (plan 3)

Container `waa-analysis` (whisper.cpp runs inside it), separate from the recorders and the web app. Start or upgrade only it:

    docker compose -f docker/docker-compose.yml -f docker/docker-compose.gpu.yml up -d --build analysis

They read `data/a` and `data/b` read-only and write only `data/analysis` (the web app reads it read-only through its
`/data` mount, `WAA_ANALYSIS=/data/analysis`). Analysis runs at nice 10 with low CPU shares, at most 6 CPUs and 6 GB,
and is the first thing the kernel kills under memory pressure; below `WAA_MIN_FREE_GB` of free disk it stops. Health:
`http://127.0.0.1:8095/healthz` (lag, backlog, realtime factor, enrichment). Details: `docs/analysis.md`; detector
figures: `docs/analysis-validation.md`.
```

In `README.md`, after the Web app section, add:
```markdown

## Analysis

A separate analysis container transcribes the recordings locally (whisper.cpp through Whisper.net on the GPU), detects swearing (with the
21:00 watershed), clipping, filler words and dead air, keeps a UTC events timeline, and derives hosts, guests, topics,
songs and artists for each show with a local LLM (Ollama), every claim backed by a quote. It reads the recordings
read-only and never touches a recorder. See `docs/analysis.md`.
```

In `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md`:
- in 3.6, replace the bullet beginning `- **Decode once:**` with:
  `- **Decode once (built in plan 3, issue #76):** each closed master segment is copied into a size-bounded scratch and the copy decoded: 16 kHz mono WAV for whisper and full-rate float PCM, streamed, for the clipping and dead-air features. The live stream is not decoded: analysis is a separate container and never in the recorder's path (3.13).`
- in 3.6, replace the bullet beginning `- **Transcriber:**` with:
  `- **Transcriber (plan 3, decisions D2 and D18):** whisper.cpp in the analysis process through Whisper.net 1.9.1 (CUDA runtime on the GPU, CPU fallback) with ggml-large-v3-turbo-q8_0, English, token timestamps (DTW off by default), about 5 minute chunks cut at quiet moments. Decoding is managed code (NLayer, NAudio); there is no ffmpeg. One transcript per master segment (recorder A, B only to fill A's holes), times as offsets into the segment's audio with a timeline to UTC; each show's transcript is a slice. The prompt is primed with fillers, the station glossary and the show title. Segments whisper invents over music or silence are kept but suppressed.`
- in 3.11, after the line `  events/<yyyy>/<MM-dd>.jsonl`, add `  (built in plan 3 as data/analysis/<station>/events/<yyyy>/<MM-dd>.json, with segments/ and shows/ beside it; see docs/analysis.md)`
- in section 8, add the bullet: `- Analysis (plan 3, 2026-10-16): whisper.cpp through Whisper.net on the GPU with large-v3-turbo q8_0, managed MP3 decoding, no ffmpeg (decision D18); qwen3.6:latest in Ollama for enrichment; recorder A analysed, B only for A's holes; watershed 21:00 to 05:30 local; analysis is its own container below the recorders' priority.`

- [ ] **Step 8: Check for em dashes, run everything, commit and push**

Run:
```powershell
Select-String -Path docs/analysis.md, docs/analysis-validation.md, docs/operations.md, README.md, config/analysis/*.json, config/analysis/prompts/* -Pattern ([string][char]0x2014) | Measure-Object | ForEach-Object Count
dotnet build -c Release
dotnet test -c Release --no-build
git add docs/analysis.md docs/analysis-validation.md docs/operations.md README.md docs/superpowers/specs/2026-10-15-webaudioarchive-design.md
git commit -m "Analysis: deployed on the RTX 3060, realtime factor and detector figures recorded, docs and spec updated (#77, #84, #85)"
git pull --rebase
git push
```
Expected: the em dash count is 0; build and all tests pass; the push succeeds and CI is green (test and docker, which builds both analysis targets).

- [ ] **Step 9: Close the issues**

Run (adjust the comments to the measured figures):
```powershell
gh issue close 76 -c "Done in plan 3 (Tasks 9 and 16): segments are copied into a 2 GiB tmpfs scratch and only the copy is decoded; scratch is swept after every job and at start-up."
gh issue close 78 -c "Done in plan 3 (Task 7): tiered list in config/analysis/swearwords.json, masks, allow list, watershed 21:00 to 05:30 Europe/London."
gh issue close 79 -c "Done in plan 3 (Task 5). Thresholds provisional; see docs/analysis-validation.md."
gh issue close 80 -c "Done in plan 3 (Task 8). Filler rate is labelled an estimate."
gh issue close 81 -c "Done in plan 3 (Task 6)."
gh issue close 82 -c "Done in plan 3 (Tasks 3, 12, 13): per-segment and per-day events.json, schema 1, rebuildable with --rebuild."
gh issue close 83 -c "Done in plan 3 (Task 14): qwen3.6:latest, evidence quotes checked against the transcript, pending and retried when Ollama is down."
gh issue close 77 -c "Done in plan 3: whisper.cpp in process through Whisper.net 1.9.1 (decision D18), large-v3-turbo q8_0, realtime factor <measured> on the RTX 3060 (docs/analysis-validation.md)."
gh issue close 85 -c "Done in plan 3: realtime factor <measured>; lag, backlog and realtime factor in /healthz; 503 above WAA_LAG_ALARM_HOURS."
```
Close #84 only when the real-sample figures are in `docs/analysis-validation.md`, and #9 when #76 to #85 are all closed: `gh issue close 9 -c "Plan 3 complete. The player UI for events and transcripts is plan 4 (#11)."`. In each `gh` command replace `<measured>` with the figure from Step 2 before running it.

---

## Self-review

**Spec coverage (plan 3 scope):**
- 3.5 disk guard for analysis: Task 11 (`DiskGuard`), Task 12 (stops between chunks), Task 15 (stops a pass), Task 19 (proved live). The recorder-side guard stays plan 5.
- 3.6 decode on a scratch copy (Task 9, managed decoder instead of the spec's ffmpeg, D18), transcriber with word timestamps on the stream timeline (Tasks 3, 10, 12), clipping (Task 5), swearing with tiers, inflections and the watershed (Task 7), fillers with the verbatim prompt and the "like" exclusion (Tasks 8, 10), events timeline (Tasks 3, 12, 13), dead air (Task 6).
- 3.7 enricher: fields, evidence quotes, drops invented claims, `derived` with model, prompt version and date, pending and retried (Task 14); derived hosts never grant access (stated in the records and docs).
- 3.8 web integration: read-only data API and marker mapping (Task 17); UI is plan 4.
- 3.9 songs: `song` event type reserved (Task 3); spoken songs and artists from the enricher (Task 14).
- 3.11 output layout: Task 3 (`AnalysisPaths`), documented in Task 19.
- 3.13 separation: own image and container, read-only mounts, no recorder change (Task 16, `DeploymentFilesTests`); readers use sidecar data and A/B preference, not file names (Tasks 3, 11).
- 5 deployment: compose, GPU opt-in override, non-root, health check, analysis lag in `/healthz` (Tasks 15, 16).
- 6 testing: synthetic clipped audio with exact times (Task 5), transcript fixtures with near misses (Tasks 7, 18), filler precision and recall on a labelled sample (Task 18, real sample by the owner), enricher with a hallucinated name dropped (Task 14), end to end with whisper and Ollama stubbed (Tasks 12, 15).

**Known gaps left deliberately:** DTW word times (off by default until whisper.cpp keeps DTW over long requests, D18); speaker diarisation; song fingerprinting (#2, #3); presenter access rules on the analysis API (plan 4, with sign-in); a VAD model; the LLM schedule fallback of 3.2 (plan 2); per-station margins from config (5 minutes is a constant until plan 2 adds `marginBefore`/`marginAfter`); retention of analysis output (plan 5 covers retention).

**Type consistency:** `TranscriptDocument`, `TranscriptSegment`, `TranscriptWord`, `TimelineAnchor`, `AnalysisEvent`, `EventsDocument`, `SourceSpan`, `TimedLine` (Task 3) are used unchanged in Tasks 7, 8, 12, 13, 14, 17 and 18. `RawSegment` (Task 10) feeds `TranscriptPartial` and `TranscriptAssembler` (Task 12). `SegmentJob`, `SourceSegment`, `JobState`, `JobRules`, `JobAction` (Task 11) are used by `SegmentPipeline` (Task 12) and `AnalysisRunner` (Task 15). `ShowAnalysis` (Task 13) is the input of `Enricher` (Task 14) and the output read by the web (Task 17). `LoadedConfig` positional order (Config, Swears, ConfigDir, DetectorVersion) is the same everywhere it is built with `with`.

**Review Focus check:** each of the eight items has its named test in the owning task (Tasks 5, 7, 8, 9, 10, 11, 12, 14).

**D18 consistency (NuGet stack):** no task starts a process or needs a native tool: `Ffmpeg`, `TestFfmpeg`, `WhisperCppTranscriber`, `Dockerfile.whisper`, the whisper service and the CUDA workflow are gone, and every caller of `SegmentDecoder` uses the parameterless constructor (Tasks 9, 12, 15, 18). `DecodedSegment.Decoder` feeds `TranscriptDocument.Decoder` (Task 3, renamed from `Ffmpeg`) through `TranscriptAssembler` (Task 12). `AnalysisSettings` (Task 1) carries `WhisperModelPath`, `WhisperDevice`, `WhisperThreads` and `WhisperDtw`, used only by `AnalysisComposition` (Task 15) and set by compose and the GPU override (Task 16). `ITranscriber` is unchanged, so the pipeline, runner and their tests (Tasks 12, 15) keep the fake. The Task 9 and Task 10 code and tests were compiled with `TreatWarningsAsErrors` and run in the spike (18 tests passing); the Dockerfile follows the throwaway images the spike built and ran.

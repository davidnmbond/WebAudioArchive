# WebAudioArchive

Records web radio streams around the clock as gapless MP3 segments, with sidecar metadata, health and coverage
reporting. Burst Radio is the primary use case.

Recordings are private archive copies and must not be republished. Nothing in this repository contains
recordings, credentials or user data.

Design: `docs/superpowers/specs/`. Plans: `docs/superpowers/plans/`.

## Run

    docker compose -f docker/docker-compose.yml up --build

Two independent recorders run (A and B). Recordings appear under `data/a/` and `data/b/`, each as `<station>/master/<yyyy>/<MM-dd>/<HHmmss>.mp3` with a `.json` beside each.
The data folder must be writable by uid 1654 (the container user): `sudo chown 1654:1654 data` on Linux.
Health: `http://127.0.0.1:8080/healthz` (recorder A) and `http://127.0.0.1:8081/healthz` (recorder B).

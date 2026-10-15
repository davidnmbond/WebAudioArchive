# Operations

## Run

    docker compose -f docker/docker-compose.yml up -d --build

Set `WAA_INSTANCE_ID` (for example `home` or `cluster`) and `WAA_DATA_DIR` in the environment or a `.env` file
beside the compose file. On Linux the data folder must be writable by uid 1654.

## Where things are

- Audio: `<data>/<station>/master/<yyyy>/<MM-dd>/<HHmmss>.mp3`, sidecar `.json` beside it. UTC throughout.
- A `.mp3.part` file is the segment being written. After a crash it is finished on the next start
  (`endedBy: crash`).
- Health: `http://127.0.0.1:8080/healthz` (503 if an enabled station has written no frame for 2 minutes after the
  first 3 minutes of uptime).

## Reading the sidecar

`gaps` lists every break in the stream (start, end, seconds, reason) that ended inside the segment;
`endedBy` is `hour-boundary`, `shutdown` or `crash`; `titles` are ICY track titles with the file byte offset.
Segment spans minus gaps give the coverage figure on `/healthz` (24 hours and 30 days).

## Known limits (plan 1)

MP3 streams only; one UTC-hour segment length; no schedule, cutting, transcript or web app yet; no disk-space
guard (plan 5). Two instances are independent and are not reconciled.

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

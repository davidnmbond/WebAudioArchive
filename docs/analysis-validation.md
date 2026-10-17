# Detector validation (issue #84)

All figures are for detections, not proven events. Thresholds are provisional until the real-sample section below is
filled in from hand labels of real Burst audio. Labels and audio are never committed (the repository is public).

## Thresholds in use

The block between the markers is checked by `DetectorValidationTests.Documented_thresholds_match_config_analysis_json`
against `config/analysis/analysis.json`: if the config changes and this block does not, the test fails and prints the
text to paste.

<!-- thresholds:begin -->
Config version 2026-10-16.1.

| Detector | Rule | Values |
|---|---|---|
| Clipping | a run of at least N consecutive samples at or above the threshold (either sign); runs closer than the merge window are one event; medium severity from the given length | threshold 0.99 of full scale, N = 3, merge 250 ms, medium from 100 ms |
| Dead air | every 50 ms window (loudest channel) below the level for at least the minimum length | below -50 dBFS for 5 s; high severity from 30 s |
| Swearing | whole words and listed suffixes from swearwords.json, masked forms at reduced confidence, allow list for near misses; severity from the tier, one step higher before the watershed; repeated runs are suppressed and reported for review only | watershed 21:00 to 05:30 Europe/London |
| Fillers | listed words only (drawn-out spellings folded), never "like"; "you know" counted: off | um, uh, er, erm, ah, hmm; reported as a rate per minute of transcribed speech, an estimate |
| Hallucination filter | suppress no-speech, annotations, known phrases, repeats | no_speech_prob above 0.6 with avg_logprob below -1; 3 or more repeats |
<!-- thresholds:end -->

## How scoring works

A detection matches a label of the same type when their spans overlap once both are widened by a tolerance (0.25 s for
clipping and dead air, 1 s for swearing and fillers in `--validate`, because word times are approximate). Each label and
each detection is used once. Precision is true positives over detections; recall is true positives over labels.

Suppressed swear events (a run of repeated swearing that detection set aside as probable hallucination; `evidence.suppressed`
is not `no`, `IsReviewOnly` in Core) are excluded from precision and recall. They are listed in the "Review only" column so
a person can listen to them, and a suppressed event never counts as finding a label.

## Synthetic sample (CI, tests/WebAudioArchive.Tests/DetectorValidationTests.cs)

Generated in the test process from signals in code; the MP3 is made by the test-only encoder in a temporary folder.
No broadcast audio is involved. 180 s of speech-like signal with 6 planted hard-clipped bursts (20 to 200 ms) and 3
dead-air gaps (5.5 to 15 s), plus near misses (a loud clean tone peaking at 0.9, a 3.5 s pause, 10 s of room tone at
-45 dBFS); 12 transcript lines covering the swear and filler spellings and near misses the detectors claim to handle,
including one repeated-swear line that must be suppressed.

Measured by the tests (precision / recall):

| Case | Clipping P / R | Dead air P / R | Swearing P / R (review only) | Fillers P / R |
|---|---|---|---|---|
| PCM, configured thresholds | 1.00 / 1.00 (6 of 6) | 1.00 / 1.00 (3 of 3) | n/a | n/a |
| Transcript lines | n/a | n/a | 1.00 / 1.00 (6 of 6, 1 review only) | 1.00 / 1.00 (7 of 7) |
| MP3 128 kbps, configured thresholds (0.99, 3 samples) | 1.00 / 1.00 (6 of 6) | 1.00 / 1.00 (3 of 3) | n/a | n/a |
| MP3 128 kbps, clipping 0.98 and 2 samples | 1.00 / 1.00 (6 of 6) | 1.00 / 1.00 (3 of 3) | n/a | n/a |

### What this shows

- The rules do what they say: the planted events are found, the listed near misses (Scunthorpe, shitake, a clean loud
  tone, a 3.5 s pause, room tone at -45 dBFS) are not reported, and a suppressed repeated-swear run is kept out of the
  precision and recall figures and counted as review only.
- Through the MP3 codec used here, the planted hard-clipped bursts still decode above 0.99 for at least 3 samples.

### What this cannot show

- It is not a measure of real-world precision or recall. The figures are 1.00 because the sample was written to match the
  rules; real speech, music and presenters are not like it.
- The clipping threshold of 0.99 after MP3 decode on real programme audio is untested. The planted bursts are a pure tone
  driven to 1.4 times full scale and hard clipped, which overshoots far more than a real clipped master; real clipping may
  decode lower and be missed at 0.99, or music with loud transients may trip it. The stored levels 0.98 and 0.999 allow a
  retune without re-decoding (`--rebuild`).
- The dead-air level (-50 dBFS for 5 s) is untested against real room tone, hiss, between-song gaps and quiet speech.
- It does not show how well whisper hears fillers and swearing on real speech, how it writes them, or how often it
  hallucinates over music. The transcript lines are hand-made, so the word times and spellings are what the author expects.
- Nothing here tests confidence values, which are provisional.

All of this needs the owner's real labels (below).

## Real sample (owner, Task 19)

Method: pick at least 30 minutes of recorded Burst speech across several presenters and both sides of 21:00. Listen
with the web Player and write every filler, swear, clip and dead air you hear into
`data/analysis/validation/labels-<date>.csv` (this folder is under the gitignored `data/`; never commit it).

Labels file format: a header line `type,startUtc,endUtc,note`, then one label per line. `type` is one of `filler`,
`swear`, `clipping`, `dead-air` (the event types in events.json). `startUtc` and `endUtc` are UTC times ending in `Z`
(for example `2026-10-16T09:12:03.4Z`); local times are rejected. `note` is optional and must not contain a line break; it
may contain commas. A synthetic example, with invented times, is committed as
[analysis-validation-labels.example.csv](analysis-validation-labels.example.csv).

Then run, for the same range (the scorer reads the labels file and the analysis output under `data/analysis` at run time;
it needs no model and writes nothing):

    docker compose -f docker/docker-compose.analysis.yml exec analysis dotnet WebAudioArchive.Analysis.Service.dll --validate /analysis/validation/labels-<date>.csv <fromUtc> <toUtc>

It prints one table for the audio types and one for the transcript types, each with a Review only column. Paste the two
tables here with the date, the range, the presenters' show names and the model in use.

| Date | Range (UTC) | Model | Clipping P / R | Dead air P / R | Swearing P / R | Fillers P / R |
|---|---|---|---|---|---|---|
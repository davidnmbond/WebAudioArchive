# WebAudioArchive instructions

## Project

WebAudioArchive records web radio streams around the clock, cuts shows out using the station's published
schedule, transcribes locally, and serves a private archive. Burst Radio (https://www.burstradio.org) is the
primary use case. The owner acts as station manager: the aims are a complete record for regulatory and
reporting purposes, and private copies of their own shows for presenters.

- Design: `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md`
- Plans: `docs/superpowers/plans/` (plan 1 is the continuous recorder; later plans add schedule and cutting,
  analysis, the web app, then operations)
- Operations: `docs/operations.md` once plan 1 is complete

## Conventions

- .NET 10, nullable enabled, warnings as errors, NuGet packages pinned to exact versions.
- Tests first (xUnit). Never skip or weaken a test to make it pass; a test that cannot run must fail with a
  message saying what is missing. Run the command and read the output before claiming anything works.
- Audio is stored exactly as broadcast. No decode or re-encode in the archive path.
- Segments are aligned and named by UTC.
- Use `/temp/` for scratch work; it is gitignored. Do not leave scratch files in the repository root.
- No em dashes anywhere (code comments, docs, commit messages, chat). Use commas, colons, semicolons or new
  sentences.
- When a question needs the owner's answer, ask it with the structured question tool, not as prose.
- Commit after each task with a clear message. This is a personal repository on a rapid workflow: commit to
  `master` directly, no pull requests.

## This repository is public

Never commit recordings, broadcast audio, credentials, tokens, user data or anything from a customer or
company system. Test audio is synthetic only. Saved public web pages are acceptable as fixtures.

## Shared skills

This repository may use the shared PanoramicData skills, read from a sibling checkout at
`../PanoramicData.Skills/.github/skills`. On the owner's machine that path is a folder link to
`C:\Users\david\source\repos\panoramicdata\PanoramicData.Skills`; if it does not resolve, tell the owner and
stop rather than improvising a substitute.

- Read `../PanoramicData.Skills/.github/skills/copilot-instructions.md` before choosing a skill, and a skill's
  `SKILL.md` in full before running it.
- Never edit anything under `../PanoramicData.Skills` from here. A fix belongs upstream in
  `panoramicdata/PanoramicData.Skills`.
- Do not copy company skill code into this repository. Code here is written fresh; reading a skill as a
  reference is fine.
- Keep the two worlds apart: no WebAudioArchive recordings, transcripts or user data into company systems, and
  no company or customer data into this repository.

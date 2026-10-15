# WebAudioArchive Plan 1: Continuous Recorder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Linux container that records the Burst Radio stream 24 hours a day as gapless, frame-aligned hourly MP3 segments with a JSON sidecar each, survives drops and crashes, and reports health and coverage.

**Architecture:** One .NET 10 process. `StationSession` holds one always-on HTTP connection per station, strips ICY metadata, splits the MP3 into whole frames and hands them to an `IFrameSink`; `MasterRecorder` is the sink that writes segments. A small ASP.NET Core host exposes `/healthz`. Later plans add more sinks (analysis) without changing the recorder.

**Tech Stack:** .NET 10 (SDK 10.0.x), ASP.NET Core (Kestrel for `/healthz` and for the in-process fake station in tests), xUnit 2, Docker (Debian-based aspnet image), GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md` (sections 3.1, 3.4, 3.5, 5, 6 and the coverage figure in 3.10). This is plan 1 of 5, see "Plan series" below.

## Plan series

| Plan | Delivers | Spec sections |
|---|---|---|
| 1 (this) | Continuous master recorder, health, Docker, CI | 3.1, 3.4, 3.5, 5 |
| 2 | Schedule service (Burst parser, LLM fallback), window planner, show cutter, show.json | 3.2, 3.3, 3.11 |
| 3 | Analysis: decode, transcriber, clipping, swear, filler, dead air, events timeline, enricher | 3.6, 3.7 |
| 4 | Web app: Google sign-in, roles, SQLite, pages, audio endpoints, audit, weekly report | 3.8, 3.12 |
| 5 | Disk-space guard, retention hold/delete tools, k8s manifest, operations docs | 3.5, 3.12, 5 |

Each later plan is written after the previous one ships, using what it learned.

## Global Constraints

- .NET 10, `net10.0`, `Nullable` enabled, `TreatWarningsAsErrors` true. Packages pinned to exact versions, no floating versions.
- The repo is public: no secrets, no recordings, no broadcast audio in git. Test audio is synthetic only. Saved web pages (HTML) are allowed as fixtures.
- Audio is stored exactly as broadcast: no decode, no re-encode, ICY blocks removed, nothing else changed.
- Segments split only on MP3 frame boundaries; concatenating a station's segments in time order reproduces the received audio bytes.
- Segment boundaries are aligned to **UTC** clock hours; files are named by UTC (`master/<yyyy>/<MM-dd>/<HHmmss>.mp3`). This replaces "station-local hour" in the spec: local naming collides when the clocks go back. The sidecar also records local time.
- No em dashes in any text, code comment, commit message or doc.
- No skipped tests. A test that cannot run must fail with a message saying what is missing.
- Commit messages end with the attribution trailer the harness supplies.
- Plan 1 supports **MP3 (MPEG Layer III) streams only**. Any other content type is refused with a clear error, not recorded wrongly.

## Review Focus

Input classes the spec implies but nothing else in the plan would exercise. Each has a test named in the task that owns the code.

1. ICY metadata that is empty almost always, contains multi-byte UTF-8, or arrives split across reads (Task 3: `Titles_and_audio_survive_any_chunking`).
2. Network reads that split an MP3 header or frame at any byte, including one byte at a time (Task 2: `Whole_frames_round_trip`).
3. Restart within the same hour or the same second, and a crashed `.part` file ending in half a frame (Task 7: `Restart_in_same_second_gets_distinct_file`, `Crash_part_file_is_recovered_and_truncated_to_whole_frames`).
4. A stream URL that redirects to a short-lived tokenised URL (Burst does), and a connection that stays open but sends nothing (Task 6: `Each_open_follows_the_redirect_afresh`, `Stalled_connection_is_dropped_and_reconnected`).
5. Hour, day and DST changeovers: segments rolling at 23:00 to 00:00 UTC and the UK clock-back hour (Task 7: `Rolls_at_utc_midnight_into_new_day_folder`, `Clock_back_hour_does_not_collide`).
6. A config typo silently ignored (Task 4: `Unknown_field_is_rejected_by_name`).

---

## File structure

```
global.json
Directory.Build.props
.editorconfig
.gitignore
README.md
WebAudioArchive.slnx
config/stations.json
src/WebAudioArchive.Core/WebAudioArchive.Core.csproj
src/WebAudioArchive.Core/Mp3/Mp3FrameHeader.cs        parse one 4-byte header
src/WebAudioArchive.Core/Mp3/Mp3FrameSplitter.cs      bytes in, whole frames out, resync
src/WebAudioArchive.Core/Icy/IcyDemuxer.cs            strips ICY metadata, reports titles
src/WebAudioArchive.Core/Config/StationConfig.cs      record + loader + validation
src/WebAudioArchive.Core/Streams/StreamCandidates.cs  listen page -> candidate stream URLs
src/WebAudioArchive.Core/Streams/StreamResolver.cs    probe candidates, playlists
src/WebAudioArchive.Core/Streams/HttpStreamSource.cs  open the stream (redirects, ICY headers)
src/WebAudioArchive.Core/Recording/IClock.cs          clock abstraction
src/WebAudioArchive.Core/Recording/IFrameSink.cs      sink interface and GapRecord
src/WebAudioArchive.Core/Recording/StationHealth.cs   thread-safe health counters
src/WebAudioArchive.Core/Recording/StationSession.cs  connect, pump, reconnect, record gaps
src/WebAudioArchive.Core/Recording/SegmentMeta.cs     sidecar model and atomic writer
src/WebAudioArchive.Core/Recording/MasterRecorder.cs  frame sink writing hourly segments
src/WebAudioArchive.Core/Recording/CoverageReader.cs  coverage fraction from sidecars
src/WebAudioArchive.Worker/WebAudioArchive.Worker.csproj
src/WebAudioArchive.Worker/Program.cs                 host, /healthz, --healthcheck
src/WebAudioArchive.Worker/StationRegistry.cs         shared per-station state
src/WebAudioArchive.Worker/StationRecorderService.cs  BackgroundService per station
tests/WebAudioArchive.Tests/WebAudioArchive.Tests.csproj
tests/WebAudioArchive.Tests/Support/Mp3TestData.cs
tests/WebAudioArchive.Tests/Support/IcyTestData.cs
tests/WebAudioArchive.Tests/Support/FakeStation.cs
tests/WebAudioArchive.Tests/Support/ManualClock.cs
tests/WebAudioArchive.Tests/Support/StubHandler.cs
tests/WebAudioArchive.Tests/Support/RecordingSink.cs
tests/WebAudioArchive.Tests/*Tests.cs
tests/fixtures/burst-listen-2026-10-15.html           already committed? see Task 1 step 1
tests/fixtures/burst-schedule-2026-10-15.html         already committed
docker/Dockerfile
docker/docker-compose.yml
.github/workflows/ci.yml
```

Working directory for every command: `C:\Users\david\source\repos\davidnmbond\WebAudioArchive` (PowerShell).

---

### Task 1: Solution scaffold, CI and a passing smoke test

**Files:**
- Create: `global.json`, `Directory.Build.props`, `.editorconfig`, `.gitignore`, `README.md`, `WebAudioArchive.slnx`
- Create: the three `.csproj` files, `tests/WebAudioArchive.Tests/SmokeTests.cs`, `.github/workflows/ci.yml`

**Interfaces:**
- Produces: projects `WebAudioArchive.Core`, `WebAudioArchive.Worker`, `WebAudioArchive.Tests`; test fixtures copied to `AppContext.BaseDirectory/fixtures/`.

- [ ] **Step 1: Create the root files**

`global.json`:
```json
{
  "sdk": { "version": "10.0.100", "rollForward": "latestFeature" }
}
```

`Directory.Build.props`:
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

`.editorconfig`:
```
root = true
[*]
indent_style = space
indent_size = 4
end_of_line = lf
insert_final_newline = true
charset = utf-8
[*.{json,yml,yaml,csproj,slnx,props}]
indent_size = 2
```

`.gitignore`:
```
bin/
obj/
.vs/
*.user
/data/
/temp/
```

`README.md`:
```markdown
# WebAudioArchive

Records web radio streams around the clock as gapless MP3 segments, with sidecar metadata, health and coverage
reporting. Burst Radio is the primary use case.

Recordings are private archive copies and must not be republished. Nothing in this repository contains
recordings, credentials or user data.

Design: `docs/superpowers/specs/`. Plans: `docs/superpowers/plans/`.

## Run

    docker compose -f docker/docker-compose.yml up --build

Recordings appear under `data/<station>/master/<yyyy>/<MM-dd>/<HHmmss>.mp3` with a `.json` beside each.
The data folder must be writable by uid 1654 (the container user): `sudo chown 1654:1654 data` on Linux.
Health: `http://127.0.0.1:8080/healthz`.
```

- [ ] **Step 2: Create the projects**

Run:
```powershell
New-Item -ItemType Directory -Force src/WebAudioArchive.Core, src/WebAudioArchive.Worker, tests/WebAudioArchive.Tests/Support, config, docker, .github/workflows | Out-Null
```

`src/WebAudioArchive.Core/WebAudioArchive.Core.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
</Project>
```

`src/WebAudioArchive.Worker/WebAudioArchive.Worker.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\WebAudioArchive.Core\WebAudioArchive.Core.csproj" />
  </ItemGroup>
</Project>
```

`tests/WebAudioArchive.Tests/WebAudioArchive.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.0" />
  </ItemGroup>
  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\WebAudioArchive.Core\WebAudioArchive.Core.csproj" />
    <ProjectReference Include="..\..\src\WebAudioArchive.Worker\WebAudioArchive.Worker.csproj" />
  </ItemGroup>
  <ItemGroup>
    <None Include="..\fixtures\**\*" Link="fixtures\%(RecursiveDir)%(Filename)%(Extension)" CopyToOutputDirectory="PreserveNewest" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Write the smoke test**

`tests/WebAudioArchive.Tests/SmokeTests.cs`:
```csharp
namespace WebAudioArchive.Tests;

public class SmokeTests
{
    [Fact]
    public void Fixtures_are_copied_next_to_the_tests()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "burst-schedule-2026-10-15.html");
        Assert.True(File.Exists(path), $"Fixture missing: {path}. Check the None Include in the test csproj.");
    }
}
```

- [ ] **Step 4: Create the solution and run the test**

Run:
```powershell
dotnet new sln --format slnx --name WebAudioArchive
dotnet sln add src/WebAudioArchive.Core src/WebAudioArchive.Worker tests/WebAudioArchive.Tests
dotnet test
```
Expected: restore succeeds, 1 test passes. If a pinned package version does not restore, run `dotnet package search <name> --exact-match` (or `dotnet nuget` equivalent), pin the newest stable version and mention it in the commit message. The Worker has no `Program.cs` yet, so add a placeholder first: create `src/WebAudioArchive.Worker/Program.cs` containing `return 0;` (replaced in Task 8).

- [ ] **Step 5: Add CI**

`.github/workflows/ci.yml`:
```yaml
name: ci
on:
  push:
    branches: [master, main]
  pull_request:
jobs:
  test:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet build -c Release
      - run: dotnet test -c Release --no-build
  docker:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - run: docker build -f docker/Dockerfile -t webaudioarchive:ci .
```
(The docker job fails until Task 8 adds the Dockerfile; that is expected, so only push after Task 8, or push now and accept one red run.)

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "Scaffold solution, CI and smoke test"
```

---

### Task 2: MP3 header and frame splitter

**Files:**
- Create: `src/WebAudioArchive.Core/Mp3/Mp3FrameHeader.cs`, `src/WebAudioArchive.Core/Mp3/Mp3FrameSplitter.cs`
- Create: `tests/WebAudioArchive.Tests/Support/Mp3TestData.cs`, `tests/WebAudioArchive.Tests/Mp3FrameSplitterTests.cs`

**Interfaces:**
- Produces:
  - `readonly record struct Mp3FrameHeader(int MpegVersion, int BitrateKbps, int SampleRateHz, int Channels, bool Padding, int FrameLength, int SamplesPerFrame)` with `const int HeaderSize = 4`, `string Codec`, `double FrameSeconds`, `static bool TryParse(ReadOnlySpan<byte>, out Mp3FrameHeader)`.
  - `delegate void FrameHandler(ReadOnlySpan<byte> frame, in Mp3FrameHeader header)`.
  - `sealed class Mp3FrameSplitter { long SkippedBytes; void Push(ReadOnlySpan<byte>, FrameHandler); void Complete(FrameHandler); }`
  - Test helper `Mp3TestData.Frame(int seq)`, `Mp3TestData.Frames(int count, int startSeq = 0)`, `Mp3TestData.SeqOf(ReadOnlySpan<byte>)`.

- [ ] **Step 1: Write the test helper**

`tests/WebAudioArchive.Tests/Support/Mp3TestData.cs`:
```csharp
namespace WebAudioArchive.Tests.Support;

/// <summary>Synthetic MPEG-1 Layer III frames: 128 kbps, 44.1 kHz, stereo. Payload bytes stay below 0x80 so they never form a sync word.</summary>
public static class Mp3TestData
{
    public static byte[] Frame(int seq)
    {
        var padded = seq % 3 == 0;
        var length = padded ? 418 : 417;
        var f = new byte[length];
        f[0] = 0xFF;
        f[1] = 0xFB;
        f[2] = padded ? (byte)0x92 : (byte)0x90;
        f[3] = 0x00;
        f[4] = (byte)((seq >> 21) & 0x7F);
        f[5] = (byte)((seq >> 14) & 0x7F);
        f[6] = (byte)((seq >> 7) & 0x7F);
        f[7] = (byte)(seq & 0x7F);
        for (var i = 8; i < length; i++) f[i] = (byte)((seq + i) & 0x7F);
        return f;
    }

    public static byte[] Frames(int count, int startSeq = 0)
    {
        var ms = new MemoryStream();
        for (var i = 0; i < count; i++) ms.Write(Frame(startSeq + i));
        return ms.ToArray();
    }

    public static int SeqOf(ReadOnlySpan<byte> frame) =>
        (frame[4] << 21) | (frame[5] << 14) | (frame[6] << 7) | frame[7];
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/Mp3FrameSplitterTests.cs`:
```csharp
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class Mp3FrameSplitterTests
{
    static List<byte[]> Split(byte[] data, int chunk, out long skipped)
    {
        var frames = new List<byte[]>();
        var splitter = new Mp3FrameSplitter();
        FrameHandler handler = (ReadOnlySpan<byte> f, in Mp3FrameHeader hdr) => frames.Add(f.ToArray());
        for (var i = 0; i < data.Length; i += chunk)
            splitter.Push(data.AsSpan(i, Math.Min(chunk, data.Length - i)), handler);
        splitter.Complete(handler);
        skipped = splitter.SkippedBytes;
        return frames;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(417)]
    [InlineData(1000)]
    [InlineData(1_000_000)]
    public void Whole_frames_round_trip(int chunk)
    {
        var data = Mp3TestData.Frames(50);
        var frames = Split(data, chunk, out var skipped);

        Assert.Equal(50, frames.Count);
        for (var i = 0; i < 50; i++) Assert.Equal(Mp3TestData.Frame(i), frames[i]);
        Assert.Equal(0, skipped);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(1000)]
    public void Resyncs_after_garbage_including_a_false_sync_word(int chunk)
    {
        // A valid-looking header followed by junk: must not be taken as a frame.
        var junk = new byte[] { 0xFF, 0xFB, 0x90, 0x00 }.Concat(Enumerable.Repeat((byte)0x11, 500)).ToArray();
        var data = junk.Concat(Mp3TestData.Frames(20)).ToArray();

        var frames = Split(data, chunk, out var skipped);

        Assert.Equal(20, frames.Count);
        Assert.Equal(Mp3TestData.Frame(0), frames[0]);
        Assert.Equal(junk.Length, skipped);
    }

    [Fact]
    public void Truncated_last_frame_is_dropped_and_counted()
    {
        var data = Mp3TestData.Frames(3).Concat(Mp3TestData.Frame(3).Take(100)).ToArray();

        var frames = Split(data, 1000, out var skipped);

        Assert.Equal(3, frames.Count);
        Assert.Equal(100, skipped);
    }

    [Fact]
    public void Header_fields_are_decoded()
    {
        Assert.True(Mp3FrameHeader.TryParse(Mp3TestData.Frame(1), out var h));
        Assert.Equal(1, h.MpegVersion);
        Assert.Equal(128, h.BitrateKbps);
        Assert.Equal(44100, h.SampleRateHz);
        Assert.Equal(2, h.Channels);
        Assert.Equal(417, h.FrameLength);
        Assert.Equal(1152, h.SamplesPerFrame);
        Assert.Equal("MPEG-1 Layer III", h.Codec);

        Assert.True(Mp3FrameHeader.TryParse(Mp3TestData.Frame(0), out var padded));
        Assert.Equal(418, padded.FrameLength);
    }

    [Fact]
    public void Non_layer3_header_is_rejected()
    {
        // 0xFD in byte 1 selects Layer II.
        Assert.False(Mp3FrameHeader.TryParse(new byte[] { 0xFF, 0xFD, 0x90, 0x00 }, out _));
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter Mp3FrameSplitterTests`
Expected: build FAIL (types not defined).

- [ ] **Step 4: Implement the header**

`src/WebAudioArchive.Core/Mp3/Mp3FrameHeader.cs`:
```csharp
namespace WebAudioArchive.Core.Mp3;

public readonly record struct Mp3FrameHeader(
    int MpegVersion,
    int BitrateKbps,
    int SampleRateHz,
    int Channels,
    bool Padding,
    int FrameLength,
    int SamplesPerFrame)
{
    public const int HeaderSize = 4;

    static readonly int[] BitratesV1 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    static readonly int[] BitratesV2 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
    static readonly int[] RatesV1 = [44100, 48000, 32000];
    static readonly int[] RatesV2 = [22050, 24000, 16000];
    static readonly int[] RatesV25 = [11025, 12000, 8000];

    public string Codec => MpegVersion switch
    {
        1 => "MPEG-1 Layer III",
        2 => "MPEG-2 Layer III",
        _ => "MPEG-2.5 Layer III",
    };

    public double FrameSeconds => (double)SamplesPerFrame / SampleRateHz;

    public static bool TryParse(ReadOnlySpan<byte> b, out Mp3FrameHeader header)
    {
        header = default;
        if (b.Length < HeaderSize || b[0] != 0xFF || (b[1] & 0xE0) != 0xE0) return false;

        var versionBits = (b[1] >> 3) & 3;
        var layerBits = (b[1] >> 1) & 3;
        if (versionBits == 1 || layerBits != 1) return false;

        var bitrateIndex = b[2] >> 4;
        var rateIndex = (b[2] >> 2) & 3;
        if (bitrateIndex == 0 || bitrateIndex == 15 || rateIndex == 3) return false;

        var v1 = versionBits == 3;
        var bitrate = (v1 ? BitratesV1 : BitratesV2)[bitrateIndex];
        var rate = (versionBits == 3 ? RatesV1 : versionBits == 2 ? RatesV2 : RatesV25)[rateIndex];
        var padding = ((b[2] >> 1) & 1) == 1;
        var length = (v1 ? 144 : 72) * bitrate * 1000 / rate + (padding ? 1 : 0);
        var version = versionBits == 3 ? 1 : versionBits == 2 ? 2 : 25;
        var channels = (b[3] >> 6) == 3 ? 1 : 2;

        header = new Mp3FrameHeader(version, bitrate, rate, channels, padding, length, v1 ? 1152 : 576);
        return true;
    }
}
```

- [ ] **Step 5: Implement the splitter**

`src/WebAudioArchive.Core/Mp3/Mp3FrameSplitter.cs`:
```csharp
namespace WebAudioArchive.Core.Mp3;

/// <summary>The span passed to the handler points into an internal buffer and is valid only during the call.</summary>
public delegate void FrameHandler(ReadOnlySpan<byte> frame, in Mp3FrameHeader header);

public sealed class Mp3FrameSplitter
{
    byte[] _buf = new byte[16 * 1024];
    int _start;
    int _count;

    public long SkippedBytes { get; private set; }

    public void Push(ReadOnlySpan<byte> data, FrameHandler onFrame)
    {
        Append(data);
        while (TryTakeFrame(requireNext: true, onFrame)) { }
    }

    /// <summary>Emit a final whole frame if one is buffered, and count anything left over as skipped.</summary>
    public void Complete(FrameHandler onFrame)
    {
        while (TryTakeFrame(requireNext: false, onFrame)) { }
        SkippedBytes += _count;
        _start = 0;
        _count = 0;
    }

    // A candidate frame is accepted only when another valid header follows it, which rejects false sync words in junk.
    bool TryTakeFrame(bool requireNext, FrameHandler onFrame)
    {
        while (true)
        {
            var span = new ReadOnlySpan<byte>(_buf, _start, _count);
            var idx = FindSync(span);
            if (idx < 0)
            {
                var keep = span.Length > 0 && span[^1] == 0xFF ? 1 : 0;
                Skip(span.Length - keep);
                return false;
            }

            if (idx > 0)
            {
                Skip(idx);
                span = new ReadOnlySpan<byte>(_buf, _start, _count);
            }

            if (span.Length < Mp3FrameHeader.HeaderSize) return false;
            if (!Mp3FrameHeader.TryParse(span, out var header))
            {
                Skip(1);
                continue;
            }

            var need = header.FrameLength + (requireNext ? Mp3FrameHeader.HeaderSize : 0);
            if (span.Length < need) return false;
            if (requireNext && !Mp3FrameHeader.TryParse(span[header.FrameLength..], out _))
            {
                Skip(1);
                continue;
            }

            onFrame(span[..header.FrameLength], in header);
            Consume(header.FrameLength);
            return true;
        }
    }

    static int FindSync(ReadOnlySpan<byte> span)
    {
        for (var i = 0; i < span.Length - 1; i++)
            if (span[i] == 0xFF && (span[i + 1] & 0xE0) == 0xE0) return i;
        return -1;
    }

    void Skip(int n)
    {
        SkippedBytes += n;
        Consume(n);
    }

    void Consume(int n)
    {
        _start += n;
        _count -= n;
        if (_count == 0) _start = 0;
    }

    void Append(ReadOnlySpan<byte> data)
    {
        if (_start + _count + data.Length > _buf.Length)
        {
            if (_count + data.Length <= _buf.Length)
            {
                Buffer.BlockCopy(_buf, _start, _buf, 0, _count);
            }
            else
            {
                var bigger = new byte[Math.Max(_buf.Length * 2, _count + data.Length)];
                Buffer.BlockCopy(_buf, _start, bigger, 0, _count);
                _buf = bigger;
            }

            _start = 0;
        }

        data.CopyTo(_buf.AsSpan(_start + _count));
        _count += data.Length;
    }
}
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test --filter Mp3FrameSplitterTests`
Expected: all pass (5 test methods, 8 cases).

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "Add MP3 header parser and resyncing frame splitter"
```

---

### Task 3: ICY metadata demuxer

**Files:**
- Create: `src/WebAudioArchive.Core/Icy/IcyDemuxer.cs`
- Create: `tests/WebAudioArchive.Tests/Support/IcyTestData.cs`, `tests/WebAudioArchive.Tests/IcyDemuxerTests.cs`

**Interfaces:**
- Produces: `delegate void AudioHandler(ReadOnlySpan<byte> audio)`; `sealed record IcyTitle(string Title, long AtAudioByte)`; `sealed class IcyDemuxer(int metaInt)` with `long AudioBytes` and `void Push(ReadOnlySpan<byte>, AudioHandler, Action<IcyTitle>)`. `metaInt <= 0` means passthrough.
- Test helper: `IcyTestData.Build(byte[] audio, int metaInt, Func<int, string?> titleForBlock)`.

- [ ] **Step 1: Write the test helper**

`tests/WebAudioArchive.Tests/Support/IcyTestData.cs`:
```csharp
using System.Text;

namespace WebAudioArchive.Tests.Support;

public static class IcyTestData
{
    /// <summary>Interleave metadata after every full metaInt bytes. A null title writes the one-byte "no change" block.</summary>
    public static byte[] Build(byte[] audio, int metaInt, Func<int, string?> titleForBlock)
    {
        var ms = new MemoryStream();
        var block = 0;
        for (var pos = 0; pos < audio.Length; pos += metaInt)
        {
            var n = Math.Min(metaInt, audio.Length - pos);
            ms.Write(audio, pos, n);
            if (n < metaInt) break;

            var title = titleForBlock(block++);
            if (title is null)
            {
                ms.WriteByte(0);
                continue;
            }

            var text = Encoding.UTF8.GetBytes($"StreamTitle='{title}';");
            var padded = (text.Length + 15) / 16 * 16;
            ms.WriteByte((byte)(padded / 16));
            ms.Write(text);
            ms.Write(new byte[padded - text.Length]);
        }

        return ms.ToArray();
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/IcyDemuxerTests.cs`:
```csharp
using WebAudioArchive.Core.Icy;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class IcyDemuxerTests
{
    static (byte[] Audio, List<IcyTitle> Titles) Demux(byte[] wire, int metaInt, int chunk)
    {
        var audio = new MemoryStream();
        var titles = new List<IcyTitle>();
        var d = new IcyDemuxer(metaInt);
        AudioHandler onAudio = a => audio.Write(a);
        for (var i = 0; i < wire.Length; i += chunk)
            d.Push(wire.AsSpan(i, Math.Min(chunk, wire.Length - i)), onAudio, titles.Add);
        return (audio.ToArray(), titles);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(1_000_000)]
    public void Titles_and_audio_survive_any_chunking(int chunk)
    {
        var audio = Enumerable.Range(0, 100).Select(i => (byte)i).ToArray();
        var wire = IcyTestData.Build(audio, metaInt: 16, block => block switch
        {
            0 => "Artist - Song",
            2 => "Artist - Song",      // repeat of the last title: must be reported once
            3 => "Caf\u00e9 \u2013 Two", // multi-byte UTF-8
            _ => null,
        });

        var (recovered, titles) = Demux(wire, 16, chunk);

        Assert.Equal(audio, recovered);
        Assert.Collection(titles,
            t => { Assert.Equal("Artist - Song", t.Title); Assert.Equal(16, t.AtAudioByte); },
            t => { Assert.Equal("Caf\u00e9 \u2013 Two", t.Title); Assert.Equal(64, t.AtAudioByte); });
    }

    [Fact]
    public void Zero_meta_interval_is_passthrough()
    {
        var audio = new byte[] { 1, 2, 3, 4, 5 };
        var (recovered, titles) = Demux(audio, 0, 2);

        Assert.Equal(audio, recovered);
        Assert.Empty(titles);
    }

    [Fact]
    public void Empty_title_is_ignored()
    {
        var audio = new byte[32];
        var wire = IcyTestData.Build(audio, 16, block => block == 0 ? "" : null);

        var (_, titles) = Demux(wire, 16, 5);

        Assert.Empty(titles);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter IcyDemuxerTests`
Expected: build FAIL (types not defined).

- [ ] **Step 4: Implement**

`src/WebAudioArchive.Core/Icy/IcyDemuxer.cs`:
```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Core.Icy;

public delegate void AudioHandler(ReadOnlySpan<byte> audio);

/// <param name="Title">Stream title as sent by the server.</param>
/// <param name="AtAudioByte">Count of audio bytes (metadata excluded) received before this title.</param>
public sealed record IcyTitle(string Title, long AtAudioByte);

/// <summary>Removes interleaved ICY metadata blocks from a stream, leaving the audio bytes untouched.</summary>
public sealed partial class IcyDemuxer(int metaInterval)
{
    long _audioTotal;
    readonly int _metaInt = metaInterval;
    int _audioLeft = metaInterval;
    int _metaLeft = -1; // -1: the next byte is the length byte
    byte[] _meta = [];
    int _metaFilled;
    string? _lastTitle;

    public long AudioBytes => _audioTotal;

    public void Push(ReadOnlySpan<byte> data, AudioHandler onAudio, Action<IcyTitle> onTitle)
    {
        if (_metaInt <= 0)
        {
            _audioTotal += data.Length;
            onAudio(data);
            return;
        }

        while (!data.IsEmpty)
        {
            if (_audioLeft > 0)
            {
                var n = Math.Min(_audioLeft, data.Length);
                onAudio(data[..n]);
                _audioTotal += n;
                _audioLeft -= n;
                data = data[n..];
            }
            else if (_metaLeft < 0)
            {
                var length = data[0] * 16;
                data = data[1..];
                if (length == 0)
                {
                    _audioLeft = _metaInt;
                }
                else
                {
                    _metaLeft = length;
                    _meta = new byte[length];
                    _metaFilled = 0;
                }
            }
            else
            {
                var n = Math.Min(_metaLeft, data.Length);
                data[..n].CopyTo(_meta.AsSpan(_metaFilled));
                _metaFilled += n;
                _metaLeft -= n;
                data = data[n..];
                if (_metaLeft == 0)
                {
                    HandleMetadata(onTitle);
                    _metaLeft = -1;
                    _audioLeft = _metaInt;
                }
            }
        }
    }

    void HandleMetadata(Action<IcyTitle> onTitle)
    {
        var text = Encoding.UTF8.GetString(_meta).TrimEnd('\0');
        var m = TitlePattern().Match(text);
        if (!m.Success) return;
        var title = m.Groups["t"].Value.Trim();
        if (title.Length == 0 || title == _lastTitle) return;
        _lastTitle = title;
        onTitle(new IcyTitle(title, _audioTotal));
    }

    [GeneratedRegex("StreamTitle='(?<t>.*?)';", RegexOptions.Singleline)]
    private static partial Regex TitlePattern();
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test --filter IcyDemuxerTests`
Expected: all pass.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "Add ICY metadata demuxer"
```

---

### Task 4: Station config

**Files:**
- Create: `src/WebAudioArchive.Core/Config/StationConfig.cs`, `config/stations.json`
- Test: `tests/WebAudioArchive.Tests/StationConfigTests.cs`

**Interfaces:**
- Produces: `sealed record StationConfig { Id, Name, ListenUrl, ScheduleUrl?, Timezone = "Europe/London", Enabled = true }`; `sealed class ConfigException(IReadOnlyList<string> errors)` with `Errors`; `static class StationConfigLoader { Parse(string json, string source); Load(string path); }`.

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/StationConfigTests.cs`:
```csharp
using WebAudioArchive.Core.Config;

namespace WebAudioArchive.Tests;

public class StationConfigTests
{
    const string Good = """
        { "stations": [ { "id": "burst", "name": "Burst Radio",
            "listenUrl": "https://www.burstradio.org/listen",
            "scheduleUrl": "https://www.burstradio.org/schedule" } ] }
        """;

    [Fact]
    public void Valid_config_parses_with_defaults()
    {
        var s = Assert.Single(StationConfigLoader.Parse(Good, "test"));
        Assert.Equal("burst", s.Id);
        Assert.Equal("Europe/London", s.Timezone);
        Assert.True(s.Enabled);
    }

    [Fact]
    public void Unknown_field_is_rejected_by_name()
    {
        var json = Good.Replace("\"listenUrl\"", "\"listenUrls\"");
        var ex = Assert.Throws<ConfigException>(() => StationConfigLoader.Parse(json, "test"));
        Assert.Contains(ex.Errors, e => e.Contains("listenUrls"));
    }

    [Fact]
    public void All_problems_are_reported_together()
    {
        const string json = """
            { "stations": [
              { "id": "Bad Id", "name": "A", "listenUrl": "not a url", "timezone": "Nowhere/City" },
              { "id": "ok", "name": "B", "listenUrl": "https://example.org/live" },
              { "id": "ok", "name": "C", "listenUrl": "https://example.org/live2" } ] }
            """;
        var ex = Assert.Throws<ConfigException>(() => StationConfigLoader.Parse(json, "test"));

        Assert.Contains(ex.Errors, e => e.Contains("Bad Id"));
        Assert.Contains(ex.Errors, e => e.Contains("listenUrl"));
        Assert.Contains(ex.Errors, e => e.Contains("Nowhere/City"));
        Assert.Contains(ex.Errors, e => e.Contains("duplicate") && e.Contains("ok"));
    }

    [Fact]
    public void Empty_station_list_is_rejected()
    {
        var ex = Assert.Throws<ConfigException>(() => StationConfigLoader.Parse("""{ "stations": [] }""", "test"));
        Assert.Contains(ex.Errors, e => e.Contains("at least one"));
    }

    [Fact]
    public void Shipped_config_is_valid()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "config", "stations.json"));
        Assert.True(File.Exists(path), $"Expected the shipped config at {path}");
        var s = Assert.Single(StationConfigLoader.Load(path));
        Assert.Equal("burst", s.Id);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter StationConfigTests`
Expected: build FAIL.

- [ ] **Step 3: Implement**

`src/WebAudioArchive.Core/Config/StationConfig.cs`:
```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebAudioArchive.Core.Config;

public sealed record StationConfig
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string ListenUrl { get; init; }
    public string? ScheduleUrl { get; init; }
    public string Timezone { get; init; } = "Europe/London";
    public bool Enabled { get; init; } = true;
}

public sealed class ConfigException(IReadOnlyList<string> errors)
    : Exception("Invalid configuration: " + string.Join("; ", errors))
{
    public IReadOnlyList<string> Errors { get; } = errors;
}

public static partial class StationConfigLoader
{
    sealed record Root(List<StationConfig>? Stations);

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    public static IReadOnlyList<StationConfig> Load(string path)
    {
        if (!File.Exists(path)) throw new ConfigException([$"{path}: file not found"]);
        return Parse(File.ReadAllText(path), path);
    }

    public static IReadOnlyList<StationConfig> Parse(string json, string source)
    {
        Root? root;
        try
        {
            root = JsonSerializer.Deserialize<Root>(json, Options);
        }
        catch (JsonException ex)
        {
            throw new ConfigException([$"{source}: {ex.Message} (at {ex.Path})"]);
        }

        var stations = root?.Stations ?? [];
        var errors = new List<string>();
        if (stations.Count == 0) errors.Add($"{source}: stations must contain at least one station");

        var seen = new HashSet<string>();
        for (var i = 0; i < stations.Count; i++)
        {
            var s = stations[i];
            var at = $"{source}: stations[{i}]";
            if (!IdPattern().IsMatch(s.Id)) errors.Add($"{at}.id '{s.Id}' must be lowercase letters, digits and hyphens");
            if (!seen.Add(s.Id)) errors.Add($"{at}.id duplicate id '{s.Id}'");
            if (!IsHttpUrl(s.ListenUrl)) errors.Add($"{at}.listenUrl '{s.ListenUrl}' is not an absolute http(s) URL");
            if (s.ScheduleUrl is not null && !IsHttpUrl(s.ScheduleUrl)) errors.Add($"{at}.scheduleUrl '{s.ScheduleUrl}' is not an absolute http(s) URL");
            try { TimeZoneInfo.FindSystemTimeZoneById(s.Timezone); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                errors.Add($"{at}.timezone '{s.Timezone}' is not a known IANA time zone");
            }
        }

        if (errors.Count > 0) throw new ConfigException(errors);
        return stations;
    }

    static bool IsHttpUrl(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]*$")]
    private static partial Regex IdPattern();
}
```

`config/stations.json`:
```json
{
  "stations": [
    {
      "id": "burst",
      "name": "Burst Radio",
      "listenUrl": "https://www.burstradio.org/listen",
      "scheduleUrl": "https://www.burstradio.org/schedule",
      "timezone": "Europe/London"
    }
  ]
}
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test --filter StationConfigTests`
Expected: all pass. If `Shipped_config_is_valid` fails on the path, count the `..` segments: the test assembly is in `tests/WebAudioArchive.Tests/bin/Debug/net10.0/`, five levels below the repo root.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "Add station config loader with strict validation"
```

---

### Task 5: Stream discovery from the listen page

**Files:**
- Create: `src/WebAudioArchive.Core/Streams/StreamCandidates.cs`, `src/WebAudioArchive.Core/Streams/StreamResolver.cs`
- Create: `tests/WebAudioArchive.Tests/Support/StubHandler.cs`
- Test: `tests/WebAudioArchive.Tests/StreamDiscoveryTests.cs`
- Fixture: `tests/fixtures/burst-listen-2026-10-15.html` (already saved from the live page on 2026-10-15; commit it if `git status` shows it untracked)

**Interfaces:**
- Produces:
  - `sealed record StreamCandidate(Uri Url, string How)`; `static class StreamCandidates { IReadOnlyList<StreamCandidate> FromHtml(string html, Uri pageUrl); }`
  - `sealed record ResolvedStream(Uri Url, string How)`; `sealed class StreamNotFoundException(string message) : Exception(message)`
  - `sealed class StreamResolver(HttpClient http, ILogger<StreamResolver> log) { Task<ResolvedStream> ResolveAsync(Uri listenUrl, CancellationToken ct); }`
  - Test helper `StubHandler(Func<HttpRequestMessage, HttpResponseMessage>)`.

- [ ] **Step 1: Write the stub handler**

`tests/WebAudioArchive.Tests/Support/StubHandler.cs`:
```csharp
namespace WebAudioArchive.Tests.Support;

public sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<Uri> Requests { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(respond(request));
    }

    public static HttpResponseMessage Html(string body) =>
        new(System.Net.HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html") };

    public static HttpResponseMessage Audio() =>
        new(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent([0xFF, 0xFB, 0x90, 0x00]) { Headers = { ContentType = new("audio/mpeg") } } };

    public static HttpResponseMessage NotFound() => new(System.Net.HttpStatusCode.NotFound);
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/StreamDiscoveryTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class StreamDiscoveryTests
{
    static readonly Uri Listen = new("https://www.burstradio.org/listen");

    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name));

    [Fact]
    public void Burst_listen_page_yields_the_radiojar_stream_first()
    {
        // Expected value observed on the live page on 2026-10-15: streamName 'nkyraup3h3nwv'.
        var candidates = StreamCandidates.FromHtml(Fixture("burst-listen-2026-10-15.html"), Listen);

        Assert.NotEmpty(candidates);
        Assert.Equal(new Uri("https://stream.rcs.revma.com/nkyraup3h3nwv"), candidates[0].Url);
        Assert.Equal("radiojar-widget", candidates[0].How);
    }

    [Fact]
    public void Audio_element_with_relative_src_is_resolved_against_the_page()
    {
        var c = StreamCandidates.FromHtml("""<audio controls src="/live.mp3"></audio>""", Listen);

        Assert.Equal(new Uri("https://www.burstradio.org/live.mp3"), c[0].Url);
        Assert.Equal("media-element", c[0].How);
    }

    [Fact]
    public void Page_without_a_stream_yields_nothing()
    {
        Assert.Empty(StreamCandidates.FromHtml("<html><body>hello</body></html>", Listen));
    }

    [Fact]
    public async Task Resolver_returns_first_candidate_that_answers_with_audio()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            "https://www.burstradio.org/listen" => StubHandler.Html(Fixture("burst-listen-2026-10-15.html")),
            "https://stream.rcs.revma.com/nkyraup3h3nwv" => StubHandler.Audio(),
            _ => StubHandler.NotFound(),
        });
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var r = await resolver.ResolveAsync(Listen, CancellationToken.None);

        Assert.Equal(new Uri("https://stream.rcs.revma.com/nkyraup3h3nwv"), r.Url);
        Assert.Equal("radiojar-widget", r.How);
    }

    [Fact]
    public async Task Resolver_skips_a_dead_candidate_and_uses_the_next()
    {
        const string page = """<script>x({ streamName: 'dead' })</script><audio src="https://example.org/live.mp3"></audio>""";
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            "https://www.burstradio.org/listen" => StubHandler.Html(page),
            "https://example.org/live.mp3" => StubHandler.Audio(),
            _ => StubHandler.NotFound(),
        });
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var r = await resolver.ResolveAsync(Listen, CancellationToken.None);

        Assert.Equal(new Uri("https://example.org/live.mp3"), r.Url);
    }

    [Fact]
    public async Task A_direct_stream_url_is_used_as_is()
    {
        var handler = new StubHandler(_ => StubHandler.Audio());
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var r = await resolver.ResolveAsync(new Uri("https://example.org/live"), CancellationToken.None);

        Assert.Equal(new Uri("https://example.org/live"), r.Url);
        Assert.Equal("direct-stream", r.How);
    }

    [Fact]
    public async Task Resolver_fails_clearly_when_nothing_works()
    {
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri == "https://www.burstradio.org/listen"
            ? StubHandler.Html("<html></html>")
            : StubHandler.NotFound());
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var ex = await Assert.ThrowsAsync<StreamNotFoundException>(() => resolver.ResolveAsync(Listen, CancellationToken.None));
        Assert.Contains("burstradio.org/listen", ex.Message);
    }

    [Fact]
    public async Task A_pls_playlist_is_followed_to_its_stream()
    {
        const string page = """<a href="x"></a><script>var u = "https://example.org/radio.pls";</script>""";
        var handler = new StubHandler(req => req.RequestUri!.AbsoluteUri switch
        {
            "https://www.burstradio.org/listen" => StubHandler.Html(page),
            "https://example.org/radio.pls" => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("[playlist]\nNumberOfEntries=1\nFile1=https://example.org:8000/live\n"),
            },
            "https://example.org:8000/live" => StubHandler.Audio(),
            _ => StubHandler.NotFound(),
        });
        var resolver = new StreamResolver(new HttpClient(handler), NullLogger<StreamResolver>.Instance);

        var r = await resolver.ResolveAsync(Listen, CancellationToken.None);

        Assert.Equal(new Uri("https://example.org:8000/live"), r.Url);
        Assert.EndsWith("+playlist", r.How);
    }
}
```

- [ ] **Step 3: Run to verify failure**

Run: `dotnet test --filter StreamDiscoveryTests`
Expected: build FAIL.

- [ ] **Step 4: Implement the candidates**

`src/WebAudioArchive.Core/Streams/StreamCandidates.cs`:
```csharp
using System.Text.RegularExpressions;

namespace WebAudioArchive.Core.Streams;

public sealed record StreamCandidate(Uri Url, string How);

public static partial class StreamCandidates
{
    /// <summary>Candidate stream URLs in a station page, best first. The order is: RadioJar widget, audio or source element, media URL in the page.</summary>
    public static IReadOnlyList<StreamCandidate> FromHtml(string html, Uri pageUrl)
    {
        var list = new List<StreamCandidate>();

        foreach (Match m in RadioJar().Matches(html))
            list.Add(new StreamCandidate(new Uri($"https://stream.rcs.revma.com/{m.Groups["n"].Value}"), "radiojar-widget"));

        foreach (Match m in MediaElement().Matches(html))
            if (Uri.TryCreate(pageUrl, m.Groups["u"].Value, out var u) && IsHttp(u))
                list.Add(new StreamCandidate(u, "media-element"));

        foreach (Match m in MediaUrl().Matches(html))
            if (Uri.TryCreate(m.Value, UriKind.Absolute, out var u) && IsHttp(u))
                list.Add(new StreamCandidate(u, "media-url-in-page"));

        return list.DistinctBy(c => c.Url).ToList();
    }

    static bool IsHttp(Uri u) => u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps;

    [GeneratedRegex(@"streamName\s*:\s*['""](?<n>[A-Za-z0-9_-]+)['""]")]
    private static partial Regex RadioJar();

    [GeneratedRegex(@"<(?:audio|source)\b[^>]*\bsrc\s*=\s*['""](?<u>[^'""]+)['""]", RegexOptions.IgnoreCase)]
    private static partial Regex MediaElement();

    [GeneratedRegex(@"https?://[^\s'""<>\\]+?\.(?:mp3|pls|m3u)(?:\?[^\s'""<>\\]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex MediaUrl();
}
```

- [ ] **Step 5: Implement the resolver**

`src/WebAudioArchive.Core/Streams/StreamResolver.cs`:
```csharp
using Microsoft.Extensions.Logging;

namespace WebAudioArchive.Core.Streams;

public sealed record ResolvedStream(Uri Url, string How);

public sealed class StreamNotFoundException(string message) : Exception(message);

public sealed class StreamResolver(HttpClient http, ILogger<StreamResolver> log)
{
    public async Task<ResolvedStream> ResolveAsync(Uri listenUrl, CancellationToken ct)
    {
        using var page = await GetHeadersAsync(listenUrl, listenUrl, ct);
        if (IsAudio(page)) return new ResolvedStream(listenUrl, "direct-stream");
        if (!page.IsSuccessStatusCode)
            throw new StreamNotFoundException($"{listenUrl} answered {(int)page.StatusCode}");

        var html = await page.Content.ReadAsStringAsync(ct);
        foreach (var candidate in StreamCandidates.FromHtml(html, listenUrl))
        {
            var url = candidate.Url;
            var how = candidate.How;
            if (IsPlaylist(url))
            {
                var inner = await ReadPlaylistAsync(url, listenUrl, ct);
                if (inner is null) continue;
                url = inner;
                how += "+playlist";
            }

            if (await ProbeAsync(url, listenUrl, ct)) return new ResolvedStream(url, how);
            log.LogInformation("Candidate {Url} ({How}) did not answer with audio", url, how);
        }

        throw new StreamNotFoundException($"No working audio stream found on {listenUrl}");
    }

    async Task<HttpResponseMessage> GetHeadersAsync(Uri url, Uri referer, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Referrer = referer;
        return await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
    }

    async Task<bool> ProbeAsync(Uri url, Uri referer, CancellationToken ct)
    {
        try
        {
            using var r = await GetHeadersAsync(url, referer, ct);
            return IsAudio(r);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    async Task<Uri?> ReadPlaylistAsync(Uri url, Uri referer, CancellationToken ct)
    {
        try
        {
            using var r = await GetHeadersAsync(url, referer, ct);
            if (!r.IsSuccessStatusCode) return null;
            var text = await r.Content.ReadAsStringAsync(ct);
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                var value = line.StartsWith("File1=", StringComparison.OrdinalIgnoreCase) ? line[6..] : line;
                if (Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")) return u;
            }

            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    static bool IsAudio(HttpResponseMessage r) =>
        r.IsSuccessStatusCode && r.Content.Headers.ContentType?.MediaType?.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true;

    static bool IsPlaylist(Uri u) =>
        u.AbsolutePath.EndsWith(".pls", StringComparison.OrdinalIgnoreCase) || u.AbsolutePath.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 6: Run to verify pass**

Run: `dotnet test --filter StreamDiscoveryTests`
Expected: all pass. The first test fails with a clear assertion if the saved fixture does not contain the stream name, which would mean the fixture was not saved in the preparation step; re-save it with `Invoke-WebRequest https://www.burstradio.org/listen -UserAgent "Mozilla/5.0"`.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "Add stream discovery from station pages"
```

---

### Task 6: Stream source, fake station and session loop

**Files:**
- Create: `src/WebAudioArchive.Core/Streams/HttpStreamSource.cs`
- Create: `src/WebAudioArchive.Core/Recording/IClock.cs`, `IFrameSink.cs`, `StationHealth.cs`, `StationSession.cs`
- Create: `tests/WebAudioArchive.Tests/Support/FakeStation.cs`, `ManualClock.cs`, `RecordingSink.cs`
- Test: `tests/WebAudioArchive.Tests/StreamSourceTests.cs`, `tests/WebAudioArchive.Tests/StationSessionTests.cs`

**Interfaces:**
- Consumes: `Mp3FrameSplitter`, `FrameHandler`, `Mp3FrameHeader` (Task 2); `IcyDemuxer`, `AudioHandler`, `IcyTitle` (Task 3); `StreamResolver`, `ResolvedStream` (Task 5).
- Produces:
  - `sealed record StreamInfo(Uri Url, string ContentType, int IcyMetaInt, IReadOnlyDictionary<string, string> IcyHeaders, string HowFound)`
  - `sealed class OpenedStream : IAsyncDisposable { required Stream Body; required StreamInfo Info; }`
  - `interface IStreamSource { Task<OpenedStream> OpenAsync(CancellationToken ct); }`
  - `sealed class HttpStreamSource(HttpClient http, StreamResolver resolver, Uri listenUrl, ILogger<HttpStreamSource> log) : IStreamSource`
  - `sealed class UnsupportedStreamException(string message) : Exception(message)`
  - `interface IClock { DateTimeOffset UtcNow { get; } }`, `sealed class SystemClock : IClock`
  - `sealed record GapRecord(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason)`
  - `interface IFrameSink { void OnConnected(StreamInfo info, DateTimeOffset utc); void OnFrame(ReadOnlySpan<byte> frame, in Mp3FrameHeader header, DateTimeOffset utc); void OnTitle(string title, DateTimeOffset utc); void OnGap(GapRecord gap); }`
  - `sealed class StationHealth { bool Connected; DateTimeOffset? LastByteUtc; int Reconnects; void NoteByte(DateTimeOffset); void NoteReconnect(); }`
  - `sealed record SessionOptions(TimeSpan StallTimeout, TimeSpan BackoffInitial, TimeSpan BackoffMax)` with defaults 30 s, 1 s, 30 s.
  - `sealed class StationSession(IStreamSource, IFrameSink, IClock, StationHealth, ILogger<StationSession>, SessionOptions? = null) { Task RunAsync(CancellationToken ct); }`
  - Test helpers: `FakeStation`, `ManualClock`, `RecordingSink`.

- [ ] **Step 1: Write the test helpers**

`tests/WebAudioArchive.Tests/Support/ManualClock.cs`:
```csharp
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests.Support;

public sealed class ManualClock(DateTimeOffset start) : IClock
{
    long _ticks = start.UtcTicks;
    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);
    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    public void Set(DateTimeOffset to) => Interlocked.Exchange(ref _ticks, to.UtcTicks);
}
```

`tests/WebAudioArchive.Tests/Support/RecordingSink.cs`:
```csharp
using System.Collections.Concurrent;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Tests.Support;

public sealed class RecordingSink : IFrameSink
{
    public ConcurrentQueue<byte[]> Frames { get; } = new();
    public ConcurrentQueue<string> Titles { get; } = new();
    public ConcurrentQueue<GapRecord> Gaps { get; } = new();
    public int Connections;

    public void OnConnected(StreamInfo info, DateTimeOffset utc) => Interlocked.Increment(ref Connections);
    public void OnFrame(ReadOnlySpan<byte> frame, in Mp3FrameHeader header, DateTimeOffset utc) => Frames.Enqueue(frame.ToArray());
    public void OnTitle(string title, DateTimeOffset utc) => Titles.Enqueue(title);
    public void OnGap(GapRecord gap) => Gaps.Enqueue(gap);
}
```

`tests/WebAudioArchive.Tests/Support/FakeStation.cs`:
```csharp
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace WebAudioArchive.Tests.Support;

/// <summary>What one connection to /stream does. The audio is sent with ICY metadata interleaved when MetaInt > 0.</summary>
public sealed class StreamPlan
{
    public byte[] Audio { get; init; } = [];
    public int MetaInt { get; init; } = 16000;
    public Func<int, string?> TitleForBlock { get; init; } = _ => null;
    public Task? Gate { get; init; }            // wait for this before sending anything
    public bool DropAfterSend { get; init; }    // abort the connection after sending
    public bool HoldOpen { get; init; } = true; // keep the connection open and silent after sending
    public int Status { get; init; } = 200;
}

public sealed class FakeStation : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly ConcurrentDictionary<string, int> _hits = new();
    int _streamConnections;

    public Func<int, StreamPlan> Plan { get; set; } = _ => new StreamPlan();
    public Uri BaseUrl { get; private set; } = null!;
    public Uri ListenUrl => new(BaseUrl, "/listen");
    public int Hits(string path) => _hits.GetValueOrDefault(path);
    public int StreamConnections => Volatile.Read(ref _streamConnections);

    FakeStation(WebApplication app) => _app = app;

    public static async Task<FakeStation> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var station = new FakeStation(app);

        app.MapGet("/listen", ctx =>
        {
            station.Count("/listen");
            ctx.Response.ContentType = "text/html";
            return ctx.Response.WriteAsync("""<html><body><audio src="/redirect-stream"></audio></body></html>""");
        });

        app.MapGet("/redirect-stream", ctx =>
        {
            var n = station.Count("/redirect-stream");
            ctx.Response.Redirect($"/stream?token={n}");
            return Task.CompletedTask;
        });

        app.MapGet("/stream", async ctx =>
        {
            station.Count("/stream");
            if (!ctx.Request.Headers.ContainsKey("Icy-MetaData"))
            {
                // The resolver probes without Icy-MetaData: answer headers only, and do not use up a plan.
                ctx.Response.ContentType = "audio/mpeg";
                return;
            }

            var index = Interlocked.Increment(ref station._streamConnections) - 1;
            var plan = station.Plan(index);
            if (plan.Status != 200)
            {
                ctx.Response.StatusCode = plan.Status;
                return;
            }

            ctx.Response.ContentType = "audio/mpeg";
            ctx.Response.Headers["icy-name"] = "Fake FM";
            ctx.Response.Headers["icy-genre"] = "Test";
            if (plan.MetaInt > 0) ctx.Response.Headers["icy-metaint"] = plan.MetaInt.ToString();
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);

            if (plan.Gate is not null) await plan.Gate.WaitAsync(ctx.RequestAborted);

            var wire = plan.MetaInt > 0 ? IcyTestData.Build(plan.Audio, plan.MetaInt, plan.TitleForBlock) : plan.Audio;
            for (var i = 0; i < wire.Length; i += 1000)
            {
                await ctx.Response.Body.WriteAsync(wire.AsMemory(i, Math.Min(1000, wire.Length - i)), ctx.RequestAborted);
                await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
            }

            if (plan.DropAfterSend)
            {
                // Give the client time to drain its socket: an RST discards unread data on Windows.
                await Task.Delay(300, ctx.RequestAborted);
                ctx.Abort();
                return;
            }

            if (plan.HoldOpen) await Task.Delay(Timeout.Infinite, ctx.RequestAborted).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        });

        await app.StartAsync();
        station.BaseUrl = new Uri(app.Urls.First());
        return station;
    }

    int Count(string path) => _hits.AddOrUpdate(path, 1, (_, v) => v + 1);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
```

- [ ] **Step 2: Write the failing source tests**

`tests/WebAudioArchive.Tests/StreamSourceTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class StreamSourceTests
{
    static HttpStreamSource Source(FakeStation station)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        return new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance), station.ListenUrl, NullLogger<HttpStreamSource>.Instance);
    }

    [Fact]
    public async Task Open_reports_stream_details_and_follows_the_redirect()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = _ => new StreamPlan { Audio = Mp3TestData.Frames(2), MetaInt = 16000 };

        await using var opened = await Source(station).OpenAsync(CancellationToken.None);

        Assert.Equal(16000, opened.Info.IcyMetaInt);
        Assert.Equal("audio/mpeg", opened.Info.ContentType);
        Assert.Equal("Fake FM", opened.Info.IcyHeaders["icy-name"]);
        Assert.Equal("media-element", opened.Info.HowFound);
    }

    [Fact]
    public async Task Each_open_follows_the_redirect_afresh()
    {
        // Burst redirects /<name> to a short-lived tokenised URL, so the final URL must never be cached.
        await using var station = await FakeStation.StartAsync();
        station.Plan = _ => new StreamPlan { Audio = Mp3TestData.Frames(2) };
        var source = Source(station);

        await using (await source.OpenAsync(CancellationToken.None)) { }
        await using (await source.OpenAsync(CancellationToken.None)) { }

        Assert.Equal(1, station.Hits("/listen"));            // resolved once
        Assert.Equal(3, station.Hits("/redirect-stream"));   // 1 probe + 2 opens
    }

    [Fact]
    public async Task Non_mp3_streams_are_refused_clearly()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("audio/aac") } },
        });
        var http = new HttpClient(handler);
        var source = new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance),
            new Uri("https://example.org/live"), NullLogger<HttpStreamSource>.Instance);

        var ex = await Assert.ThrowsAsync<UnsupportedStreamException>(() => source.OpenAsync(CancellationToken.None));
        Assert.Contains("audio/aac", ex.Message);
    }
}
```

- [ ] **Step 3: Write the failing session tests**

`tests/WebAudioArchive.Tests/StationSessionTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public class StationSessionTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 15, 10, 30, 0, TimeSpan.Zero);
    static readonly SessionOptions Fast = new(TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100));

    static StationSession NewSession(FakeStation station, RecordingSink sink, ManualClock clock, StationHealth health)
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var source = new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance), station.ListenUrl, NullLogger<HttpStreamSource>.Instance);
        return new StationSession(source, sink, clock, health, NullLogger<StationSession>.Instance, Fast);
    }

    static async Task WaitFor(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task Frames_titles_and_metadata_arrive_and_icy_blocks_are_removed()
    {
        await using var station = await FakeStation.StartAsync();
        var audio = Mp3TestData.Frames(100);
        station.Plan = _ => new StreamPlan { Audio = audio, MetaInt = 4000, TitleForBlock = b => b == 1 ? "Artist - Song" : null };
        var sink = new RecordingSink();
        var health = new StationHealth();
        var session = NewSession(station, sink, new ManualClock(T0), health);
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.Frames.Count >= 100, "100 frames");
        cts.Cancel();
        await run;

        Assert.Equal(audio, sink.Frames.SelectMany(f => f).ToArray());
        Assert.Equal(["Artist - Song"], sink.Titles.ToArray());
        Assert.True(health.LastByteUtc is not null);
        Assert.False(health.Connected);
    }

    [Fact]
    public async Task Dropped_connection_is_reconnected_and_the_gap_recorded()
    {
        await using var station = await FakeStation.StartAsync();
        var gate = new TaskCompletionSource();
        station.Plan = n => n == 0
            ? new StreamPlan { Audio = Mp3TestData.Frames(50), DropAfterSend = true }
            : new StreamPlan { Audio = Mp3TestData.Frames(50, startSeq: 50), Gate = gate.Task };
        var clock = new ManualClock(T0);
        var sink = new RecordingSink();
        var session = NewSession(station, sink, clock, new StationHealth());
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.Frames.Count >= 50, "first 50 frames");
        await WaitFor(() => station.StreamConnections >= 2, "the reconnect");
        clock.Advance(TimeSpan.FromSeconds(7));
        gate.SetResult();
        await WaitFor(() => sink.Frames.Count >= 100, "second 50 frames");
        cts.Cancel();
        await run;

        var gap = Assert.Single(sink.Gaps);
        Assert.Equal(T0, gap.StartUtc);
        Assert.Equal(T0.AddSeconds(7), gap.EndUtc);
        Assert.False(string.IsNullOrWhiteSpace(gap.Reason));
        Assert.Equal(Mp3TestData.Frames(100), sink.Frames.SelectMany(f => f).ToArray());
        Assert.Equal(2, sink.Connections);
    }

    [Fact]
    public async Task Stalled_connection_is_dropped_and_reconnected()
    {
        // The server keeps the socket open and sends nothing: only a read timeout can notice.
        await using var station = await FakeStation.StartAsync();
        station.Plan = n => n == 0
            ? new StreamPlan { Audio = Mp3TestData.Frames(10), HoldOpen = true }
            : new StreamPlan { Audio = Mp3TestData.Frames(10, startSeq: 10), HoldOpen = true };
        var sink = new RecordingSink();
        var health = new StationHealth();
        var session = NewSession(station, sink, new ManualClock(T0), health);
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => station.StreamConnections >= 2 && sink.Frames.Count >= 20, "the reconnect after a stall");
        cts.Cancel();
        await run;

        Assert.True(health.Reconnects >= 1);
        Assert.Equal(Mp3TestData.Frames(20), sink.Frames.SelectMany(f => f).ToArray());
    }

    [Fact]
    public async Task A_failing_server_is_retried_without_a_gap_before_any_audio()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = n => n < 2
            ? new StreamPlan { Status = 503 }
            : new StreamPlan { Audio = Mp3TestData.Frames(10) };
        var sink = new RecordingSink();
        var session = NewSession(station, sink, new ManualClock(T0), new StationHealth());
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await WaitFor(() => sink.Frames.Count >= 10, "audio after two failures");
        cts.Cancel();
        await run;

        Assert.Empty(sink.Gaps);
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test --filter "StreamSourceTests|StationSessionTests"`
Expected: build FAIL (types not defined).

- [ ] **Step 5: Implement the recording contracts**

`src/WebAudioArchive.Core/Recording/IClock.cs`:
```csharp
namespace WebAudioArchive.Core.Recording;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

`src/WebAudioArchive.Core/Recording/IFrameSink.cs`:
```csharp
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Core.Recording;

public sealed record GapRecord(DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Reason);

/// <summary>Receives what one station's stream produces. Calls come from one thread, in order.</summary>
public interface IFrameSink
{
    void OnConnected(StreamInfo info, DateTimeOffset utc);

    /// <summary>The span is valid only during the call.</summary>
    void OnFrame(ReadOnlySpan<byte> frame, in Mp3FrameHeader header, DateTimeOffset utc);

    void OnTitle(string title, DateTimeOffset utc);

    /// <summary>Called when audio resumes after a break, just before the first frame that follows it.</summary>
    void OnGap(GapRecord gap);
}
```

`src/WebAudioArchive.Core/Recording/StationHealth.cs`:
```csharp
namespace WebAudioArchive.Core.Recording;

public sealed class StationHealth
{
    long _lastByteTicks;
    int _reconnects;
    volatile bool _connected;

    public bool Connected
    {
        get => _connected;
        set => _connected = value;
    }

    public DateTimeOffset? LastByteUtc
    {
        get
        {
            var t = Interlocked.Read(ref _lastByteTicks);
            return t == 0 ? null : new DateTimeOffset(t, TimeSpan.Zero);
        }
    }

    public int Reconnects => Volatile.Read(ref _reconnects);

    public void NoteByte(DateTimeOffset utc) => Interlocked.Exchange(ref _lastByteTicks, utc.UtcTicks);

    public void NoteReconnect() => Interlocked.Increment(ref _reconnects);
}
```

- [ ] **Step 6: Implement the stream source**

`src/WebAudioArchive.Core/Streams/HttpStreamSource.cs`:
```csharp
using Microsoft.Extensions.Logging;

namespace WebAudioArchive.Core.Streams;

public sealed record StreamInfo(
    Uri Url,
    string ContentType,
    int IcyMetaInt,
    IReadOnlyDictionary<string, string> IcyHeaders,
    string HowFound);

public sealed class OpenedStream : IAsyncDisposable
{
    HttpResponseMessage? _response;

    public required Stream Body { get; init; }
    public required StreamInfo Info { get; init; }

    internal void Attach(HttpResponseMessage response) => _response = response;

    public async ValueTask DisposeAsync()
    {
        await Body.DisposeAsync();
        _response?.Dispose();
    }
}

public interface IStreamSource
{
    Task<OpenedStream> OpenAsync(CancellationToken ct);
}

public sealed class UnsupportedStreamException(string message) : Exception(message);

public sealed class HttpStreamSource(HttpClient http, StreamResolver resolver, Uri listenUrl, ILogger<HttpStreamSource> log)
    : IStreamSource
{
    const int ReResolveAfterFailures = 3;
    static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(20);

    ResolvedStream? _resolved;
    int _failures;

    public async Task<OpenedStream> OpenAsync(CancellationToken ct)
    {
        try
        {
            if (_resolved is null || _failures >= ReResolveAfterFailures)
            {
                _resolved = await resolver.ResolveAsync(listenUrl, ct);
                _failures = 0;
                log.LogInformation("Resolved {Listen} to {Stream} ({How})", listenUrl, _resolved.Url, _resolved.How);
            }

            var opened = await OpenCoreAsync(_resolved, ct);
            _failures = 0;
            return opened;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _failures++;
            throw;
        }
    }

    async Task<OpenedStream> OpenCoreAsync(ResolvedStream resolved, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(HeaderTimeout);

        // The resolved URL is requested afresh each time: stations redirect to short-lived tokenised URLs.
        var req = new HttpRequestMessage(HttpMethod.Get, resolved.Url);
        req.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        req.Headers.Referrer = listenUrl;

        var response = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        try
        {
            response.EnsureSuccessStatusCode();
            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedStreamException($"Only MP3 (audio/mpeg) streams are supported; {resolved.Url} sent '{contentType}'");

            var icy = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var h in response.Headers)
                if (h.Key.StartsWith("icy-", StringComparison.OrdinalIgnoreCase)) icy[h.Key.ToLowerInvariant()] = string.Join(", ", h.Value);
            var metaInt = icy.TryGetValue("icy-metaint", out var mi) && int.TryParse(mi, out var parsed) ? parsed : 0;

            var body = await response.Content.ReadAsStreamAsync(ct);
            var info = new StreamInfo(response.RequestMessage?.RequestUri ?? resolved.Url, contentType, metaInt, icy, resolved.How);
            var opened = new OpenedStream { Body = body, Info = info };
            opened.Attach(response);
            return opened;
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }
}
```
Note: `StreamInfo.Url` records the final (tokenised) URL for diagnostics only; it is never reused for connecting.

- [ ] **Step 7: Implement the session**

`src/WebAudioArchive.Core/Recording/StationSession.cs`:
```csharp
using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Icy;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Core.Recording;

public sealed record SessionOptions(TimeSpan StallTimeout, TimeSpan BackoffInitial, TimeSpan BackoffMax)
{
    public SessionOptions() : this(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)) { }
}

/// <summary>Holds one always-on connection to a station, reconnecting with backoff and recording every gap.</summary>
public sealed class StationSession(
    IStreamSource source,
    IFrameSink sink,
    IClock clock,
    StationHealth health,
    ILogger<StationSession> log,
    SessionOptions? options = null)
{
    readonly SessionOptions _options = options ?? new SessionOptions();
    DateTimeOffset? _lastDataUtc;
    DateTimeOffset? _gapStart;
    string _gapReason = "";
    bool _gotData;

    public async Task RunAsync(CancellationToken ct)
    {
        var backoff = _options.BackoffInitial;
        while (!ct.IsCancellationRequested)
        {
            _gotData = false;
            var reason = "disconnected";
            try
            {
                await using var opened = await source.OpenAsync(ct).ConfigureAwait(false);
                sink.OnConnected(opened.Info, clock.UtcNow);
                health.Connected = true;
                await PumpAsync(opened, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                reason = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
                log.LogWarning("Stream connection ended: {Reason}", reason);
            }

            health.Connected = false;
            health.NoteReconnect();
            if (_lastDataUtc is { } last && _gapStart is null)
            {
                _gapStart = last;
                _gapReason = reason;
            }

            var delay = _gotData ? _options.BackoffInitial : backoff;
            backoff = _gotData ? _options.BackoffInitial : TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, _options.BackoffMax.Ticks));
            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        health.Connected = false;
    }

    async Task PumpAsync(OpenedStream opened, CancellationToken ct)
    {
        var icy = new IcyDemuxer(opened.Info.IcyMetaInt);
        var splitter = new Mp3FrameSplitter();
        var buffer = new byte[16 * 1024];
        var now = clock.UtcNow;

        FrameHandler onFrame = (ReadOnlySpan<byte> frame, in Mp3FrameHeader header) => sink.OnFrame(frame, in header, now);
        AudioHandler onAudio = audio => splitter.Push(audio, onFrame);
        Action<IcyTitle> onTitle = t => sink.OnTitle(t.Title, now);

        try
        {
            while (true)
            {
                var n = await ReadWithStallTimeoutAsync(opened.Body, buffer, ct).ConfigureAwait(false);
                if (n == 0) throw new IOException("stream ended by server");

                now = clock.UtcNow;
                if (!_gotData)
                {
                    _gotData = true;
                    if (_gapStart is { } start)
                    {
                        sink.OnGap(new GapRecord(start, now, _gapReason));
                        _gapStart = null;
                    }
                }

                _lastDataUtc = now;
                health.NoteByte(now);
                icy.Push(buffer.AsSpan(0, n), onAudio, onTitle);
            }
        }
        finally
        {
            splitter.Complete(onFrame);
        }
    }

    async Task<int> ReadWithStallTimeoutAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(_options.StallTimeout);
        try
        {
            return await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"no data for {_options.StallTimeout.TotalSeconds:0.##}s");
        }
    }
}
```

- [ ] **Step 8: Run to verify pass**

Run: `dotnet test --filter "StreamSourceTests|StationSessionTests"`
Expected: all pass. `Each_open_follows_the_redirect_afresh` asserts 3 hits on `/redirect-stream` (one probe during resolution, then two opens). If the Kestrel startup in `FakeStation` fails to find a port in `app.Urls`, print `app.Urls` and confirm `UseUrls("http://127.0.0.1:0")` was applied before `Build()`.

- [ ] **Step 9: Commit**

```powershell
git add -A
git commit -m "Add stream source and always-on session with reconnect and gap recording"
```

---

### Task 7: Master recorder

**Files:**
- Create: `src/WebAudioArchive.Core/Recording/SegmentMeta.cs`, `src/WebAudioArchive.Core/Recording/MasterRecorder.cs`
- Test: `tests/WebAudioArchive.Tests/MasterRecorderTests.cs`, `tests/WebAudioArchive.Tests/RecorderEndToEndTests.cs`

**Interfaces:**
- Consumes: `IFrameSink`, `GapRecord`, `IClock` (Task 6); `Mp3FrameSplitter`, `Mp3FrameHeader` (Task 2); `StreamInfo` (Task 6).
- Produces:
  - Records `SegmentMeta`, `SegmentFormat`, `SegmentStream`, `SegmentTitle`, `SegmentGap` (JSON shape in the code below) and `static class SegmentMetaStore { void Write(string jsonPath, SegmentMeta meta); SegmentMeta? Read(string jsonPath); }`
  - `sealed class MasterRecorder(string dataRoot, string stationId, string instanceId, IClock clock, ILogger<MasterRecorder> log) : IFrameSink, IDisposable { string MasterDir; long FramesWritten; DateTimeOffset? CurrentSegmentStartUtc; void RecoverCrashedSegments(); }`
  - Layout: `<dataRoot>/<stationId>/master/<yyyy>/<MM-dd>/<HHmmss>[-n].mp3` plus `.json`; `.mp3.part` while open.
  - `EndedBy` values: `hour-boundary`, `shutdown`, `crash`.

- [ ] **Step 1: Write the failing recorder tests**

`tests/WebAudioArchive.Tests/MasterRecorderTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class MasterRecorderTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-tests-" + Guid.NewGuid().ToString("N"));
    readonly ManualClock _clock = new(new DateTimeOffset(2026, 10, 15, 10, 30, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    MasterRecorder NewRecorder() => new(_root, "burst", "test-instance", _clock, NullLogger<MasterRecorder>.Instance);

    static void Write(MasterRecorder r, int seq, DateTimeOffset at)
    {
        var frame = Mp3TestData.Frame(seq);
        Mp3FrameHeader.TryParse(frame, out var header);
        r.OnFrame(frame, in header, at);
    }

    string MasterDir => Path.Combine(_root, "burst", "master");

    IEnumerable<string> Files(string pattern) =>
        Directory.Exists(MasterDir)
            ? Directory.EnumerateFiles(MasterDir, pattern, SearchOption.AllDirectories).Order()
            : [];

    [Fact]
    public void Writes_a_part_file_then_finalises_with_a_sidecar()
    {
        var t = _clock.UtcNow;
        var r = NewRecorder();
        Write(r, 0, t);
        Write(r, 1, t.AddSeconds(1));

        var part = Assert.Single(Files("*.part"));
        Assert.EndsWith(Path.Combine("2026", "10-15", "103000.mp3.part"), part);

        r.Dispose();

        Assert.Empty(Files("*.part"));
        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.Equal(Mp3TestData.Frames(2), File.ReadAllBytes(mp3));

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
        Assert.Equal("burst", meta.Station);
        Assert.Equal("test-instance", meta.Instance);
        Assert.Equal(2, meta.Frames);
        Assert.Equal(Mp3TestData.Frames(2).Length, meta.Bytes);
        Assert.Equal("shutdown", meta.EndedBy);
        Assert.Equal(t, meta.StartUtc);
        Assert.Equal(t.AddSeconds(1), meta.EndUtc);
        Assert.Equal(2 * 1152.0 / 44100, meta.AudioSeconds, 6);
        Assert.Equal(new SegmentFormat("MPEG-1 Layer III", 128, 44100, 2), meta.Format);
    }

    [Fact]
    public void Rolls_at_the_hour_and_segments_concatenate_to_the_input()
    {
        var r = NewRecorder();
        Write(r, 0, new DateTimeOffset(2026, 10, 15, 10, 59, 59, 900, TimeSpan.Zero));
        Write(r, 1, new DateTimeOffset(2026, 10, 15, 11, 0, 0, 100, TimeSpan.Zero));
        Write(r, 2, new DateTimeOffset(2026, 10, 15, 11, 0, 1, 0, TimeSpan.Zero));
        r.Dispose();

        var mp3s = Files("*.mp3").ToList();
        Assert.Equal(2, mp3s.Count);
        Assert.EndsWith("105959.mp3", mp3s[0]);
        Assert.EndsWith("110000.mp3", mp3s[1]);
        Assert.Equal(Mp3TestData.Frames(3), mp3s.SelectMany(File.ReadAllBytes).ToArray());
        Assert.Equal("hour-boundary", SegmentMetaStore.Read(Path.ChangeExtension(mp3s[0], ".json"))!.EndedBy);
        Assert.Equal("shutdown", SegmentMetaStore.Read(Path.ChangeExtension(mp3s[1], ".json"))!.EndedBy);
    }

    [Fact]
    public void Rolls_at_utc_midnight_into_new_day_folder()
    {
        var r = NewRecorder();
        Write(r, 0, new DateTimeOffset(2026, 10, 15, 23, 59, 59, TimeSpan.Zero));
        Write(r, 1, new DateTimeOffset(2026, 10, 16, 0, 0, 1, TimeSpan.Zero));
        r.Dispose();

        var mp3s = Files("*.mp3").ToList();
        Assert.Equal(2, mp3s.Count);
        Assert.EndsWith(Path.Combine("10-15", "235959.mp3"), mp3s[0]);
        Assert.EndsWith(Path.Combine("10-16", "000001.mp3"), mp3s[1]);
    }

    [Fact]
    public void Clock_back_hour_does_not_collide()
    {
        // 25 Oct 2026: the UK clocks go back, so local 01:30 happens twice. UTC names stay distinct.
        var r = NewRecorder();
        Write(r, 0, new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero)); // 01:30 BST
        Write(r, 1, new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero)); // 01:30 GMT
        r.Dispose();

        var mp3s = Files("*.mp3").ToList();
        Assert.Equal(2, mp3s.Count);
        Assert.EndsWith("003000.mp3", mp3s[0]);
        Assert.EndsWith("013000.mp3", mp3s[1]);
    }

    [Fact]
    public void Restart_in_same_hour_makes_a_new_segment_and_in_same_second_a_distinct_file()
    {
        var t = _clock.UtcNow;
        var first = NewRecorder();
        Write(first, 0, t);
        first.Dispose();

        var second = NewRecorder();
        Write(second, 1, t.AddMinutes(15));
        second.Dispose();

        var third = NewRecorder();
        Write(third, 2, t); // exactly the first segment's start second
        third.Dispose();

        var names = Files("*.mp3").Select(Path.GetFileName).ToList();
        Assert.Equal(["103000-1.mp3", "103000.mp3", "104500.mp3"], names);
    }

    [Fact]
    public void Gap_and_titles_are_attached_to_the_segment_that_follows()
    {
        var t = _clock.UtcNow;
        var r = NewRecorder();
        r.OnGap(new GapRecord(t.AddSeconds(-30), t, "dropped"));
        Write(r, 0, t);
        r.OnTitle("Artist - Song", t.AddSeconds(1));
        Write(r, 1, t.AddSeconds(1));
        r.Dispose();

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(Files("*.mp3").Single(), ".json"))!;
        var gap = Assert.Single(meta.Gaps);
        Assert.Equal(30, gap.Seconds, 3);
        Assert.Equal("dropped", gap.Reason);
        var title = Assert.Single(meta.Titles);
        Assert.Equal("Artist - Song", title.Title);
        Assert.Equal(Mp3TestData.Frame(0).Length, title.AtFileByte);
    }

    [Fact]
    public void Crash_part_file_is_recovered_and_truncated_to_whole_frames()
    {
        var dir = Path.Combine(MasterDir, "2026", "10-15");
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, "103045.mp3.part");
        File.WriteAllBytes(part, Mp3TestData.Frames(10).Concat(Mp3TestData.Frame(10).Take(100)).ToArray());

        var r = NewRecorder();
        r.RecoverCrashedSegments();
        r.Dispose();

        Assert.Empty(Files("*.part"));
        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.Equal(Mp3TestData.Frames(10), File.ReadAllBytes(mp3));
        var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
        Assert.Equal("crash", meta.EndedBy);
        Assert.Equal(10, meta.Frames);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 10, 30, 45, TimeSpan.Zero), meta.StartUtc);
    }

    [Fact]
    public void Empty_crash_part_file_is_removed()
    {
        var dir = Path.Combine(MasterDir, "2026", "10-15");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "103045.mp3.part"), []);

        var r = NewRecorder();
        r.RecoverCrashedSegments();
        r.Dispose();

        Assert.Empty(Files("*.part"));
        Assert.Empty(Files("*.mp3"));
    }

    [Fact]
    public void Frames_written_counts_frames()
    {
        var r = NewRecorder();
        Write(r, 0, _clock.UtcNow);
        Write(r, 1, _clock.UtcNow);
        Assert.Equal(2, r.FramesWritten);
        r.Dispose();
    }

    [Fact]
    public void Failed_write_is_truncated_to_whole_frames_and_later_frames_append_cleanly()
    {
        FaultyStream? faulty = null;
        var r = new MasterRecorder(_root, "burst", "test-instance", _clock, NullLogger<MasterRecorder>.Instance)
        {
            OpenPartStream = p => faulty = new FaultyStream(new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024)),
        };
        var t = _clock.UtcNow;
        Write(r, 0, t);
        faulty!.FailNextWrite = true;
        Assert.Throws<IOException>(() => Write(r, 1, t.AddSeconds(1)));
        Write(r, 2, t.AddSeconds(2));
        r.Dispose();

        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.Equal(Mp3TestData.Frame(0).Concat(Mp3TestData.Frame(2)).ToArray(), File.ReadAllBytes(mp3));
        var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
        Assert.Equal(2, meta.Frames);
        Assert.Equal(Mp3TestData.Frame(0).Length + Mp3TestData.Frame(2).Length, meta.Bytes);
        Assert.Equal(2, r.FramesWritten);
    }

    [Fact]
    public void Crash_recovery_keeps_the_part_files_last_write_time_as_the_end()
    {
        var dir = Path.Combine(MasterDir, "2026", "10-15");
        Directory.CreateDirectory(dir);
        var part = Path.Combine(dir, "103045.mp3.part");
        File.WriteAllBytes(part, Mp3TestData.Frames(10).Concat(Mp3TestData.Frame(10).Take(100)).ToArray());
        var lastWrite = new DateTime(2026, 10, 15, 10, 41, 7, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(part, lastWrite);

        var r = NewRecorder();
        r.RecoverCrashedSegments();
        r.Dispose();

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(Assert.Single(Files("*.mp3")), ".json"))!;
        Assert.Equal(new DateTimeOffset(lastWrite, TimeSpan.Zero), meta.EndUtc);
    }

    [Fact]
    public void One_bad_part_file_does_not_stop_recovery_of_the_others()
    {
        var dir = Path.Combine(MasterDir, "2026", "10-15");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "oddname.mp3.part"), Mp3TestData.Frames(3));
        File.WriteAllBytes(Path.Combine(dir, "103045.mp3.part"), Mp3TestData.Frames(4));

        var r = NewRecorder();
        r.RecoverCrashedSegments();
        r.Dispose();

        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.EndsWith("103045.mp3", mp3);
        Assert.Equal(Mp3TestData.Frames(4), File.ReadAllBytes(mp3));
        Assert.Equal("oddname.mp3.part", Path.GetFileName(Assert.Single(Files("*.part"))));
    }

    [Fact]
    public void Failed_close_leaves_a_part_file_that_the_next_start_recovers()
    {
        var r = new MasterRecorder(_root, "burst", "test-instance", _clock, NullLogger<MasterRecorder>.Instance)
        {
            OpenPartStream = p => new FaultyStream(new FileStream(p, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024)) { ThrowOnDispose = true },
        };
        Write(r, 0, _clock.UtcNow);
        Write(r, 1, _clock.UtcNow.AddSeconds(1));
        r.Dispose(); // must not throw

        Assert.Empty(Files("*.mp3"));
        Assert.Single(Files("*.part"));

        var next = NewRecorder();
        next.RecoverCrashedSegments();
        next.Dispose();

        var mp3 = Assert.Single(Files("*.mp3"));
        Assert.Equal(Mp3TestData.Frames(2), File.ReadAllBytes(mp3));
        Assert.Equal("crash", SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!.EndedBy);
    }

    [Fact]
    public void Failed_rename_does_not_throw_and_keeps_the_part_file()
    {
        var r = NewRecorder();
        Write(r, 0, _clock.UtcNow);
        var final = Path.Combine(MasterDir, "2026", "10-15", "103000.mp3");
        File.WriteAllBytes(final, [1]); // makes the rename fail
        r.Dispose(); // must not throw

        Assert.Single(Files("*.part"));
        Assert.Empty(Files("*.json"));
    }

    sealed class FaultyStream(Stream inner) : Stream
    {
        public bool FailNextWrite { get; set; }
        public bool ThrowOnDispose { get; init; }

        public override bool CanRead => false;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (FailNextWrite)
            {
                FailNextWrite = false;
                inner.Write(buffer[..(buffer.Length / 2)]); // a torn write
                throw new IOException("disk full");
            }

            inner.Write(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
            if (disposing && ThrowOnDispose) throw new IOException("close failed");
        }
    }
}
```

- [ ] **Step 2: Write the failing coverage tests**

Append to the same test project a new file `tests/WebAudioArchive.Tests/CoverageReaderTests.cs` (the reader is implemented in Task 8, so this test fails to compile until then; write it now and run it in Task 8):

```csharp
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Tests;

public sealed class CoverageReaderTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "waa-cov-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    static DateTimeOffset At(int h, int m, int s = 0) => new(2026, 10, 15, h, m, s, TimeSpan.Zero);

    void Segment(string name, DateTimeOffset start, DateTimeOffset end, params SegmentGap[] gaps)
    {
        var day = Path.Combine(_dir, start.ToString("yyyy"), start.ToString("MM-dd"));
        Directory.CreateDirectory(day);
        SegmentMetaStore.Write(Path.Combine(day, name + ".json"), new SegmentMeta(
            1, "i", "burst", name + ".mp3", start, end, 0, 0, (end - start).TotalSeconds, null, null, [], gaps, "hour-boundary"));
    }

    [Fact]
    public void Coverage_is_segment_time_minus_gaps_inside_the_window()
    {
        Segment("100000", At(10, 0), At(10, 30));
        Segment("103500", At(10, 35), At(11, 0), new SegmentGap(At(10, 40), At(10, 41), 60, "x"));

        var c = CoverageReader.Read(_dir, now: At(11, 0), window: TimeSpan.FromHours(1));

        Assert.Equal(1800 + 1500 - 60, c.CoveredSeconds, 3);
        Assert.Equal(3240.0 / 3600, c.Fraction, 6);
    }

    [Fact]
    public void Coverage_clips_segments_to_the_window()
    {
        Segment("100000", At(10, 0), At(10, 30));
        Segment("103500", At(10, 35), At(11, 0), new SegmentGap(At(10, 40), At(10, 41), 60, "x"));

        var c = CoverageReader.Read(_dir, now: At(11, 0), window: TimeSpan.FromMinutes(30));

        Assert.Equal(1440, c.CoveredSeconds, 3);
        Assert.Equal(0.8, c.Fraction, 6);
    }

    [Fact]
    public void Empty_directory_is_zero_coverage()
    {
        var c = CoverageReader.Read(_dir, now: At(11, 0), window: TimeSpan.FromHours(1));
        Assert.Equal(0, c.CoveredSeconds);
        Assert.Equal(0, c.Fraction);
    }
}
```

- [ ] **Step 3: Write the failing end-to-end test**

`tests/WebAudioArchive.Tests/RecorderEndToEndTests.cs`:
```csharp
using Microsoft.Extensions.Logging.Abstractions;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class RecorderEndToEndTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Fake_station_with_icy_titles_a_drop_and_a_gap_is_archived_exactly()
    {
        await using var station = await FakeStation.StartAsync();
        var gate = new TaskCompletionSource();
        station.Plan = n => n == 0
            ? new StreamPlan { Audio = Mp3TestData.Frames(100), MetaInt = 16000, TitleForBlock = b => b == 0 ? "A - B" : null, DropAfterSend = true }
            : new StreamPlan { Audio = Mp3TestData.Frames(100, startSeq: 100), MetaInt = 16000, Gate = gate.Task };

        var clock = new ManualClock(new DateTimeOffset(2026, 10, 15, 10, 30, 0, TimeSpan.Zero));
        var recorder = new MasterRecorder(_root, "fake", "e2e", clock, NullLogger<MasterRecorder>.Instance);
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var source = new HttpStreamSource(http, new StreamResolver(http, NullLogger<StreamResolver>.Instance), station.ListenUrl, NullLogger<HttpStreamSource>.Instance);
        var health = new StationHealth();
        var session = new StationSession(source, recorder, clock, health, NullLogger<StationSession>.Instance,
            new SessionOptions(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(100)));
        using var cts = new CancellationTokenSource();

        var run = session.RunAsync(cts.Token);
        await Wait(() => recorder.FramesWritten >= 100);
        await Wait(() => station.StreamConnections >= 2);
        clock.Advance(TimeSpan.FromSeconds(7));
        gate.SetResult();
        await Wait(() => recorder.FramesWritten >= 200);
        cts.Cancel();
        await run;
        recorder.Dispose();

        var master = Path.Combine(_root, "fake", "master");
        var mp3 = Assert.Single(Directory.EnumerateFiles(master, "*.mp3", SearchOption.AllDirectories));
        Assert.Equal(Mp3TestData.Frames(200), File.ReadAllBytes(mp3));

        var meta = SegmentMetaStore.Read(Path.ChangeExtension(mp3, ".json"))!;
        Assert.Equal(200, meta.Frames);
        Assert.Equal("shutdown", meta.EndedBy);
        Assert.Equal("A - B", Assert.Single(meta.Titles).Title);
        Assert.Equal(7, Assert.Single(meta.Gaps).Seconds, 3);
        Assert.Equal(16000, meta.Stream!.IcyMetaInt);
        Assert.Equal("Fake FM", meta.Stream.IcyName);
        Assert.Equal("media-element", meta.Stream.HowFound);
    }

    static async Task Wait(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for the recorder");
            await Task.Delay(10);
        }
    }
}
```

- [ ] **Step 4: Run to verify failure**

Run: `dotnet test --filter "MasterRecorderTests|RecorderEndToEndTests"`
Expected: build FAIL (`MasterRecorder`, `SegmentMetaStore`, `CoverageReader` not defined; `CoverageReaderTests` compiles after Task 8, so for now exclude it by temporarily moving `CoverageReaderTests.cs` out, or implement `CoverageReader` now by doing Task 8 step 1 first).

To avoid the compile error, implement `CoverageReader` now (it is small), then continue.

- [ ] **Step 5: Implement the sidecar model and store**

`src/WebAudioArchive.Core/Recording/SegmentMeta.cs`:
```csharp
using System.Text.Json;

namespace WebAudioArchive.Core.Recording;

public sealed record SegmentFormat(string Codec, int BitrateKbps, int SampleRateHz, int Channels);

public sealed record SegmentStream(string Url, string HowFound, string? IcyName, string? IcyGenre, int IcyMetaInt);

public sealed record SegmentTitle(string Title, long AtFileByte, DateTimeOffset Utc);

public sealed record SegmentGap(DateTimeOffset StartUtc, DateTimeOffset EndUtc, double Seconds, string Reason);

/// <summary>Sidecar for one master segment. EndedBy is hour-boundary, shutdown or crash.</summary>
public sealed record SegmentMeta(
    int Schema,
    string Instance,
    string Station,
    string File,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    long Bytes,
    int Frames,
    double AudioSeconds,
    SegmentFormat? Format,
    SegmentStream? Stream,
    IReadOnlyList<SegmentTitle> Titles,
    IReadOnlyList<SegmentGap> Gaps,
    string EndedBy);

public static class SegmentMetaStore
{
    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Written to a temporary file and renamed, so a reader never sees half a file.</summary>
    public static void Write(string jsonPath, SegmentMeta meta)
    {
        var tmp = jsonPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(meta, Options));
        File.Move(tmp, jsonPath, overwrite: true);
    }

    public static SegmentMeta? Read(string jsonPath) =>
        File.Exists(jsonPath) ? JsonSerializer.Deserialize<SegmentMeta>(File.ReadAllText(jsonPath), Options) : null;
}
```

- [ ] **Step 6: Implement the coverage reader**

`src/WebAudioArchive.Core/Recording/CoverageReader.cs`:
```csharp
namespace WebAudioArchive.Core.Recording;

public sealed record Coverage(double CoveredSeconds, double Fraction);

public static class CoverageReader
{
    /// <summary>Seconds of audio in the window ending at <paramref name="now"/>, from segment sidecars: each segment's span clipped to the window, minus gaps that fall inside that span.</summary>
    public static Coverage Read(string masterDir, DateTimeOffset now, TimeSpan window)
    {
        var from = now - window;
        double covered = 0;
        if (Directory.Exists(masterDir))
        {
            foreach (var path in Directory.EnumerateFiles(masterDir, "*.json", SearchOption.AllDirectories))
            {
                SegmentMeta? meta;
                try { meta = SegmentMetaStore.Read(path); }
                catch (System.Text.Json.JsonException) { continue; }
                if (meta is null) continue;

                var start = Max(meta.StartUtc, from);
                var end = Min(meta.EndUtc, now);
                if (end <= start) continue;

                var seconds = (end - start).TotalSeconds;
                foreach (var gap in meta.Gaps)
                {
                    var gs = Max(gap.StartUtc, start);
                    var ge = Min(gap.EndUtc, end);
                    if (ge > gs) seconds -= (ge - gs).TotalSeconds;
                }

                covered += Math.Max(0, seconds);
            }
        }

        return new Coverage(covered, Math.Min(1, covered / window.TotalSeconds));
    }

    static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
    static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
```

- [ ] **Step 7: Implement the recorder**

`src/WebAudioArchive.Core/Recording/MasterRecorder.cs`:
```csharp
using Microsoft.Extensions.Logging;
using WebAudioArchive.Core.Mp3;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Core.Recording;

/// <summary>
/// Writes a station's frames as gapless hourly segments aligned to UTC hours.
/// Layout: &lt;dataRoot&gt;/&lt;station&gt;/master/yyyy/MM-dd/HHmmss.mp3 with HHmmss.json beside it.
/// </summary>
public sealed class MasterRecorder(string dataRoot, string stationId, string instanceId, IClock clock, ILogger<MasterRecorder> log)
    : IFrameSink, IDisposable
{
    static readonly TimeSpan FlushEvery = TimeSpan.FromSeconds(10);

    readonly object _lock = new();
    readonly List<GapRecord> _pendingGaps = [];
    Segment? _segment;
    StreamInfo? _info;
    long _framesWritten;

    /// <summary>Test seam: how the .part file stream is opened.</summary>
    internal Func<string, Stream> OpenPartStream { get; init; } =
        path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024);

    public string MasterDir { get; } = Path.Combine(dataRoot, stationId, "master");

    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    public DateTimeOffset? CurrentSegmentStartUtc
    {
        get
        {
            lock (_lock) return _segment?.StartUtc;
        }
    }

    public void OnConnected(StreamInfo info, DateTimeOffset utc)
    {
        lock (_lock) _info = info;
    }

    public void OnGap(GapRecord gap)
    {
        lock (_lock) _pendingGaps.Add(gap);
    }

    public void OnTitle(string title, DateTimeOffset utc)
    {
        lock (_lock) _segment?.Titles.Add(new SegmentTitle(title, _segment.Bytes, utc));
    }

    public void OnFrame(ReadOnlySpan<byte> frame, in Mp3FrameHeader header, DateTimeOffset utc)
    {
        lock (_lock)
        {
            var hour = new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
            if (_segment is not null && _segment.HourStart != hour) CloseSegment("hour-boundary");
            // CloseSegment never throws, so a failed close cannot stop the next hour's audio being written.
            _segment ??= OpenSegment(utc, hour, header);

            if (_pendingGaps.Count > 0)
            {
                _segment.Gaps.AddRange(_pendingGaps);
                _pendingGaps.Clear();
            }

            _segment.Write(frame, header, utc, clock.UtcNow);
            Interlocked.Increment(ref _framesWritten);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_segment is not null) CloseSegment("shutdown");
        }
    }

    /// <summary>Finish any .part file left by a crash: cut it back to whole frames, rename it and write its sidecar.</summary>
    public void RecoverCrashedSegments()
    {
        if (!Directory.Exists(MasterDir)) return;
        foreach (var part in Directory.EnumerateFiles(MasterDir, "*.mp3.part", SearchOption.AllDirectories).ToList())
        {
            try
            {
                RecoverOne(part);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // One bad file must not stop the others. The .part stays where it is, so the next start retries it.
                log.LogError(ex, "Could not recover crashed segment {Part}; leaving it in place", part);
            }
        }
    }

    void RecoverOne(string part)
    {
        var bytes = File.ReadAllBytes(part);
        long good = 0;
        var frames = 0;
        double seconds = 0;
        Mp3FrameHeader? first = null;
        var splitter = new Mp3FrameSplitter();
        FrameHandler handler = (ReadOnlySpan<byte> f, in Mp3FrameHeader h) =>
        {
            good += f.Length;
            frames++;
            seconds += h.FrameSeconds;
            first ??= h;
        };
        splitter.Push(bytes, handler);
        splitter.Complete(handler);

        if (frames == 0)
        {
            File.Delete(part);
            log.LogWarning("Removed empty crashed segment {Part}", part);
            return;
        }

        // Everything that can fail without changing the file is done before the truncate. The last-write time is read
        // first because SetLength would replace it with the recovery time.
        var final = part[..^".part".Length];
        var start = StartFromPath(final);
        var end = new DateTimeOffset(File.GetLastWriteTimeUtc(part), TimeSpan.Zero);
        if (end < start) end = start;
        var h0 = first!.Value;

        using (var fs = new FileStream(part, FileMode.Open, FileAccess.Write)) fs.SetLength(good);
        File.Move(part, final, overwrite: false);

        try
        {
            SegmentMetaStore.Write(Path.ChangeExtension(final, ".json"), new SegmentMeta(
                1, instanceId, stationId, Path.GetFileName(final), start, end, good, frames, seconds,
                new SegmentFormat(h0.Codec, h0.BitrateKbps, h0.SampleRateHz, h0.Channels), null, [], [], "crash"));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Recovered {File} ({Frames} frames) but could not write its sidecar", final, frames);
            return;
        }

        log.LogWarning("Recovered crashed segment {File}: {Frames} frames", final, frames);
    }
    static DateTimeOffset StartFromPath(string mp3Path)
    {
        var name = Path.GetFileName(mp3Path); // 103045.mp3 or 103045-1.mp3
        var dayDir = Path.GetFileName(Path.GetDirectoryName(mp3Path))!; // MM-dd
        var yearDir = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(mp3Path)))!;
        return new DateTimeOffset(
            int.Parse(yearDir), int.Parse(dayDir[..2]), int.Parse(dayDir[3..5]),
            int.Parse(name[..2]), int.Parse(name[2..4]), int.Parse(name[4..6]), TimeSpan.Zero);
    }

    Segment OpenSegment(DateTimeOffset utc, DateTimeOffset hour, Mp3FrameHeader header)
    {
        var dir = Path.Combine(MasterDir, utc.ToString("yyyy"), utc.ToString("MM-dd"));
        Directory.CreateDirectory(dir);
        var stem = utc.ToString("HHmmss");
        var path = Path.Combine(dir, stem + ".mp3");
        for (var n = 1; File.Exists(path) || File.Exists(path + ".part"); n++) path = Path.Combine(dir, $"{stem}-{n}.mp3");
        log.LogInformation("Opening segment {Path}", path);
        return new Segment(path, OpenPartStream(path + ".part"), hour, utc, header, clock.UtcNow);
    }

    /// <summary>
    /// Never throws. If the stream cannot be closed or the rename fails, the .part is left in place for
    /// RecoverCrashedSegments on the next start. Once the rename has succeeded the sidecar is always attempted.
    /// </summary>
    void CloseSegment(string endedBy)
    {
        var s = _segment!;
        _segment = null;

        try
        {
            s.Dispose();
            File.Move(s.PartPath, s.FinalPath, overwrite: false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            log.LogError(ex, "Could not finalise segment {Path}; leaving {Part} for crash recovery", s.FinalPath, s.PartPath);
            return;
        }

        var h = s.FirstHeader;
        var stream = _info is null ? null : new SegmentStream(
            _info.Url.ToString(), _info.HowFound,
            _info.IcyHeaders.GetValueOrDefault("icy-name"), _info.IcyHeaders.GetValueOrDefault("icy-genre"), _info.IcyMetaInt);
        var gaps = s.Gaps.Select(g => new SegmentGap(g.StartUtc, g.EndUtc, (g.EndUtc - g.StartUtc).TotalSeconds, g.Reason)).ToList();
        var meta = new SegmentMeta(
            1, instanceId, stationId, Path.GetFileName(s.FinalPath), s.StartUtc, s.LastFrameUtc, s.Bytes, s.Frames, s.Seconds,
            new SegmentFormat(h.Codec, h.BitrateKbps, h.SampleRateHz, h.Channels), stream, s.Titles, gaps, endedBy);
        var json = Path.ChangeExtension(s.FinalPath, ".json");
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                SegmentMetaStore.Write(json, meta);
                break;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                if (attempt < 2) continue;
                log.LogError(ex, "Segment {Path} was finalised but its sidecar could not be written", s.FinalPath);
                return;
            }
        }

        log.LogInformation("Closed segment {Path}: {Frames} frames, {Bytes} bytes, ended by {EndedBy}", s.FinalPath, s.Frames, s.Bytes, endedBy);
    }

    sealed class Segment : IDisposable
    {
        readonly Stream _fs;
        DateTimeOffset _lastFlush;

        public Segment(string finalPath, Stream fs, DateTimeOffset hourStart, DateTimeOffset startUtc, Mp3FrameHeader first, DateTimeOffset wallNow)
        {
            FinalPath = finalPath;
            PartPath = finalPath + ".part";
            HourStart = hourStart;
            StartUtc = startUtc;
            LastFrameUtc = startUtc;
            FirstHeader = first;
            _lastFlush = wallNow;
            _fs = fs;
        }

        public string FinalPath { get; }
        public string PartPath { get; }
        public DateTimeOffset HourStart { get; }
        public DateTimeOffset StartUtc { get; }
        public DateTimeOffset LastFrameUtc { get; private set; }
        public Mp3FrameHeader FirstHeader { get; }
        public long Bytes { get; private set; }
        public int Frames { get; private set; }
        public double Seconds { get; private set; }
        public List<SegmentTitle> Titles { get; } = [];
        public List<GapRecord> Gaps { get; } = [];

        public void Write(ReadOnlySpan<byte> frame, Mp3FrameHeader header, DateTimeOffset utc, DateTimeOffset wallNow)
        {
            try
            {
                _fs.Write(frame);
            }
            catch
            {
                // Cut the file back to the last whole frame so later frames append cleanly.
                try
                {
                    _fs.SetLength(Bytes);
                    _fs.Position = Bytes;
                }
                catch
                {
                    // Best effort: crash recovery also truncates to whole frames.
                }

                throw;
            }

            Bytes += frame.Length;
            Frames++;
            Seconds += header.FrameSeconds;
            LastFrameUtc = utc;
            if (wallNow - _lastFlush >= FlushEvery)
            {
                _fs.Flush();
                _lastFlush = wallNow;
            }
        }

        public void Dispose() => _fs.Dispose();
    }
}
```
Notes for the implementer: `WebAudioArchive.Core.csproj` needs `<ItemGroup><InternalsVisibleTo Include="WebAudioArchive.Tests" /></ItemGroup>` for the `OpenPartStream` test seam. In `Clock_back_hour_does_not_collide` and the roll tests, the `wallNow` argument comes from `clock` (the injected `ManualClock`), which stays fixed, so no flush occurs mid-test; that is fine because `Dispose` flushes.

- [ ] **Step 8: Run to verify pass**

Run: `dotnet test --filter "MasterRecorderTests|RecorderEndToEndTests|CoverageReaderTests"`
Expected: all pass. If `Gap_and_titles_are_attached_to_the_segment_that_follows` fails on `AtFileByte`, remember `OnTitle` is called after frame 0 was written, so bytes written is the length of frame 0 (417 + padding: `Frame(0)` is padded, 418).

- [ ] **Step 9: Commit**

```powershell
git add -A
git commit -m "Add master recorder with UTC hourly segments, sidecars, crash recovery and coverage"
```

---

### Task 8: Worker host, health endpoint, Docker

**Files:**
- Create: `src/WebAudioArchive.Worker/Program.cs` (replace placeholder), `StationRegistry.cs`, `StationRecorderService.cs`
- Create: `docker/Dockerfile`, `docker/docker-compose.yml`
- Test: `tests/WebAudioArchive.Tests/WorkerHostTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 2 to 7.
- Produces:
  - `StationRegistry` with `IReadOnlyList<StationRuntime> Stations`; `sealed record StationRuntime(StationConfig Config, StationHealth Health, MasterRecorder Recorder)`.
  - `GET /healthz` JSON: `{ instance, uptimeSeconds, stations: [{ id, enabled, connected, lastByteUtc, secondsSinceLastByte, reconnects, currentSegmentStartUtc, coverage24h, coverage30d }] }`; HTTP 503 when an enabled station has had no audio for 120 s and the process has been up for at least 180 s.
  - `dotnet WebAudioArchive.Worker.dll --healthcheck` exits 0 if `/healthz` answers 200, else 1.
  - Environment: `WAA_CONFIG` (default `/config/stations.json`), `WAA_DATA` (default `/data`), `WAA_INSTANCE_ID` (default machine name), `WAA_PORT` (default `8080`).

- [ ] **Step 1: Write the failing host test**

`tests/WebAudioArchive.Tests/WorkerHostTests.cs`:
```csharp
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

public sealed class WorkerHostTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-host-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Healthz_reports_a_recording_station_and_the_archive_has_a_part_file()
    {
        await using var station = await FakeStation.StartAsync();
        station.Plan = _ => new StreamPlan { Audio = Mp3TestData.Frames(60) };

        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "stations.json");
        File.WriteAllText(config, $$"""
            { "stations": [ { "id": "fake", "name": "Fake FM", "listenUrl": "{{station.ListenUrl}}" } ] }
            """);
        var data = Path.Combine(_root, "data");

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("WAA_CONFIG", config);
            b.UseSetting("WAA_DATA", data);
            b.UseSetting("WAA_INSTANCE_ID", "host-test");
        });
        var client = factory.CreateClient();

        JsonElement body = default;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var response = await client.GetAsync("/healthz");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
            if (body.GetProperty("stations")[0].GetProperty("lastByteUtc").ValueKind != JsonValueKind.Null) break;
            await Task.Delay(50);
        }

        Assert.Equal("host-test", body.GetProperty("instance").GetString());
        var s = body.GetProperty("stations")[0];
        Assert.Equal("fake", s.GetProperty("id").GetString());
        Assert.True(s.GetProperty("connected").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("lastByteUtc").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, s.GetProperty("currentSegmentStartUtc").ValueKind);
        Assert.NotEmpty(Directory.EnumerateFiles(Path.Combine(data, "fake", "master"), "*.part", SearchOption.AllDirectories));
    }
}
```
This needs the `Microsoft.AspNetCore.Mvc.Testing` package: add to the test csproj `<PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />` (pin the newest stable 10.0.x that restores) and make the Worker's `Program` visible by adding `public partial class Program;` at the end of `Program.cs`.

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test --filter WorkerHostTests`
Expected: FAIL (no host yet).

- [ ] **Step 3: Implement the registry and the service**

`src/WebAudioArchive.Worker/StationRegistry.cs`:
```csharp
using WebAudioArchive.Core.Config;
using WebAudioArchive.Core.Recording;

namespace WebAudioArchive.Worker;

public sealed record StationRuntime(StationConfig Config, StationHealth Health, MasterRecorder Recorder);

public sealed class StationRegistry(string instanceId, IReadOnlyList<StationRuntime> stations)
{
    public string InstanceId { get; } = instanceId;
    public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
    public IReadOnlyList<StationRuntime> Stations { get; } = stations;
}
```

`src/WebAudioArchive.Worker/StationRecorderService.cs`:
```csharp
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Core.Streams;

namespace WebAudioArchive.Worker;

public sealed class StationRecorderService(StationRegistry registry, ILoggerFactory loggers, IClock clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = registry.Stations.Where(s => s.Config.Enabled).Select(s => RunStationAsync(s, stoppingToken)).ToList();
        await Task.WhenAll(tasks);
    }

    async Task RunStationAsync(StationRuntime station, CancellationToken ct)
    {
        var http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 5,
            ConnectTimeout = TimeSpan.FromSeconds(15),
        }) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; WebAudioArchive)");

        try
        {
            station.Recorder.RecoverCrashedSegments();
            var resolver = new StreamResolver(http, loggers.CreateLogger<StreamResolver>());
            var source = new HttpStreamSource(http, resolver, new Uri(station.Config.ListenUrl), loggers.CreateLogger<HttpStreamSource>());
            var session = new StationSession(source, station.Recorder, clock, station.Health, loggers.CreateLogger<StationSession>());
            await session.RunAsync(ct);
        }
        finally
        {
            station.Recorder.Dispose();
            http.Dispose();
        }
    }
}
```

- [ ] **Step 4: Implement Program.cs**

`src/WebAudioArchive.Worker/Program.cs`:
```csharp
using WebAudioArchive.Core.Config;
using WebAudioArchive.Core.Recording;
using WebAudioArchive.Worker;

static string Setting(IConfiguration c, string name, string fallback) => c[name] ?? Environment.GetEnvironmentVariable(name) ?? fallback;

if (args.Contains("--healthcheck"))
{
    var port = Environment.GetEnvironmentVariable("WAA_PORT") ?? "8080";
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var response = await http.GetAsync($"http://127.0.0.1:{port}/healthz");
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch (HttpRequestException)
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);
var configPath = Setting(builder.Configuration, "WAA_CONFIG", "/config/stations.json");
var dataRoot = Setting(builder.Configuration, "WAA_DATA", "/data");
var instanceId = Setting(builder.Configuration, "WAA_INSTANCE_ID", Environment.MachineName);

IReadOnlyList<StationConfig> stations;
try
{
    stations = StationConfigLoader.Load(configPath);
}
catch (ConfigException ex)
{
    foreach (var e in ex.Errors) Console.Error.WriteLine("config error: " + e);
    return 2;
}

if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls($"http://+:{Setting(builder.Configuration, "WAA_PORT", "8080")}");

var clock = new SystemClock();
var runtimes = stations.Select(s => new StationRuntime(
    s,
    new StationHealth(),
    new MasterRecorder(dataRoot, s.Id, instanceId, clock, LoggerFactory.Create(l => l.AddConsole()).CreateLogger<MasterRecorder>()))).ToList();

builder.Services.AddSingleton<IClock>(clock);
builder.Services.AddSingleton(new StationRegistry(instanceId, runtimes));
builder.Services.AddHostedService<StationRecorderService>();

var app = builder.Build();

app.MapGet("/healthz", (StationRegistry registry) =>
{
    var now = DateTimeOffset.UtcNow;
    var uptime = now - registry.StartedUtc;
    var unhealthy = false;
    var stationReports = registry.Stations.Select(s =>
    {
        var last = s.Health.LastByteUtc;
        var since = last is null ? (double?)null : Math.Round((now - last.Value).TotalSeconds, 1);
        var silent = s.Config.Enabled && uptime > TimeSpan.FromSeconds(180) && (since is null || since > 120);
        if (silent) unhealthy = true;
        return new
        {
            id = s.Config.Id,
            enabled = s.Config.Enabled,
            connected = s.Health.Connected,
            lastByteUtc = last,
            secondsSinceLastByte = since,
            reconnects = s.Health.Reconnects,
            currentSegmentStartUtc = s.Recorder.CurrentSegmentStartUtc,
            coverage24h = CoverageReader.Read(s.Recorder.MasterDir, now, TimeSpan.FromHours(24)).Fraction,
            coverage30d = CoverageReader.Read(s.Recorder.MasterDir, now, TimeSpan.FromDays(30)).Fraction,
        };
    }).ToList();

    var body = new { instance = registry.InstanceId, uptimeSeconds = Math.Round(uptime.TotalSeconds), stations = stationReports };
    return Results.Json(body, statusCode: unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
});

app.Run();
return 0;

public partial class Program;
```
Notes: `WebApplicationFactory<Program>` runs the host in-process; `UseSetting("WAA_CONFIG", ...)` flows into `builder.Configuration`, which is why `Setting` reads configuration before the environment. The `/healthz` coverage scan reads up to 720 small JSON files per call for 30 days; Docker's 30 s health interval keeps that cheap, and plan 5 adds a cache if it ever matters.

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test`
Expected: the whole suite passes. In `WorkerHostTests`, the first `/healthz` poll may show `lastByteUtc` null; the test polls for up to 15 s.

- [ ] **Step 6: Add the Dockerfile and compose file**

`docker/Dockerfile` (build context is the repository root):
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet publish src/WebAudioArchive.Worker -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends tzdata ca-certificates \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV WAA_CONFIG=/config/stations.json \
    WAA_DATA=/data \
    WAA_PORT=8080
VOLUME /data
EXPOSE 8080
USER $APP_UID
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
    CMD ["dotnet", "WebAudioArchive.Worker.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "WebAudioArchive.Worker.dll"]
```

`docker/docker-compose.yml`:
```yaml
services:
  recorder:
    build:
      context: ..
      dockerfile: docker/Dockerfile
    restart: unless-stopped
    environment:
      WAA_INSTANCE_ID: ${WAA_INSTANCE_ID:-home}
    volumes:
      - ${WAA_DATA_DIR:-../data}:/data
      - ../config:/config:ro
    ports:
      - "127.0.0.1:8080:8080"
```

- [ ] **Step 7: Build and run the container briefly**

Run:
```powershell
New-Item -ItemType Directory -Force data | Out-Null
docker compose -f docker/docker-compose.yml build
docker compose -f docker/docker-compose.yml up -d
Start-Sleep -Seconds 20
Invoke-RestMethod http://127.0.0.1:8080/healthz | ConvertTo-Json -Depth 5
docker compose -f docker/docker-compose.yml ps
```
Expected: `/healthz` shows `connected: true`, a `lastByteUtc`, and `currentSegmentStartUtc`; `ps` shows the container running. On Docker Desktop for Windows the bind-mounted `data` folder is writable; on Linux run `sudo chown 1654:1654 data` first. Leave the container running for Task 9 and stop it afterwards.

- [ ] **Step 8: Commit**

```powershell
git add -A
git commit -m "Add worker host with /healthz, Dockerfile and compose file"
```

---

### Task 9: Live smoke test, spec corrections and push

**Files:**
- Modify: `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md`
- Create: `docs/operations.md`

- [ ] **Step 1: Let the container record for a few minutes, then stop it cleanly**

Run:
```powershell
Start-Sleep -Seconds 240
docker compose -f docker/docker-compose.yml stop
Get-ChildItem data -Recurse -File | Select-Object FullName, Length
```
Expected: under `data/burst/master/<yyyy>/<MM-dd>/` an `.mp3` (not `.part`; `stop` sends SIGTERM and the recorder closes the segment) of roughly 16 KB per second (about 4 MB for 4 minutes) and a `.json`.

- [ ] **Step 2: Check the recording against first principles**

Run: `Get-Content (Get-ChildItem data -Recurse -Filter *.json | Select-Object -First 1).FullName`
Check, and record the observed numbers in the commit message:
- `frames` is close to `elapsed seconds * 44100 / 1152` (about 38.28 frames per second).
- `format` is `MPEG-1 Layer III, 128, 44100, 2`.
- `endedBy` is `shutdown`, `gaps` is empty, `stream.icyMetaInt` is 16000.
- `bytes` equals the `.mp3` length (`(Get-Item <file>).Length`).
- Decoding check: if ffmpeg or VLC is available, play the file; otherwise open it in Windows Media Player. It must play without glitches from the first second. (ffmpeg is not installed on the dev machine; this check is manual and is also done by plan 3 when ffmpeg is added to the image.)

- [ ] **Step 3: Prove crash recovery on the live container**

Run:
```powershell
docker compose -f docker/docker-compose.yml up -d
Start-Sleep -Seconds 30
docker kill (docker compose -f docker/docker-compose.yml ps -q recorder)
Get-ChildItem data -Recurse -Filter *.part
docker compose -f docker/docker-compose.yml up -d
Start-Sleep -Seconds 15
Get-ChildItem data -Recurse -File | Select-Object FullName, Length
docker compose -f docker/docker-compose.yml stop
```
Expected: after the kill a `.part` file exists; after restart it is gone, replaced by an `.mp3` and a `.json` with `endedBy: crash`, plus a new segment for the restarted container.

- [ ] **Step 4: Correct the spec with what the live stream showed**

In `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md`:

Replace, in section 2, the line
`- The stream is found from https://www.burstradio.org/listen: a RadioJar widget naming a Revma stream, MP3,
  128 kbps, 44.1 kHz, stereo (measured 2026-10-15; no ICY track titles seen).`
with
`- The stream is found from https://www.burstradio.org/listen: a RadioJar widget (streamName in a script block)
  naming a Revma stream, MP3, 128 kbps, 44.1 kHz, stereo (measured 2026-10-15). The stream URL answers 302
  with a short-lived token (rj-ttl=5), so the final URL must be requested afresh on every connect. The
  response carries icy-metaint 16000, so ICY metadata is interleaved and must be removed from the audio.`

Replace, in section 3.5, the sentence beginning `- Writes hourly segments aligned to the station-local clock hour:` through the end of that bullet with
`- Writes segments aligned to UTC clock hours, named by UTC: master/<yyyy>/<MM-dd>/<HHmmss>.mp3 (HHmmss is the
  first frame's arrival time) plus <HHmmss>.json (actual start and end, bytes, gaps, ICY titles, audio format,
  instance id, endedBy). UTC naming replaces station-local naming because local clock hours repeat when the
  clocks go back and would collide. A restart makes a new segment starting at the restart time.`

Also in section 3.11 change `master/<yyyy>/<MM-dd>/<HH>.mp3  <HH>.json` to `master/<yyyy>/<MM-dd>/<HHmmss>.mp3  <HHmmss>.json`, and in section 3.1 note that `segmentLength` is fixed at one UTC hour in plan 1 (remove it from the config list).

- [ ] **Step 5: Write the operations note**

`docs/operations.md`:
```markdown
# Operations

## Run

    docker compose -f docker/docker-compose.yml up -d --build

Set `WAA_INSTANCE_ID` (for example `home` or `cluster`) and `WAA_DATA_DIR` in the environment or a `.env` file
beside the compose file. On Linux the data folder must be writable by uid 1654.

## Where things are

- Audio: `<data>/<station>/master/<yyyy>/<MM-dd>/<HHmmss>.mp3`, sidecar `.json` beside it. UTC throughout.
- A `.mp3.part` file is the segment being written. After a crash it is finished on the next start
  (`endedBy: crash`).
- Health: `http://127.0.0.1:8080/healthz` (503 if an enabled station has had no audio for 2 minutes after the
  first 3 minutes of uptime).

## Reading the sidecar

`gaps` lists every break in the stream (start, end, seconds, reason) that ended inside the segment;
`endedBy` is `hour-boundary`, `shutdown` or `crash`; `titles` are ICY track titles with the file byte offset.
Segment spans minus gaps give the coverage figure on `/healthz` (24 hours and 30 days).

## Known limits (plan 1)

MP3 streams only; one UTC-hour segment length; no schedule, cutting, transcript or web app yet; no disk-space
guard (plan 5). Two instances are independent and are not reconciled.
```

- [ ] **Step 6: Run the full suite once more and push**

Run:
```powershell
dotnet test
git add -A
git commit -m "Record live smoke results, correct spec for UTC naming and Burst redirect and ICY facts, add operations note"
git push
```
Expected: all tests pass; push succeeds; the CI run on GitHub is green (test and docker jobs). If the CI Docker job fails because `global.json` pins an SDK the image lacks, confirm `rollForward` is `latestFeature` and the version is `10.0.100`.

---

## Self-review

**Spec coverage (plan 1 scope):**
- 3.1 config: Task 4 (station fields used now; schedule fields come with plan 2).
- 3.4 stream session: discovery (Task 5), always-on connection, redirects, ICY removal, backoff, gap recording, stall detection, bounded fan-out is deferred to plan 3 (only one sink exists in plan 1; `IFrameSink` is the extension point).
- 3.5 master recorder: Tasks 7 and 9 (frame-aligned UTC hourly segments, `.part` then rename, crash recovery, sidecar). Disk-space guard is plan 5, stated in "Known limits".
- 3.10 coverage figure and `/healthz`: Task 7 (`CoverageReader`) and Task 8.
- 5 deployment: Task 8 (Dockerfile, compose, health check, non-root, structured logging is the default console logger; JSON logging is not done, noted below).
- 6 testing items for these components: Tasks 2 to 8.

**Known gaps left deliberately (no placeholders, just later plans):** JSON-formatted logs, disk guard, GPU compose override, Kubernetes manifest, fan-out channel with slow-subscriber drop (needs a second subscriber to be meaningful).

**Type consistency:** `FrameHandler`/`AudioHandler`/`IFrameSink.OnFrame` signatures match across Tasks 2, 3, 6, 7. `StreamInfo` is defined in Task 6 and used by `IFrameSink` and `MasterRecorder`. `SegmentMeta` field order is identical in the recorder (Task 7), the coverage test and the store. `SessionOptions` positional order (stall, initial, max) is used the same way in Tasks 6 and 7.

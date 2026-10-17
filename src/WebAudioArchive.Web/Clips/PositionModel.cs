using WebAudioArchive.Core.Analysis;

namespace WebAudioArchive.Web.Clips;

/// <summary>
/// Maps UTC wall time to a byte offset inside a segment and back, at the stream bitrate (CBR: BitrateKbps * 125 bytes
/// per second, 16000 for 128 kbps). The time model itself (connect bursts, gaps, a stall's lost audio) is Core's
/// <see cref="SegmentTimeline"/>, shared with analysis so clip positions and event times agree. Positions are good to
/// about a second; the cutter then snaps them to real MP3 frame boundaries, and joined clips (#12) match frames exactly.
/// </summary>
public static class PositionModel
{
    /// <summary>When the audio at byte 0 was broadcast: the first arrival minus the burst.</summary>
    public static DateTimeOffset AudioStartUtc(CatalogSegment s) => TimeAt(s, 0);

    /// <summary>Estimated byte offset (not yet frame aligned) of wall time <paramref name="t"/>, clamped to the file.</summary>
    public static long Offset(CatalogSegment s, DateTimeOffset t) =>
        Math.Clamp((long)Math.Round(SegmentTimeline.ToOffset(s.Anchors, t) * s.BytesPerSecond), 0, s.Bytes);

    /// <summary>Wall time of the audio at byte <paramref name="offset"/>.</summary>
    public static DateTimeOffset TimeAt(CatalogSegment s, long offset) =>
        SegmentTimeline.ToUtc(s.Anchors, (double)offset / s.BytesPerSecond);
}

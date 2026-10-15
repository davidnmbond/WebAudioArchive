using Microsoft.AspNetCore.Components;
using WebAudioArchive.Web.Clips;
using WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Web.Components.Shared;

public sealed partial class ScheduleStatus
{
    [Parameter, EditorRequired] public required ScheduleSnapshot Snapshot { get; set; }
    [Parameter, EditorRequired] public required ScheduleSource Source { get; set; }

    static string Utc(DateTimeOffset? t) => t is null ? "-" : ClipPlanner.Utc(t.Value);
}

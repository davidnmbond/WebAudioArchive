using Microsoft.AspNetCore.Components;
using WebAudioArchive.Web.Consolidation;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Consolidation
{
    /// <summary>
    /// Who confirms a deletion. The app has no sign-in yet and is loopback only, so it is "loopback" until plan 4a's
    /// sign-in names the manager.
    /// </summary>
    public const string ConfirmedBy = "loopback";

    [Inject] public required ConsolidatorClient Client { get; set; }

    ConsolidationReview Review { get; set; } = null!;
    bool Busy { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Review = new ConsolidationReview(Client);
        await Review.LoadAsync(CancellationToken.None);
    }

    async Task ReloadAsync()
    {
        Busy = true;
        try
        {
            await Review.LoadAsync(CancellationToken.None);
        }
        finally
        {
            Busy = false;
        }
    }

    async Task ConfirmAsync()
    {
        if (Busy || !Review.Confirming) return; // a repeated click queued before the first render
        Busy = true;
        try
        {
            await Review.ConfirmAsync(ConfirmedBy, CancellationToken.None);
        }
        finally
        {
            Busy = false;
        }
    }

    static string Start(HeldItem i) => i.StartUtc is { } s ? Clips.ClipPlanner.Utc(s) : i.File;

    static string Utc(DateTimeOffset? t) => t is null ? "-" : Clips.ClipPlanner.Utc(t.Value);
}


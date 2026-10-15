using Microsoft.AspNetCore.Components;

namespace WebAudioArchive.Web.Components.Shared;

public sealed partial class UtcDatePicker
{
    [Parameter] public string Id { get; set; } = "utc-date";
    [Parameter] public string Label { get; set; } = "Date (UTC)";
    [Parameter] public DateOnly? Value { get; set; }
    [Parameter] public EventCallback<DateOnly?> ValueChanged { get; set; }

    /// <summary>Shown when Value is null (for example today's UTC date while a filter is off).</summary>
    [Parameter] public DateOnly Fallback { get; set; } = DateOnly.FromDateTime(DateTime.UtcNow);

    [Parameter] public bool IsEnabled { get; set; } = true;

    // PDDateTime holds a DateTime; it is only ever a UTC calendar date at midnight here.
    DateTime Shown => DateTime.SpecifyKind((Value ?? Fallback).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    async Task OnChangedAsync(DateTime value)
    {
        if (value == default) return;
        Value = DateOnly.FromDateTime(value);
        await ValueChanged.InvokeAsync(Value);
    }
}

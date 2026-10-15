using System.Globalization;
using Microsoft.AspNetCore.Components;

namespace WebAudioArchive.Web.Components.Shared;

public sealed partial class UtcDatePicker
{
    [Parameter] public string Id { get; set; } = "utc-date";
    [Parameter] public string Label { get; set; } = "Date (UTC)";
    [Parameter] public DateOnly? Value { get; set; }
    [Parameter] public EventCallback<DateOnly?> ValueChanged { get; set; }

    string Text => Value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";

    async Task OnChangeAsync(ChangeEventArgs e)
    {
        DateOnly? value = DateOnly.TryParseExact(e.Value?.ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        Value = value;
        await ValueChanged.InvokeAsync(value);
    }
}

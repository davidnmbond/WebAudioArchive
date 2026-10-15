namespace WebAudioArchive.Web.Components.Shared;

public sealed partial class ThemeToggle
{
    static readonly (string Value, string Text, string Icon)[] Choices =
    [
        ("auto", "Auto", "fas fa-adjust"),
        ("light", "Light", "fas fa-sun"),
        ("dark", "Dark", "fas fa-moon"),
    ];

    static Dictionary<string, object> AttributesFor(string value) => new()
    {
        ["data-theme-choice"] = value,
        ["aria-pressed"] = value == "auto" ? "true" : "false",
        ["type"] = "button",
    };
}

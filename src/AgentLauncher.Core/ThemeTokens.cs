namespace AgentLauncher.Core;

public sealed record ThemePalette(
    string Page,
    string Surface,
    string Header,
    string Line,
    string Ink,
    string Muted,
    string Accent,
    string StatusOk,
    string StatusWait,
    string StatusBad);

public static class ThemeTokens
{
    public static ThemePalette Dark { get; } = new(
        "#0E1116", "#161B22", "#1C2128", "#2B3139", "#E6EDF3", "#8B949E",
        "#2F81F7", "#3FB950", "#D29922", "#F85149");

    public static ThemePalette Light { get; } = new(
        "#F6F8FA", "#FFFFFF", "#F0F2F4", "#D0D7DE", "#1F2328", "#656D76",
        Dark.Accent, Dark.StatusOk, Dark.StatusWait, Dark.StatusBad);
}
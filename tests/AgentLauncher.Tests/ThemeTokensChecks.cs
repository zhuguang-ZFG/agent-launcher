using AgentLauncher.Core;

namespace AgentLauncher.Tests;

internal static class ThemeTokensChecks
{
    public static void Run(Action<string, Action> Test)
    {
        void Assert(bool value, string message) { if (!value) throw new Exception(message); }

        Test("dark and light palettes match the approved spec", () =>
        {
            var dark = ThemeTokens.Dark;
            Assert(dark.Page == "#0E1116" && dark.Surface == "#161B22" && dark.Header == "#1C2128", "dark surfaces");
            Assert(dark.Line == "#2B3139" && dark.Ink == "#E6EDF3" && dark.Muted == "#8B949E", "dark text");
            Assert(dark.Accent == "#2F81F7" && dark.StatusOk == "#3FB950" && dark.StatusWait == "#D29922" && dark.StatusBad == "#F85149", "dark accents");
            var light = ThemeTokens.Light;
            Assert(light.Page == "#F6F8FA" && light.Surface == "#FFFFFF" && light.Header == "#F0F2F4", "light surfaces");
            Assert(light.Line == "#D0D7DE" && light.Ink == "#1F2328" && light.Muted == "#656D76", "light text");
            Assert(light.Accent == dark.Accent && light.StatusOk == dark.StatusOk && light.StatusWait == dark.StatusWait && light.StatusBad == dark.StatusBad, "shared accents");
        });

        Test("new configurations default to dark rather than system", () =>
        {
            Assert(new Configuration().Theme == "dark", "default theme");
        });
    }
}
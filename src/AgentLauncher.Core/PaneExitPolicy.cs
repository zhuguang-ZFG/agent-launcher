namespace AgentLauncher.Core;
public static class PaneExitPolicy
{
    // Ordinary buffered newlines from the CLI must not dismiss an error screen.
    public static ConsoleKey DismissalKey(int exitCode,bool memoryLimitReached)=>exitCode!=0||memoryLimitReached?ConsoleKey.F12:ConsoleKey.Enter;
}

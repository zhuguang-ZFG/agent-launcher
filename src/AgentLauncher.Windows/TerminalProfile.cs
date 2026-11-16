using System.Diagnostics;

namespace AgentLauncher.Windows;

// Beautification via Windows Terminal JSON fragments: an officially supported per-user extension point
// (%LOCALAPPDATA%\Microsoft\Windows Terminal\Fragments). We never edit the user's settings.json.
// Fragments load only when a WT monarch process starts, so a running Terminal that predates the fragment
// cannot resolve --profile AgentLauncher; in that case the launch must fall back to profile-less panes.
public static class TerminalProfile
{
    public const string Name = "AgentLauncher";

    // closeOnExit "graceful": panes that exit 0 close on their own; failed panes stay so the output survives.
    private const string Fragment = """
        {
          "profiles": [
            {
              "name": "AgentLauncher",
              "font": { "face": "Cascadia Mono", "size": 11 },
              "padding": "8, 8, 8, 12",
              "closeOnExit": "graceful",
              "scrollbarState": "visible"
            }
          ]
        }
        """;

    // Returns the profile name when every running Terminal can resolve it; otherwise null (launch unchanged).
    // deferred == true means the fragment is in place and takes effect after the next Terminal restart.
    public static string? TryPrepare(out bool deferred)
    {
        deferred=false;
        try
        {
            var local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if(local.Length==0)return null; // Fail closed: an empty root would make the fragment path relative to the CWD.
            var directory=Path.Combine(local,"Microsoft","Windows Terminal","Fragments","AgentLauncher");
            var file=Path.Combine(directory,"agent-launcher.json");
            var current=File.Exists(file)?File.ReadAllText(file):null;
            if(current!=Fragment){Directory.CreateDirectory(directory);File.WriteAllText(file,Fragment);}
            var writtenUtc=File.GetLastWriteTimeUtc(file);
            var terminals=Process.GetProcessesByName("WindowsTerminal");
            try
            {
                foreach(var terminal in terminals)
                {
                    DateTime startedUtc;
                    try{startedUtc=terminal.StartTime.ToUniversalTime();}
                    catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){return null;} // A Terminal we cannot date may predate the fragment.
                    if(startedUtc<=writtenUtc){deferred=true;return null;}
                }
                return Name;
            }
            finally{foreach(var terminal in terminals)terminal.Dispose();}
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.Security.SecurityException){return null;} // Beautification must never break a launch.
    }
}

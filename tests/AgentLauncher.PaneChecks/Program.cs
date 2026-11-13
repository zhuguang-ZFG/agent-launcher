using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

if (args is not [var mode, var packageArgument, var resultArgument, var terminalArgument] || mode is not ("--launch" or "--check")) return 2;
var package = Path.GetFullPath(packageArgument);
var resultPath = Path.GetFullPath(resultArgument);
if (File.Exists(resultPath)) throw new IOException("Existing pane evidence must not be overwritten");
Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
AssemblyLoadContext.Default.Resolving += (context, name) => name.Name is "AgentLauncher.Core" or "AgentLauncher.Windows"
    ? context.LoadFromAssemblyPath(Path.Combine(package, name.Name + ".dll")) : null;

if (mode == "--launch")
{
    var terminal = terminalArgument;
    if (terminal.Length == 0 || terminal == "-")
    {
        var windows = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(package, "AgentLauncher.Windows.dll"));
        terminal = (string)windows.GetType("AgentLauncher.Windows.Commands", true)!.GetMethod("Terminal")!.Invoke(null, [""])!;
    }
    var start = new ProcessStartInfo(terminal) { UseShellExecute = false };
    foreach (var value in new[] { "-w", "launcher-production-check-" + Guid.NewGuid().ToString("N"), "new-tab", "--title", "Launcher exit-key acceptance", "--suppressApplicationTitle", "--", Environment.ProcessPath!, "--check", package, resultPath, "-" }) start.ArgumentList.Add(value);
    using var submission = Process.Start(start) ?? throw new IOException("Could not submit isolated Terminal probe");
    var wait = Stopwatch.StartNew();
    while (!File.Exists(resultPath) && wait.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(100);
    if (!File.Exists(resultPath)) throw new IOException("Production pane probe did not publish evidence; inspect its isolated Terminal window");
    using var report = JsonDocument.Parse(File.ReadAllText(resultPath));
    var passed = report.RootElement.GetProperty("Passed").GetBoolean();
    Console.WriteLine(File.ReadAllText(resultPath));
    return passed ? 0 : 1;
}

var completed = false;
void Publish(object report)
{
    var temporary = resultPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
    File.WriteAllText(temporary, JsonSerializer.Serialize(report));
    File.Move(temporary, resultPath); // Final visibility is atomic and never overwrites earlier evidence.
}
using var watchdog = new Timer(_ =>
{
    if (Volatile.Read(ref completed)) return;
    try { Publish(new { Passed = false, At = DateTimeOffset.UtcNow, Error = "Production dismissal did not finish within 20 seconds" }); }
    finally { Environment.Exit(124); } // This process is only the isolated console fixture, never a production task.
}, null, TimeSpan.FromSeconds(20), Timeout.InfiniteTimeSpan);
try
{
    var workerPath = Path.Combine(package, "AgentLauncher.Worker.dll");
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(workerPath);
    var method = assembly.GetType("AgentLauncher.Worker.Program", true)!.GetMethod("HoldPane", BindingFlags.Static | BindingFlags.NonPublic)!;
    if (Console.IsInputRedirected) throw new IOException("Requires a real interactive Terminal console");
    var results = new List<object>();
    foreach (var sample in new[] { (Code: 1, Limited: false), (Code: 0, Limited: true) })
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Input.Key(0x0d, '\r');
        var injector = Task.Run(async () =>
        {
            await Task.Delay(750);
            var ignoredEnter = !finished.Task.IsCompleted;
            Input.Key(0x7b, '\0');
            return ignoredEnter;
        });
        method.Invoke(null, [true, sample.Code, sample.Limited]); finished.SetResult();
        if (!await injector) throw new IOException("Buffered Enter dismissed an abnormal or limited pane before F12");
        results.Add(new { sample.Code, sample.Limited, BufferedEnterIgnored = true, F12Dismissed = true });
    }
    Input.Key(0x0d, '\r'); method.Invoke(null, [true, 0, false]);
    results.Add(new { Code = 0, Limited = false, EnterDismissed = true });
    Publish(new { Passed = true, At = DateTimeOffset.UtcNow, Pid = Environment.ProcessId, Assembly = workerPath, WorkerSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(workerPath))), Cases = results });
    Volatile.Write(ref completed, true);
    return 0;
}
catch (Exception error)
{
    Volatile.Write(ref completed, true);
    Publish(new { Passed = false, At = DateTimeOffset.UtcNow, Error = error.ToString() });
    return 1;
}

internal static class Input
{
    [StructLayout(LayoutKind.Explicit, Size = 20)] internal struct Record
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(4)] public int Down;
        [FieldOffset(8)] public ushort Repeat;
        [FieldOffset(10)] public ushort VirtualKey;
        [FieldOffset(14)] public char Character;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int id);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool WriteConsoleInputW(IntPtr input, [In] Record[] records, uint count, out uint written);
    internal static void Key(ushort key, char character)
    {
        var records = new[] { new Record { Type = 1, Down = 1, Repeat = 1, VirtualKey = key, Character = character }, new Record { Type = 1, Repeat = 1, VirtualKey = key, Character = character } };
        if (!WriteConsoleInputW(GetStdHandle(-10), records, (uint)records.Length, out var written) || written != records.Length) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "Could not inject fixture console input");
    }
}

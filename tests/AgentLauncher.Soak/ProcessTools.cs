using System.Collections;
using System.Diagnostics;
using System.Text;

namespace SoakRunner;

internal sealed class BoundedResult
{
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Truncated { get; init; }
    public string StdOut { get; init; } = "";
    public string StdErr { get; init; } = "";
}

internal sealed class ChildEnvironment
{
    public ChildEnvironment(Dictionary<string, string> variables, int credentialLikeParentVariables)
    {
        Variables = variables;
        CredentialLikeParentVariables = credentialLikeParentVariables;
    }

    public Dictionary<string, string> Variables { get; }
    public int CredentialLikeParentVariables { get; }
}

internal sealed class LoggedProcess : IDisposable
{
    private readonly Thread stdoutThread;
    private readonly Thread stderrThread;
    private readonly FilePump stdoutPump;
    private readonly FilePump stderrPump;
    private bool disposed;

    public Process Process { get; }
    public string StdOutPath { get; }
    public string StdErrPath { get; }
    public bool StdOutTruncated => stdoutPump.Truncated;
    public bool StdErrTruncated => stderrPump.Truncated;
    public string? LogError => stdoutPump.Error ?? stderrPump.Error;

    private LoggedProcess(Process process, Thread stdoutThread, Thread stderrThread, FilePump stdoutPump, FilePump stderrPump,
        string stdoutPath, string stderrPath)
    {
        Process = process;
        this.stdoutThread = stdoutThread;
        this.stderrThread = stderrThread;
        this.stdoutPump = stdoutPump;
        this.stderrPump = stderrPump;
        StdOutPath = stdoutPath;
        StdErrPath = stderrPath;
    }

    public static LoggedProcess Start(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string> environment,
        string stdoutPath, string stderrPath, int maximumCharacters = 256 * 1024)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.Environment.Clear();
        foreach (var pair in environment) startInfo.Environment[pair.Key] = pair.Value;
        var outPump = new FilePump(stdoutPath, maximumCharacters);
        var errPump = new FilePump(stderrPath, maximumCharacters);
        var process = new Process { StartInfo = startInfo };
        if (!process.Start()) { process.Dispose(); throw new InvalidOperationException("进程启动失败: " + startInfo.FileName); }
        var outThread = new Thread(() => outPump.Pump(process.StandardOutput)) { IsBackground = true, Name = "stdout-log" };
        var errThread = new Thread(() => errPump.Pump(process.StandardError)) { IsBackground = true, Name = "stderr-log" };
        try { outThread.Start(); errThread.Start(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                if (!process.WaitForExit(5000)) throw new ExecutionFailureException("日志线程启动失败，自有进程退出未得到确认: " + e.Message);
            }
            finally { process.Dispose(); }
            throw;
        }
        return new LoggedProcess(process, outThread, errThread, outPump, errPump, stdoutPath, stderrPath);
    }

    public bool WaitForExit(TimeSpan timeout) => Process.WaitForExit((int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue));

    public bool HasExited
    {
        get
        {
            try { return Process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public bool JoinPumps(TimeSpan timeout)
    {
        var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        return stdoutThread.Join(milliseconds) & stderrThread.Join(milliseconds);
    }

    public int? ExitCode
    {
        get
        {
            try { return Process.HasExited ? Process.ExitCode : null; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public bool Kill(bool entireTree)
    {
        try
        {
            if (Process.HasExited) return false;
            Process.Kill(entireTree);
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            if (!Process.HasExited) Process.WaitForExit(1000);
        }
        catch (InvalidOperationException) { }
        JoinPumps(TimeSpan.FromSeconds(2));
        Process.Dispose();
    }
}

internal sealed class FilePump
{
    private readonly string path;
    private readonly int maximumCharacters;
    private volatile bool truncated;
    private volatile int writtenCharacters;
    private volatile string? error;

    public FilePump(string path, int maximumCharacters)
    {
        this.path = Path.GetFullPath(path);
        this.maximumCharacters = maximumCharacters;
    }

    public bool Truncated => truncated;
    public int WrittenCharacters => writtenCharacters;
    public string? Error => error;

    public void Pump(StreamReader reader)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: false) { AutoFlush = false };
            var buffer = new char[4096];
            while (true)
            {
                int read;
                try { read = reader.Read(buffer, 0, buffer.Length); }
                catch (IOException e) { error = e.Message; break; }
                catch (ObjectDisposedException e) { error = e.Message; break; }
                if (read <= 0) break;
                var remaining = maximumCharacters - writtenCharacters;
                if (remaining <= 0)
                {
                    truncated = true;
                    continue;
                }
                var count = Math.Min(read, remaining);
                writer.Write(buffer, 0, count);
                writtenCharacters += count;
                if (count < read) truncated = true;
            }
            writer.Flush();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
        }
    }
}

internal sealed class TextSink
{
    private readonly StringBuilder text = new();
    private readonly int maximumCharacters;
    public bool Truncated { get; private set; }

    public TextSink(int maximumCharacters) => this.maximumCharacters = maximumCharacters;
    public string Text => text.ToString();

    public void Read(StreamReader reader)
    {
        var buffer = new char[4096];
        while (true)
        {
            int read;
            try { read = reader.Read(buffer, 0, buffer.Length); }
            catch (IOException) { break; }
            catch (ObjectDisposedException) { break; }
            if (read <= 0) break;
            var remaining = maximumCharacters - text.Length;
            if (remaining <= 0)
            {
                Truncated = true;
                continue;
            }
            var count = Math.Min(read, remaining);
            text.Append(buffer, 0, count);
            if (count < read) Truncated = true;
        }
    }
}

internal static class ProcessTools
{
    private static readonly string[] AllowedVariables =
    [
        "SystemRoot", "SYSTEMROOT", "WINDIR", "SystemDrive", "ComSpec", "COMSPEC",
        "PATH", "PATHEXT", "DOTNET_ROOT", "DOTNET_ROOT(x86)", "DOTNET_MULTILEVEL_LOOKUP",
        "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "PROCESSOR_IDENTIFIER", "OS",
        "PROGRAMDATA", "ALLUSERSPROFILE", "PROGRAMFILES", "PROGRAMFILES(X86)", "PROGRAMW6432",
        "COMMONPROGRAMFILES", "COMMONPROGRAMFILES(X86)", "COMMONPROGRAMW6432",
        "USERNAME", "USERDOMAIN", "USERDNSDOMAIN", "LANG", "LC_ALL", "LC_CTYPE"
    ];

    private static readonly string[] SensitiveMarkers =
    [
        "KEY", "TOKEN", "SECRET", "PASSWORD", "PASSWD", "CREDENTIAL", "COOKIE", "AUTHORIZATION", "PROXY"
    ];

    public static bool IsCredentialLike(string name) =>
        SensitiveMarkers.Any(marker => name.Contains(marker, StringComparison.OrdinalIgnoreCase));

    public static ChildEnvironment BuildChildEnvironment(
        string? testRoot = null,
        string? homeDirectory = null,
        string? agentDirectory = null,
        IEnumerable<string>? extraPathDirectories = null,
        IReadOnlyDictionary<string, string>? explicitVariables = null)
    {
        var parent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var credentialLike = 0;
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is not string key || entry.Value is not string value) continue;
            parent[key] = value;
            if (IsCredentialLike(key)) credentialLike++;
        }

        var child = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in AllowedVariables)
        {
            if (IsCredentialLike(name)) continue;
            if (parent.TryGetValue(name, out var value) && value.Length > 0) child[name] = value;
        }
        if (!child.TryGetValue("SystemRoot", out var systemRoot) || string.IsNullOrWhiteSpace(systemRoot))
        {
            systemRoot = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrWhiteSpace(systemRoot)) child["SystemRoot"] = systemRoot;
        }
        if (!child.TryGetValue("PATH", out var path) || string.IsNullOrWhiteSpace(path))
        {
            path = string.IsNullOrWhiteSpace(systemRoot)
                ? ""
                : Path.Combine(systemRoot, "System32") + ";" + systemRoot;
        }
        if (testRoot is not null) child["AGENT_LAUNCHER_TEST_ROOT"] = Path.GetFullPath(testRoot);
        if (homeDirectory is not null) ApplyHome(child, Path.GetFullPath(homeDirectory));
        if (agentDirectory is not null) child["PI_CODING_AGENT_DIR"] = Path.GetFullPath(agentDirectory);
        child["GIT_CONFIG_NOSYSTEM"] = "1";
        child["GIT_TERMINAL_PROMPT"] = "0";
        child["NO_COLOR"] = "1";

        var pathParts = new List<string>();
        foreach (var directory in extraPathDirectories ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var full = Path.GetFullPath(directory);
            if (Directory.Exists(full) && !pathParts.Contains(full, StringComparer.OrdinalIgnoreCase)) pathParts.Add(full);
        }
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var full = directory.Trim('"');
            if (full.Length > 0 && !pathParts.Contains(full, StringComparer.OrdinalIgnoreCase)) pathParts.Add(full);
        }
        child["PATH"] = string.Join(';', pathParts);

        if (explicitVariables is not null)
        {
            foreach (var pair in explicitVariables)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || IsCredentialLike(pair.Key)) continue;
                child[pair.Key] = pair.Value;
            }
        }
        foreach (var key in child.Keys.Where(IsCredentialLike).ToArray()) child.Remove(key);
        return new ChildEnvironment(child, credentialLike);
    }

    private static void ApplyHome(Dictionary<string, string> child, string home)
    {
        child["HOME"] = home;
        child["USERPROFILE"] = home;
        child["APPDATA"] = Path.Combine(home, "AppData", "Roaming");
        child["LOCALAPPDATA"] = Path.Combine(home, "AppData", "Local");
        child["TEMP"] = Path.Combine(home, "Temp");
        child["TMP"] = Path.Combine(home, "Temp");
        child["XDG_CONFIG_HOME"] = Path.Combine(home, ".config");
        child["XDG_CACHE_HOME"] = Path.Combine(home, ".cache");
        child["XDG_DATA_HOME"] = Path.Combine(home, ".local", "share");
        var drive = Path.GetPathRoot(home);
        if (!string.IsNullOrWhiteSpace(drive) && drive.Length >= 2)
        {
            child["HOMEDRIVE"] = drive.TrimEnd('\\');
            var tail = home[drive.Length..];
            child["HOMEPATH"] = tail.StartsWith('\\') ? tail : "\\" + tail;
        }
    }

    public static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string>? arguments, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (arguments is not null)
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    public static BoundedResult RunBounded(ProcessStartInfo startInfo, TimeSpan timeout, int maximumCharacters = 64 * 1024)
    {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new InvalidOperationException("进程启动失败: " + startInfo.FileName);
        var stdout = new TextSink(maximumCharacters);
        var stderr = new TextSink(maximumCharacters);
        var outThread = new Thread(() => stdout.Read(process.StandardOutput)) { IsBackground = true, Name = "bounded-stdout" };
        var errThread = new Thread(() => stderr.Read(process.StandardError)) { IsBackground = true, Name = "bounded-stderr" };
        outThread.Start();
        errThread.Start();
        var milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue);
        var exited = process.WaitForExit(milliseconds);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            process.WaitForExit(5000);
        }
        if (!outThread.Join(5000) || !errThread.Join(5000))
            throw new ExecutionFailureException("子进程输出未能在期限内排干。");
        if (!process.HasExited) throw new ExecutionFailureException("自有探测进程退出未得到确认。");
        var exit = -1;
        try { if (process.HasExited) exit = process.ExitCode; } catch (InvalidOperationException) { }
        return new BoundedResult
        {
            ExitCode = exit,
            TimedOut = !exited,
            Truncated = stdout.Truncated || stderr.Truncated,
            StdOut = stdout.Text,
            StdErr = stderr.Text
        };
    }

    public static string? ResolveCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        if (Path.IsPathFullyQualified(command)) return File.Exists(command) ? Path.GetFullPath(command) : null;
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT")
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var hasExtension = Path.HasExtension(command);
        foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var clean = directory.Trim('"');
            if (clean.Length == 0) continue;
            if (hasExtension)
            {
                var candidate = Path.Combine(clean, command);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                continue;
            }
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(clean, command + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        }
        return null;
    }

    public static IEnumerable<string> ExtraPathDirectoriesForTool(string toolPath)
    {
        var result = new List<string>();
        if (!Path.IsPathFullyQualified(toolPath)) return result;
        var directory = Path.GetDirectoryName(Path.GetFullPath(toolPath));
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return result;
        result.Add(directory);
        var name = Path.GetFileName(directory).ToLowerInvariant();
        var parent = Directory.GetParent(directory)?.FullName;
        var parentName = parent is null ? "" : Path.GetFileName(parent).ToLowerInvariant();
        if (parent is not null)
        {
            if (name == "cmd")
            {
                AddIfExists(result, Path.Combine(parent, "bin"));
                AddIfExists(result, Path.Combine(parent, "usr", "bin"));
            }
            else if (name == "bin" && parentName == "usr")
            {
                var root = Directory.GetParent(parent)?.FullName;
                if (root is not null)
                {
                    AddIfExists(result, Path.Combine(root, "cmd"));
                    AddIfExists(result, Path.Combine(root, "bin"));
                    AddIfExists(result, Path.Combine(root, "usr", "bin"));
                }
            }
            else if (name == "bin")
            {
                AddIfExists(result, Path.Combine(parent, "cmd"));
                AddIfExists(result, Path.Combine(parent, "usr", "bin"));
            }
            else if (name == "usr")
            {
                var root = Directory.GetParent(parent)?.FullName;
                if (root is not null)
                {
                    AddIfExists(result, Path.Combine(root, "cmd"));
                    AddIfExists(result, Path.Combine(root, "bin"));
                }
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddIfExists(List<string> result, string path)
    {
        if (Directory.Exists(path) && !result.Contains(path, StringComparer.OrdinalIgnoreCase)) result.Add(path);
    }

    public static string? ReadSmallText(string path, int maximumBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > maximumBytes) return null;
            return File.ReadAllText(path, Encoding.UTF8).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void DeleteIfExists(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

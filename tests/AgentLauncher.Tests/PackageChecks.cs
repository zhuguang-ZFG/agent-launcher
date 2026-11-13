using System.Diagnostics;
using System.Text.Json;

internal static class PackageChecks
{
    public static void Register(Action<string, Action> test, string temp)
    {
        test("R17 portable launch rejects mixed missing and invalid packages before starting App", () =>
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "scripts", "launch-preview.ps1"))) repository = repository.Parent;
            if (repository is null) throw new Exception("Cannot locate package launcher source");
            var root = Path.Combine(temp, "package-check-中文"); Directory.CreateDirectory(root);
            var names = new[] { "AgentLauncher.App.exe", "AgentLauncher.App.dll", "AgentLauncher.App.deps.json", "AgentLauncher.App.runtimeconfig.json", "AgentLauncher.Worker.exe", "AgentLauncher.Worker.dll", "AgentLauncher.Worker.deps.json", "AgentLauncher.Worker.runtimeconfig.json", "AgentLauncher.Core.dll", "AgentLauncher.Windows.dll", "LICENSE", "NOTICE", "Launch.ps1", "Start-AgentLauncher.cmd" };
            foreach (var name in names) File.WriteAllText(Path.Combine(root, name), "package fixture only; CheckOnly must never execute this file");
            File.Copy(Path.Combine(repository.FullName, "scripts", "launch-preview.ps1"), Path.Combine(root, "Launch.ps1"), overwrite: true);
            string Manifest(bool duplicate = false, bool traversal = false) => JsonSerializer.Serialize(new
            {
                SchemaVersion = 1, Architecture = "x64", TargetFramework = "net8.0-windows", SourceCommit = "test-fixture",
                Files = names.Concat(duplicate ? [names[0]] : Array.Empty<string>()).Select((name, index) => new { Name = traversal && index == 0 ? "../escape.exe" : name, Length = new FileInfo(Path.Combine(root, name)).Length, Sha256 = SoakRunner.JsonIo.Sha256File(Path.Combine(root, name)) })
            });
            var manifestPath = Path.Combine(root, "distribution.json"); var manifest = Manifest(); File.WriteAllText(manifestPath, manifest);
            int Check(string? invalidRuntime = null, bool fake = false)
            {
                var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var value in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "Launch.ps1"), "-CheckOnly" }) start.ArgumentList.Add(value);
                start.Environment.Remove("AGENT_LAUNCHER_TEST_ROOT"); start.Environment.Remove("AGENT_LAUNCHER_FAKE_SAMPLE");
                if (invalidRuntime is not null) start.Environment["DOTNET_ROOT_X64"] = invalidRuntime;
                if (fake) start.Environment["AGENT_LAUNCHER_FAKE_SAMPLE"] = "1";
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000)) { process.Kill(entireProcessTree: true); process.WaitForExit(5000); throw new Exception("Package check timed out"); }
                if (!Task.WaitAll([stdout, stderr], 5000)) throw new Exception("Package check output did not drain");
                return process.ExitCode;
            }
            if (Check() != 0) throw new Exception("Valid package failed CheckOnly");
            var worker = Path.Combine(root, "AgentLauncher.Worker.dll"); var original = File.ReadAllBytes(worker); File.WriteAllText(worker, "different worker version");
            if (Check() == 0) throw new Exception("Mixed Worker was accepted"); File.WriteAllBytes(worker, original);
            var companion = Path.Combine(root, "AgentLauncher.Worker.runtimeconfig.json"); File.Delete(companion);
            if (Check() == 0) throw new Exception("Missing companion was accepted"); File.WriteAllText(companion, "package fixture only; CheckOnly must never execute this file");
            File.WriteAllText(manifestPath, Manifest(duplicate: true)); if (Check() == 0) throw new Exception("Duplicate manifest entry was accepted");
            File.WriteAllText(manifestPath, Manifest(traversal: true)); if (Check() == 0) throw new Exception("Manifest traversal was accepted");
            File.WriteAllText(manifestPath, manifest);
            if (Check(Path.Combine(root, "missing-runtime")) == 0) throw new Exception("Missing Desktop runtime was accepted");
            if (Check(fake: true) == 0) throw new Exception("Inherited fake sampling was accepted by production entry");
        });
    }
}

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>An additive, process-local Qoder plugin. No Qoder or project settings are written.</summary>
public static class QoderMemoryGuard
{
    public const string VerifiedVersion = "1.1.89";
    private const string Prefix = "qoder-memory-guard-v1-";
    public static string PluginPath => DirectoryFor(Store.WorkerPath, Store.Root);

    public static string DirectoryFor(string worker, string storage)
    {
        var identity = Path.GetFullPath(worker) + "|" + typeof(QoderMemoryGuard).Module.ModuleVersionId;
        return Path.Combine(storage, "memory-guards", Prefix + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32]);
    }

    public static CommandSpec Attach(CommandSpec command)
    {
        var offset = command.Adapter == "node-shim" ? 1 : 0;
        return command with { Arguments = [..command.Arguments.Take(offset), "--plugin-dir", PluginPath, ..command.Arguments.Skip(offset)] };
    }

    public static CommandSpec Detach(CommandSpec command)
    {
        var offset = command.Adapter == "node-shim" ? 1 : 0;
        return HasMarker(command) ? command with { Arguments = [..command.Arguments.Take(offset), ..command.Arguments.Skip(offset + 2)] } : command;
    }

    private static bool HasMarker(CommandSpec command)
    {
        var offset = command.Adapter == "node-shim" ? 1 : 0;
        return command.Arguments.Length > offset + 1 && command.Arguments[offset] == "--plugin-dir"
            && Path.GetFileName(command.Arguments[offset + 1]).StartsWith(Prefix, StringComparison.Ordinal);
    }

    public static bool IsAttached(CommandSpec command)
    {
        if (!HasMarker(command)) return false;
        var offset = command.Adapter == "node-shim" ? 1 : 0;
        if (!string.Equals(command.Arguments[offset + 1], PluginPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("排队记录的 Qoder 内存保护版本或位置已变化，请重新入队。");
        return true;
    }

    public static void Prepare() => Prepare(Store.WorkerPath, Store.Root);

    public static void Prepare(string worker, string storage)
    {
        if (!Path.IsPathFullyQualified(worker) || !File.Exists(worker)) throw new IOException("缺少 Qoder 内存保护执行程序，请使用完整发布包。");
        var root = DirectoryFor(worker, storage);
        using var mutex = new Mutex(false, @"Local\AgentLauncher-qoder-plugin-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant()))));
        bool owned;
        try { owned = mutex.WaitOne(2000); } catch (AbandonedMutexException) { owned = true; }
        if (!owned) throw new IOException("Qoder 内存保护插件正在准备，请稍后重试。");
        try
        {
            var hook = new { type = "command", command = Path.GetFullPath(worker), args = new[] { "qoder-memory-hook" }, timeout = 5 };
            WriteOnce(Path.Combine(root, ".qoder-plugin", "plugin.json"), new { name = "agent-launcher-memory", version = "1.0.0", description = "Checks launcher memory headroom before starting tools." });
            WriteOnce(Path.Combine(root, "hooks", "hooks.json"), new { hooks = new Dictionary<string, object>
            {
                ["SessionStart"] = new[] { new { hooks = new[] { hook } } },
                ["PreToolUse"] = new[] { new { hooks = new[] { hook } } }
            } });
        }
        finally { mutex.ReleaseMutex(); }
    }

    private static void WriteOnce<T>(string path, T value)
    {
        if (File.Exists(path))
        {
            if (new FileInfo(path).Length > 32 * 1024 || File.ReadAllText(path) != JsonSerializer.Serialize(value, Store.Json))
                throw new IOException("Qoder 内存保护插件内容已变化，已阻止启动；请核对启动器生成的插件文件。");
        }
        else Store.Save(path, value);
    }
}

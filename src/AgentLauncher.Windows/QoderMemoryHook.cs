using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

/// <summary>The Qoder hook reads bounded input, sends no tool arguments, and never grants permissions.</summary>
public static class QoderMemoryHook
{
    public const int MaxInputBytes = 1024 * 1024;
    private const string Unavailable = "启动器内存保护连接或读数不可用，已拒绝新增工具；已有工作继续运行。请稍后重试。";

    public static async Task<int> RunAsync(Stream input, TextWriter output, string? pipeName)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[4096];
            int count;
            while ((count = await input.ReadAsync(chunk.AsMemory(), timeout.Token).AsTask().WaitAsync(timeout.Token)) > 0)
            {
                if (buffer.Length + count > MaxInputBytes) throw new InvalidDataException("Hook input exceeds limit.");
                buffer.Write(chunk, 0, count);
            }
            using var json = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 32 });
            var eventName = json.RootElement.GetProperty("hook_event_name").GetString();
            if (eventName == "SessionStart")
            {
                var hello = await ExchangeAsync(pipeName, 2, timeout.Token);
                await output.WriteLineAsync(JsonSerializer.Serialize(new { systemMessage = hello.Allowed ? "启动器内存检查已连接；新增工具将检查内存余量。" : Unavailable }));
                return 0;
            }
            if (eventName != "PreToolUse" || !json.RootElement.TryGetProperty("tool_name", out var tool) || tool.ValueKind != JsonValueKind.String || string.IsNullOrEmpty(tool.GetString()))
                throw new InvalidDataException("Invalid memory hook event.");
            var decision = await ExchangeAsync(pipeName, 1, timeout.Token);
            // An empty response preserves Qoder's normal permission decisions and all other hooks.
            // In particular, never emit permissionDecision=allow on healthy memory.
            await output.WriteLineAsync(decision.Allowed ? "{}" : Denied(decision.Reason));
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            await output.WriteLineAsync(Denied(Unavailable));
            return 0;
        }
    }

    private static string Denied(string reason) => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new { hookEventName = "PreToolUse", permissionDecision = "deny", permissionDecisionReason = reason }
    });

    private static async Task<Admission> ExchangeAsync(string? name, byte request, CancellationToken ct)
    {
        if (name is null || !Regex.IsMatch(name, "\\AAgentLauncher-memory-[0-9a-f]{32}\\z")) return new(false, Unavailable);
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(ct);
        await pipe.WriteAsync(new byte[] { request }, ct);
        await pipe.FlushAsync(ct);
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < 4096)
        {
            if (await pipe.ReadAsync(one, ct) != 1) return new(false, Unavailable);
            if (one[0] == (byte)'\n')
            {
                using var json = JsonDocument.Parse(buffer.ToArray());
                var root = json.RootElement;
                if (!root.TryGetProperty("allowed", out var allowed) || allowed.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
                    || !root.TryGetProperty("reason", out var reason) || reason.ValueKind != JsonValueKind.String) return new(false, Unavailable);
                return new(allowed.GetBoolean(), reason.GetString()!);
            }
            buffer.WriteByte(one[0]);
        }
        return new(false, Unavailable);
    }
}

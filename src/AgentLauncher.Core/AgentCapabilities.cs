using System.Text.Json.Serialization;

namespace AgentLauncher.Core;

/// <summary>Named argument set verified against one exact CLI version. Every entry is passed as one argv element.</summary>
public sealed record ArgumentPreset(string AgentId, string Id, string Name, string Semantic, string[] Arguments, string? ValueLabel, string VerifiedVersion)
{
    public const string ValueToken = "{value}";
    public const int MaxArguments = 32;
    public const int MaxArgumentLength = 4096;

    public bool NeedsValue => !string.IsNullOrEmpty(ValueLabel);
    public ArgumentPreset Copy() => this with { Arguments = [.. Arguments] };

    /// <summary>Replaces the single value placeholder; presets without a placeholder ignore the value.</summary>
    public ArgumentPreset Fill(string? value)
    {
        if (!NeedsValue) return Copy();
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("该预设需要一个值；留空请改用“新会话”。");
        if (value.Contains('\0')) throw new InvalidDataException("参数值不能包含 NUL。");
        return this with { Arguments = Arguments.Select(argument => argument == ValueToken ? value : argument).ToArray() };
    }

    public string Preview => Arguments.Length == 0 ? "（无参数，使用 CLI 默认行为）" : string.Join(" ", Arguments.Select(argument => argument == ValueToken ? $"⟨{ValueLabel}⟩" : argument.Length == 0 ? "\"\"" : argument));
}

public sealed class AgentCapability
{
    [JsonRequired] public string AgentId { get; set; } = "";
    [JsonRequired] public string ProgramPath { get; set; } = "";
    [JsonRequired] public string Fingerprint { get; set; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Version { get; set; }
    [JsonRequired] public DateTimeOffset ProbedAt { get; set; }
    public string Note { get; set; } = "";

    [JsonIgnore] public IReadOnlyList<ArgumentPreset> Presets => AgentPresets.For(AgentId, Version);
    public AgentCapability Copy() => new() { AgentId = AgentId, ProgramPath = ProgramPath, Fingerprint = Fingerprint, Version = Version, ProbedAt = ProbedAt, Note = Note };
}

public static class AgentPresets
{
    // Versions below are the ones actually probed on 2026-11-11; a different installed version keeps manual editing and hides these buttons.
    public const string OmpVerifiedVersion = "18.18.8";
    public const string CodexVerifiedVersion = "0.167.1";

    private static readonly ArgumentPreset[] all =
    [
        new("omp", "omp-new", "新会话（无参数）", "new-session", [], null, OmpVerifiedVersion),
        new("omp", "omp-continue", "续接上次会话", "resume-last", ["--continue"], null, OmpVerifiedVersion),
        new("omp", "omp-resume", "恢复指定会话", "resume-session", ["--resume", ArgumentPreset.ValueToken], "会话 ID 前缀或路径", OmpVerifiedVersion),
        new("omp", "omp-model", "指定模型", "model", ["--model", ArgumentPreset.ValueToken], "模型（如 openai/gpt-5.2）", OmpVerifiedVersion),
        new("omp", "omp-thinking", "高思考强度", "thinking", ["--thinking", "high"], null, OmpVerifiedVersion),
        new("codex", "codex-new", "新会话（无参数）", "new-session", [], null, CodexVerifiedVersion),
        new("codex", "codex-continue", "续接最近的会话", "resume-last", ["resume", "--last"], null, CodexVerifiedVersion),
        new("codex", "codex-resume", "恢复指定会话", "resume-session", ["resume", ArgumentPreset.ValueToken], "会话 ID", CodexVerifiedVersion),
        new("codex", "codex-model", "指定模型", "model", ["-m", ArgumentPreset.ValueToken], "模型名称", CodexVerifiedVersion)
    ];

    public static IReadOnlyList<ArgumentPreset> All => all;

    /// <summary>Presets are offered only for the exact verified version; unknown or upgraded versions return none.</summary>
    public static IReadOnlyList<ArgumentPreset> For(string agentId, string? version) =>
        string.IsNullOrEmpty(version) ? [] : [.. all.Where(preset => preset.AgentId == agentId && string.Equals(preset.VerifiedVersion, version, StringComparison.Ordinal))];

    public static bool StructurallyValid(ArgumentPreset preset) =>
        preset.AgentId.Length > 0 && preset.Id.Length > 0 && preset.Name.Length > 0 && preset.Semantic.Length > 0 &&
        preset.Arguments.Length <= ArgumentPreset.MaxArguments &&
        preset.Arguments.All(argument => argument.Length <= ArgumentPreset.MaxArgumentLength && !argument.Contains('\0')) &&
        preset.Arguments.Count(argument => argument == ArgumentPreset.ValueToken) == (preset.NeedsValue ? 1 : 0);
}

public static class AgentVersion
{
    /// <summary>Extracts a bounded version token from version output such as "omp/18.18.8" or "codex-cli 0.167.1".</summary>
    public static string? Parse(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(64))
            foreach (var raw in line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(32))
            {
                var token = raw;
                var slash = token.LastIndexOf('/');
                if (slash >= 0) token = token[(slash + 1)..];
                if (token.StartsWith('v') || token.StartsWith('V')) token = token[1..];
                if (token.Length is < 1 or > 32 || !char.IsAsciiDigit(token[0])) continue;
                if (!token.All(character => char.IsAsciiDigit(character) || character is '.' or '-' or '+')) continue;
                return token;
            }
        return null;
    }
}

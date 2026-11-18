using System.Text;
using System.Text.Json.Nodes;

namespace AgentLauncher.ManualRelay;

public sealed record SessionReport(string Code, string? TaskId, string Status);

public static class FileWork
{
    public static byte[] Verify(string workspace, string canonical)
    {
        var files = JsonNode.Parse(canonical, documentOptions: RelayJson.Document)!["files"]!.AsArray();
        var rows = new JsonArray();
        foreach (var item in files)
        {
            var path = item!["path"]!.GetValue<string>();
            var expected = item["sha256"]!.GetValue<string>();
            var actual = Digests.Sha256File(SafePath.ResolveExisting(workspace, path));
            rows.Add(new JsonObject { ["path"] = path, ["expected"] = expected, ["actual"] = actual, ["match"] = actual == expected });
        }
        return Encoding.UTF8.GetBytes(new JsonObject { ["kind"] = "file_verify", ["files"] = rows }.ToJsonString());
    }

    public static byte[] ResultData(string canonical)
    {
        var data = JsonNode.Parse(canonical, documentOptions: RelayJson.Document)!["data"]!.ToJsonString();
        return Encoding.UTF8.GetBytes("{\"kind\":\"result_data\",\"data\":" + data + "}");
    }
}

public sealed class ManualSession
{
    public SessionReport RunOnce(LocalRelay relay, string workspace, string holderId, TimeSpan ttl, string? taskId)
    {
        taskId ??= relay.NextQueued();
        if (taskId is null) return new SessionReport("idle", null, "idle");
        var lease = relay.TryLease(taskId, holderId, ttl);
        if (!lease.Accepted || lease.View.AttemptId is null) return new SessionReport(lease.Code, taskId, lease.View.Status);
        var record = relay.ReadRecord(taskId);
        try
        {
            var bytes = record.Kind switch
            {
                "file_verify" => FileWork.Verify(workspace, record.PayloadCanonical),
                "result_data" => FileWork.ResultData(record.PayloadCanonical),
                _ => throw new InvalidDataException("类型不在白名单。")
            };
            var destination = Path.Combine(relay.StagingDirectory(taskId, lease.View.AttemptId), "result.json");
            if ((File.Exists(destination) || Directory.Exists(destination)) && SafePath.IsLink(destination)) throw new InvalidDataException("拒绝符号链接。");
            File.WriteAllBytes(destination, bytes);
            var submit = relay.Submit(taskId, lease.View.AttemptId, lease.View.Version, new[] { new FileDigest("result.json", Digests.Sha256(bytes), bytes.Length) });
            return new SessionReport(submit.Code, taskId, relay.Read(taskId).Status);
        }
        catch (InvalidDataException)
        {
            var view = relay.Reject(taskId, lease.View.AttemptId, lease.View.Version, "path_rejected");
            return new SessionReport("path_rejected", taskId, view.Status);
        }
    }
}

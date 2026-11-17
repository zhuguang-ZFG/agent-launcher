using AgentLauncher.Core;

namespace AgentLauncher.Tests;

// 远程主机配置界面的边界回归：字段校验、同步模式、稳定 Id 与删除引用核对。
internal static class RemoteHostEditorChecks
{
    public static void Run(Action<string, Action> test, Action<bool, string> assert)
    {
        test("H1 remote host form rejects blank name and address", () =>
        {
            assert(RemoteHostEditor.Validate("", "10.0.0.1", "22", "pi") == "名称不能为空。", "blank name accepted");
            assert(RemoteHostEditor.Validate("VPS", "   ", "22", "pi") == "地址不能为空。", "blank address accepted");
        });

        test("H2 remote host form rejects out-of-range port and empty agent list", () =>
        {
            foreach (var port in new[] { "0", "65536", "abc", "-1", "" })
                assert(RemoteHostEditor.Validate("VPS", "10.0.0.1", port, "pi") == "端口必须是 1-65535 的数字。", "bad port accepted: " + port);
            assert(RemoteHostEditor.Validate("VPS", "10.0.0.1", "22", " , , ") == "至少指定一个可用 agent 类型。", "empty agent list accepted");
        });

        test("H3 remote host form trims values and accepts port boundaries", () =>
        {
            assert(RemoteHostEditor.Validate(" VPS ", " 10.0.0.1 ", "65535", "pi, kimi ,, qoder ") is null, "valid form rejected");
            assert(RemoteHostEditor.ParseAgents("pi, kimi ,, qoder ").SequenceEqual(new[] { "pi", "kimi", "qoder" }), "agent parsing lost entries");
        });

        test("H4 sync mode round trips all values and preserves fallback on unknown text", () =>
        {
            foreach (var mode in new[] { WorkspaceSyncMode.None, WorkspaceSyncMode.Mutagen, WorkspaceSyncMode.SshfsMount })
                assert(RemoteHostEditor.ParseSyncMode(RemoteHostEditor.SyncModeText(mode), WorkspaceSyncMode.Mutagen) == mode, "sync mode lost: " + mode);
            assert(RemoteHostEditor.ParseSyncMode("sshfs", WorkspaceSyncMode.None) == WorkspaceSyncMode.SshfsMount, "sshfs not parsed");
            assert(RemoteHostEditor.ParseSyncMode("typo", WorkspaceSyncMode.SshfsMount) == WorkspaceSyncMode.SshfsMount, "unknown sync mode replaced original");
        });

        test("H5 duplicate detection matches name or address and excludes the edited host", () =>
        {
            var id = Guid.NewGuid();
            var hosts = new[] { new RemoteHost(id, "示例 VPS A", "203.0.113.10", 22, "root", "agent", ["pi"], null) };
            assert(RemoteHostEditor.Duplicate(hosts, "示例 VPS A", "10.0.0.2") is not null, "duplicate name accepted");
            assert(RemoteHostEditor.Duplicate(hosts, "Other", "203.0.113.10") is not null, "duplicate address accepted");
            assert(RemoteHostEditor.Duplicate(hosts, "示例 VPS A", "203.0.113.10", id) is null, "editing host flagged as its own duplicate");
            assert(RemoteHostEditor.Duplicate(hosts, "Other", "10.0.0.2") is null, "unique host rejected");
        });

        test("H6 delete reference check follows stable id after rename", () =>
        {
            var id = Guid.NewGuid();
            var slots = new (string Name, Guid? HostId)[] { ("renamed host still bound", id), ("local", null), ("other host", Guid.NewGuid()) };
            var names = RemoteHostEditor.ReferencingSlotNames(slots, id);
            assert(names.SequenceEqual(new[] { "renamed host still bound" }), "rename detached slot reference");
            assert(RemoteHostEditor.ReferencingSlotNames(slots, Guid.NewGuid()).Length == 0, "unreferenced host reported as used");
        });
    }
}

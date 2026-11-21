using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{

    // 对标 claude-squad / agent-deck 的“已安装即可用”：扫描一次本机 CLI，把结果填进空白且未勾选的槽位。
    // 只改完全空白的默认槽位；有备注、目录、程序配置或远程主机的槽位一律保留。
    private bool addingInstalledAgents;
    private async Task AddInstalledAgentsAsync(IReadOnlyList<string>? searchDirs = null)
    {
        if (!CanEditSlots || addingInstalledAgents) return;
        addingInstalledAgents = true;
        try { await AddInstalledAgentsCoreAsync(searchDirs); }
        finally { addingInstalledAgents = false; }
    }

    private async Task AddInstalledAgentsCoreAsync(IReadOnlyList<string>? searchDirs)
    {
        var directories = searchDirs ?? Commands.InstallSearchDirs();
        var installs = await Task.Run(() => Agents.All
            .Where(agent => agent.Id != "custom" && agent.Command.Length > 0)
            .ToDictionary(agent => agent.Id, agent => Commands.DiscoverInstalls(agent.Command, directories).ToArray()));
        discoveredInstalls = installs;
        RefreshAgentChoices();
        var found = Agents.All.Where(agent => installs.GetValueOrDefault(agent.Id)?.Length > 0).Select(agent => agent.Name).ToArray();
        DiscoveryStatus.Text = found.Length == 0
            ? "没有找到本机 CLI；可在详情中选择已安装的程序"
            : "已找到 " + string.Join("、", found) + " · 补齐时按任务使用频率优先，版本与登录状态未验证";
        if (found.Length == 0) { ProgressText.Text = "没有找到本机已安装的 Agent 命令。"; return; }

        var taskCounts = taskBook?.Tasks
            .GroupBy(task => RemoteAgentMaintenance.CatalogId(task.AgentId))
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var catalogOrder = Agents.All.Select((agent, index) => (agent.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.OrdinalIgnoreCase);
        var present = config.Draft.Slots
            .Where(slot => slot.Enabled || !IsBlankSlot(slot))
            .Select(slot => RemoteAgentMaintenance.CatalogId(slot.AgentId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var blankSlots = config.Draft.Slots.Where(IsBlankSlot).ToList();
        var ranked = installs.Keys
            .Where(id => installs.GetValueOrDefault(id)?.Length > 0)
            .OrderByDescending(id => taskCounts.GetValueOrDefault(id))
            .ThenBy(id => config.RecentAgents.IndexOf(id) is var index && index >= 0 ? index : int.MaxValue)
            .ThenBy(id => catalogOrder.GetValueOrDefault(id))
            .ToArray();
        var targets = new List<(Guid? SlotId, string AgentId)>();

        // 先复用“已经写着这个 Agent”的空白默认槽位（例如默认配置里的 codex/hermes），
        // 否则后续 Agent 可能抢占这些槽位，造成同一 Agent 出现两份配置。
        foreach (var agentId in ranked)
        {
            if (present.Contains(agentId)) continue;
            var same = blankSlots.FirstOrDefault(slot =>
                string.Equals(RemoteAgentMaintenance.CatalogId(slot.AgentId), agentId, StringComparison.OrdinalIgnoreCase));
            if (same is null) continue;
            targets.Add((same.Id, agentId));
            blankSlots.Remove(same);
            present.Add(agentId);
        }

        var skipped = new List<string>();
        foreach (var agentId in ranked)
        {
            if (present.Contains(agentId)) continue;
            if (blankSlots.Count > 0)
            {
                var blank = blankSlots[0];
                targets.Add((blank.Id, agentId));
                blankSlots.RemoveAt(0);
            }
            else if (config.Draft.Slots.Count + targets.Count(target => target.SlotId is null) < ResourcePolicy.MaxSlots)
            {
                targets.Add(((Guid?)null, agentId));
            }
            else
            {
                skipped.Add(Agents.Get(agentId).Name);
                continue;
            }
            present.Add(agentId);
        }

        if (targets.Count == 0)
        {
            ProgressText.Text = skipped.Count == 0
                ? "已安装的 Agent 都已在当前配置中。"
                : "槽位已达上限，未加入：" + string.Join("、", skipped) + "。";
            return;
        }

        var created = new List<Guid>();
        if (!Edit(draft =>
            {
                foreach (var (slotId, agentId) in targets)
                {
                    if (slotId is { } existing)
                    {
                        var slot = draft.Slots.Single(item => item.Id == existing);
                        slot.AgentId = agentId;
                        slot.Enabled = true;
                        slot.CustomName = null;
                    }
                    else
                    {
                        var slot = new Slot { AgentId = agentId, Enabled = true };
                        draft.Slots.Add(slot);
                        created.Add(slot.Id);
                    }
                }
            })) return;
        var names = targets.Select(target => Agents.Get(target.AgentId).Name).ToArray();
        var message = $"已补齐 {string.Join("、", names)}；只改了空白槽位，其他配置保持不变。";
        if (skipped.Count > 0) message += " 槽位已达上限，未加入：" + string.Join("、", skipped) + "。";
        ProgressText.Text = message;
        var lastId = targets[^1].SlotId ?? created.LastOrDefault();
        if (lastId != Guid.Empty && config.Draft.Slots.FirstOrDefault(slot => slot.Id == lastId) is { } selected) SelectNewSlot(selected.Id);
    }

    private static bool IsBlankSlot(Slot slot) =>
        !slot.Enabled && slot.Directory.Length == 0 && slot.Note.Length == 0
        && slot.RemoteHostId is null && slot.RemoteDirectory is null && slot.Profiles.Count == 0 && slot.CustomName is null;
    private bool CanEditSlots => !loading && !busy && !readOnly && !closeRequested;
    private bool CanAddSlot => CanEditSlots && config.Draft.Slots.Count < ResourcePolicy.MaxSlots;
    private bool CanEditSlot(Guid id) => CanEditSlots && config.Draft.Slots.Any(s => s.Id == id);

    private void AddAgentSlot()
    {
        if (!CanAddSlot) return;
        var slot = new Slot();
        if (!Edit(d => d.Slots.Add(slot))) return;
        SelectNewSlot(slot.Id);
        ProgressText.Text = "已添加 Agent，请选择类型；勾选后才会加入本次启动。";
    }

    private void DuplicateSlot(Guid id)
    {
        if (!CanAddSlot || config.Draft.Slots.FirstOrDefault(s => s.Id == id) is not { } source) return;
        var duplicate = source.Duplicate();
        var baseName = duplicate.DisplayName;
        var names = config.Draft.Slots.Select(s => s.DisplayName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var suffix = 2; names.Contains(duplicate.DisplayName); suffix++) duplicate.CustomName = baseName + " " + suffix;
        if (!Edit(d => d.Slots.Insert(d.Slots.FindIndex(s => s.Id == id) + 1, duplicate))) return;
        SelectNewSlot(duplicate.Id);
        ProgressText.Text = "已复制配置，副本尚未勾选。请确认名称和工作目录后再启动。";
    }

    private void SelectNewSlot(Guid id)
    {
        NavigateToSlot(id);
        FindLogical(SlotList).OfType<ComboBox>().FirstOrDefault()?.Focus();
        FindLogical(RailSlots).OfType<Button>().FirstOrDefault(b => Equals(b.Tag, id))?.BringIntoView();
    }

    private FormWindow CreateSlotNameDialog(Slot slot)
    {
        var dialog = new FormWindow(this, "重命名 Agent", [("name", "分工名称（留空使用 Agent 名称）", slot.CustomName ?? "", false)],
            $"Agent 类型：{Agents.Get(slot.AgentId).Name}。例如「前端开发」「接口实现」「代码审查」。名称用于配置和后续启动记录，分工指令仍需在终端中输入。", applyLabel: "保存名称");
        dialog.Height = Math.Min(360 * UiScale.Value, SystemParameters.WorkArea.Height - 32);
        dialog.Loaded += (_, _) => FindLogical(dialog).OfType<TextBox>().FirstOrDefault()?.SelectAll();
        return dialog;
    }

    private void RenameSlot(Guid id)
    {
        if (!CanEditSlot(id)) return;
        var revision = config.Draft.Revision;
        var dialog = CreateSlotNameDialog(config.Draft.Slots.Single(s => s.Id == id));
        if (dialog.ShowDialog() == true)
        {
            try { ApplySlotName(id, dialog.Values["name"], revision); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { ErrorText.Text = ex.Message; }
        }
    }

    private bool ApplySlotName(Guid id, string name, long revision)
    {
        if (!CanEditSlot(id)) return false;
        if (revision != config.Draft.Revision) { ErrorText.Text = "配置已改变，请重新打开重命名。"; return false; }
        return Edit(d => d.Slots.Single(s => s.Id == id).CustomName = string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    private CheckBox CreateSlotToggle(Slot slot, int position, bool inRail)
    {
        var toggle = new CheckBox
        {
            IsChecked = slot.Enabled, IsEnabled = CanEditSlots, Tag = slot.Id,
            VerticalAlignment = VerticalAlignment.Center, MinHeight = 36,
            ToolTip = $"{position:00} · {slot.DisplayName} · {Agents.Get(slot.AgentId).Name}\n勾选加入本次启动；取消勾选不结束已运行的会话"
        };
        AutomationProperties.SetName(toggle, $"第 {position} 项{(inRail ? "侧栏" : "")}本次启动");
        // UIA TogglePattern raises Checked/Unchecked without Click. Both controls share the same guarded transaction.
        void OnToggled(object sender, RoutedEventArgs e)
        {
            if (rendering) return;
            var value = toggle.IsChecked == true;
            if (CanEditSlot(slot.Id) && Edit(d => d.Slots.Single(s => s.Id == slot.Id).Enabled = value)) return;
            var wasRendering = rendering; rendering = true;
            try { toggle.IsChecked = config.Draft.Slots.FirstOrDefault(s => s.Id == slot.Id)?.Enabled ?? slot.Enabled; }
            finally { rendering = wasRendering; }
        }
        toggle.Checked += OnToggled; toggle.Unchecked += OnToggled;
        return toggle;
    }
}

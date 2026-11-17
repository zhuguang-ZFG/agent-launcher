using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using AgentLauncher.Core;

namespace AgentLauncher.App;

public partial class MainWindow
{
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

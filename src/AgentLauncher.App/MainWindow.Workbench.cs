using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using AgentLauncher.Core;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private MainWindowMode windowMode = MainWindowMode.StartPage;
    private readonly Dictionary<Guid, (bool Ready, string Reason)> chipStatus = [];

    private void ApplyWindowMode()
    {
        var start = windowMode == MainWindowMode.StartPage;
        StartPagePanel.Visibility = start ? Visibility.Visible : Visibility.Collapsed;
        WorkbenchPanel.Visibility = start ? Visibility.Collapsed : Visibility.Visible;
        LegacyWorkspace.Visibility = Visibility.Collapsed;
        Rail.Visibility = Visibility.Collapsed;
        PreviewCard.Visibility = Visibility.Collapsed;
        Preview.Visibility = Visibility.Collapsed;
        RefreshLaunchButton.Visibility = Visibility.Collapsed;
        LaunchOptionsExpander.Visibility = Visibility.Collapsed;
        ConfigurationToolsExpander.Visibility = Visibility.Collapsed;
        SessionExpander.Visibility = Visibility.Collapsed;
        QueueQuickButton.Visibility = Visibility.Collapsed;
        if (start) ChooseProjectButton.Visibility = Visibility.Visible;
    }

    private int CountReadySelected() =>
        config.Draft.Slots.Count(slot => slot.Enabled && IsSlotReady(slot));

    private bool IsSlotReady(Slot slot) =>
        chipStatus.TryGetValue(slot.Id, out var status) && status.Ready;

    private string SlotChipReason(Slot slot) =>
        chipStatus.TryGetValue(slot.Id, out var status) ? status.Reason : (commandsChecking ? "正在检查" : "暂不清楚");

    private int? RemainingCapacity(int alreadySelected)
    {
        if (status?.Memory is not { } memory || !ResourcePolicy.IsSampleValid(memory)) return null;
        var room = Math.Max(0, WorkbenchLayout.MaxPanes - alreadySelected);
        var remaining = 0;
        for (var extra = 1; extra <= room; extra++)
        {
            var draft = config.Draft.Copy();
            var enabled = draft.Slots.Count(slot => slot.Enabled);
            while (draft.Slots.Count(slot => slot.Enabled) < enabled + extra)
                draft.Slots.Add(new Slot { Enabled = true, AgentId = draft.Slots.FirstOrDefault()?.AgentId ?? "custom" });
            var decision = DecideAdmission(draft, memory, status.Entries, budgets: status.Budgets);
            if (!decision.Launchable) break;
            remaining = extra;
        }
        return remaining;
    }

    private void RenderAgentChips()
    {
        if (AgentChipPanel is null) return;
        var previous = rendering;
        rendering = true;
        try
        {
            AgentChipPanel.Children.Clear();
            foreach (var slot in config.Draft.Slots)
            {
                var ready = IsSlotReady(slot);
                var reason = SlotChipReason(slot);
                var place = slot.RemoteHostId is null ? "本机" : (PlaceLabel(slot) ?? "远程");
                var chip = new ToggleButton
                {
                    Tag = slot.Id,
                    Margin = new Thickness(0, 0, 12, 12),
                    Padding = new Thickness(12, 10, 12, 10),
                    MinWidth = 168,
                    MinHeight = 72,
                    HorizontalContentAlignment = HorizontalAlignment.Left,
                    VerticalContentAlignment = VerticalAlignment.Top,
                    IsChecked = slot.Enabled,
                    IsEnabled = ready && !readOnly && !busy && !loading && !closeRequested
                };
                AutomationProperties.SetName(chip, slot.DisplayName);
                var body = new StackPanel();
                body.Children.Add(new TextBlock { Text = slot.DisplayName, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis });
                body.Children.Add(new TextBlock { Text = place, Margin = new Thickness(0, 4, 0, 0), Style = (Style)FindResource("Caption") });
                var state = new TextBlock { Text = ready ? "就绪" : "未就绪 · " + reason, Margin = new Thickness(0, 2, 0, 0), TextWrapping = TextWrapping.Wrap };
                state.SetResourceReference(TextBlock.ForegroundProperty, ready ? "Muted" : "WarningInk");
                body.Children.Add(state);
                chip.Content = body;
                chip.ToolTip = ready ? place : reason;
                var id = slot.Id;
                chip.Checked += (_, _) => { if (!rendering) TrySetSlotEnabled(id, true, chip); };
                chip.Unchecked += (_, _) => { if (!rendering) TrySetSlotEnabled(id, false, chip); };
                AgentChipPanel.Children.Add(chip);
            }
        }
        finally { rendering = previous; }
    }

    private bool TrySetSlotEnabled(Guid id, bool enable, ToggleButton? chip)
    {
        var slot = config.Draft.Slots.FirstOrDefault(item => item.Id == id);
        if (slot is null) return false;
        if (enable)
        {
            var already = config.Draft.Slots.Count(item => item.Enabled && item.Id != id && IsSlotReady(item));
            if (!StartPageSurface.CanSelect(already, IsSlotReady(slot)))
            {
                LaunchHint.Text = StartPageSurface.CapDenied;
                if (chip is not null && chip.IsChecked != false)
                {
                    var previous = rendering;
                    rendering = true;
                    chip.IsChecked = false;
                    rendering = previous;
                }
                return false;
            }
        }
        return Edit(draft => draft.Slots.Single(item => item.Id == id).Enabled = enable);
    }

#if INTEGRATION_TESTS
    private void TrySelectOverCapReadyAgent()
    {
        var candidate = config.Draft.Slots.FirstOrDefault(slot => !slot.Enabled && IsSlotReady(slot))
            ?? config.Draft.Slots.FirstOrDefault(slot => !slot.Enabled);
        if (candidate is null) return;
        var chip = FindLogical(AgentChipPanel).OfType<ToggleButton>().FirstOrDefault(button => button.Tag is Guid id && id == candidate.Id);
        if (chip is null) return;
        chip.IsChecked = true;
    }
#endif
}
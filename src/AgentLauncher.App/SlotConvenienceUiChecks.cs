#if INTEGRATION_TESTS
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task RunSlotConvenienceChecksAsync(string folder, Action<string, bool> check, Func<double, double, Task> resize)
    {
        var original = config; var originalSlot = selectedSlotId;
        var originalReadOnly = readOnly; var originalBusy = busy; var originalClose = closeRequested;
        var originalWidth = Width; var originalHeight = Height;
        var wasPolling = timer.IsEnabled; var wasPumping = taskTimer.IsEnabled;
        timer.Stop(); taskTimer.Stop(); await saveTask;
        var diskBefore = File.Exists(configPath) ? File.ReadAllBytes(configPath) : null;
        try
        {
            config = new Configuration { Theme = "dark" };
            config.Draft.Project = Store.Root;
            config.Draft.Slots = [new() { AgentId = "codex", CustomName = "接口实现", Enabled = true, Directory = Store.Root, Note = "核对请求格式", Profiles = new() { ["codex"] = new() { Arguments = ["--model", "model with spaces"] }, ["pi"] = new() { Arguments = ["--continue"] } } }, new() { AgentId = "pi" }];
            readOnly = false; busy = false; closeRequested = false; ApplyTheme(); Render();
            await resize(1120, 800);
            var sourceId = config.Draft.Slots[0].Id; var nextId = config.Draft.Slots[1].Id;
            var sourceBefore = JsonSerializer.Serialize(config.Draft.Slots[0], Store.Json);
            var menu = FindLogical(SlotList).OfType<MenuButton>().Single();
            ((Button)menu.Content).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var duplicateItem = menu.menu.Items.OfType<MenuItem>().Single(item => item.Header as string == "复制此项配置");
            check("UX11 copy and rename are available in the selected Agent menu", duplicateItem.IsEnabled && menu.MenuLabels.Contains("重命名…"));
            menu.menu.IsOpen = false; duplicateItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            var duplicate = config.Draft.Slots[1];
            check("UX11 copy inserts next to source and selects the disabled new configuration", config.Draft.Slots.Count == 3 && duplicate.Id != sourceId && config.Draft.Slots[2].Id == nextId && !duplicate.Enabled && selectedSlotId == duplicate.Id && DetailTitle.Text.Contains("接口实现 副本"));
            check("UX11 copying keeps source and structured configuration intact", JsonSerializer.Serialize(config.Draft.Slots[0], Store.Json) == sourceBefore && duplicate.Directory == Store.Root && duplicate.Note == "核对请求格式" && duplicate.Current.Arguments.SequenceEqual(new[] { "--model", "model with spaces" }) && duplicate.Profiles.ContainsKey("pi"));
            check("UX11 new configuration gives keyboard focus to Agent selection", Keyboard.FocusedElement is ComboBox);
            DuplicateSlot(sourceId);
            check("UX11 repeat copies get distinguishable names", config.Draft.Slots[1].DisplayName == "接口实现 副本 2" && config.Draft.Slots.Select(s => s.Id).Distinct().Count() == 4);
            AddClick(this, new());
            check("UX11 add immediately selects the new disabled Agent", selectedSlotId == config.Draft.Slots[^1].Id && !config.Draft.Slots[^1].Enabled && Keyboard.FocusedElement is ComboBox && DetailTitle.Text.StartsWith("05"));

            NavigateToSlot(sourceId);
            var revision = config.Draft.Revision;
            var originalStatus = status; var originalBook = taskBook;
            CheckBox RailToggle(Guid id) => FindLogical(RailSlots).OfType<CheckBox>().Single(c => Equals(c.Tag, id));
            var toggle = RailToggle(nextId); toggle.Focus();
            ((IToggleProvider)new CheckBoxAutomationPeer(toggle)).Toggle();
            check("UX11 sidebar UIA toggle updates selection without navigating or launching", config.Draft.Slots.Single(s => s.Id == nextId).Enabled && selectedSlotId == sourceId && config.Draft.Revision == revision + 1 && SelectionSummary.Text == "已选 2 个，共 5 个" && ReferenceEquals(status, originalStatus) && ReferenceEquals(taskBook, originalBook));
            check("UX11 sidebar toggle retains keyboard focus after render", Keyboard.FocusedElement is CheckBox focused && Equals(focused.Tag, nextId));
            var row = FindLogical(RailSlots).OfType<Button>().Single(b => Equals(b.Tag, nextId));
            row.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            check("UX11 sidebar navigation does not toggle checkbox", config.Draft.Slots.Single(s => s.Id == nextId).Enabled && config.Draft.Revision == revision + 1 && selectedSlotId == nextId);
            check("UX11 detail checkbox mirrors sidebar selection", FindLogical(SlotList).OfType<CheckBox>().Single().IsChecked == true);

            void ExerciseRename(Action<FormWindow> action)
            {
                Exception? failure = null;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var dialog = OwnedWindows.OfType<FormWindow>().Single();
                    try { action(dialog); }
                    catch (Exception ex) { failure = ex; }
                    finally { if (dialog.IsVisible) dialog.Close(); }
                }), DispatcherPriority.ApplicationIdle);
                RenameSlot(sourceId);
                if (failure is not null) throw failure;
            }
            ExerciseRename(dialog =>
            {
                var input = FindLogical(dialog).OfType<TextBox>().Single();
                check("UX11 rename opens directly with focused selected name", input.IsKeyboardFocused && input.SelectedText == "接口实现" && dialog.Title == "重命名 Agent");
                input.Text = "  代码审查  ";
                FindLogical(dialog).OfType<Button>().Single(b => b.Content as string == "保存名称").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            check("UX11 rename applies to built-in Agent while preserving program and arguments", config.Draft.Slots[0].DisplayName == "代码审查" && config.Draft.Slots[0].AgentId == "codex" && config.Draft.Slots[0].Current.Arguments.SequenceEqual(new[] { "--model", "model with spaces" }));
            var actions = BuildQuickActions();
            check("UX11 quick search can find role and underlying Agent type", actions.Single(a => a.Id == "slot:" + sourceId) is { Title: var title, Detail: var detail } && title.Contains("代码审查") && detail.Contains("Codex") && actions.Any(a => a.Id == "duplicate-agent") && actions.Any(a => a.Id == "rename-agent"));
            revision = config.Draft.Revision;
            ExerciseRename(dialog => FindLogical(dialog).OfType<TextBox>().Single().Text = "取消的修改");
            check("UX11 cancelling rename leaves draft untouched", config.Draft.Revision == revision && config.Draft.Slots[0].DisplayName == "代码审查");
            ExerciseRename(dialog =>
            {
                config.Edit(d => d.Slots[0].Note = "newer edit");
                FindLogical(dialog).OfType<TextBox>().Single().Text = "过期名称";
                FindLogical(dialog).OfType<Button>().Single(b => b.Content as string == "保存名称").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            check("UX11 stale rename refuses to overwrite newer edits", config.Draft.Slots[0].DisplayName == "代码审查" && config.Draft.Slots[0].Note == "newer edit" && ErrorText.Text.Contains("配置已改变"));
            ApplySlotName(sourceId, " ", config.Draft.Revision);
            check("UX11 clearing role restores the catalog name", config.Draft.Slots[0].CustomName is null && config.Draft.Slots[0].DisplayName == "Codex");
            ApplySlotName(sourceId, "代码审查", config.Draft.Revision);

            foreach (var state in new[] { "read-only", "busy", "closing" })
            {
                revision = config.Draft.Revision; var count = config.Draft.Slots.Count;
                readOnly = state == "read-only"; busy = state == "busy"; closeRequested = state == "closing";
                AddAgentSlot(); DuplicateSlot(sourceId); ApplySlotName(sourceId, "refused", revision);
                RailToggle(nextId).IsChecked = false;
                check($"UX11 {state} refuses new edits and reverts sidebar toggles", config.Draft.Revision == revision && config.Draft.Slots.Count == count && config.Draft.Slots[0].DisplayName == "代码审查" && RailToggle(nextId).IsChecked == true && !actions.Single(a => a.Id == "duplicate-agent").Enabled);
            }
            readOnly = false; busy = false; closeRequested = false;
            await saveTask;
            var saved = Store.Read<Configuration>(configPath)!;
            check("UX11 additions names and checked state persist through atomic save", saved.Draft.Slots.Count == 5 && saved.Draft.Slots[0].DisplayName == "代码审查" && saved.Draft.Slots.Single(s => s.Id == nextId).Enabled);

            NavigateToSlot(sourceId); ContentScroll.ScrollToTop();
            foreach (var scale in new[] { 100, 150 })
            {
                config.TextScalePercent = scale; ApplyTheme(); Render(); await resize(650, 700);
                var checkbox = RailToggle(sourceId); row = FindLogical(RailSlots).OfType<Button>().Single(b => Equals(b.Tag, sourceId));
                var checkboxPoint = checkbox.TranslatePoint(new Point(), Rail); var rowPoint = row.TranslatePoint(new Point(), Rail);
                check($"UX11 narrow sidebar keeps independent checkbox and navigation targets at {scale}%", checkbox.IsVisible && row.IsVisible && checkbox.ActualWidth >= 20 && row.ActualWidth >= 18 && checkboxPoint.X + checkbox.ActualWidth <= rowPoint.X + 1 && rowPoint.X + row.ActualWidth <= Rail.ActualWidth);
                CaptureQuickActions(this, Path.Combine(folder, $"slot-convenience-{scale}.png"));
            }
            config.TextScalePercent = 100; ApplyTheme(); Render(); await resize(1120, 800); ContentScroll.ScrollToTop(); UpdateLayout();
            var textPoint = QuickActionsText.TranslatePoint(new Point(), QuickActionsButton);
            Store.Save(Path.Combine(folder, "slot-header-layout.json"), new { Scale = UiScale.Value, ActualWidth, Text = QuickActionsText.Text, TextWidth = QuickActionsText.ActualWidth, TextDesired = QuickActionsText.DesiredSize.Width, TextX = textPoint.X, ButtonWidth = QuickActionsButton.ActualWidth, ButtonDesired = QuickActionsButton.DesiredSize.Width, ButtonX = QuickActionsButton.TranslatePoint(new Point(), this).X, ParentWidth = ((Grid)QuickActionsButton.Parent).ActualWidth, ParentDesired = ((Grid)QuickActionsButton.Parent).DesiredSize.Width, ProjectWidth = ProjectSwitchButton.ActualWidth, ProjectDesired = ProjectSwitchButton.DesiredSize.Width });
            check("UX11 quick action label fits after restoring wide layout from large text", QuickActionsText.Text == "快捷操作  Ctrl+K" && textPoint.X >= 0 && textPoint.X + QuickActionsText.ActualWidth <= QuickActionsButton.ActualWidth);
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
            CaptureQuickActions(this, Path.Combine(folder, "slot-convenience-wide.png"));

            while (config.Draft.Slots.Count < ResourcePolicy.MaxSlots) AddAgentSlot();
            revision = config.Draft.Revision; DuplicateSlot(sourceId); AddAgentSlot();
            check("UX11 full draft disables add and copy and rejects stale action snapshots", config.Draft.Slots.Count == ResourcePolicy.MaxSlots && config.Draft.Revision == revision && !AddButton.IsEnabled && !actions.Single(a => a.Id == "duplicate-agent").Enabled);
            foreach (var scale in new[] { 100, 150 })
            {
                config.TextScalePercent = scale; ApplyTheme(); Render(); await resize(650, 700);
                var lastId = config.Draft.Slots[^1].Id;
                row = FindLogical(RailSlots).OfType<Button>().Single(b => Equals(b.Tag, lastId)); row.BringIntoView();
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
                var checkbox = RailToggle(lastId); var origin = row.TranslatePoint(new Point(), Rail);
                check($"UX11 full narrow sidebar retains both targets beside scrollbar at {scale}%", checkbox.ActualWidth >= 20 && row.ActualWidth >= 18 && origin.X + row.ActualWidth <= Rail.ActualWidth && origin.Y >= 0 && origin.Y + row.ActualHeight <= Rail.ActualHeight);
                CaptureQuickActions(this, Path.Combine(folder, $"slot-convenience-full-{scale}.png"));
            }
        }
        finally
        {
            readOnly = originalReadOnly; busy = originalBusy; closeRequested = originalClose;
            await saveTask; config = original; selectedSlotId = originalSlot;
            if (diskBefore is not null) File.WriteAllBytes(configPath, diskBefore);
            else if (File.Exists(configPath)) File.Delete(configPath);
            ApplyTheme(); Render(); await resize(originalWidth, originalHeight);
            if (wasPolling) timer.Start(); if (wasPumping) taskTimer.Start();
        }
    }
}
#endif

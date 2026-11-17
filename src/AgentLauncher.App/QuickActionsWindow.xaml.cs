using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace AgentLauncher.App;

internal sealed record QuickAction(string Id, string Title, string Detail, Func<Task> Run, Func<bool>? Available = null)
{
    public bool Enabled => Available?.Invoke() ?? true;
}

public partial class QuickActionsWindow : Window
{
    private readonly QuickAction[] actions;
    internal QuickAction? Chosen { get; private set; }

    internal QuickActionsWindow(Window owner, IEnumerable<QuickAction> actions)
    {
        this.actions = actions.ToArray();
        InitializeComponent(); Owner = owner;
        Style = (Style)Application.Current.Resources[typeof(Window)];
        Width = Math.Min(620 * UiScale.Value, SystemParameters.WorkArea.Width - 32);
        Height = Math.Min(480 * UiScale.Value, SystemParameters.WorkArea.Height - 32);
        MinWidth = Math.Min(360 * UiScale.Value, Width); MinHeight = Math.Min(260 * UiScale.Value, Height);
        UiScale.Apply(this);
        Loaded += (_, _) => SearchBox.Focus();
        Filter();
    }

    private void SearchChanged(object sender, TextChangedEventArgs e) { if (ActionList is not null) Filter(); }
    private void Filter()
    {
        var words = SearchBox.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var matches = actions.Where(action => words.All(word => (action.Title + " " + action.Detail).Contains(word, StringComparison.OrdinalIgnoreCase))).ToArray();
        ActionList.ItemsSource = matches; ActionList.SelectedIndex = matches.Length > 0 ? 0 : -1;
        EmptyText.Visibility = matches.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ResultText.Text = $"{matches.Length} 项 · ↑↓ 选择 · Enter 打开 · Esc 返回";
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenButton is not null) OpenButton.IsEnabled = ActionList.SelectedItem is QuickAction { Enabled: true };
    }
    private void OpenClick(object sender, RoutedEventArgs e) => Accept();
    private void OpenDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(ActionList, e.OriginalSource as DependencyObject) is ListBoxItem) Accept();
    }
    private void Accept()
    {
        if (ActionList.SelectedItem is not QuickAction action) return;
        if (!action.Enabled) { ResultText.Text = "当前操作不可用，请等待正在进行的操作完成"; OpenButton.IsEnabled = false; return; }
        Chosen = action; DialogResult = true;
    }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        if (e.Key is Key.Down or Key.Up && SearchBox.IsKeyboardFocusWithin)
        {
            if (ActionList.Items.Count > 0)
            {
                ActionList.SelectedIndex = Math.Clamp(ActionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, ActionList.Items.Count - 1);
                ActionList.ScrollIntoView(ActionList.SelectedItem);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (SearchBox.IsKeyboardFocusWithin || ActionList.IsKeyboardFocusWithin))
        {
            e.Handled = true;
            if (!e.IsRepeat) Accept();
        }
    }
}

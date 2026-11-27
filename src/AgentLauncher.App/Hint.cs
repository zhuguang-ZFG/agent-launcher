using System.Windows;

namespace AgentLauncher.App;

// 输入框为空时显示的提示文字；只影响外观，不参与取值、校验或自动化名称。
public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached("Text", typeof(string), typeof(Hint), new PropertyMetadata(""));
    public static string GetText(DependencyObject element) => (string)element.GetValue(TextProperty);
    public static void SetText(DependencyObject element, string value) => element.SetValue(TextProperty, value);
}

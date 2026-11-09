using System.Windows;
using System.Windows.Media;

namespace AgentLauncher.App;
internal static class UiScale
{
    internal static double Value { get; set; }=1;
    internal static void Apply(Window window)
    {
        if(window.Content is FrameworkElement content)content.LayoutTransform=Value==1?Transform.Identity:new ScaleTransform(Value,Value);
    }
}

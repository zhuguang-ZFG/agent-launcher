#if INTEGRATION_TESTS
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AgentLauncher.Core;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task CheckCompactAgentLabelsAsync(Action<string,bool> check,int scale,string folder)
    {
        static IEnumerable<T> Visuals<T>(DependencyObject parent) where T:DependencyObject
        {
            for(var i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
            {
                var child=VisualTreeHelper.GetChild(parent,i);if(child is T value)yield return value;
                foreach(var nested in Visuals<T>(child))yield return nested;
            }
        }
        var slot=config.Draft.Slots.Single(item=>item.Id==selectedSlotId);
        var original=slot.AgentId;
        var results=new List<object>();var allReadable=true;
        try
        {
            foreach(var agent in Agents.All.Where(agent=>Agents.IsSupported(agent.Id)))
            {
                slot.AgentId=agent.Id;Render();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);
                var combo=Visuals<ComboBox>(SlotList).Single(value=>AutomationProperties.GetName(value).Contains("项 Agent"));
                combo.ApplyTemplate();var presenter=combo.Template.FindName("SelectionBox",combo) as ContentPresenter;
                var label=presenter is null?null:Visuals<TextBlock>(presenter).FirstOrDefault();
                var readable=false;
                var requiredWidth=0d;var visibleWidth=0d;
                if(label is not null&&label.Text.StartsWith(agent.Name,StringComparison.Ordinal))
                {
                    var required=new FormattedText(agent.Name,CultureInfo.CurrentUICulture,label.FlowDirection,
                        new Typeface(label.FontFamily,label.FontStyle,label.FontWeight,label.FontStretch),label.FontSize,label.Foreground,VisualTreeHelper.GetDpi(label).PixelsPerDip);
                    var visible=new Rect(0,0,label.ActualWidth,label.ActualHeight);
                    // A control can fit the card while its selected text is clipped by an inner DockPanel.
                    // Intersect every real layout bound, then require the entire rendered Agent name on one line.
                    for(DependencyObject? ancestor=VisualTreeHelper.GetParent(label);ancestor is not null;ancestor=VisualTreeHelper.GetParent(ancestor))
                    {
                        if(ancestor is FrameworkElement element)
                        {
                            var bounds=element.TransformToDescendant(label).TransformBounds(new Rect(new Point(),element.RenderSize));
                            visible.Intersect(bounds);
                        }
                        if(ReferenceEquals(ancestor,SlotList))break;
                    }
                    readable=label.TextWrapping==TextWrapping.NoWrap&&!visible.IsEmpty&&visible.Left<=0.5&&visible.Top<=0.5&&
                        visible.Right+0.5>=required.WidthIncludingTrailingWhitespace&&visible.Height+0.5>=required.Height;
                    requiredWidth=required.WidthIncludingTrailingWhitespace;visibleWidth=visible.Width;
                }
                allReadable&=readable;results.Add(new{Agent=agent.Name,Text=label?.Text,Passed=readable,RequiredNameWidth=requiredWidth,VisibleTextWidth=visibleWidth});
                check($"UX17 {agent.Name} selected name is fully readable at compact {scale}%",readable);
                if(scale==150)
                {
                    var dpi=VisualTreeHelper.GetDpi(this);
                    var bitmap=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
                    bitmap.Render(this);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var output=File.Create(Path.Combine(folder,$"native-agent-{agent.Id}-{scale}.png"));encoder.Save(output);
                }
            }
            File.WriteAllText(Path.Combine(folder,$"native-agent-labels-{scale}.json"),System.Text.Json.JsonSerializer.Serialize(new{Scale=scale,Width=ActualWidth,Passed=allReadable,Results=results}));
        }
        finally{slot.AgentId=original;Render();await Dispatcher.InvokeAsync(UpdateLayout,DispatcherPriority.Render);}
    }
}
#endif

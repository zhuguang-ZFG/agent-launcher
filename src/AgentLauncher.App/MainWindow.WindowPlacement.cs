using System.IO;
using System.Windows;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

// 记住操作员上次把配置窗口放在哪里。配置文件仍是唯一业务来源；窗口几何单独存放，
// 读取失败、尺寸过小或完全移出当前虚拟桌面时回退到现有默认位置，不阻止启动。
public partial class MainWindow
{
    private sealed record WindowPlacement(double Left,double Top,double Width,double Height,bool Maximized);
    private const string WindowPlacementFile="window-placement.json";

    private void ApplyWindowPlacement()
    {
        try
        {
            var saved=Store.Read<WindowPlacement>(Store.FilePath(WindowPlacementFile));
            if(saved is null)return;
            if(!IsFinite(saved.Left)||!IsFinite(saved.Top)||!IsFinite(saved.Width)||!IsFinite(saved.Height))return;
            var screen=new Rect(SystemParameters.VirtualScreenLeft,SystemParameters.VirtualScreenTop,SystemParameters.VirtualScreenWidth,SystemParameters.VirtualScreenHeight);
            if(screen.Width<=0||screen.Height<=0)return;
            var width=Math.Min(saved.Width,screen.Width);
            var height=Math.Min(saved.Height,screen.Height);
            if(width<MinWidth||height<MinHeight)return;
            var savedRect=new Rect(saved.Left,saved.Top,width,height);
            if(!savedRect.IntersectsWith(screen))return;
            WindowStartupLocation=WindowStartupLocation.Manual;
            Left=Math.Clamp(saved.Left,screen.Left,screen.Right-width);
            Top=Math.Clamp(saved.Top,screen.Top,screen.Bottom-height);
            Width=width;
            Height=height;
            if(saved.Maximized)WindowState=WindowState.Maximized;
        }
        catch(Exception e)when(e is not OutOfMemoryException){/* 单个 UI 记录损坏时保留默认窗口，不阻止启动。 */}
    }

    private void SaveWindowPlacement()
    {
        try
        {
            var bounds=WindowState==WindowState.Normal?new Rect(Left,Top,Width,Height):RestoreBounds;
            if(bounds.IsEmpty||!IsFinite(bounds.Left)||!IsFinite(bounds.Top)||!IsFinite(bounds.Width)||!IsFinite(bounds.Height))return;
            if(bounds.Width<MinWidth||bounds.Height<MinHeight)return;
            Store.Save(Store.FilePath(WindowPlacementFile),new WindowPlacement(bounds.Left,bounds.Top,bounds.Width,bounds.Height,WindowState==WindowState.Maximized));
        }
        catch(Exception e)when(e is not OutOfMemoryException){/* 位置记录是便利信息，写入失败不能影响退出。 */}
    }

    private static bool IsFinite(double value)=>double.IsFinite(value);
}

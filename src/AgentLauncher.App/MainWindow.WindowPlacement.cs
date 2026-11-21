using System.Windows;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

// 记住操作员上次把配置窗口放在哪里。配置文件仍是唯一业务来源；窗口几何单独存放，
// 读取失败、尺寸过小或完全移出当前虚拟桌面时回退到现有默认位置，不阻止启动。
public partial class MainWindow
{
    private sealed record WindowPlacement(double Left,double Top,double Width,double Height,bool Maximized);
    private const string WindowPlacementFile="window-placement.json";
    // WPF may not expose usable RestoreBounds while a minimized window is closing.
    private Rect windowedBounds=Rect.Empty;
    private WindowState lastNonMinimizedState=WindowState.Normal;
    private IntPtr windowHandle;

    private void InitializeWindowPlacementTracking()
    {
        lastNonMinimizedState=WindowState;
        RememberWindowedBounds();
        StateChanged+=WindowPlacementStateChanged;
        SizeChanged+=WindowPlacementSizeChanged;
        LocationChanged+=WindowPlacementLocationChanged;
        SourceInitialized+=WindowPlacementSourceInitialized;
    }
    private void WindowPlacementSourceInitialized(object? sender,EventArgs e)=>windowHandle=new System.Windows.Interop.WindowInteropHelper(this).Handle;

    private void WindowPlacementStateChanged(object? sender,EventArgs e)
    {
        if(WindowState!=WindowState.Minimized)lastNonMinimizedState=WindowState;
        RememberWindowedBounds();
    }

    private void WindowPlacementSizeChanged(object sender,SizeChangedEventArgs e)=>RememberWindowedBounds();
    private void WindowPlacementLocationChanged(object? sender,EventArgs e)=>RememberWindowedBounds();

    private void RememberWindowedBounds()
    {
        if(!IsActuallyWindowed()||!IsFinite(Left)||!IsFinite(Top)||!IsFinite(Width)||!IsFinite(Height))return;
        if(Width<MinWidth||Height<MinHeight)return;
        windowedBounds=new Rect(Left,Top,Width,Height);
    }
    // Native caption state changes before WPF's StateChanged event during maximize/restore.
    private bool IsActuallyWindowed()=>WindowState==WindowState.Normal&&(windowHandle==IntPtr.Zero||(!WindowPlacementNative.IsZoomed(windowHandle)&&!WindowPlacementNative.IsIconic(windowHandle)));


    private bool IsUsableWindowedBounds(Rect bounds)=>!bounds.IsEmpty&&IsFinite(bounds.Left)&&IsFinite(bounds.Top)&&IsFinite(bounds.Width)&&IsFinite(bounds.Height)&&bounds.Width>=MinWidth&&bounds.Height>=MinHeight;

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
            RememberWindowedBounds();
            if(saved.Maximized)WindowState=WindowState.Maximized;
        }
        catch(Exception e)when(e is not OutOfMemoryException){/* 单个 UI 记录损坏时保留默认窗口，不阻止启动。 */}
    }

    private void SaveWindowPlacement()
    {
        try
        {
            if(WindowState==WindowState.Normal)RememberWindowedBounds();
            var bounds=IsUsableWindowedBounds(windowedBounds)?windowedBounds:RestoreBounds;
            if(!IsUsableWindowedBounds(bounds))return;
            var state=WindowState==WindowState.Minimized?lastNonMinimizedState:WindowState;
            Store.Save(Store.FilePath(WindowPlacementFile),new WindowPlacement(bounds.Left,bounds.Top,bounds.Width,bounds.Height,state==WindowState.Maximized));
        }
        catch(Exception e)when(e is not OutOfMemoryException){/* 位置记录是便利信息，写入失败不能影响退出。 */}
    }

    private static class WindowPlacementNative
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool IsZoomed(IntPtr handle);
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool IsIconic(IntPtr handle);
    }
    private static bool IsFinite(double value)=>double.IsFinite(value);
}

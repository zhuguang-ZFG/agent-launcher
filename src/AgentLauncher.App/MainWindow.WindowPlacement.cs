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
    private void WindowPlacementSourceInitialized(object? sender,EventArgs e)
    {
        windowHandle=new System.Windows.Interop.WindowInteropHelper(this).Handle;
        FitWindowPlacementToWorkArea();
    }

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
    private bool TryGetWindowWorkArea(out Rect workArea)
    {
        workArea=Rect.Empty;
        if(windowHandle==IntPtr.Zero)return false;
        var target=System.Windows.Interop.HwndSource.FromHwnd(windowHandle)?.CompositionTarget;
        if(target is null)return false;
        var monitor=WindowPlacementNative.MonitorFromWindow(windowHandle,2);
        var info=new WindowPlacementNative.MonitorInfo{Size=System.Runtime.InteropServices.Marshal.SizeOf<WindowPlacementNative.MonitorInfo>()};
        if(monitor==IntPtr.Zero||!WindowPlacementNative.GetMonitorInfo(monitor,ref info))return false;
        var fromDevice=target.TransformFromDevice;
        workArea=new Rect(fromDevice.Transform(new Point(info.Work.Left,info.Work.Top)),fromDevice.Transform(new Point(info.Work.Right,info.Work.Bottom)));
        return !workArea.IsEmpty&&workArea.Width>0&&workArea.Height>0;
    }
    private void FitWindowPlacementToWorkArea()
    {
        if(!TryGetWindowWorkArea(out var work))return;
        var bounds=RestoreBounds;
        if(!IsUsableWindowedBounds(bounds))bounds=windowedBounds;
        if(!IsUsableWindowedBounds(bounds))return;
        MinWidth=Math.Min(MinWidth,work.Width);MinHeight=Math.Min(MinHeight,work.Height);
        var width=Math.Min(bounds.Width,work.Width);var height=Math.Min(bounds.Height,work.Height);
        var left=Math.Clamp(bounds.Left,work.Left,work.Right-width);var top=Math.Clamp(bounds.Top,work.Top,work.Bottom-height);
        WindowStartupLocation=WindowStartupLocation.Manual;
        Width=width;Height=height;Left=left;Top=top;
        windowedBounds=new Rect(left,top,width,height);
    }



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
            if(windowHandle!=IntPtr.Zero)FitWindowPlacementToWorkArea();
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
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr handle,uint flags);
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool GetMonitorInfo(IntPtr monitor,ref MonitorInfo info);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct NativeRect{internal int Left,Top,Right,Bottom;}
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct MonitorInfo{internal int Size;internal NativeRect Monitor,Work;internal uint Flags;}
    }
    private static bool IsFinite(double value)=>double.IsFinite(value);
}

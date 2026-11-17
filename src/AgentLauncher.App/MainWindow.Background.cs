using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Threading;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private BackgroundIcon? backgroundIcon;
    private EventWaitHandle? showRequest;
    private readonly DispatcherTimer backgroundTimer=new(){Interval=TimeSpan.FromSeconds(1)};

    private void BackgroundClick(object sender,RoutedEventArgs e)=>EnterBackground();
    private void EnterBackground()
    {
        if(closeRequested||loading||OwnedWindows.Cast<Window>().Any(w=>w.IsVisible))return;
        try
        {
            if(backgroundIcon is null)
            {
                showRequest=new(false,EventResetMode.AutoReset,@"Local\"+Store.Pipe+"-show");
                backgroundIcon=new BackgroundIcon(this,RestoreBackground,()=>
                {
                    var menu=new ContextMenu{Placement=PlacementMode.MousePoint};
                    var restore=new MenuItem{Header="打开启动器"};restore.Click+=(_,_)=>RestoreBackground();menu.Items.Add(restore);
                    var exit=new MenuItem{Header="退出并暂停队列"};exit.Click+=(_,_)=>{RestoreBackground();Close();};menu.Items.Add(exit);menu.IsOpen=true;
                });
                backgroundTimer.Tick+=(_,_)=>{if(showRequest?.WaitOne(0)==true)RestoreBackground();};
                Closed+=(_,_)=>{backgroundTimer.Stop();backgroundIcon?.Dispose();showRequest?.Dispose();};
            }
            backgroundIcon.Show();backgroundTimer.Start();Hide();
        }
        catch(Exception ex)when(ex is not OutOfMemoryException){ErrorText.Text="后台运行未开启："+ex.Message;}
    }
    internal void RestoreBackground(){Show();WindowState=WindowState.Normal;Activate();backgroundTimer.Stop();backgroundIcon?.Hide();}
}

internal sealed class BackgroundIcon : IDisposable
{
    private const uint Message=0x8000+31;
    private readonly HwndSource source;
    private readonly Action restore,menu;
    private NotifyData data;
    private bool visible;
    private readonly uint taskbarCreated=RegisterWindowMessage("TaskbarCreated");
    public BackgroundIcon(Window window,Action restore,Action menu)
    {
        this.restore=restore;this.menu=menu;
        var handle=new WindowInteropHelper(window).EnsureHandle();source=HwndSource.FromHwnd(handle);source.AddHook(Hook);
        ExtractIconEx(Environment.ProcessPath!,0,out var large,out var small,1);
        if(large!=IntPtr.Zero)DestroyIcon(large);
        data=new(){Size=(uint)Marshal.SizeOf<NotifyData>(),Window=handle,Id=1,Flags=7,Callback=Message,Icon=small,Tip="多 Agent 启动器 · 队列与通知继续运行",Info="",InfoTitle=""};
    }
    public void Show(){if(visible)return;if(!Shell_NotifyIcon(0,ref data))throw new System.ComponentModel.Win32Exception("无法创建通知区域图标，窗口保持打开。");visible=true;}
    public void Hide(){if(visible){Shell_NotifyIcon(2,ref data);visible=false;}}
    private IntPtr Hook(IntPtr hwnd,int message,IntPtr w,IntPtr l,ref bool handled)
    {if(message==taskbarCreated&&visible){visible=false;try{Show();}catch(System.ComponentModel.Win32Exception){restore();}}if(message==Message){handled=true;if(l.ToInt32()==0x203)restore();else if(l.ToInt32()==0x205)menu();}return IntPtr.Zero;}
    public void Dispose(){Hide();source.RemoveHook(Hook);if(data.Icon!=IntPtr.Zero){DestroyIcon(data.Icon);data.Icon=IntPtr.Zero;}}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct NotifyData
    {
        public uint Size;public IntPtr Window;public uint Id,Flags,Callback;public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)]public string Info;
        public uint Timeout;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)]public string InfoTitle;
        public uint InfoFlags;public Guid Guid;public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool Shell_NotifyIcon(uint operation,ref NotifyData data);
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] private static extern uint ExtractIconEx(string file,int index,out IntPtr large,out IntPtr small,uint count);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyIcon(IntPtr icon);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
}

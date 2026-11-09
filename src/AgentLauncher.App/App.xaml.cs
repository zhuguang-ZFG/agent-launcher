using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using AgentLauncher.Windows;
namespace AgentLauncher.App;
public partial class App : Application
{
    private Mutex? singleton;
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);singleton=new Mutex(false,@"Local\"+Store.Pipe+"-gui");bool own;
        try{own=singleton.WaitOne(0);}catch(AbandonedMutexException){own=true;}
        if(!own){foreach(var p in Process.GetProcessesByName("AgentLauncher.App")){using(p){if(p.Id!=Environment.ProcessId&&p.MainWindowHandle!=IntPtr.Zero){ShowWindow(p.MainWindowHandle,9);SetForegroundWindow(p.MainWindowHandle);}}}singleton.Dispose();singleton=null;Shutdown();return;}
        DispatcherUnhandledException+=(_,args)=>{MessageBox.Show("发生未预期错误，已停止新提交。现有终端不会由配置窗口结束。\n"+args.Exception.Message,"启动器错误");args.Handled=true;Shutdown(1);};
        new MainWindow(e.Args).Show();
    }
    protected override void OnExit(ExitEventArgs e){if(singleton!=null){singleton.ReleaseMutex();singleton.Dispose();}base.OnExit(e);}
}

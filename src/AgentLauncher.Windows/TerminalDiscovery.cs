using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AgentLauncher.Windows;
public static class TerminalDiscovery
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern int GetPackagesByPackageFamily(string family,ref uint count,IntPtr names,ref uint length,IntPtr buffer);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,ExactSpelling=true)] private static extern int GetPackagePathByFullName(string name,ref uint length,StringBuilder? path);
    public static IReadOnlyList<string> Installs()
    {
        var found=new List<string>();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string path){if(File.Exists(path)&&seen.Add(path))found.Add(path);}
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Microsoft","WindowsApps","wt.exe"));
        foreach(var path in Commands.DiscoverInstalls("wt.exe"))Add(path);
        foreach(var family in new[]{"Microsoft.WindowsTerminal_8wekyb3d8bbwe","Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe"})
        {
            uint count=0,length=0;var result=GetPackagesByPackageFamily(family,ref count,IntPtr.Zero,ref length,IntPtr.Zero);
            if(result==0&&count==0)continue;if(result!=122)throw new Win32Exception(result,"无法查询 Windows Terminal 注册包。");
            var names=Marshal.AllocHGlobal(checked((int)count*IntPtr.Size));var buffer=IntPtr.Zero;
            try
            {
                buffer=Marshal.AllocHGlobal(checked((int)length*2));result=GetPackagesByPackageFamily(family,ref count,names,ref length,buffer);if(result!=0)throw new Win32Exception(result,"Windows Terminal 注册包查询未完成。");
                for(var i=0;i<count;i++)
                {
                    var name=Marshal.PtrToStringUni(Marshal.ReadIntPtr(names,i*IntPtr.Size))!;uint pathLength=0;
                    result=GetPackagePathByFullName(name,ref pathLength,null);if(result!=122)throw new Win32Exception(result,"无法读取 Terminal 安装位置。");
                    var path=new StringBuilder(checked((int)pathLength));result=GetPackagePathByFullName(name,ref pathLength,path);if(result!=0)throw new Win32Exception(result,"无法读取 Terminal 安装位置。");
                    Add(Path.Combine(path.ToString(),"WindowsTerminal.exe"));
                }
            }
            finally{if(buffer!=IntPtr.Zero)Marshal.FreeHGlobal(buffer);Marshal.FreeHGlobal(names);}
        }
        return found;
    }
}

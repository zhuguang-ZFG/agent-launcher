using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AgentLauncher.Core;

namespace AgentLauncher.Windows;

public static class Native
{
    [StructLayout(LayoutKind.Sequential)] public struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, Extended; }
    [StructLayout(LayoutKind.Sequential)] public struct IoCounters { public ulong ReadCount, WriteCount, OtherCount, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] public struct BasicLimits { public long ProcessTime, JobTime; public uint Flags; public UIntPtr MinWorking, MaxWorking; public uint Active; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] public struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [StructLayout(LayoutKind.Sequential)] public struct CpuRate { public uint Flags, Rate; }
    [StructLayout(LayoutKind.Sequential)] public struct StartupInfo { public int Size; public IntPtr Reserved; public IntPtr Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public IntPtr ReservedBytes, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] public struct StartupInfoEx { public StartupInfo Info; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] public struct ProcessInfo { public IntPtr Process, Thread; public uint Pid, Tid; }
    [StructLayout(LayoutKind.Sequential)] public struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int InheritHandle; }
    [StructLayout(LayoutKind.Sequential)] public struct FileInfo { public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Created, Accessed, Written; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern SafeFileHandle CreateJobObject(IntPtr attributes, string name);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern SafeFileHandle OpenJobObject(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool SetInformationJobObject(SafeFileHandle job, int kind, IntPtr data, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool QueryInformationJobObject(SafeFileHandle job, int kind, IntPtr data, uint size, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool IsProcessInJob(IntPtr process, SafeFileHandle job, out bool result);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref UIntPtr size);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] public static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string cwd, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInfo info);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll")] public static extern void SetLastError(uint error);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetHandleInformation(IntPtr handle,uint mask,uint flags);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    [DllImport("kernel32.dll", SetLastError=true)] public static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetExitCodeProcess(IntPtr process, out uint code);
    public static void Check(bool value) { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
}

public sealed class Sampler
{
    private long idle, kernel, user;
    private bool initialized;
    private MemorySnapshot? previous;
    public MemorySnapshot Sample()
    {
        var m = new Native.MemoryStatus { Length = (uint)Marshal.SizeOf<Native.MemoryStatus>() };
        Native.Check(Native.GlobalMemoryStatusEx(ref m)); Native.Check(Native.GetSystemTimes(out var i, out var k, out var u));
        var total = k - kernel + u - user;
        // Back-to-back checks can see unchanged or briefly regressing OS counters. Keep the
        // CPU baseline and its timestamp until counters advance; memory is still read now.
        if(initialized&&total<=0&&previous!=null)
            return previous with{Total=m.TotalPhysical,Available=m.AvailablePhysical,CommitLimit=m.TotalPage,CommitAvailable=m.AvailablePage};
        var cpu = initialized && total > 0 ? Math.Clamp(100.0 * (total - (i - idle)) / total, 0, 100) : double.NaN;
        idle=i; kernel=k; user=u; initialized=true;
        return previous=new(m.TotalPhysical,m.AvailablePhysical,m.TotalPage,m.AvailablePage,cpu,DateTimeOffset.UtcNow);
    }
}
public static class PhysicalDirectory
{
    public static WorkspaceIdentity Resolve(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\")) throw new IOException("请选择本地绝对目录。");
        using var h=Native.CreateFile(Path.GetFullPath(path),0,7,IntPtr.Zero,3,0x02000000,IntPtr.Zero);
        if(h.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        Native.Check(Native.GetFileInformationByHandle(h,out var info));
        if((info.Attributes & 0x10)==0) throw new IOException("所选路径不是文件夹。");
        var text=new StringBuilder(32768);var n=Native.GetFinalPathNameByHandle(h,text,(uint)text.Capacity,0);
        if(n==0||n>=text.Capacity)throw new IOException("无法解析物理目录。");
        var final=text.ToString();if(final.StartsWith("\\\\?\\"))final=final[4..];
        return new(final,$"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}");
    }
    public static WorkspaceIdentity Workspace(string path)
    {
        var actual=Resolve(path);
        for(var d=new DirectoryInfo(actual.Path);d!=null;d=d.Parent)
            if(System.IO.Directory.Exists(Path.Combine(d.FullName,".git"))||File.Exists(Path.Combine(d.FullName,".git")))return Resolve(d.FullName);
        return actual;
    }
    public static bool Overlaps(WorkspaceIdentity a, WorkspaceIdentity b) => a.Key==b.Key||Within(a.Path,b.Path)||Within(b.Path,a.Path);
    private static bool Within(string a,string b)=>a.TrimEnd('\\').StartsWith(b.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);
}
public sealed class Job : IDisposable
{
    public SafeFileHandle Handle { get; }
    public bool CreatedNew { get; }
    public Job(string name, ulong memory=0, bool root=false, bool openOnly=false, int cpuRate=ResourcePolicy.DefaultCpuRate)
    {
        if(openOnly)
        {
            Handle=Native.OpenJobObject(0x001F003F,false,name);
            if(Handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
            CreatedNew=false;
            return;
        }
        Native.SetLastError(0);
        Handle=Native.CreateJobObject(IntPtr.Zero,name);
        if(Handle.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        CreatedNew=Marshal.GetLastWin32Error()!=183;
        try
        {
            if(CreatedNew && root) Set(15,new Native.CpuRate{Flags=1|4,Rate=(uint)cpuRate});
            if(CreatedNew && memory>0)Set(9,new Native.ExtendedLimits{Basic=new Native.BasicLimits{Flags=0x200},JobMemory=(UIntPtr)memory});
        }
        catch { Handle.Dispose();throw; }
    }
    public Native.CpuRate? QueryCpuRate()
    {
        var size=Marshal.SizeOf<Native.CpuRate>();var p=Marshal.AllocHGlobal(size);
        try
        {
            if(!Native.QueryInformationJobObject(Handle,15,p,(uint)size,IntPtr.Zero))return null;
            return Marshal.PtrToStructure<Native.CpuRate>(p);
        }
        finally{Marshal.FreeHGlobal(p);}
    }
    public JobMemoryEvidence? QueryMemory()
    {
        if(Handle.IsClosed||Handle.IsInvalid)return null;
        var size=Marshal.SizeOf<Native.ExtendedLimits>();var p=Marshal.AllocHGlobal(size);
        try
        {
            if(!Native.QueryInformationJobObject(Handle,9,p,(uint)size,IntPtr.Zero))return null;
            var limits=Marshal.PtrToStructure<Native.ExtendedLimits>(p);
            return new(limits.JobMemory.ToUInt64(),limits.PeakJob.ToUInt64());
        }
        finally{Marshal.FreeHGlobal(p);}
    }
    public void SetMemoryLimit(ulong bytes)
    {
        if(Handle.IsClosed||Handle.IsInvalid)throw new InvalidOperationException("任务组句柄不可用，无法调整内存额度。");
        Set(9,new Native.ExtendedLimits{Basic=new Native.BasicLimits{Flags=bytes>0?0x200u:0u},JobMemory=(UIntPtr)bytes});
    }
    private void Set<T>(int kind,T data) where T:struct
    {
        var size=Marshal.SizeOf<T>();var p=Marshal.AllocHGlobal(size);
        try{Marshal.StructureToPtr(data,p,false);Native.Check(Native.SetInformationJobObject(Handle,kind,p,(uint)size));}finally{Marshal.FreeHGlobal(p);}
    }
    public int[] Members()
    {
        const int size=65536;var p=Marshal.AllocHGlobal(size);
        try
        {
            Native.Check(Native.QueryInformationJobObject(Handle,3,p,size,IntPtr.Zero));var count=Marshal.ReadInt32(p,4);
            if(count<0||count>(size-8)/IntPtr.Size)throw new IOException("进程归属超出查询边界。");
            return Enumerable.Range(0,count).Select(i=>checked((int)Marshal.ReadIntPtr(p,8+i*IntPtr.Size))).ToArray();
        }
        finally{Marshal.FreeHGlobal(p);}
    }
    public void Dispose()=>Handle.Dispose();
}
public sealed class ProtectedProcess : IDisposable
{
    private Native.ProcessInfo info;
    public Process Process { get; }
    public long CreationTime { get; }
    public ProtectedProcess(CommandSpec command,string cwd,Job root,Job child,bool redirected=false,bool createNoWindow=false):this(command,cwd,[root,child],redirected,createNoWindow){}
    public ProtectedProcess(CommandSpec command,string cwd,IReadOnlyList<Job> chain,bool redirected=false,bool createNoWindow=false)
    {
        if(chain.Count<1)throw new ArgumentException("启动链至少需要一个任务组。",nameof(chain));
        var attributeCount=redirected?2:1;
        UIntPtr size=UIntPtr.Zero;Native.InitializeProcThreadAttributeList(IntPtr.Zero,attributeCount,0,ref size);
        var attrs=Marshal.AllocHGlobal(checked((int)size));var jobs=Marshal.AllocHGlobal(chain.Count*IntPtr.Size);var handles=Marshal.AllocHGlobal(3*IntPtr.Size);var initialized=false;
        try
        {
            Native.Check(Native.InitializeProcThreadAttributeList(attrs,attributeCount,0,ref size));initialized=true;
            for(var i=0;i<chain.Count;i++)Marshal.WriteIntPtr(jobs,i*IntPtr.Size,chain[i].Handle.DangerousGetHandle());
            Native.Check(Native.UpdateProcThreadAttribute(attrs,0,(UIntPtr)0x0002000D,jobs,(UIntPtr)(chain.Count*IntPtr.Size),IntPtr.Zero,IntPtr.Zero));
            var startup=new Native.StartupInfoEx{Info=new(){Size=Marshal.SizeOf<Native.StartupInfoEx>()},Attributes=attrs};
            if(redirected)
            {
                var std=new[]{Native.GetStdHandle(-10),Native.GetStdHandle(-11),Native.GetStdHandle(-12)};
                for(var i=0;i<3;i++){Native.Check(Native.SetHandleInformation(std[i],1,1));Marshal.WriteIntPtr(handles,i*IntPtr.Size,std[i]);}
                Native.Check(Native.UpdateProcThreadAttribute(attrs,0,(UIntPtr)0x00020002,handles,(UIntPtr)(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero));
                startup.Info.Flags=0x100;startup.Info.StdInput=std[0];startup.Info.StdOutput=std[1];startup.Info.StdError=std[2];
            }
            var line=new StringBuilder(string.Join(" ",new[]{command.Executable}.Concat(command.Arguments).Select(Quote)));
            Native.Check(Native.CreateProcess(command.Executable,line,IntPtr.Zero,IntPtr.Zero,redirected,0x00080000|0x4|(createNoWindow?0x08000000u:0),IntPtr.Zero,cwd,ref startup,out info));
            foreach(var level in chain){Native.Check(Native.IsProcessInJob(info.Process,level.Handle,out var joined));if(!joined)throw new IOException("进程未进入要求的资源组。");}
            Process=Process.GetProcessById((int)info.Pid);_ = Process.Handle;CreationTime=Process.StartTime.ToUniversalTime().ToFileTimeUtc();
        }
        catch { if(info.Process!=IntPtr.Zero){Native.TerminateProcess(info.Process,1);DisposeHandles();}throw; }
        finally{if(initialized)Native.DeleteProcThreadAttributeList(attrs);Marshal.FreeHGlobal(attrs);Marshal.FreeHGlobal(jobs);Marshal.FreeHGlobal(handles);}
    }
    public void Resume(){if(Native.ResumeThread(info.Thread)==uint.MaxValue)throw new Win32Exception(Marshal.GetLastWin32Error());}
    public void AbortBeforeResume(){Native.Check(Native.TerminateProcess(info.Process,1));Process.WaitForExit(2000);}
    public static string Quote(string arg)
    {
        if(arg.Contains('\0'))throw new ArgumentException("参数不能含 NUL。");
        var b=new StringBuilder("\"");var slashes=0;
        foreach(var c in arg){if(c=='\\'){slashes++;continue;}if(c=='\"'){b.Append('\\',slashes*2+1).Append(c);slashes=0;continue;}b.Append('\\',slashes).Append(c);slashes=0;}
        return b.Append('\\',slashes*2).Append('"').ToString();
    }
    private void DisposeHandles(){if(info.Thread!=IntPtr.Zero)Native.CloseHandle(info.Thread);if(info.Process!=IntPtr.Zero)Native.CloseHandle(info.Process);info=default;}
    public void Dispose(){DisposeHandles();Process?.Dispose();}
}

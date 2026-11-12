using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AgentLauncher.Windows;

// Completion packets are evidence of a refused allocation, not proof that Windows killed a process.
// The observer never changes limits and never terminates a member (or a sibling Job).
public sealed class JobMemoryWatch : IDisposable
{
    [StructLayout(LayoutKind.Sequential)] private struct Association { public IntPtr Key, Port; }
    [DllImport("kernel32.dll",SetLastError=true)] private static extern SafeFileHandle CreateIoCompletionPort(IntPtr file,IntPtr existing,UIntPtr key,uint concurrency);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool GetQueuedCompletionStatus(SafeFileHandle port,out uint message,out UIntPtr key,out IntPtr value,uint timeout);
    private readonly SafeFileHandle port;
    public JobMemoryWatch(Job job)
    {
        port=CreateIoCompletionPort(new IntPtr(-1),IntPtr.Zero,UIntPtr.Zero,1);
        if(port.IsInvalid)throw new Win32Exception(Marshal.GetLastWin32Error());
        var size=Marshal.SizeOf<Association>();var p=Marshal.AllocHGlobal(size);
        try{Marshal.StructureToPtr(new Association{Key=new IntPtr(1),Port=port.DangerousGetHandle()},p,false);Native.Check(Native.SetInformationJobObject(job.Handle,7,p,(uint)size));}
        catch{port.Dispose();throw;}
        finally{Marshal.FreeHGlobal(p);}
    }
    public int[] Drain()
    {
        var pids=new List<int>();
        // Bound each tick even when a program repeatedly retries a refused allocation.
        for(var i=0;i<4096;i++)
        {
            if(!GetQueuedCompletionStatus(port,out var message,out _,out var value,0))
            {
                var error=Marshal.GetLastWin32Error();if(error==258)break;throw new Win32Exception(error);
            }
            if(message is 9 or 10&&value.ToInt64() is >0 and <=int.MaxValue)pids.Add((int)value.ToInt64());
        }
        return pids.Distinct().ToArray();
    }
    public void Dispose()=>port.Dispose();
}

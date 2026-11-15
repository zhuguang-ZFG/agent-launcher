using System.Runtime.InteropServices;
internal static class MemoryFixture
{
    [DllImport("kernel32.dll",SetLastError=true)] private static extern IntPtr VirtualAlloc(IntPtr address,UIntPtr size,uint allocation,uint protection);
    [DllImport("kernel32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool VirtualFree(IntPtr address,UIntPtr size,uint type);
    public static int Allocate(int mib)
    {
        var address=VirtualAlloc(IntPtr.Zero,(UIntPtr)((ulong)mib*1024*1024),0x3000,4);
        if(address==IntPtr.Zero)return 37;
        VirtualFree(address,UIntPtr.Zero,0x8000);return 11;
    }
    public static int AllocateHold(int mib,int milliseconds)
    {
        var address=VirtualAlloc(IntPtr.Zero,(UIntPtr)((ulong)mib*1024*1024),0x3000,4);
        if(address==IntPtr.Zero)return 37;
        Thread.Sleep(milliseconds);
        VirtualFree(address,UIntPtr.Zero,0x8000);return 11;
    }
    public static int Cycle(string prefix)
    {
        var address=VirtualAlloc(IntPtr.Zero,(UIntPtr)(64UL*1024*1024),0x3000,4);
        if(address==IntPtr.Zero)return 37;
        try
        {
            for(var offset=0;offset<64*1024*1024;offset+=4096)Marshal.WriteByte(address,offset,1);
            File.WriteAllText(prefix+".allocated","ready");
            if(!Wait(prefix+".free"))return 38;
        }
        finally{VirtualFree(address,UIntPtr.Zero,0x8000);}
        File.WriteAllText(prefix+".freed","ready");
        return Wait(prefix+".exit")?0:39;
    }
    private static bool Wait(string path)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(!File.Exists(path)){if(timer.ElapsedMilliseconds>20000)return false;Thread.Sleep(20);}
        return true;
    }
}

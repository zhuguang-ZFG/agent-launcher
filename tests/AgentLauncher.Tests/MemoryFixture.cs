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
}

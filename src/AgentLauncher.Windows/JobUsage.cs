using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentLauncher.Windows;

public sealed record JobUsage(ulong? CommitBytes, ulong? PrivateWorkingSetBytes);

/// <summary>Live, private usage only. Shared working-set pages and historical peaks must never become admission credits.</summary>
public static class JobUsageReader
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Counters
    {
        public uint Size, PageFaults;
        public UIntPtr PeakWorkingSet, WorkingSet, PeakPagedPool, PagedPool, PeakNonPagedPool, NonPagedPool;
        public UIntPtr Pagefile, PeakPagefile, PrivateUsage, PrivateWorkingSet;
        public ulong SharedCommit;
    }
    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref Counters counters, uint size);

    public static JobUsage Read(Job job)
    {
        var commit = job.QueryCurrentCommit();
        try
        {
            var members = job.Members();
            // Windows can retain a small charge while handles to an exited process remain open.
            // An empty Job must not get a live-process admission credit from that residual charge.
            if (members.Length == 0) return new(0, 0);
            if (members.Length > 4096) return new(commit, null);
            ulong physical = 0;
            foreach (var pid in members)
            {
                using var process = Process.GetProcessById(pid);
                var handle = process.Handle;
                if (!Native.IsProcessInJob(handle, job.Handle, out var belongs) || !belongs) return new(commit, null);
                var counters = new Counters { Size = (uint)Marshal.SizeOf<Counters>() };
                if (!GetProcessMemoryInfo(handle, ref counters, counters.Size)) return new(commit, null);
                physical = checked(physical + counters.PrivateWorkingSet.ToUInt64());
            }
            // A disappearing/replaced member invalidates the sample, rather than granting speculative free capacity.
            if (!members.Order().SequenceEqual(job.Members().Order())) return new(commit, null);
            return new(commit, physical);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or ArgumentException or IOException or OverflowException)
        {
            return new(commit, null);
        }
    }
}

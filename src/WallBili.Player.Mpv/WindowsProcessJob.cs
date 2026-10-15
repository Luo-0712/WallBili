using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WallBili.Player.Mpv;

/// <summary>The kernel closes only our mpv process when the application exits or is terminated.</summary>
internal static class WindowsProcessJob
{
    public static SafeFileHandle Attach(Process process)
    {
        var job = CreateJobObject(0, null);
        try
        {
            var limits = new ExtendedLimits { Basic = new() { Flags = 0x2000 } }; // KILL_ON_JOB_CLOSE
            if (job.IsInvalid || !SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<ExtendedLimits>()) ||
                !AssignProcessToJobObject(job, process.Handle))
                throw new MpvException(MpvErrorCode.StartupFailed, "Could not establish ownership of the player process.");
            return job;
        }
        catch { job.Dispose(); throw; }
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long ProcessTime, JobTime;
        public uint Flags;
        public nuint MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [DllImport("kernel32", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObject(nint attributes, string? name);
    [DllImport("kernel32")] private static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref ExtendedLimits info, uint length);
    [DllImport("kernel32")] private static extern bool AssignProcessToJobObject(SafeFileHandle job, nint process);
}

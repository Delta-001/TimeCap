using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ScreenClipTool.Capture;

/// <summary>
/// Job object Windows avec KILL_ON_JOB_CLOSE : tout process ffmpeg attaché est
/// tué par l'OS si l'app hôte meurt (crash inclus) — pas de ffmpeg orphelin.
/// </summary>
public static class ChildProcessJob
{
    private static readonly IntPtr Job = Create();

    public static void Attach(Process process)
    {
        if (Job == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Job, process.Handle); }
        catch (Exception ex) { Log.Warn("Job object non attaché : " + ex.Message); }
    }

    private static IntPtr Create()
    {
        try
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;

            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
                {
                    LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
                },
            };
            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr ptr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
                    return IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return job;
        }
        catch { return IntPtr.Zero; }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, IntPtr lpInfo, uint cbInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}

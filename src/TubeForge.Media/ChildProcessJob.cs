using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TubeForge.Media;

/// <summary>
/// Ties helper processes to the lifetime of TubeForge using a Windows job object configured to
/// terminate its members when the job handle closes. Cooperative cancellation cannot cover a
/// crash, a forced termination or a shutdown that races an in-flight transcode, and an FFmpeg
/// process that outlives the app keeps writing to a file the user believes is finished and holds
/// a lock that blocks the next attempt.
/// </summary>
public static class ChildProcessJob
{
    private const int ExtendedLimitInformation = 9;
    private const int LimitKillOnJobClose = 0x2000;
    private static readonly nint JobHandle = CreateKillOnCloseJob();

    /// <summary>
    /// Adds a started process to the job. Returns false when the platform refuses the assignment;
    /// the caller still owns cooperative cancellation, so this is a safety net rather than the
    /// primary mechanism.
    /// </summary>
    public static bool TryEnroll(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows() || JobHandle == 0)
        {
            return false;
        }

        try
        {
            return AssignProcessToJobObject(JobHandle, process.Handle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or
                                          NotSupportedException or EntryPointNotFoundException or
                                          DllNotFoundException)
        {
            // The process may already have exited, which needs no protection.
            return false;
        }
    }

    private static nint CreateKillOnCloseJob()
    {
        if (!OperatingSystem.IsWindows())
        {
            return 0;
        }

        try
        {
            var handle = CreateJobObjectW(0, null);
            if (handle == 0)
            {
                return 0;
            }

            var information = new ExtendedLimit
            {
                BasicLimitInformation = new BasicLimit { LimitFlags = LimitKillOnJobClose }
            };
            var size = Marshal.SizeOf<ExtendedLimit>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
                if (!SetInformationJobObject(handle, ExtendedLimitInformation, buffer, (uint)size))
                {
                    CloseHandle(handle);
                    return 0;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return handle;
        }
        catch (Exception exception) when (exception is EntryPointNotFoundException or DllNotFoundException)
        {
            return 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimit
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimit
    {
        public BasicLimit BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObjectW(nint securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        nint job,
        int informationClass,
        nint information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

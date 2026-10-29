using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ANalyze.Infrastructure;

public static partial class Native
{
    /// <summary>CPU nice value in background mode (0 = normal, 19 = lowest).</summary>
    public const int BackgroundNice = 10;

    private const int PrioProcess = 0;
    private const int IoprioWhoProcess = 1;
    private const int IoprioClassShift = 13;
    private const int IoprioClassIdle = 3;

    [LibraryImport("libc", EntryPoint = "setpriority", SetLastError = true)]
    private static partial int SetPriorityNative(int which, uint who, int prio);

    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial long Syscall3(long number, long a, long b, long c);

    /// <summary>
    /// Puts the whole process at low priority (background) or back to normal. Linux: every thread gets nice
    /// <see cref="BackgroundNice"/> and the idle I/O class (disk time only when nobody else wants it); threads started later
    /// inherit it. Elsewhere the process priority class is lowered. Returns null on success, else why it did not fully work
    /// (going back to normal needs root or CAP_SYS_NICE on Linux: otherwise a restart).
    /// </summary>
    public static string? SetBackgroundPriority(bool background)
    {
        if (!OperatingSystem.IsLinux())
        {
            try
            {
                Process.GetCurrentProcess().PriorityClass = background ? ProcessPriorityClass.BelowNormal : ProcessPriorityClass.Normal;
                return null;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException or InvalidOperationException)
            {
                return e.Message;
            }
        }

        long? ioprioSet = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 251,
            Architecture.Arm64 => 30,
            Architecture.X86 => 289,
            Architecture.Arm => 314,
            _ => null,
        };
        var nice = background ? BackgroundNice : 0;
        // Class "none" (0) means: follow the CPU nice value, the kernel default.
        var io = background ? IoprioClassIdle << IoprioClassShift : 0;
        int failed = 0, total = 0, errno = 0;
        try
        {
            foreach (var dir in Directory.EnumerateDirectories("/proc/self/task"))
            {
                if (!uint.TryParse(Path.GetFileName(dir), out var tid))
                {
                    continue;
                }

                total++;
                var ok = SetPriorityNative(PrioProcess, tid, nice) == 0;
                if (ok && ioprioSet is { } nr)
                {
                    ok = Syscall3(nr, IoprioWhoProcess, tid, io) == 0;
                }

                if (!ok)
                {
                    failed++;
                    errno = Marshal.GetLastPInvokeError();
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EntryPointNotFoundException or DllNotFoundException)
        {
            return e.Message;
        }

        return failed == 0 ? null
            : errno == 13 || errno == 1 ? $"not allowed for {failed} of {total} threads (raising the priority again needs root or CAP_SYS_NICE; restart ANalyze instead)"
            : $"failed for {failed} of {total} threads (errno {errno})";
    }
}

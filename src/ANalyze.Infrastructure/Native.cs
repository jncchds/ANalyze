using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ANalyze.Infrastructure;

/// <summary>lstat-style metadata for one path.</summary>
public readonly record struct FileStat(
    bool Exists,
    bool IsDir,
    bool IsSymlink,
    bool IsRegular,
    long Size,
    long AllocSize,
    long MTime,
    long ATime,
    long CTime,
    long Inode,
    long Dev,
    int NLink,
    int Uid = -1,
    int Gid = -1);

/// <summary>
/// Thin wrappers over Linux syscalls that .NET does not expose (inode, device, link count, allocated blocks,
/// hard links, realpath). On other platforms they fall back to managed APIs with reduced information.
/// </summary>
public static partial class Native
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxBasicStats = 0x7ff;
    private const int SIfmt = 0xF000;
    private const int SIfdir = 0x4000;
    private const int SIflnk = 0xA000;
    private const int SIfreg = 0x8000;

    private static bool _statxAvailable = OperatingSystem.IsLinux();

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int StatxNative(int dirfd, string path, int flags, uint mask, byte* buffer);

    [LibraryImport("libc", EntryPoint = "link", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LinkNative(string oldPath, string newPath);

    [LibraryImport("libc", EntryPoint = "realpath", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr RealPathNative(string path, IntPtr resolved);

    [LibraryImport("libc", EntryPoint = "lchown", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int LChownNative(string path, int uid, int gid);

    [LibraryImport("libc", EntryPoint = "geteuid")]
    private static partial uint GetEuidNative();

    private static readonly Lazy<bool> IsRoot = new(() =>
    {
        try
        {
            return OperatingSystem.IsLinux() && GetEuidNative() == 0;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            return false;
        }
    });

    /// <summary>
    /// Gives a path the given owner. Only acts when running as root (the container default), so files the app
    /// creates on a share keep the share's owner instead of becoming root-owned.
    /// </summary>
    public static void SetOwner(string path, int uid, int gid)
    {
        if (!IsRoot.Value || uid < 0 || gid < 0)
        {
            return;
        }

        _ = LChownNative(path, uid, gid);
    }

    /// <summary>Creates a directory (and missing parents); each new one takes the owner of the existing folder above it.</summary>
    public static void CreateDirectoryLikeParent(string path)
    {
        var missing = new Stack<string>();
        var p = Path.GetFullPath(path);
        while (!Directory.Exists(p))
        {
            missing.Push(p);
            p = Path.GetDirectoryName(p) ?? throw new IOException("Cannot create " + path);
        }

        var owner = LStat(p);
        while (missing.Count > 0)
        {
            var d = missing.Pop();
            Directory.CreateDirectory(d);
            SetOwner(d, owner.Uid, owner.Gid);
        }
    }

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void FreeNative(IntPtr ptr);

    /// <summary>Stats a path without following a final symlink.</summary>
    public static unsafe FileStat LStat(string path)
    {
        if (_statxAvailable)
        {
            try
            {
                var buf = stackalloc byte[256];
                if (StatxNative(AtFdCwd, path, AtSymlinkNoFollow, StatxBasicStats, buf) != 0)
                {
                    return default;
                }

                var s = new ReadOnlySpan<byte>(buf, 256);
                var mode = BinaryPrimitives.ReadUInt16LittleEndian(s[28..]) & SIfmt;
                var devMajor = BinaryPrimitives.ReadUInt32LittleEndian(s[136..]);
                var devMinor = BinaryPrimitives.ReadUInt32LittleEndian(s[140..]);
                return new FileStat(
                    Exists: true,
                    IsDir: mode == SIfdir,
                    IsSymlink: mode == SIflnk,
                    IsRegular: mode == SIfreg,
                    Size: (long)BinaryPrimitives.ReadUInt64LittleEndian(s[40..]),
                    AllocSize: (long)BinaryPrimitives.ReadUInt64LittleEndian(s[48..]) * 512,
                    MTime: BinaryPrimitives.ReadInt64LittleEndian(s[112..]),
                    ATime: BinaryPrimitives.ReadInt64LittleEndian(s[64..]),
                    CTime: BinaryPrimitives.ReadInt64LittleEndian(s[96..]),
                    Inode: (long)BinaryPrimitives.ReadUInt64LittleEndian(s[32..]),
                    Dev: ((long)devMajor << 32) | devMinor,
                    NLink: (int)BinaryPrimitives.ReadUInt32LittleEndian(s[16..]),
                    Uid: (int)BinaryPrimitives.ReadUInt32LittleEndian(s[20..]),
                    Gid: (int)BinaryPrimitives.ReadUInt32LittleEndian(s[24..]));
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                _statxAvailable = false;
            }
        }

        return ManagedStat(path);
    }

    private static FileStat ManagedStat(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (!info.Exists && info.LinkTarget is null)
        {
            return default;
        }

        var isLink = info.LinkTarget is not null;
        var isDir = !isLink && info is DirectoryInfo;
        var size = info is FileInfo fi && !isLink ? fi.Length : 0;
        return new FileStat(true, isDir, isLink, !isDir && !isLink, size, size,
            new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            new DateTimeOffset(info.LastAccessTimeUtc).ToUnixTimeSeconds(),
            new DateTimeOffset(info.CreationTimeUtc).ToUnixTimeSeconds(),
            0, 0, 1);
    }

    /// <summary>Creates a hard link (Linux only).</summary>
    public static void CreateHardLink(string existing, string newPath)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Hard links are only supported on Linux.");
        }

        if (LinkNative(existing, newPath) != 0)
        {
            throw new IOException($"link() failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }

    /// <summary>Canonical absolute path with every symlink resolved, or null when the path does not exist.</summary>
    public static string? RealPath(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            try
            {
                var p = RealPathNative(path, IntPtr.Zero);
                if (p == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    return Marshal.PtrToStringUTF8(p);
                }
                finally
                {
                    FreeNative(p);
                }
            }
            catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
            {
                // fall through
            }
        }

        var full = Path.GetFullPath(path);
        if (!File.Exists(full) && !Directory.Exists(full))
        {
            return null;
        }

        FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
        return info.ResolveLinkTarget(true)?.FullName ?? full;
    }
}

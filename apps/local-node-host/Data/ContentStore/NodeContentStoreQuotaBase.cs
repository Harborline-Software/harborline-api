using System.ComponentModel;
using System.Runtime.InteropServices;
using Harborline.Foundation.ContentStore;
using Microsoft.Win32.SafeHandles;

namespace Harborline.Api.LocalNodeHost.Data.ContentStore;

/// <summary>Q22: caller-available bytes on the store's volume plus its allocated physical bytes.</summary>
/// <remarks>
/// Measured on every call: <see cref="DriveInfo.AvailableFreeSpace"/> (<c>f_bavail * f_frsize</c>) or Windows
/// <c>GetDiskFreeSpaceEx</c> caller bytes, plus each store file's allocated size (Unix <c>st_blocks * 512</c>, Windows
/// <c>FILE_STANDARD_INFO.AllocationSize</c>). .NET exposes no allocated-size API, so Unix reads <c>fstat</c>
/// directly; <c>st_blocks</c> sits at offset 104 in the 64-bit-inode macOS <c>struct stat</c> and at 64 in both the
/// glibc x86_64 and the generic aarch64 layouts, and a test checks the result against <c>stat(1)</c>. Database
/// allocation, including free pages, is counted rather than plaintext or sparse logical length.
/// </remarks>
public sealed class NodeContentStoreQuotaBase(NodeContentStoreStorage storage) : IContentStoreQuotaBase
{
    public long GetQuotaBaseBytes()
    {
        lock (storage.SyncRoot)
        {
            var directory = storage.DirectoryPath;
            long available;
            if (OperatingSystem.IsWindows())
            {
                if (!GetDiskFreeSpaceEx(directory, out var caller, out _, out _)) throw new Win32Exception();
                available = checked((long)caller);
            }
            else available = new DriveInfo(directory).AvailableFreeSpace; // .NET uses f_bavail * f_frsize.
            long allocated = 0;
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (Path.GetFileName(path) == "writer.lock") continue; // Empty lease file, no data allocation.
                using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                allocated = checked(allocated + AllocatedBytes(file));
            }
            return checked(available + allocated);
        }
    }

    internal static long AllocatedBytes(SafeFileHandle file)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandleEx(file, 1, out var info, Marshal.SizeOf<FileStandardInfo>())) throw new Win32Exception();
            return info.AllocationSize;
        }
        // The st_blocks offsets below are verified only for these ABIs; refuse every other platform explicitly.
        var arch = RuntimeInformation.ProcessArchitecture;
        if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) || (arch != Architecture.X64 && arch != Architecture.Arm64))
            throw new PlatformNotSupportedException($"Content-store allocation measurement supports Windows, and Linux or macOS on x64 or arm64; this host is {RuntimeInformation.OSDescription} {arch}.");
        // Native 64-bit ABI: st_blocks is measured in 512-byte units, including sparse files.
        var status = new byte[256];
        // x64 macOS exports the 64-bit-inode stat as fstat$INODE64; arm64 macOS has only that layout, as fstat.
        var result = !OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? UnixFStat(file, status)
            : MacX64FStat(file, status);
        if (result != 0) throw new Win32Exception();
        return checked(BitConverter.ToInt64(status, OperatingSystem.IsMacOS() ? 104 : 64) * 512);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInfo
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        public byte DeletePending;
        public byte Directory;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, out FileStandardInfo info, int size);
    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int MacX64FStat(SafeFileHandle file, [Out] byte[] status);
    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)]
    private static extern int UnixFStat(SafeFileHandle file, [Out] byte[] status);
}

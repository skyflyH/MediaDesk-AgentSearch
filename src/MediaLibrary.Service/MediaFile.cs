using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MediaLibrary.Service;

internal sealed record MediaFile(string Identity, long CreatedTicks)
{
    private const FileAttributes Unavailable =
        FileAttributes.Directory | FileAttributes.Offline | (FileAttributes)0x00440000;

    public static MediaFile Read(string path) => ReadInfo(path).Identity;

    public static (MediaFile Identity, long Length, long WriteTicks) ReadInfo(string path)
    {
        if ((File.GetAttributes(path) & Unavailable) != 0)
            throw new IOException("File is unavailable.");

        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return ReadInfo(handle);
    }

    public string Volume => Identity.Split(':', 2)[0];

    public static MediaFile Read(SafeFileHandle handle, bool directory = false) =>
        ReadInfo(handle, directory).Identity;

    public static (MediaFile Identity, long Length, long WriteTicks) ReadInfo(SafeFileHandle handle, bool directory = false)
    {
        if (!GetFileInformationByHandle(handle, out var info) ||
            info.Attributes.HasFlag(FileAttributes.Directory) != directory ||
            (info.Attributes & (Unavailable & ~FileAttributes.Directory)) != 0 ||
            (info.IndexHigh == 0 && info.IndexLow == 0))
            throw new IOException("File identity is unavailable.");
        var identity = new MediaFile(
            $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}",
            DateTime.FromFileTimeUtc(((long)info.CreatedHigh << 32) | info.CreatedLow).Ticks);
        return (identity, ((long)info.SizeHigh << 32) | info.SizeLow,
            DateTime.FromFileTimeUtc(((long)info.WrittenHigh << 32) | info.WrittenLow).Ticks);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public FileAttributes Attributes;
        public uint CreatedLow, CreatedHigh;
        public uint AccessedLow, AccessedHigh;
        public uint WrittenLow, WrittenHigh;
        public uint Volume;
        public uint SizeHigh, SizeLow, LinkCount;
        public uint IndexHigh, IndexLow;
    }
}

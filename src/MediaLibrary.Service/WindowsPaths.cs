using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaLibrary.Service;

internal static class WindowsPaths
{
    private const uint ReadData = 0x80000000;
    private const uint WriteData = 0x40000000;
    private const uint DeleteAccess = 0x10000;

    public static SafeFileHandle OpenDirectory(string path) =>
        Open(path, 0x80 | 0x01, FileShare.ReadWrite, 3, 0x02000000);

    public static SafeFileHandle OpenDirectoryForCreate(string path) =>
        Open(path, 0x02, FileShare.ReadWrite, 3, 0x02000000); // FILE_ADD_FILE

    public static SafeFileHandle OpenSource(string path) => Open(path, ReadData | DeleteAccess, FileShare.Read, 3, 0);

    public static SafeFileHandle OpenProtectedRead(string path) => Open(path, ReadData, FileShare.Read, 3, 0);

    public static SafeFileHandle ProtectProperties(string path) => Open(path, 0x80, FileShare.ReadWrite, 3, 0);

    public static SafeFileHandle CreateStage(string path) => Open(path, ReadData | WriteData | DeleteAccess, FileShare.Read, 1, 0);

    public static SafeFileHandle OpenStage(string path) => Open(path, ReadData | WriteData | DeleteAccess, FileShare.Read, 3, 0);

    private static SafeFileHandle Open(string path, uint access, FileShare share, uint creation, uint flags)
    {
        var handle = CreateFile(path, access, share, IntPtr.Zero, creation, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw IoError(error);
    }

    public static string FinalPath(SafeFileHandle handle)
    {
        var buffer = new StringBuilder(512);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length >= buffer.Capacity)
        {
            buffer.EnsureCapacity(checked((int)length + 1));
            length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        }
        if (length == 0 || length >= buffer.Capacity) throw IoError(Marshal.GetLastWin32Error());
        var value = buffer.ToString();
        if (value.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + value[8..];
        if (value.StartsWith(@"\\?\", StringComparison.Ordinal)) return value[4..];
        throw new IOException("Unrecognized Windows path.");
    }

    public static bool SamePath(string left, string right) =>
        string.Equals(Path.TrimEndingDirectorySeparator(left), Path.TrimEndingDirectorySeparator(right), StringComparison.OrdinalIgnoreCase);

    public static bool Inside(string path, string root)
    {
        if (SamePath(path, root)) return true;
        return path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.EndsInDirectorySeparator(root) && path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    public static void Rename(SafeFileHandle file, SafeFileHandle directory, string destination)
    {
        if (!SamePath(FinalPath(directory), Path.GetDirectoryName(destination)!))
            throw new IOException("Destination directory changed.");
        // Win32 FileRenameInfo accepts a fully qualified DOS path with RootDirectory set to NULL.
        var handleOffset = IntPtr.Size;
        var lengthOffset = handleOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var nameBytes = Encoding.Unicode.GetBytes(destination);
        var length = nameOffset + nameBytes.Length + sizeof(char);
        var info = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.WriteInt32(info, 0, 0);
            Marshal.WriteIntPtr(info, handleOffset, IntPtr.Zero);
            Marshal.WriteInt32(info, lengthOffset, nameBytes.Length);
            Marshal.Copy(nameBytes, 0, info + nameOffset, nameBytes.Length);
            Marshal.WriteInt16(info, length - sizeof(char), 0);
            if (!SetFileInformationByHandle(file, 3, info, (uint)length))
                throw IoError(Marshal.GetLastWin32Error());
            GC.KeepAlive(directory);
        }
        finally { Marshal.FreeHGlobal(info); }
    }

    private static IOException IoError(int code) => new("Local filesystem request failed.", new Win32Exception(code));

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, IntPtr data, uint length);
}

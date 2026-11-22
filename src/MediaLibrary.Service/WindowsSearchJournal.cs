using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MediaLibrary.Service;

internal sealed record JournalCursor(string Id, long First, long Next);
internal sealed record JournalChange(ulong FileId, ulong ParentId, uint Reason, bool Directory, string Name);
internal sealed record JournalPage(long Next, JournalChange[] Changes);
internal sealed class JournalUnavailableException(string message) : IOException(message);

// Read-only access: never creates/resizes the system journal and never elevates the application.
internal sealed class WindowsSearchJournal : IDisposable
{
    private readonly SafeFileHandle volume;
    private readonly uint readControl;
    internal string Volume { get; }
    private WindowsSearchJournal(SafeFileHandle volume, uint control, string identity)
    { this.volume = volume; readControl = control; Volume = identity; }

    internal static WindowsSearchJournal? TryOpen(string root)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var drive = Path.GetPathRoot(root);
        if (drive is null || drive.Length != 3 || drive[1] != ':') return null;
        foreach (var access in new uint[] { 0, 0x80000000 })
        {
            var handle = CreateFileW(@"\\.\" + drive[..2], access, FileShare.ReadWrite | FileShare.Delete,
                IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) { handle.Dispose(); continue; }
            try
            {
                using var directory = WindowsPaths.OpenDirectory(root);
                var identity = MediaFile.Read(directory, directory: true).Volume;
                var journal = new WindowsSearchJournal(handle, access == 0 ? 0x000903ABu : 0x000900BBu, identity);
                var state = journal.Query();
                // Probe the read capability before selecting journal mode.
                journal.Read(state.Next, state.Id);
                return journal;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or Win32Exception)
            { handle.Dispose(); }
        }
        return null;
    }

    internal JournalCursor Query()
    {
        var output = new byte[80];
        if (!DeviceIoControl(volume, 0x000900F4, null, 0, output, output.Length, out var bytes, IntPtr.Zero) || bytes < 56)
            throw new IOException("USN journal unavailable.", new Win32Exception(Marshal.GetLastWin32Error()));
        return new(BitConverter.ToUInt64(output, 0).ToString("X16"),
            Math.Max(BitConverter.ToInt64(output, 8), BitConverter.ToInt64(output, 24)), BitConverter.ToInt64(output, 16));
    }

    internal JournalPage Read(long next, string id)
    {
        var input = new byte[48];
        BinaryPrimitives.WriteInt64LittleEndian(input.AsSpan(0), next);
        // Content, names, attributes, extended properties, links, streams and reparse changes.
        BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(8), 0x003FFF77);
        BinaryPrimitives.WriteUInt64LittleEndian(input.AsSpan(32), Convert.ToUInt64(id, 16));
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(40), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(input.AsSpan(42), 2);
        var output = new byte[64 * 1024];
        if (!DeviceIoControl(volume, readControl, input, input.Length, output, output.Length, out var bytes, IntPtr.Zero))
            throw new JournalUnavailableException($"USN journal read failed ({Marshal.GetLastWin32Error()}).");
        return Parse(output.AsSpan(0, bytes));
    }

    internal static JournalPage Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8) throw new IOException("Truncated journal page.");
        var next = BinaryPrimitives.ReadInt64LittleEndian(data);
        var changes = new List<JournalChange>();
        for (var offset = 8; offset < data.Length;)
        {
            var record = data[offset..];
            if (record.Length < 60) throw new IOException("Truncated journal record.");
            var length = BinaryPrimitives.ReadInt32LittleEndian(record);
            if (length < 60 || length > record.Length || BinaryPrimitives.ReadUInt16LittleEndian(record[4..]) != 2)
                throw new IOException("Unsupported journal record.");
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(record[56..]);
            var nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(record[58..]);
            if (nameOffset < 60 || nameOffset + nameLength > length || nameLength % 2 != 0)
                throw new IOException("Invalid journal name.");
            changes.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(record[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(record[16..]), BinaryPrimitives.ReadUInt32LittleEndian(record[40..]),
                (BinaryPrimitives.ReadUInt32LittleEndian(record[52..]) & 16) != 0,
                Encoding.Unicode.GetString(record.Slice(nameOffset, nameLength))));
            offset += length;
        }
        return new(next, changes.ToArray());
    }

    internal string Identity(ulong file) => $"{Volume}:{file:X16}";

    internal bool Matches(string path, ulong file, bool directory)
    {
        if (directory) { using var handle = WindowsPaths.OpenDirectory(path); return MediaFile.Read(handle, true).Identity == Identity(file); }
        return MediaFile.Read(path).Identity == Identity(file);
    }

    internal string? Resolve(ulong file)
    {
        var descriptor = new FileIdDescriptor { Size = 24, Type = 0, Id = file };
        using var handle = OpenFileById(volume, ref descriptor, 0x80, FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero, 0x02000000);
        if (handle.IsInvalid) return null;
        try { return WindowsPaths.FinalPath(handle); }
        catch (IOException) { return null; }
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct FileIdDescriptor
    {
        [FieldOffset(0)] public uint Size;
        [FieldOffset(4)] public uint Type;
        [FieldOffset(8)] public ulong Id;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, FileShare share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint control, byte[]? input, int inputSize,
        byte[] output, int outputSize, out int bytes, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle volume, ref FileIdDescriptor descriptor,
        uint access, FileShare share, IntPtr security, uint flags);
    public void Dispose() => volume.Dispose();
}

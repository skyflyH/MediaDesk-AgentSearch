using System.Runtime.InteropServices;

namespace MediaLibrary.Service;

internal sealed record MediaProperties(string Remark, string[] Authors, DateTime? CapturedAt = null);

internal interface IArchiveProperties
{
    string WriteRemark(string path, string value);
    string WriteAuthors(string path, string[] value);
}

internal sealed class WindowsProperties(string? metadataScratchRoot = null) : IArchiveProperties
{
    private const int NotSupported = unchecked((int)0x80070032);
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int NoInterface = unchecked((int)0x80004002);
    private const int MediaPropertyReadOnly = unchecked((int)0xC00D3E8E);
    private const int AccessDenied = unchecked((int)0x80070005);
    private static readonly PropertyKey Comment = new(new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 6);
    private static readonly PropertyKey Author = new(new("F29F85E0-4FF9-1068-AB91-08002B27B3D9"), 4);
    private static readonly PropertyKey DateTaken = new(new("14B81DA1-0135-4D31-96D9-6CBFC9671A99"), 36867);
    public string? LastWriteFailure { get; private set; }
    public bool LastWriteUsedEmbeddedMetadata { get; private set; }

    public static MediaProperties Read(string path)
    {
        var properties = ReadNative(path);
        return RawSidecar.Read(path) is { } remark ? properties with { Remark = remark } : properties;
    }

    private static MediaProperties ReadNative(string path)
    {
        IPropertyStore? store = null;
        try
        {
            if (Open(path, false, out store) != 0 || store is null) return new("", []);
            return new(ReadRemark(store) ?? "", ReadAuthors(store) ?? [], ReadCaptureDate(store));
        }
        catch (COMException) { return new("", []); }
        finally { Release(store); }
    }

    internal static Dictionary<string, double> ReadSearchNumbers(string path)
    {
        var result = new Dictionary<string, double>();
        IPropertyStore? store = null;
        try
        {
            if (Open(path, false, out store) != 0 || store is null) return result;
            foreach (var (field, name) in LibraryDatabase.SearchPropertyNames)
            {
                if (PSGetPropertyKeyFromName(name, out var key) != 0 || store.GetValue(ref key, out var value) != 0) continue;
                try
                {
                    if (value.Type == 0 || PropVariantToDouble(ref value, out var number) != 0 || !double.IsFinite(number)) continue;
                    if (field == "durationSeconds") number /= 10000000;
                    if (field == "frameRate") number /= 1000;
                    result[field] = number;
                }
                finally { _ = PropVariantClear(ref value); }
            }
        }
        catch (COMException) { /* Unsupported properties remain unknown. */ }
        finally { Release(store); }
        return result;
    }

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int PSGetPropertyKeyFromName(string name, out PropertyKey key);
    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToDouble(ref Variant value, out double number);

    public string WriteRemark(string path, string value) => RawPhoto.Supports(Path.GetExtension(path))
        ? WriteRawRemark(path, value) : WriteRemark(path, value, explorerOnly: false);

    internal string WriteRawRemark(string path, string value, string? templateRaw = null)
    {
        LastWriteFailure = null;
        LastWriteUsedEmbeddedMetadata = false;
        try { RawSidecar.Write(path, value, templateRaw); return "written"; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Xml.XmlException) {
            LastWriteFailure = $"无法保存 XMP 备注：{error.Message}";
            return "write_failed";
        }
    }

    private string WriteRemark(string path, string value, bool explorerOnly) =>
        WriteWithFallback(path, false, value.Length == 0 ? [] : [value],
            () => Write(path, Comment, store => SameRemark(ReadRemark(store), value),
                (out Variant variant) => InitPropVariantFromString(value, out variant)), explorerOnly);

    public string WriteExplorerRemark(string path, string value)
    {
        if (RawPhoto.Supports(Path.GetExtension(path))) return WriteRawRemark(path, value);
        if (MatchesExplorerRemark(path, value))
        {
            LastWriteFailure = null;
            LastWriteUsedEmbeddedMetadata = false;
            RefreshExplorer(path);
            return "written";
        }
        var state = WriteRemark(path, value, explorerOnly: true);
        if (state == "library_only")
        {
            LastWriteFailure = "Windows 无法写入此文件的「详细信息 → 备注」";
            return "write_failed";
        }
        return state;
    }

    internal static bool MatchesExplorerRemark(string path, string value)
    {
        if (RawPhoto.Supports(Path.GetExtension(path))) return RawSidecar.Read(path) == value;
        IPropertyStore? store = null;
        try
        {
            return Open(path, 0, out store) == 0 && store is not null && SameRemark(ReadRemark(store), value);
        }
        catch (COMException) { return false; }
        finally { Release(store); }
    }

    private static bool SameRemark(string? actual, string expected) =>
        actual?.ReplaceLineEndings("\n") == expected.ReplaceLineEndings("\n");

    public string WriteAuthors(string path, string[] value) => WriteWithFallback(path, true, value,
        () => Write(path, Author, store => ReadAuthors(store) is { } authors && authors.SequenceEqual(value),
            (out Variant variant) => InitPropVariantFromStringVector(value, (uint)value.Length, out variant)));

    internal enum NativeWrite { Written, Unsupported, Failed, OpenAccessDenied }

    internal string WriteWithFallback(string path, bool authors, string[] values, Func<NativeWrite> native,
        bool explorerOnly = false)
    {
        LastWriteFailure = null;
        LastWriteUsedEmbeddedMetadata = false;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var useEmbedded = !explorerOnly && (RawPhoto.Supports(extension) ||
            EmbeddedMetadata.Available && (extension == ".png" || authors && extension is ".mov" or ".mp4" or ".m4v"));
        var status = useEmbedded ? NativeWrite.Unsupported : native();
        if (status == NativeWrite.Failed) return "write_failed";
        if (status == NativeWrite.Written && (!explorerOnly || ConfirmExplorerRemark(path, values)))
            return "written";
        var canWriteExplorerRemark = extension is ".jpg" or ".jpeg" or ".mp4" or ".m4v" or ".mov";
        if ((explorerOnly ? !canWriteExplorerRemark : !EmbeddedMetadata.Supports(path)) ||
            status == NativeWrite.OpenAccessDenied && !EmbeddedMetadata.Available)
            return status == NativeWrite.Unsupported ? "library_only" : "write_failed";
        if (explorerOnly && !CanOpenForMetadataWrite(path)) return "write_failed";
        if (EmbeddedMetadata.TryWrite(path, authors, values, out var failure, metadataScratchRoot))
        {
            if (explorerOnly && !ConfirmExplorerRemark(path, values)) return "write_failed";
            LastWriteFailure = null;
            LastWriteUsedEmbeddedMetadata = true;
            return "written";
        }
        LastWriteFailure = LastWriteFailure is { } nativeFailure ? $"{nativeFailure}；{failure}" : failure;
        return "write_failed";
    }

    private bool CanOpenForMetadataWrite(string path)
    {
        try
        {
            using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            LastWriteFailure = "Windows 拒绝写入原件。请检查文件是否只读，以及当前账户是否有写入权限";
            return false;
        }
        catch (IOException error)
        {
            LastWriteFailure = DescribeWriteFailure("打开原件进行写入", error.HResult);
            return false;
        }
    }

    private bool ConfirmExplorerRemark(string path, string[] values)
    {
        RefreshExplorer(path);
        if (MatchesExplorerRemark(path, values.FirstOrDefault() ?? "")) return true;
        LastWriteFailure = "资源管理器「详细信息 → 备注」读回结果与草稿不一致";
        return false;
    }

    // SHCNE_UPDATEITEM with SHCNF_PATHW | SHCNF_FLUSHNOWAIT.
    private static void RefreshExplorer(string path) => SHChangeNotify(0x00002000, 0x2005, path, IntPtr.Zero);

    internal static bool VerifyRemark(IArchiveProperties writer, string path, string value) =>
        writer is WindowsProperties { LastWriteUsedEmbeddedMetadata: true }
            ? EmbeddedMetadata.Matches(path, false, value.Length == 0 ? [] : [value])
            : SameRemark(Read(path).Remark, value);

    internal static bool VerifyAuthors(IArchiveProperties writer, string path, string[] value) =>
        writer is WindowsProperties { LastWriteUsedEmbeddedMetadata: true }
            ? EmbeddedMetadata.Matches(path, true, value)
            : Read(path).Authors.SequenceEqual(value);

    private delegate int InitVariant(out Variant variant);

    private NativeWrite Write(string path, PropertyKey key, Func<IPropertyStore, bool> matches, InitVariant initialize)
    {
        LastWriteFailure = null;
        IPropertyStore? store = null;
        try
        {
            var result = Open(path, true, out store);
            if (result < 0 || store is null)
            {
                // Built-in handlers for some containers expose only a read store.
                if (result == AccessDenied) return Failed("打开属性存储", result, NativeWrite.OpenAccessDenied);
                if (IsUnsupported(result) || !HasPropertyHandler(Path.GetExtension(path))) return NativeWrite.Unsupported;
                return Failed("打开属性存储", result);
            }
            if (matches(store)) return NativeWrite.Written;
            if (store is IPropertyStoreCapabilities capabilities && capabilities.IsPropertyWritable(ref key) == 1)
                return NativeWrite.Unsupported;

            var variant = default(Variant);
            try
            {
                result = initialize(out variant);
                if (result >= 0) result = store.SetValue(ref key, ref variant);
                if (result < 0) return IsUnsupported(result) ? NativeWrite.Unsupported : Failed("设置属性", result);
                result = store.Commit();
            }
            finally { _ = PropVariantClear(ref variant); }
            if (result < 0) return IsUnsupported(result) ? NativeWrite.Unsupported : Failed("提交属性", result);
            Release(store);
            store = null;
            result = Open(path, false, out store);
            if (result != 0 || store is null) return Failed("读回属性", result);
            if (matches(store)) return NativeWrite.Written;
            LastWriteFailure = "Windows 读回的属性与草稿不一致";
            return NativeWrite.Failed;
        }
        catch (COMException error) { return Failed("写入属性", error.HResult); }
        finally { Release(store); }
    }

    private NativeWrite Failed(string operation, int result, NativeWrite status = NativeWrite.Failed)
    {
        LastWriteFailure = DescribeWriteFailure(operation, result);
        return status;
    }

    internal static string DescribeWriteFailure(string operation, int result) => result switch
    {
        unchecked((int)0x80070020) or unchecked((int)0x80070021) or unchecked((int)0x80030020) or unchecked((int)0x80030021)
            => "原件正被占用",
        AccessDenied or unchecked((int)0x80030005) => $"Windows 在{operation}时拒绝访问（0x{result:X8}）",
        _ => $"Windows 无法{operation}（0x{result:X8}）"
    };

    private static bool IsUnsupported(int result) => result is NotSupported or NotImplemented or NoInterface or MediaPropertyReadOnly;

    private static bool HasPropertyHandler(string extension)
    {
        using var handler = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\PropertySystem\PropertyHandlers\" + extension);
        return handler?.GetValue(null) is string;
    }

    private static string? ReadRemark(IPropertyStore store)
    {
        var key = Comment;
        var result = store.GetValue(ref key, out var value);
        try
        {
            if (result < 0) return null;
            if (value.Type == 0) return "";
            return value.Type switch
            {
                31 => Marshal.PtrToStringUni(value.Pointer),
                8 => Marshal.PtrToStringBSTR(value.Pointer),
                _ => null
            };
        }
        finally { _ = PropVariantClear(ref value); }
    }

    private static string[]? ReadAuthors(IPropertyStore store)
    {
        var key = Author;
        var result = store.GetValue(ref key, out var value);
        try
        {
            if (result < 0) return null;
            if (value.Type == 0) return [];
            if (PropVariantToStringVectorAlloc(ref value, out var vector, out var count) < 0) return null;
            var strings = new List<string>();
            try
            {
                for (var index = 0; index < count; index++)
                {
                    var item = Marshal.ReadIntPtr(vector, index * IntPtr.Size);
                    try { strings.Add(Marshal.PtrToStringUni(item) ?? ""); }
                    finally { Marshal.FreeCoTaskMem(item); }
                }
                return strings.ToArray();
            }
            finally { Marshal.FreeCoTaskMem(vector); }
        }
        finally { _ = PropVariantClear(ref value); }
    }

    private static DateTime? ReadCaptureDate(IPropertyStore store)
    {
        var key = DateTaken;
        var result = store.GetValue(ref key, out var value);
        try
        {
            if (result < 0 || value.Type != 64 || value.FileTime <= 0) return null;
            return DateTime.FromFileTimeUtc(value.FileTime);
        }
        catch (ArgumentOutOfRangeException) { return null; }
        finally { _ = PropVariantClear(ref value); }
    }

    private static int Open(string path, bool writable, out IPropertyStore? store) =>
        Open(path, writable ? 0x03U : 0x01U, out store);

    private static int Open(string path, uint flags, out IPropertyStore? store)
    {
        var iid = typeof(IPropertyStore).GUID;
        return SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, flags, ref iid, out store);
    }

    private static void Release(IPropertyStore? store)
    {
        if (store is not null) Marshal.ReleaseComObject(store);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PropertyKey(Guid format, uint property)
    {
        public Guid Format = format;
        public uint Property = property;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(8)] public long FileTime;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out Variant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref Variant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("C8E2D566-186E-4D49-BF41-6909EAD56ACC"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStoreCapabilities
    {
        [PreserveSig] int IsPropertyWritable(ref PropertyKey key);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr binding, uint flags,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern void SHChangeNotify(int eventId, uint flags, string path, IntPtr unused);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int InitPropVariantFromStringVector(
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] value, uint count, out Variant variant);

    private static int InitPropVariantFromString(string value, out Variant variant)
    {
        // InitPropVariantFromString is an inline Windows SDK helper.
        variant = new Variant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(value) };
        return 0;
    }

    [DllImport("propsys.dll", ExactSpelling = true)]
    private static extern int PropVariantToStringVectorAlloc(ref Variant variant, out IntPtr values, out int count);

    [DllImport("ole32.dll", ExactSpelling = true)]
    private static extern int PropVariantClear(ref Variant variant);
}

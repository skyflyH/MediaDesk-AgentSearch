using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Principal;

namespace MediaLibrary.WebView;

internal static class ExplorerLauncher
{
    internal sealed record Request(string Directory, string Arguments, string? SelectedPath = null);
    private static readonly object gate = new();
    private static Task? pending;

    internal static Request CreateRequest(string path, bool select)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('"') || path.Contains('\0'))
            throw new InvalidOperationException("文件路径无效。");
        path = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        return select && parent is not null
            ? new Request(parent, "/select,\"" + path + "\"", path)
            : new Request(path, "\"" + path + "\"");
    }

    internal static async Task OpenAsync(string path, bool select, Action<string> log)
    {
        var request = CreateRequest(path, select);
        Task operation;
        lock (gate) {
            if (pending is { IsCompleted: false }) throw new InvalidOperationException("资源管理器仍在处理上一次请求，请稍后重试。");
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            operation = pending = completion.Task;
            var thread = new Thread(() => {
                try { OpenAndActivate(request, log); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
            }) { IsBackground = true, Name = "MediaDesk Explorer launcher" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }
        try { await operation.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (TimeoutException) { throw new InvalidOperationException("资源管理器未及时响应，请确认目录可访问后重试。"); }
    }

    private static void OpenAndActivate(Request request, Action<string> log)
    {
        // Shell operations may wait on Explorer; keep them off the WebView UI
        // thread. Track the actual directory window, not a short-lived launcher PID.
        dynamic? shell = null;
        var initialized = false;
        try {
            Marshal.ThrowExceptionForHR(CoInitializeEx(0, 2)); initialized = true;
            using (var identity = WindowsIdentity.GetCurrent()) {
                log($"Explorer caller: user={identity.Name}; sid={identity.User}; impersonation={identity.ImpersonationLevel}; elevated={new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)}; session={Process.GetCurrentProcess().SessionId}");
            }
            OpenThroughShell(request, log);
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")
                ?? throw new InvalidOperationException("无法连接 Windows 资源管理器。"));
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(6)) {
                nint window;
                try { window = FindDirectoryWindow(shell!, request.Directory); }
                catch (UnauthorizedAccessException error) {
                    log("Explorer window enumeration denied: " + error);
                    throw new InvalidOperationException("已请求打开文件夹，但当前进程无权访问资源管理器窗口，无法确认或激活窗口。请从普通桌面启动应用后重试。", error);
                }
                if (window != 0) {
                    Activate(window);
                    var activation = Stopwatch.StartNew();
                    while (GetForegroundWindow() != window && activation.ElapsedMilliseconds < 700) Thread.Sleep(25);
                    log($"Explorer directory={request.Directory}; hwnd={window}; foreground={GetForegroundWindow() == window}");
                    if (GetForegroundWindow() != window) throw new InvalidOperationException("文件夹已打开，但 Windows 未允许切换到该窗口。请从任务栏打开资源管理器。");
                    return;
                }
                Thread.Sleep(100);
            }
            throw new InvalidOperationException("已发送打开请求，但未找到目标文件夹窗口。请确认资源管理器和目标目录可用。");
        } finally {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            if (initialized) CoUninitialize();
        }
    }

    private static void OpenThroughShell(Request request, Action<string> log)
    {
        if (request.SelectedPath is null) {
            // cidl=0 in SHOpenFolderAndSelectItems treats the supplied PIDL as
            // the item to select, opening its parent. It is not "open this
            // directory without selection". Use the Shell open verb instead.
            OpenDirectory(request.Directory, log);
            return;
        }
        nint directory = 0, selected = 0, children = 0;
        try {
            Marshal.ThrowExceptionForHR(SHParseDisplayName(request.Directory, 0, out directory, 0, out _));
            if (request.SelectedPath is not null) {
                Marshal.ThrowExceptionForHR(SHParseDisplayName(request.SelectedPath, 0, out selected, 0, out _));
                children = Marshal.AllocCoTaskMem(IntPtr.Size);
                Marshal.WriteIntPtr(children, ILFindLastID(selected));
            }
            // Delegate to the existing desktop shell. Do not start another
            // explorer.exe, which can fail DLL initialization in restricted hosts.
            var result = SHOpenFolderAndSelectItems(directory, children == 0 ? 0u : 1u, children, 0);
            log($"SHOpenFolderAndSelectItems: hr=0x{unchecked((uint)result):X8}; directory={request.Directory}");
            if (result < 0) {
                // ShellExecuteEx uses the standard desktop open verb. Suppress
                // OS error dialogs and preserve the actual Win32 failure code.
                OpenDirectory(request.Directory, log);
                // A select request must not be reported as successful if only
                // its parent opened. Retry the native selection after the shell wakes.
                if (children != 0) Marshal.ThrowExceptionForHR(SHOpenFolderAndSelectItems(directory, 1, children, 0));
            }
        } finally {
            if (children != 0) Marshal.FreeCoTaskMem(children);
            if (selected != 0) Marshal.FreeCoTaskMem(selected);
            if (directory != 0) Marshal.FreeCoTaskMem(directory);
        }
    }

    private static void OpenDirectory(string directory, Action<string> log)
    {
        var execute = new ShellExecuteInfo {
            Size = Marshal.SizeOf<ShellExecuteInfo>(), Mask = 0x00000400 | 0x00000100,
            Verb = "open", File = directory, Show = 1
        };
        if (!ShellExecuteEx(ref execute)) {
            var error = Marshal.GetLastWin32Error();
            log($"ShellExecuteEx: error={error}; directory={directory}");
            throw new Win32Exception(error, "Windows 无法打开目录：" + directory);
        }
        log("ShellExecuteEx: success; directory=" + directory);
    }

    private static nint FindDirectoryWindow(dynamic shell, string directory)
    {
        object? windows = null;
        try {
            windows = shell.Windows();
            dynamic collection = windows;
            for (var i = 0; i < (int)collection.Count; i++) {
                object? window = null, document = null, folder = null, item = null;
                try {
                    window = collection.Item(i);
                    dynamic candidate = window;
                    // Browser shell windows may have no filesystem folder.
                    document = candidate.Document;
                    folder = ((dynamic)document).Folder;
                    item = ((dynamic)folder).Self;
                    var path = (string)((dynamic)item).Path;
                    if (SameDirectory(path, directory)) return (nint)(long)candidate.HWND;
                } catch { /* A window can close or navigate while enumerating. */ }
                finally {
                    foreach (var value in new[] { item, folder, document, window })
                        if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
                }
            }
            return 0;
        } finally { if (windows is not null && Marshal.IsComObject(windows)) Marshal.FinalReleaseComObject(windows); }
    }

    internal static bool SameDirectory(string left, string right)
    {
        if (!Path.IsPathFullyQualified(left) || !Path.IsPathFullyQualified(right)) return false;
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    private static void Activate(nint window)
    {
        GetWindowThreadProcessId(window, out var process);
        AllowSetForegroundWindow(process);
        ShowWindowAsync(window, IsIconic(window) ? 9 : 5);
        if (SetForegroundWindow(window)) return;
        var current = GetCurrentThreadId();
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var target = GetWindowThreadProcessId(window, out _);
        var attachedForeground = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        var attachedTarget = target != 0 && target != current && target != foreground && AttachThreadInput(current, target, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally {
            if (attachedTarget) AttachThreadInput(current, target, false);
            if (attachedForeground) AttachThreadInput(current, foreground, false);
        }
    }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint window);
    [DllImport("user32.dll")] private static extern bool ShowWindowAsync(nint window, int command);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint process);
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHParseDisplayName(string path, nint context, out nint item, uint attributes, out uint found);
    [DllImport("shell32.dll")] private static extern nint ILFindLastID(nint item);
    [DllImport("shell32.dll")] private static extern int SHOpenFolderAndSelectItems(nint directory, uint count, nint children, uint flags);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo {
        internal int Size; internal uint Mask; internal nint Owner;
        internal string? Verb, File, Parameters, Directory; internal int Show;
        internal nint Instance, Item; internal string? Class; internal nint ClassKey;
        internal uint Hotkey; internal nint Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
}
